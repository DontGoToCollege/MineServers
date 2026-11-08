using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MineServers.Core;

namespace MineServers.Tabs;

public partial class SchedulerTab : UserControl
{
    ServerItem? _item;

    public SchedulerTab()
    {
        InitializeComponent();
        DayBox.ItemsSource = Enum.GetValues<DayOfWeek>();
        TemplateBox.ItemsSource = Scheduler.Templates.Select(t => t.Name).Prepend("Add a ready-made task...").ToList();
        TemplateBox.SelectedIndex = 0;
    }

    MainWindow Host => (MainWindow)Window.GetWindow(this);
    ScheduledTask? Current => TaskList.SelectedItem as ScheduledTask;

    public void Show(ServerItem item)
    {
        _item = item;
        var keep = Current?.Id;
        TaskList.ItemsSource = null;
        TaskList.ItemsSource = item.Entry.Tasks;
        TaskList.SelectedItem = item.Entry.Tasks.FirstOrDefault(t => t.Id == keep) ?? item.Entry.Tasks.FirstOrDefault();
        LoadEditor();
    }

    void LoadEditor()
    {
        var t = Current;
        EditorScroll.Visibility = t != null ? Visibility.Visible : Visibility.Collapsed;
        NoTaskText.Visibility = t != null ? Visibility.Collapsed : Visibility.Visible;
        if (t == null) return;
        NameBox.Text = t.Name;
        EnabledBox.IsChecked = t.Enabled;
        KindBox.SelectedItem = KindBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == t.Kind);
        IntervalBox.Text = t.IntervalMinutes.ToString();
        TimeBox.Text = t.Time;
        DayBox.SelectedItem = t.Day;
        CommandsBox.Text = t.Commands;
        LastRunText.Text = t.LastRun is { } last ? $"Last ran {last:g}." : "Hasn't run yet.";
    }

    void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadEditor();

    void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string;
        IntervalPanel.Visibility = kind == "interval" ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = kind == "interval" ? Visibility.Collapsed : Visibility.Visible;
        DayBox.Visibility = kind == "weekly" ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddTask(ScheduledTask t)
    {
        if (_item == null) return;
        _item.Entry.Tasks.Add(t);
        Host.SaveConfig();
        Show(_item);
        TaskList.SelectedItem = t;
    }

    void New_Click(object sender, RoutedEventArgs e) => AddTask(new ScheduledTask());

    void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedIndex <= 0) return;
        var template = Scheduler.Templates[TemplateBox.SelectedIndex - 1].Task;
        var copy = JsonSerializer.Deserialize<ScheduledTask>(JsonSerializer.Serialize(template))!;
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.CreatedAt = DateTime.Now;
        TemplateBox.SelectedIndex = 0;
        AddTask(copy);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || Current is not { } t) return;
        var kind = (string)((ComboBoxItem)KindBox.SelectedItem).Tag;
        if (kind == "interval" && (!int.TryParse(IntervalBox.Text, out var minutes) || minutes < 1))
        {
            MessageBox.Show("Type how many minutes between runs (1 or more).", "MineServers");
            return;
        }
        if (kind != "interval" && !TimeSpan.TryParse(TimeBox.Text, out _))
        {
            MessageBox.Show("Type the time as hours:minutes, for example 04:00 or 21:30.", "MineServers");
            return;
        }
        bool scheduleChanged = t.Kind != kind || t.IntervalMinutes.ToString() != IntervalBox.Text;
        t.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Task";
        t.Enabled = EnabledBox.IsChecked == true;
        t.Kind = kind;
        if (int.TryParse(IntervalBox.Text, out var m)) t.IntervalMinutes = m;
        t.Time = TimeBox.Text.Trim();
        t.Day = DayBox.SelectedItem is DayOfWeek d ? d : DayOfWeek.Sunday;
        t.Commands = CommandsBox.Text;
        if (scheduleChanged) t.CreatedAt = DateTime.Now; // count a new interval from now
        Host.SaveConfig();
        Show(_item);
    }

    async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null && Current is { } t) await Host.RunTask(_item, t);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || Current is not { } t) return;
        if (MessageBox.Show($"Delete the task \"{t.Name}\"?", "MineServers", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _item.Entry.Tasks.Remove(t);
        Host.SaveConfig();
        Show(_item);
    }
}
