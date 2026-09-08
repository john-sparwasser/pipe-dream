using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// MainWindow — the Overworld's Tiles tab: layer 2, the land, painted in 8x8 words. The brush from
// the drawer, the fill a moved block leaves, and the float a lasso lifts until it is let go of.
// The gestures arrive through OwPaint / OwSelectionDragged / OwDeleteSelection in
// MainWindow.Overworld.cs, which sends the Tiles tab's here.

public partial class MainWindow
{
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

    /// <summary>The word a sheet tile stamps: the tile, and the palette row and flips from the bar.</summary>
    private int OwWordFor(int tile) => tile | (Math.Max(0, owPalRow.SelectedIndex) << 10);

    /// <summary>Mirror the selected land left to right (<paramref name="mirror"/>) or flip it top
    /// to bottom: the block's layout reversed and every word's flip bit turned, so the picture
    /// turns as a whole. A carried block turns in the float; a settled one in the map, as one undo.</summary>
    private void OwFlipSelection(bool mirror)
    {
        if (OwModeNow != OwMode.Tiles || OwColorsOnly || session.OwMap is not { } map) return;
        if (owFloat is { } f) { owFloat = (Turned(f.Cells, f.W, f.H, mirror), f.W, f.H); owView.Invalidate(); return; }
        if (owView.Selection is not { } sel) return;
        var cells = Turned(ReadRect(map, sel), sel.W, sel.H, mirror);
        bool changed = false;
        for (int j = 0; j < sel.H; j++)
            for (int i = 0; i < sel.W; i++) changed |= OwStamp(map, sel.X + i, sel.Y + j, cells[j * sel.W + i]);
        if (!changed || !map.EndStroke()) return;
        owView.Invalidate();
        RefreshOverworld();
        UpdateTitle();
    }

    /// <summary>A block of words mirrored (columns reversed, X bit turned) or flipped (rows
    /// reversed, Y bit turned).</summary>
    private static int[] Turned(int[] cells, int w, int h, bool mirror)
    {
        var turned = new int[cells.Length];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                turned[j * w + i] = cells[(mirror ? j : h - 1 - j) * w + (mirror ? w - 1 - i : i)] ^ (mirror ? 0x4000 : 0x8000);
        return turned;
    }

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

    /// <summary>Stamp the drawer's armed block or tile at a cell, landing any float first: a
    /// paint reads the map, so the float must be in it. ponytail: flips apply per tile, the
    /// block keeps its layout — mirror the layout too when a flipped stamp of a whole block is wanted.</summary>
    private bool OwPaintLayer2(TilemapEdit map, int col, int row)
    {
        DropOwFloat();
        if (owBrushBlock is not { } b) return OwStamp(map, col, row, OwWordFor(owBrushTile));
        bool changed = false;
        for (int j = 0; j < b.H; j++)
            for (int i = 0; i < b.W; i++)
                changed |= OwStamp(map, col + i, row + j, OwWordFor((b.Y + j) * 16 + b.X + i));
        return changed;
    }

    /// <summary>A lasso dragged or grown. A MOVE lifts the block into the float (or carries the
    /// float on) and writes nothing yet. A grow repeats the block over the new rectangle, in
    /// place, dropping any float first so it reads settled tiles.</summary>
    private void OwLayer2Dragged(TilemapEdit map, TilemapView.SelectionDrag d)
    {
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

    /// <summary>Delete over a lasso: the land under it goes back to its region's fill — what a
    /// move leaves behind — and a floating block is thrown away rather than landed, so only where
    /// it was lifted from needs filling. True when the stroke changed the map.</summary>
    private bool OwDeleteLayer2(TilemapEdit map, (int X, int Y, int W, int H) sel)
    {
        var fill = owFloat is not null ? owFloatFrom : sel;
        owFloat = null;
        int word = OwFillWord(fill);
        bool changed = false;
        for (int r = fill.Y; r < fill.Y + fill.H; r++)
            for (int c = fill.X; c < fill.X + fill.W; c++) changed |= OwStamp(map, c, r, word);
        return changed && map.EndStroke();
    }
}
