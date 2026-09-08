using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

// MainWindow — Overworld mode: the world map on one canvas, as Lunar Magic draws it, with the
// drawer's tabs choosing what a click edits there. This file wires the mode, refreshes the
// canvas and drawer for the tab, and dispatches each gesture to the layer the tab edits:
//   MainWindow.Overworld.Layer2.cs   the Tiles tab — the land, 8x8 words, brush and float
//   MainWindow.Overworld.Layer1.cs   the Paths & Levels tab — level tiles and paths, 16x16
//   MainWindow.Overworld.Badges.cs   Lunar Magic's View menu drawn over the map
// The map itself and the tables behind it come from EditorSession.Overworld.cs; the mode
// switch that shows this pane is OnMode in MainWindow.axaml.cs.
public partial class MainWindow
{
    private ToggleButton modeOverworld = null!;

    private DockPanel owPane = null!, owToolPanel = null!;

    private TilemapView owView = null!, owSheet = null!;

    private TextBlock owNote = null!;

    private TabStrip owTabs = null!;

    private StackPanel owBrushBar = null!;

    private ComboBox owPalRow = null!;

    private Border owPaletteFooter = null!;

    private PaletteGridView owColors = null!;

    private ToggleButton owShowLayer1 = null!;
    private Button owFlipX = null!, owFlipY = null!;

    private ToggleButton owShowPaths = null!, owShowLevelNumbers = null!, owShowEventNumbers = null!, owShowWarps = null!;

    private StackPanel owViewBar = null!;

    /// <summary>The map is on screen for its colours — the Palette drawer's Overworld tab — so the
    /// brushes, the View toggles, the pictures and the badges stay out of the way, and nothing paints.</summary>
    private bool OwColorsOnly => modePalette.IsChecked == true;

    /// <summary>What a click on the map edits — the drawer's tabs, in order. Tiles is layer 2,
    /// the land, in 8x8s; Paths &amp; Levels is layer 1 in 16x16s — Lunar Magic's Layer 1 16x16
    /// Editor, where level tiles and the invisible path tiles are one layer and move alike; the
    /// other two are layer 1's tables.</summary>
    private enum OwMode { Tiles, Layer1, Events, Transitions }

    private OwMode OwModeNow => (OwMode)Math.Max(0, owTabs.SelectedIndex);

    /// <summary>The tilemap the current tab edits — what undo rewinds while the map is on screen.</summary>
    private Services.TilemapEdit? OwEditNow => OwModeNow == OwMode.Layer1 ? session.OwLayer1 : OwModeNow == OwMode.Tiles ? session.OwMap : null;

    /// <summary>Overworld mode: the map canvas, its drawer sheet and the five tabs.</summary>
    private void WireOverworld()
    {
        modeOverworld = this.GetControl<ToggleButton>("ModeOverworld");
        owPane = this.GetControl<DockPanel>("OverworldPane");
        owToolPanel = this.GetControl<DockPanel>("OwToolPanel");
        owNote = this.GetControl<TextBlock>("OwNote");
        owTabs = this.GetControl<TabStrip>("OwTabs");
        owTabs.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        WireOwBar();
        WireOwSheet();
        WireOwView();
    }

    /// <summary>The bar over the map: the flips for the selected land, and Lunar Magic's View toggles.</summary>
    private void WireOwBar()
    {
        owBrushBar = this.GetControl<StackPanel>("OwBrushBar");
        owFlipX = this.GetControl<Button>("OwFlipX");
        owFlipY = this.GetControl<Button>("OwFlipY");
        owFlipX.Click += (_, _) => OwFlipSelection(mirror: true);
        owFlipY.Click += (_, _) => OwFlipSelection(mirror: false);

        owViewBar = this.GetControl<StackPanel>("OwViewBar");
        owShowLayer1 = this.GetControl<ToggleButton>("OwShowLayer1");
        owShowPaths = this.GetControl<ToggleButton>("OwShowPaths");
        owShowLevelNumbers = this.GetControl<ToggleButton>("OwShowLevelNumbers");
        owShowEventNumbers = this.GetControl<ToggleButton>("OwShowEventNumbers");
        owShowWarps = this.GetControl<ToggleButton>("OwShowWarps");
        // Layer 1 and the paths are pixels in the overlay, so those two recompose the map; the
        // number toggles are chrome, redrawn and never recomposed.
        owShowLayer1.IsCheckedChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        owShowPaths.IsCheckedChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        foreach (var toggle in new[] { owShowLevelNumbers, owShowEventNumbers, owShowWarps })
            toggle.IsCheckedChanged += (_, _) => owView.InvalidateVisual();
    }

    /// <summary>The drawer's sheet: a click arms one tile, a lasso a block, for whichever layer
    /// the tab edits.</summary>
    private void WireOwSheet()
    {
        // Under the sheet: the brush's palette row with every colour in it — the row the armed
        // layer 2 tile is stamped with. Tiles already on the map keep theirs.
        owPaletteFooter = this.GetControl<Border>("OwPaletteFooter");
        owPalRow = this.GetControl<ComboBox>("OwPalRow");
        owColors = this.GetControl<PaletteGridView>("OwColors");
        owColors.Rows = 1;
        owColors.Cell = 20;
        owColors.Selectable = false;        // it shows the row; the drawer's tile is what gets stamped
        owColors.ShowHoverIndex = false;
        for (int i = 0; i < 8; i++) owPalRow.Items.Add($"{i}");
        owPalRow.SelectedIndex = 4;
        owPalRow.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) { RefreshOwColors(); owSheet.Invalidate(); } };

        owSheet = this.GetControl<TilemapView>("OwSheet");
        owSheet.Backdrop = 0xFF303030u;     // the sheets' grey for transparent (colour 0), as the Map16 and GFX drawers show it
        owSheet.PickOnLeft = true;
        owSheet.FitWidth = true;            // the sheet is the drawer's width, like every drawer sheet
        owSheet.LassoPicks = true;          // a dragged rectangle is a block brush, as in the level's Tiles drawer
        owSheet.Picked += (_, c) =>
        {
            if (OwModeNow == OwMode.Layer1) { owL1Tile = c.Row * 16 + c.Col; owL1Block = null; owSheet.Selected = owL1Tile; }
            else { owBrushTile = c.Row * 16 + c.Col; owBrushBlock = null; owSheet.Selected = owBrushTile; }
            owView.ClearSelection();
            RefreshOwNote();
        };
        owSheet.BlockPicked += (_, r) =>
        {
            if (OwModeNow == OwMode.Layer1) owL1Block = r; else owBrushBlock = r;
            owSheet.Selected = null;        // the lasso on the sheet is the ring now
            owView.ClearSelection();
            RefreshOwNote();
        };
    }

    /// <summary>The map canvas: every gesture goes to the dispatchers below, which send it to
    /// the layer the tab edits. Wired once, like the background's — RefreshOverworld runs per tab
    /// switch and re-subscribing there would stack a handler per refresh.</summary>
    private void WireOwView()
    {
        owView = this.GetControl<TilemapView>("OwView");
        owView.ReanchorOnClick = false;     // a click on a carried block leaves it selected, and floating
        owView.Decorate = DrawOwOverlays;
        owView.HolePixels = OwHolePixels;
        owView.Painted += (_, c) => OwPaint(c.Col, c.Row);
        owView.StrokeEnded += (_, _) => OwStrokeEnded();
        owView.SelectionDragged += (_, d) => OwSelectionDragged(d);
        // A selection that leaves the float's rectangle drops the float. Mid-drag the lasso
        // passes through every rectangle in between, so the check waits for the pointer to let go.
        owView.SelectionChanged += (_, _) => { if (!owView.Dragging) DropOwFloatIfLeft(); else OwLayer1DragMoved(); };
        owView.PointerReleased += (_, _) => Dispatcher.UIThread.Post(() => { DropOwFloatIfLeft(); OwLayer1DragMoved(); });
        this.GetControl<ScrollViewer>("OwScroll").PointerPressed += (_, e) =>
        {
            // Clicking the desk beside the map drops the selection, and the float with it.
            if (e.Source is Control src && !ReferenceEquals(src, owView) && src.FindAncestorOfType<ScrollBar>() is null
                && !owView.IsVisualAncestorOf(src)) owView.ClearSelection();
        };
        // The gutter answers "which tile is this", so it follows the cursor.
        owView.PointerMoved += (_, _) => UpdateReadout();
        owView.PointerExited += (_, _) => UpdateReadout();
    }

    /// <summary>Redraw the map and the drawer for the current tab.</summary>
    private void RefreshOverworld()
    {
        if (!session.HasRom)
        {
            owNote.Text = "open a project to see its overworld";
            owView.Reshape(0, 0, 16);
            owSheet.Reshape(0, 0, 16);
            return;
        }
        bool tiles = OwModeNow == OwMode.Tiles, layer1Tab = OwModeNow == OwMode.Layer1 && !OwColorsOnly, colours = OwColorsOnly;
        owBrushBar.IsVisible = tiles && !colours;
        owPaletteFooter.IsVisible = tiles && !colours;
        owShowLayer1.IsVisible = tiles;         // the other tabs always show layer 1: it is what they edit
        owViewBar.IsVisible = !colours;
        owShowEventNumbers.IsVisible = OwModeNow == OwMode.Events;      // event numbers belong to the Events tab

        // One canvas for every tab: the land in 8x8 cells, laid out as Lunar Magic lays it out
        // (EditorSession.OwMapCell: the lower map rotated two cells right and one down, wrapping),
        // with layer 1 and the path pictures riding in an overlay a
        // lasso never carries — the level tiles stay put while the land under them moves, as in
        // LM's Layer 2 mode. The tabs differ in what a click does and what the drawer holds.
        bool layer1 = colours || !tiles || owShowLayer1.IsChecked == true, paths = !colours && owShowPaths.IsChecked == true;
        owView.CellAt = (c, r) => r * EditorSession.Ow8Cols + c;
        owView.CellPixels = OwCellPixels;
        owView.OverlayPixels = layer1Tab ? (c, r) => OwLayer1Overlay(c, r, paths)
                             : layer1 || paths ? (c, r) => session.Ow8Overlay(c, r, layer1, paths) : null;
        // The Paths & Levels tab edits layer 1, the overlay: every gesture snaps to its 16x16
        // tiles (a cell right and down on the lower map), and the drag preview is the overlay's.
        owView.Snap = layer1Tab ? EditorSession.OwLayer1Block : null;
        owView.EditsOverlay = layer1Tab;
        // Whole tiles move; they do not stretch. A grip on a one-tile lasso covered most of it,
        // so a drag from near its edge grew the lasso instead of moving the tile.
        owView.Resizable = !layer1Tab;
        owView.Reshape(EditorSession.Ow8Cols, EditorSession.Ow8Rows, 8);
        if (tiles)
        {
            // The drawer is the 256 8x8 tiles the two FG files give layer 2, in the brush's palette row.
            owSheet.CellAt = (c, r) => r * 16 + c;
            owSheet.CellPixels = t => session.OwSheetPixels(t, Math.Max(0, owPalRow.SelectedIndex));
            owSheet.Selected = owBrushBlock is null ? owBrushTile : null;
            RefreshOwColors();
            owSheet.Reshape(16, EditorSession.OwSheetTiles / 16, 8);
        }
        else
        {
            // The drawer is layer 1's Map16 tiles, with the path pictures over them while Paths
            // is on. The Paths & Levels tab stamps them; ponytail: Events and Transitions are
            // read-only until their editors land.
            DropOwFloat();
            owSheet.CellAt = (c, r) => r * 16 + c;
            owSheet.CellPixels = t => session.Overworld is { } ow && t < ow.Map16Count ? OwSheetTile(ow, t) : null;
            if (layer1Tab) owSheet.Selected = owL1Block is null ? owL1Tile : null;
            else { owView.ClearSelection(); owSheet.Selected = null; owSheet.ClearSelection(); }
            owSheet.Reshape(16, ((session.Overworld?.Map16Count ?? 0) + 15) / 16, 16);
        }
        RefreshOwNote();
    }

    /// <summary>The footer's swatches: the brush's palette row in the main map's colours, colour 0
    /// as the sheet's grey.</summary>
    private void RefreshOwColors()
    {
        int row = Math.Max(0, owPalRow.SelectedIndex);
        var colors = new uint[16];
        if (session.Overworld is { } ow)
            for (int i = 0; i < 16; i++) colors[i] = i == 0 ? 0xFF303030u : ow.PaletteOf(0).Rgba[row * 16 + i];
        owColors.Cols = 16;
        owColors.Colors = colors;
        owColors.InvalidateVisual();
    }

    private void RefreshOwNote()
        => owNote.Text = "main map above, the six submaps below — " + (OwColorsOnly ? "each submap in its own colours; the drawer shows one palette at a time" : OwModeNow switch
        {
            OwMode.Tiles => "layer 2, the land: right-click paints "
                            + (owBrushBlock is { } b ? $"a {b.W}x{b.H} block" : $"tile 0x{owBrushTile:X2}")
                            + (session.OwEdited ? " — edited" : ""),
            OwMode.Layer1 => "layer 1, the level tiles and paths in 16x16s: right-click places "
                             + (owL1Block is { } b ? $"a {b.W}x{b.H} block" : $"tile 0x{owL1Tile:X2}")
                             + ", a dragged lasso moves" + (session.OwLayer1Edited ? " — edited" : ""),
            OwMode.Events => "what an event reveals, in order",
            _ => "pipes, star roads and exit paths, and where they come out",
        });

    // ---- gestures, sent to the layer the tab edits ----

    /// <summary>Right-click or right-drag paints the drawer's armed tile or block. A lasso up on
    /// the map is not a brush here: as in the GFX editor, reaching for the paint tool lands any
    /// float and drops the selection, and what lands under the pointer is what the drawer holds.
    /// The Tiles tab paints layer 2 words, the Paths &amp; Levels tab layer 1 tiles; the others
    /// do not paint.</summary>
    private void OwPaint(int col, int row)
    {
        if (OwColorsOnly) return;
        owView.ClearSelection();
        bool changed = OwModeNow switch
        {
            OwMode.Layer1 when session.OwLayer1 is { } l1 => OwStampLayer1(l1, col, row),
            OwMode.Tiles when session.OwMap is { } map => OwPaintLayer2(map, col, row),
            _ => false,
        };
        if (changed) owView.Invalidate();
    }

    /// <summary>A lasso dragged or grown, in canvas cells: the Tiles tab lifts a float or repeats
    /// the land; the Paths &amp; Levels tab moves or repeats layer 1 tiles, as one stroke.</summary>
    private void OwSelectionDragged(TilemapView.SelectionDrag d)
    {
        if (OwColorsOnly) return;
        switch (OwModeNow)
        {
            case OwMode.Layer1 when session.OwLayer1 is { } l1: owL1Drag = null; OwLayer1Dragged(l1, d); break;
            case OwMode.Tiles when session.OwMap is { } map: OwLayer2Dragged(map, d); break;
        }
    }

    /// <summary>Delete or Backspace over a lasso empties what is under it on the layer the tab
    /// edits. One undo either way; the lasso stays, so the same spot can take the next stamp.</summary>
    private void OwDeleteSelection()
    {
        if (OwColorsOnly || owView.Selection is not { } sel) return;
        bool changed = OwModeNow switch
        {
            OwMode.Layer1 when session.OwLayer1 is { } l1 => OwDeleteLayer1(l1, sel),
            OwMode.Tiles when session.OwMap is { } map => OwDeleteLayer2(map, sel),
            _ => false,
        };
        owView.Invalidate();
        if (!changed) return;
        RefreshOverworld();
        UpdateTitle();
    }


    private void OwStrokeEnded()
    {
        if (OwEditNow?.EndStroke() != true) return;
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>The gutter: the cell under the pointer in its map's own coordinates (an s marks the
    /// submap map) — an 8x8 word on the Tiles tab, the layer 1 tile on the others.</summary>
    private string OwReadout()
    {
        if (owView.Hover is not { } h || !EditorSession.OwMapCell(h.Col, h.Row, out int cx, out int cy, out bool sub)) return "";
        string map = sub ? "s" : "";
        if (OwModeNow == OwMode.Tiles)
        {
            if (session.OwMap is not { } m) return "";
            int w = m.At(h.Col, h.Row);
            string flags = ((w & 0x4000) != 0 ? " flipX" : "") + ((w & 0x8000) != 0 ? " flipY" : "");
            return $"({cx,2},{cy,2}{map})  tile 0x{w & 0x3FF:X3}  pal {(w >> 10) & 7}{flags}";
        }
        int tile = session.OwLayer1At(h.Col, h.Row);
        if (tile < 0) return "";
        string what = tile == 0 ? "" : tile < 0x56 ? "  path" : tile <= 0x86 ? "  level tile" : "";
        return $"({cx >> 1,2},{cy >> 1,2}{map})  layer 1 tile 0x{tile:X2}{what}";
    }
}
