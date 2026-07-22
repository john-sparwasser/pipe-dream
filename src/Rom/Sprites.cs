namespace PipeDream;

/// <summary>One sprite entry (CONTRACT §11). Decoded from the 3-byte format confirmed at
/// $02A82C: b1 = YYYYEEsy, b2 = XXXXSSSS, b3 = sprite number.</summary>
public readonly record struct Sprite(int Screen, int XNibble, int Y, int Extra, int Number, byte[]? ExtraBytes = null)
{
    public int AbsoluteX => Screen * 16 + XNibble;
    /// <summary>Numbers >= 0xE7 are scroll commands, not real sprites ($02A866).</summary>
    public bool IsScrollCommand => Number >= 0xE7;

    /// <summary>
    /// Cell position honoring the level orientation. Vertical levels use the same decode
    /// "with X and Y coords swapped" ($02A943): the Y field becomes the X cell (0-31) and
    /// the screen walk (our AbsoluteX) runs down the level.
    /// </summary>
    public (int X, int Y) Cell(bool vertical) => vertical ? (Y, AbsoluteX) : (AbsoluteX, Y);
}

/// <summary>A level's sprite list + header byte (memory setting / buoyancy).</summary>
public sealed class SpriteData
{
    public int SpriteMemory;             // header & 0x3F ($05D8FB)
    public int Buoyancy;                 // header bits 7-6 ($05D902)
    public readonly List<Sprite> Sprites = new();

    public static SpriteData Parse(Rom rom, int level)
    {
        var d = new SpriteData();
        int p = rom.FileOffset(rom.SpritePointer(level));
        int header = rom.Data[p++];
        d.SpriteMemory = header & 0x3F;
        d.Buoyancy = header >> 6;
        int sizeBase = rom.LmSpriteSizeBase;     // -1 = vanilla fixed 3-byte entries
        while (p + 2 < rom.Data.Length && rom.Data[p] != 0xFF)
        {
            int b1 = rom.Data[p], b2 = rom.Data[p + 1], b3 = rom.Data[p + 2];
            int extra = (b1 >> 2) & 0x03;
            // LM/PIXI custom sprites: per-(extraBits,number) entry size from the size table.
            int size = sizeBase < 0 ? 3 : Math.Max(3, (int)rom.Data[rom.FileOffset(sizeBase + (extra << 8) + b3)]);
            byte[]? eb = size > 3 ? rom.Data.AsSpan(p + 3, size - 3).ToArray() : null;
            p += size;
            d.Sprites.Add(new Sprite(
                Screen: ((b1 & 0x02) << 3) | (b2 & 0x0F),
                XNibble: b2 >> 4,
                Y: ((b1 & 0x01) << 4) | (b1 >> 4),
                Extra: extra,
                Number: b3,
                ExtraBytes: eb));
        }
        return d;
    }

    /// <summary>Exact inverse of Parse (header + 3-byte entries + 0xFF terminator).</summary>
    public byte[] Encode()
    {
        var outb = new List<byte> { (byte)((Buoyancy << 6) | (SpriteMemory & 0x3F)) };
        foreach (var s in Sprites)
        {
            outb.Add((byte)(((s.Y & 0x0F) << 4) | ((s.Extra & 0x03) << 2)
                            | ((s.Screen & 0x10) >> 3) | ((s.Y & 0x10) >> 4)));
            outb.Add((byte)((s.XNibble << 4) | (s.Screen & 0x0F)));
            outb.Add((byte)s.Number);
            if (s.ExtraBytes is not null) outb.AddRange(s.ExtraBytes);
        }
        outb.Add(0xFF);
        return outb.ToArray();
    }

    // 4x6 hex digit font (rows of 4 bits, MSB = left pixel) for the overlay badges.
    private static readonly ushort[] FontRows = BuildFont();
    private static ushort[] BuildFont()
    {
        string[] glyphs =                       // 6 rows of 4 pixels per hex digit
        {
            "0:699996", "1:262227", "2:69124F", "3:E1611E", "4:99F111",
            "5:F8E11E", "6:68E996", "7:F12244", "8:696996", "9:697116",
            "A:69F999", "B:E9E99E", "C:698896", "D:E9999E", "E:F8E88F", "F:F8E888",
        };
        var rows = new ushort[16 * 6];
        for (int g = 0; g < 16; g++)
        {
            string bits = glyphs[g][2..];
            for (int r = 0; r < 6; r++)
                rows[g * 6 + r] = (ushort)(r < bits.Length ? Convert.ToInt32(bits[r].ToString(), 16) : 0);
        }
        return rows;
    }

    /// <summary>
    /// Draw sprites with their real graphics (OAM capture via emulation, CONTRACT §14);
    /// badge markers for scroll commands and sprites whose routines can't be captured.
    /// </summary>
    public void DrawOverlay(uint[] img, int W, int H, Rom? rom = null, LevelHeader? header = null, int level = -1,
                            Palette? palOverride = null)
    {
        if (rom is null || header is null) { foreach (var s in Sprites) DrawBadge(img, W, H, s, false); return; }
        var ov = SpriteOverlay.Build(rom, this, header.Value, level);
        ov.Draw(img, W, H, palOverride ?? Palette.Load(rom, header.Value, level));
    }

    /// <summary>Badge marker (bordered box + hex number) at the sprite's cell.</summary>
    internal static void DrawBadge(uint[] img, int W, int H, Sprite s, bool vert)
    {
        var (cx, cy) = s.Cell(vert);
        int px = cx * 16, py = cy * 16;
        if (px < 0 || py < 0 || px + 16 > W || py + 16 > H) return;
        uint border = s.IsScrollCommand ? 0xFF00A0FFu : 0xFF00C000u;   // ABGR: orange / green
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (py + y) * W + (px + x);
                // solid dark interior (idempotent: cached overlays redraw onto live canvases)
                img[i] = y == 0 || y == 15 || x == 0 || x == 15 ? border : 0xFF303030u;
            }
        DrawHex(img, W, px + 3, py + 5, (s.Number >> 4) & 0xF);
        DrawHex(img, W, px + 8, py + 5, s.Number & 0xF);
    }

    private static void DrawHex(uint[] img, int W, int px, int py, int digit)
    {
        for (int r = 0; r < 6; r++)
        {
            int bits = FontRows[digit * 6 + r];
            for (int c = 0; c < 4; c++)
                if ((bits & (8 >> c)) != 0) img[(py + r) * W + px + c] = 0xFFFFFFFFu;
        }
    }
}

/// <summary>
/// Cached sprite overlay: the expensive part (per-sprite 65816 OAM capture + SP tile
/// decode) runs once in Build; Draw is cheap pixel blits, safe to repeat on a live
/// canvas (used by the editor on every incremental repaint).
/// </summary>
public sealed class SpriteOverlay
{
    private readonly (Sprite s, List<SpriteRender.Oam>? oam)[] items;
    private readonly byte[][]? sp;
    private readonly bool vert;

    private SpriteOverlay((Sprite, List<SpriteRender.Oam>?)[] items, byte[][]? sp, bool vert)
    { this.items = items; this.sp = sp; this.vert = vert; }

    public static SpriteOverlay Build(Rom rom, SpriteData sprites, LevelHeader h, int level)
    {
        byte[][]? sp = null;
        try { sp = SpriteRender.LoadSpTiles(rom, h, level); } catch { }
        bool vert = rom.IsVerticalMode(h.LevelMode);
        var items = new (Sprite, List<SpriteRender.Oam>?)[sprites.Sprites.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var s = sprites.Sprites[i];
            var (cx, cy) = s.Cell(vert);
            List<SpriteRender.Oam>? oam = null;
            if (sp is not null && !s.IsScrollCommand)
            {
                // Static display table for vanilla sprites (extra bits 0/1); PIXI custom
                // sprites (extra bits 2/3 = different routines entirely) capture live.
                if (s.Extra < 2 && SpriteDisplay.TryGet(s.Number, out var rel))
                    oam = rel.Select(o => o with { X = o.X + cx * 16, Y = o.Y + cy * 16 }).ToList();
                else
                    oam = SpriteRender.Capture(rom, s, cx, cy, vert);
            }
            items[i] = (s, oam);
        }
        return new SpriteOverlay(items, sp, vert);
    }

    public void Draw(uint[] img, int W, int H, Palette pal)
    {
        foreach (var (s, oam) in items)
        {
            if (oam is not null && sp is not null) SpriteRender.Draw(img, W, H, oam, sp, pal);
            else SpriteData.DrawBadge(img, W, H, s, vert);
        }
    }

    /// <summary>Level-pixel bounds of one sprite's tiles (null when badge-only).</summary>
    public (int MinX, int MinY, int MaxX, int MaxY)? PixelBounds(int i)
    {
        var (_, oam) = items[i];
        if (oam is null || oam.Count == 0) return null;
        return (oam.Min(o => o.X), oam.Min(o => o.Y),
                oam.Max(o => o.X + (o.Big ? 16 : 8)), oam.Max(o => o.Y + (o.Big ? 16 : 8)));
    }

    /// <summary>Draw one sprite shifted by (shiftX, shiftY) pixels — for drag ghosts.</summary>
    public void DrawOne(int i, uint[] img, int W, int H, Palette pal, int shiftX, int shiftY)
    {
        var (_, oam) = items[i];
        if (oam is null || sp is null) return;
        SpriteRender.Draw(img, W, H, oam.Select(o => o with { X = o.X + shiftX, Y = o.Y + shiftY }).ToList(), sp, pal);
    }
}
