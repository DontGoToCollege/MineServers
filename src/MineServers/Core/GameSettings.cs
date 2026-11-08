using System.Text.RegularExpressions;

namespace MineServers.Core;

/// <summary>A server.properties setting shown as a friendly control. Kind: bool, int, text or choice.</summary>
public record PropertyDef(string Key, string Label, string Kind, string[]? Options = null, int Min = 0, int Max = 0, string? Help = null);

/// <summary>A game rule. 26.x names them in snake_case; 1.x versions use camelCase (null = that version has no such rule).</summary>
public record RuleDef(string Snake, string? Camel, string Label, bool IsInt = false, string? Help = null)
{
    public string? NameFor(bool snake) => snake ? Snake : Camel;
}

public static class GameSettings
{
    static readonly string[] OnOff = { "true", "false" };

    /// <summary>Shown only when the key is in the server's server.properties, since each Minecraft version has a different set.</summary>
    public static readonly (string Group, PropertyDef[] Items)[] Properties =
    {
        ("Gameplay", new PropertyDef[]
        {
            new("motd", "Server message (shown in the server list)", "text"),
            new("gamemode", "Game mode for new players", "choice", new[] { "survival", "creative", "adventure", "spectator" }),
            new("force-gamemode", "Always put players in that game mode when they join", "bool"),
            new("difficulty", "Difficulty", "choice", new[] { "peaceful", "easy", "normal", "hard" }),
            new("hardcore", "Hardcore (one life; players become spectators when they die)", "bool"),
            new("pvp", "PvP (players can hurt each other)", "bool"),
            new("allow-flight", "Allow flying (otherwise players who fly get kicked as cheaters)", "bool",
                Help: "Turn on if you use mods or plugins that let players fly."),
            new("enable-command-block", "Command blocks work", "bool"),
            new("max-players", "Max players", "int", Min: 1, Max: 1000),
            new("spawn-protection", "Spawn protection radius (blocks only operators can change)", "int", Min: 0, Max: 1000,
                Help: "Stops griefing around spawn. 0 turns it off."),
            new("player-idle-timeout", "Kick idle players after (minutes, 0 = never)", "int", Min: 0, Max: 100000),
        }),
        ("Performance", new PropertyDef[]
        {
            new("view-distance", "View distance (chunks sent to players)", "int", Min: 2, Max: 32, Help: "Lower = less lag. 10 is normal."),
            new("simulation-distance", "Simulation distance (chunks where mobs and crops update)", "int", Min: 2, Max: 32,
                Help: "Lower = less lag. 8 to 10 is normal."),
            new("entity-broadcast-range-percentage", "Entity visibility range (%)", "int", Min: 10, Max: 1000),
            new("pause-when-empty-seconds", "Pause the server after it's empty for (seconds, 0 = never)", "int", Min: 0, Max: 100000),
            new("sync-chunk-writes", "Safe chunk saving (slower, protects against corruption on crashes)", "bool"),
        }),
        ("World", new PropertyDef[]
        {
            new("level-name", "World folder name", "text", Help: "A new name makes a new world; the old one stays in its folder."),
            new("level-seed", "World seed (used when a new world is made)", "text"),
            new("level-type", "World type (used when a new world is made)", "choice",
                new[] { "minecraft:normal", "minecraft:flat", "minecraft:large_biomes", "minecraft:amplified", "minecraft:single_biome_surface" }),
            new("generate-structures", "Generate villages, temples and other structures", "bool"),
            new("allow-nether", "Nether is reachable", "bool"),
            new("spawn-monsters", "Monsters spawn", "bool"),
            new("max-world-size", "World border radius (blocks)", "int", Min: 1, Max: 29999984),
        }),
        ("Access and security", new PropertyDef[]
        {
            new("online-mode", "Require real Minecraft accounts (online mode)", "bool",
                Help: "Keep on. Off lets anyone join with any name, including yours."),
            new("enforce-whitelist", "Kick players who are removed from the whitelist while online", "bool"),
            new("enforce-secure-profile", "Require signed chat (secure profiles)", "bool"),
            new("enable-status", "Show the server as online in the server list", "bool"),
            new("hide-online-players", "Hide who's online in the server list", "bool"),
            new("op-permission-level", "Operator power level (4 = everything)", "int", Min: 1, Max: 4),
            new("prevent-proxy-connections", "Block players using VPNs or proxies", "bool"),
            new("accepts-transfers", "Accept players transferred from other servers", "bool"),
        }),
    };

    public static readonly RuleDef[] Rules =
    {
        new("pvp", null, "PvP (players can hurt each other)"),
        new("mob_griefing", "mobGriefing", "Mob griefing (creepers break blocks, endermen take blocks)"),
        new("keep_inventory", "keepInventory", "Keep inventory when you die"),
        new("command_blocks_work", null, "Command blocks work"),
        new("spawn_monsters", null, "Monsters spawn"),
        new("spawn_phantoms", "doInsomnia", "Phantoms spawn when players don't sleep"),
        new("allow_entering_nether_using_portals", null, "Nether portals work"),
        new("advance_time", "doDaylightCycle", "Day and night cycle"),
        new("advance_weather", "doWeatherCycle", "Weather changes"),
        new("natural_health_regeneration", "naturalRegeneration", "Health regenerates naturally"),
        new("immediate_respawn", "doImmediateRespawn", "Respawn instantly (skip the death screen)"),
        new("show_death_messages", "showDeathMessages", "Show death messages in chat"),
        new("tnt_explodes", "tntExplodes", "TNT explodes"),
        new("fall_damage", "fallDamage", "Fall damage"),
        new("fire_damage", "fireDamage", "Fire damage"),
        new("players_sleeping_percentage", "playersSleepingPercentage", "Players who must sleep to skip the night (%)", IsInt: true),
    };

    static readonly Regex RuleLine = new(@"Game ?rule (\S+?) (?:is currently set to|is now set to|has been set to|set to):? (\S+)", RegexOptions.IgnoreCase);

    /// <summary>"Game rule mob_griefing is currently set to true" (26.x) or "Gamerule mobGriefing is now set to: false" (1.x).</summary>
    public static (string Name, string Value)? ParseRule(string line)
    {
        var m = RuleLine.Match(line);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.TrimEnd('.')) : null;
    }

    public static bool IsBool(string value) => OnOff.Contains(value);
}
