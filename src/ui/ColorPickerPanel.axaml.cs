using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// The colour picker's whole popup: the BGR555 square and hue strip, the exact 0-31 channel
/// sliders, a hex box, and before/after swatches — the same set ImGui's ColorPicker3 offered.
///
/// It is a control rather than inline XAML in MainWindow because a flyout has its own name
/// scope that <c>GetControl</c> cannot reach into. Keeping it standalone means the window can
/// hold a typed reference to it, and the tests can instantiate and drive it directly instead of
/// having to open a popup — the same reason the secondary windows are built this way.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    // Named controls are looked up rather than used through the XAML name generator's fields:
    // those are only assigned by InitializeComponent, and this control loads its XAML directly.
    // Same reason as LevelPropertiesWindow.
    private readonly ColorPickerView picker;
    private readonly Slider palR, palG, palB;
    private readonly TextBlock palRv, palGv, palBv;
    private readonly TextBox hexBox;
    private readonly Border beforeSwatch, afterSwatch;

    /// <summary>Guard for the round trip: every input writes the colour, and writing the colour
    /// updates every input. Without this a slider tick re-enters itself through the picker.</summary>
    private bool loading;

    private ushort color;

    public ColorPickerPanel()
    {
        AvaloniaXamlLoader.Load(this);
        picker = this.GetControl<ColorPickerView>("Picker");
        palR = this.GetControl<Slider>("PalR");
        palG = this.GetControl<Slider>("PalG");
        palB = this.GetControl<Slider>("PalB");
        palRv = this.GetControl<TextBlock>("PalRv");
        palGv = this.GetControl<TextBlock>("PalGv");
        palBv = this.GetControl<TextBlock>("PalBv");
        hexBox = this.GetControl<TextBox>("HexBox");
        beforeSwatch = this.GetControl<Border>("BeforeSwatch");
        afterSwatch = this.GetControl<Border>("AfterSwatch");

        picker.ColorChanged += (_, c) => Set(c, fromPicker: true);
        foreach (var s in new[] { palR, palG, palB })
            s.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !loading)
                    Set((ushort)(((int)palB.Value << 10) | ((int)palG.Value << 5) | (int)palR.Value));
            };
        hexBox.TextChanged += (_, _) =>
        {
            if (!loading && ParseHex(hexBox.Text) is { } c) Set(c, fromHex: true);
        };
    }

    /// <summary>The picked colour, BGR555. Setting it does not raise <see cref="ColorChanged"/> —
    /// it is how the OWNER pushes a colour in.</summary>
    public ushort Bgr
    {
        get => color;
        set => Set(value, silent: true);
    }

    public event EventHandler<ushort>? ColorChanged;

    /// <summary>Point the panel at a swatch: <paramref name="current"/> becomes the colour being
    /// edited and also the "before" the popup compares against, so a picker closed back on the
    /// original colour visibly changed nothing.</summary>
    public void Begin(ushort current)
    {
        Fill(beforeSwatch, current);
        Set(current, silent: true);
    }

    private void Set(ushort c, bool silent = false, bool fromPicker = false, bool fromHex = false)
    {
        color = c;
        loading = true;
        // Whichever control the value came FROM is left alone: rewriting the picker mid-drag
        // would snap its crosshair onto the quantised colour and lose the hue, and rewriting the
        // hex box would move the caret out from under the user as they type.
        if (!fromPicker) picker.Bgr = c;
        palR.Value = c & 0x1F;
        palG.Value = (c >> 5) & 0x1F;
        palB.Value = (c >> 10) & 0x1F;
        palRv.Text = $"{c & 0x1F}";
        palGv.Text = $"{(c >> 5) & 0x1F}";
        palBv.Text = $"{(c >> 10) & 0x1F}";
        if (!fromHex) hexBox.Text = $"{c:X4}";
        Fill(afterSwatch, c);
        loading = false;
        if (!silent) ColorChanged?.Invoke(this, c);
    }

    /// <summary>4 hex digits is BGR555, the form Lunar Magic and this editor's own readouts use;
    /// 6 is RRGGBB, for pasting from anywhere else. Anything else — including half-typed input —
    /// is null and simply ignored.</summary>
    internal static ushort? ParseHex(string? text)
    {
        string s = (text ?? "").Trim().TrimStart('#', '$');
        const NumberStyles hex = NumberStyles.HexNumber;
        var inv = CultureInfo.InvariantCulture;
        if (s.Length == 4 && ushort.TryParse(s, hex, inv, out ushort bgr)) return (ushort)(bgr & 0x7FFF);
        if (s.Length == 6 && int.TryParse(s, hex, inv, out int rgb))
            return Palette.ToBgr555((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return null;
    }

    private static void Fill(Border b, ushort bgr)
    {
        uint rgba = Palette.ToRgba(bgr);
        b.Background = new SolidColorBrush(Color.FromRgb((byte)(rgba & 0xFF),
                                                         (byte)((rgba >> 8) & 0xFF),
                                                         (byte)((rgba >> 16) & 0xFF)));
    }
}
