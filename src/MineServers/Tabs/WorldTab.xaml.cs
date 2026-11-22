using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MineServers.Core;
using Microsoft.Win32;

namespace MineServers.Tabs;

public partial class WorldTab : UserControl
{
    ServerItem? _item;

    public WorldTab() => InitializeComponent();

    MainWindow Host => (MainWindow)Window.GetWindow(this);

    public void Show(ServerItem item)
    {
        if (_item != item) StatusText.Text = "";
        _item = item;
        BackupList.ItemsSource = Backups.List(item.Entry);
        KeepBox.Text = item.Entry.BackupKeep.ToString();
        bool stopped = item.Runner.State == "Stopped" && !item.Busy;
        StoppedNote.Visibility = item.Runner.State == "Stopped" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in new[] { ZipButton, FolderButton, NetherButton, EndButton, NewWorldButton }) b.IsEnabled = stopped;
        BackupButton.IsEnabled = !item.Busy;
    }

    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        StatusText.Text = "Backing up...";
        BackupButton.IsEnabled = false;
        StatusText.Text = await Host.BackUp(item) ? "Backed up." : "The backup failed; the console says why.";
        if (_item == item) Show(item);
    }

    void KeepBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        if (int.TryParse(KeepBox.Text, out var n) && n >= 1) { _item.Entry.BackupKeep = n; Host.SaveConfig(); }
        else KeepBox.Text = _item.Entry.BackupKeep.ToString();
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) Process.Start(new ProcessStartInfo(Backups.DirFor(_item.Entry)) { UseShellExecute = true });
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || BackupList.SelectedItem is not BackupInfo b) return;
        if (MessageBox.Show($"Delete the backup from {b}?\n\nThis can't be undone.", "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { File.Delete(b.Path); }
        catch (IOException ex) { MessageBox.Show("Couldn't delete it:\n\n" + ex.Message, "MineServers"); }
        Show(_item);
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { } item) return;
        var dlg = new SaveFileDialog { FileName = $"{item.Entry.Name} world.zip", Filter = "Zip file|*.zip" };
        if (dlg.ShowDialog() != true) return;
        StatusText.Text = "Zipping the world...";
        try
        {
            var skipped = await Host.WithWorldSaved(item, () => Backups.ZipWorld(item.Entry, dlg.FileName));
            StatusText.Text = $"Saved {Path.GetFileName(dlg.FileName)}." + (skipped.Count > 0 ? $" {skipped.Count} file(s) were in use and left out." : "");
        }
        catch (Exception ex) { StatusText.Text = "Couldn't save the world: " + ex.Message; }
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupInfo b) { MessageBox.Show("Pick a backup in the list first.", "MineServers"); return; }
        _ = Change($"Put back the backup from {b}?\n\nThe world goes back to how it was then.", "before restore",
            s => Backups.Restore(s, b.Path), "Restored the backup.");
    }

    void ReplaceZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip file|*.zip", Title = "Pick a zipped Minecraft world" };
        if (dlg.ShowDialog() != true) return;
        _ = Change($"Replace this server's world with the one in {Path.GetFileName(dlg.FileName)}?", "before replacing the world",
            s => Backups.ReplaceWorld(s, dlg.FileName), "The world was replaced. Start the server to play it.");
    }

    void ReplaceFolder_Click(object sender, RoutedEventArgs e)
    {
        var saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "saves");
        var dlg = new OpenFolderDialog { Title = "Pick a Minecraft world folder", InitialDirectory = Directory.Exists(saves) ? saves : "" };
        if (dlg.ShowDialog() != true) return;
        _ = Change($"Replace this server's world with \"{Path.GetFileName(dlg.FolderName)}\"?\n\nThe folder you picked is copied, not changed.",
            "before replacing the world", s => Backups.ReplaceWorld(s, dlg.FolderName), "The world was replaced. Start the server to play it.");
    }

    void ResetNether_Click(object sender, RoutedEventArgs e) => ResetDimension("the_nether", "Nether");
    void ResetEnd_Click(object sender, RoutedEventArgs e) => ResetDimension("the_end", "End");

    void ResetDimension(string id, string name) =>
        _ = Change($"Reset the {name}? Everything built there is removed and it's made fresh the next time the server starts.\n\nPlayers who are in the {name} appear at spawn.",
            $"before resetting the {name}", s => Backups.ResetDimension(s, id), $"The {name} will be made fresh when the server starts.");

    void NewWorld_Click(object sender, RoutedEventArgs e)
    {
        var seed = SeedBox.Text.Trim();
        _ = Change("Start a new world" + (seed.Length > 0 ? $" with the seed {seed}" : " with a random seed")
                   + "?\n\nThe current world is removed (it stays in the backup).", "before a new world",
            s => Backups.NewWorld(s, seed), "A new world will be made when the server starts.");
    }

    /// <summary>Backs up, then changes the world. Only while the server is stopped.</summary>
    async Task Change(string question, string label, Action<ServerEntry> change, string done)
    {
        if (_item is not { } item) return;
        if (item.Runner.State != "Stopped" || item.Busy) { MessageBox.Show("Stop the server first, then try again.", "MineServers"); return; }
        if (MessageBox.Show(question, "MineServers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StatusText.Text = "Making a backup first...";
        if (!await Host.BackUp(item, label)) { StatusText.Text = "The backup failed, so nothing was changed. The console says why."; return; }
        item.Busy = true;
        Show(item);
        StatusText.Text = "Working...";
        try
        {
            await Task.Run(() => change(item.Entry));
            StatusText.Text = done;
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            MessageBox.Show($"That didn't work:\n\n{ex.Message}\n\nThe backup made just before (\"{label}\") is in the list if anything needs putting back.", "MineServers");
        }
        item.Busy = false;
        if (_item == item) Show(item);
    }
}
