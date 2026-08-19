using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// The level's CGRAM as a 16x16 grid of swatches: rows 0-7 are background and foreground, 8-F
/// are sprites. Click one to edit it.
///
/// Edited entries are marked, because an edit is invisible otherwise — a colour nudged by one
/// step looks like the original, and "which of these 256 did I change" is the question you ask
/// immediately afterwards.
/// </summary>
public class PaletteGridView : Control
{
    public const int Cols = 16, Count = 256;

    public double Cell { get; set; } = 28;

    /// <summary>The 256 colours to draw, RGBA.</summary>
    public uint[] Colors { get; set; } = new uint[Count];

    /// <summary>Which indices carry an edit, for the marker.</summary>
    public Func<int, bool>? IsEdited { get; set; }

    public int Selected { get; private set; } = -1;

    public event EventHandler<int>? SelectionChanged;

    public PaletteGridView() => Focusable = true;

    public void Select(int index)
    {
        Selected = index is >= 0 and < Count ? index : -1;
        InvalidateVisual();
    }

    public int? IndexAt(Point p)
    {
        int col = (int)(p.X / Cell), row = (int)(p.Y / Cell);
        if (col is < 0 or >= Cols || row is < 0 or >= Count / Cols) return null;
        return row * Cols + col;
    }

    protected override Size MeasureOverride(Size available) => new(Cols * Cell, Count / Cols * Cell);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (IndexAt(e.GetPosition(this)) is not { } i) return;
        Select(i);
        SelectionChanged?.Invoke(this, i);
    }

    public override void Render(DrawingContext ctx)
    {
        double c = Cell;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
        for (int i = 0; i < Count; i++)
        {
            var r = new Rect(i % Cols * c, i / Cols * c, c, c);
            uint v = i < Colors.Length ? Colors[i] : 0xFF000000u;
            // Colors are 0xAABBGGRR from the composer; Avalonia wants the channels named.
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(v & 0xFF),
                                                               (byte)((v >> 8) & 0xFF),
                                                               (byte)((v >> 16) & 0xFF))), r);
            ctx.DrawRectangle(null, grid, r);
            if (IsEdited?.Invoke(i) == true)
                ctx.FillRectangle(UiColors.Selection, new Rect(r.Right - 5, r.Top + 2, 3, 3));
        }
        if (Selected >= 0)
        {
            var sel = new Rect(Selected % Cols * c, Selected / Cols * c, c, c);
            // Black under white: a ring in one colour disappears against a swatch of that colour.
            ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), sel);
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), sel);
        }
    }
}
