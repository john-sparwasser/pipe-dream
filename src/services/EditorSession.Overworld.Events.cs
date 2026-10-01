namespace PipeDream.Services;

// EditorSession — the overworld's layer 2 events as the Events tab reads and edits them: which
// event laid the piece under a canvas cell, an event's steps, a piece's picture for the step list,
// and adding, moving, replacing or deleting a step, or clearing an event. The steps themselves are Overworld.EventSteps.

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

    /// <summary>The events with no steps, in number order: the ones New event offers.</summary>
    public IEnumerable<int> OwEmptyEvents()
        => Enumerable.Range(0, Overworld.EventCount).Where(ev => !OwEventSteps(ev).Any());

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

    /// <summary>The first few pieces an event lays, side by side, for the drawer's list: each as it
    /// lands, in a square slot <see cref="OwPreviewSlot"/> pixels across (a 2x2 drawn three times
    /// over so it reads beside a 6x6), a few transparent pixels apart. Null for an event with none.</summary>
    public (uint[] Px, int W, int H)? OwEventPreview(int ev, int count)
    {
        var steps = OwEventSteps(ev).Take(count).ToList();
        if (steps.Count == 0) return null;
        const int slot = OwPreviewSlot, gap = 4;
        int w = steps.Count * (slot + gap) - gap;
        var px = new uint[w * slot];
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var img = OwEventStepImage(s);
            int side = s.Size * 8, k = slot / side;
            for (int y = 0; y < slot; y++)
                for (int x = 0; x < slot; x++)
                    px[y * w + i * (slot + gap) + x] = img[y / k * side + x / k];
        }
        return (px, w, slot);
    }

    /// <summary>A preview slot's side: a 6x6 piece at its own size.</summary>
    public const int OwPreviewSlot = 48;

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
        OwEventSnapshot();
        ow.AddEventStep(new Overworld.EventStep(ev, src, cx, cy, sub, size));
        OwEventStepsChanged();
        return null;
    }

    /// <summary>Take every step off an event; how many went. Undoable, like every other edit.</summary>
    public int OwClearEvent(int ev)
    {
        if (Overworld is not { } ow || !ow.EventSteps.Any(s => s.Event == ev)) return 0;
        OwEventSnapshot();
        int gone = ow.ClearEvent(ev);
        OwEventStepsChanged();
        return gone;
    }

    /// <summary>Move an event's i-th step to place j, one place up or down its order; false when
    /// either is not one of its steps (the top step has nowhere up to go).</summary>
    public bool OwMoveEventStep(int ev, int i, int j)
    {
        if (Overworld is not { } ow || !OwIsEventStep(ev, i) || !OwIsEventStep(ev, j) || i == j) return false;
        OwEventSnapshot();
        ow.SwapEventSteps(ev, i, j);
        OwEventStepsChanged();
        return true;
    }

    /// <summary>Take one step off an event. Undoable.</summary>
    public bool OwDeleteEventStep(int ev, int i)
    {
        if (Overworld is not { } ow || !OwIsEventStep(ev, i)) return false;
        OwEventSnapshot();
        ow.RemoveEventStep(ev, i);
        OwEventStepsChanged();
        return true;
    }

    /// <summary>
    /// Swap the piece an event's i-th step lays for another, keeping where it lands and its place
    /// in the order. A reason when it cannot: a bigger piece would hang off the map's edge.
    /// </summary>
    public string? OwReplaceEventStep(int ev, int i, int src, int size)
    {
        if (Overworld is not { } ow || !OwIsEventStep(ev, i)) return "no such step";
        var s = OwEventSteps(ev).ElementAt(i);
        if (s.Cx + size > 2 * Overworld.Cols || s.Cy + size > 2 * Overworld.Rows) return $"a {size}x{size} piece does not fit there — it would hang off the map";
        OwEventSnapshot();
        ow.SetEventStep(ev, i, s with { Piece = src, Size = size });
        OwEventStepsChanged();
        return null;
    }

    private bool OwIsEventStep(int ev, int i) => i >= 0 && i < OwEventSteps(ev).Count();

    // The step table's history: whole lists, because an edit is one step in a few hundred and a
    // copy is cheaper than describing the difference. Its own stacks rather than a TilemapEdit's,
    // since the steps are not a grid; the window sends Ctrl+Z here while the Events tab is up.
    private readonly Stack<List<Overworld.EventStep>> owEventUndo = new(), owEventRedo = new();

    public bool OwCanUndoEvent => owEventUndo.Count > 0;
    public bool OwCanRedoEvent => owEventRedo.Count > 0;

    private void OwEventSnapshot()
    {
        if (Overworld is not { } ow) return;
        owEventUndo.Push([.. ow.EventSteps]);
        owEventRedo.Clear();
    }

    public bool OwEventUndo() => OwEventMove(owEventUndo, owEventRedo);
    public bool OwEventRedo() => OwEventMove(owEventRedo, owEventUndo);

    private bool OwEventMove(Stack<List<Overworld.EventStep>> from, Stack<List<Overworld.EventStep>> to)
    {
        if (from.Count == 0 || Overworld is not { } ow) return false;
        to.Push([.. ow.EventSteps]);
        ow.ReplaceEventSteps(from.Pop());
        OwEventStepsChanged();
        return true;
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
