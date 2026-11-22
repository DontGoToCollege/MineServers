using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MineServers.Core;

/// <summary>Sets up a server from a Modrinth modpack (.mrpack): the right Fabric server, its mods and its config files.</summary>
public static class Modpack
{
    public static async Task<(string Name, string McVersion, int JavaMajor)> Install(string mrpack, string dir, IProgress<string> log)
    {
        using var zip = ZipFile.OpenRead(mrpack);
        var index = zip.GetEntry("modrinth.index.json") ?? throw new InvalidOperationException("That isn't a Modrinth modpack (it has no modrinth.index.json).");
        using var doc = JsonDocument.Parse(index.Open());
        var root = doc.RootElement;
        var deps = root.GetProperty("dependencies");
        var mc = deps.GetProperty("minecraft").GetString()!;
        // shortcut: Fabric only; Quilt/Forge/NeoForge packs need their own installers, add them if someone needs one.
        if (!deps.TryGetProperty("fabric-loader", out var loader))
            throw new InvalidOperationException("MineServers can set up Fabric modpacks only, and this one uses "
                + string.Join(", ", deps.EnumerateObject().Select(p => p.Name).Where(n => n != "minecraft")) + ".");

        var version = (await Downloads.GetReleases()).FirstOrDefault(v => v.Id == mc)
                      ?? throw new InvalidOperationException($"Minecraft {mc} isn't a release MineServers can download.");
        var javaMajor = await Downloads.InstallServerJar("fabric", version, dir, log, loader.GetString());

        var files = root.GetProperty("files").EnumerateArray()
            .Where(f => !(f.TryGetProperty("env", out var env) && env.TryGetProperty("server", out var side) && side.GetString() == "unsupported"))
            .ToList();
        int n = 0;
        foreach (var f in files)
        {
            var dest = Inside(dir, f.GetProperty("path").GetString()!);
            log.Report($"Downloading {Path.GetFileName(dest)} ({++n} of {files.Count})...");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await Downloads.DownloadFile(f.GetProperty("downloads")[0].GetString()!, dest);
            if (!Modrinth.Sha1(dest).Equals(f.GetProperty("hashes").GetProperty("sha1").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dest);
                throw new InvalidOperationException($"{Path.GetFileName(dest)} didn't download correctly (its checksum doesn't match).");
            }
        }
        // Config files that come with the pack; server-overrides win over overrides.
        foreach (var prefix in new[] { "overrides/", "server-overrides/" })
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.Length > 0))
            {
                var dest = Inside(dir, e.FullName[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        return (root.TryGetProperty("name", out var name) ? name.GetString() ?? "Modpack" : "Modpack", mc, javaMajor);
    }

    /// <summary>A path from the pack, refused if it would land outside the server folder.</summary>
    static string Inside(string dir, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        if (!full.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The modpack tried to put a file outside the server folder: " + relative);
        return full;
    }
}
