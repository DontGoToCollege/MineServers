using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MineServers.Core;

namespace MineServers;

/// <summary>Reads the server's earlier log files (logs\*.log.gz and latest.log), newest first.</summary>
public class OldLogsWindow : Window
{
    readonly TextBox _text = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), Padding = new Thickness(6),
    };

    public OldLogsWindow(string logsDir)
    {
        Title = "Old logs";
        Width = 900;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var files = Directory.Exists(logsDir)
            // latest.log first: its time stamp lags while the server writes it.
            ? new DirectoryInfo(logsDir).GetFiles("*.log*").OrderByDescending(f => f.Name == "latest.log").ThenByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName).ToList()
            : new List<string>();
        var pick = new ComboBox { ItemsSource = files.Select(Path.GetFileName).ToList(), Margin = new Thickness(0, 0, 0, 6) };
        pick.SelectionChanged += (_, _) => { if (pick.SelectedIndex >= 0) Load(files[pick.SelectedIndex]); };
        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(pick, Dock.Top);
        panel.Children.Add(pick);
        panel.Children.Add(_text);
        Content = panel;
        if (files.Count > 0) pick.SelectedIndex = 0;
        else _text.Text = "This server has no log files yet.";
    }

    void Load(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream s = file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(fs, CompressionMode.Decompress) : fs;
            using var reader = new StreamReader(s);
            _text.Text = reader.ReadToEnd();
            _text.ScrollToEnd();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            _text.Text = "Couldn't read it: " + e.Message;
        }
    }
}
