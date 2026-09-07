using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
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
    private ToggleButton owShowPaths = null!, owShowLevelNumbers = null!, owShowEventNumbers = null!, owShowWarps = null!;
    private StackPanel owViewBar = null!;

    /// <summary>The map is on screen for its colours — the Palette drawer's Overworld tab — so the
    /// brushes, the View toggles, the pictures and the badges stay out of the way, and nothing paints.</summary>
    private bool OwColorsOnly => modePalette.IsChecked == true;
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

    /// <summary>What a click on the map edits — the drawer's tabs, in order. Tiles is layer 2,
    /// the land, in 8x8s; Paths &amp; Levels is layer 1 in 16x16s — Lunar Magic's Layer 1 16x16
    /// Editor, where level tiles and the invisible path tiles are one layer and move alike; the
    /// other two are layer 1's tables.</summary>
    private enum OwMode { Tiles, Layer1, Events, Transitions }
    private OwMode OwModeNow => (OwMode)Math.Max(0, owTabs.SelectedIndex);
    /// <summary>The tilemap the current tab edits — what undo rewinds while the map is on screen.</summary>
    private Services.TilemapEdit? OwEditNow => OwModeNow == OwMode.Layer1 ? session.OwLayer1 : OwModeNow == OwMode.Tiles ? session.OwMap : null;

    /// <summary>The drawer's armed layer 1 tile, or a lassoed block of them, for the Paths &amp;
    /// Levels tab. Tile 0x56 to start with: the first level tile.</summary>
    private int owL1Tile = 0x56;
    private (int X, int Y, int W, int H)? owL1Block;
    /// <summary>A layer 1 block mid-drag: drawn where the lasso is and not where it came from,
    /// so the move shows before it lands. Written on release, as one stroke.</summary>
    private TilemapView.SelectionDrag? owL1Drag;

    /// <summary>Overworld mode: the map canvas, its drawer sheet and the five tabs.</summary>
    private void WireOverworld()
    {
        modeOverworld = this.GetControl<ToggleButton>("ModeOverworld");
        owPane = this.GetControl<DockPanel>("OverworldPane");
        owToolPanel = this.GetControl<DockPanel>("OwToolPanel");
        owView = this.GetControl<TilemapView>("OwView");
        owSheet = this.GetControl<TilemapView>("OwSheet");
        owSheet.Backdrop = 0xFF303030u;   // the sheets' grey for transparent (colour 0), as the Map16 and GFX drawers show it
        owNote = this.GetControl<TextBlock>("OwNote");
        owTabs = this.GetControl<TabStrip>("OwTabs");
        owBrushBar = this.GetControl<StackPanel>("OwBrushBar");
        owPalRow = this.GetControl<ComboBox>("OwPalRow");
        owFlipX = this.GetControl<ToggleButton>("OwFlipX");
        owFlipY = this.GetControl<ToggleButton>("OwFlipY");
        owShowLayer1 = this.GetControl<ToggleButton>("OwShowLayer1");
        owViewBar = this.GetControl<StackPanel>("OwViewBar");
        owShowLayer1.IsCheckedChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        // The number toggles are chrome: redrawn, never recomposed. Paths are pixels in the
        // overlay layer, so that one recomposes.
        foreach (var name in new[] { "OwShowLevelNumbers", "OwShowEventNumbers", "OwShowWarps" })
            this.GetControl<ToggleButton>(name).IsCheckedChanged += (_, _) => owView.InvalidateVisual();
        this.GetControl<ToggleButton>("OwShowPaths").IsCheckedChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        (owShowPaths, owShowLevelNumbers, owShowEventNumbers, owShowWarps) =
            (this.GetControl<ToggleButton>("OwShowPaths"), this.GetControl<ToggleButton>("OwShowLevelNumbers"),
             this.GetControl<ToggleButton>("OwShowEventNumbers"), this.GetControl<ToggleButton>("OwShowWarps"));
        owView.Decorate = DrawOwOverlays;
        for (int i = 0; i < 8; i++) owPalRow.Items.Add($"{i}");
        owPalRow.SelectedIndex = 4;
        owTabs.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) RefreshOverworld(); };
        owPalRow.SelectionChanged += (_, _) => { if (modeOverworld.IsChecked == true) owSheet.Invalidate(); };
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
        // Wired once, like the background's: RefreshOverworld runs per tab switch and re-subscribing
        // there would stack a handler per refresh. The handlers check the tab instead.
        owView.Painted += (_, c) => OwPaint(c.Col, c.Row);
        owView.StrokeEnded += (_, _) => OwStrokeEnded();
        owView.SelectionDragged += (_, d) => OwSelectionDragged(d);
        // A selection that leaves the float's rectangle drops the float. Mid-drag the lasso
        // passes through every rectangle in between, so the check waits for the pointer to let go.
        owView.HolePixels = OwHolePixels;
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

    /// <summary>A layer 1 tile for the drawer: its art, with Lunar Magic's picture over it while
    /// the Paths view is on — path tiles are blank art, and the picture is what tells them apart.</summary>
    private uint[] OwSheetTile(Overworld ow, int t)
    {
        var art = ow.Map16Pixels(t, 0);
        if (owShowPaths.IsChecked != true || Overworld.PathGlyph(t) is not { } g) return art;
        var img = (uint[])art.Clone();
        for (int i = 0; i < img.Length; i++) if (g[i] != 0) img[i] = g[i];
        return img;
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
        bool tiles = OwModeNow == OwMode.Tiles, layer1Tab = OwModeNow == OwMode.Layer1 && !OwColorsOnly, colours = OwColorsOnly;
        owBrushBar.IsVisible = tiles && !colours;
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
        owView.Reshape(EditorSession.Ow8Cols, EditorSession.Ow8Rows, 8);
        if (tiles)
        {
            // The drawer is the 256 8x8 tiles the two FG files give layer 2, in the brush's palette row.
            owSheet.CellAt = (c, r) => r * 16 + c;
            owSheet.CellPixels = t => session.OwSheetPixels(t, Math.Max(0, owPalRow.SelectedIndex));
            owSheet.Selected = owBrushBlock is null ? owBrushTile : null;
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
        // The main map's sea at its corner; a submap's own corner, past the rotation.
        if (!EditorSession.OwMapCell(r.X, r.Y, out int cx, out int cy, out bool sub) || !sub) return 0;
        int col = EditorSession.OwSubDx + (cx >= Overworld.Cols ? Overworld.Cols : 0);
        int r8 = r.Y - 2 * Overworld.Rows;
        int row = 2 * Overworld.Rows + (r8 < Overworld.SubmapRow8Middle ? EditorSession.OwSubDy : r8 < Overworld.SubmapRow8Bottom ? Overworld.SubmapRow8Middle : Overworld.SubmapRow8Bottom);
        return row * EditorSession.Ow8Cols + col;
    }
    /// <summary>A stamp that stays on the canvas.</summary>
    private static bool OwStamp(TilemapEdit map, int col, int row, int word)
        => EditorSession.OwMapCell(col, row, out _, out _, out _) && map.Stamp(col, row, word);

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
                    OwStamp(map, owFloatFrom.X + i, owFloatFrom.Y + j, fill);
        for (int j = 0; j < f.H; j++)
            for (int i = 0; i < f.W; i++) OwStamp(map, owFloatAt.X + i, owFloatAt.Y + j, f.Cells[j * f.W + i]);
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

    // ---- layer 1 placing and moving: the same gestures, snapped to 16x16 tiles ----

    /// <summary>The overlay on the Paths &amp; Levels tab: layer 1 with the path pictures, and a
    /// block being dragged shown where it is going rather than where it was.</summary>
    private uint[]? OwLayer1Overlay(int col, int row, bool paths)
    {
        if (owL1Drag is { } d && EditorSession.OwLayer1Cell(col, row, out _, out _))
        {
            if (Lasso.Contains(d.To, (col, row)))
            {
                var (sc, sr) = d.Source(col, row);
                return session.Ow8TileOverlay(session.OwLayer1At(sc, sr), col, row, paths);
            }
            if (Lasso.Contains(d.From, (col, row))) return null;
        }
        return session.Ow8Overlay(col, row, true, paths);
    }

    /// <summary>Follow a layer 1 move as the lasso is dragged: the block draws at the lasso,
    /// and nothing draws where it came from, until the pointer lets go.</summary>
    private void OwLayer1DragMoved()
    {
        var next = OwModeNow == OwMode.Layer1 && owView.Dragging && owView.LiveDrag is { Move: true } d ? d : (TilemapView.SelectionDrag?)null;
        if (next == owL1Drag) return;
        owL1Drag = next;
        owView.Invalidate();
    }

    /// <summary>Stamp the drawer's layer 1 tile, or block of tiles, at the 16x16 cell under a
    /// canvas cell. A stroke crosses every 8x8 cell on its way, so a tile gets asked for four
    /// times over and written once.</summary>
    private bool OwStampLayer1(Services.TilemapEdit map, int col, int row)
    {
        if (!EditorSession.OwLayer1Cell(col, row, out int x, out int y)) return false;
        if (owL1Block is not { } b) return map.Stamp(x, y, owL1Tile);
        bool changed = false;
        for (int j = 0; j < b.H; j++)
            for (int i = 0; i < b.W; i++)
                changed |= map.Stamp(x + i, y + j, (b.Y + j) * 16 + b.X + i);
        return changed;
    }

    /// <summary>A layer 1 lasso dragged or grown, in canvas cells: a MOVE lifts the tiles and
    /// sets them down at the lasso, leaving empty tiles behind; a grow repeats them over the new
    /// rectangle. One stroke either way — one undo.</summary>
    private void OwLayer1Dragged(Services.TilemapEdit map, TilemapView.SelectionDrag d)
    {
        var (from, to) = (d.From, d.To);
        var src = new Dictionary<(int, int), int>();
        for (int r = from.Y; r < from.Y + from.H; r++)
            for (int c = from.X; c < from.X + from.W; c++)
                if (EditorSession.OwLayer1Cell(c, r, out int x, out int y)) src[(x, y)] = map.At(x, y);
        bool changed = false;
        if (d.Move)
            foreach (var ((x, y), _) in src)
            {
                var (c, r) = EditorSession.OwLayer1Origin(x, y);
                if (!Lasso.Contains(to, (c, r))) changed |= map.Stamp(x, y, 0);
            }
        for (int r = to.Y; r < to.Y + to.H; r++)
            for (int c = to.X; c < to.X + to.W; c++)
            {
                if (!EditorSession.OwLayer1Cell(c, r, out int x, out int y) || EditorSession.OwLayer1Origin(x, y) != (c, r)) continue;
                var (sc, sr) = d.Source(c, r);
                if (EditorSession.OwLayer1Cell(sc, sr, out int sx, out int sy) && src.TryGetValue((sx, sy), out int tile))
                    changed |= map.Stamp(x, y, tile);
            }
        if (!changed || !map.EndStroke()) return;
        owView.Invalidate();
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>Right-click or right-drag paints the drawer's armed block or tile. A lasso up on
    /// the map is not a brush here: as in the GFX editor, reaching for the paint tool lands any
    /// float and drops the selection, and what lands under the pointer is what the drawer holds.
    /// The Tiles tab paints layer 2 words, the Paths &amp; Levels tab layer 1 tiles; the others
    /// do not paint. ponytail: flips apply per tile, the block
    /// keeps its layout — mirror the layout too when a flipped stamp of a whole block is wanted.</summary>
    private void OwPaint(int col, int row)
    {
        if (OwColorsOnly) return;
        if (OwModeNow == OwMode.Layer1)
        {
            if (session.OwLayer1 is { } l1) { owView.ClearSelection(); if (OwStampLayer1(l1, col, row)) owView.Invalidate(); }
            return;
        }
        if (OwModeNow != OwMode.Tiles || session.OwMap is not { } map) return;
        DropOwFloat();
        owView.ClearSelection();
        bool changed = false;
        if (owBrushBlock is { } b)
        {
            for (int j = 0; j < b.H; j++)
                for (int i = 0; i < b.W; i++)
                    changed |= OwStamp(map, col + i, row + j, OwWordFor((b.Y + j) * 16 + b.X + i));
        }
        else changed = OwStamp(map, col, row, OwWordFor(owBrushTile));
        if (changed) owView.Invalidate();
    }

    /// <summary>A lasso dragged or grown. A MOVE lifts the block into the float (or carries the
    /// float on) and writes nothing yet. A grow repeats the block over the new rectangle, in
    /// place, dropping any float first so it reads settled tiles.</summary>
    private void OwSelectionDragged(TilemapView.SelectionDrag d)
    {
        if (OwColorsOnly) return;
        if (OwModeNow == OwMode.Layer1)
        {
            owL1Drag = null;
            if (session.OwLayer1 is { } l1) OwLayer1Dragged(l1, d);
            return;
        }
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
                changed |= OwStamp(map, c, r, src[(sr - from.Y) * from.W + (sc - from.X)]);
            }
        if (!changed || !map.EndStroke()) return;
        owView.Invalidate();
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>Delete or Backspace over a lasso. On the Tiles tab the land under it goes back to
    /// its region's fill — what a move leaves behind — and a floating block is thrown away rather
    /// than landed; on the Paths &amp; Levels tab the layer 1 tiles under it become empty. One
    /// undo either way; the lasso stays, so the same spot can take the next stamp.</summary>
    private void OwDeleteSelection()
    {
        if (OwColorsOnly || owView.Selection is not { } sel) return;
        bool changed = false;
        if (OwModeNow == OwMode.Layer1 && session.OwLayer1 is { } l1)
        {
            owL1Drag = null;
            for (int r = sel.Y; r < sel.Y + sel.H; r++)
                for (int c = sel.X; c < sel.X + sel.W; c++)
                    if (EditorSession.OwLayer1Cell(c, r, out int x, out int y)) changed |= l1.Stamp(x, y, 0);
            if (!changed || !l1.EndStroke()) return;
        }
        else if (OwModeNow == OwMode.Tiles && session.OwMap is { } map)
        {
            var fill = owFloat is not null ? owFloatFrom : sel;         // a float never wrote: only where it was lifted from needs filling
            owFloat = null;
            int word = OwFillWord(fill);
            for (int r = fill.Y; r < fill.Y + fill.H; r++)
                for (int c = fill.X; c < fill.X + fill.W; c++) changed |= OwStamp(map, c, r, word);
            if (!changed || !map.EndStroke()) { owView.Invalidate(); return; }
        }
        else return;
        owView.Invalidate();
        RefreshOverworld();
        UpdateTitle();
    }

    private void OwStrokeEnded()
    {
        if (OwEditNow?.EndStroke() != true) return;
        RefreshOverworld();
        UpdateTitle();
    }

    // ---- Lunar Magic's View menu, drawn over the map ----

    /// <summary>
    /// The paths Mario walks, coloured by what he does on them; the level and base event each
    /// level tile carries; the warp indexes. Chrome over the composed cells, so it reads at any
    /// zoom and never enters a stroke. A 16x16 cell is two view cells on the Tiles tab.
    /// </summary>
    private void DrawOwOverlays(DrawingContext ctx, double step)
    {
        if (session.Overworld is not { } ow || OwColorsOnly) return;
        double tile = step * 2;                                  // a 16x16 map cell is two canvas cells
        bool onEvents = OwModeNow == OwMode.Events;
        // Where a map's 16x16 cell sits on the canvas: the submap map is offset, as LM draws it.
        Point Origin(bool sub, int x, int y)
            => new((2 * x + (sub ? EditorSession.OwSubDx : 0)) * step, (2 * y + (sub ? 2 * Overworld.Rows + EditorSession.OwSubDy : 0)) * step);
        bool paths = owShowPaths.IsChecked == true, levels = owShowLevelNumbers.IsChecked == true, events = onEvents && owShowEventNumbers.IsChecked == true;
        double size = Math.Clamp(tile * 0.4, 8, 13);
        if (onEvents)
        {
            // The Events tab shows what the events lay on the land: every standard step's
            // footprint, the event's number on its first piece — Lunar Magic's event tiles.
            // Drawn first, so the level tiles' own badges sit on top of the footprints.
            Rect Foot(Overworld.EventStep s)
                => new((s.Cx + (s.SubmapMap ? EditorSession.OwSubDx : 0)) * step, (s.Cy + (s.SubmapMap ? 2 * Overworld.Rows + EditorSession.OwSubDy : 0)) * step, s.Size * step, s.Size * step);
            foreach (var s in ow.EventSteps) Overlay.EventPiece(ctx, Foot(s));
            int last = -1;                                   // badges after every footprint, so none sits under a later piece
            foreach (var s in ow.EventSteps)
                if (s.Event != last) { Overlay.Badge(ctx, $"E{s.Event:X2}", size, Foot(s).TopLeft + new Vector(1, 1), UiColors.EventBadge); last = s.Event; }
        }
        if (paths || levels || events)
            for (int row = 0; row < EditorSession.OwRows; row++)
                for (int x = 0; x < EditorSession.OwCols; x++)
                {
                    bool sub = row >= Overworld.Rows;
                    int y = row % Overworld.Rows;
                    var kind = ow.KindOf(ow.Layer1At(x, y, sub));
                    if (kind == Overworld.PathKind.None) continue;
                    var r = new Rect(Origin(sub, x, y), new Size(tile, tile));
                    // LM's own picture is in the overlay pixels; the fill only stands in where it has none.
                    if (kind != Overworld.PathKind.Level) { if (paths && Overworld.PathGlyph(ow.Layer1At(x, y, sub)) is null) Overlay.Path(ctx, r, kind); continue; }
                    int tl = ow.TranslevelAt(x, y, sub);
                    var at = new Point(r.Left + 1, r.Top + 1);
                    if (levels) at = at.WithY(Overlay.Badge(ctx, $"{Overworld.LevelOf(tl):X3}", size, at).Bottom + 1);
                    if (events && ow.BaseEventOf(tl) is var e && e >= 0) Overlay.Badge(ctx, $"E{e:X2}", size, at, UiColors.EventBadge);
                }
        if (owShowWarps.IsChecked != true) return;
        foreach (var w in ow.Warps)
        {
            Pair(w.Submap, w.X, w.Y, $"{w.Index:X}", w.DestIndex);
            // Nothing leads back from here: the arrival wears the index that lands on it, in red.
            if (w.DestIndex < 0) Overlay.Badge(ctx, $"{w.Index:X}", size, Corner(w.DestSubmap, w.DestX >> 4, w.DestY >> 4), UiColors.WarpOneWay);
        }
        foreach (var e in ow.ExitPaths)
        {
            Pair(e.Submap, e.X, e.Y, $"X{e.Index:X}", e.DestIndex);
            if (e.DestIndex < 0) Overlay.Badge(ctx, $"X{e.Index:X}", size, Corner(e.DestSubmap, e.DestX, e.DestY), UiColors.WarpOneWay);
        }
        for (int i = 0; i < ow.KoopaTeleports.Count; i++)
            Overlay.Badge(ctx, $"K{i}", size, Corner(0, ow.KoopaTeleports[i].X, ow.KoopaTeleports[i].Y), UiColors.WarpBadge);

        // Warp badges hang off the cell's right half so a pipe's level number stays readable beside them.
        Point Corner(int submap, int x, int y) => Origin(submap != 0, x, y) + new Vector(tile * 0.5, 1);
        void Pair(int submap, int x, int y, string index, int dest)
        {
            var box = Overlay.Badge(ctx, index, size, Corner(submap, x, y), UiColors.WarpBadge);
            Overlay.Badge(ctx, dest < 0 ? "N/A" : $"{dest:X}", size, new Point(box.X, box.Bottom + 1), dest < 0 ? UiColors.WarpOneWay : UiColors.WarpBadge);
        }
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
