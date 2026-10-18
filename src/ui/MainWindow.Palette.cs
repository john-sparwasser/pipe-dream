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
    private Button paletteReset = null!, paletteExport = null!, paletteImport = null!;

    private PaletteGridView paletteGrid = null!, paletteBg = null!;
    private TextBlock paletteNote = null!, paletteIndex = null!;

    /// <summary>The drawer's Level / Overworld tab, and the submap whose palette the Overworld tab shows.</summary>
    private TabStrip palScopeTabs = null!;
    private ComboBox palSubmap = null!;
    private StackPanel palSubmapRow = null!;
    private CheckBox palSpecialPassed = null!;
    private static readonly string[] SubmapNames =
        ["Main map", "Yoshi's Island", "Vanilla Dome", "Forest of Illusion", "Valley of Bowser", "Special World", "Star World"];
    private bool PaletteScopeOverworld => palScopeTabs.SelectedIndex == 1;

    private int PalSubmapIndex => Math.Max(0, palSubmap.SelectedIndex);

    /// <summary>The submap palette under the Overworld tab, or null on the Level tab or without a ROM.</summary>
    private Palette? OwPalette => PaletteScopeOverworld ? session.Overworld?.PaletteOf(PalSubmapIndex) : null;

    /// <summary>A swatch the Overworld tab cannot edit: a colour the level loader leaves, which
    /// no overworld table sets. Veiled rather than hidden, as the grid does for any inert swatch.</summary>
    private bool OwSwatchFixed(int index) => PaletteScopeOverworld && session.OwPaletteSource(PalSubmapIndex, index) is null;

    /// <summary>Whether a swatch carries an edit, on whichever tab is showing.</summary>
    private bool SwatchEdited(int index)
        => PaletteScopeOverworld ? session.IsOwPaletteEdited(PalSubmapIndex, index) : session.IsPaletteEdited(index);

    /// <summary>A swatch's colour wherever the drawer is pointed: the level's CGRAM, or the submap's.</summary>
    private ushort SwatchBgr(int index) => OwPalette is { } p ? p.Bgr[index] : session.PaletteBgr(index);

    /// <summary>
    /// The Palette drawer's tab decides which canvas sits beside it: the level for the level's
    /// colours, the overworld for a submap's — colours are read against the picture they colour.
    /// Runs on the tab and on entering the mode.
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

    /// <summary>
    /// The map is drawn as after the special world only while the Overworld tab is showing with
    /// "Special world passed" ticked. Leaving the tab or the mode puts it back as a new game sees
    /// it, so no other mode paints against colours nothing on screen explains; the box keeps its
    /// tick for the way back. Runs on every mode change and every refresh of this tab.
    /// </summary>
    private void SyncOwSpecialPassed()
    {
        if (session.Overworld is not { } ow) return;
        bool want = modePalette.IsChecked == true && PaletteScopeOverworld && palSpecialPassed.IsChecked == true;
        if (ow.SpecialPassed == want) return;
        ow.SpecialPassed = want;
        RepaintOverworld();
    }

    private void RefreshPaletteTab()
    {
        SyncOwSpecialPassed();
        // Layer 3 is 2bpp and can name eight palette groups of four, so its whole reach is CGRAM
        // 00-1F. Four wide by eight tall over that range is group-major for free — grid row g IS
        // palette group g — so nothing has to remap indices: a swatch's position in this view and
        // its CGRAM number stay the same thing, and edits, tooltips and the picker are unchanged.
        bool ow = PaletteScopeOverworld;
        palSubmapRow.IsVisible = ow;
        paletteLayer3.IsVisible = !ow;          // layer 3's reach and the .pal files are the level's business
        paletteExport.IsVisible = !ow;
        paletteImport.IsVisible = !ow;
        // The back-area row is the level header's colour 0. The overworld has no header colour:
        // on vanilla its colour 0 is the level loader's; under Lunar Magic's table it is swatch 0,
        // so the grid shows that cell there rather than leaving it to a row that is hidden.
        this.GetControl<StackPanel>("PaletteBgRow").IsVisible = !ow;
        paletteGrid.HideFirst = !ow;
        this.GetControl<WrapPanel>("PaletteActions").HorizontalAlignment = ow ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        bool l3 = !ow && paletteLayer3.IsChecked == true;
        var all = ow ? OwPalette?.Rgba ?? new uint[256] : session.PaletteRgba;
        paletteGrid.Cols = l3 ? Layer3.PaletteColors : 16;
        paletteGrid.Rows = l3 ? Layer3.PaletteGroups : 16;
        paletteGrid.Colors = l3 ? [.. all.Take(Layer3.PaletteSpace)] : all;
        paletteGrid.InvalidateVisual();
        paletteBg.Colors = all is { Length: > 0 } pr ? [pr[0]] : [0xFF000000u];
        paletteBg.InvalidateVisual();
        // Only what the grid cannot say itself: how many colours you have moved. Nothing at all
        // on an untouched palette — the line goes away.
        int edits = ow ? session.OwPaletteEditCount : session.PaletteEditCount;
        paletteNote.Text = edits > 0 ? $"{edits} edit(s)" : "";
        paletteNote.IsVisible = paletteNote.Text.Length > 0;
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
        => paletteIndex.Text = index < 0 ? "" : DescribeSwatch(index);

    /// <summary>The swatch hover text, as the ImGui grid had it.</summary>
    private string DescribeSwatch(int index)
        => $"0x{index:X2} r{index >> 4} c{index & 15}  {SwatchBgr(index):X4}"
         + (SwatchEdited(index) ? "  (edited)" : "");

    /// <summary>A swatch's colour, for the hover tip: the five-bit channels the picker's sliders
    /// use, then the 24-bit hex they display as — and on the Overworld tab, who else it colours.</summary>
    private string SwatchRgb(int index)
    {
        ushort bgr = SwatchBgr(index);
        uint rgba = EditorSession.Rgba(bgr);
        return $"R{bgr & 31} G{bgr >> 5 & 31} B{bgr >> 10 & 31}  #{rgba & 0xFF:X2}{rgba >> 8 & 0xFF:X2}{rgba >> 16 & 0xFF:X2}"
             + (PaletteScopeOverworld ? "\n" + OwSwatchReach(index) : "");
    }

    /// <summary>Where an overworld colour lands besides here. On vanilla most words are one table
    /// for every submap, so an edit that recolours the whole map must say so before it is made —
    /// otherwise it reads as a bug the first time a neighbour changes. Under Lunar Magic's table
    /// every colour is the submap's own.</summary>
    private string OwSwatchReach(int index)
    {
        var all = session.OwPaletteSharers(PalSubmapIndex, index);
        return all.Length switch
        {
            0 => "the level loader's colour — not the overworld's to edit",
            Overworld.Submaps => "shared by every submap",
            1 => "this submap's own",
            _ => "also " + string.Join(", ", all.Where(s => s != PalSubmapIndex).Select(s => SubmapNames[s])),
        };
    }

    /// <summary>Load the picker with the clicked swatch and pop it over the cursor — ImGui
    /// opened its ColorPicker3 in a popup on the swatch, and that is the gesture being restored.
    /// BGR555 is five bits per channel and the picker works in that space directly, so nothing
    /// is quantised behind the user's back the way a 24-bit picker would.</summary>
    private void OpenPicker()
    {
        if (paletteGrid.Selected < 0 || OwSwatchFixed(paletteGrid.Selected)) return;
        loadingSwatch = true;
        picker.Begin(SwatchBgr(paletteGrid.Selected));
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
    ///
    /// On the Overworld tab the colour goes into the submap's table word, and the map beside the
    /// drawer redraws from the overworld's own caches, which the edit has dropped.
    /// </summary>
    private void OnPickerColor(ushort bgr)
    {
        if (loadingSwatch || paletteGrid.Selected < 0) return;
        if (PaletteScopeOverworld)
        {
            if (!session.SetOwPaletteColor(PalSubmapIndex, paletteGrid.Selected, bgr)) return;
            RepaintOverworld();
        }
        else
        {
            if (!session.SetPaletteColor(paletteGrid.Selected, bgr)) return;
            bitmap.SetImages(session.Phases, session.PxW, session.PxH, canvas.Phase);
            canvas.InvalidateVisual();
        }
        RefreshPaletteTab();
    }

    /// <summary>Right-click: the selected swatch's colour onto <paramref name="index"/>, on
    /// whichever tab is showing, as one undo entry of its own. The selection stays where it is,
    /// so one colour can be laid on several swatches in turn. Nothing selected, or the swatch
    /// itself, does nothing.</summary>
    private void CopySwatch(int index)
    {
        int from = paletteGrid.Selected;
        if (from < 0 || from == index || OwSwatchFixed(index)) return;
        ushort bgr = SwatchBgr(from);
        if (PaletteScopeOverworld)
        {
            if (!session.SetOwPaletteColor(PalSubmapIndex, index, bgr)) return;
            RepaintOverworld();
        }
        else if (!session.SetPaletteColor(index, bgr)) return;
        AdoptSession();                    // every phase and sheet, as the picker's close does
        RefreshPaletteTab();
    }

    /// <summary>
    /// Alt/Cmd+right-click: blend from the selected swatch's colour to <paramref name="index"/>'s
    /// across every swatch between them in CGRAM order — rightward and down when the clicked one
    /// comes later, leftward and up when it comes earlier. Both ends keep their colours; the
    /// blend is <see cref="Palette.Gradient"/>'s. Swatches the tab cannot edit are stepped over,
    /// keeping their place in the blend. One undo step for the lot.
    /// </summary>
    private void GradientSwatches(int index)
    {
        int from = paletteGrid.Selected;
        if (from < 0 || Math.Abs(index - from) < 2 || OwSwatchFixed(index)) return;
        int step = Math.Sign(index - from), n = Math.Abs(index - from) + 1;
        var colours = PipeDream.Palette.Gradient(SwatchBgr(from), SwatchBgr(index), n);
        bool ow = PaletteScopeOverworld;
        session.BeginPaletteStroke();
        for (int k = 1; k < n - 1; k++)
        {
            int i = from + k * step;
            if (OwSwatchFixed(i)) continue;
            if (ow) session.SetOwPaletteColor(PalSubmapIndex, i, colours[k]);
            else session.SetPaletteColor(i, colours[k]);
        }
        session.EndPaletteStroke();
        if (ow) RepaintOverworld();
        AdoptSession();
        RefreshPaletteTab();
    }

    /// <summary>Redraw the overworld map and its sheet in the current colours.</summary>
    private void RepaintOverworld()
    {
        owView.Invalidate();
        owSheet.Invalidate();
    }

    /// <summary>Reset throws away every colour edit on the tab at once, so it asks first — it
    /// is one undo, but a miss-click that recolours the whole map is worth a question.</summary>
    private async void OnResetPalette(object? sender, RoutedEventArgs e)
    {
        bool ow = PaletteScopeOverworld;
        int n = ow ? session.OwPaletteEditCount : session.PaletteEditCount;
        string whose = ow ? "the overworld" : "this level";
        var dlg = new ConfirmWindow("Reset palette",
            n > 0 ? $"Discard {n} colour edit(s) on {whose} and go back to its original palette?"
                  : $"Reset {whose}'s palette to its original colours?", "Reset");
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed || !(ow ? session.ResetOwPalette() : session.ResetPalette())) return;
        AdoptSession();
        if (ow) RepaintOverworld();
    }

    /// <summary>Lunar Magic's "Export Level Palette to File", on the palette bar rather than in a
    /// File menu: it is about the palette in front of you, and that is where the eye is.</summary>
    private async void OnExportPalette(object? sender, RoutedEventArgs e)
    {
        if (await PickSaveFile("Export this level's palette",
                               $"level{session.LevelNum:X3}.pal",
                               new FilePickerFileType("Palette") { Patterns = ["*.pal"] }) is not { } path)
            return;
        session.ExportLevelPalette(path);
        UpdateTitle();
    }

    /// <summary>...and the import, which lands as ordinary colour edits — one undo, recorded in
    /// the project, so a rebuild keeps it.</summary>
    private async void OnImportPalette(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Import a level palette",
                           new FilePickerFileType("Palette") { Patterns = ["*.pal"] }) is not { } path)
            return;
        if (session.ImportLevelPalette(path)) { AdoptSession(); UpdateTitle(); }
    }
}
