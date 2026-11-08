using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineServers.Core;

public class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "vanilla";   // vanilla | paper | fabric | other
    public string Version { get; set; } = "";
    public int JavaMajor { get; set; }              // 0 = unknown, any Java is tried
    public string? JavaPath { get; set; }           // null = pick automatically
    public int RamMb { get; set; } = 2048;
    public int Port { get; set; } = 25565;
    public string JarFile { get; set; } = "server.jar";
    public string Tunnel { get; set; } = TunnelKinds.None;
    public string? CustomTunnelCommand { get; set; }
    public string? CustomTunnelPattern { get; set; }
    public string? ImportedFrom { get; set; }

    public override string ToString() => Name;
}

public class AppConfig
{
    public List<ServerEntry> Servers { get; set; } = new();
    public string? NgrokAuthToken { get; set; }
}

public static class AppInfo
{
    /// <summary>From &lt;Version&gt; in MineServers.csproj.</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version!.ToString(3);
}

/// <summary>Everything lives in a MineServers-Data folder next to the exe, so the app is portable.</summary>
public static class Store
{
    public static string DataDir { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "MineServers-Data");
    public static string ServersDir => Dir("Servers");
    public static string ToolsDir => Dir("Tools");
    public static string JavaDir => Dir("Java");
    public static string PlayitDir => Dir("Playit");
    static string ConfigPath => System.IO.Path.Combine(DataDir, "config.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Dir(string name)
    {
        var d = System.IO.Path.Combine(DataDir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file instead of silently overwriting the owner's server list.
            File.Copy(ConfigPath, ConfigPath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true);
        }
        return new();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, ConfigPath, true); // atomic replace: a crash mid-save never leaves a half-written list
    }

    /// <summary>A folder name Windows accepts, unique inside <paramref name="parent"/>.</summary>
    public static string UniqueFolder(string parent, string name)
    {
        var clean = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "Server";
        var path = System.IO.Path.Combine(parent, clean);
        for (int i = 2; Directory.Exists(path); i++) path = System.IO.Path.Combine(parent, $"{clean} ({i})");
        return path;
    }
}
