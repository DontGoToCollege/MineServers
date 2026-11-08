using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

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
        Draw(CpuChart, CpuLine, item.Perf.Samples.Select(s => s.CpuPercent).ToList(), 100);
        Draw(MemChart, MemLine, item.Perf.Samples.Select(s => s.MemoryMb).ToList(), Math.Max(item.Entry.RamMb * 1.1, 1));
    }

    static void Draw(Canvas canvas, Polyline line, List<double> values, double max)
    {
        var points = new PointCollection();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        for (int i = 0; i < values.Count; i++)
            points.Add(new Point(w * i / Core.PerfMeter.MaxSamples, h - h * Math.Min(values[i] / max, 1)));
        line.Points = points;
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
