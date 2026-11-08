using System.IO;
using MineServers.Core;
using Xunit;

namespace MineServers.Tests;

public class FeatureTests
{
    static readonly DateTime Mon0400 = new(2026, 11, 9, 4, 0, 0); // a Monday

    [Fact]
    public void DailyTaskRunsOnceAtItsTime()
    {
        var t = new ScheduledTask { Kind = "daily", Time = "04:00", CreatedAt = Mon0400.AddDays(-1) };
        Assert.False(Scheduler.IsDue(t, Mon0400.AddMinutes(-1)));
        Assert.True(Scheduler.IsDue(t, Mon0400.AddSeconds(5)));
        t.LastRun = Mon0400.AddSeconds(5);
        Assert.False(Scheduler.IsDue(t, Mon0400.AddMinutes(1)));
        Assert.True(Scheduler.IsDue(t, Mon0400.AddDays(1).AddSeconds(1)));
    }

    [Fact]
    public void MissedDailyRunIsSkippedNotRunLate()
    {
        var t = new ScheduledTask { Kind = "daily", Time = "04:00", CreatedAt = Mon0400.AddDays(-1) };
        Assert.False(Scheduler.IsDue(t, Mon0400.AddHours(3))); // MineServers opened at 7:00
    }

    [Fact]
    public void WeeklyTaskOnlyRunsOnItsDay()
    {
        var t = new ScheduledTask { Kind = "weekly", Day = DayOfWeek.Tuesday, Time = "04:00", CreatedAt = Mon0400.AddDays(-7) };
        Assert.False(Scheduler.IsDue(t, Mon0400.AddSeconds(5)));
        Assert.True(Scheduler.IsDue(t, Mon0400.AddDays(1).AddSeconds(5)));
    }

    [Fact]
    public void IntervalTaskCountsFromLastRun()
    {
        var t = new ScheduledTask { Kind = "interval", IntervalMinutes = 30, CreatedAt = Mon0400 };
        Assert.False(Scheduler.IsDue(t, Mon0400.AddMinutes(29)));
        Assert.True(Scheduler.IsDue(t, Mon0400.AddMinutes(30)));
        t.LastRun = Mon0400.AddMinutes(30);
        Assert.False(Scheduler.IsDue(t, Mon0400.AddMinutes(45)));
        t.Enabled = false;
        Assert.False(Scheduler.IsDue(t, Mon0400.AddDays(1)));
    }

    [Fact]
    public void SplitsTaskStepsLikeSquidServers() =>
        Assert.Equal(new[] { "say hi", "!wait 5", "save-all", "!restart" },
            Scheduler.Steps("say hi; !wait 5\n/save-all\n\n  !restart  "));

    [Theory]
    [InlineData("[12:08:09] [Server thread/INFO]: System chat: Game rule mob_griefing is currently set to true", "mob_griefing", "true")]
    [InlineData("[12:08:09] [Server thread/INFO]: Gamerule keepInventory is now set to: false", "keepInventory", "false")]
    [InlineData("[12:08:09] [Server thread/INFO]: System chat: Game rule players_sleeping_percentage is currently set to 100", "players_sleeping_percentage", "100")]
    public void ReadsGameRuleReplies(string line, string name, string value) =>
        Assert.Equal((name, value), GameSettings.ParseRule(line));

    [Fact]
    public void EveryRuleHasA26Name() =>
        Assert.All(GameSettings.Rules, r => Assert.Matches("^[a-z_]+$", r.Snake));

    [Fact]
    public void TracksPlayersJoiningAndLeaving()
    {
        Assert.Equal("DontGoToCollege", Players.JoinedName("[20:36:54] [Server thread/INFO]: DontGoToCollege[/192.168.1.226:49953] logged in with entity id 775 at ([minecraft:overworld]-237.8, 65.0, -181.7)"));
        Assert.Equal("DontGoToCollege", Players.LeftName("[20:37:19] [Server thread/INFO]: System chat: DontGoToCollege left the game"));
        Assert.Equal(new[] { "Amy", "Bob" }, Players.ListedNames("[12:08:13] [Server thread/INFO]: System chat: There are 2 of a max of 20 players online: Amy, Bob"));
        Assert.Empty(Players.ListedNames("[12:08:13] [Server thread/INFO]: System chat: There are 0 of a max of 20 players online: ")!);
        Assert.Null(Players.JoinedName("[20:22:47] [User Authenticator #0/INFO]: UUID of player DontGoToCollege is 18f66fef"));
    }

    [Fact]
    public void ReadsTickTime() =>
        Assert.Equal(1.0, PerfMeter.ParseTickMs("  Average time per tick: 1.0ms (Target: 50.0ms)"));

    [Fact]
    public void EscapesPropertiesLikeTheServer()
    {
        Assert.Equal(@"minecraft\:normal", ServerProperties.Escape("minecraft:normal"));
        Assert.Equal("minecraft:normal", ServerProperties.Unescape(@"minecraft\:normal"));
        Assert.Equal("§aHi é", ServerProperties.Unescape(ServerProperties.Escape("§aHi é")));
        Assert.Equal(@"C:\x", ServerProperties.Unescape(ServerProperties.Escape(@"C:\x")));
    }

    [Fact]
    public void SavesSeveralPropertiesAndReadsThemBack()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "#Minecraft server properties\nlevel-type=minecraft\\:normal\nmax-players=20\n");
        ServerProperties.SetMany(f, new Dictionary<string, string> { ["max-players"] = "8", ["difficulty"] = "hard" });
        var all = ServerProperties.ReadAll(f);
        Assert.Equal(("minecraft:normal", "8", "hard"), (all["level-type"], all["max-players"], all["difficulty"]));
        Assert.StartsWith("#Minecraft", File.ReadAllText(f));
        File.Delete(f);
    }

    [Theory]
    [InlineData("paper", "plugins")]
    [InlineData("fabric", "mods")]
    [InlineData("vanilla", null)]
    public void PicksAddonFolderByServerType(string type, string? folder) =>
        Assert.Equal(folder, Modrinth.For(type)?.Folder);
}
