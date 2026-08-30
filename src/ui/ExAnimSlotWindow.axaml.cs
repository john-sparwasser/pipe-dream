using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// One ExAnimation slot, LM's dialog with the arithmetic done for the user: frames are typed as
/// LM tile numbers (0x600-based RAM tiles, or 0xC00-0x1BFF for the alternate files 60-63) and
/// converted to the record's words on the way out; the destination is a tile number or, for
/// palette types, a colour index plus a count. ponytail: a form, not the timeline — the timeline
/// (reference/EXANIMATION.md §8) sits on top of the same Slot.
/// </summary>
public partial class ExAnimSlotWindow : Window
{
    public ExAnimation.Slot? Applied { get; private set; }

    public static readonly (int Code, string Name)[] Types =
    [
        (0x01, "8x8 — 1 tile"), (0x02, "16x8 — 2 tiles in a row"), (0x03, "24x8 — 3 tiles in a row"), (0x04, "32x8 — 4 tiles in a row"),
        (0x05, "40x8 — 5 tiles in a row"), (0x06, "48x8 — 6 tiles in a row"), (0x07, "56x8 — 7 tiles in a row"), (0x08, "64x8 — 8 tiles in a row"),
        (0x09, "12 tiles in a row"), (0x0A, "16 tiles in a row"), (0x0B, "20 tiles in a row"), (0x0C, "24 tiles in a row"),
        (0x0D, "28 tiles in a row"), (0x0E, "32 tiles in a row"), (0x0F, "8x8 — 1 tile, 2bpp (layer 3)"), (0x10, "8x16 — 2 tiles stacked"),
        (0x11, "16x16 — 4 tiles"), (0x12, "32x16 — 8 tiles"), (0x13, "Palette"), (0x14, "Palette + working copies"),
        (0x15, "Palette + working, stop on fade"), (0x16, "Palette back area colour"),
        (0x17, "Palette back area colour, stop on fade"), (0x18, "Palette rotate right"),
        (0x19, "Palette rotate right, reverse on trigger"), (0x1A, "Palette rotate left"),
        (0x1B, "Palette rotate left, reverse on trigger"),
    ];

    public static readonly (int Code, string Name)[] Triggers = BuildTriggers();

    private static (int, string)[] BuildTriggers()
    {
        var t = new List<(int, string)>
        {
            (0x00, "None"), (0x01, "POW"), (0x02, "Silver POW"), (0x03, "ON/OFF"), (0x04, "Have Star"),
            (0x05, "Timer < 100 (unverified code)"), (0x06, "Timer < 100 one shot (unverified code)"),
            (0x07, ">= 5 Yoshi coins (unverified code)"), (0x08, ">= 5 Yoshi coins one shot (unverified code)"),
        };
        for (int n = 0; n < 16; n++) t.Add((0x10 + n, $"Manual {n:X}"));
        for (int n = 0; n < 16; n++) t.Add((0x20 + n, $"Custom {n:X}"));
        for (int n = 0; n < 16; n++) t.Add((0x30 + n, $"One shot {n:X}"));
        return [.. t];
    }

    private readonly int altFileIndex;
    private TextBox slotBox = null!, framesBox = null!, destBox = null!, colorsBox = null!, listBox = null!;
    private ComboBox typeBox = null!, triggerBox = null!;
    private CheckBox altBox = null!;

    public ExAnimSlotWindow() => AvaloniaXamlLoader.Load(this);

    public ExAnimSlotWindow(ExAnimation.Slot? existing, int altFileIndex, bool global) : this()
    {
        this.altFileIndex = altFileIndex;
        Title = (global ? "Global" : "Level") + " ExAnimation slot";
        this.GetControl<TextBlock>("Note").Text =
            $"Frames are LM tile numbers: 600-77F AN1, 780-857 AN2, 900-BE7 Mario, {0xC00 + altFileIndex * 0x400:X}-{0xC00 + altFileIndex * 0x400 + 0x3FF:X} file {0x60 + altFileIndex:X2} " +
            "(tick \"alternate file\"). A stateful trigger takes twice the frames: untriggered ones first. Palette types take SNES colour words.";
        var f = this.GetControl<StackPanel>("Fields");
        var s = existing ?? new ExAnimation.Slot(0, 1, 0, 1, 0x0000, [], altFileIndex);

        f.Children.Add(Row("Slot number (hex 0-1F)", slotBox = new TextBox { Text = s.Index.ToString("X"), Width = 70 }));
        f.Children.Add(Row("Type", typeBox = new ComboBox { ItemsSource = Types.Select(t => t.Name).ToList(), Width = 300,
                                                            SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Code == s.Type)) }));
        f.Children.Add(Row("Trigger", triggerBox = new ComboBox { ItemsSource = Triggers.Select(t => t.Name).ToList(), Width = 300,
                                                                  SelectedIndex = Math.Max(0, Array.FindIndex(Triggers, t => t.Code == s.Trigger)) }));
        f.Children.Add(Row("Frames", framesBox = new TextBox { Text = s.FrameCount.ToString(), Width = 70 }));
        f.Children.Add(Row("Destination (hex tile / colour)", destBox = new TextBox
        {
            Text = s.IsPalette ? s.DestColor.ToString("X2") : s.DestTile.ToString("X3"), Width = 70,
        }));
        f.Children.Add(Row("Colours (palette types)", colorsBox = new TextBox { Text = s.IsPalette ? s.Colors.ToString() : "1", Width = 70 }));
        f.Children.Add(altBox = new CheckBox { Content = $"Use alternate ExGFX file {0x60 + altFileIndex:X2} for source", IsChecked = s.AltFile });
        listBox = new TextBox { Width = 460, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 60 };
        listBox.Classes.Add("mono");
        listBox.Text = string.Join(" ", Enumerable.Range(0, s.Frames.Length)
            .Select(i => s.IsPalette ? s.Frames[i].ToString("X4") : s.SrcTile(i).ToString("X3")));
        f.Children.Add(Row("Frame tiles (hex, space-separated)", listBox));
        foreach (var c in new Control[] { slotBox, framesBox, destBox, colorsBox, listBox })
            ((TextBox)c).TextChanged += (_, _) => Refresh();
        typeBox.SelectionChanged += (_, _) => Refresh();
        triggerBox.SelectionChanged += (_, _) => Refresh();
        altBox.IsCheckedChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The slot the fields describe, or the reason there is none.</summary>
    internal (ExAnimation.Slot? Slot, string? Problem) Build()
    {
        try
        {
            int index = Convert.ToInt32(slotBox.Text?.Trim(), 16);
            if (index is < 0 or > 0x1F) return (null, "slot number is 0-1F");
            int type = Types[Math.Max(0, typeBox.SelectedIndex)].Code;
            int trigger = Triggers[Math.Max(0, triggerBox.SelectedIndex)].Code;
            int frames = int.Parse(framesBox.Text?.Trim() ?? "1");
            if (frames is < 1 or > 0x100) return (null, "frames is 1-256");
            bool palette = type >= ExAnimation.TypePalette, alt = altBox.IsChecked == true;
            int dest = Convert.ToInt32(destBox.Text?.Trim(), 16);
            int destWord;
            if (palette)
            {
                int colors = int.Parse(colorsBox.Text?.Trim() ?? "1");
                if (dest is < 0 or > 0xFF || colors is < 1 or > 0x80) return (null, "colour 00-FF, colours 1-128");
                destWord = dest | (colors - 1) << 8;
            }
            else
            {
                if (dest is < 0 or > 0x1DFF) return (null, "destination tile 000-1DFF");
                destWord = dest << 4;
            }
            if (alt) destWord |= 0x8000;

            var words = new List<ushort>();
            foreach (var tok in (listBox.Text ?? "").Split([' ', ',', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                int v = Convert.ToInt32(tok, 16);
                if (palette) { words.Add((ushort)v); continue; }
                int w = TileToWord(v, alt, altFileIndex);
                if (w < 0) return (null, $"tile {tok} is not in a source range" + (alt ? $" of file {0x60 + altFileIndex:X2}" : ""));
                words.Add((ushort)w);
            }
            int want = ExAnimation.HasFrameWords(type) ? frames * (ExAnimation.TriggerDoubles(trigger) ? 2 : 1) : 0;
            if (want > 0 && words.Count < frames) return (null, $"needs {frames} frame tiles ({words.Count} given)");
            if (words.Count > want) return (null, $"too many frame tiles: {words.Count} for {want}");
            return (new ExAnimation.Slot(index, type, trigger, frames, destWord, [.. words], altFileIndex), null);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return (null, "a field is not a number");
        }
    }

    /// <summary>LM tile number → record word: RAM address for the AN1/AN2/Mario ranges, byte
    /// offset for the alternate file. -1 when the tile is outside every range.</summary>
    internal static int TileToWord(int tile, bool alt, int altFileIndex)
    {
        if (alt)
        {
            int lo = 0xC00 + altFileIndex * 0x400;
            return tile >= lo && tile < lo + 0x400 ? (tile - lo) * 0x20 : -1;
        }
        return tile switch
        {
            >= 0x600 and < 0x858 => 0x7D00 + (tile - 0x600) * 0x20,
            >= 0x900 and < 0xBE8 => 0x2000 + (tile - 0x900) * 0x20,
            _ => -1,
        };
    }

    private void Refresh()
    {
        var (slot, problem) = Build();
        this.GetControl<TextBlock>("Problem").Text = problem ?? "";
        this.GetControl<TextBlock>("Bytes").Text = slot is { } s ? s.Describe() : "";
    }

    private static Control Row(string label, Control field)
    {
        var name = new TextBlock { Text = label, Width = 210 };
        name.Classes.Add("label");
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { name, field } };
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var (slot, _) = Build();
        if (slot is null) return;
        Applied = slot;
        Close();
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
