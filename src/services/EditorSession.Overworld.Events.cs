namespace PipeDream.Services;

// EditorSession — the overworld's layer 2 events as the Events tab reads and edits them: which
// event laid the piece under a canvas cell, an event's steps, a piece's picture for the step list,
// and adding a step or clearing an event. The steps themselves are Overworld.EventSteps.

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

    /// <summary>
    /// Lay a piece as a new step of an event, its top-left at a canvas cell. The last thing the
    /// event does, since events play in table order. A reason when it cannot: the cell is not on
    /// a map, or the piece would hang off the map's edge (the game's writer wraps into the wrong
    /// screen there). Room is the build's concern — vanilla's table is full and the writer
    /// moves it (Overworld.WriteEventSteps).
    /// </summary>
    public string? OwAddEventStep(int ev, int src, int size, int col, int row)
    {
        if (Overworld is not { } ow) return "no overworld";
        if (!OwMapCell(col, row, out int cx, out int cy, out bool sub)) return "a piece has to land on one of the maps";
        if (cx + size > 2 * Overworld.Cols || cy + size > 2 * Overworld.Rows) return $"a {size}x{size} piece does not fit there — it would hang off the map";
        ow.AddEventStep(new Overworld.EventStep(ev, src, cx, cy, sub, size));
        OwEventStepsChanged();
        return null;
    }

    /// <summary>Take every step off an event; how many went.</summary>
    public int OwClearEvent(int ev)
    {
        int gone = Overworld?.ClearEvent(ev) ?? 0;
        if (gone > 0) OwEventStepsChanged();
        return gone;
    }

    private void OwEventStepsChanged()
    {
        if (Project is null || Overworld is not { } ow) return;
        Project.Data.Overworld.EventSteps = Convert.ToBase64String(Overworld.PackEventSteps(ow.EventSteps));
        Project.MarkDirty();
    }

    /// <summary>The submap a step lands on, by name.</summary>
    public static string OwEventStepPlace(Overworld.EventStep s)
        => OwSubmapNames[Overworld.SubmapAt(s.Cx >> 1, s.Cy >> 1, s.SubmapMap)];
}
