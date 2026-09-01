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
    // Setting these RESHAPES the control, so they invalidate measure: a grid that switches
    // between sixteen wide and four keeps its old width otherwise, and the swatches spread out
    // to fill it rather than the strip getting narrower.
    public int Cols { get => cols; set { cols = value; InvalidateMeasure(); } }
    public int Rows { get => rows; set { rows = value; InvalidateMeasure(); } }
    private int cols = 16, rows = 16;
    public int Count => Cols * Rows;

    public double Cell { get; set; } = 28;

    /// <summary>The colours to draw, RGBA, row-major.</summary>
    public uint[] Colors { get; set; } = new uint[256];

    /// <summary>Which indices carry an edit, for the marker. Null = mark nothing.</summary>
    public Func<int, bool>? IsEdited { get; set; }

    /// <summary>Hover text for one swatch, as the ImGui grid showed it. Null = no tooltip.</summary>
    public Func<int, string>? Describe { get; set; }

    /// <summary>Swatches that cannot be chosen — a colour index this ROM's bit depth cannot
    /// store. Drawn under a veil and inert to clicks, rather than hidden: the colour is really
    /// there in CGRAM, and a row that stops at 8 reads as if the palette were 8 colours long.
    /// Null = everything is selectable.</summary>
    public Func<int, bool>? IsDisabled { get; set; }

    /// <summary>Print the index inside the swatch the pointer is over. For the paint row, where
    /// "which colour number is this" is the question asked constantly.</summary>
    public bool ShowHoverIndex { get; set; }

    /// <summary>Leave cell 0 empty — the Palette tab breaks the background colour out into its
    /// own swatch above the grid, so the grid must not offer it twice. Hidden, not shifted:
    /// every other index keeps its place.</summary>
    public bool HideFirst { get; set; }

    public int Selected { get; private set; } = -1;

    /// <summary>False for a grid that only SHOWS a palette — the Map16 gutter, where a tile picks
    /// a row rather than a colour. Clicks do nothing and nothing is ever ringed.</summary>
    public bool Selectable { get; set; } = true;

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
        int i = row * Cols + col;
        return HideFirst && i == 0 ? null : i;
    }

    protected override Size MeasureOverride(Size available) => new(Cols * Cell, Rows * Cell);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Selectable) return;
        Focus();
        if (IndexAt(e.GetPosition(this)) is not { } i) return;
        // A disabled swatch takes the click and does nothing with it — moving the ring there
        // would claim a paint colour that cannot be painted.
        if (IsDisabled?.Invoke(i) == true) return;
        Select(i);
        SelectionChanged?.Invoke(this, i);
    }

    private int hoverIndex = -1;

    /// <summary>Retarget the tooltip as the pointer crosses swatches. One tip on the control,
    /// rewritten on the way past, rather than 256 child controls to carry 256 tips.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int i = IndexAt(e.GetPosition(this)) ?? -1;
        if (i == hoverIndex) return;
        hoverIndex = i;
        if (Describe is not null) ToolTip.SetTip(this, i >= 0 ? Describe(i) : null);
        if (ShowHoverIndex) InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hoverIndex < 0) return;
        hoverIndex = -1;
        if (ShowHoverIndex) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double c = Cell;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
        var veil = new SolidColorBrush(Color.FromArgb(0xA8, 0x10, 0x12, 0x16));
        for (int i = 0; i < Count; i++)
        {
            if (HideFirst && i == 0) continue;
            var r = new Rect(i % Cols * c, i / Cols * c, c, c);
            uint v = i < Colors.Length ? Colors[i] : 0xFF000000u;
            // Colours arrive as 0xAABBGGRR from the composer; Avalonia wants them named.
            var swatch = UiColors.FromRgba(v);
            ctx.FillRectangle(new SolidColorBrush(swatch), r);
            bool off = IsDisabled?.Invoke(i) == true;
            if (off) ctx.FillRectangle(veil, r);
            ctx.DrawRectangle(null, grid, r);
            if (IsEdited?.Invoke(i) == true)
                ctx.FillRectangle(UiColors.Selection, new Rect(r.Right - 5, r.Top + 2, 3, 3));
            // The index, inside the swatch it names, in whichever of black/white the swatch
            // itself does not drown — a fixed ink colour vanishes on half a palette.
            if (ShowHoverIndex && i == hoverIndex)
            {
                var ink = off || Luminance(swatch) < 0.55 ? Brushes.White : Brushes.Black;
                var text = new FormattedText($"{i}", System.Globalization.CultureInfo.InvariantCulture,
                                             FlowDirection.LeftToRight, Typeface.Default, c * 0.55, ink);
                ctx.DrawText(text, new Point(r.Center.X - text.Width / 2, r.Center.Y - text.Height / 2));
            }
        }
        if (Selected >= 0 && Selected < Count && !(HideFirst && Selected == 0))
        {
            var sel = new Rect(Selected % Cols * c, Selected / Cols * c, c, c);
            // Black under white: a ring in one colour disappears against a swatch of that colour.
            ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), sel);
            ctx.DrawRectangle(null, new Pen(Brushes.White, 1.5), sel);
        }
    }

    /// <summary>Perceived brightness, 0-1. Rec.601 weights: cheap, and the question is only
    /// "does black or white read on this", which it answers as well as anything costlier.</summary>
    internal static double Luminance(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
