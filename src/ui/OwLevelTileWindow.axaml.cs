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

    /// <summary>Set on OK: the base event ($FF none) and the four exit directions.</summary>
    public bool Applied { get; private set; }
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

        // Level number. On a base of ours the level a tile enters is its place in the game's own
        // scan, not a number the tile carries, so it is shown and not offered.
        Row("Level number to use for this tile:",
            new TextBox { Text = $"{tile.Level:X3}", IsReadOnly = true },
            tile.LevelEditable ? "Lunar Magic's per-tile table holds this; move the tile to move its number"
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

        var flags = this.GetControl<StackPanel>("Flags");
        foreach (string f in new[] { "Enable up", "Enable down", "Enable left", "Enable right",
                                     "Level has been passed", "Midway point obtained",
                                     "No entry if level passed", "Save prompt" })
            flags.Children.Add(new CheckBox { Content = f, IsEnabled = false });
        this.GetControl<TextBlock>("FlagsNote").Text =
            "These are the states a new game starts with. Lunar Magic keeps them in a table it adds when it saves an overworld; this base still uses the game's own short list, so they are shown and not offered.";
    }

    private static Control Cell(Control c, int col, int row)
    {
        Grid.SetColumn(c, col);
        Grid.SetRow(c, row);
        return c;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        BaseEvent = eventBox.SelectedIndex <= 0 ? -1 : eventBox.SelectedIndex - 1;
        ExitDirs = [.. dirs.Select(d => Math.Max(0, d.SelectedIndex))];
        Applied = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
