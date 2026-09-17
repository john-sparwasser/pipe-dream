using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PipeDream.Ui;

// The Events tab: the layer 2 event pieces in the left drawer, picked whole; the events on the
// map, picked by clicking one of their pieces; and the selected event's steps in a drawer on the
// right. Laying a piece as a new step is still to come. The rest of the overworld mode is in
// MainWindow.Overworld.cs and its other partials.

public partial class MainWindow
{
    private Border owEventPanel = null!;
    private TextBlock owEventTitle = null!;
    private StackPanel owEventStepsList = null!;

    /// <summary>The event picked on the map, or null. Its pieces wear the selection ring, and
    /// its steps fill the right drawer.</summary>
    private int? owEvent;

    private void WireOwEvents()
    {
        owEventPanel = this.GetControl<Border>("OwEventPanel");
        owEventTitle = this.GetControl<TextBlock>("OwEventTitle");
        owEventStepsList = this.GetControl<StackPanel>("OwEventSteps");
    }

    /// <summary>A click on the Events tab's map picks the event whose piece is there; a click
    /// on bare land picks none. The map has no reticle and no lasso on this tab (PickOnLeft): a
    /// piece is a thing to point at, not a cell to paint.</summary>
    private void OwEventClicked((int Col, int Row) cell) => OwSelectEvent(session.OwEventAt(cell.Col, cell.Row));

    private void OwSelectEvent(int ev)
    {
        owEvent = ev < 0 ? null : ev;
        RefreshOwEventPanel();
        owView.InvalidateVisual();                  // the ring moves; the cells do not
        RefreshOwNote();
    }

    /// <summary>The right drawer: the selected event's steps in the order the game lays them —
    /// each piece as it lands, in the colours of the submap it lands on, with where. Hidden when
    /// no event is selected, so the map has the width when nothing is being read off it.</summary>
    private void RefreshOwEventPanel()
    {
        bool show = OwModeNow == OwMode.Events && owEvent is { } ev;
        owEventPanel.IsVisible = show;
        owEventStepsList.Children.Clear();
        if (!show) return;
        var steps = session.OwEventSteps(owEvent!.Value).ToList();
        owEventTitle.Text = $"Event {owEvent:X2} — {steps.Count} step{(steps.Count == 1 ? "" : "s")}";
        for (int i = 0; i < steps.Count; i++) owEventStepsList.Children.Add(OwEventStepRow(i, steps[i]));
    }

    /// <summary>One step: its number, the piece as it lands, and where it lands.</summary>
    private Control OwEventStepRow(int i, Overworld.EventStep s)
    {
        var img = session.OwEventStepImage(s);
        int side = s.Size * 8;
        var picture = new PixelImage { Source = LevelBitmap.FromPixels(img, side, side), Zoom = 2, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = $"{i + 1}.  {s.Size}x{s.Size} piece 0x{s.Piece:X3}" });
        var place = new TextBlock { Text = $"{EditorSession.OwEventStepPlace(s)}  ({s.Cx >> 1}, {(s.Cy >> 1) % Overworld.Rows})" };
        place.Classes.Add("dim");
        text.Children.Add(place);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(picture);
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(4),
                            Background = (IBrush)this.FindResource("SurfaceBrush")! };
    }

    /// <summary>The armed piece, as an event step would name it: its source offset, where its
    /// top-left sits in the event area, and its side. Null until one is picked.</summary>
    private (int Src, int X, int Y, int Size)? owEventPiece;

    /// <summary>
    /// The drawer as the event pieces area, laid out as the Tiles canvas lays it out (the 6x6s
    /// above the 2x2s), with a pick snapping to the piece under the pointer — six cells square in
    /// the upper band, two in the lower — so the brush is always exactly one piece, never a
    /// corner of two. The cells the short last band leaves belong to no piece and pick nothing.
    /// </summary>
    private void ShowOwEventSheet()
    {
        owSheet.CellAt = (c, r) => Overworld.EventIndexOf(c, r);
        owSheet.CellPixels = session.OwEventPiecePixels;
        owSheet.Snap = (c, r) => Overworld.EventPieceAt(c, r) is { } p ? (p.X, p.Y, p.Size, p.Size) : (c, r, 1, 1);
        owSheet.PickWhole = true;
        owSheet.Backdrop = 0;                       // the desk shows through where no piece is
        owSheet.Selected = null;
        owSheet.Reshape(Overworld.EventArea.Cols, Overworld.EventArea.Rows, 8);
    }

    /// <summary>The other tabs' sheets are grids of single tiles again.</summary>
    private void LeaveOwEventSheet()
    {
        owSheet.Snap = null;
        owSheet.PickWhole = false;
        owSheet.Backdrop = 0xFF303030u;
    }

    /// <summary>A pick on the events sheet arms the piece it landed on.</summary>
    private void OwEventPicked((int X, int Y, int W, int H) r)
    {
        owEventPiece = Overworld.EventPieceAt(r.X, r.Y);
        if (owEventPiece is null) owSheet.ClearSelection();
    }
}
