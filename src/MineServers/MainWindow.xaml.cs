using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MineServers.Core;

namespace MineServers;

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

        // Every 2 seconds: sample CPU/memory and run any scheduled tasks that are due.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    void Tick()
    {
        var now = DateTime.Now;
        foreach (var item in _items)
        {
            if (item.Runner.IsRunning) item.Perf.Sample(item.Runner.Process);
            foreach (var task in item.Entry.Tasks.Where(t => Scheduler.IsDue(t, now)).ToList())
            {
                task.LastRun = now;
                SaveConfig();
                _ = RunTask(item, task);
            }
        }
        if (Selected is { } sel && Tabs.SelectedContent == PerfTab) PerfTab.Show(sel);
    }

    // ---------- used by the tabs ----------

    internal void SaveConfig() => Store.Save(_cfg);

    /// <summary>Sends a console command, showing it in the console. Says so if the server isn't running.</summary>
    internal bool Run(ServerItem item, string command)
    {
        if (!item.Runner.IsRunning)
        {
            MessageBox.Show("Start the server first, then try again.", "MineServers");
            return false;
        }
        AppendLog(item, "> " + command);
        item.Runner.Send(command);
        return true;
    }

    internal async Task RestartServer(ServerItem item)
    {
        if (item.Runner.IsRunning)
        {
            await item.Runner.Stop();
            if (item.Tunnel is { } t) { item.Tunnel = null; item.Address = ""; await t.Stop(); }
        }
        await StartServer(item);
    }

    /// <summary>Runs a scheduled task's steps: console commands plus !wait, !restart, !start and !stop.</summary>
    internal async Task RunTask(ServerItem item, ScheduledTask task)
    {
        AppendLog(item, $"[Scheduler] Running \"{task.Name}\".");
        foreach (var step in Scheduler.Steps(task.Commands))
        {
            var word = step.Split(' ', 2)[0].ToLowerInvariant();
            switch (word)
            {
                case "!wait":
                    await Task.Delay(TimeSpan.FromSeconds(int.TryParse(step.Split(' ', 2).ElementAtOrDefault(1), out var s) ? Math.Max(s, 0) : 0));
                    break;
                case "!restart":
                    if (item.Runner.IsRunning) await RestartServer(item);
                    break;
                case "!start":
                    if (!item.Runner.IsRunning) await StartServer(item);
                    break;
                case "!stop":
                    await item.Runner.Stop();
                    break;
                default:
                    if (item.Runner.IsRunning) { AppendLog(item, "> " + step); item.Runner.Send(step); }
                    else AppendLog(item, $"[Scheduler] Skipped \"{step}\": the server isn't running.");
                    break;
            }
        }
    }

    /// <summary>Asks the server for its game rules. Tries 26.x names first, then 1.x camelCase. The replies stay out of the console.</summary>
    async Task QueryRules(ServerItem item)
    {
        item.QuietUntil = DateTime.Now.AddSeconds(8);
        item.Runner.Send("gamerule mob_griefing");
        await Task.Delay(1500);
        if (item.SnakeRules == null) { item.Runner.Send("gamerule mobGriefing"); await Task.Delay(1500); }
        if (item.SnakeRules is not { } snake || !item.Runner.IsRunning) return;
        item.QuietUntil = DateTime.Now.AddSeconds(5);
        foreach (var name in GameSettings.Rules.Select(r => r.NameFor(snake)).OfType<string>())
            item.Runner.Send("gamerule " + name);
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
        item.Runner.Output += line => Dispatcher.BeginInvoke(() =>
        {
            int online = item.Online.Count;
            if (!item.Observe(line)) AppendLog(item, line);
            if (Players.RejectedName(line) is { } name) OfferToAllow(item, name);
            if (online != item.Online.Count) item.Refresh();
            if (item != Selected) return;
            if (online != item.Online.Count) OnlineList.ItemsSource = item.Online.ToList();
            if (GameSettings.ParseRule(line) != null) GamePanel.ShowRules(item);
            if (PerfMeter.ParseTickMs(line) != null) PerfTab.Show(item);
        });
        item.Runner.StateChanged += () => Dispatcher.BeginInvoke(() => OnStateChanged(item));
        _items.Add(item);
        return item;
    }

    void Save() => SaveConfig();

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
        PluginsItem.Header = Modrinth.For(s.Type) is { } k ? char.ToUpper(k.Noun[0]) + k.Noun[1..] + "s" : "Plugins";
        RefreshHeader();
        RefreshTabs(item);
    }

    void RefreshTabs(ServerItem item)
    {
        RefreshPlayers();
        PerfTab.Show(item);
        SchedTab.Show(item);
        PlugTab.Show(item);
        FileTab.Show(item);
        GamePanel.Show(item);
    }

    // ---------- players ----------

    void RefreshPlayers()
    {
        if (Selected is not { } item) return;
        var dir = item.Entry.Path;
        try { WhitelistBox.IsChecked = ServerProperties.Get(Path.Combine(dir, "server.properties"), "white-list") == "true"; }
        catch (IOException) { } // still busy after waiting: keep showing the last known state
        AllowedList.ItemsSource = Players.Allowed(dir);
        BannedList.ItemsSource = Players.Banned(dir);
        OpsList.ItemsSource = Players.Operators(dir);
        OnlineList.ItemsSource = item.Online.ToList();
        PlayersNote.Visibility = item.Runner.IsRunning ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnlineCommand(string verb)
    {
        if (Selected is { } item && OnlineList.SelectedItem is string name) PlayerCommand(item, $"{verb} {name}");
        else MessageBox.Show("Pick a player in the Online now list first.", "MineServers");
    }

    void Kick_Click(object sender, RoutedEventArgs e) => OnlineCommand("kick");
    void OpOnline_Click(object sender, RoutedEventArgs e) => OnlineCommand("op");
    void BanOnline_Click(object sender, RoutedEventArgs e) => OnlineCommand("ban");
    void RefreshOnline_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) PlayerCommand(item, "list"); }
    void OpName_Click(object sender, RoutedEventArgs e) => PlayerNameCommand("op");

    void Deop_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && OpsList.SelectedItem is string name) PlayerCommand(item, $"deop {name}");
    }

    /// <summary>Someone was turned away by the whitelist: one click lets them in for good.</summary>
    void OfferToAllow(ServerItem item, string name)
    {
        if (!item.AskedAbout.Add(name)) return;
        if (MessageBox.Show($"{name} tried to join \"{item.Entry.Name}\" but isn't on its whitelist.\n\nLet {name} join from now on?",
                "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        PlayerCommand(item, $"whitelist add {name}");
        AppendLog(item, $"[MineServers] {name} can join now. Ask them to connect again.");
    }

    /// <summary>Runs a player command on the server, then reloads the lists once the server has saved them.</summary>
    async void PlayerCommand(ServerItem item, string command)
    {
        if (!item.Runner.IsRunning)
        {
            MessageBox.Show("Start the server first, then try again.", "MineServers");
            return;
        }
        AppendLog(item, "> " + command);
        item.Runner.Send(command);
        await Task.Delay(800);
        if (item == Selected) RefreshPlayers();
    }

    void PlayerNameCommand(string verb)
    {
        var name = PlayerNameBox.Text.Trim();
        if (!Players.IsValidName(name))
        {
            MessageBox.Show("Type a Minecraft name (3 to 16 letters, numbers or _).", "MineServers");
            return;
        }
        if (Selected is { } item) PlayerCommand(item, $"{verb} {name}");
        PlayerNameBox.Clear();
    }

    void AllowPlayer_Click(object sender, RoutedEventArgs e) => PlayerNameCommand("whitelist add");
    void BanPlayer_Click(object sender, RoutedEventArgs e) => PlayerNameCommand("ban");

    void RemoveAllowed_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && AllowedList.SelectedItem is string name) PlayerCommand(item, $"whitelist remove {name}");
    }

    void Unban_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && BannedList.SelectedItem is string name) PlayerCommand(item, $"pardon {name}");
    }

    void WhitelistBox_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        bool on = WhitelistBox.IsChecked == true;
        if (item.Runner.IsRunning) PlayerCommand(item, on ? "whitelist on" : "whitelist off"); // the server saves the setting itself
        else
        {
            try { ServerProperties.Set(Path.Combine(item.Entry.Path, "server.properties"), "white-list", on ? "true" : "false"); }
            catch (IOException ex)
            {
                WhitelistBox.IsChecked = !on; // show what's really in the file
                MessageBox.Show("Couldn't change the whitelist setting because another program is holding server.properties open:\n\n" + ex.Message, "MineServers");
            }
        }
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
        var state = item.Runner.State;
        if (state == "Stopped")
        {
            item.ClearLive();
            if (item.Tunnel is { } t)
            {
                item.Tunnel = null;
                item.Address = "";
                await t.Stop();
            }
        }
        item.Refresh();
        if (item == Selected) { RefreshHeader(); RefreshTabs(item); }
        if (state == "Running") await QueryRules(item);
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
            if (item.Entry.PendingProperties.Count > 0)
            {
                // Settings saved while the server ran. Written now, before it starts, so it can't overwrite them.
                ServerProperties.SetMany(Path.Combine(item.Entry.Path, "server.properties"), item.Entry.PendingProperties);
                AppendLog(item, $"[MineServers] Applied {item.Entry.PendingProperties.Count} setting change(s).");
                item.Entry.PendingProperties.Clear();
                Save();
            }
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

    void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendCommand();
        if (e.Key is not (Key.Up or Key.Down) || Selected is not { } item) return;
        var history = item.Entry.CommandHistory;
        if (history.Count == 0) return;
        item.HistoryIndex = Math.Clamp(item.HistoryIndex + (e.Key == Key.Up ? 1 : -1), -1, history.Count - 1);
        CommandBox.Text = item.HistoryIndex < 0 ? "" : history[history.Count - 1 - item.HistoryIndex];
        CommandBox.CaretIndex = CommandBox.Text.Length;
        e.Handled = true;
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
        var history = item.Entry.CommandHistory;
        history.Remove(cmd);
        history.Add(cmd);
        if (history.Count > 50) history.RemoveAt(0);
        item.HistoryIndex = -1;
        Save();
    }

    void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        item.Log.Clear();
        ConsoleBox.Clear();
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

    async void Save_Click(object sender, RoutedEventArgs e)
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

        var changes = GamePanel.Changes();
        bool restartNeeded = false;
        if (changes.Count > 0 && item.Runner.IsRunning)
        {
            foreach (var (k, v) in changes) s.PendingProperties[k] = v; // the running server would overwrite the file
            restartNeeded = true;
        }
        else if (changes.Count > 0)
        {
            try { ServerProperties.SetMany(Path.Combine(s.Path, "server.properties"), changes); }
            catch (IOException ex)
            {
                foreach (var (k, v) in changes) s.PendingProperties[k] = v; // keep them; they're applied at the next start
                AppendLog(item, "[MineServers] server.properties is busy; settings will be applied at the next start. " + ex.Message);
            }
        }
        Save();
        item.Refresh();
        RefreshHeader();
        GamePanel.Show(item);
        if (restartNeeded && MessageBox.Show($"Saved. The server reads these {changes.Count} setting(s) when it starts.\n\nRestart the server now? Players will be disconnected for a moment.",
                "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RestartServer(item);
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
