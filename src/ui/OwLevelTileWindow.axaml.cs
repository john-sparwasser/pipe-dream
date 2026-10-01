using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// Lunar Magic's "Modify Level Tile Settings", for one level tile on the overworld: which level
/// it enters, the event passing it fires, and the direction each of its four exits opens. Laid
/// out as LM lays it out, and carrying LM's own two groups that this base cannot hold — shown
/// with the reason rather than left off, so a reader can see what is missing and why.
///
/// Edits are STAGED and applied on OK, as the level properties window stages a header: the
/// tables are per translevel, so a half-applied dialog would leave a level's exits disagreeing
/// with its event.
/// </summary>
public partial class OwLevelTileWindow : Window
{
    private readonly EditorSession.OwLevelTile tile;
    private readonly ComboBox[] dirs = new ComboBox[4];
    private ComboBox eventBox = null!;
    private TextBox levelBox = null!;

    /// <summary>Set on OK: the level number, the base event ($FF none) and the four exit
    /// directions. A level number that is not hex reads as -1, which the session refuses with
    /// the range in its report rather than the dialog growing a validator of its own.</summary>
    public bool Applied { get; private set; }
    public int Level { get; private set; }
    public int BaseEvent { get; private set; }
    public int[] ExitDirs { get; private set; } = [];

    public OwLevelTileWindow() => AvaloniaXamlLoader.Load(this);

    public OwLevelTileWindow(EditorSession.OwLevelTile t) : this()
    {
        tile = t;
        Title = $"Level Tile Settings — ({t.X:X2},{t.Y:X2}){(t.SubmapMap ? " submap" : "")}";
        Build();
    }

    private void Build()
    {
        var fields = this.GetControl<Grid>("Fields");
        int row = 0;
        void Row(string label, Control c, string? why = null)
        {
            fields.Children.Add(Cell(new TextBlock { Text = label, Classes = { "label" }, Margin = new(0, 4) }, 0, row));
            c.Margin = new(0, 3);
            c.IsEnabled = why is null;
            ToolTip.SetTip(c, why);
            fields.Children.Add(Cell(c, 1, row));
            row++;
        }

        // Level number. Editable only where Lunar Magic's per-tile table holds it: on a base of
        // ours the level a tile enters is its place in the game's own scan, not a number the tile
        // carries, so there is nothing to write and it is shown instead.
        levelBox = new TextBox { Text = $"{tile.Level:X3}", IsReadOnly = !tile.LevelEditable };
        Row("Level number to use for this tile:", levelBox,
            tile.LevelEditable ? null
                               : "this base numbers level tiles by their place in the map, so the number follows the tile rather than being set here");

        // Base event: $FF is "no event", then 00-7F.
        eventBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        eventBox.Items.Add("No event");
        for (int e = 0; e < 0x80; e++) eventBox.Items.Add($"{e:X2}");
        eventBox.SelectedIndex = tile.BaseEvent < 0 ? 0 : tile.BaseEvent + 1;
        Row("Base event for when this level is passed:", eventBox);

        string[] exits = ["normal exit", "secret exit 1", "secret exit 2", "secret exit 3"];
        string? dirWhy = tile.DirsEditable ? null
            : "Lunar Magic keeps the directions in a packed block of its own on a ROM it has saved; this base has none we can write";
        for (int i = 0; i < 4; i++)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (string d in Overworld.DirectionNames) box.Items.Add(d);
            box.SelectedIndex = tile.ExitDirs[i];
            dirs[i] = box;
            Row($"Direction to enable when {exits[i]} is used:", box, dirWhy);
        }

        this.GetControl<TextBlock>("RevealNote").Text =
            "Revealing this tile on an event is a layer 1 event, which the Events tab will hold; Lunar Magic keeps it with its own event data.";

        // The states a new game starts the level with, as the ROM has them: each box its bit of
        // the save file's $1EA2 byte (Overworld.InitialFlagsOf).
        var flags = this.GetControl<StackPanel>("Flags");
        foreach (var (f, bit) in new[] { ("Enable up", 3), ("Enable down", 2), ("Enable left", 1), ("Enable right", 0),
                                         ("Level has been passed", 7), ("Midway point obtained", 6),
                                         ("No entry if level passed", 5), ("Save prompt", 4) })
            flags.Children.Add(new CheckBox { Content = f, IsEnabled = false, IsChecked = tile.InitialFlags is { } b && (b >> bit & 1) != 0 });
        this.GetControl<TextBlock>("FlagsNote").Text = tile.InitialFlags is null
            ? "These are the states a new game starts with. This ROM keeps them somewhere we cannot read, so they are left blank."
            : tile.LevelEditable
            ? "These are the states a new game starts with, from the table Lunar Magic added when it saved this overworld. They are shown and not offered."
            : "These are the states a new game starts with, from the game's own short list of eight levels; every other level starts with none. Lunar Magic replaces the list with a table when it saves an overworld, so they are shown and not offered.";
    }

    private static Control Cell(Control c, int col, int row)
    {
        Grid.SetColumn(c, col);
        Grid.SetRow(c, row);
        return c;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Level = int.TryParse(levelBox.Text?.Trim(), System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out int lv) ? lv : -1;
        BaseEvent = eventBox.SelectedIndex <= 0 ? -1 : eventBox.SelectedIndex - 1;
        ExitDirs = [.. dirs.Select(d => Math.Max(0, d.SelectedIndex))];
        Applied = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
