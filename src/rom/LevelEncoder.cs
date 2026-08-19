namespace PipeDream;

/// <summary>
/// SAVING level data: object list → raw Layer-1 byte stream (CONTRACT §4), the exact
/// inverse of LevelParser. Each object re-emits its 3 bytes (+ extras for screen exits and
/// DM16 forms), preceded by the level's 5 header bytes re-packed from its fields, terminated
/// by 0xFF. Round-trip verified byte-identical.
///
/// NormalizeStream orders an EDITED object list into a valid stream: because the raw
/// new-screen bit only advances the running screen counter by 1, arbitrary placement needs
/// explicit screen-jump commands, which this inserts (matching how LM stores levels).
/// </summary>
public static class LevelEncoder
{
    public static byte[] Encode(Level level) => Encode(level, level.Objects);

    /// <summary>
    /// Order an edited object list into a valid stream: objects sorted by absolute screen
    /// (stable, so within-screen layering is preserved) with a screen-jump command inserted
    /// before each screen change and NewScreen flags cleared. The raw NewScreen bit only
    /// advances the running screen counter by 1, so arbitrary placement needs explicit jumps.
    /// </summary>
    public static List<LevelObject> NormalizeStream(IEnumerable<LevelObject> objs)
        => NormalizeStream(objs, null);

    /// <summary>
    /// Same, optionally recording each output entry's source index in the input enumeration
    /// (-1 for inserted screen jumps) — lets callers map stream records back to their list.
    /// </summary>
    public static List<LevelObject> NormalizeStream(IEnumerable<LevelObject> objs, List<int>? provenance)
    {
        var outl = new List<LevelObject>();
        int running = 0;
        // Input screen jumps are dropped and re-derived below — they're stream plumbing,
        // not content. Keeping them would stack a fresh jump in front of each old one on
        // every normalize→save cycle, growing the stream forever.
        foreach (var (o, i) in objs.Select((o, i) => (o, i)).Where(t => !t.o.IsScreenJump).OrderBy(t => t.o.Screen))
        {
            if (o.Screen != running) { outl.Add(LevelObject.ScreenJump(o.Screen)); provenance?.Add(-1); running = o.Screen; }
            outl.Add(o.WithNewScreen(false)); provenance?.Add(i);
        }
        return outl;
    }

    /// <summary>Encode a level's header (re-packed from its fields) + a given object list + 0xFF.
    /// <paramref name="offsets"/>, when given, records each object's byte offset in the output.</summary>
    public static byte[] Encode(Level level, IEnumerable<LevelObject> objects, List<int>? offsets = null)
    {
        var outb = new List<byte>(256);
        outb.AddRange(level.Header.ToBytes());
        foreach (var o in objects) { offsets?.Add(outb.Count); AppendObject(outb, o); }
        outb.Add(0xFF);
        return outb.ToArray();
    }

    private static void AppendObject(List<byte> outb, LevelObject o)
    {
        if (o.IsDm16)
        {
            // b1 carries object# bits 4-5 (<<1), b2 high nibble = object# low nibble.
            byte db1 = (byte)((o.NewScreen ? 0x80 : 0) | ((o.Number & 0x30) << 1) | (o.Y & 0x1F));
            byte db2 = (byte)(((o.Number & 0x0F) << 4) | (o.XNibble & 0x0F));
            outb.Add(db1); outb.Add(db2); outb.Add((byte)o.Byte3);
            if (o.Number is 0x22 or 0x23)                  // 1 tile byte (page fixed 0/1)
            {
                outb.Add((byte)(o.Dm16Tile & 0xFF));
            }
            else                                           // 0x27/0x29: page byte + low (+extras)
            {
                outb.Add((byte)(o.Dm16Page >= 0 ? o.Dm16Page : (o.Dm16Tile >> 8) & 0x3F));
                outb.Add((byte)(o.Dm16Tile & 0xFF));
                if (o.Dm16ExtX >= 0) outb.Add((byte)o.Dm16ExtX);
                if (o.Dm16ExtH >= 0) outb.Add((byte)o.Dm16ExtH);
            }
            return;
        }
        byte b1 = (byte)((o.NewScreen ? 0x80 : 0) | ((o.Number & 0x30) << 1) | (o.Y & 0x1F));
        byte b2 = (byte)(((o.Number & 0x0F) << 4) | (o.XNibble & 0x0F));
        outb.Add(b1); outb.Add(b2); outb.Add((byte)o.Byte3);
        if (o.IsScreenExit && o.ExtraByte >= 0) outb.Add((byte)o.ExtraByte);
        else if (o.Extended && o.Byte3 == 0x02 && o.ExtraByte >= 0)
        {   // LM secondary exit: 2-byte exit word
            outb.Add((byte)(o.ExtraByte & 0xFF)); outb.Add((byte)(o.ExtraByte >> 8));
        }
    }
}
