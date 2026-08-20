using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// A clickable grid of colour swatches. Two uses, which is why the shape is a property rather
/// than a constant: the whole of CGRAM as 16x16 in the Palette tab (rows 0-7 background and
/// foreground, 8-F sprites), and a single palette row as 16x1 as the GFX editor's paint colours.
///
/// Edited entries are marked, because an edit is invisible otherwise — a colour nudged by one
/// step looks like the original, and "which of these did I change" is the question you ask
/// immediately afterwards.
/// </summary>
public class PaletteGridView : Control
{
    public int Cols { get; set; } = 16;
    public int Rows { get; set; } = 16;
    public int Count => Cols * Rows;

    public double Cell { get; set; } = 28;

    /// <summary>The colours to draw, RGBA, row-major.</summary>
    public uint[] Colors { get; set; } = new uint[256];

    /// <summary>Which indices carry an edit, for the marker. Null = mark nothing.</summary>
    public Func<int, bool>? IsEdited { get; set; }

    /// <summary>Hover text for one swatch, as the ImGui grid showed it. Null = no tooltip.</summary>
    public Func<int, string>? Describe { get; set; }

    public int Selected { get; private set; } = -1;

    public event EventHandler<int>? SelectionChanged;

    public PaletteGridView() => Focusable = true;

    public void Select(int index)
    {
        Selected = index >= 0 && index < Count ? index : -1;
        InvalidateVisual();
    }

    public int? IndexAt(Point p)
    {
        int col = (int)(p.X / Cell), row = (int)(p.Y / Cell);
        if (col < 0 || col >= Cols || row < 0 || row >= Rows) return null;
        return row * Cols + col;
    }

    protected override Size MeasureOverride(Size available) => new(Cols * Cell, Rows * Cell);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (IndexAt(e.GetPosition(this)) is not { } i) return;
        Select(i);
        SelectionChanged?.Invoke(this, i);
    }

    private int hoverIndex = -1;

    /// <summary>Retarget the tooltip as the pointer crosses swatches. One tip on the control,
    /// rewritten on the way past, rather than 256 child controls to carry 256 tips.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Describe is null) return;
        int i = IndexAt(e.GetPosition(this)) ?? -1;
        if (i == hoverIndex) return;
        hoverIndex = i;
        ToolTip.SetTip(this, i >= 0 ? Describe(i) : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverIndex = -1;
    }

    public override void Render(DrawingContext ctx)
    {
        double c = Cell;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
        for (int i = 0; i < Count; i++)
        {
            var r = new Rect(i % Cols * c, i / Cols * c, c, c);
            uint v = i < Colors.Length ? Colors[i] : 0xFF000000u;
            // Colours arrive as 0xAABBGGRR from the composer; Avalonia wants them named.
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(v & 0xFF),
                                                               (byte)((v >> 8) & 0xFF),
                                                               (byte)((v >> 16) & 0xFF))), r);
            ctx.DrawRectangle(null, grid, r);
            if (IsEdited?.Invoke(i) == true)
                ctx.FillRectangle(UiColors.Selection, new Rect(r.Right - 5, r.Top + 2, 3, 3));
        }
        if (Selected >= 0 && Selected < Count)
        {
            var sel = new Rect(Selected % Cols * c, Selected / Cols * c, c, c);
            // Black under white: a ring in one colour disappears against a swatch of that colour.
            ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), sel);
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), sel);
        }
    }
}
