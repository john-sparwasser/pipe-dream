using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// One secondary entrance: the destination side of a secondary screen exit. There are 512 and
/// they are global — any level's exit may point at any index — so this edits one index at a time
/// rather than listing them all.
/// </summary>
public partial class SecondaryEntranceWindow : Window
{
    /// <summary>The index and record to write, when Apply was pressed.</summary>
    public (int Index, SecondaryEntrance Entrance)? Applied { get; private set; }

    private readonly Func<int, SecondaryEntrance?> read = null!;
    private TextBox indexBox = null!, destBox = null!;
    private TextBlock pairNote = null!, bytes = null!;
    private Grid fields = null!;
    private SecondaryEntrance entry;
    private int index;

    public SecondaryEntranceWindow() => AvaloniaXamlLoader.Load(this);

    internal SecondaryEntranceWindow(int index, Func<int, SecondaryEntrance?> read) : this()
    {
        this.read = read;
        indexBox = this.GetControl<TextBox>("IndexBox");
        destBox = this.GetControl<TextBox>("DestBox");
        pairNote = this.GetControl<TextBlock>("PairNote");
        bytes = this.GetControl<TextBlock>("Bytes");
        fields = this.GetControl<Grid>("Fields");

        // Switching index abandons unapplied edits and reloads, so the fields always describe
        // the entrance named above them.
        indexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            Load(Hex(indexBox.Text, EditorSession.SecondaryEntranceCount - 1));
            e.Handled = true;
        };
        destBox.TextChanged += (_, _) =>
        {
            entry = entry with { DestinationLevel = Hex(destBox.Text, 0xFF) };
            ShowBytes();
        };

        BuildFields();
        Load(index);
    }

    private static int Hex(string? text, int max)
        => int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int v)
            ? Math.Clamp(v, 0, max) : 0;

    private readonly List<(string Label, Func<SecondaryEntrance, int> Get,
                           Func<SecondaryEntrance, int, SecondaryEntrance> Set, int Max)> spec = [];
    private readonly List<Slider> sliders = [];
    private readonly List<TextBlock> readouts = [];

    private void BuildFields()
    {
        spec.Add(("Mario X", e => e.MarioX, (e, v) => e with { MarioX = v }, 7));
        spec.Add(("Mario Y", e => e.MarioY, (e, v) => e with { MarioY = v }, 15));
        spec.Add(("Screen boundary Y", e => e.ScreenBoundaryY, (e, v) => e with { ScreenBoundaryY = v }, 3));
        spec.Add(("Vertical scroll", e => e.VerticalScroll, (e, v) => e with { VerticalScroll = v }, 3));
        spec.Add(("Entrance action", e => e.EntranceAction, (e, v) => e with { EntranceAction = v }, 7));
        spec.Add(("FG/BG relative to player", e => e.FgBg >> 7, (e, v) => e with { FgBg = (e.FgBg & 0x7F) | (v << 7) }, 1));
        spec.Add(("Face left", e => (e.FgBg >> 6) & 1, (e, v) => e with { FgBg = (e.FgBg & 0xBF) | (v << 6) }, 1));
        spec.Add(("Slippery level", e => e.ActionHigh, (e, v) => e with { ActionHigh = v }, 1));
        spec.Add(("Water level", e => (e.FgBg >> 5) & 1, (e, v) => e with { FgBg = (e.FgBg & 0xDF) | (v << 5) }, 1));

        for (int i = 0; i < spec.Count; i++)
        {
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = spec[i].Label, Classes = { "label" } };
            var slider = new Slider
            {
                Minimum = 0, Maximum = spec[i].Max, TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var readout = new TextBlock { Classes = { "mono" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            int slot = i;
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || loading) return;
                entry = spec[slot].Set(entry, (int)slider.Value);
                readout.Text = $"{(int)slider.Value}";
                ShowBytes();
            };
            Grid.SetRow(label, i); Grid.SetRow(slider, i); Grid.SetRow(readout, i);
            Grid.SetColumn(slider, 1); Grid.SetColumn(readout, 2);
            fields.Children.Add(label);
            fields.Children.Add(slider);
            fields.Children.Add(readout);
            sliders.Add(slider);
            readouts.Add(readout);
        }
    }

    /// <summary>Guard so populating the controls from a record does not read back as edits.</summary>
    private bool loading;

    private void Load(int at)
    {
        if (read(at) is not { } e) return;
        index = at;
        entry = e;
        loading = true;
        indexBox.Text = $"{index:X3}";
        destBox.Text = $"{entry.DestinationLevel:X2}";
        for (int i = 0; i < spec.Count; i++)
        {
            sliders[i].Value = spec[i].Get(entry);
            readouts[i].Text = $"{spec[i].Get(entry)}";
        }
        loading = false;
        // The index is 9 bits: an exit supplies the low byte and bit 8 comes from the player's
        // submap flag. So exit byte $BB reaches record $0BB from the main map and $1BB from a
        // submap — edit the wrong half and the exit appears to do nothing.
        pairNote.Text = index < 0x100
            ? $"Reached from the main map. A submap exit with the same byte uses ${index + 0x100:X3}."
            : $"Reached from a submap. From the main map the same byte uses ${index - 0x100:X3}.";
        ShowBytes();
    }

    private void ShowBytes() => bytes.Text = "bytes " + Convert.ToHexString(entry.ToBytes());

    private void OnGoToPair(object? sender, RoutedEventArgs e) => Load(index ^ 0x100);

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        Applied = (index, entry);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
