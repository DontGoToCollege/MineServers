using System.ComponentModel;
using System.Text;
using MineServers.Core;

namespace MineServers;

/// <summary>A console line, coloured by what it is.</summary>
public record LogLine(string Text)
{
    static readonly System.Windows.Media.Brush Error = Frozen("#F87171"), Warn = Frozen("#FBBF24"), Ours = Frozen("#86EFAC"),
        Typed = Frozen("#67E8F9"), Plain = Frozen("#E5E7EB");

    public System.Windows.Media.Brush Color { get; } =
        Text.Contains("ERROR") || Text.Contains("Exception") || Text.Contains("SEVERE") ? Error
        : Text.Contains("WARN") ? Warn
        : Text.StartsWith("[MineServers]") || Text.StartsWith("[Tunnel]") || Text.StartsWith("[Scheduler]") ? Ours
        : Text.StartsWith("> ") ? Typed
        : Plain;

    static System.Windows.Media.Brush Frozen(string hex)
    {
        var b = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }

    public override string ToString() => Text;
}

/// <summary>A server in the list plus what's running for it right now.</summary>
public class ServerItem(ServerEntry entry) : INotifyPropertyChanged
{
    public ServerEntry Entry { get; } = entry;
    public ServerRunner Runner { get; } = new();
    public Tunnel? Tunnel { get; set; }
    public string Address { get; set; } = "";
    public System.Collections.ObjectModel.ObservableCollection<LogLine> Log { get; } = new();
    public Dictionary<string, DateTime> JoinedAt { get; } = new(StringComparer.OrdinalIgnoreCase); // for playtime
    public bool SparkPending { get; set; } // open the Spark report link when it appears
    public DateTime QuietTicksUntil { get; set; } // hide the replies to MineServers' own TPS sampling until then
    public DateTime NextTpsSample { get; set; }
    public bool NoTickCommand { get; set; } // versions before 1.20.3 have no "tick query"
    public HashSet<string> AskedAbout { get; } = new(StringComparer.OrdinalIgnoreCase); // don't ask twice in one run

    // Live state, read from the console.
    public SortedSet<string> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Rules { get; } = new();
    public bool? SnakeRules { get; set; } // 26.x rule names, or 1.x camelCase; null until the server answers
    public double? TickMs { get; set; }
    public PerfMeter Perf { get; } = new();
    public DateTime QuietUntil { get; set; } // hide MineServers' own rule queries from the console until then
    public int HistoryIndex { get; set; } = -1;
    public bool Busy { get; set; } // a backup or world change is under way: don't start, and don't back up twice
    public List<DateTime> Crashes { get; } = new();

    public string Subtitle => $"{Runner.State} · {Entry.Type} {Entry.Version}".Trim()
                              + (Online.Count > 0 ? $" · {Online.Count} online" : "");

    /// <summary>Updates live state from one console line. Returns true if the line should stay out of the console.</summary>
    public bool Observe(string line)
    {
        if (Players.JoinedName(line) is { } joined)
        {
            Online.Add(joined);
            JoinedAt[joined] = DateTime.Now;
            var stat = Stat(joined);
            stat.LastSeen = DateTime.Now;
            stat.LastIp = Players.JoinedIp(line);
        }
        else if (Players.LeftName(line) is { } left) { Online.Remove(left); EndSession(left); }
        else if (Players.ListedNames(line) is { } listed) { Online.Clear(); Online.UnionWith(listed); }
        else if (PerfMeter.ParseTickMs(line) is { } ms)
        {
            TickMs = ms;
            Perf.AddTickMs(ms);
            return DateTime.Now < QuietTicksUntil;
        }
        else if (DateTime.Now < QuietTicksUntil && PerfMeter.IsTickReply(line))
        {
            if (line.Contains("Unknown or incomplete command")) NoTickCommand = true;
            return true;
        }
        else if (GameSettings.ParseRule(line) is { } rule)
        {
            Rules[rule.Name] = rule.Value;
            SnakeRules ??= rule.Name.Contains('_') || rule.Name == "pvp";
            return DateTime.Now < QuietUntil;
        }
        else if (DateTime.Now < QuietUntil && (line.Contains("Incorrect argument for command") || line.EndsWith("<--[HERE]")))
            return true; // the reply to probing the other rule naming style
        return false;
    }

    PlayerStat Stat(string name) =>
        Entry.PlayerStats.TryGetValue(name, out var s) ? s : Entry.PlayerStats[name] = new PlayerStat();

    /// <summary>Adds the time since they joined to their playtime.</summary>
    void EndSession(string name)
    {
        if (!JoinedAt.Remove(name, out var at)) return;
        var stat = Stat(name);
        stat.Minutes += (DateTime.Now - at).TotalMinutes;
        stat.LastSeen = DateTime.Now;
    }

    /// <summary>"Steve   12h 5m played   last seen 22 Nov 14:02", or "online now".</summary>
    public List<string> PlayerSummaries() =>
        Entry.PlayerStats.OrderByDescending(p => Online.Contains(p.Key)).ThenByDescending(p => p.Value.LastSeen).Select(p =>
        {
            var minutes = p.Value.Minutes + (JoinedAt.TryGetValue(p.Key, out var at) ? (DateTime.Now - at).TotalMinutes : 0);
            var played = minutes >= 60 ? $"{(int)(minutes / 60)}h {(int)minutes % 60}m" : $"{(int)minutes}m";
            var seen = Online.Contains(p.Key) ? "online now" : $"last seen {p.Value.LastSeen:d MMM HH:mm}";
            return $"{p.Key,-17} {played,9} played   {seen}";
        }).ToList();

    public void ClearLive()
    {
        foreach (var name in JoinedAt.Keys.ToList()) EndSession(name);
        Online.Clear();
        Rules.Clear();
        SnakeRules = null;
        TickMs = null;
        Perf.Tps.Clear();
        NoTickCommand = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new("Entry"));
    }
}
