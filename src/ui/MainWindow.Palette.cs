using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

/// <summary>
/// Palette mode: the swatch grid in the drawer and the colour picker flyout over it. The
/// controls are resolved in WireDrawer (MainWindow.Drawer.cs) because the pane is drawer
/// content; everything they do is here.
/// </summary>
public partial class MainWindow
{
    private CheckBox paletteLayer3 = null!;
    private Button paletteReset = null!;

    private PaletteGridView paletteGrid = null!, paletteBg = null!;
    private TextBlock paletteNote = null!, paletteIndex = null!;

    /// <summary>The drawer's Level / Overworld tab, and the submap whose palette the Overworld tab shows.</summary>
    private TabStrip palScopeTabs = null!;
    private ComboBox palSubmap = null!;
    private StackPanel palSubmapRow = null!;
    private static readonly string[] SubmapNames =
        ["Main map", "Yoshi's Island", "Vanilla Dome", "Forest of Illusion", "Valley of Bowser", "Special World", "Star World"];
    private bool PaletteScopeOverworld => palScopeTabs.SelectedIndex == 1;

    /// <summary>The submap palette under the Overworld tab, or null on the Level tab or without a ROM.</summary>
    private Palette? OwPalette => PaletteScopeOverworld ? session.Overworld?.PaletteOf(Math.Max(0, palSubmap.SelectedIndex)) : null;

    /// <summary>A swatch's colour wherever the drawer is pointed: the level's CGRAM, or the submap's.</summary>
    private ushort SwatchBgr(int index) => OwPalette is { } p ? p.Bgr[index] : session.PaletteBgr(index);

    /// <summary>
    /// The Palette drawer's tab decides which canvas sits beside it: the level for the level's
    /// colours, the overworld for a submap's — colours are read against the picture they colour.
    /// Runs on the tab and on entering the mode. ponytail: the overworld palettes are shown, not
    /// edited — LM writes them back into the vanilla tables in place, which is the next step.
    /// </summary>
    private void ApplyPaletteScope()
    {
        if (modePalette.IsChecked != true) return;
        bool ow = PaletteScopeOverworld;
        this.GetControl<DockPanel>("LevelPane").IsVisible = !ow;
        owPane.IsVisible = ow;
        ApplyZoomTarget();                                   // the slider drives whichever canvas is showing
        if (ow) RefreshOverworld();
        paletteGrid.Select(-1);
        RefreshPaletteTab();
    }

    /// <summary>The colour picker and the flyout that shows it over the clicked swatch. The
    /// panel is held directly rather than reached through the flyout, whose content lives in its
    /// own name scope — and so the tests can drive it without opening a popup.</summary>
    internal readonly ColorPickerPanel picker = new();
    private readonly Flyout pickerFlyout = new() { Placement = PlacementMode.Pointer };

    /// <summary>Guard against the picker firing while it is being LOADED from a selection —
    /// otherwise picking a swatch immediately writes its own colour back as an "edit".</summary>
    private bool loadingSwatch;

    private void RefreshPaletteTab()
    {
        // Layer 3 is 2bpp and can name eight palette groups of four, so its whole reach is CGRAM
        // 00-1F. Four wide by eight tall over that range is group-major for free — grid row g IS
        // palette group g — so nothing has to remap indices: a swatch's position in this view and
        // its CGRAM number stay the same thing, and edits, tooltips and the picker are unchanged.
        bool ow = PaletteScopeOverworld;
        palSubmapRow.IsVisible = ow;
        paletteLayer3.IsVisible = !ow;          // layer 3's reach and the level's reset are the level's business
        paletteReset.IsVisible = !ow;
        bool l3 = !ow && paletteLayer3.IsChecked == true;
        var all = ow ? OwPalette?.Rgba ?? new uint[256] : session.PaletteRgba;
        paletteGrid.Cols = l3 ? Layer3.PaletteColors : 16;
        paletteGrid.Rows = l3 ? Layer3.PaletteGroups : 16;
        paletteGrid.Colors = l3 ? [.. all.Take(Layer3.PaletteSpace)] : all;
        paletteGrid.InvalidateVisual();
        paletteBg.Colors = all is { Length: > 0 } pr ? [pr[0]] : [0xFF000000u];
        paletteBg.InvalidateVisual();
        // Just the provenance: which palette you are editing, and whether you have moved it. The
        // rest was a paragraph explaining a grid that explains itself.
        paletteNote.Text = ow
            ? $"overworld — {SubmapNames[Math.Max(0, palSubmap.SelectedIndex)]}: the level loader's colours under the overworld's own ($00AD25). Shown, not edited yet."
            : (session.HasCustomPalette ? "LM custom palette" : "vanilla")
              + (session.PaletteEditCount > 0 ? $"  —  {session.PaletteEditCount} edit(s)" : "");
        ShowPaletteColor(paletteGrid.Selected);
    }

    /// <summary>
    /// Pointing at "Layer 3 only" shows what it would do, on the grid: the eight palette groups
    /// it keeps get ringed, and the 224 colours it drops go under the disabled veil. Reading the
    /// effect off the thing it acts on beats pressing the toggle and comparing two pictures from
    /// memory — and the rings land on the groups, so the shape of the narrowed view is visible
    /// before you get there.
    ///
    /// Nothing to preview once it IS narrowed: at that point nothing is being filtered out.
    /// </summary>
    private void PreviewLayer3Palette(bool on)
        => paletteGrid.Preview = on && paletteLayer3.IsChecked != true
            ? [.. Enumerable.Range(0, Layer3.PaletteGroups)
                            .Select(g => (Layer3.PaletteBase(g), Layer3.PaletteColors, $"{g}"))]
            : null;

    /// <summary>Narrow the palette page to what layer 3 can reach, and back. A selection outside
    /// the narrowed range is DROPPED rather than clamped: clamping would silently move the picker
    /// to a colour the user never chose, and the next edit would land on it.</summary>
    private void OnPaletteLayer3Only(object? sender, RoutedEventArgs e)
    {
        if (paletteLayer3.IsChecked == true && paletteGrid.Selected >= Layer3.PaletteSpace)
            paletteGrid.Select(-1);
        // Pressing it while the pointer is still on it: the preview would otherwise stay up over
        // a grid that has already been narrowed.
        PreviewLayer3Palette(false);
        RefreshPaletteTab();
    }

    /// <summary>The readout under the swatch grid. Deliberately does NOT touch the picker: every
    /// commit recomposes and refreshes this tab, and pushing the colour back into an open picker
    /// would re-derive H/S/V from the quantised value, jumping the crosshair and losing the hue
    /// mid-drag. Loading the picker is <see cref="OpenPicker"/>'s job and happens once, on open.</summary>
    private void ShowPaletteColor(int index)
        => paletteIndex.Text = index < 0 ? "pick a colour" : DescribeSwatch(index);

    /// <summary>The swatch hover text, as the ImGui grid had it.</summary>
    private string DescribeSwatch(int index)
        => $"0x{index:X2} r{index >> 4} c{index & 15}  {SwatchBgr(index):X4}"
         + (!PaletteScopeOverworld && session.IsPaletteEdited(index) ? "  (edited)" : "");

    /// <summary>A swatch's colour, for the hover tip: the five-bit channels the picker's sliders
    /// use, then the 24-bit hex they display as.</summary>
    private string SwatchRgb(int index)
    {
        ushort bgr = SwatchBgr(index);
        uint rgba = EditorSession.Rgba(bgr);
        return $"R{bgr & 31} G{bgr >> 5 & 31} B{bgr >> 10 & 31}  #{rgba & 0xFF:X2}{rgba >> 8 & 0xFF:X2}{rgba >> 16 & 0xFF:X2}";
    }

    /// <summary>Load the picker with the clicked swatch and pop it over the cursor — ImGui
    /// opened its ColorPicker3 in a popup on the swatch, and that is the gesture being restored.
    /// BGR555 is five bits per channel and the picker works in that space directly, so nothing
    /// is quantised behind the user's back the way a 24-bit picker would.</summary>
    private void OpenPicker()
    {
        if (paletteGrid.Selected < 0 || PaletteScopeOverworld) return;     // the overworld's colours are read-only here
        loadingSwatch = true;
        picker.Begin(session.PaletteBgr(paletteGrid.Selected));
        loadingSwatch = false;
        pickerFlyout.ShowAt(paletteGrid, showAtPointer: true);
    }

    /// <summary>
    /// Apply a picked colour to the level, live. There is no debounce: a colour change now
    /// recomposes only the phase on screen and reuses its buffer, which is ~26ms rather than the
    /// ~75ms a full scene rebuild cost, so it can keep up with the drag. The picker also only
    /// raises this when the QUANTISED colour actually changes, which caps it at 32 steps an axis.
    ///
    /// Only the level image and this tab are refreshed. The Map16 sheet and the rest of the
    /// drawer are recoloured too, but nobody is looking at them mid-drag; AdoptSession brings
    /// them up to date when the picker closes.
    /// </summary>
    private void OnPickerColor(ushort bgr)
    {
        if (loadingSwatch || paletteGrid.Selected < 0) return;
        if (!session.SetPaletteColor(paletteGrid.Selected, bgr)) return;

        bitmap.SetImages(session.Phases, session.PxW, session.PxH, canvas.Phase);
        canvas.InvalidateVisual();
        RefreshPaletteTab();
    }

    /// <summary>Reset throws away every colour edit on the level at once and there is no undo on
    /// this tab, so it asks first — the one button here whose miss-click cannot be walked back.</summary>
    private async void OnResetPalette(object? sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmWindow("Reset palette",
            session.PaletteEditCount is var n and > 0
                ? $"Discard {n} colour edit(s) on this level and go back to its original palette?"
                : "Reset this level's palette to its original colours?", "Reset");
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed || !session.ResetPalette()) return;
        AdoptSession();
    }
}
