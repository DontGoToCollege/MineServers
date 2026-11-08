using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MineServers.Core;

namespace MineServers.Tabs;

public partial class PluginsTab : UserControl
{
    ServerItem? _item;

    public PluginsTab() => InitializeComponent();

    string? Folder => _item != null && Modrinth.For(_item.Entry.Type) is { } k ? Path.Combine(_item.Entry.Path, k.Folder) : null;

    public void Show(ServerItem item)
    {
        bool changed = _item != item;
        _item = item;
        var kind = Modrinth.For(item.Entry.Type);
        UnsupportedText.Visibility = kind == null ? Visibility.Visible : Visibility.Collapsed;
        Panel.Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
        if (kind is not { } k) return;
        var noun = k.Noun;
        SearchHelp.Text = $"Search Modrinth for {noun}s that work with {item.Entry.Type} {item.Entry.Version}. Leave empty for the most popular.";
        InstalledTitle.Text = $"Installed {noun}s";
        InstalledList.ItemsSource = Modrinth.Installed(item.Entry.Path, item.Entry.Type)
            .Select(f => f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? $"{f[..^9]}  (off)" : f).ToList();
        if (changed)
        {
            ResultList.ItemsSource = null;
            DescriptionText.Text = StatusText.Text = "";
        }
    }

    async void Search_Click(object sender, RoutedEventArgs e) => await Search();

    async void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await Search();
    }

    async Task Search()
    {
        if (_item == null) return;
        StatusText.Text = "Searching...";
        try
        {
            var hits = await Modrinth.Search(QueryBox.Text.Trim(), _item.Entry.Type, _item.Entry.Version);
            ResultList.ItemsSource = hits;
            StatusText.Text = hits.Count == 0 ? "Nothing found for this Minecraft version." : "";
        }
        catch (Exception ex) { StatusText.Text = "Search failed: " + ex.Message; }
    }

    void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DescriptionText.Text = (ResultList.SelectedItem as ModHit)?.Description ?? "";

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || ResultList.SelectedItem is not ModHit hit) return;
        var item = _item;
        InstallButton.IsEnabled = false;
        try
        {
            var added = await Modrinth.Install(hit.Id, item.Entry.Type, item.Entry.Version, item.Entry.Path,
                new Progress<string>(m => StatusText.Text = m));
            StatusText.Text = added.Count == 0 ? $"{hit.Title} was already installed."
                : $"Installed {string.Join(", ", added)}." + (item.Runner.IsRunning ? " Restart the server to load it." : "");
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't install {hit.Title}: {ex.Message}"; }
        InstallButton.IsEnabled = true;
        if (_item == item) Show(item);
    }

    string? SelectedFile()
    {
        if (InstalledList.SelectedItem is not string shown || Folder is not { } dir) return null;
        var name = shown.EndsWith("  (off)") ? shown[..^7] + ".disabled" : shown;
        return Path.Combine(dir, name);
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || SelectedFile() is not { } file) return;
        try
        {
            File.Move(file, file.EndsWith(".disabled") ? file[..^9] : file + ".disabled");
        }
        catch (IOException ex) { MessageBox.Show("Couldn't change it (stop the server first if it's in use):\n\n" + ex.Message, "MineServers"); }
        Show(_item);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || SelectedFile() is not { } file) return;
        if (MessageBox.Show($"Delete {Path.GetFileName(file)}?\n\nIts settings folder (if any) is kept.", "MineServers", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { File.Delete(file); }
        catch (IOException ex) { MessageBox.Show("Couldn't delete it (stop the server first if it's in use):\n\n" + ex.Message, "MineServers"); }
        Show(_item);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
