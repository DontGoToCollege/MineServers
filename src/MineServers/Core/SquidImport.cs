using System.IO;
using System.Text.Json;

namespace MineServers.Core;

/// <summary>Adds servers made in SquidServers to MineServers. Their folders stay where they are; nothing is copied or changed.</summary>
public static class SquidImport
{
    public static string ListPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "squidservers", "servers.json");

    public static List<ServerEntry> Read(string listPath, IEnumerable<ServerEntry> already)
    {
        var known = already.Select(s => Path.GetFullPath(s.Path).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(listPath));
        var result = new List<ServerEntry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var path = Str(e, "serverPath");
            if (path == null || !Directory.Exists(path) || known.Contains(Path.GetFullPath(path).TrimEnd('\\'))) continue;
            int.TryParse(ServerProperties.Get(Path.Combine(path, "server.properties"), "server-port"), out var port);
            var java = Str(e, "javaPath");
            result.Add(new ServerEntry
            {
                Name = Str(e, "name") ?? Path.GetFileName(path),
                Path = path,
                Type = Str(e, "serverType") ?? "other",
                Version = Str(e, "version") ?? "",
                RamMb = e.TryGetProperty("ram", out var ram) && ram.TryGetInt32(out var mb) && mb > 0 ? mb : 2048,
                Port = port > 0 ? port : 25565,
                JavaPath = java != null && File.Exists(java) ? java : null,
                ImportedFrom = "SquidServers",
            });
        }
        return result;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
