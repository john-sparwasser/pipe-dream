using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// A sprite's tiles laid out around its placement cell, to pick and move: click a tile to pick it,
/// drag to move every picked tile, drag on empty space to lasso, Shift or Ctrl/Cmd-click to add
/// one, arrows to nudge (Shift: 8px), Ctrl/Cmd+A to pick all, Delete to remove. The host owns the
/// tiles and the pick (its tile list); this asks for them and reports what the pointer did.
/// Debug ▸ Sprite tiles and the custom sprite editor both arrange tiles with it.
/// </summary>
public sealed class TileArrangeView : Panel
{
    /// <summary>The canvas is PreviewPx square, the placement cell's top-left at Origin, drawn at Scale.</summary>
    public const int PreviewPx = 128, OriginX = 48, OriginY = 64, Scale = 3;

    /// <summary>How many tiles, and tile i's top-left (cell-relative) and size in pixels.</summary>
    public Func<int> Count { get; set; } = () => 0;
    public Func<int, (int X, int Y, int Size)> BoxOf { get; set; } = _ => default;
    /// <summary>The picked tiles, and the primary one (the one the fields show), or -1.</summary>
    public Func<IReadOnlyList<int>> Picked { get; set; } = () => [];
    public Func<int> Primary { get; set; } = () => -1;
    /// <summary>Pick these tiles, the first as primary.</summary>
    public Action<int[]> Pick { get; set; } = _ => { };
    /// <summary>Put these tiles at these cell-relative positions, picked, the first as primary.</summary>
    public Action<(int I, int X, int Y)[]> MoveTo { get; set; } = _ => { };
    public Action Delete { get; set; } = () => { };
    /// <summary>The tiles drawn on a clear w×h canvas with the cell's top-left at (ox, oy).</summary>
    public Func<int, int, int, int, uint[]> RenderTiles { get; set; } = (w, h, _, _) => new uint[w * h];

    private readonly PixelImage image = new() { Stretch = true };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    /// <summary>A move in progress: where the press was, and each picked tile's position then.</summary>
    private (Point At, (int I, int X, int Y)[] From)? drag;
    /// <summary>A lasso in progress, in view pixels; Add keeps what was already picked.</summary>
    private (Point From, Point To, bool Add)? lasso;

    public TileArrangeView()
    {
        Width = Height = PreviewPx * Scale;
        Focusable = true;
        Background = UiColors.DeskPattern;
        Children.Add(image);
        Children.Add(overlay);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        KeyDown += OnKey;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();                                       // arrows and Ctrl+A act on the preview
        var p = e.GetPosition(this);
        bool add = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        int hit = TileAt(p);
        var picked = Picked().ToList();
        if (hit < 0) { lasso = (p, p, add); return; }
        if (add)                                       // toggle one tile in or out, no move
        {
            if (!picked.Remove(hit)) picked.Insert(0, hit);
            Pick([.. picked]);
            return;
        }
        // Grabbing a picked tile moves the whole pick; any other tile becomes the pick.
        if (!picked.Contains(hit)) picked = [hit];
        else { picked.Remove(hit); picked.Insert(0, hit); }
        Pick([.. picked]);
        drag = (p, picked.Select(i => (i, BoxOf(i).X, BoxOf(i).Y)).ToArray());
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (lasso is { } l) { lasso = l with { To = p }; Redraw(); return; }
        if (drag is not { } d) return;
        int dx = (int)Math.Round((p.X - d.At.X) / Scale), dy = (int)Math.Round((p.Y - d.At.Y) / Scale);
        var (i0, x0, y0) = d.From[0];
        if (BoxOf(i0).X == x0 + dx && BoxOf(i0).Y == y0 + dy) return;
        MoveTo([.. d.From.Select(f => (f.I, f.X + dx, f.Y + dy))]);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        drag = null;
        if (lasso is not { } l) return;
        lasso = null;
        var (x0, y0, x1, y1) = LassoRect(l.From, l.To);
        var inside = Enumerable.Range(0, Count()).Where(i =>
        {
            var b = BoxOf(i);
            return b.X < x1 && b.X + b.Size > x0 && b.Y < y1 && b.Y + b.Size > y0;
        });
        Pick([.. (l.Add ? Picked().Concat(inside) : inside)]);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 8 : 1;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
        };
        if (dx != 0 || dy != 0) Nudge(dx, dy);
        else if (cmd && e.Key == Key.A) Pick([.. Enumerable.Range(0, Count())]);
        else if (e.Key is Key.Delete or Key.Back) Delete();
        else return;
        e.Handled = true;
    }

    /// <summary>The front-most tile under a view point — the earliest in the list — or -1.</summary>
    private int TileAt(Point p)
    {
        int px = (int)(p.X / Scale) - OriginX, py = (int)(p.Y / Scale) - OriginY;
        for (int i = 0; i < Count(); i++)
        {
            var b = BoxOf(i);
            if (px >= b.X && py >= b.Y && px < b.X + b.Size && py < b.Y + b.Size) return i;
        }
        return -1;
    }

    /// <summary>A lasso's corners as a cell-relative pixel rect, x0/y0 inclusive, x1/y1 exclusive.</summary>
    private static (int X0, int Y0, int X1, int Y1) LassoRect(Point a, Point b)
    {
        int ax = (int)Math.Floor(a.X / Scale) - OriginX, ay = (int)Math.Floor(a.Y / Scale) - OriginY;
        int bx = (int)Math.Floor(b.X / Scale) - OriginX, by = (int)Math.Floor(b.Y / Scale) - OriginY;
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx) + 1, Math.Max(ay, by) + 1);
    }

    private void Nudge(int dx, int dy)
    {
        var picked = Picked().ToList();
        if (picked.Count == 0) return;
        int primary = Primary();
        if (picked.Remove(primary)) picked.Insert(0, primary);   // the primary stays primary
        MoveTo([.. picked.Select(i => (i, BoxOf(i).X + dx, BoxOf(i).Y + dy))]);
    }

    /// <summary>Draw the tiles again, with the dashed placement cell, the pick and any lasso.</summary>
    public void Redraw()
    {
        image.Source = LevelBitmap.FromPixels(RenderTiles(PreviewPx, PreviewPx, OriginX, OriginY), PreviewPx, PreviewPx);
        overlay.Children.Clear();
        overlay.Children.Add(Box(OriginX, OriginY, 16, Scale, UiColors.Band, dashed: true));
        foreach (int i in Picked())
        {
            var b = BoxOf(i);
            overlay.Children.Add(Box(OriginX + b.X, OriginY + b.Y, b.Size, Scale, UiColors.Selection));
        }
        if (lasso is { } l)
        {
            var (x0, y0, x1, y1) = LassoRect(l.From, l.To);
            var r = new Rectangle { Width = (x1 - x0) * Scale, Height = (y1 - y0) * Scale,
                                    Stroke = UiColors.Selection, StrokeThickness = 1, StrokeDashArray = [4, 3], Fill = UiColors.SelectionFill };
            Canvas.SetLeft(r, (OriginX + x0) * Scale); Canvas.SetTop(r, (OriginY + y0) * Scale);
            overlay.Children.Add(r);
        }
    }

    /// <summary>A size-pixel square outline at (x, y), drawn at scale.</summary>
    public static Rectangle Box(int x, int y, int size, int scale, IBrush stroke, bool dashed = false)
    {
        var r = new Rectangle { Width = size * scale, Height = size * scale, Stroke = stroke, StrokeThickness = dashed ? 1 : 2 };
        if (dashed) r.StrokeDashArray = [4, 3];
        Canvas.SetLeft(r, x * scale); Canvas.SetTop(r, y * scale);
        return r;
    }
}
