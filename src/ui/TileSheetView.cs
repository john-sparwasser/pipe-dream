using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PipeDream.Ui;

/// <summary>
/// A sheet of square tiles, Columns to a row, to point at and pick from: hovering outlines the
/// tile (HoverCells wide) and reports it, a click picks it. <see cref="Marks"/> is the host's, for
/// outlining what it uses. The custom sprite editor picks its Map16 and 8x8 tiles from these.
/// </summary>
public sealed class TileSheetView : Panel
{
    public int CellPx { get; set; } = 8;
    public int Scale { get; set; } = 2;
    public int Columns { get; set; } = 16;
    /// <summary>How many cells wide the hover outline is: what a click will put down.</summary>
    public int HoverCells { get; set; } = 1;

    public event Action<int>? Picked;
    public event Action<int?>? Hovered;

    /// <summary>The host's own outlines, in sheet pixels × Scale.</summary>
    public Canvas Marks { get; } = new() { IsHitTestVisible = false };
    private readonly PixelImage image = new() { Stretch = true };
    private readonly Canvas hover = new() { IsHitTestVisible = false };
    private int rows;

    public TileSheetView()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Background = Avalonia.Media.Brushes.Transparent;
        Children.Add(image);
        Children.Add(Marks);
        Children.Add(hover);
        PointerMoved += (_, e) => Hover(TileAt(e.GetPosition(this)));
        PointerExited += (_, _) => Hover(null);
        PointerPressed += (_, e) => { if (TileAt(e.GetPosition(this)) is { } t) Picked?.Invoke(t); };
    }

    /// <summary>Show a w×h sheet of tiles.</summary>
    public void Show(uint[] px, int w, int h)
    {
        rows = w == 0 ? 0 : h / CellPx;
        image.Source = px.Length == 0 ? null : LevelBitmap.FromPixels(px, w, h);
        Width = w * Scale;
        Height = h * Scale;
    }

    /// <summary>Outline tile t, cells wide, on the host's marks.</summary>
    public void Mark(int t, int cells, Avalonia.Media.IBrush stroke)
        => Marks.Children.Add(TileArrangeView.Box(t % Columns * CellPx, t / Columns * CellPx, cells * CellPx, Scale, stroke));

    private int? TileAt(Point p)
    {
        int cx = (int)(p.X / (CellPx * Scale)), cy = (int)(p.Y / (CellPx * Scale));
        return p.X >= 0 && p.Y >= 0 && cx < Columns && cy < rows ? cy * Columns + cx : null;
    }

    private void Hover(int? t)
    {
        hover.Children.Clear();
        if (t is { } n) hover.Children.Add(TileArrangeView.Box(n % Columns * CellPx, n / Columns * CellPx, HoverCells * CellPx, Scale, UiColors.Selection));
        Hovered?.Invoke(t);
    }
}
