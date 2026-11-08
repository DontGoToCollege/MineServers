using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using MineServers.Core;

namespace MineServers;

public partial class NewServerWindow : Window
{
    readonly HashSet<int> _usedPorts;
    public ServerEntry? Created { get; private set; }

    public NewServerWindow(IEnumerable<int> usedPorts)
    {
        InitializeComponent();
        _usedPorts = usedPorts.ToHashSet();
        // 2 GB is enough for a few friends; 4 GB if the PC has room for it.
        var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        RamBox.Text = totalMb >= 12_000 ? "4096" : "2048";
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Loading Minecraft versions...";
        try
        {
            VersionBox.ItemsSource = await Downloads.GetReleases();
            VersionBox.SelectedIndex = 0;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't load versions (are you online?): " + ex.Message;
            CreateButton.IsEnabled = false;
        }
    }

    void Eula_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var type = (string)((ComboBoxItem)TypeBox.SelectedItem).Tag;
        if (name.Length == 0) { StatusText.Text = "Give the server a name."; return; }
        if (VersionBox.SelectedItem is not McVersion version) { StatusText.Text = "Pick a Minecraft version."; return; }
        if (!int.TryParse(RamBox.Text, out var ram) || ram < 512) { StatusText.Text = "Memory must be at least 512 MB."; return; }
        if (EulaBox.IsChecked != true) { StatusText.Text = "Minecraft servers only run after you agree to the EULA."; return; }

        IsEnabled = false;
        var dir = Store.UniqueFolder(Store.ServersDir, name);
        try
        {
            Directory.CreateDirectory(dir);
            var javaMajor = await Downloads.InstallServerJar(type, version, dir, new Progress<string>(m => StatusText.Text = m));
            await File.WriteAllTextAsync(Path.Combine(dir, "eula.txt"), $"# Agreed in MineServers on {DateTime.Now:u}\neula=true\n");
            int port = 25565;
            while (_usedPorts.Contains(port)) port++;
            Created = new ServerEntry
            {
                Name = name, Path = dir, Type = type, Version = version.Id,
                JavaMajor = javaMajor, RamMb = ram, Port = port,
            };
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't create the server: " + ex.Message;
            IsEnabled = true;
            try { Directory.Delete(dir, true); } catch (IOException) { } // only the folder this dialog just made
        }
    }
}
