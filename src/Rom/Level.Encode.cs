namespace PipeDream;

/// <summary>
/// Level — SAVING level data: object list → raw Layer-1 byte stream (CONTRACT §4), the exact
/// inverse of Level.Parse. Each object re-emits its 3 bytes (+ extras for screen exits and
/// DM16 forms), preceded by the 5-byte header copied verbatim from the ROM (header fields
/// aren't re-derived yet), terminated by 0xFF. Round-trip verified byte-identical.
///
/// NormalizeStream orders an EDITED object list into a valid stream: because the raw
/// new-screen bit only advances the running screen counter by 1, arbitrary placement needs
/// explicit screen-jump commands, which this inserts (matching how LM stores levels).
/// </summary>
public sealed partial class Level
{
    public byte[] Encode(Rom rom) => Encode(rom, Objects);

    /// <summary>
    /// Order an edited object list into a valid stream: objects sorted by absolute screen
    /// (stable, so within-screen layering is preserved) with a screen-jump command inserted
    /// before each screen change and NewScreen flags cleared. The raw NewScreen bit only
    /// advances the running screen counter by 1, so arbitrary placement needs explicit jumps.
    /// </summary>
    public static List<LevelObject> NormalizeStream(IEnumerable<LevelObject> objs)
    {
        var outl = new List<LevelObject>();
        int running = 0;
        foreach (var o in objs.OrderBy(o => o.Screen))
        {
            if (o.Screen != running) { outl.Add(LevelObject.ScreenJump(o.Screen)); running = o.Screen; }
            outl.Add(o.WithNewScreen(false));
        }
        return outl;
    }

    /// <summary>Encode this level's header (verbatim from ROM) + a given object list + 0xFF.</summary>
    public byte[] Encode(Rom rom, IEnumerable<LevelObject> objects)
    {
        var outb = new List<byte>(256);
        outb.AddRange(rom.Data.AsSpan(rom.FileOffset(DataPointer), 5).ToArray());   // header
        foreach (var o in objects) AppendObject(outb, o);
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
