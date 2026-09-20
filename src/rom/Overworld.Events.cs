namespace PipeDream;

// Overworld — layer 2 events: the pieces an event lays on the land, step by step ($04E496).

public sealed partial class Overworld
{
    /// <summary>
    /// One standard step of a layer 2 event: a piece written onto the land, one per frame with
    /// a sound while the event plays. A step is 4 bytes, [src][dst]: src at or past 0x900 is a
    /// 2x2 piece (one 16x16), below that a 6x6 (three); dst is a byte offset into the layer 2
    /// buffer at $7F4000, whose words sit in the same screen order as <see cref="Layer2Index"/>,
    /// so cells here are 8x8 columns and rows of the map the submap lives on. Event N owns steps
    /// ends[N-1]..ends[N] of the cumulative table at $04E35B (the word before it is 0). Lunar
    /// Magic leaves that table in place and relocates the steps (long operand at $04E49E).
    /// </summary>
    public readonly record struct EventStep(int Event, int Piece, int Cx, int Cy, bool SubmapMap, int Size);

    public const int EventStepEnds = 0x04E35B, EventStepTable = 0x04DD8D, EventCount = 0x78;

    /// <summary>The steps, in table order — the ROM's edited copy, so an added step shows on the
    /// map and goes into the build (<see cref="WriteEventSteps"/>).</summary>
    public IReadOnlyList<EventStep> EventSteps => Rom.OwEventSteps ??= ReadEventSteps();

    /// <summary>Append a step to an event. Events play in table order, so a new step is the last
    /// thing its event lays; the list stays grouped by event, which is the shape the table has.</summary>
    public void AddEventStep(EventStep step)
    {
        var list = (List<EventStep>)EventSteps;
        int at = list.FindLastIndex(s => s.Event <= step.Event);
        list.Insert(at + 1, step);
    }

    /// <summary>Take every step off an event. The event still exists — the table has a slot for
    /// all 0x78 — it just lays nothing.</summary>
    public int ClearEvent(int ev) => ((List<EventStep>)EventSteps).RemoveAll(s => s.Event == ev);

    /// <summary>Put a whole list back — what undo does — into the same list object the map and
    /// the drawers read from, so nothing that holds it has to be told.</summary>
    public void ReplaceEventSteps(IEnumerable<EventStep> steps)
    {
        var list = (List<EventStep>)EventSteps;
        list.Clear();
        list.AddRange(steps);
    }

    /// <summary>Where the steps table is, in the file: Lunar Magic relocates it and repoints the
    /// long read at $04E49E; vanilla's sits at <see cref="EventStepTable"/>.</summary>
    private static int EventStepsPc(Rom rom)
    {
        var d = rom.Data;
        int p = rom.FileOffset(StepReadPiece);
        return d[p] == 0xBF ? rom.FileOffset(d[p + 1] | d[p + 2] << 8 | d[p + 3] << 16) : rom.FileOffset(EventStepTable);
    }

    /// <summary>
    /// How many steps the table has room for. Vanilla's runs from <see cref="EventStepTable"/>
    /// up to the word before the ends table — 371 steps. A table Lunar Magic relocated sits in a
    /// RATS block, and the block's end is its room. A table found in neither place can hold what
    /// it holds and no more, since nothing says what follows it.
    /// </summary>
    public static int EventStepCapacity(Rom rom)
    {
        int pc = EventStepsPc(rom);
        if (pc == rom.FileOffset(EventStepTable)) return (rom.FileOffset(EventStepEnds) - 2 - pc) / 4;
        int pcRom = pc - rom.HeaderOffset;
        foreach (var rat in RatsWriter.EnumerateRats(rom))
            if (pcRom >= rat.PcOffset + 8 && pcRom < rat.PcOffset + 8 + rat.Size) return (rat.PcOffset + 8 + rat.Size - pcRom) / 4;
        return new Overworld(rom).EventSteps.Count;
    }

    /// <summary>A step's destination byte, the inverse of the decode in <see cref="ReadEventSteps"/>:
    /// screen bits, then row and column within the screen, doubled for a word offset, and 0x2000
    /// for the submap map's buffer.</summary>
    public static int EncodeEventDst(int cx, int cy, bool submapMap)
        => ((((cy >> 5) << 1 | (cx >> 5)) << 10 | (cy & 31) << 5 | (cx & 31)) | (submapMap ? 0x1000 : 0)) << 1;

    /// <summary>The two long reads of the table, piece word then destination word, whose
    /// operands say where it is — vanilla's own <c>LDA.L DATA_04DD8D,X</c> and <c>DATA_04DD8F,X</c>
    /// at $04E49E and $04E4A3. Lunar Magic relocates by repointing both; so do we.</summary>
    private const int StepReadPiece = 0x04E49E, StepReadDst = 0x04E4A3;

    /// <summary>
    /// Write the steps back, grouped by event, with the cumulative ends table over them — the
    /// reader's start word before it is kept, and every end counts from there. Vanilla's table
    /// is FULL (371 steps in 371 slots), so a table that has to grow moves into a RATS block in
    /// expanded space with headroom, and both reads are repointed at it, as Lunar Magic's own
    /// save does. A reason only when the ROM has no free space to move it to.
    /// </summary>
    public static string? WriteEventSteps(Rom rom, IReadOnlyList<EventStep> steps)
    {
        var d = rom.Data;
        int table = EventStepsPc(rom), ends = rom.FileOffset(EventStepEnds);
        if (steps.Count > EventStepCapacity(rom))
        {
            int slots = steps.Count + 64;                               // room to keep adding without moving again
            int snes;
            try { snes = RatsWriter.Allocate(rom, new byte[slots * 4]); }
            catch (Exception e) { return $"the events have {steps.Count} steps and the ROM has no room to move their table: {e.Message}"; }
            for (int i = 0; i < 3; i++)
            {
                d[rom.FileOffset(StepReadPiece) + 1 + i] = (byte)(snes >> (8 * i));
                d[rom.FileOffset(StepReadDst) + 1 + i] = (byte)((snes + 2) >> (8 * i));
            }
            table = rom.FileOffset(snes);
        }
        var ordered = steps.OrderBy(s => s.Event).ToList();            // stable: an event's steps keep their order
        for (int k = 0; k < ordered.Count; k++)
        {
            var s = ordered[k];
            int dst = EncodeEventDst(s.Cx, s.Cy, s.SubmapMap);
            d[table + 4 * k] = (byte)s.Piece; d[table + 4 * k + 1] = (byte)(s.Piece >> 8);
            d[table + 4 * k + 2] = (byte)dst; d[table + 4 * k + 3] = (byte)(dst >> 8);
        }
        int start = d[ends - 2] | d[ends - 1] << 8, k2 = 0;
        for (int ev = 0; ev < EventCount; ev++)
        {
            while (k2 < ordered.Count && ordered[k2].Event <= ev) k2++;
            int end = start + k2;
            d[ends + 2 * ev] = (byte)end; d[ends + 2 * ev + 1] = (byte)(end >> 8);
        }
        return null;
    }

    /// <summary>The steps as the project keeps them: five bytes each — event, piece word,
    /// destination word — so the file carries what the table carries and nothing derived.</summary>
    public static byte[] PackEventSteps(IReadOnlyList<EventStep> steps)
    {
        var b = new byte[steps.Count * 5];
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            int dst = EncodeEventDst(s.Cx, s.Cy, s.SubmapMap);
            b[5 * i] = (byte)s.Event;
            b[5 * i + 1] = (byte)s.Piece; b[5 * i + 2] = (byte)(s.Piece >> 8);
            b[5 * i + 3] = (byte)dst; b[5 * i + 4] = (byte)(dst >> 8);
        }
        return b;
    }

    public static List<EventStep> UnpackEventSteps(byte[] b)
    {
        var list = new List<EventStep>(b.Length / 5);
        for (int i = 0; i + 5 <= b.Length; i += 5)
            list.Add(DecodeEventStep(b[i], b[i + 1] | b[i + 2] << 8, b[i + 3] | b[i + 4] << 8));
        return list;
    }

    /// <summary>One table entry to a step — the one place the destination word is taken apart.</summary>
    private static EventStep DecodeEventStep(int ev, int src, int dst)
    {
        int w = dst >> 1;
        bool sub = w >= 0x1000;
        w &= 0xFFF;
        int scr = w >> 10;
        return new(ev, src, (scr & 1) * 32 + (w & 31), (scr >> 1) * 32 + ((w >> 5) & 31), sub, src >= 0x900 ? 2 : 6);
    }

    private List<EventStep> ReadEventSteps()
    {
        var d = Rom.Data;
        int steps = EventStepsPc(Rom);
        int ends = Rom.FileOffset(EventStepEnds);
        var list = new List<EventStep>();
        int start = d[ends - 2] | d[ends - 1] << 8;
        for (int ev = 0; ev < EventCount; ev++)
        {
            int end = d[ends + 2 * ev] | d[ends + 2 * ev + 1] << 8;
            for (int k = start; k < end && k < 0x1000; k++)
                list.Add(DecodeEventStep(ev, d[steps + 4 * k] | d[steps + 4 * k + 1] << 8, d[steps + 4 * k + 2] | d[steps + 4 * k + 3] << 8));
            start = Math.Max(start, end);
        }
        return list;
    }
}
