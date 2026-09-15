using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// Everything that is a property OF a level rather than of its contents: the five header
/// bytes (CONTRACT §4) and the main entrance / entry settings, which live in their own
/// bank-05 tables rather than in the header.
///
/// The fields are grouped by what a setting DOES, not by which record it packs into — a
/// level's height and its screen count are one decision even though one is the header's and
/// the other Lunar Magic's. The two records are read out at the foot for anyone who wants the
/// bytes. The level's GFX sets are NOT here: they are the Graphics tab's Header button, beside
/// the files they choose.
///
/// Edits are STAGED and only committed on Apply. Every header field forces a full reparse —
/// the tileset drives object dispatch, the palette fields drive every tile cache — which is
/// far too expensive to run on each tick of a control.
/// </summary>
public partial class LevelPropertiesWindow : Window
{
    private LevelHeader header;
    private MainEntrance entry;
    private bool hasHeaderOverride;
    private Services.LevelChoices choices;

    /// <summary>Set on Apply: the staged header (null = unchanged) and entry settings.</summary>
    public LevelHeader? AppliedHeader { get; private set; }
    public MainEntrance? AppliedEntry { get; private set; }
    public bool RevertRequested { get; private set; }

    public LevelPropertiesWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>The choice lists come in already resolved: several are read out of the ROM's
    /// own tables, and this layer holds no ROM (see EditorSession.LevelOptionChoices).</summary>
    public LevelPropertiesWindow(LevelHeader h, MainEntrance e, bool headerOverridden,
                                 Services.LevelChoices c) : this()
    {
        header = h;
        entry = e;
        hasHeaderOverride = headerOverridden;
        choices = c;
        Build();
    }

    // Avalonia's name generator also emits HeaderBytes/EntryBytes, but those are backed by
    // fields that only InitializeComponent() assigns — this window loads its XAML directly, so
    // they stay null. Look the controls up explicitly, under names that do not collide.
    private TextBlock HeaderReadout => this.GetControl<TextBlock>("HeaderBytes");
    private TextBlock EntryReadout => this.GetControl<TextBlock>("EntryBytes");

    private void Build()
    {
        var fields = this.GetControl<StackPanel>("Fields");
        this.GetControl<Button>("RevertBtn").IsEnabled = hasHeaderOverride;

        // Staging helpers: each names the record it writes, so a row below reads as its label,
        // its control and the one field it sets.
        void H(Control c) => fields.Children.Add(c);
        Action<int> SetH(Func<int, LevelHeader> set) => v => { header = set(v); Refresh(); };
        Action<int> SetE(Func<int, MainEntrance> set) => v => { entry = set(v); Refresh(); };
        // A field packed as bits of a wider one: read and write bit `b` of `whole`.
        Action<bool> SetEBit(Func<int> whole, int b, Func<int, MainEntrance> set)
            => on => { entry = set(on ? whole() | 1 << b : whole() & ~(1 << b)); Refresh(); };

        H(Section("Level"));
        H(Choice("Level mode", header.LevelMode, choices.LevelMode, SetH(v => header with { LevelMode = v })));
        H(Number("Screens", header.Screens, 1, 32, SetH(v => header with { Screens = v })));
        // LM's level height: an index into a 32-entry LUT, and W columns of that height have to
        // fit the tilemap — the session refuses a pair that does not, which is why this is the
        // one place the two dimensions are next to each other.
        H(Choice("Height", entry.HeightIndex, choices.Height, SetE(v => entry with { HeightIndex = v })));

        H(Section("Palettes"));
        H(Number("FG palette", header.FgPalette, 0, 7, SetH(v => header with { FgPalette = v })));
        H(Number("BG palette", header.BgPalette, 0, 7, SetH(v => header with { BgPalette = v })));
        H(Number("Sprite palette", header.SpritePalette, 0, 7, SetH(v => header with { SpritePalette = v })));
        H(Number("Back area color", header.BackAreaColor, 0, 7, SetH(v => header with { BackAreaColor = v })));
        // The level's GFX sets used to sit here. They are the Graphics tab's Header button now:
        // a tileset is chosen by the files it loads, which is what that drawer already shows.

        H(Section("Play"));
        H(Choice("Music", header.Music, choices.Music, SetH(v => header with { Music = v })));
        H(Choice("Time", header.Time, choices.Time, SetH(v => header with { Time = v })));
        H(Choice("Item memory", header.ItemMemory, LevelOptions.ItemMemory, SetH(v => header with { ItemMemory = v })));

        H(Section("Scrolling"));
        H(Choice("Layer 1 vertical", header.ScrollSetting, LevelOptions.VerticalScroll,
                 SetH(v => header with { ScrollSetting = v })));
        H(Choice("Layer 2 rate", entry.Layer2Scroll, choices.Layer2Scroll,
                 SetE(v => entry with { Layer2Scroll = v })));
        // Not the background's real height: it positions the BG so the bottom of the image is on
        // screen when the player reaches the bottom of the level (level_change_other.htm).
        H(Number("BG height (tiles)", entry.BgHeight, 0, 63, SetE(v => entry with { BgHeight = v })));
        // Layer 3's two fields — the priority flag and the entrance's layer 3 option — are the
        // Background tab's "Layer 3 Options", where the result is on screen.

        H(Section("Entry"));
        H(Check("Skip the entrance walk", entry.SkipEntranceWalk != 0,
                on => { entry = entry with { SkipEntranceWalk = on ? 1 : 0 }; Refresh(); }));
        // LM's own warning: on in a vertical level, off in a horizontal one, or an entrance
        // lands somewhere that kills the player on arrival (level_change_other.htm).
        H(Check("Vertical entrance positioning", entry.VerticalPositioning != 0,
                on => { entry = entry with { VerticalPositioning = on ? 1 : 0 }; Refresh(); }));
        // $5B's two bits: bit 0 is the level itself, bit 1 the one Nintendo left unused — it was
        // probably meant for a vertical layer 2, and the mixed level modes were never shipped.
        H(Check("Vertical level", (entry.VerticalLevel & 1) != 0,
                SetEBit(() => entry.VerticalLevel, 0, v => entry with { VerticalLevel = v })));
        H(Check("Unknown vertical level", (entry.VerticalLevel & 2) != 0,
                SetEBit(() => entry.VerticalLevel, 1, v => entry with { VerticalLevel = v })));

        H(Section("Sprites"));
        H(Choice("Spawn range", entry.SpriteSpawnRange, LevelOptions.SpriteSpawnRange,
                 SetE(v => entry with { SpriteSpawnRange = v })));
        H(Check("Smart spawn", entry.SmartSpawn != 0,
                on => { entry = entry with { SmartSpawn = on ? 1 : 0 }; Refresh(); }));

        Refresh();
    }

    private void Refresh()
    {
        HeaderReadout.Text = "header bytes " + Convert.ToHexString(header.ToBytes());
        EntryReadout.Text = "entrance bytes " + Convert.ToHexString(entry.ToBytes());
    }

    // ---- rows ----
    //
    // One shape for all three: a fixed label column so the controls line up, then the control.
    // Which control a field gets follows what its values ARE — a list of meanings is a list, a
    // single bit is a checkbox, and a number whose values mean only themselves stays a number.

    private const int LabelWidth = 130;

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("section");
        return t;
    }

    /// <summary>A field whose values have names: the name is the choice, and the value it packs
    /// leads each entry so the ROM byte is still legible.</summary>
    private static Control Choice(string label, int value, IReadOnlyList<string> items, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = items, MinWidth = 320, VerticalAlignment = VerticalAlignment.Center };
        // A level on a base that lacks the engine behind a field (LM's level height, say) can
        // hold a value the list has no entry for. Show it empty rather than silently moving it.
        box.SelectedIndex = value < items.Count ? value : -1;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) set(box.SelectedIndex); };
        return Row(label, box);
    }

    /// <summary>A bounded number that means only itself — a palette index, a screen count.</summary>
    private static Control Number(string label, int value, int min, int max, Action<int> set)
    {
        var box = new NumericUpDown
        {
            Minimum = min, Maximum = max, Value = value, Increment = 1,
            FormatString = "0", Width = 110, VerticalAlignment = VerticalAlignment.Center,
        };
        box.ValueChanged += (_, _) => { if (box.Value is { } d) set((int)d); };
        return Row(label, box);
    }

    /// <summary>A single bit. The box carries its own words and sits in the control column, so
    /// the flags line up with the fields above them.</summary>
    private static Control Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(LabelWidth + 6, 0, 0, 0) };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static Control Row(string label, Control control)
    {
        var name = new TextBlock { Text = label, Width = LabelWidth };
        name.Classes.Add("label");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(name);
        row.Children.Add(control);
        return row;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        AppliedHeader = header;
        AppliedEntry = entry;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        RevertRequested = true;
        Close();
    }
}
