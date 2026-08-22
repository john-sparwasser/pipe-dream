using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PipeDream.Ui;

/// <summary>
/// The GFX canvas mode: one GFX file's 8x8 tiles as an editable pixel sheet, 16 tiles per row.
///
/// Controls match the ImGui editor's GFX mode exactly:
///
///   LEFT drag     the current tool acts (pencil, fill, eraser; the eyedropper reads)
///   RIGHT click   eyedrop the colour under the cursor, whatever the tool
///   F             cycle the tools
///   [ ]           zoom out / in
///   up / down     step the palette row
///
/// Note the difference from the level canvas, where right-drag paints and left selects. There is
/// nothing to select here — every pixel belongs to the sheet — so this mode uses the ordinary
/// paint-program bindings, and that is what the ImGui version does too.
/// </summary>
public class GfxCanvasView : Control
{
    /// <summary>Screen pixels per GFX pixel.</summary>
    public double Zoom { get; set; } = 8;

    /// <summary>Tiles in the file, so the canvas can stop responding past the end of the sheet —
    /// the last row is usually part empty.</summary>
    public int Tiles { get; set; }

    /// <summary>Raised for every sheet pixel a drag passes through.</summary>
    public event EventHandler<(int X, int Y)>? PixelPainted;
    public event EventHandler? StrokeEnded;

    /// <summary>Right-click: the colour index under the cursor was picked.</summary>
    public event EventHandler<(int X, int Y)>? ColorPicked;

    /// <summary>Keys the mode owns: F cycles the tool, [ and ] zoom, up/down step the palette row.
    /// They live here rather than on the window because the window never sees them — the top level
    /// eats arrow keys for focus navigation before a bubbling handler runs.</summary>
    public event EventHandler? ToolToggled;
    public event EventHandler<int>? ZoomStepped;
    public event EventHandler<int>? PalRowStepped;

    private WriteableBitmap? sheet;
    private int sheetW, sheetH;

    public (int X, int Y)? Hover { get; private set; }

    public GfxCanvasView() => Focusable = true;

    public void SetSheet(uint[] px, int w, int h)
    {
        sheetW = w; sheetH = h;
        sheet?.Dispose();
        sheet = w > 0 && h > 0 ? LevelBitmap.FromPixels(px, w, h) : null;
        InvalidateVisual();
        InvalidateMeasure();
    }

    /// <summary>Screen point → sheet pixel, or null outside the tiles the file actually has.</summary>
    public (int X, int Y)? PixelAt(Point p)
    {
        if (Zoom <= 0 || sheetW == 0) return null;
        int x = (int)(p.X / Zoom), y = (int)(p.Y / Zoom);
        if (x < 0 || y < 0 || x >= sheetW || y >= sheetH) return null;
        return (y / 8) * 16 + x / 8 < Tiles ? (x, y) : null;
    }

    protected override Size MeasureOverride(Size available) => new(sheetW * Zoom, sheetH * Zoom);

    private bool painting;
    private (int X, int Y)? lastPainted;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (PixelAt(e.GetPosition(this)) is not { } px) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { ColorPicked?.Invoke(this, px); return; }
        if (!props.IsLeftButtonPressed) return;

        painting = true;
        lastPainted = px;
        e.Pointer.Capture(this);
        PixelPainted?.Invoke(this, px);
    }

    /// <summary>The hover ends with the pointer, as on the level canvas.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); Hover = null; InvalidateVisual(); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = PixelAt(e.GetPosition(this));
        if (at != Hover) { Hover = at; InvalidateVisual(); }
        if (!painting || at is not { } px) return;
        // Interpolate: at speed the pointer skips pixels, and a stroke with gaps in it is a bug
        // rather than a style. Same rule as the level canvas.
        if (lastPainted is { } prev)
            foreach (var step in Between(prev, px)) PixelPainted?.Invoke(this, step);
        else PixelPainted?.Invoke(this, px);
        lastPainted = px;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!painting) return;
        painting = false;
        lastPainted = null;
        e.Pointer.Capture(null);
        StrokeEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.F: ToolToggled?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
            case Key.OemOpenBrackets: ZoomStepped?.Invoke(this, -1); e.Handled = true; break;
            case Key.OemCloseBrackets: ZoomStepped?.Invoke(this, 1); e.Handled = true; break;
            case Key.Up: PalRowStepped?.Invoke(this, -1); e.Handled = true; break;
            case Key.Down: PalRowStepped?.Invoke(this, 1); e.Handled = true; break;
        }
    }

    private static IEnumerable<(int X, int Y)> Between((int X, int Y) a, (int X, int Y) b)
    {
        int steps = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (steps == 0) { yield return b; yield break; }
        for (int i = 1; i <= steps; i++)
            yield return (a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
    }

    public override void Render(DrawingContext ctx)
    {
        double z = Zoom;
        var full = new Rect(0, 0, sheetW * z, sheetH * z);
        ctx.FillRectangle(Brushes.Black, full);
        if (sheet is null) return;
        ctx.DrawImage(sheet, new Rect(0, 0, sheetW, sheetH), full);

        // The 8x8 tile grid always; the per-pixel grid only once a pixel is big enough to see
        // it round, otherwise the lines are the picture.
        var tileLine = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        var pixLine = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
        int step = z >= 8 ? 1 : 8;
        for (int x = 0; x <= sheetW; x += step)
            ctx.DrawLine((x & 7) == 0 ? tileLine : pixLine, new Point(x * z, 0), new Point(x * z, full.Height));
        for (int y = 0; y <= sheetH; y += step)
            ctx.DrawLine((y & 7) == 0 ? tileLine : pixLine, new Point(0, y * z), new Point(full.Width, y * z));

        if (Hover is not { } h) return;
        ctx.DrawRectangle(null, new Pen(UiColors.Selection, 1.5), new Rect(h.X * z, h.Y * z, z, z));
        ctx.DrawRectangle(null, new Pen(UiColors.Band, 1),
                          new Rect((h.X & ~7) * z, (h.Y & ~7) * z, 8 * z, 8 * z));
    }
}
