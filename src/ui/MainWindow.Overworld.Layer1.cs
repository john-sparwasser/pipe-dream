using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// MainWindow — the Overworld's Paths & Levels tab: layer 1, the level tiles and Mario's paths, in
// 16x16 tiles snapped over the 8x8 canvas. The drawer's tile or block, the drag that shows a
// block where it is going, and the stamps. Dispatched from MainWindow.Overworld.cs.

public partial class MainWindow
{
    /// <summary>The drawer's armed layer 1 tile, or a lassoed block of them, for the Paths &amp;
    /// Levels tab. Tile 0x56 to start with: the first level tile.</summary>
    private int owL1Tile = 0x56;

    private (int X, int Y, int W, int H)? owL1Block;

    /// <summary>A layer 1 block mid-drag: drawn where the lasso is and not where it came from,
    /// so the move shows before it lands. Written on release, as one stroke.</summary>
    private TilemapView.SelectionDrag? owL1Drag;

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

    /// <summary>Delete over a lasso: the layer 1 tiles under it become empty. True when the
    /// stroke changed the map.</summary>
    private bool OwDeleteLayer1(TilemapEdit map, (int X, int Y, int W, int H) sel)
    {
        owL1Drag = null;
        bool changed = false;
        for (int r = sel.Y; r < sel.Y + sel.H; r++)
            for (int c = sel.X; c < sel.X + sel.W; c++)
                if (EditorSession.OwLayer1Cell(c, r, out int x, out int y)) changed |= map.Stamp(x, y, 0);
        return changed && map.EndStroke();
    }
}
