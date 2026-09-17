using Avalonia.Controls;

namespace PipeDream.Ui;

// The Events tab's drawer: the layer 2 event pieces, picked whole. The canvas side of the event
// editor — laying a piece as a step of an event — is still to come; what is here is the palette.
// The rest of the overworld mode is in MainWindow.Overworld.cs and its other partials.

public partial class MainWindow
{
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
