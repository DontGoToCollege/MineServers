using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineServers.Core;

/// <summary>Whitelist and ban lists. Changes go through the server's own commands so it looks up each player's account itself.</summary>
public static class Players
{
    static readonly Regex Rejected = new(@"Disconnecting (\w{3,16}) .*not white-?listed", RegexOptions.IgnoreCase);

    /// <summary>The player name in a "not whitelisted" kick line from the server log, or null.</summary>
    public static string? RejectedName(string line)
    {
        var m = Rejected.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    // 26.x: "Name[/1.2.3.4:5] logged in with entity id 7 at ..." and "System chat: Name left the game".
    static readonly Regex JoinedLine = new(@"\]: (\w{3,16})\[/[^\]]*\] logged in with entity id");
    static readonly Regex LeftLine = new(@"\]: (?:System chat: )?(\w{3,16}) left the game");
    static readonly Regex ListLine = new(@"There are \d+ of a max(?: of)? \d+ players online:(.*)$");

    public static string? JoinedName(string line) => JoinedLine.Match(line) is { Success: true } m ? m.Groups[1].Value : null;
    public static string? LeftName(string line) => LeftLine.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Names from the reply to the "list" command, or null if the line isn't that reply.</summary>
    public static List<string>? ListedNames(string line) =>
        ListLine.Match(line) is { Success: true } m
            ? m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : null;

    public static List<string> Allowed(string serverDir) => Names(Path.Combine(serverDir, "whitelist.json"));
    public static List<string> Banned(string serverDir) => Names(Path.Combine(serverDir, "banned-players.json"));
    public static List<string> Operators(string serverDir) => Names(Path.Combine(serverDir, "ops.json"));

    static List<string> Names(string file)
    {
        try
        {
            if (!File.Exists(file)) return new();
            using var doc = JsonDocument.Parse(SharedFile.ReadAllText(file));
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new(); // the server may be mid-write; the next refresh picks it up
        }
    }

    /// <summary>Minecraft names are 3-16 letters, digits or underscores; anything else would be a different console command.</summary>
    public static bool IsValidName(string name) => Regex.IsMatch(name, @"^\w{3,16}$");
}
