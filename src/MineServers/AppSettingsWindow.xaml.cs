using System.Windows;
using MineServers.Core;
using Microsoft.Win32;

namespace MineServers;

public partial class AppSettingsWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly AppConfig _cfg;

    public AppSettingsWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        StartupBox.IsChecked = StartsWithWindows;
        TrayBox.IsChecked = cfg.MinimizeToTray;
        JoinsBox.IsChecked = cfg.NotifyJoins;
        WebhookBox.Text = cfg.DiscordWebhook ?? "";
    }

    static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("MineServers") is string v && v == Command;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("MineServers", Command);
            else key.DeleteValue("MineServers", false);
        }
    }

    string? Webhook()
    {
        var url = WebhookBox.Text.Trim();
        if (url.Length == 0) return null;
        if (Discord.IsWebhook(url)) return url;
        MessageBox.Show("That isn't a Discord webhook link. It starts with https://discord.com/api/webhooks/", "MineServers");
        return "";
    }

    void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Webhook() is { Length: > 0 } url) Discord.Post(url, "MineServers is connected to this channel.");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var webhook = Webhook();
        if (webhook == "") return;
        // Only touch the Run entry when the box changed, so a moved exe's old entry isn't rewritten unasked.
        if (StartupBox.IsChecked != StartsWithWindows) StartsWithWindows = StartupBox.IsChecked == true;
        _cfg.MinimizeToTray = TrayBox.IsChecked == true;
        _cfg.NotifyJoins = JoinsBox.IsChecked == true;
        _cfg.DiscordWebhook = webhook;
        DialogResult = true;
    }
}
