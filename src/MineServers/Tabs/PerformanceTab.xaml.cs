using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MineServers.Core;

namespace MineServers.Tabs;

public partial class PerformanceTab : UserControl
{
    ServerItem? _item;

    public PerformanceTab() => InitializeComponent();

    MainWindow Host => (MainWindow)Window.GetWindow(this);

    public void Show(ServerItem item)
    {
        _item = item;
        var running = item.Runner.IsRunning;
        var last = item.Perf.Samples.LastOrDefault();
        CpuText.Text = running && last != null ? $"{last.CpuPercent:0}%" : "-";
        MemText.Text = running && last != null ? $"{last.MemoryMb / 1024:0.0} / {item.Entry.RamMb / 1024.0:0.#} GB" : "-";
        PlayersText.Text = running ? item.Online.Count.ToString() : "-";
        var up = DateTime.Now - item.Runner.StartedAt;
        UptimeText.Text = running ? (up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s") : "-";
        TickText.Text = item.TickMs is { } ms
            ? $"{ms:0.0} ms per tick: " + (ms < 40 ? "running smoothly" : ms <= 50 ? "close to the limit" : "lagging")
            : "";
        Draw(CpuChart, CpuLine, item.Perf.Samples.Select(s => s.CpuPercent).ToList(), 100, PerfMeter.MaxSamples);
        Draw(MemChart, MemLine, item.Perf.Samples.Select(s => s.MemoryMb).ToList(), Math.Max(item.Entry.RamMb * 1.1, 1), PerfMeter.MaxSamples);
        Draw(TpsChart, TpsLine, item.Perf.Tps, 20, PerfMeter.MaxTps);
        TpsNote.Text = item.NoTickCommand ? "This Minecraft version can't report its tick speed (needs 1.20.3 or newer)."
            : item.Perf.Tps.LastOrDefault() is > 0 and var tps ? $"Now {tps:0.#} TPS" : running ? "Measured every 15 seconds." : "";
    }

    static void Draw(Canvas canvas, Polyline line, List<double> values, double max, int slots)
    {
        var points = new PointCollection();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        for (int i = 0; i < values.Count; i++)
            points.Add(new Point(w * i / slots, h - h * Math.Min(values[i] / max, 1)));
        line.Points = points;
    }

    async void Spark_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        if (!item.Runner.IsRunning) { SparkText.Text = "Start the server first."; return; }
        var s = item.Entry;
        if (Modrinth.For(s.Type) == null) { SparkText.Text = "Spark needs a Paper, Fabric, Forge or NeoForge server."; return; }
        // Paper has Spark built in since 1.21; everything else needs the Spark plugin or mod.
        bool builtIn = s.Type == "paper" && (!s.Version.StartsWith("1.") || int.TryParse(s.Version.Split('.')[1], out var minor) && minor >= 21);
        if (!builtIn && !Modrinth.Installed(s.Path, s.Type).Any(f => f.StartsWith("spark", StringComparison.OrdinalIgnoreCase)))
        {
            SparkButton.IsEnabled = false;
            try
            {
                await Modrinth.Install("spark", s.Type, s.Version, s.Path, new Progress<string>(m => SparkText.Text = m));
                SparkText.Text = "Spark is installed. Restart the server, then click again.";
            }
            catch (Exception ex) { SparkText.Text = "Couldn't install Spark: " + ex.Message; }
            SparkButton.IsEnabled = true;
            return;
        }
        if (!Host.Run(item, "spark profiler start --timeout 60")) return;
        item.SparkPending = true;
        SparkText.Text = "Watching for 60 seconds... the report opens in your browser when it's ready.";
    }

    void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_item != null) Show(_item);
    }

    void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) Host.Run(_item, "tick query");
    }
}
