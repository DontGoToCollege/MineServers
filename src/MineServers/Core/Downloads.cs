using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace MineServers.Core;

public record McVersion(string Id, string Url)
{
    public override string ToString() => Id;
}

/// <summary>Server jars, Java and tunnel programs, all from their official public sources.</summary>
public static class Downloads
{
    public static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // PaperMC and GitHub refuse requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"MineServers/{AppInfo.Version} (+https://github.com/DontGoToCollege/MineServers)");
        return c;
    }

    static async Task<JsonElement> GetJson(string url, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return doc.RootElement.Clone();
    }

    public static async Task<List<McVersion>> GetReleases()
    {
        var root = await GetJson("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        return root.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => new McVersion(v.GetProperty("id").GetString()!, v.GetProperty("url").GetString()!))
            .ToList();
    }

    /// <summary>Downloads the server jar for <paramref name="type"/> into <paramref name="dir"/> and returns the Java version it needs.</summary>
    public static async Task<int> InstallServerJar(string type, McVersion version, string dir, IProgress<string> log)
    {
        var meta = await GetJson(version.Url);
        int javaMajor = meta.TryGetProperty("javaVersion", out var jv) ? jv.GetProperty("majorVersion").GetInt32() : 8;
        string jarUrl;
        switch (type)
        {
            case "vanilla":
                jarUrl = meta.GetProperty("downloads").GetProperty("server").GetProperty("url").GetString()!;
                break;
            case "paper":
                try
                {
                    var build = await GetJson($"https://fill.papermc.io/v3/projects/paper/versions/{version.Id}/builds/latest");
                    jarUrl = build.GetProperty("downloads").GetProperty("server:default").GetProperty("url").GetString()!;
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException($"Paper has no build for Minecraft {version.Id} yet. Pick another version or use Vanilla.");
                }
                break;
            case "fabric":
                var loader = (await GetJson("https://meta.fabricmc.net/v2/versions/loader")).EnumerateArray()
                    .First(l => l.GetProperty("stable").GetBoolean()).GetProperty("version").GetString();
                var installer = (await GetJson("https://meta.fabricmc.net/v2/versions/installer")).EnumerateArray()
                    .First(l => l.GetProperty("stable").GetBoolean()).GetProperty("version").GetString();
                jarUrl = $"https://meta.fabricmc.net/v2/versions/loader/{version.Id}/{loader}/{installer}/server/jar";
                break;
            default:
                throw new ArgumentException($"Unknown server type {type}");
        }
        log.Report($"Downloading {type} {version.Id} server...");
        await DownloadFile(jarUrl, Path.Combine(dir, "server.jar"));
        return javaMajor;
    }

    public static async Task DownloadFile(string url, string dest, CancellationToken ct = default)
    {
        var tmp = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>Downloads an Eclipse Temurin JRE for <paramref name="major"/> and returns its java.exe.</summary>
    public static async Task<string> InstallJava(int major, IProgress<string> log)
    {
        var target = Path.Combine(Store.JavaDir, $"jre-{major}");
        var existing = JavaFinder.JavaExeUnder(target);
        if (existing != null) return existing;
        log.Report($"Downloading Java {major} (Eclipse Temurin)... this happens once.");
        var zip = Path.Combine(Store.JavaDir, $"jre-{major}.zip");
        await DownloadFile($"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse", zip);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);
        return JavaFinder.JavaExeUnder(target) ?? throw new InvalidOperationException("The Java download had no java.exe in it.");
    }

    /// <summary>Returns the path to a tunnel program, downloading it the first time.
    /// <paramref name="assetMatch"/> picks the file from the project's latest GitHub release.</summary>
    public static async Task<string> EnsureTool(string exeName, string? githubRepo, Func<string, bool>? assetMatch, string? directUrl, IProgress<string> log)
    {
        var exe = Path.Combine(Store.ToolsDir, exeName);
        if (File.Exists(exe)) return exe;
        string url = directUrl ?? "";
        if (githubRepo != null)
        {
            var rel = await GetJson($"https://api.github.com/repos/{githubRepo}/releases/latest");
            url = rel.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString()!)
                .FirstOrDefault(u => assetMatch!(Path.GetFileName(u)))
                ?? throw new InvalidOperationException($"No Windows download found in {githubRepo}'s latest release.");
        }
        log.Report($"Downloading {exeName}... this happens once.");
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zipPath = exe + ".zip";
            await DownloadFile(url, zipPath);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{exeName} was not in the download.");
                entry.ExtractToFile(exe, true);
            }
            File.Delete(zipPath);
        }
        else
        {
            await DownloadFile(url, exe);
        }
        return exe;
    }
}
