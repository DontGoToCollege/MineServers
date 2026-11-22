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
                if (d.Key == "motd") AddMotdTools(row, box);
                break;
        }
        if (d.Help != null) row.Children.Add(Help(d.Help));
        return row;
    }

    // Minecraft's 16 chat colours, by code.
    const string ColorCodes = "0123456789abcdef";
    static readonly string[] ColorHex =
    {
        "#000000", "#0000AA", "#00AA00", "#00AAAA", "#AA0000", "#AA00AA", "#FFAA00", "#AAAAAA",
        "#555555", "#5555FF", "#55FF55", "#55FFFF", "#FF5555", "#FF55FF", "#FFFF55", "#FFFFFF",
    };

    /// <summary>Colour buttons that insert § codes, and a preview of how the message looks in the server list.</summary>
    static void AddMotdTools(StackPanel row, TextBox box)
    {
        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < 16; i++)
        {
            var code = "§" + ColorCodes[i];
            var b = new Button { Width = 22, Height = 22, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0),
                Background = (Brush)new BrushConverter().ConvertFrom(ColorHex[i])!, ToolTip = "Text after this is this colour" };
            b.Click += (_, _) => Insert(box, code);
            swatches.Children.Add(b);
        }
        foreach (var (code, label, tip) in new[] { ("§l", "B", "Bold"), ("§o", "I", "Italic"), ("§n", "U", "Underline"), ("§r", "Reset", "Back to plain text") })
        {
            var b = new Button { Content = label, MinWidth = 0, Padding = new Thickness(6, 0, 6, 0), Height = 22, Margin = new Thickness(0, 0, 3, 0), ToolTip = tip };
            b.Click += (_, _) => Insert(box, code);
            swatches.Children.Add(b);
        }
        var preview = new TextBlock { Padding = new Thickness(8, 6, 8, 6), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
        box.TextChanged += (_, _) => RenderMotd(preview, box.Text);
        RenderMotd(preview, box.Text);
        row.Children.Add(swatches);
        row.Children.Add(new Border { Background = Brushes.Black, Width = 400, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Child = preview });
    }

    static void Insert(TextBox box, string code)
    {
        var at = box.CaretIndex;
        box.Text = box.Text.Insert(at, code);
        box.CaretIndex = at + code.Length;
        box.Focus();
    }

    static void RenderMotd(TextBlock preview, string text)
    {
        preview.Inlines.Clear();
        Brush color = (Brush)new BrushConverter().ConvertFrom("#AAAAAA")!; // the server list shows plain text in grey
        bool bold = false, italic = false, underline = false;
        var parts = text.Split('§');
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i > 0 && part.Length > 0)
            {
                var c = char.ToLowerInvariant(part[0]);
                part = part[1..];
                if (ColorCodes.IndexOf(c) is var k and >= 0) { color = (Brush)new BrushConverter().ConvertFrom(ColorHex[k])!; bold = italic = underline = false; }
                else if (c == 'l') bold = true;
                else if (c == 'o') italic = true;
                else if (c == 'n') underline = true;
                else if (c == 'r') { color = (Brush)new BrushConverter().ConvertFrom("#AAAAAA")!; bold = italic = underline = false; }
            }
            if (part.Length == 0) continue;
            var run = new System.Windows.Documents.Run(part) { Foreground = color };
            if (bold) run.FontWeight = FontWeights.Bold;
            if (italic) run.FontStyle = FontStyles.Italic;
            if (underline) run.TextDecorations = TextDecorations.Underline;
            preview.Inlines.Add(run);
        }
        if (preview.Inlines.Count == 0) preview.Inlines.Add(new System.Windows.Documents.Run(" "));
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
