namespace MineServers.Core;

/// <summary>A task like SquidServers': a schedule plus a script of console commands and !wait / !restart / !start / !stop steps.</summary>
public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New task";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "daily"; // interval | daily | weekly
    public int IntervalMinutes { get; set; } = 60;
    public string Time { get; set; } = "04:00";
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    public string Commands { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastRun { get; set; }

    public string Summary => Kind switch
    {
        "interval" => $"every {IntervalMinutes} min",
        "weekly" => $"every {Day} at {Time}",
        _ => $"every day at {Time}",
    } + (Enabled ? "" : " (off)");

    public override string ToString() => $"{Name}  ·  {Summary}";
}

public static class Scheduler
{
    /// <summary>A daily or weekly run that was missed (MineServers was closed) is skipped if it's more than this late.</summary>
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    public static bool IsDue(ScheduledTask t, DateTime now)
    {
        if (!t.Enabled) return false;
        if (t.Kind == "interval")
            return t.IntervalMinutes > 0 && now - (t.LastRun ?? t.CreatedAt) >= TimeSpan.FromMinutes(t.IntervalMinutes);
        if (!TimeSpan.TryParse(t.Time, out var at)) return false;
        if (t.Kind == "weekly" && now.DayOfWeek != t.Day) return false;
        var slot = now.Date + at;
        return now >= slot && now - slot < Grace && (t.LastRun ?? DateTime.MinValue) < slot;
    }

    /// <summary>One step per line or per ';', like SquidServers' scripts. A leading / is dropped from console commands.</summary>
    public static List<string> Steps(string commands) =>
        commands.Split('\n', ';').Select(c => c.Trim()).Where(c => c.Length > 0)
            .Select(c => c.StartsWith('/') ? c[1..] : c).ToList();

    public static readonly (string Name, ScheduledTask Task)[] Templates =
    {
        ("Daily restart at 4:00 (with a 1-minute warning)", new ScheduledTask
        {
            Name = "Daily restart", Kind = "daily", Time = "04:00",
            Commands = "say Server restarts in 1 minute.\n!wait 50\nsay Restarting in 10 seconds...\n!wait 10\n!restart",
        }),
        ("Save the world every 30 minutes", new ScheduledTask
        {
            Name = "Auto-save", Kind = "interval", IntervalMinutes = 30, Commands = "save-all",
        }),
        ("Announcement every hour", new ScheduledTask
        {
            Name = "Announcement", Kind = "interval", IntervalMinutes = 60, Commands = "say Welcome! Be nice and have fun.",
        }),
        ("Stop the server every night at 1:00", new ScheduledTask
        {
            Name = "Nightly stop", Kind = "daily", Time = "01:00",
            Commands = "say Server closes in 5 minutes.\n!wait 300\n!stop",
        }),
    };
}
