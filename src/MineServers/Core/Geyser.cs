using System.IO;

namespace MineServers.Core;

/// <summary>Geyser + Floodgate: lets Bedrock players (phones, consoles, Windows Bedrock) join a Paper server, without a Java account.</summary>
public static class Geyser
{
    public const int BedrockPort = 19132; // Geyser's default; Bedrock uses UDP

    public static bool Installed(string serverDir) =>
        Directory.Exists(Path.Combine(serverDir, "plugins")) && Directory.GetFiles(Path.Combine(serverDir, "plugins"), "Geyser-*.jar").Length > 0;

    public static async Task Install(string serverDir, IProgress<string> log)
    {
        var plugins = Path.Combine(serverDir, "plugins");
        Directory.CreateDirectory(plugins);
        foreach (var (project, file) in new[] { ("geyser", "Geyser-Spigot.jar"), ("floodgate", "floodgate-spigot.jar") })
        {
            log.Report($"Downloading {file}...");
            await Downloads.DownloadFile($"https://download.geysermc.org/v2/projects/{project}/versions/latest/builds/latest/downloads/spigot",
                Path.Combine(plugins, file));
        }
    }

    /// <summary>Geyser writes "auth-type: online" in its config on first run, which would make Bedrock players need a Java account.
    /// Switches it to Floodgate. Returns true if the file changed (then "geyser reload" applies it to a running server).</summary>
    public static bool UseFloodgate(string serverDir)
    {
        var config = Path.Combine(serverDir, "plugins", "Geyser-Spigot", "config.yml");
        if (!File.Exists(config) || Directory.GetFiles(Path.Combine(serverDir, "plugins"), "floodgate*.jar").Length == 0) return false;
        var text = File.ReadAllText(config);
        var fixedText = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(\s*auth-type:\s*)online\s*$", "${1}floodgate");
        if (fixedText == text) return false;
        File.WriteAllText(config, fixedText);
        return true;
    }

    /// <summary>Where Bedrock players connect, for the tunnel in use.</summary>
    public static string JoinInfo(ServerEntry s, string javaAddress) => s.Tunnel switch
    {
        TunnelKinds.None => $"Bedrock players on your Wi-Fi join at {Tunnel.LanIp()}, port {BedrockPort}.",
        TunnelKinds.Upnp => $"Bedrock players join at {javaAddress.Split(':')[0]}, port {BedrockPort}.",
        TunnelKinds.Playit => $"Bedrock players on your Wi-Fi join at {Tunnel.LanIp()}, port {BedrockPort}. For friends elsewhere, add a \"Minecraft Bedrock\" tunnel to port {BedrockPort} on playit.gg.",
        _ => $"Bedrock players on your Wi-Fi join at {Tunnel.LanIp()}, port {BedrockPort}. This tunnel can't carry Bedrock; UPnP or playit.gg can.",
    };
}
