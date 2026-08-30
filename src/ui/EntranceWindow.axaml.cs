using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// Lunar Magic's "Modify Main and Midway Entrance", one entrance at a time: what Mario does on
/// arrival, the level-wide flags that ride on the entrance record (slippery, water), which way he
/// faces, and where the camera starts. Position is the canvas's job — drag Mario.
///
/// The record behind it is the level's <see cref="MainEntrance"/>: the main entrance's own fields,
/// or the separate-midway fields LM adds (CONTRACT §9d-3). Vanilla folds slippery and water into
/// the action list (action 5 = none + slippery, 7 = pipe down + water, $00A6D5); LM adds them as
/// bits 7/6 of $192A via the action's high bits ($05DD00), and face-left as bit 6 of the FG/BG
/// byte (block A: BIT $13CD → STZ $76).
/// </summary>
public partial class EntranceWindow : Window
{
    /// <summary>The record to write, when Apply was pressed.</summary>
    public MainEntrance? Applied { get; private set; }

    /// <summary>$192A values 0-7, as LM names them. 5 and 7 are vanilla's way of setting the
    /// level flags; with LM's bits the plain action plus a checkbox says the same thing.</summary>
    public static readonly string[] Actions =
    [
        "None", "Vertical pipe exit up", "Vertical pipe exit down", "Horizontal pipe exit left",
        "Horizontal pipe exit right", "None (slippery level)", "Shoot from slanted pipe right",
        "Vertical pipe exit down (water level)",
    ];
    private static readonly string[] FgPositions = ["Top (00)", "Middle (60)", "Bottom (C0)", "Top (00)"];
    private static readonly string[] BgPositions = ["Middle (60)", "Low (90)", "Bottom (C0)", "Top (00)"];

    private MainEntrance entry;
    private readonly bool midway;

    public EntranceWindow() => AvaloniaXamlLoader.Load(this);

    public EntranceWindow(MainEntrance e, EntranceKind kind, bool separateMidwaySupported) : this()
    {
        entry = e;
        midway = kind == EntranceKind.Midway;
        Title = midway ? "Midway entrance" : "Main entrance";
        var fields = this.GetControl<StackPanel>("Fields");
        var note = this.GetControl<TextBlock>("Note");

        if (midway && !separateMidwaySupported)
        {
            note.Text = "This base has no separate midway settings (prep v10 installs them); the midway uses the main entrance's action and camera settings.";
            Refresh();
            return;
        }
        note.Text = midway
            ? "With separate settings off the midway borrows the main entrance's action and camera; on, the fields below are its own."
            : "Where Mario appears is set by dragging him on the canvas. Everything else about arriving is here.";

        if (midway)
            fields.Children.Add(Check("Separate midway settings", () => entry.MidwaySeparate != 0,
                                      v => entry = entry with { MidwaySeparate = v ? 1 : 0 }));

        Section(fields, "Mario");
        fields.Children.Add(Combo("Action", Actions, () => Action, v => Action = v));
        fields.Children.Add(Check("Slippery level", () => (ActionHigh & 2) != 0, v => ActionHigh = (ActionHigh & 1) | (v ? 2 : 0)));
        fields.Children.Add(Check("Water level", () => (ActionHigh & 1) != 0, v => ActionHigh = (ActionHigh & 2) | (v ? 1 : 0)));
        fields.Children.Add(Check("Face left", () => (FgBg & 0x40) != 0, v => FgBg = (FgBg & 0xBF) | (v ? 0x40 : 0)));

        Section(fields, "Camera");
        fields.Children.Add(Check("Set FG/BG relative to player", () => (FgBg & 0x80) != 0, v => FgBg = (FgBg & 0x7F) | (v ? 0x80 : 0)));
        fields.Children.Add(Combo("FG initial position", FgPositions, () => (Nibble >> 2) & 3, v => Nibble = (Nibble & 3) | (v << 2)));
        fields.Children.Add(Combo("BG initial position", BgPositions, () => Nibble & 3, v => Nibble = (Nibble & 0xC) | v));
        fields.Children.Add(Slider("FG offset (relative, x16 px)", () => Nibble, 15, v => Nibble = v));
        if (!midway)
            fields.Children.Add(Check("FG offset downward", () => entry.FgOffsetNegative != 0, v => entry = entry with { FgOffsetNegative = v ? 1 : 0 }));
        fields.Children.Add(new TextBlock
        {
            Classes = { "dim" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Fixed positions apply with relative off; with it on, the same nibble is the FG offset from Mario and the BG follows the FG, the scroll settings, and the BG height (Level ▸ Properties).",
        });
        Refresh();
    }

    // ---- the fields, main or midway ----
    private int Action
    {
        get => midway ? entry.MidwayAction : entry.EntranceAction;
        set => entry = midway ? entry with { MidwayAction = value } : entry with { EntranceAction = value };
    }
    private int ActionHigh
    {
        get => midway ? entry.MidwayActionHigh : entry.ActionHigh;
        set => entry = midway ? entry with { MidwayActionHigh = value } : entry with { ActionHigh = value };
    }
    /// <summary>The FG/BG byte: bit 7 relative, bit 6 face left. Main's low bits are the BG height
    /// (left alone here); the midway's carry its $05F400-style camera nibble.</summary>
    private int FgBg
    {
        get => midway ? entry.MidwayFgBg : (entry.FgBgRelative << 7) | (entry.FaceLeft << 6) | entry.BgHeight;
        set => entry = midway ? entry with { MidwayFgBg = value }
                              : entry with { FgBgRelative = value >> 7, FaceLeft = (value >> 6) & 1, BgHeight = value & 0x3F };
    }
    /// <summary>$05F400's camera nibble: bits 2-3 FG position, 0-1 BG position — or, relative, the
    /// FG offset. Main keeps it in two fields; the midway's FG/BG byte holds it in bits 0-3.</summary>
    private int Nibble
    {
        get => midway ? entry.MidwayFgBg & 0x0F : (entry.ScreenBoundaryY << 2) | entry.VerticalScroll;
        set => entry = midway ? entry with { MidwayFgBg = (entry.MidwayFgBg & 0xF0) | (value & 0x0F) }
                              : entry with { ScreenBoundaryY = (value >> 2) & 3, VerticalScroll = value & 3 };
    }

    // ---- rows ----
    private static void Section(Panel p, string text)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add("section");
        p.Children.Add(t);
    }

    private Control Check(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get() };
        cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); Refresh(); };
        return cb;
    }

    private Control Combo(string label, string[] items, Func<int> get, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = items, SelectedIndex = get(), Width = 260 };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); Refresh(); } };
        return Row(label, box);
    }

    private Control Slider(string label, Func<int> get, int max, Action<int> set)
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = max, Value = get(), Width = 210, TickFrequency = 1,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock { Text = get().ToString(), Width = 34, VerticalAlignment = VerticalAlignment.Center };
        readout.Classes.Add("mono");
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            int v = (int)Math.Round(slider.Value);
            readout.Text = v.ToString();
            set(v);
            Refresh();
        };
        var row = (StackPanel)Row(label, slider);
        row.Children.Add(readout);
        return row;
    }

    private static Control Row(string label, Control field)
    {
        var name = new TextBlock { Text = label, Width = 170 };
        name.Classes.Add("label");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(name);
        row.Children.Add(field);
        return row;
    }

    private void Refresh()
        => this.GetControl<TextBlock>("Bytes").Text = "entrance bytes " + Convert.ToHexString(entry.ToBytes());

    private void OnApply(object? sender, RoutedEventArgs e) { Applied = entry; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
