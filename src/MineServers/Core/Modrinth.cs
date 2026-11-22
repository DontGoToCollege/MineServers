using System.IO;
using System.Text.Json;

namespace MineServers.Core;

public record ModHit(string Id, string Title, string Author, string Description, long Downloads)
{
    public override string ToString() => $"{Title}  by {Author}  ·  {Downloads:N0} downloads";
}

public record ModUpdate(string OldFile, string NewFile, string Url, string Version)
{
    public override string ToString() => $"{OldFile}  >  {Version}";
}

/// <summary>Plugins and mods from Modrinth (the source SquidServers uses), matched to the server's type and version.</summary>
public static class Modrinth
{
    const string Api = "https://api.modrinth.com/v2";

    /// <summary>Where add-ons go and which Modrinth loaders fit, or null for server types that can't load any (Vanilla).</summary>
    public static (string Folder, string[] Loaders, string ProjectType, string Noun)? For(string serverType) => serverType switch
    {
        "paper" or "purpur" or "spigot" or "bukkit" => ("plugins", new[] { "paper", "spigot", "bukkit", "purpur" }, "plugin", "plugin"),
        "fabric" => ("mods", new[] { "fabric" }, "mod", "mod"),
        "quilt" => ("mods", new[] { "quilt", "fabric" }, "mod", "mod"),
        "forge" => ("mods", new[] { "forge" }, "mod", "mod"),
        "neoforge" => ("mods", new[] { "neoforge" }, "mod", "mod"),
        _ => null,
    };

    static string Json(object o) => JsonSerializer.Serialize(o);

    public static async Task<List<ModHit>> Search(string query, string serverType, string version)
    {
        var kind = For(serverType) ?? throw new InvalidOperationException("This server type can't use plugins or mods.");
        var facets = new List<string[]>
        {
            kind.Loaders.Select(l => "categories:" + l).ToArray(),
            new[] { "versions:" + version },
            new[] { "project_type:" + kind.ProjectType },
        };
        if (kind.ProjectType == "mod") facets.Add(new[] { "server_side:required", "server_side:optional" }); // skip client-only mods
        var url = $"{Api}/search?limit=30&query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(Json(facets))}"
                  + (query.Length == 0 ? "&index=downloads" : "");
        using var doc = JsonDocument.Parse(await Downloads.Http.GetStringAsync(url));
        return doc.RootElement.GetProperty("hits").EnumerateArray().Select(h => new ModHit(
            h.GetProperty("project_id").GetString()!, h.GetProperty("title").GetString()!,
            h.GetProperty("author").GetString() ?? "", h.GetProperty("description").GetString() ?? "",
            h.GetProperty("downloads").GetInt64())).ToList();
    }

    /// <summary>Installs the newest fitting version and any required dependencies. Returns the files added.</summary>
    public static async Task<List<string>> Install(string projectId, string serverType, string version, string serverDir, IProgress<string> log)
    {
        var kind = For(serverType)!.Value;
        var dir = Path.Combine(serverDir, kind.Folder);
        Directory.CreateDirectory(dir);
        var added = new List<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>(new[] { projectId });
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            var url = $"{Api}/project/{id}/version?loaders={Uri.EscapeDataString(Json(kind.Loaders))}&game_versions={Uri.EscapeDataString(Json(new[] { version }))}";
            using var doc = JsonDocument.Parse(await Downloads.Http.GetStringAsync(url));
            var best = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (best.ValueKind != JsonValueKind.Object)
            {
                if (id == projectId) throw new InvalidOperationException($"It has no version for Minecraft {version} on this server type.");
                log.Report($"A required add-on ({id}) has no version for Minecraft {version}; skipped it.");
                continue;
            }
            var file = PrimaryFile(best);
            var name = Path.GetFileName(file.GetProperty("filename").GetString()!); // never trust a path from the internet
            var dest = Path.Combine(dir, name);
            if (File.Exists(dest) || File.Exists(dest + ".disabled")) log.Report($"{name} is already installed.");
            else
            {
                log.Report($"Downloading {name}...");
                await Downloads.DownloadFile(file.GetProperty("url").GetString()!, dest);
                added.Add(name);
            }
            foreach (var dep in best.GetProperty("dependencies").EnumerateArray())
                if (dep.GetProperty("dependency_type").GetString() == "required"
                    && dep.TryGetProperty("project_id", out var pid) && pid.GetString() is { } depId)
                    queue.Enqueue(depId);
        }
        return added;
    }

    static JsonElement PrimaryFile(JsonElement version)
    {
        var files = version.GetProperty("files").EnumerateArray().ToList();
        var file = files.FirstOrDefault(f => f.GetProperty("primary").GetBoolean());
        return file.ValueKind == JsonValueKind.Object ? file : files[0];
    }

    public static string Sha1(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Installed add-ons (turned-on ones) that have a newer version for this server's Minecraft version.
    /// Add-ons that didn't come from Modrinth are skipped, since Modrinth can't recognise them.</summary>
    public static async Task<List<ModUpdate>> CheckUpdates(string serverDir, string serverType, string version)
    {
        var kind = For(serverType)!.Value;
        var dir = Path.Combine(serverDir, kind.Folder);
        var byHash = Installed(serverDir, serverType).Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => Sha1(Path.Combine(dir, f))).ToDictionary(g => g.Key, g => g.First());
        if (byHash.Count == 0) return new();
        var body = Json(new { hashes = byHash.Keys, algorithm = "sha1", loaders = kind.Loaders, game_versions = new[] { version } });
        using var resp = await Downloads.Http.PostAsync($"{Api}/version_files/update",
            new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var updates = new List<ModUpdate>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var file = PrimaryFile(p.Value);
            if (!byHash.TryGetValue(p.Name, out var old) || file.GetProperty("hashes").GetProperty("sha1").GetString() == p.Name) continue;
            updates.Add(new ModUpdate(old, Path.GetFileName(file.GetProperty("filename").GetString()!), file.GetProperty("url").GetString()!,
                p.Value.GetProperty("version_number").GetString() ?? "?"));
        }
        return updates;
    }

    /// <summary>Swaps an add-on for its newer file. The server must be stopped (Windows locks jars in use).</summary>
    public static async Task ApplyUpdate(string serverDir, string serverType, ModUpdate u)
    {
        var dir = Path.Combine(serverDir, For(serverType)!.Value.Folder);
        await Downloads.DownloadFile(u.Url, Path.Combine(dir, u.NewFile));
        if (!u.OldFile.Equals(u.NewFile, StringComparison.OrdinalIgnoreCase)) File.Delete(Path.Combine(dir, u.OldFile));
    }

    /// <summary>Installed add-on files; disabled ones end in .jar.disabled.</summary>
    public static List<string> Installed(string serverDir, string serverType) =>
        For(serverType) is { } kind && Directory.Exists(Path.Combine(serverDir, kind.Folder))
            ? Directory.GetFiles(Path.Combine(serverDir, kind.Folder))
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new();
}
