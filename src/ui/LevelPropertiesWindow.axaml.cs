using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// Everything that is a property OF a level rather than of its contents: the five header
/// bytes (CONTRACT §4) and the main entrance / entry settings, which live in their own
/// bank-05 tables rather than in the header.
///
/// Edits are STAGED and only committed on Apply. Every header field forces a full reparse —
/// the tileset drives object dispatch, the palette fields drive every tile cache — which is
/// far too expensive to run on each tick of a slider.
/// </summary>
public partial class LevelPropertiesWindow : Window
{
    private LevelHeader header;
    private MainEntrance entry;
    private bool hasHeaderOverride;

    /// <summary>Set on Apply: the staged header (null = unchanged) and entry settings.</summary>
    public LevelHeader? AppliedHeader { get; private set; }
    public MainEntrance? AppliedEntry { get; private set; }
    public bool RevertRequested { get; private set; }

    public LevelPropertiesWindow() => AvaloniaXamlLoader.Load(this);

    public LevelPropertiesWindow(LevelHeader h, MainEntrance e, bool headerOverridden) : this()
    {
        header = h;
        entry = e;
        hasHeaderOverride = headerOverridden;
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

        // Insert header fields after the "Level header" caption + byte readout (indices 0,1).
        int at = 2;
        void H(string label, int value, int min, int max, Func<int, LevelHeader> set)
            => fields.Children.Insert(at++, Row(label, value, min, max, v => { header = set(v); Refresh(); }));

        H("Screens", header.Screens, 1, 32, v => header with { Screens = v });
        H("Level mode", header.LevelMode, 0, 31, v => header with { LevelMode = v });
        H("Tileset", header.Tileset, 0, 15, v => header with { Tileset = v });
        H("Sprite set", header.SpriteSet, 0, 15, v => header with { SpriteSet = v });
        H("FG palette", header.FgPalette, 0, 7, v => header with { FgPalette = v });
        H("BG palette", header.BgPalette, 0, 7, v => header with { BgPalette = v });
        H("Sprite palette", header.SpritePalette, 0, 7, v => header with { SpritePalette = v });
        H("Back area color", header.BackAreaColor, 0, 7, v => header with { BackAreaColor = v });
        H("Music", header.Music, 0, 7, v => header with { Music = v });
        H("Time", header.Time, 0, 3, v => header with { Time = v });
        H("Item memory", header.ItemMemory, 0, 3, v => header with { ItemMemory = v });
        H("Vertical scroll", header.ScrollSetting, 0, 3, v => header with { ScrollSetting = v });
        H("Layer 3 priority", header.Layer3Priority, 0, 1, v => header with { Layer3Priority = v });

        // The spawn position only applies when the level is entered from the overworld — a
        // secondary exit places Mario itself — but the vertical and entrance-walk bits always do.
        void E(string label, int value, int min, int max, Func<int, MainEntrance> set)
            => fields.Children.Add(Row(label, value, min, max, v => { entry = set(v); Refresh(); }));

        E("Layer 2 scroll", entry.Layer2Scroll, 0, 15, v => entry with { Layer2Scroll = v });
        E("Layer 2 BG setting", entry.Layer2Setting, 0, 3, v => entry with { Layer2Setting = v });
        E("Vertical level", entry.VerticalLevel, 0, 3, v => entry with { VerticalLevel = v });
        E("Skip entrance walk", entry.SkipEntranceWalk, 0, 1, v => entry with { SkipEntranceWalk = v });
        // Where Mario appears and how he arrives (action, slippery, water, face left, camera) is the
        // entrance dialog off the canvas marker; what is left here is level-wide.
        E("BG height (tiles)", entry.BgHeight, 0, 63, v => entry with { BgHeight = v });
        // Lunar Magic's level height: index into a 32-entry table of heights (0 = vanilla's 27 rows,
        // 0x1C = one 896-row column); the "Screens" row above is the width, and width x height has
        // to fit the tilemap — the session refuses a pair that does not.
        E("Level height (LM index)", entry.HeightIndex, 0, 31, v => entry with { HeightIndex = v });
        E("Sprite spawn range", entry.SpriteSpawnRange, 0, 3, v => entry with { SpriteSpawnRange = v });
        E("Smart sprite spawn", entry.SmartSpawn, 0, 1, v => entry with { SmartSpawn = v });
        E("Vertical positioning", entry.VerticalPositioning, 0, 1, v => entry with { VerticalPositioning = v });

        Refresh();
    }

    private void Refresh()
    {
        HeaderReadout.Text = "header bytes " + Convert.ToHexString(header.ToBytes());
        EntryReadout.Text = "entrance bytes " + Convert.ToHexString(entry.ToBytes());
    }

    /// <summary>A labelled slider with a live value, which is how these fields read: they are
    /// small bounded integers whose meaning is in the ROM, not in a friendly name.</summary>
    private static Control Row(string label, int value, int min, int max, Action<int> set)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, Width = 210,
            TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = value.ToString(), Width = 34, VerticalAlignment = VerticalAlignment.Center,
        };
        readout.Classes.Add("mono");
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            int v = (int)Math.Round(slider.Value);
            readout.Text = v.ToString();
            set(v);
        };

        var name = new TextBlock { Text = label, Width = 130 };
        name.Classes.Add("label");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(readout);
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
