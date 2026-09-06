using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

// MainWindow — Overworld mode: the world map on one canvas, as Lunar Magic draws it, with the
// drawer's tabs choosing what a click edits there. The map itself, its tiles and the tables
// behind the level tiles, paths, events and transitions come from EditorSession.Overworld.cs;
// the mode switch that shows this pane is OnMode in MainWindow.axaml.cs.
public partial class MainWindow
{
    private ToggleButton modeOverworld = null!;
    private DockPanel owPane = null!, owToolPanel = null!;
    private TilemapView owView = null!, owSheet = null!;
    private TextBlock owNote = null!;
    private TabStrip owTabs = null!;
    private StackPanel owBrushBar = null!;
    private ComboBox owPalRow = null!;
    private ToggleButton owFlipX = null!, owFlipY = null!, owShowLayer1 = null!;
    /// <summary>The drawer's armed 8x8 tile, 0x000-0x0FF; the row and flips come from the bar.</summary>
    private int owBrushTile;
    /// <summary>A lassoed block of the sheet, armed instead of the one tile: stamped whole.</summary>
    private (int X, int Y, int W, int H)? owBrushBlock;

    /// <summary>
    /// A block lifted off the map by dragging its lasso. It floats — drawn where the lasso is,
    /// its old place drawn as fill — and nothing is written until it is let go of: a click
    /// elsewhere, a tab or mode switch, a paint, an undo. Lunar Magic and the GFX editor's
    /// float behave the same way: passing a block over tiles must not eat them.
    /// </summary>
    private (int[] Cells, int W, int H)? owFloat;
    private (int X, int Y, int W, int H) owFloatFrom, owFloatAt;

    /// <summary>What a click on the map edits — the drawer's tabs, in order. Lunar Magic's five
    /// editor modes, by the thing each one is about rather than the layer it happens to live on.
    /// Tiles is layer 2, the land, in 8x8s; the other four are layer 1 and its tables.</summary>
    private enum OwMode { Tiles, Paths, Levels, Events, Transitions }
    private OwMode OwModeNow => (OwMode)Math.Max(0, owTabs.SelectedIndex);

    /// <summary>Overworld mode: the map canvas, its drawer sheet and the five tabs.</summary>
    private void WireOverworld()
    {
        modeOverworld = this.GetControl<ToggleButton>("ModeOverworld");
        owPane = this.GetControl<DockPanel>("OverworldPane");
        owToolPanel = this.GetControl<DockPanel>("OwToolPanel");
        owView = this.GetControl<TilemapView>("OwView");
        owSheet = this.GetControl<TilemapView>("OwSheet");
        owNote = this.GetControl<TextBlock>("OwNote");
        owTabs = this.GetControl<TabStrip>("OwTabs");
        owBrushBar = this.GetControl<StackPanel>("OwBrushBar");
        owPalRow = this.GetControl<ComboBox>("OwPalRow");
        owFlipX = this.GetControl<ToggleButton>("OwFlipX");
        owFlipY = this.GetControl<ToggleButton>("OwFlipY");
        owShowLayer1 = this.GetControl<ToggleButton>("OwShowLayer1");
        owShowLayer1.IsCheckedChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        for (int i = 0; i < 8; i++) owPalRow.Items.Add($"{i}");
        owPalRow.SelectedIndex = 4;
        owTabs.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        owPalRow.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) owSheet.Invalidate(); };
        owSheet.PickOnLeft = true;
        owSheet.FitWidth = true;            // the sheet is the drawer's width, like every drawer sheet
        owSheet.LassoPicks = true;          // a dragged rectangle is a block brush, as in the level's Tiles drawer
        owSheet.Picked += (_, c) =>
        {
            owBrushTile = c.Row * 16 + c.Col;
            owBrushBlock = null;
            owSheet.Selected = owBrushTile;
            owView.ClearSelection();
            RefreshOwNote();
        };
        owSheet.BlockPicked += (_, r) =>
        {
            owBrushBlock = r;
            owSheet.Selected = null;        // the lasso on the sheet is the ring now
            owView.ClearSelection();
            RefreshOwNote();
        };
        // Wired once, like the background's: RefreshOverworld runs per tab switch and re-subscribing
        // there would stack a handler per refresh. The handlers check the tab instead.
        owView.Painted += (_, c) => OwPaint(c.Col, c.Row);
        owView.StrokeEnded += (_, _) => OwStrokeEnded();
        owView.SelectionDragged += (_, d) => OwSelectionDragged(d);
        // A selection that leaves the float's rectangle drops the float. Mid-drag the lasso
        // passes through every rectangle in between, so the check waits for the pointer to let go.
        owView.HolePixels = OwHolePixels;
        owView.SelectionChanged += (_, _) => { if (!owView.Dragging) DropOwFloatIfLeft(); };
        owView.PointerReleased += (_, _) => Dispatcher.UIThread.Post(DropOwFloatIfLeft);
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

    /// <summary>The word a sheet tile stamps: the tile, and the palette row and flips from the bar.</summary>
    private int OwWordFor(int tile)
        => tile | (Math.Max(0, owPalRow.SelectedIndex) << 10)
           | (owFlipX.IsChecked == true ? 0x4000 : 0) | (owFlipY.IsChecked == true ? 0x8000 : 0);

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
        bool tiles = OwModeNow == OwMode.Tiles;
        owBrushBar.IsVisible = tiles;
        if (tiles)
        {
            // Layer 2 is the land, in 8x8s: the canvas is the map at that grain, and the drawer is
            // the 256 tiles the two FG files give it, in the brush's palette row. Layer 1 rides on
            // top as an overlay the lasso and the drag preview leave alone — the level tiles stay
            // put while the land under them moves, as in Lunar Magic's Layer 2 mode.
            owView.CellAt = (c, r) => r * EditorSession.Ow8Cols + c;
            owView.CellPixels = OwCellPixels;
            owView.OverlayPixels = owShowLayer1.IsChecked == true ? session.Ow8OverlayPixels : null;
            owView.Reshape(EditorSession.Ow8Cols, EditorSession.Ow8Rows, 8);
            owSheet.CellAt = (c, r) => r * 16 + c;
            owSheet.CellPixels = t => session.OwSheetPixels(t, Math.Max(0, owPalRow.SelectedIndex));
            owSheet.Selected = owBrushBlock is null ? owBrushTile : null;
            owSheet.Reshape(16, EditorSession.OwSheetTiles / 16, 8);
        }
        else
        {
            // ponytail: the layer 1 tabs are read-only until their editors land; the map draws
            // at layer 1's grain and the drawer shows layer 1's Map16 tiles.
            DropOwFloat();
            owView.ClearSelection();
            owView.CellAt = (c, r) => r * EditorSession.OwCols + c;
            owView.CellPixels = session.OwCellPixels;
            owView.OverlayPixels = null;
            owView.Reshape(EditorSession.OwCols, EditorSession.OwRows, 16);
            owSheet.CellAt = (c, r) => r * 16 + c;
            owSheet.CellPixels = t => t < Overworld.Map16Count ? session.Overworld?.Map16Pixels(t, 0) : null;
            owSheet.Selected = null;
            owSheet.ClearSelection();
            owSheet.Reshape(16, (Overworld.Map16Count + 15) / 16, 16);
        }
        RefreshOwNote();
    }

    private void RefreshOwNote()
        => owNote.Text = "main map above, the six submaps below — " + OwModeNow switch
        {
            OwMode.Tiles => "layer 2, the land: right-click paints "
                            + (owBrushBlock is { } b ? $"a {b.W}x{b.H} block" : $"tile 0x{owBrushTile:X2}")
                            + (session.OwEdited ? " — edited" : ""),
            OwMode.Paths => "the tiles Mario walks between levels",
            OwMode.Levels => "which level a tile enters, and what passing it opens",
            OwMode.Events => "what an event reveals, in order",
            _ => "pipes, star roads and exit paths, and where they come out",
        };

    // ---- layer 2 painting: the same three gestures the background tilemap has, plus a float ----

    /// <summary>An 8x8 canvas cell as the Tiles tab shows it: the float where it hovers, the
    /// fill where the float was lifted from, the map everywhere else.</summary>
    private uint[]? OwCellPixels(int cell)
    {
        int cx = cell % EditorSession.Ow8Cols, cy = cell / EditorSession.Ow8Cols;
        if (owFloat is { } f)
        {
            if (Lasso.Contains(owFloatAt, (cx, cy)))
                return session.Ow8WordPixels(f.Cells[(cy - owFloatAt.Y) * f.W + cx - owFloatAt.X], cx, cy);
            if (Lasso.Contains(owFloatFrom, (cx, cy)))
                return session.Ow8WordPixels(OwFillWord(owFloatFrom), cx, cy);
        }
        return session.Ow8CellPixels(cell);
    }

    /// <summary>What the drag preview shows where a moved block was: fill while the block is
    /// being lifted (that is what lifting leaves), and under a block already floating the map as
    /// it is — the float never wrote there, so what shows during the drag is what stays after it.</summary>
    private uint[]? OwHolePixels(int col, int row)
    {
        var cell = (col, row, 1, 1);
        if (owFloat is null || Lasso.Contains(owFloatFrom, (col, row)))
            return session.Ow8WordPixels(OwFillWord(cell), col, row);
        return session.Ow8CellPixels(row * EditorSession.Ow8Cols + col);
    }

    /// <summary>The map's fill for a rectangle: the word at the corner of the region it sits in —
    /// the main map's sea, or the top-left of its submap. ponytail: a corner sample, not a per-
    /// submap "default tile" table, which Lunar Magic does not expose either.</summary>
    private int OwFillCell((int X, int Y, int W, int H) r)
    {
        const int subRows = 2 * Overworld.Rows;                      // 8x8 rows per map
        if (r.Y < subRows) return 0;
        int col = r.X >= EditorSession.Ow8Cols / 2 ? EditorSession.Ow8Cols / 2 : 0;
        int row = subRows + (r.Y - subRows < 20 ? 0 : r.Y - subRows < 40 ? 20 : 40);
        return row * EditorSession.Ow8Cols + col;
    }
    private int OwFillWord((int X, int Y, int W, int H) r)
        => session.OwMap?.At(OwFillCell(r) % EditorSession.Ow8Cols, OwFillCell(r) / EditorSession.Ow8Cols) ?? 0;

    /// <summary>Write the float into the map — fill where it came from, its cells where it is —
    /// as one undo entry, and stop floating.</summary>
    private void DropOwFloat()
    {
        if (owFloat is not { } f || session.OwMap is not { } map) { owFloat = null; return; }
        owFloat = null;
        int fill = OwFillWord(owFloatFrom);
        for (int j = 0; j < owFloatFrom.H; j++)
            for (int i = 0; i < owFloatFrom.W; i++)
                if (!Lasso.Contains(owFloatAt, (owFloatFrom.X + i, owFloatFrom.Y + j)))
                    map.Stamp(owFloatFrom.X + i, owFloatFrom.Y + j, fill);
        for (int j = 0; j < f.H; j++)
            for (int i = 0; i < f.W; i++) map.Stamp(owFloatAt.X + i, owFloatAt.Y + j, f.Cells[j * f.W + i]);
        owView.Invalidate();
        if (!map.EndStroke()) return;
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>The float drops once the lasso is somewhere else, or gone.</summary>
    private void DropOwFloatIfLeft()
    {
        if (owFloat is not null && !owView.Dragging && owView.Selection != owFloatAt) DropOwFloat();
    }

    /// <summary>Right-click or right-drag paints the drawer's armed block or tile. A lasso up on
    /// the map is not a brush here: as in the GFX editor, reaching for the paint tool lands any
    /// float and drops the selection, and what lands under the pointer is what the drawer holds.
    /// Only on the Tiles tab; the others do not paint. ponytail: flips apply per tile, the block
    /// keeps its layout — mirror the layout too when a flipped stamp of a whole block is wanted.</summary>
    private void OwPaint(int col, int row)
    {
        if (OwModeNow != OwMode.Tiles || session.OwMap is not { } map) return;
        DropOwFloat();
        owView.ClearSelection();
        bool changed = false;
        if (owBrushBlock is { } b)
        {
            for (int j = 0; j < b.H; j++)
                for (int i = 0; i < b.W; i++)
                    changed |= map.Stamp(col + i, row + j, OwWordFor((b.Y + j) * 16 + b.X + i));
        }
        else changed = map.Stamp(col, row, OwWordFor(owBrushTile));
        if (changed) owView.Invalidate();
    }

    /// <summary>A lasso dragged or grown. A MOVE lifts the block into the float (or carries the
    /// float on) and writes nothing yet. A grow repeats the block over the new rectangle, in
    /// place, dropping any float first so it reads settled tiles.</summary>
    private void OwSelectionDragged(TilemapView.SelectionDrag d)
    {
        if (OwModeNow != OwMode.Tiles || session.OwMap is not { } map) return;
        var (from, to) = (d.From, d.To);
        if (d.Move)
        {
            if (owFloat is null) { owFloat = (ReadRect(map, from), from.W, from.H); owFloatFrom = from; }
            owFloatAt = to;
            owView.Invalidate();
            return;
        }
        DropOwFloat();
        var src = ReadRect(map, from);
        bool changed = false;
        for (int r = to.Y; r < to.Y + to.H; r++)
            for (int c = to.X; c < to.X + to.W; c++)
            {
                var (sc, sr) = d.Source(c, r);
                changed |= map.Stamp(c, r, src[(sr - from.Y) * from.W + (sc - from.X)]);
            }
        if (!changed || !map.EndStroke()) return;
        owView.Invalidate();
        RefreshOverworld();
        UpdateTitle();
    }

    private void OwStrokeEnded()
    {
        if (session.OwMap?.EndStroke() != true) return;
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>The gutter: the cell under the pointer — an 8x8 word on the Tiles tab, the layer
    /// 1 tile on the others.</summary>
    private string OwReadout()
    {
        if (owView.Hover is not { } h) return "";
        if (OwModeNow == OwMode.Tiles)
        {
            if (session.OwMap is not { } map || !map.InBounds(h.Col, h.Row)) return "";
            int w = map.At(h.Col, h.Row);
            string flags = ((w & 0x4000) != 0 ? " flipX" : "") + ((w & 0x8000) != 0 ? " flipY" : "");
            return $"({h.Col,2},{h.Row,3})  tile 0x{w & 0x3FF:X3}  pal {(w >> 10) & 7}{flags}";
        }
        int tile = session.OwLayer1At(h.Col, h.Row);
        if (tile < 0) return "";
        string what = tile == 0 ? "" : tile < 0x56 ? "  path" : tile <= 0x86 ? "  level tile" : "";
        return $"({h.Col,2},{h.Row,2})  layer 1 tile 0x{tile:X2}{what}";
    }
}
