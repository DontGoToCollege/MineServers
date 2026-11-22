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
        BedrockPanel.Visibility = item.Entry.Type == "paper" ? Visibility.Visible : Visibility.Collapsed;
        bool geyser = Geyser.Installed(item.Entry.Path);
        BedrockButton.IsEnabled = !geyser;
        BedrockButton.Content = geyser ? "Bedrock players can join (installed)" : "Let Bedrock players join";
        if (changed)
        {
            ResultList.ItemsSource = null;
            DescriptionText.Text = StatusText.Text = UpdatesText.Text = "";
            _updates.Clear();
            UpdateButton.IsEnabled = false;
        }
    }

    List<ModUpdate> _updates = new();

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        CheckButton.IsEnabled = false;
        UpdatesText.Text = "Checking Modrinth...";
        try
        {
            _updates = await Modrinth.CheckUpdates(item.Entry.Path, item.Entry.Type, item.Entry.Version);
            UpdatesText.Text = _updates.Count == 0 ? "Everything is up to date (add-ons not from Modrinth can't be checked)."
                : $"{_updates.Count} update(s):\n" + string.Join("\n", _updates);
        }
        catch (Exception ex) { UpdatesText.Text = "Couldn't check: " + ex.Message; }
        CheckButton.IsEnabled = true;
        UpdateButton.IsEnabled = _updates.Count > 0;
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        if (item.Runner.State != "Stopped") { MessageBox.Show("Stop the server first: Windows won't let files in use be replaced.", "MineServers"); return; }
        UpdateButton.IsEnabled = false;
        var failed = new List<string>();
        foreach (var u in _updates)
        {
            UpdatesText.Text = $"Updating {u.OldFile}...";
            try { await Modrinth.ApplyUpdate(item.Entry.Path, item.Entry.Type, u); }
            catch (Exception ex) { failed.Add($"{u.OldFile}: {ex.Message}"); }
        }
        UpdatesText.Text = failed.Count == 0 ? $"Updated {_updates.Count} add-on(s)." : "Some couldn't be updated:\n" + string.Join("\n", failed);
        _updates.Clear();
        if (_item == item) Show(item);
    }

    async void Bedrock_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        BedrockButton.IsEnabled = false;
        try
        {
            await Geyser.Install(item.Entry.Path, new Progress<string>(m => StatusText.Text = m));
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't install Geyser: " + ex.Message;
            Show(item);
            return;
        }
        // LAN and UPnP Bedrock players connect straight in on UDP, which Windows Firewall blocks by default.
        bool allowed = true;
        try { allowed = await Task.Run(() => Firewall.IsAllowed(Geyser.BedrockPort, udp: true)); } catch (Exception) { }
        if (!allowed && MessageBox.Show($"Windows Firewall blocks Bedrock's port ({Geyser.BedrockPort}, UDP). Allow it? Windows will ask for your permission.",
                "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await Firewall.Allow(Geyser.BedrockPort, udp: true);
        MessageBox.Show("Geyser and Floodgate are installed. Restart the server to turn them on.\n\n"
                        + Geyser.JoinInfo(item.Entry, item.Address) + "\n\nBedrock players don't need a Java account.", "MineServers");
        if (_item == item) Show(item);
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
