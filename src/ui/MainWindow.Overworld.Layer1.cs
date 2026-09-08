using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    /// <summary>A layer 1 tile for the drawer: its art (a hidden tile as the level it becomes,
    /// translucent, laid on the sheet's grey), with Lunar Magic's picture over it while the Paths
    /// view is on — path tiles are blank art, and the picture is what tells them apart.</summary>
    private uint[] OwSheetTile(Overworld ow, int t)
    {
        var img = (uint[])ow.Layer1Art(t, 0).Clone();
        for (int i = 0; i < img.Length; i++)
            if (img[i] >> 24 is > 0 and < 0xFF) img[i] = OverGrey(img[i]);        // the ghost, settled on the sheet
        if (owShowPaths.IsChecked == true && Overworld.PathGlyph(t) is { } g)
            for (int i = 0; i < img.Length; i++) if (g[i] != 0) img[i] = g[i];
        return img;
    }

    /// <summary>A translucent, premultiplied pixel settled onto the sheet's grey (0x303030), opaque.</summary>
    private static uint OverGrey(uint c)
    {
        uint a = c >> 24;
        uint Ch(int shift) => (((c >> shift) & 0xFF) + 0x30u * (255 - a) / 255) << shift;
        return 0xFF000000 | Ch(0) | Ch(8) | Ch(16);
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

    // ---- the settings button, on the level tile the pointer is over ----

    /// <summary>The layer 1 cell the Edit button is currently sitting on, or null when it is
    /// hidden. A real button rather than drawn chrome: the framework hit-tests it, so the press
    /// never reaches the canvas below and starts a lasso.</summary>
    private (int X, int Y)? owEditTile;

    /// <summary>
    /// Move the Edit button onto the level tile the pointer is over, or hide it. Lunar Magic
    /// opens the same settings with an Alt-right click on a tile; here the tile grows a button,
    /// so the gesture is visible rather than remembered. Only on the Paths &amp; Levels tab, and
    /// never mid-drag, when the pointer is busy moving tiles.
    /// </summary>
    private void PlaceOwEditButton() => PlaceOwEditButtonAt(owView.Hover);

    /// <summary>The same, for a named canvas cell — the seam a test drives, since a headless
    /// pointer only moves once it has been pressed and the button is hidden mid-drag.</summary>
    private void PlaceOwEditButtonAt((int Col, int Row)? hover)
    {
        if (this.FindControl<Button>("OwEditBtn") is not { } btn) return;
        // Reaching for the button takes the pointer off the canvas, which is what would otherwise
        // hide it out from under the hand going to press it.
        if (btn.IsPointerOver) return;
        owEditTile = null;
        if (OwModeNow == OwMode.Layer1 && !OwColorsOnly && !owView.Dragging
            && hover is { } h
            && EditorSession.OwLayer1Cell(h.Col, h.Row, out int x, out int y)
            && session.OwLevelTileAt(x, y) is not null)
        {
            var (c, r) = EditorSession.OwLayer1Origin(x, y);
            double step = owView.CellPx * owView.Zoom;
            double h2 = btn.Bounds.Height > 0 ? btn.Bounds.Height : 20;   // 0 until it has been laid out once
            // Viewport coordinates: the map's own margin, the tile, less how far the view is
            // scrolled. Up and to the RIGHT of the tile, clear of it — the tile is what you are
            // looking at, and the level and event badges already sit inside its top-left corner.
            // Against the top edge of the map it sits level with the tile instead of above it.
            const double gap = 2;
            var off = this.FindControl<ScrollViewer>("OwScroll")?.Offset ?? default;
            btn.Margin = new Thickness(owView.Margin.Left + (c + 2) * step + gap - off.X,
                                       owView.Margin.Top + Math.Max(0, r * step - h2 - gap) - off.Y, 0, 0);
            owEditTile = (x, y);
        }
        btn.IsVisible = owEditTile is not null;
    }

    /// <summary>Lunar Magic's Modify Level Tile Settings for one tile, staged and applied on OK.</summary>
    private async void OnOwEditTile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (owEditTile is not { } at || session.OwLevelTileAt(at.X, at.Y) is not { } tile) return;
        var w = new OwLevelTileWindow(tile);
        await w.ShowDialog(this);
        if (!w.Applied) return;
        if (session.SetOwLevelTile(tile, w.BaseEvent, w.ExitDirs)) { owView.Invalidate(); RefreshOverworld(); UpdateTitle(); }
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
