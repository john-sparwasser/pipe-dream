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

    private List<EventStep>? eventSteps;

    public IReadOnlyList<EventStep> EventSteps => eventSteps ??= ReadEventSteps();

    private List<EventStep> ReadEventSteps()
    {
        var d = Rom.Data;
        int p = Rom.FileOffset(0x04E49E);
        int steps = d[p] == 0xBF ? Rom.FileOffset(d[p + 1] | d[p + 2] << 8 | d[p + 3] << 16) : Rom.FileOffset(EventStepTable);
        int ends = Rom.FileOffset(EventStepEnds);
        var list = new List<EventStep>();
        int start = d[ends - 2] | d[ends - 1] << 8;
        for (int ev = 0; ev < EventCount; ev++)
        {
            int end = d[ends + 2 * ev] | d[ends + 2 * ev + 1] << 8;
            for (int k = start; k < end && k < 0x1000; k++)
            {
                int src = d[steps + 4 * k] | d[steps + 4 * k + 1] << 8, dst = d[steps + 4 * k + 2] | d[steps + 4 * k + 3] << 8;
                int w = dst >> 1;
                bool sub = w >= 0x1000;
                w &= 0xFFF;
                int scr = w >> 10;
                list.Add(new(ev, src, (scr & 1) * 32 + (w & 31), (scr >> 1) * 32 + ((w >> 5) & 31), sub, src >= 0x900 ? 2 : 6));
            }
            start = Math.Max(start, end);
        }
        return list;
    }
}
