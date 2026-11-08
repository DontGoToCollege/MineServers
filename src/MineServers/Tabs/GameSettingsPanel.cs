using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MineServers.Core;

namespace MineServers.Tabs;

/// <summary>server.properties as friendly controls, plus live game rules. Built from GameSettings so each version shows only what it has.</summary>
public class GameSettingsPanel : StackPanel
{
    ServerItem? _item;
    Dictionary<string, string> _shown = new();
    readonly Dictionary<string, Func<string>> _readers = new();
    readonly StackPanel _props = new(), _rules = new();

    public GameSettingsPanel()
    {
        Children.Add(_props);
        Children.Add(_rules);
    }

    MainWindow Host => (MainWindow)Window.GetWindow(this);

    public void Show(ServerItem item)
    {
        _item = item;
        _readers.Clear();
        _props.Children.Clear();
        Dictionary<string, string> file;
        try { file = ServerProperties.ReadAll(Path.Combine(item.Entry.Path, "server.properties")); }
        catch (IOException) { file = new(); }
        _shown = file.Concat(item.Entry.PendingProperties).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);

        if (_shown.Count < 5)
            _props.Children.Add(Help("Start the server once to see all its game settings. (The server writes them the first time it runs.)"));
        if (item.Entry.PendingProperties.Count > 0)
            _props.Children.Add(Help($"{item.Entry.PendingProperties.Count} change(s) are waiting for the next restart.", "#B45309"));

        foreach (var (group, defs) in GameSettings.Properties)
        {
            var present = defs.Where(d => _shown.ContainsKey(d.Key)).ToList();
            if (present.Count == 0) continue;
            _props.Children.Add(Heading(group));
            foreach (var d in present) _props.Children.Add(PropertyRow(d, _shown[d.Key]));
        }
        ShowRules(item);
    }

    /// <summary>Redraws only the game rules, so values the server reports don't wipe unsaved settings above.</summary>
    public void ShowRules(ServerItem item)
    {
        if (item != _item) return;
        var rules = _rules.Children;
        rules.Clear();
        rules.Add(Heading("Game rules (change instantly while the server runs)"));
        if (!item.Runner.IsRunning || item.SnakeRules == null)
            rules.Add(Help(item.Runner.IsRunning ? "Reading the game rules from the server..." : "Start the server to see and change game rules."));
        else
            foreach (var rule in GameSettings.Rules)
                if (rule.NameFor(item.SnakeRules.Value) is { } name && item.Rules.TryGetValue(name, out var value))
                    rules.Add(RuleRow(rule, name, value));
    }

    /// <summary>The settings the owner changed in this panel.</summary>
    public Dictionary<string, string> Changes() =>
        _readers.Select(r => (r.Key, Value: r.Value())).Where(r => _shown.GetValueOrDefault(r.Key) != r.Value)
            .ToDictionary(r => r.Key, r => r.Value);

    UIElement PropertyRow(PropertyDef d, string value)
    {
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        switch (d.Kind)
        {
            case "bool":
                var cb = new CheckBox { Content = d.Label, IsChecked = value == "true" };
                _readers[d.Key] = () => cb.IsChecked == true ? "true" : "false";
                row.Children.Add(cb);
                break;
            case "choice":
                row.Children.Add(new TextBlock { Text = d.Label });
                var options = d.Options!.Contains(value) ? d.Options : d.Options!.Append(value).ToArray();
                var combo = new ComboBox { ItemsSource = options, SelectedItem = value, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
                _readers[d.Key] = () => combo.SelectedItem as string ?? value;
                row.Children.Add(combo);
                break;
            default: // int and text
                row.Children.Add(new TextBlock { Text = d.Label });
                var box = new TextBox { Text = value, Width = d.Kind == "int" ? 120 : 400, HorizontalAlignment = HorizontalAlignment.Left };
                _readers[d.Key] = () => d.Kind == "int" && int.TryParse(box.Text.Trim(), out var n) ? Math.Clamp(n, d.Min, d.Max).ToString() : d.Kind == "int" ? value : box.Text;
                row.Children.Add(box);
                break;
        }
        if (d.Help != null) row.Children.Add(Help(d.Help));
        return row;
    }

    UIElement RuleRow(RuleDef rule, string name, string value)
    {
        if (!rule.IsInt && GameSettings.IsBool(value))
        {
            var cb = new CheckBox { Content = rule.Label, IsChecked = value == "true", Margin = new Thickness(0, 6, 0, 0) };
            cb.Click += (_, _) => SetRule(name, cb.IsChecked == true ? "true" : "false");
            return cb;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var box = new TextBox { Text = value, Width = 70 };
        var apply = new Button { Content = "Set", Margin = new Thickness(6, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (int.TryParse(box.Text.Trim(), out var n)) SetRule(name, n.ToString());
            else MessageBox.Show("Type a whole number.", "MineServers");
        };
        row.Children.Add(new TextBlock { Text = rule.Label + "  ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        row.Children.Add(apply);
        return row;
    }

    void SetRule(string name, string value)
    {
        if (_item == null) return;
        Host.Run(_item, $"gamerule {name} {value}");
    }

    static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 18, 0, 2) };

    static TextBlock Help(string text, string color = "#555") => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
        Foreground = (Brush)new BrushConverter().ConvertFrom(color)!,
    };
}
