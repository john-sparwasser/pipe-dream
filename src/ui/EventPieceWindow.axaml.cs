using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// Pick one event piece — what Add lays and Replace swaps in on the Events tab. The pieces area
/// laid out as the Tiles canvas lays it out (the 6x6s above the 2x2s), with a pick snapping to
/// the piece under the pointer, so the choice is always exactly one piece, never a corner of two.
/// The cells the short last band leaves belong to no piece and pick nothing. The piece the step
/// lays now, if there is one, wears the armed halo, and the piece under the pointer the reticle — the
/// exact cells a click takes. A pick on a piece IS the choice: the window
/// closes with it. <see cref="Result"/> is null when cancelled.
/// </summary>
public partial class EventPieceWindow : Window
{
    /// <summary>The piece chosen, as an event step names it: its source offset and its side.</summary>
    public (int Src, int Size)? Result { get; private set; }

    private TilemapView sheet = null!;

    public EventPieceWindow() => AvaloniaXamlLoader.Load(this);

    public EventPieceWindow(EditorSession session, string title, string prompt, int? current) : this()
    {
        Title = title;
        this.GetControl<TextBlock>("Prompt").Text = prompt;
        sheet = this.GetControl<TilemapView>("Sheet");
        sheet.CellAt = (c, r) => Overworld.EventIndexOf(c, r);
        sheet.CellPixels = session.OwEventPiecePixels;
        sheet.Snap = (c, r) => Overworld.EventPieceAt(c, r) is { } p ? (p.X, p.Y, p.Size, p.Size) : (c, r, 1, 1);
        sheet.PickWhole = true;
        sheet.PickOnLeft = true;
        sheet.LassoPicks = true;
        sheet.Backdrop = 0;                       // the desk shows through where no piece is
        sheet.Zoom = 2;
        sheet.RepaintOnHover = true;              // a pick sheet draws no reticle of its own
        sheet.Decorate = (ctx, step) =>
        {
            if (current is { } src)
            {
                var (x, y) = Overworld.EventCellOf(src);
                int side = src < Overworld.Event6Bytes ? Overworld.Event6Size : Overworld.Event2Size;
                Overlay.Armed(ctx, new Rect(x * step, y * step, side * step, side * step));
            }
            // The whole piece a click would take, under the pointer; nothing over the desk, where
            // a click takes nothing.
            // Lit and ringed, as the GFX canvas marks its pointer: the edge says which cells.
            if (sheet.Hover is { } h && Overworld.EventPieceAt(h.Col, h.Row) is { } p)
            {
                var r = new Rect(p.X * step, p.Y * step, p.Size * step, p.Size * step);
                Overlay.Hover(ctx, r);
                Overlay.Outline(ctx, r);
            }
        };
        sheet.Reshape(Overworld.EventArea.Cols, Overworld.EventArea.Rows, 8);
        sheet.BlockPicked += (_, r) => Pick(Overworld.EventPieceAt(r.X, r.Y));
        sheet.Picked += (_, c) => Pick(Overworld.EventPieceAt(c.Col, c.Row));
    }

    /// <summary>A pick on a piece takes it and closes; one on the desk between pieces takes nothing
    /// and leaves the window up.</summary>
    private void Pick((int Src, int X, int Y, int Size)? p)
    {
        if (p is not { } q) { sheet.ClearSelection(); return; }
        Result = (q.Src, q.Size);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
