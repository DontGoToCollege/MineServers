using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MineServers.Core;

namespace MineServers.Tabs;

public partial class FilesTab : UserControl
{
    static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".properties", ".yml", ".yaml", ".json", ".json5", ".txt", ".toml", ".conf", ".cfg", ".ini", ".log", ".md", ".mcmeta", ".csv", ".sk" };

    record Entry(string Path, bool IsDir, long Size)
    {
        public override string ToString() => IsDir
            ? $"[{System.IO.Path.GetFileName(Path)}]"
            : $"{System.IO.Path.GetFileName(Path),-50} {(Size < 1024 ? $"{Size} B" : Size < 1048576 ? $"{Size / 1024} KB" : $"{Size / 1048576.0:0.0} MB"),10}";
    }

    ServerItem? _item;
    string _dir = "";
    string? _editing;

    public FilesTab() => InitializeComponent();

    public void Show(ServerItem item)
    {
        if (_item != item)
        {
            _item = item;
            _dir = item.Entry.Path;
            CloseEditor();
        }
        RunningWarning.Visibility = item.Runner.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        if (_editing == null) List();
    }

    void List()
    {
        if (_item == null) return;
        if (!Directory.Exists(_dir)) _dir = _item.Entry.Path;
        PathText.Text = Path.GetRelativePath(Path.GetDirectoryName(_item.Entry.Path)!, _dir);
        try
        {
            var di = new DirectoryInfo(_dir);
            EntryList.ItemsSource = di.GetDirectories().OrderBy(d => d.Name).Select(d => new Entry(d.FullName, true, 0))
                .Concat(di.GetFiles().OrderBy(f => f.Name).Select(f => new Entry(f.FullName, false, f.Length))).ToList();
        }
        catch (UnauthorizedAccessException) { EntryList.ItemsSource = null; }
    }

    // Stay inside the server folder.
    bool Inside(string path) => _item != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(_item.Entry.Path), StringComparison.OrdinalIgnoreCase);

    void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_dir);
        if (parent != null && Inside(parent) && _item != null && _dir.Length > _item.Entry.Path.Length) { _dir = parent; List(); }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => List();

    void Explorer_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_dir) { UseShellExecute = true });

    void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelected();
        else if (e.Key == Key.Back) Up_Click(sender, e);
    }

    void OpenSelected()
    {
        if (EntryList.SelectedItem is not Entry entry) return;
        if (entry.IsDir) { _dir = entry.Path; List(); return; }
        if (TextTypes.Contains(Path.GetExtension(entry.Path)) && entry.Size < 5 * 1048576)
        {
            try { EditorBox.Text = SharedFile.ReadAllText(entry.Path); }
            catch (IOException ex) { MessageBox.Show("Couldn't open it:\n\n" + ex.Message, "MineServers"); return; }
            _editing = entry.Path;
            EditingText.Text = Path.GetFileName(entry.Path);
            Browser.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
        }
        else Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        try
        {
            SharedFile.WriteAllText(_editing, EditorBox.Text);
            EditingText.Text = Path.GetFileName(_editing) + "  (saved)";
        }
        catch (IOException ex) { MessageBox.Show("Couldn't save it:\n\n" + ex.Message, "MineServers"); }
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseEditor();
        List();
    }

    void CloseEditor()
    {
        _editing = null;
        EditorBox.Clear();
        EditorPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
    }
}
