namespace PipeDream.Services;

// EditorSession — the overworld's layer 2 events as the Events tab reads them: which event laid
// the piece under a canvas cell, an event's steps, and a piece's picture for the step list. The
// steps themselves are Overworld.EventSteps; nothing here writes yet.

public partial class EditorSession
{
    /// <summary>A step's footprint in canvas cells — its piece's cells, on the map it lands on,
    /// placed as the canvas places that map (the lower one shifted, as Lunar Magic draws it).</summary>
    public static (int X, int Y, int W, int H) OwEventFoot(Overworld.EventStep s)
        => (s.Cx + (s.SubmapMap ? OwSubDx : 0), s.Cy + (s.SubmapMap ? 2 * Overworld.Rows + OwSubDy : 0), s.Size, s.Size);

    /// <summary>The event whose piece is under a canvas cell, or -1. The LAST step covering the
    /// cell wins: events play in order, and a later piece lands on top of an earlier one.</summary>
    public int OwEventAt(int col, int row)
    {
        if (Overworld is not { } ow) return -1;
        int found = -1;
        foreach (var s in ow.EventSteps)
        {
            var f = OwEventFoot(s);
            if (col >= f.X && col < f.X + f.W && row >= f.Y && row < f.Y + f.H) found = s.Event;
        }
        return found;
    }

    /// <summary>An event's steps, in the order the game lays them.</summary>
    public IEnumerable<Overworld.EventStep> OwEventSteps(int ev)
        => Overworld?.EventSteps.Where(s => s.Event == ev) ?? [];

    /// <summary>A piece as it lands: its cells' pictures in the colours of the submap the step
    /// puts it on, as one square image <c>Size * 8</c> pixels across.</summary>
    public uint[] OwEventStepImage(Overworld.EventStep s)
    {
        int side = s.Size * 8;
        var img = new uint[side * side];
        if (Overworld is not { } ow) return img;
        int submap = Overworld.SubmapAt(s.Cx >> 1, s.Cy >> 1, s.SubmapMap);
        for (int r = 0; r < s.Size; r++)
            for (int c = 0; c < s.Size; c++)
            {
                int word = ow.EventPieces[s.Piece + r * s.Size + c];
                var px = word == Overworld.BlankEventWord ? ow.FillerPixels() : ow.TilePixels(word, submap);
                for (int y = 0; y < 8; y++) Array.Copy(px, y * 8, img, (r * 8 + y) * side + c * 8, 8);
            }
        return img;
    }

    /// <summary>The submap a step lands on, by name.</summary>
    public static string OwEventStepPlace(Overworld.EventStep s)
        => OwSubmapNames[Overworld.SubmapAt(s.Cx >> 1, s.Cy >> 1, s.SubmapMap)];
}
