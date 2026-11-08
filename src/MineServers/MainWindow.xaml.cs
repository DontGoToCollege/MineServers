using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public string Subtitle => $"{Runner.State} · {Entry.Type} {Entry.Version}".Trim();

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
        PropertyChanged?.Invoke(this, new("Entry"));
    }
}

public partial class MainWindow : Window
{
    const int MaxLog = 300_000;
    readonly AppConfig _cfg = Store.Load();
    readonly ObservableCollection<ServerItem> _items = new();
    readonly List<JavaInstall> _javas = JavaFinder.FindAll();
    bool _closing, _updating;

    ServerItem? Selected => ServerList.SelectedItem as ServerItem;
    static string ResumePath => Path.Combine(Store.DataDir, "resume.json");

    public MainWindow()
    {
        InitializeComponent();
        Title = $"MineServers {AppInfo.Version}";
        TunnelBox.ItemsSource = TunnelKinds.All;
        foreach (var s in _cfg.Servers) Add(s);
        ServerList.ItemsSource = _items;
        if (_items.Count > 0) ServerList.SelectedIndex = 0;

        // publish.ps1 signals this to swap in a new version: save worlds, close, and the new version restarts the servers.
        var updateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\MineServers-Close-{Environment.ProcessId}");
        ThreadPool.RegisterWaitForSingleObject(updateSignal, (_, _) => Dispatcher.BeginInvoke(CloseForUpdate), null, -1, true);
        Loaded += async (_, _) => await ResumeAfterUpdate();
    }

    void CloseForUpdate()
    {
        var ids = _items.Where(i => i.Runner.IsRunning).Select(i => i.Entry.Id).ToList();
        File.WriteAllText(ResumePath, System.Text.Json.JsonSerializer.Serialize(ids));
        _updating = true;
        Close();
    }

    async Task ResumeAfterUpdate()
    {
        if (!File.Exists(ResumePath)) return;
        var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ResumePath)) ?? new();
        File.Delete(ResumePath);
        foreach (var item in _items.Where(i => ids.Contains(i.Entry.Id)).ToList())
        {
            ServerList.SelectedItem = item;
            AppendLog(item, $"[MineServers] Updated to {AppInfo.Version}; starting the server again.");
            await StartServer(item);
        }
    }

    ServerItem Add(ServerEntry s)
    {
        var item = new ServerItem(s);
        item.Runner.Output += line => Dispatcher.BeginInvoke(() => AppendLog(item, line));
        item.Runner.StateChanged += () => Dispatcher.BeginInvoke(() => OnStateChanged(item));
        _items.Add(item);
        return item;
    }

    void Save() => Store.Save(_cfg);

    // ---------- list ----------

    void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    void ShowSelected()
    {
        var item = Selected;
        DetailPanel.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
        if (item == null) return;
        var s = item.Entry;
        ConsoleBox.Text = item.Log.ToString();
        ConsoleBox.ScrollToEnd();
        NameBox.Text = s.Name;
        TunnelBox.SelectedItem = TunnelKinds.All.FirstOrDefault(k => k.Id == s.Tunnel) ?? TunnelKinds.All[0];
        CustomCommandBox.Text = s.CustomTunnelCommand ?? "";
        CustomPatternBox.Text = s.CustomTunnelPattern ?? "";
        NgrokTokenBox.Password = _cfg.NgrokAuthToken ?? "";
        RamBox.Text = s.RamMb.ToString();
        PortBox.Text = s.Port.ToString();
        var javaChoices = new List<object> { "Automatic (best match for this version)" };
        javaChoices.AddRange(_javas);
        JavaBox.ItemsSource = javaChoices;
        JavaBox.SelectedItem = _javas.FirstOrDefault(j => string.Equals(j.Exe, s.JavaPath, StringComparison.OrdinalIgnoreCase)) ?? javaChoices[0];
        RefreshHeader();
    }

    void RefreshHeader()
    {
        var item = Selected;
        if (item == null) return;
        var s = item.Entry;
        NameText.Text = s.Name;
        var tunnel = TunnelKinds.All.FirstOrDefault(k => k.Id == s.Tunnel)?.Label ?? s.Tunnel;
        InfoText.Text = $"{item.Runner.State} · {s.Type} {s.Version} · port {s.Port} · tunnel: {tunnel}"
                        + (s.ImportedFrom != null ? $" · from {s.ImportedFrom}" : "");
        AddressBox.Text = item.Address;
        bool busy = item.Runner.State != "Stopped";
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = item.Runner.IsRunning && item.Runner.State != "Stopping";
    }

    void AppendLog(ServerItem item, string line)
    {
        item.Log.AppendLine(line);
        bool trimmed = false;
        if (item.Log.Length > MaxLog)
        {
            item.Log.Remove(0, item.Log.Length - MaxLog / 2);
            trimmed = true;
        }
        if (item != Selected) return;
        if (trimmed) ConsoleBox.Text = item.Log.ToString();
        else ConsoleBox.AppendText(line + Environment.NewLine);
        ConsoleBox.ScrollToEnd();
    }

    async void OnStateChanged(ServerItem item)
    {
        if (item.Runner.State == "Stopped" && item.Tunnel != null)
        {
            var t = item.Tunnel;
            item.Tunnel = null;
            item.Address = "";
            await t.Stop();
        }
        item.Refresh();
        if (item == Selected) RefreshHeader();
    }

    // ---------- start / stop ----------

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) await StartServer(item);
    }

    async Task StartServer(ServerItem item)
    {
        var progress = new Progress<string>(m => AppendLog(item, "[MineServers] " + m));
        await CheckFirewall(item);
        try
        {
            await item.Runner.Start(item.Entry, progress);
        }
        catch (Exception ex)
        {
            AppendLog(item, "[MineServers] Couldn't start: " + ex.Message);
            return;
        }

        var tunnel = Tunnel.Create(item.Entry, _cfg);
        tunnel.Log += line => Dispatcher.BeginInvoke(() => AppendLog(item, line));
        item.Tunnel = tunnel;
        item.Address = "Starting tunnel...";
        RefreshHeader();
        try
        {
            var address = await tunnel.Start(item.Entry, CancellationToken.None);
            if (item.Tunnel != tunnel) { await tunnel.Stop(); return; } // the server stopped meanwhile
            item.Address = address;
            AppendLog(item, $"[MineServers] Players join at: {address}");
        }
        catch (Exception ex)
        {
            AppendLog(item, "[Tunnel] Failed: " + ex.Message);
            if (item.Tunnel == tunnel) item.Address = $"Tunnel failed. LAN players can use {Tunnel.LanIp()}:{item.Entry.Port}";
            await tunnel.Stop();
        }
        if (item == Selected) RefreshHeader();
    }

    /// <summary>LAN and UPnP players connect straight to this PC, so Windows Firewall must let the port in.</summary>
    async Task CheckFirewall(ServerItem item)
    {
        var s = item.Entry;
        if (s.Tunnel is not (TunnelKinds.None or TunnelKinds.Upnp) || s.SkipFirewallCheck) return;
        try
        {
            if (await Task.Run(() => Firewall.IsAllowed(s.Port))) return;
        }
        catch (Exception ex)
        {
            AppendLog(item, "[MineServers] Couldn't check Windows Firewall: " + ex.Message);
            return;
        }
        var answer = MessageBox.Show(
            $"Windows Firewall is blocking port {s.Port}, so other computers can't join (they get \"Connection timed out\").\n\n"
            + $"Allow Minecraft through the firewall on port {s.Port}? Windows will ask for your permission.\n\n"
            + "Choose No to leave the firewall alone and not be asked again for this server.",
            "MineServers", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No)
        {
            s.SkipFirewallCheck = true;
            Save();
        }
        else if (answer == MessageBoxResult.Yes)
        {
            AppendLog(item, await Firewall.Allow(s.Port)
                ? $"[MineServers] Windows Firewall now allows port {s.Port}."
                : "[MineServers] The firewall wasn't changed, so other computers may still time out.");
        }
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) await item.Runner.Stop();
    }

    void Send_Click(object sender, RoutedEventArgs e) => SendCommand();

    void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendCommand();
    }

    void SendCommand()
    {
        var item = Selected;
        var cmd = CommandBox.Text.Trim().TrimStart('/');
        if (item == null || cmd.Length == 0) return;
        if (!item.Runner.IsRunning) { AppendLog(item, "[MineServers] The server isn't running."); return; }
        AppendLog(item, "> " + cmd);
        item.Runner.Send(cmd);
        CommandBox.Clear();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (AddressBox.Text.Length > 0) Clipboard.SetText(AddressBox.Text);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && Directory.Exists(item.Entry.Path))
            Process.Start(new ProcessStartInfo(item.Entry.Path) { UseShellExecute = true });
    }

    // ---------- settings ----------

    void TunnelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = TunnelBox.SelectedItem as TunnelKind;
        TunnelHelp.Text = kind?.Help ?? "";
        CustomPanel.Visibility = kind?.Id == TunnelKinds.Custom ? Visibility.Visible : Visibility.Collapsed;
        NgrokPanel.Visibility = kind?.Id == TunnelKinds.Ngrok ? Visibility.Visible : Visibility.Collapsed;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        if (!int.TryParse(RamBox.Text, out var ram) || ram < 512)
        {
            MessageBox.Show("Memory must be a number of MB, at least 512.", "MineServers");
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Port must be a number from 1 to 65535.", "MineServers");
            return;
        }
        var s = item.Entry;
        s.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : s.Name;
        s.Tunnel = (TunnelBox.SelectedItem as TunnelKind)?.Id ?? TunnelKinds.None;
        s.CustomTunnelCommand = NullIfEmpty(CustomCommandBox.Text);
        s.CustomTunnelPattern = NullIfEmpty(CustomPatternBox.Text);
        s.RamMb = ram;
        s.Port = port;
        s.JavaPath = (JavaBox.SelectedItem as JavaInstall)?.Exe;
        _cfg.NgrokAuthToken = NullIfEmpty(NgrokTokenBox.Password);
        Save();
        item.Refresh();
        RefreshHeader();
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null) return;
        if (item.Runner.IsRunning) { MessageBox.Show("Stop the server first.", "MineServers"); return; }
        if (MessageBox.Show($"Remove \"{item.Entry.Name}\" from the list?\n\nIts folder and world stay on disk:\n{item.Entry.Path}",
                "MineServers", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _cfg.Servers.Remove(item.Entry);
        _items.Remove(item);
        Save();
        ShowSelected();
    }

    // ---------- new / import ----------

    void NewServer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewServerWindow(_cfg.Servers.Select(s => s.Port)) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Created == null) return;
        _cfg.Servers.Add(dlg.Created);
        Save();
        ServerList.SelectedItem = Add(dlg.Created);
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(SquidImport.ListPath))
        {
            MessageBox.Show("No SquidServers servers were found on this PC.", "MineServers");
            return;
        }
        var found = SquidImport.Read(SquidImport.ListPath, _cfg.Servers);
        if (found.Count == 0)
        {
            MessageBox.Show("All your SquidServers servers are already in the list.", "MineServers");
            return;
        }
        foreach (var s in found) { _cfg.Servers.Add(s); Add(s); }
        Save();
        MessageBox.Show($"Imported {found.Count} server(s). Their folders stay where SquidServers keeps them.\n\n"
                        + "Don't run the same server in both apps at once.", "MineServers");
        ServerList.SelectedIndex = _items.Count - found.Count;
    }

    // ---------- closing ----------

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        var running = _items.Where(i => i.Runner.IsRunning).ToList();
        if (running.Count > 0 && !_updating && MessageBox.Show($"Stop {running.Count} running server(s) and close MineServers?\nWorlds are saved first.",
                "MineServers", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _closing = true;
        IsEnabled = false;
        Title = "MineServers - saving worlds and stopping servers...";
        await Task.WhenAll(running.Select(i => i.Runner.Stop()));
        await Task.WhenAll(_items.Where(i => i.Tunnel != null).Select(i => i.Tunnel!.Stop()));
        PlayitTunnel.StopAgent();
        // Posted, not called: if nothing above had to wait, we're still inside this Closing event, where Close() throws.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
