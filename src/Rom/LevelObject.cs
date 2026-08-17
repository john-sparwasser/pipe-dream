namespace PipeDream;

/// <summary>One decoded Layer-1 object. Encoding confirmed via bank 05 `LoadLevelData`.</summary>
public readonly struct LevelObject
{
    public readonly bool NewScreen;
    public readonly bool Extended;   // object# == 0
    public readonly int Number;      // 0x00-0x3F (0 = extended)
    public readonly int Screen;      // absolute screen number
    public readonly int XNibble;     // 0-15 within screen
    public readonly int Y;           // 0-0x1F
    public readonly int Byte3;       // raw settings byte (= size for DM16)
    public readonly int ExtraByte;   // 4th byte for screen exits, else -1
    public readonly int Dm16Tile;    // Direct-Map16 tile number, else -1
    // Extended DM16 forms (obj 0x27/0x29 with page-byte bits 6-7 set, CONTRACT §8):
    public readonly int Dm16Page;    // raw page byte (-1 = simple/none)
    public readonly int Dm16ExtX;    // extra run byte (page bit7), -1 = absent
    public readonly int Dm16ExtH;    // height-override byte (page bits 6+7), -1 = absent
    // For standard (rectangle-family) objects; other families reinterpret Byte3.
    public int Width => (Byte3 & 0x0F) + 1;
    public int Height => (Byte3 >> 4) + 1;
    public int ExtendedNumber => Byte3;   // when Extended
    public int AbsoluteX => Screen * 16 + XNibble;
    // Screen exit = extended object 0x00; the only variable-length object (4 bytes).
    public bool IsScreenExit => Extended && Byte3 == 0x00;
    // Screen jump = extended object 0x01; stream plumbing, re-derived by NormalizeStream.
    public bool IsScreenJump => Extended && Byte3 == 0x01;
    // Direct Map16: LM object # 0x23 (Form A, page 1) or 0x27 (Form B, any page).
    public bool IsDm16 => Dm16Tile >= 0;

    public LevelObject(bool newScreen, int number, int screen, int xNibble, int y, int b3, int extra, int dm16 = -1,
                       int dm16Page = -1, int dm16ExtX = -1, int dm16ExtH = -1)
    {
        NewScreen = newScreen; Number = number; Screen = screen;
        XNibble = xNibble; Y = y; Byte3 = b3; ExtraByte = extra; Dm16Tile = dm16; Extended = number == 0;
        Dm16Page = dm16Page; Dm16ExtX = dm16ExtX; Dm16ExtH = dm16ExtH;
    }

    /// <summary>This object with a different NewScreen flag (struct is immutable).</summary>
    public LevelObject WithNewScreen(bool ns) =>
        new(ns, Number, Screen, XNibble, Y, Byte3, ExtraByte, Dm16Tile, Dm16Page, Dm16ExtX, Dm16ExtH);

    /// <summary>A vanilla screen-jump command (ext obj 0x01) targeting a screen.</summary>
    public static LevelObject ScreenJump(int screen) =>
        new(false, 0, screen, 0, screen & 0x1F, 0x01, -1);

    // ---- Screen exits (CONTRACT §4, handler $0DA512) -------------------------------
    // The handler indexes its tables with `$0A & $1F` — the Y FIELD, not the object's
    // stream screen — so Y is the screen this exit governs. The X nibble carries the
    // flags: bit0 → $19D8,X (water), the whole nibble >> 1 → $1B93 (use the secondary
    // entrance table, in which case the destination byte indexes $05F800+ instead of
    // naming a level). The 4th byte is the destination.

    /// <summary>LM's own secondary exit (ext obj 0x02): 2 extra bytes = the exit word,
    /// written to $19B8/$19D8 by the same tables the vanilla handler uses.</summary>
    public bool IsLmSecondaryExit => Extended && Byte3 == 0x02;

    /// <summary>Screen this exit governs (both exit forms index by the Y field).</summary>
    public int ExitScreen => Y;
    public bool ExitIsWater => (XNibble & 1) != 0;
    public bool ExitUsesSecondary => XNibble >> 1 != 0;
    /// <summary>Destination: a level number for a plain exit, or an index into the
    /// secondary entrance table when <see cref="ExitUsesSecondary"/>.</summary>
    public int ExitDestination => ExtraByte;

    public static LevelObject ScreenExit(int screen, int destination, bool water, bool secondary) =>
        new(false, 0, screen, (secondary ? 2 : 0) | (water ? 1 : 0), screen & 0x1F, 0x00, destination & 0xFF);

    /// <summary>Create a Direct Map16 object placing <paramref name="tile"/> at a cell.
    /// Sizes past 16 use LM's extended Form B (page bits 6+7: 7-bit width in byte3,
    /// height byte) — verified against the handler: max 128 wide x 256 tall.</summary>
    public static LevelObject MakeDm16(int tile, int screen, int xNib, int y, int w = 1, int h = 1, bool newScreen = false)
    {
        if (w > 16 || h > 16)
            return new LevelObject(newScreen, 0x27, screen, xNib, y, Math.Clamp(w, 1, 128) - 1, -1, tile,
                                   ((tile >> 8) & 0x3F) | 0xC0, 0, Math.Clamp(h, 1, 256) - 1);
        // Page-0 Form (obj 0x22), page-1 Form A (0x23), or general Form B (0x27).
        int num = tile <= 0xFF ? 0x22 : tile <= 0x1FF ? 0x23 : 0x27;
        int size = ((h - 1) << 4) | (w - 1);
        return new LevelObject(newScreen, num, screen, xNib, y, size, -1, tile);
    }

    /// <summary>Declared size of a DM16 object (extended Form B or nibble forms).
    /// Dm16Page is -1 for the compact 0x22/0x23 forms — check before masking.</summary>
    public (int w, int h) Dm16Size()
        => Dm16Page >= 0 && (Dm16Page & 0xC0) == 0xC0
            ? ((Byte3 & 0x7F) + 1, Math.Max(1, Dm16ExtH + 1)) : (Width, Height);

    /// <summary>
    /// This DM16 object resized: compact nibble form when it fits, extended Form B when
    /// not (converting 0x22/0x23 to 0x27 as needed). The bit7 "run byte" (Dm16ExtX) is a
    /// stamp descriptor — low nibble stamp width-1, high nibble stamp height-1, fill
    /// cycles tileLow + (col%W) + 0x10*(row%H) (CONTRACT §9d, decoded via RomPrep parity
    /// probes) — preserved verbatim whenever bit7 stays set so resizing keeps the stamp.
    /// </summary>
    public LevelObject Dm16Resized(int w, int h)
    {
        w = Math.Clamp(w, 1, 128); h = Math.Clamp(h, 1, 256);
        int page = Dm16Page >= 0 ? Dm16Page & 0x3F : (Dm16Tile >> 8) & 0x3F;
        if (w <= 16 && h <= 16)
        {
            int b3 = ((h - 1) << 4) | (w - 1);
            if (Number is 0x22 or 0x23 || Dm16Page < 0)
                return new(NewScreen, Number, Screen, XNibble, Y, b3, ExtraByte, Dm16Tile);
            bool run = (Dm16Page & 0x80) != 0;         // keep the stamp descriptor if present
            return new(NewScreen, Number, Screen, XNibble, Y, b3, ExtraByte, Dm16Tile,
                       run ? page | 0x80 : page, run ? Math.Max(0, Dm16ExtX) : -1, -1);
        }
        int num = Number == 0x29 ? 0x29 : 0x27;
        return new(NewScreen, num, Screen, XNibble, Y, w - 1, ExtraByte, Dm16Tile,
                   page | 0xC0, Math.Max(0, Dm16ExtX), h - 1);
    }
}
