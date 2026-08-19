using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// The level's screen exits, staged as a table and applied in one go — so a session of retyping
/// destinations costs one undo step rather than one per keystroke.
///
/// Rows are built in code rather than bound. Ten near-identical composites with per-row enable
/// rules (the LM exit form has no water or secondary flag of its own) is less machinery this way
/// than a template plus a view model per row.
/// </summary>
public partial class LevelExitsWindow : Window
{
    /// <summary>The staged table, when Apply was pressed; null when cancelled.</summary>
    public List<LevelExit>? Applied { get; private set; }

    /// <summary>Set when the user asked to go to a secondary entrance record instead — the
    /// staged table is applied first, so nothing typed is lost on the way.</summary>
    public int? OpenEntrance { get; private set; }

    private readonly List<(LevelExit Exit, TextBox Screen, TextBox Dest, CheckBox Water, CheckBox Secondary)> rows = [];
    private StackPanel host = null!;

    public LevelExitsWindow() => AvaloniaXamlLoader.Load(this);

    public LevelExitsWindow(IEnumerable<LevelExit> exits) : this()
    {
        host = this.GetControl<StackPanel>("Rows");
        foreach (var e in exits) AddRow(e);
        if (rows.Count == 0) ShowEmptyNote();
    }

    private void ShowEmptyNote()
        => host.Children.Add(new TextBlock
        {
            Text = "This level has no screen exits.",
            Classes = { "dim" },
            Margin = new Thickness(0, 6, 0, 0),
        });

    private void AddRow(LevelExit e)
    {
        // A vanilla exit's destination is one byte; the LM form's word is two.
        var screen = new TextBox { Text = $"{e.Screen:X2}", Width = 60 };
        var dest = new TextBox { Text = e.LmForm ? $"{e.Destination:X4}" : $"{e.Destination:X2}", Width = 80 };
        var water = new CheckBox { IsChecked = e.Water, IsEnabled = !e.LmForm };
        var secondary = new CheckBox { IsChecked = e.Secondary, IsEnabled = !e.LmForm };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("70,90,60,80,80,*"),
        };
        void Put(Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }
        Put(screen, 0);
        Put(dest, 1);
        Put(water, 2);
        Put(secondary, 3);
        Put(new TextBlock { Text = e.Kind, Classes = { "dim" } }, 4);

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
        };
        // A secondary exit's destination IS an entrance index, so offer the record it points at
        // rather than making the user find it by number.
        var entrance = new Button { Content = "Entrance…", Padding = new Thickness(8, 1) };
        entrance.Click += (_, _) =>
        {
            OpenEntrance = Parse(dest, e.LmForm ? 0xFFFF : 0xFF);
            OnApply(this, new RoutedEventArgs());
        };
        entrance.IsVisible = e.Secondary && !e.LmForm;
        secondary.IsCheckedChanged += (_, _) => entrance.IsVisible = secondary.IsChecked == true && !e.LmForm;

        var remove = new Button { Content = "Remove", Padding = new Thickness(8, 1) };
        remove.Click += (_, _) =>
        {
            rows.RemoveAll(r => ReferenceEquals(r.Exit, e));
            host.Children.Remove(grid);
            if (rows.Count == 0) ShowEmptyNote();
        };
        actions.Children.Add(entrance);
        actions.Children.Add(remove);
        Put(actions, 5);

        host.Children.Add(grid);
        rows.Add((e, screen, dest, water, secondary));
    }

    private static int Parse(TextBox box, int max)
        => int.TryParse(box.Text, System.Globalization.NumberStyles.HexNumber, null, out int v)
            ? Math.Clamp(v, 0, max) : 0;

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (rows.Count == 0) host.Children.Clear();       // drop the "no exits" note
        // A new exit lands on the screen after the last one, which is the usual shape of a
        // level's exit table.
        int screen = rows.Count == 0 ? 0 : Parse(rows[^1].Screen, 0x1F) + 1;
        AddRow(new LevelExit { Screen = Math.Min(screen, 0x1F) });
    }

    private void OnEntrances(object? sender, RoutedEventArgs e)
    {
        // A way into the entrance records even for a level with no secondary exit to hang the
        // per-row button off.
        var first = rows.FirstOrDefault(r => r.Secondary.IsChecked == true && !r.Exit.LmForm);
        OpenEntrance = first.Dest is null ? 0 : Parse(first.Dest, 0xFF);
        OnApply(this, e);
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        Applied = rows.Select(r =>
        {
            r.Exit.Screen = Parse(r.Screen, 0x1F);
            r.Exit.Destination = Parse(r.Dest, r.Exit.LmForm ? 0xFFFF : 0xFF);
            r.Exit.Water = r.Water.IsChecked == true;
            r.Exit.Secondary = r.Secondary.IsChecked == true;
            return r.Exit;
        }).ToList();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
