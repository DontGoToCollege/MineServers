using System.ComponentModel;
using System.Text;
using MineServers.Core;

namespace MineServers;

/// <summary>A server in the list plus what's running for it right now.</summary>
public class ServerItem(ServerEntry entry) : INotifyPropertyChanged
{
    public ServerEntry Entry { get; } = entry;
    public ServerRunner Runner { get; } = new();
    public Tunnel? Tunnel { get; set; }
    public string Address { get; set; } = "";
    public StringBuilder Log { get; } = new();
    public HashSet<string> AskedAbout { get; } = new(StringComparer.OrdinalIgnoreCase); // don't ask twice in one run

    // Live state, read from the console.
    public SortedSet<string> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Rules { get; } = new();
    public bool? SnakeRules { get; set; } // 26.x rule names, or 1.x camelCase; null until the server answers
    public double? TickMs { get; set; }
    public PerfMeter Perf { get; } = new();
    public DateTime QuietUntil { get; set; } // hide MineServers' own rule queries from the console until then
    public int HistoryIndex { get; set; } = -1;

    public string Subtitle => $"{Runner.State} · {Entry.Type} {Entry.Version}".Trim()
                              + (Online.Count > 0 ? $" · {Online.Count} online" : "");

    /// <summary>Updates live state from one console line. Returns true if the line should stay out of the console.</summary>
    public bool Observe(string line)
    {
        if (Players.JoinedName(line) is { } joined) Online.Add(joined);
        else if (Players.LeftName(line) is { } left) Online.Remove(left);
        else if (Players.ListedNames(line) is { } listed) { Online.Clear(); Online.UnionWith(listed); }
        else if (PerfMeter.ParseTickMs(line) is { } ms) TickMs = ms;
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

    public void ClearLive()
    {
        Online.Clear();
        Rules.Clear();
        SnakeRules = null;
        TickMs = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new("Entry"));
    }
}
