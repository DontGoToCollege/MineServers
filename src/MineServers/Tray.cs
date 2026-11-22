using System.Windows;
using Forms = System.Windows.Forms;

namespace MineServers;

/// <summary>MineServers' icon by the clock: pops up alerts, and brings the window back when it's hidden there.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Window _window;

    public Tray(Window window, Action exit)
    {
        _window = window;
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "MineServers",
            ContextMenuStrip = new Forms.ContextMenuStrip(),
            Visible = true,
        };
        _icon.ContextMenuStrip.Items.Add("Open MineServers", null, (_, _) => ShowWindow());
        _icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => exit());
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
