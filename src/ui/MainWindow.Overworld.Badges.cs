using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using PipeDream.Services;

namespace PipeDream.Ui;

// MainWindow — Lunar Magic's View menu drawn over the overworld: the paths' kinds where LM has no
// picture, each level tile's level and event, the event footprints, and the warp indexes. Chrome
// over the composed cells, so it reads at any zoom and never enters a stroke.

public partial class MainWindow
{
    /// <summary>
    /// Lunar Magic's View menu: the paths Mario walks where LM has no picture for the tile, the
    /// level and base event each level tile carries, the event footprints on the Events tab, and
    /// the warp indexes. Chrome over the composed cells, so it reads at any zoom and never enters
    /// a stroke. Each pass is its own method; <paramref name="step"/> is a canvas cell on screen.
    /// </summary>
    private void DrawOwOverlays(DrawingContext ctx, double step)
    {
        if (session.Overworld is not { } ow || OwColorsOnly) return;
        double size = Math.Clamp(step * 2 * 0.4, 8, 13);     // badge text, for a 16x16 tile two cells wide
        if (OwModeNow == OwMode.Events) DrawOwEventFootprints(ctx, ow, step, size);
        DrawOwTileBadges(ctx, ow, step, size);
        if (owShowWarps.IsChecked == true) DrawOwWarpBadges(ctx, ow, step, size);
    }

    /// <summary>Where a map's 16x16 cell sits on the canvas: the submap map is offset, as LM draws it.</summary>
    private static Point OwTileOrigin(double step, bool sub, int x, int y)
        => new((2 * x + (sub ? EditorSession.OwSubDx : 0)) * step, (2 * y + (sub ? 2 * Overworld.Rows + EditorSession.OwSubDy : 0)) * step);

    /// <summary>The Events tab shows what the events lay on the land: every standard step's
    /// footprint, the event's number on its first piece — Lunar Magic's event tiles. Drawn before
    /// the tile badges, so those sit on top of the footprints.</summary>
    private static void DrawOwEventFootprints(DrawingContext ctx, Overworld ow, double step, double size)
    {
        Rect Foot(Overworld.EventStep s)
            => new((s.Cx + (s.SubmapMap ? EditorSession.OwSubDx : 0)) * step, (s.Cy + (s.SubmapMap ? 2 * Overworld.Rows + EditorSession.OwSubDy : 0)) * step, s.Size * step, s.Size * step);
        foreach (var s in ow.EventSteps) Overlay.EventPiece(ctx, Foot(s));
        int last = -1;                                   // badges after every footprint, so none sits under a later piece
        foreach (var s in ow.EventSteps)
            if (s.Event != last) { Overlay.Badge(ctx, $"E{s.Event:X2}", size, Foot(s).TopLeft + new Vector(1, 1), UiColors.EventBadge); last = s.Event; }
    }

    /// <summary>Per layer 1 tile: the path kind's fill where LM has no picture, and on a level tile
    /// its level number and the event its exit fires, as the View toggles ask.</summary>
    private void DrawOwTileBadges(DrawingContext ctx, Overworld ow, double step, double size)
    {
        bool paths = owShowPaths.IsChecked == true, levels = owShowLevelNumbers.IsChecked == true;
        bool events = OwModeNow == OwMode.Events && owShowEventNumbers.IsChecked == true;
        if (!paths && !levels && !events) return;
        double tile = step * 2;
        for (int row = 0; row < EditorSession.OwRows; row++)
            for (int x = 0; x < EditorSession.OwCols; x++)
            {
                bool sub = row >= Overworld.Rows;
                int y = row % Overworld.Rows;
                int t = ow.Layer1At(x, y, sub);
                var kind = ow.KindOf(t);
                if (kind == Overworld.PathKind.None) continue;
                var r = new Rect(OwTileOrigin(step, sub, x, y), new Size(tile, tile));
                // LM's own picture is in the overlay pixels; the fill only stands in where it has none.
                if (kind != Overworld.PathKind.Level) { if (paths && Overworld.PathGlyph(t) is null) Overlay.Path(ctx, r, kind); continue; }
                int tl = ow.TranslevelAt(x, y, sub);
                var at = new Point(r.Left + 1, r.Top + 1);
                if (levels) at = at.WithY(Overlay.Badge(ctx, $"{Overworld.LevelOf(tl):X3}", size, at).Bottom + 1);
                if (events && ow.BaseEventOf(tl) is var e && e >= 0) Overlay.Badge(ctx, $"E{e:X2}", size, at, UiColors.EventBadge);
            }
    }

    /// <summary>Star, pipe and exit tiles wear their index over the index they lead to — N/A for a
    /// one-way trip, whose arrival wears the index that lands on it in red — and the three Koopa
    /// Kid drops wear K0-K2. Badges hang off the cell's right half so a pipe's level number stays
    /// readable beside them.</summary>
    private static void DrawOwWarpBadges(DrawingContext ctx, Overworld ow, double step, double size)
    {
        Point Corner(int submap, int x, int y) => OwTileOrigin(step, submap != 0, x, y) + new Vector(step * 2 * 0.5, 1);
        void Pair(int submap, int x, int y, string index, int dest)
        {
            var box = Overlay.Badge(ctx, index, size, Corner(submap, x, y), UiColors.WarpBadge);
            Overlay.Badge(ctx, dest < 0 ? "N/A" : $"{dest:X}", size, new Point(box.X, box.Bottom + 1), dest < 0 ? UiColors.WarpOneWay : UiColors.WarpBadge);
        }
        foreach (var w in ow.Warps)
        {
            Pair(w.Submap, w.X, w.Y, $"{w.Index:X}", w.DestIndex);
            if (w.DestIndex < 0) Overlay.Badge(ctx, $"{w.Index:X}", size, Corner(w.DestSubmap, w.DestX >> 4, w.DestY >> 4), UiColors.WarpOneWay);
        }
        foreach (var e in ow.ExitPaths)
        {
            Pair(e.Submap, e.X, e.Y, $"X{e.Index:X}", e.DestIndex);
            if (e.DestIndex < 0) Overlay.Badge(ctx, $"X{e.Index:X}", size, Corner(e.DestSubmap, e.DestX, e.DestY), UiColors.WarpOneWay);
        }
        for (int i = 0; i < ow.KoopaTeleports.Count; i++)
            Overlay.Badge(ctx, $"K{i}", size, Corner(0, ow.KoopaTeleports[i].X, ow.KoopaTeleports[i].Y), UiColors.WarpBadge);
    }
}
