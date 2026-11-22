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
    const int MaxLog = 5000; // console lines kept per server
    readonly AppConfig _cfg = Store.Load();
    readonly ObservableCollection<ServerItem> _items = new();
    readonly List<JavaInstall> _javas = JavaFinder.FindAll();
    readonly Tray _tray;
    readonly HashSet<ServerItem> _wasRunning = new(); // reached "Running", so a stop is worth announcing
    bool _closing, _updating;

    ServerItem? Selected => ServerList.SelectedItem as ServerItem;
    static string ResumePath => Path.Combine(Store.DataDir, "resume.json");

    public MainWindow()
    {
        InitializeComponent();
        Title = $"MineServers {AppInfo.Version}";
        _tray = new Tray(this, Close);
        // Fabric servers SquidServers labelled "vanilla" (imported before 1.5.0) get their Mods tab back.
        var secretlyFabric = _cfg.Servers.Where(s => s.Type == "vanilla" && SquidImport.IsFabric(s.Path)).ToList();
        foreach (var s in secretlyFabric) s.Type = "fabric";
        if (secretlyFabric.Count > 0) SaveConfig();
        TunnelBox.ItemsSource = TunnelKinds.All;
        foreach (var s in _cfg.Servers) Add(s);
        ServerList.ItemsSource = _items;
        if (_items.Count > 0) ServerList.SelectedIndex = 0;

        // publish.ps1 signals this to swap in a new version: save worlds, close, and the new version restarts the servers.
        var updateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\MineServers-Close-{Environment.ProcessId}");
        ThreadPool.RegisterWaitForSingleObject(updateSignal, (_, _) => Dispatcher.BeginInvoke(CloseForUpdate), null, -1, true);
        Loaded += async (_, _) => await ResumeAfterUpdate();
        // Started with Windows: stay out of the way, by the clock.
        if (Environment.GetCommandLineArgs().Contains("--minimized"))
        {
            WindowState = WindowState.Minimized;
            ShowActivated = false;
            Loaded += (_, _) => { if (_cfg.MinimizeToTray) Hide(); };
        }

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
            if (item.Runner.State == "Running" && !item.NoTickCommand && now >= item.NextTpsSample && now >= item.QuietUntil)
            {
                // For the TPS graph; the reply is kept out of the console.
                item.NextTpsSample = now.AddSeconds(15);
                item.QuietTicksUntil = now.AddSeconds(3);
                item.Runner.Send("tick query");
            }
            foreach (var task in item.Entry.Tasks.Where(t => Scheduler.IsDue(t, now) && Scheduler.ConditionMet(t, item.Online.Count)).ToList())
            {
                task.LastRun = now;
                if (task.Kind == "once") task.Enabled = false;
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
                case "!backup":
                    await BackUp(item);
                    break;
                default:
                    if (item.Runner.IsRunning) { AppendLog(item, "> " + step); item.Runner.Send(step); }
                    else AppendLog(item, $"[Scheduler] Skipped \"{step}\": the server isn't running.");
                    break;
            }
        }
    }

    /// <summary>Backs up the server. While it runs, saving is paused and the world saved first, so the copy is consistent.</summary>
    internal async Task<bool> BackUp(ServerItem item, string? label = null)
    {
        if (item.Busy) { AppendLog(item, "[MineServers] Skipped the backup: another backup or world change is still going."); return false; }
        item.Busy = true;
        if (item == Selected) WorldTab.Show(item);
        try
        {
            var (path, skipped) = await WithWorldSaved(item, () => Backups.Create(item.Entry, label, item.Entry.BackupKeep));
            AppendLog(item, $"[MineServers] Backed up to {Path.GetFileName(path)}."
                            + (skipped.Count > 0 ? $" Left out (in use): {string.Join(", ", skipped.Take(5))}" : ""));
            return true;
        }
        catch (Exception ex)
        {
            AppendLog(item, "[MineServers] Backup failed: " + ex.Message);
            return false;
        }
        finally
        {
            item.Busy = false;
            if (item == Selected) WorldTab.Show(item);
        }
    }

    /// <summary>Runs <paramref name="work"/> off the UI thread. If the server is running, it stops writing the world until the work is done.</summary>
    internal async Task<T> WithWorldSaved<T>(ServerItem item, Func<T> work)
    {
        bool running = item.Runner.IsRunning;
        if (running)
        {
            var saved = new TaskCompletionSource();
            void OnLine(string line) { if (line.Contains("Saved the game")) saved.TrySetResult(); }
            item.Runner.Output += OnLine;
            item.Runner.Send("save-off");
            item.Runner.Send("save-all flush");
            await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            item.Runner.Output -= OnLine;
        }
        try { return await Task.Run(work); }
        finally { if (running) item.Runner.Send("save-on"); }
    }

    /// <summary>Restarts a server that crashed, up to 3 times in 10 minutes, and says so by the clock.</summary>
    async Task OnCrash(ServerItem item)
    {
        var now = DateTime.Now;
        item.Crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        item.Crashes.Add(now);
        bool retry = item.Entry.RestartOnCrash && item.Crashes.Count <= 3;
        var what = retry ? "Restarting it in 5 seconds."
            : item.Entry.RestartOnCrash ? "It crashed 3 times in 10 minutes, so it's left stopped. Its console may say why."
            : "Its console may say why.";
        AppendLog(item, "[MineServers] The server crashed. " + what);
        _tray.Notify($"{item.Entry.Name} crashed", what);
        _wasRunning.Remove(item);
        Discord.Post(_cfg.DiscordWebhook, $"{item.Entry.Name} crashed. {what}");
        if (!retry) return;
        await Task.Delay(5000);
        if (item.Runner.State == "Stopped") await StartServer(item);
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

    void AppSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new AppSettingsWindow(_cfg) { Owner = this }.ShowDialog() == true) SaveConfig();
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _cfg.MinimizeToTray) Hide();
    }

    async Task ResumeAfterUpdate()
    {
        if (!File.Exists(ResumePath))
        {
            foreach (var item in _items.Where(i => i.Entry.AutoStart).ToList()) await StartServer(item);
            return;
        }
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
            if (Players.JoinedName(line) is { } joined)
            {
                if (_cfg.NotifyJoins) _tray.Notify($"{joined} joined", $"{item.Entry.Name} · {item.Online.Count} online");
                Discord.Post(_cfg.DiscordWebhook, $"**{joined}** joined {item.Entry.Name} ({item.Online.Count} online)");
            }
            else if (Players.LeftName(line) is { } left)
                Discord.Post(_cfg.DiscordWebhook, $"**{left}** left {item.Entry.Name} ({item.Online.Count} online)");
            if (item.SparkPending && System.Text.RegularExpressions.Regex.Match(line, @"https://spark\.lucko\.me/\w+") is { Success: true } report)
            {
                item.SparkPending = false;
                Process.Start(new ProcessStartInfo(report.Value) { UseShellExecute = true });
            }
            if (online != item.Online.Count) { item.Refresh(); SaveConfig(); } // playtime is kept in the config
            if (item != Selected) return;
            if (online != item.Online.Count) ShowOnline(item);
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
        ConsoleList.ItemsSource = item.Log;
        ApplyFilter(item);
        ScrollConsoleToEnd();
        NameBox.Text = s.Name;
        TunnelBox.SelectedItem = TunnelKinds.All.FirstOrDefault(k => k.Id == s.Tunnel) ?? TunnelKinds.All[0];
        CustomCommandBox.Text = s.CustomTunnelCommand ?? "";
        CustomPatternBox.Text = s.CustomTunnelPattern ?? "";
        NgrokTokenBox.Password = _cfg.NgrokAuthToken ?? "";
        RamBox.Text = s.RamMb.ToString();
        PortBox.Text = s.Port.ToString();
        RestartOnCrashBox.IsChecked = s.RestartOnCrash;
        AutoStartBox.IsChecked = s.AutoStart;
        AikarBox.IsChecked = s.AikarFlags;
        ShowIcon(s);
        UpgradeBox.ItemsSource = null;
        bool upgradable = s.Type is "vanilla" or "paper" or "fabric";
        UpgradeBox.IsEnabled = UpgradeButton.IsEnabled = upgradable;
        UpgradeHelp.Text = !upgradable ? $"MineServers can't upgrade {s.Type} servers."
            : $"Now on {s.Version}. A backup is made first." + (s.Type == "paper" ? " Picking the same version gets the latest Paper build." : "");
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
        WorldTab.Show(item);
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
        BannedIpList.ItemsSource = Players.BannedIps(dir);
        ShowOnline(item);
        PlayersNote.Visibility = item.Runner.IsRunning ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowOnline(ServerItem item)
    {
        OnlineList.ItemsSource = item.Online.ToList();
        TeleportBox.ItemsSource = item.Online.ToList();
        KnownList.ItemsSource = item.PlayerSummaries();
    }

    void BanIpOnline_Click(object sender, RoutedEventArgs e) => OnlineCommand("ban-ip");

    void GameMode_Click(object sender, RoutedEventArgs e) =>
        OnlineCommand($"gamemode {((ComboBoxItem)GameModeBox.SelectedItem).Content}");

    void Teleport_Click(object sender, RoutedEventArgs e)
    {
        if (TeleportBox.SelectedItem is string target) OnlineCommand("tp", " " + target);
        else MessageBox.Show("Pick who to teleport to.", "MineServers");
    }

    void Tell_Click(object sender, RoutedEventArgs e)
    {
        var text = TellBox.Text.ReplaceLineEndings(" ").Trim(); // one line, so it can't become a second command
        if (text.Length == 0) return;
        OnlineCommand("tell", " " + text);
        TellBox.Clear();
    }

    void UnbanIp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item && BannedIpList.SelectedItem is string ip && Players.IsValidIp(ip)) PlayerCommand(item, $"pardon-ip {ip}");
    }

    void OnlineCommand(string verb, string after)
    {
        if (Selected is { } item && OnlineList.SelectedItem is string name) PlayerCommand(item, $"{verb} {name}{after}");
        else MessageBox.Show("Pick a player in the Online now list first.", "MineServers");
    }

    void OnlineCommand(string verb) => OnlineCommand(verb, "");

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
        // Bedrock players (".Name") have no Java account, so Floodgate keeps its own whitelist.
        PlayerCommand(item, name.StartsWith('.') ? $"fwhitelist add {name[1..]}" : $"whitelist add {name}");
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
        item.Log.Add(new LogLine(line));
        if (item.Log.Count > MaxLog) for (int i = 0; i < MaxLog / 5; i++) item.Log.RemoveAt(0);
        if (item == Selected && FilterBox.Text.Length == 0) ScrollConsoleToEnd();
    }

    void ScrollConsoleToEnd()
    {
        if (ConsoleList.Items.Count > 0) ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Selected is { } item) ApplyFilter(item);
    }

    void ApplyFilter(ServerItem item)
    {
        var text = FilterBox.Text;
        System.Windows.Data.CollectionViewSource.GetDefaultView(item.Log).Filter =
            text.Length == 0 ? null : o => ((LogLine)o).Text.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (text.Length == 0) ScrollConsoleToEnd();
    }

    void ConsoleList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || ConsoleList.SelectedItems.Count == 0) return;
        var lines = ConsoleList.SelectedItems.Cast<LogLine>().OrderBy(l => ConsoleList.Items.IndexOf(l)).Select(l => l.Text);
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        e.Handled = true;
    }

    void OldLogs_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) new OldLogsWindow(Path.Combine(item.Entry.Path, "logs")) { Owner = this }.Show();
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
        if (state == "Running")
        {
            _wasRunning.Add(item);
            if (Geyser.UseFloodgate(item.Entry.Path)) Run(item, "geyser reload"); // first run after installing Geyser
            await QueryRules(item);
        }
        if (state != "Stopped") return;
        SaveConfig(); // playtime of players who were still on
        if (item.Runner.CrashedWhileRunning && !_closing) await OnCrash(item);
        else if (_wasRunning.Remove(item)) Discord.Post(_cfg.DiscordWebhook, $"{item.Entry.Name} stopped.");
    }

    // ---------- start / stop ----------

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item) await StartServer(item);
    }

    async Task StartServer(ServerItem item)
    {
        if (item.Busy) { AppendLog(item, "[MineServers] Wait for the backup or world change to finish, then start the server."); return; }
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
            Geyser.UseFloodgate(item.Entry.Path);
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
            if (item.Entry.Type == "paper" && Geyser.Installed(item.Entry.Path)) AppendLog(item, "[MineServers] " + Geyser.JoinInfo(item.Entry, address));
            Discord.Post(_cfg.DiscordWebhook, $"{item.Entry.Name} is online. Join at {address}");
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
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (AddressBox.Text.Length > 0) Clipboard.SetText(AddressBox.Text);
    }

    async void Reach_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        ReachButton.IsEnabled = false;
        var answer = await Reach.Check(item.Entry, item.Address, item.Runner.State == "Running");
        ReachButton.IsEnabled = true;
        MessageBox.Show(answer, "Can friends join?");
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
        s.RestartOnCrash = RestartOnCrashBox.IsChecked == true;
        s.AutoStart = AutoStartBox.IsChecked == true;
        s.AikarFlags = AikarBox.IsChecked == true;
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

    void ShowIcon(ServerEntry s)
    {
        var file = Path.Combine(s.Path, "server-icon.png");
        if (!File.Exists(file)) { IconImage.Source = null; return; }
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(file);
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file locked
        img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
        img.EndInit();
        IconImage.Source = img;
    }

    void Icon_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var src = new System.Windows.Media.Imaging.BitmapImage();
            src.BeginInit();
            src.UriSource = new Uri(dlg.FileName);
            src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            src.EndInit();
            int side = Math.Min(src.PixelWidth, src.PixelHeight);
            var square = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
            var icon = new System.Windows.Media.Imaging.TransformedBitmap(square, new System.Windows.Media.ScaleTransform(64.0 / side, 64.0 / side));
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(icon));
            using (var fs = File.Create(Path.Combine(item.Entry.Path, "server-icon.png"))) png.Save(fs);
            ShowIcon(item.Entry);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show("Couldn't use that picture:\n\n" + ex.Message, "MineServers");
        }
    }

    async void UpgradeBox_DropDownOpened(object sender, EventArgs e)
    {
        if (UpgradeBox.ItemsSource != null || Selected is not { } item) return;
        try
        {
            var releases = await Downloads.GetReleases();
            // Newest first; worlds can't go back to an older version, so only offer this one and newer.
            int current = releases.FindIndex(r => r.Id == item.Entry.Version);
            if (item == Selected) UpgradeBox.ItemsSource = current >= 0 ? releases.Take(current + 1).ToList() : releases;
        }
        catch (Exception ex) { UpgradeHelp.Text = "Couldn't load versions (are you online?): " + ex.Message; }
    }

    async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (UpgradeBox.SelectedItem is not McVersion v) { MessageBox.Show("Pick a version in the list first.", "MineServers"); return; }
        var s = item.Entry;
        if (item.Runner.State != "Stopped" || item.Busy) { MessageBox.Show("Stop the server first, then try again.", "MineServers"); return; }
        bool same = v.Id == s.Version;
        var addOns = Modrinth.For(s.Type) is { } k ? $"\n\nSome {k.Noun}s may need updating afterwards: {k.Noun[0].ToString().ToUpper()}{k.Noun[1..]}s tab > Check for updates." : "";
        if (MessageBox.Show((same ? $"Get the newest {s.Type} build of {v.Id}?" : $"Upgrade \"{s.Name}\" from {s.Version} to {v.Id}?\n\nOnce the world opens in {v.Id} it can't go back to {s.Version} except by restoring the backup made now.") + addOns,
                "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!await BackUp(item, "before upgrading")) { MessageBox.Show("The backup failed, so nothing was changed. The console says why.", "MineServers"); return; }
        item.Busy = true;
        try
        {
            s.JavaMajor = await Downloads.InstallServerJar(s.Type, v, s.Path, new Progress<string>(m => AppendLog(item, "[MineServers] " + m)));
            s.Version = v.Id;
            s.JarFile = "server.jar";
            s.JavaPath = null; // a newer version may need a newer Java; pick it automatically
            Save();
            AppendLog(item, $"[MineServers] Now on {s.Type} {v.Id}. Start the server to finish the upgrade.");
        }
        catch (Exception ex) { AppendLog(item, "[MineServers] Upgrade failed: " + ex.Message); MessageBox.Show("The upgrade failed:\n\n" + ex.Message, "MineServers"); }
        item.Busy = false;
        item.Refresh();
        if (item == Selected) ShowSelected();
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
        _tray.Dispose();
        // Posted, not called: if nothing above had to wait, we're still inside this Closing event, where Close() throws.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
