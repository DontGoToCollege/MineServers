using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MineServers.Core;

public record PerfSample(DateTime At, double CpuPercent, double MemoryMb);

/// <summary>CPU and memory of a server's process, sampled every few seconds.</summary>
public class PerfMeter
{
    public const int MaxSamples = 300; // 10 minutes at one sample every 2 seconds
    public List<PerfSample> Samples { get; } = new();
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
