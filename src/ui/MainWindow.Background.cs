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
/// Background mode: the level's layer 2 (a BG Map16 tilemap) or layer 3 (a 2bpp tilemap) as
/// a paintable grid, with the tiles it can address in the drawer and a four-swatch palette
/// strip in the gutter. Both views are <see cref="TilemapView"/>; the maps are the session's
/// TilemapEdits.
/// </summary>
public partial class MainWindow
{
    private DockPanel bgPane = null!, bgToolPanel = null!;
    private ToggleButton bgLayer2 = null!, bgLayer3 = null!;
    private Button bgOptions = null!, bgImportMap = null!, bgExportMap = null!, bgTilemaps = null!;
    private TilemapView bgView = null!, bgSheet = null!;

    /// <summary>What a stamp writes: a BG Map16 tile on layer 2, a whole BG3 word on layer 3
    /// (palette group 2 by default — the eyedropper is how any other one is picked up).</summary>
    private int bgBrush = 0x100, bgBrushL3 = 2 << 10;
    private TextBlock bgNote = null!, bgDrawerTitle = null!;
    private Border bgPaletteBar = null!;
    private Button bgApplyPal = null!, bgEditPal = null!;
    private ComboBox bgPalRow = null!;
    private PaletteGridView bgColors = null!;
    private bool loadingBgPalRow;

    /// <summary>Background mode: the layer 2/3 tilemap view and its drawer sheet.</summary>
    private void WireBackground()
    {
        bgPane = this.GetControl<DockPanel>("BgPane");
        bgToolPanel = this.GetControl<DockPanel>("BgToolPanel");
        bgLayer2 = this.GetControl<ToggleButton>("BgLayer2");
        bgLayer3 = this.GetControl<ToggleButton>("BgLayer3");
        bgOptions = this.GetControl<Button>("BgOptions");
        bgImportMap = this.GetControl<Button>("BgImportMap");
        bgExportMap = this.GetControl<Button>("BgExportMap");
        bgTilemaps = this.GetControl<Button>("BgTilemaps");
        bgTilemaps.Click += (_, _) => ShowTilemapMenu();
        bgView = this.GetControl<TilemapView>("BgView");
        bgSheet = this.GetControl<TilemapView>("BgSheet");
        bgSheet.PickOnLeft = true;
        bgSheet.FitWidth = true;          // the sheet is the drawer's width, not a fixed 256px
        // Wired ONCE: RefreshBg runs on every phase tick and mode switch, and re-subscribing
        // there would stack a handler per refresh. The handlers look the layer up instead.
        bgView.Painted += (_, c) => BgPaint(c.Col, c.Row);
        bgView.StrokeEnded += (_, _) => BgStrokeEnded();
        bgView.SelectionChanged += (_, _) => RefreshBgNote();
        bgView.SelectionDragged += (_, d) => BgSelectionDragged(d);
        bgSheet.Picked += (_, c) => BgBrushPicked(c.Col, c.Row);
        bgNote = this.GetControl<TextBlock>("BgNote");
        bgDrawerTitle = this.GetControl<TextBlock>("BgDrawerTitle");
        bgPaletteBar = this.GetControl<Border>("BgPaletteBar");
        bgApplyPal = this.GetControl<Button>("BgApplyPal");
        bgEditPal = this.GetControl<Button>("BgEditPal");
        bgColors = this.GetControl<PaletteGridView>("BgColors");
        bgColors.Rows = 1;
        bgColors.Cell = 20;
        bgColors.Selectable = false;       // a tilemap word picks a GROUP, not a colour in it
        bgColors.ShowHoverIndex = false;
        bgPalRow = this.GetControl<ComboBox>("BgPalRow");
        for (int i = 0; i < Layer3.PaletteGroups; i++) bgPalRow.Items.Add($"{i}");
        bgPalRow.SelectionChanged += (_, _) =>
        {
            if (loadingBgPalRow || bgPalRow.SelectedIndex < 0) return;
            bgBrushL3 = bgBrushL3 & ~0x1C00 | bgPalRow.SelectedIndex << 10;
            RefreshBg();                   // the drawer sheet draws in the brush's group
        };
        // The gutter answers "what palette is this cell using", so it has to follow the cursor.
        bgView.PointerMoved += (_, _) => UpdateReadout();
        bgView.PointerExited += (_, _) => UpdateReadout();
    }

    /// <summary>Radio behaviour for the two layers, the same hand-rolled pair the Animations
    /// bar uses for Global/Level.</summary>
    private void OnBgLayer(object? sender, RoutedEventArgs e)
    {
        bgLayer2.IsChecked = ReferenceEquals(sender, bgLayer2);
        bgLayer3.IsChecked = ReferenceEquals(sender, bgLayer3);
        RefreshBg();
    }

    // ---- refreshing the mode ----

    /// <summary>
    /// Repaint the Background pane: the level's layer-2 background as a tile map, with the BG
    /// Map16 tiles it can address in the drawer.
    ///
    /// Layer 2 shows something only when this level's layer 2 IS a background image. When it is
    /// an object stream the pane says so and points at the Level canvas — the same division LM
    /// draws, and the reason its own Background editor is empty on those levels.
    /// </summary>
    private void RefreshBg()
    {
        bool layer3 = bgLayer3.IsChecked == true;
        bgDrawerTitle.Text = layer3 ? "Layer 3 tiles" : "BG Map16 — pages 80-81";
        bgOptions.IsVisible = bgImportMap.IsVisible = bgExportMap.IsVisible = layer3;
        bgOptions.IsEnabled = bgImportMap.IsEnabled = bgTilemaps.IsEnabled = session.HasLevel;
        // Export needs something to write; the other two are how you get there.
        bgExportMap.IsEnabled = session.Layer3Map is not null;
        bgView.Backdrop = session.PaletteRgba is { Length: > 0 } pal ? pal[0] : 0xFF000000u;
        bgSheet.Backdrop = bgView.Backdrop;

        if (!session.HasLevel) { EmptyBg(""); return; }
        if (layer3) ShowBgLayer3();
        else ShowBgLayer2(canvas.Phase);
    }

    /// <summary>Nothing to paint: both grids collapse and the note says why.</summary>
    private void EmptyBg(string note)
    {
        bgView.CellAt = null; bgView.Reshape(0, 0, 16);
        bgSheet.CellAt = null; bgSheet.Reshape(0, 0, 16);
        bgNoteBase = note; RefreshBgNote();
    }

    /// <summary>Layer 3 on the canvas and its 8x8 tiles in the drawer, drawn in the brush's palette group.</summary>
    private void ShowBgLayer3()
    {
        int opt = session.Layer3Option;
        if (session.Layer3Map is not { } map)
        {
            // Two different empty states, and saying which one it is IS the fix: "no layer 3"
            // alone left no way to tell a level that never asked for one from a level whose
            // mode has no tilemap to give it (vanilla's table covers modes 0-14, §12b).
            EmptyBg(opt != 0
                ? $"{Layer3.OptionNames[opt]}, but level mode {session.Header?.LevelMode} has no tilemap for it"
                : session.Layer3TilemapImported
                    ? "a tilemap is imported, but this level's option is Blank Layer 3"
                    : "no layer 3 — give this level one with Layer 3 Options");
            return;
        }
        var palCounts = Layer3PaletteCounts(map);
        SeedBgBrushPalette(map, palCounts);
        bgView.CellAt = map.At;
        bgView.CellPixels = session.Layer3CellPixels;
        bgView.Reshape(map.Cols, map.Rows, map.CellPx);

        // The drawer is the SAME control in picker mode: its cells are tile numbers laid out
        // sixteen to a row, drawn in whatever palette group the brush carries, so the sheet
        // and the thing about to be painted match.
        bgSheet.CellAt = (c, r) => (bgBrushL3 & ~0x3FF) | (r * SheetCols + c);
        bgSheet.CellPixels = session.Layer3CellPixels;
        bgSheet.Selected = bgBrushL3 & 0x3FF;
        bgSheet.Reshape(SheetCols, Layer3.TileCount / SheetCols, 8);

        bgNoteBase = $"{Layer3.Cols}x{Layer3.Rows} tiles — {Layer3.OptionNames[opt]}"
                   + $", palettes {Layer3PalettesInUse(palCounts)}"
                   // "Did my painting stick?" is answered here, because the fork is invisible
                   // otherwise: until the first stroke this level is LOOKING AT a tilemap
                   // every level of its mode shares, and after it the level owns one. The bar
                   // is narrow and ellipsises, so the sentence goes in the tip below.
                   + (session.Layer3TilemapImported ? ", own tilemap" : ", shared")
                   + (session.Layer3Advanced is { } a3
                      ? $", scroll {Layer3.VScrollNames[a3.VScroll]}/{Layer3.HScrollNames[a3.HScroll]}"
                      : "");
        RefreshBgNote();
    }

    /// <summary>The layer-2 background on the canvas and the BG Map16 tiles in the drawer, at phase <paramref name="ph"/>.</summary>
    private void ShowBgLayer2(int ph)
    {
        if (session.BgMap is not { } bg)
        {
            EmptyBg("this level's layer 2 is an object stream — edit it on the Level canvas");
            return;
        }
        bgView.CellAt = bg.At;
        bgView.CellPixels = t => session.BgCellPixels(t, ph);
        bgView.Reshape(bg.Cols, bg.Rows, bg.CellPx);

        bgSheet.CellAt = (c, r) => r * SheetCols + c;
        bgSheet.CellPixels = t => session.BgCellPixels(t, ph);
        bgSheet.Selected = session.BgPaintable(bgBrush) & 0x1FF;   // the ring is on the tile that will LAND
        bgSheet.Reshape(SheetCols, EditorSession.BgSheetTiles / SheetCols, 16);

        bgNoteBase = $"{EditorSession.BgCols}x{EditorSession.BgRows} tiles — two screens, repeats"
                   + (session.BgTilemapEdited ? ", edited" : "")
                   // Which page the drawer's tiles land on. Only ONE on a base without the
                   // custom-background hook: a tile from the other page paints as this page's.
                   + (session.BgFixedPage is { } fixedPage
                      ? $", page {fixedPage:X2} only (upgrade the base for both)" : ", pages 80-81");
        RefreshBgNote();
    }

    /// <summary>
    /// Which palette groups the level's tilemap ACTUALLY names, for the note. An imported map
    /// is somebody else's file and the answer is not guessable from the level: this one turned
    /// out to be group 3 alone, and nothing on screen said so. Cells the map never wrote (-1)
    /// are not counted — they are the blank the build pads with, not a choice.
    /// </summary>
    private static int[] Layer3PaletteCounts(TilemapEdit map)
    {
        var counts = new int[Layer3.PaletteGroups];
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Cols; c++)
                if (map.At(c, r) is >= 0 and var w) counts[Layer3.PaletteOf(w)]++;
        return counts;
    }

    private static string Layer3PalettesInUse(int[] counts)
    {
        var used = Enumerable.Range(0, counts.Length).Where(g => counts[g] > 0).ToArray();
        return used.Length == 0 ? "none" : string.Join("/", used);
    }

    /// <summary>
    /// Start the brush on the palette the level's map actually uses, once per level. The drawer
    /// sheet draws every tile in the BRUSH's group, so a brush left on the default showed the
    /// whole sheet in the status bar's font colours while the canvas next to it was drawn in
    /// another — two pictures of the same tiles, disagreeing, with nothing saying why.
    ///
    /// Once per level, not per refresh: after that the group is the user's to pick, and
    /// re-seeding it would undo their choice on the next repaint.
    /// </summary>
    private int bgBrushSeededFor = -1;

    private void SeedBgBrushPalette(TilemapEdit map, int[] counts)
    {
        if (bgBrushSeededFor == session.LevelNum) return;
        bgBrushSeededFor = session.LevelNum;
        int best = 0;
        for (int g = 1; g < counts.Length; g++) if (counts[g] > counts[best]) best = g;
        if (counts[best] > 0) bgBrushL3 = bgBrushL3 & ~0x1C00 | best << 10;
    }

    /// <summary>The drawer sheets are sixteen tiles to a row, as every other sheet here is.</summary>
    private const int SheetCols = 16;

    /// <summary>What the note says apart from the lasso. Kept so a lasso DRAG can update the
    /// note without going back through RefreshBg, which would recompose the whole grid on every
    /// cell the cursor crosses.</summary>
    private string bgNoteBase = "";

    private void RefreshBgNote()
    {
        // The tip carries what the ellipsised note cannot, and it is the question people arrive
        // with: painting a layer 3 needs no Save button of its own — the strokes ride the
        // project like every other level edit, and the ROM gets them at the next build.
        ToolTip.SetTip(bgNote, bgLayer3.IsChecked != true ? null
            : session.Layer3TilemapImported
                ? "This level has a layer-3 tilemap of its own. Painting it is saved with the "
                + "project (Ctrl+S) and written into the ROM when you build (F4)."
                : "This level is still showing the tilemap every level of its mode shares. The "
                + "first stroke gives it one of its own, so painting cannot disturb the others.");
        bgNote.Text = bgNoteBase
                    + (bgView.Selection is { } s ? $"  —  {s.W}x{s.H} selected, right-click to stamp" : "");
        // Every exit from RefreshBg lands here, and so does a lasso drag — which is what the
        // Apply button's label reads, so the gutter has to follow both.
        RefreshBgPalette();
    }

    /// <summary>
    /// The Background gutter palette. Layer 3 is 2bpp, so this shows FOUR colours — the whole
    /// point of giving the mode its own strip rather than reusing the sixteen-wide one, which
    /// would show twelve swatches the layer cannot draw and invite picking one.
    ///
    /// On layer 3 the picker is live: a tilemap word carries its own palette group, so this is
    /// what the next stamp writes into bits 10-12. On layer 2 it is inert and shows the BRUSH
    /// TILE's row instead — a BG Map16 tile carries its palette in its own definition, so the
    /// place to change it is the Map16 editor, and a live picker here would promise otherwise.
    /// </summary>
    private void RefreshBgPalette()
    {
        bool layer3 = bgLayer3.IsChecked == true;
        var pal = session.PaletteRgba;
        int group = BgPaletteGroup();
        int count = layer3 ? Layer3.PaletteColors : 16;
        int at = layer3 ? Layer3.PaletteBase(group) : group * 16;

        loadingBgPalRow = true;
        bgPalRow.SelectedIndex = layer3 ? group : -1;
        loadingBgPalRow = false;
        bgPalRow.IsEnabled = layer3;

        var colors = new uint[count];
        if (group >= 0 && pal.Length >= at + count)
            for (int i = 0; i < count; i++)
                // Colour 0 keeps the sheet's grey convention: in a tile it means transparent,
                // and a black swatch would read as a black you could paint with.
                colors[i] = i == 0 ? 0xFF303030u : pal[at + i];
        bgColors.Cols = count;
        bgColors.Colors = colors;
        bgColors.Describe = i => $"CGRAM {at + i:X2}" + (i == 0 ? " — transparent" : "");
        bgColors.InvalidateVisual();

        // The label says WHICH cells, because the two cases are a rectangle and the whole map and
        // the difference is not recoverable once pressed.
        bgApplyPal.IsVisible = layer3 && session.Layer3Map is not null;
        bgApplyPal.Content = bgView.Selection is { } s ? $"Apply to {s.W}x{s.H}" : "Apply to all";

        bgEditPal.IsVisible = group >= 0;
    }

    /// <summary>The palette group the strip shows: the brush word's on layer 3, the brush tile's
    /// own row on layer 2, or -1 when there is no level.</summary>
    private int BgPaletteGroup() => bgLayer3.IsChecked == true ? Layer3.PaletteOf(bgBrushL3)
                                  : map16?.BgTilePalette(bgBrush) ?? -1;

    /// <summary>The Background gutter readout. "Which palette is this cell using" has no other
    /// answer in this mode — every cell carries its own group, so a single figure in the header
    /// could only ever be the brush's.</summary>
    private string BgReadout()
    {
        if (bgView.Hover is not { } h || BgLayerEdit is not { } map) return "";
        if (h.Col >= map.Cols || h.Row >= map.Rows) return "";
        int w = map.At(h.Col, h.Row);
        if (bgLayer3.IsChecked != true) return $"({h.Col,2},{h.Row,2})  tile 0x{w & 0x1FF:X3}";
        string flags = ((w & 0x2000) != 0 ? " pri" : "") + ((w & 0x4000) != 0 ? " flipX" : "")
                     + ((w & 0x8000) != 0 ? " flipY" : "");
        return $"({h.Col,2},{h.Row,2})  tile 0x{w & 0x3FF:X3}  pal {Layer3.PaletteOf(w)} "
             + $"(CGRAM {Layer3.PaletteBase(Layer3.PaletteOf(w)):X2})" + flags;
    }

    // ---- painting a background ----

    // Left paints, right is the eyedropper, and the drawer's left click arms the brush. The
    // level and Map16 canvases stamp on the RIGHT because their left button runs a selection;
    // this mode has none, so left painting is the binding that leaves no button idle.

    private TilemapEdit? BgLayerEdit => bgLayer3.IsChecked == true ? session.Layer3Map : session.BgMap;

    private void BgPaint(int col, int row)
    {
        if (BgLayerEdit is not { } map) return;
        bool changed;
        if (bgView.Selection is { } sel)
        {
            // Read the whole rectangle BEFORE writing any of it: stamping a selection over
            // itself is the ordinary case (nudging a pattern along by a cell), and reading as
            // you write smears the first row across the rest.
            var copy = ReadRect(map, sel);
            changed = false;
            for (int j = 0; j < sel.H; j++)
                for (int i = 0; i < sel.W; i++)
                    changed |= map.Stamp(col + i, row + j, copy[j * sel.W + i]);
        }
        else changed = map.Stamp(col, row, bgLayer3.IsChecked == true ? bgBrushL3 : session.BgPaintable(bgBrush));
        if (changed) bgView.Invalidate();
    }

    /// <summary>
    /// A selection was dragged to a new place, or grown by a grip. Both are the same write: fill
    /// the new rectangle by REPEATING the old one, which for an equal-sized rectangle is a plain
    /// copy and for a grown one tiles the pattern out along whichever axis was dragged. The
    /// repeat is phased on the old rectangle's own origin, so the block that was there does not
    /// shift under the cursor while the space beside it fills in.
    ///
    /// A move then clears what it left behind — to -1 on layer 3, which is a word the tilemap
    /// never wrote and builds as the transparent tile, and to tile 0 on layer 2, which has no
    /// "unwritten": its cells are bytes and every one of them draws something.
    /// </summary>
    private void BgSelectionDragged(TilemapView.SelectionDrag d)
    {
        if (BgLayerEdit is not { } map) return;
        var (from, to) = (d.From, d.To);
        var src = ReadRect(map, from);

        bool changed = false;
        if (d.Move)
            for (int j = 0; j < from.H; j++)
                for (int i = 0; i < from.W; i++)
                {
                    int c = from.X + i, r = from.Y + j;
                    if (Lasso.Contains(to, (c, r))) continue;
                    changed |= map.Stamp(c, r, bgLayer3.IsChecked == true ? -1 : 0);
                }
        for (int r = to.Y; r < to.Y + to.H; r++)
            for (int c = to.X; c < to.X + to.W; c++)
            {
                var (sc, sr) = d.Source(c, r);
                changed |= map.Stamp(c, r, src[(sr - from.Y) * from.W + (sc - from.X)]);
            }
        if (!changed || !map.EndStroke()) return;
        bgView.Invalidate();
        RefreshBg();
        UpdateTitle();
    }

    /// <summary>A rectangle of cells, row-major — a snapshot to stamp from after writes begin.</summary>
    private static int[] ReadRect(TilemapEdit map, (int X, int Y, int W, int H) r)
    {
        var cells = new int[r.W * r.H];
        for (int j = 0; j < r.H; j++)
            for (int i = 0; i < r.W; i++)
                cells[j * r.W + i] = map.At(r.X + i, r.Y + j);
        return cells;
    }

    /// <summary>Mouse up: the stroke becomes one undo entry and the level's data changes. A drag
    /// that painted nothing new settles into nothing, so it cannot clear the redo stack.</summary>
    private void BgStrokeEnded()
    {
        if (BgLayerEdit?.EndStroke() != true) return;
        RefreshBg();
        UpdateTitle();
    }

    /// <summary>Picking in the drawer DROPS the canvas lasso. The lasso outranks the drawer's
    /// tile when both exist, so leaving it up would make the pick do nothing at all — the
    /// precedence is about which is armed, and picking is how you arm the other one.</summary>
    private void BgBrushPicked(int col, int row)
    {
        int tile = row * SheetCols + col;
        if (bgLayer3.IsChecked == true)
        {
            if (tile >= Layer3.TileCount) return;
            bgBrushL3 = (bgBrushL3 & ~0x3FF) | tile;
        }
        else
        {
            if (tile >= EditorSession.BgSheetTiles) return;
            bgBrush = tile;
        }
        bgView.ClearSelection();
        RefreshBg();
    }

    // ---- the palette bar's buttons ----

    /// <summary>Edit the strip's colours where colours are edited: Palette mode, narrowed to
    /// layer 3's reach when that is the layer, with the group's first paintable colour selected
    /// so the picker opens on it.</summary>
    private void OnEditBgPalette(object? sender, RoutedEventArgs e)
    {
        bool layer3 = bgLayer3.IsChecked == true;
        int group = BgPaletteGroup();
        if (group < 0) return;
        int at = (layer3 ? Layer3.PaletteBase(group) : group * 16) + 1;   // colour 0 is transparent

        OnMode(modePalette, new RoutedEventArgs());
        paletteLayer3.IsChecked = layer3;
        paletteGrid.Select(at);
        paletteBg.Select(-1);
        ShowPaletteColor(at);
    }

    /// <summary>
    /// Put the picked palette group on cells that already have tiles — the lasso's, or the whole
    /// map when there is no lasso. Only the group moves: the tile number, both flips and the
    /// priority bit stay, which is what makes this a recolour rather than a repaint.
    ///
    /// Cells the map never wrote (-1) are LEFT ALONE. Writing a group into one would turn the
    /// gaps into real words, and a flat file's gaps are exactly what <see cref="Layer3.ToBytes"/>
    /// pads with the blank tile — filling them here would put a tile everywhere the level meant
    /// to show nothing.
    /// </summary>
    private void OnApplyBgPalette(object? sender, RoutedEventArgs e)
    {
        if (session.Layer3Map is not { } map) return;
        var (x, y, w, h) = bgView.Selection ?? (0, 0, map.Cols, map.Rows);
        int group = Layer3.PaletteOf(bgBrushL3);
        bool changed = false;
        for (int r = y; r < y + h && r < map.Rows; r++)
            for (int c = x; c < x + w && c < map.Cols; c++)
                if (map.At(c, r) is >= 0 and var word)
                    changed |= map.Stamp(c, r, word & ~0x1C00 | group << 10);
        if (!changed || !map.EndStroke()) return;
        bgView.Invalidate();
        RefreshBg();
        UpdateTitle();
    }

    // ---- layer 3 tilemaps: the library menu, import/export, options ----

    /// <summary>
    /// The Tilemaps menu, built fresh each time it opens so it always shows the library as it
    /// stands: the saved maps for the layer on screen (click one to apply it), then Save as…,
    /// then Delete. One menu for both layers — the layer toggle decides which half you see.
    /// </summary>
    private void ShowTilemapMenu()
    {
        int layer = bgLayer3.IsChecked == true ? 3 : 2;
        var names = session.TilemapPresets(layer).ToList();
        var menu = new MenuFlyout();
        foreach (string n in names)
        {
            var item = new MenuItem { Header = n };
            item.Click += (_, _) => { if (session.ApplyTilemapPreset(n)) { RefreshBg(); UpdateTitle(); } };
            menu.Items.Add(item);
        }
        if (names.Count == 0)
            menu.Items.Add(new MenuItem { Header = $"No saved layer {layer} tilemaps", IsEnabled = false });
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save this level's as…", IsEnabled = BgLayerEdit is not null };
        save.Click += async (_, _) => await SaveTilemapPreset(layer);
        menu.Items.Add(save);
        if (names.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete" };
            foreach (string n in names)
            {
                var item = new MenuItem { Header = n };
                item.Click += (_, _) => { if (session.DeleteTilemapPreset(n)) UpdateTitle(); };
                delete.Items.Add(item);
            }
            menu.Items.Add(delete);
        }
        menu.ShowAt(bgTilemaps);
    }

    private async Task SaveTilemapPreset(int layer)
    {
        var dlg = new TextPromptWindow($"Name for this layer {layer} tilemap",
                                       $"level {session.LevelNum:X3} layer {layer}");
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } name) return;              // cancelled: nothing saved
        if (session.SaveTilemapPreset(name, layer)) UpdateTitle();
    }

    /// <summary>Save the level's layer-3 tilemap to a file. Painting it is already saved with
    /// the project — this is for getting it OUT: into Lunar Magic, another level, or a backup.</summary>
    private async void OnExportLayer3Tilemap(object? sender, RoutedEventArgs e)
    {
        if (await PickSaveFile("Export this level's layer-3 tilemap",
                               $"level{session.LevelNum:X3}.bin",
                               new FilePickerFileType("Tilemap") { Patterns = ["*.bin", "*.map"] }) is not { } path)
            return;
        session.ExportLayer3Tilemap(path);
        UpdateTitle();
    }

    /// <summary>Import a raw layer-3 tilemap for this level — LM's LT3 file, a flat 16-bit map.
    /// Editor-only until LM's tilemap-bypass slot is decoded, which the build says out loud;
    /// this is where you SEE it, which is most of what authoring one needs.</summary>
    private async void OnImportLayer3Tilemap(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Import a layer-3 tilemap",
                           new FilePickerFileType("Tilemap") { Patterns = ["*.bin", "*.map"] }) is not { } path)
            return;
        if (session.ImportLayer3Tilemap(path)) { AdoptSession(); UpdateTitle(); }
    }

    /// <summary>
    /// The level's layer-3 settings, off the Layer 3 bar rather than the level properties
    /// dialog: they are two bits in two different records, but they are one decision, and the
    /// place you make it should be the place you can see the result.
    ///
    /// The option is an ENTRANCE field and the priority a HEADER one, so they apply through
    /// different session calls — and a header change reparses the level, which is why it is
    /// applied second and the repaint happens once at the end.
    /// </summary>
    private async void OnLayer3Options(object? sender, RoutedEventArgs e)
    {
        if (session.Header is not { } header || session.MainEntrance is not { } entry) return;
        var adv = session.Layer3Advanced;
        var dlg = new Layer3OptionsWindow(Layer3.OptionNames, entry.Layer3Option,
                                          header.Layer3Priority != 0, session.Layer3HasTilemap,
                                          adv, session.Layer3AdvancedSupported);
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } r) return;

        if (r.Option != entry.Layer3Option) session.ApplyEntry(entry with { Layer3Option = r.Option });
        int priority = r.Priority ? 1 : 0;
        if (priority != header.Layer3Priority) session.ApplyHeader(header with { Layer3Priority = priority });
        if (r.Advanced != adv) session.ApplyLayer3Advanced(r.Advanced);
        AdoptSession();
        UpdateTitle();
    }
}
