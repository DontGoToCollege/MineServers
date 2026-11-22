using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MineServers.Core;

public record PerfSample(DateTime At, double CpuPercent, double MemoryMb);

/// <summary>CPU and memory of a server's process, sampled every few seconds.</summary>
public class PerfMeter
{
    public const int MaxSamples = 300; // 10 minutes at one sample every 2 seconds
    public List<PerfSample> Samples { get; } = new();
    public const int MaxTps = 40; // 10 minutes at one "tick query" every 15 seconds
    public List<double> Tps { get; } = new();

    /// <summary>Ticks per second from the average tick time: 20 is full speed, less is lag.</summary>
    public void AddTickMs(double ms)
    {
        Tps.Add(Math.Min(20, 1000 / Math.Max(ms, 0.001)));
        if (Tps.Count > MaxTps) Tps.RemoveAt(0);
    }

    /// <summary>Lines of the reply to "tick query" (or of an old server not knowing the command), hidden while MineServers samples TPS.</summary>
    public static bool IsTickReply(string line) =>
        new[] { "The game is running", "The game is frozen", "The game is sprinting", "Target tick rate", "Average time per tick", "Percentiles: P50",
                "Unknown or incomplete command", "<--[HERE]" }.Any(line.Contains);
    TimeSpan _lastCpu;
    DateTime _lastAt;
    int _lastPid;

    public void Sample(Process? p)
    {
        if (p == null || p.HasExited) return;
        try
        {
            p.Refresh();
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (_lastPid == p.Id && _lastAt != default)
            {
                var wall = (now - _lastAt).TotalMilliseconds * Environment.ProcessorCount;
                var pct = wall > 0 ? Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall * 100, 0, 100) : 0;
                Samples.Add(new PerfSample(DateTime.Now, pct, p.WorkingSet64 / 1048576.0));
                if (Samples.Count > MaxSamples) Samples.RemoveAt(0);
            }
            (_lastPid, _lastCpu, _lastAt) = (p.Id, cpu, now);
        }
        catch (InvalidOperationException) { } // exited between the check and the read
    }

    static readonly Regex TickLine = new(@"Average time per tick: ([\d.]+) ?ms");

    /// <summary>From the reply to "tick query": "Average time per tick: 1.0ms (Target: 50.0ms)".</summary>
    public static double? ParseTickMs(string line) =>
        TickLine.Match(line) is { Success: true } m && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? ms : null;
}
