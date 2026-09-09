using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PipeDream.Services;

namespace PipeDream.Ui;

// MainWindow — the Overworld's Transitions tab: the star and pipe warps, linked and unlinked from
// the map itself. Lunar Magic hides this behind Alt+Left on two tiles in its layer 1 editor; here
// the tile under the pointer grows a Link button, pressing it lights every tile the link can land
// on, and a tile that already has one offers Unlink instead. Dispatched from MainWindow.Overworld.cs;
// the tables are EditorSession.Overworld.Transitions.cs.

public partial class MainWindow
{
    /// <summary>The tile a link is being drawn FROM, while the map is waiting for its other end.
    /// Null the rest of the time, and the map is its ordinary self.</summary>
    private (int X, int Y)? owLinkFrom;

    /// <summary>Where that link may land, worked out once when it is armed: every warp tile but
    /// the one it came from. A list, not a test per cell per frame — the map redraws on every
    /// pointer move and the test walks the warp table.</summary>
    private List<(int X, int Y)> owLinkTargets = [];

    /// <summary>
    /// The map on the Transitions tab: layer 1 as everywhere else, except that the stars and pipes
    /// are drawn RED. They are the tab's whole subject and SMW paints them the same green as every
    /// path and level tile around them, which leaves the one thing this tab can act on impossible
    /// to pick out of the map.
    /// </summary>
    private uint[]? OwTransitionOverlay(int col, int row, bool layer1, bool paths)
    {
        var px = session.Ow8Overlay(col, row, layer1, paths);
        if (px is null || !Overworld.IsWarpTile(session.OwLayer1At(col, row))) return px;
        var red = new uint[px.Length];
        for (int i = 0; i < px.Length; i++) red[i] = Reddened(px[i]);
        return red;
    }

    /// <summary>A pixel with its colour swung to red, keeping its shading: the brightest channel
    /// carries the shape, the other two go to the dimmest. Green pipes come out red and the
    /// yellow stars orange; the black outlines and the transparent holes are untouched.</summary>
    private static uint Reddened(uint p)
    {
        if (p >> 24 == 0) return p;                                  // transparent stays transparent
        uint r = p & 0xFF, g = p >> 8 & 0xFF, b = p >> 16 & 0xFF;    // 0xAABBGGRR, as the palettes build it
        uint hi = Math.Max(r, Math.Max(g, b)), lo = Math.Min(r, Math.Min(g, b));
        return p & 0xFF000000 | lo << 16 | lo << 8 | hi;
    }

    /// <summary>The tile's button, pressed on the Transitions tab: Unlink asks first, Link arms
    /// the pick. Both are the same button the Paths &amp; Levels tab uses for Edit.</summary>
    private async void OwTransitionButton(int x, int y)
    {
        if (session.OwTransitionAt(x, y) is { } t)
        {
            string it = t.Exit ? "exit path" : "warp";
            string what = t.Partner >= 0 ? $"This tile and the one it leads to will both stop working."
                                         : "This one-way trip will stop working.";
            var ask = new ConfirmWindow($"Unlink {it}", $"Unlink the {it} on ({t.X:X2},{t.Y:X2})? {what}", "Unlink");
            await ask.ShowDialog(this);
            if (!ask.Confirmed) return;
            if (session.UnlinkOwTransition(x, y) is { } why) await ConfirmWindow.Notice($"Unlink {it}", why).ShowDialog(this);
            AfterOwTransition();
            return;
        }
        ArmOwLink(x, y);
    }

    /// <summary>Arm the pick: the map lights every tile the link can land on and takes the next
    /// click as the answer. Only tiles of the same kind — the two tables are different shapes, so
    /// a pipe cannot come out of an exit tile. The hover button goes; the pointer is going
    /// somewhere else now.</summary>
    private void ArmOwLink(int x, int y)
    {
        bool? kind = session.OwLinkableAt(x, y);
        owLinkFrom = (x, y);
        owLinkTargets = [.. from ty in Enumerable.Range(0, EditorSession.OwL1Rows)
                           from tx in Enumerable.Range(0, EditorSession.OwL1Cols)
                           where (tx, ty) != (x, y) && session.OwLinkableAt(tx, ty) == kind
                           select (tx, ty)];
        // Not HideOwEditButton: the pointer is ON the button, which that one takes as a reason to
        // keep it. It has done its job and the map is the thing to look at now.
        if (this.FindControl<Button>("OwEditBtn") is { } btn) btn.IsVisible = false;
        owView.ClearSelection();
        owView.PickOnLeft = true;                 // the next click on the map answers, and edits nothing
        owView.InvalidateVisual();
        RefreshOwNote();
    }

    /// <summary>Nothing was picked after all: the map goes back to its ordinary self. The tab
    /// still picks rather than paints, so that stays on while it is the tab on screen.</summary>
    private void CancelOwLink()
    {
        if (owLinkFrom is null) return;
        owLinkFrom = null;
        owLinkTargets = [];
        owView.PickOnLeft = OwModeNow == OwMode.Transitions && !OwColorsOnly;
        owView.InvalidateVisual();
        RefreshOwNote();
    }

    /// <summary>A click on the map while a link is armed. A tile that cannot take it — the sea,
    /// a level tile, the tile it came from — calls the whole thing off rather than doing something
    /// the click did not ask for.</summary>
    private async void OwLinkPicked((int Col, int Row) cell)
    {
        if (owLinkFrom is not { } from) return;
        bool ok = EditorSession.OwLayer1Cell(cell.Col, cell.Row, out int x, out int y) && owLinkTargets.Contains((x, y));
        CancelOwLink();
        if (!ok) return;
        string? why = session.LinkOwTransitions(from.X, from.Y, x, y);
        AfterOwTransition();
        if (why is not null) await ConfirmWindow.Notice("Link transition", why).ShowDialog(this);
    }

    /// <summary>The tables changed: the badges, the title's dirty mark and the note all follow.</summary>
    private void AfterOwTransition()
    {
        owView.InvalidateVisual();
        RefreshOwNote();
        UpdateTitle();
    }

    /// <summary>The exit tiles, always red on this tab. They are layer 1 tiles with no picture at
    /// all — Lunar Magic only shows them under its "Layer 1 Mario Paths" view — and a tile you
    /// cannot see is one you cannot link, so here they do not wait for the toggle. Skipped when
    /// the Paths view is already drawing them, or the two fills would stack into a darker red.</summary>
    private void DrawOwExitTiles(DrawingContext ctx, Overworld ow, double step)
    {
        if (OwModeNow != OwMode.Transitions || owShowPaths.IsChecked == true) return;
        for (int row = 0; row < EditorSession.OwL1Rows; row++)
            for (int x = 0; x < EditorSession.OwL1Cols; x++)
            {
                bool sub = row >= Overworld.Rows;
                if (ow.KindOf(ow.Layer1At(x, row % Overworld.Rows, sub)) != Overworld.PathKind.Exit) continue;
                Overlay.Path(ctx, OwLinkRect(step, x, row), Overworld.PathKind.Exit);
            }
    }

    /// <summary>While a link is armed, every tile it can land on is lit and the tile it came from
    /// wears the selection ring. Drawn under the warp badges, so an index stays readable on a lit
    /// tile.</summary>
    private void DrawOwLinkTargets(DrawingContext ctx, double step)
    {
        if (owLinkFrom is not { } from) return;
        foreach (var (x, y) in owLinkTargets)
            Overlay.LinkTarget(ctx, OwLinkRect(step, x, y));
        Overlay.LinkSource(ctx, OwLinkRect(step, from.X, from.Y));
    }

    private static Avalonia.Rect OwLinkRect(double step, int x, int y)
        => new(OwTileOrigin(step, y >= Overworld.Rows, x, y % Overworld.Rows), new Avalonia.Size(2 * step, 2 * step));
}
