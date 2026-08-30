namespace PipeDream;

/// <summary>A level's sprite list + header byte (memory setting / buoyancy).</summary>
public sealed class SpriteData
{
    public int SpriteMemory;             // header & 0x1F ($05D8FB; LM narrows vanilla's 0x3F to free bit 5)
    public int Buoyancy;                 // header bits 7-6 ($05D902)
    /// <summary>Header bit 5 — Lunar Magic's extended list: `FF nn` sets a 32-row band for the
    /// sprites that follow, `FF FE` ends the list (its loader, block C of the level engine, reads
    /// it as $0BF5 bit 5). Set on encode whenever a sprite sits in a band; kept if the ROM had it.</summary>
    public bool ExtendedList;
    public readonly List<Sprite> Sprites = new();

    public static SpriteData Parse(Rom rom, int level)
    {
        var d = new SpriteData();
        int p = rom.FileOffset(rom.SpritePointer(level));
        int header = rom.Data[p++];
        d.SpriteMemory = header & 0x1F;
        d.Buoyancy = header >> 6;
        d.ExtendedList = (header & 0x20) != 0;
        int sizeBase = rom.LmSpriteSizeBase;     // -1 = vanilla fixed 3-byte entries
        int band = 0;
        while (p + 1 < rom.Data.Length)
        {
            if (rom.Data[p] == 0xFF)
            {
                // Extended list: FF nn = the band for what follows, FF FE / FF FF = end. Vanilla: end.
                if (!d.ExtendedList || rom.Data[p + 1] >= 0xFE) break;
                band = rom.Data[p + 1]; p += 2;
                continue;
            }
            if (p + 2 >= rom.Data.Length) break;
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
                ExtraBytes: eb,
                Band: band));
        }
        return d;
    }

    /// <summary>Exact inverse of Parse: header, entries with LM's `FF nn` band markers where the
    /// band changes, and `FF FE` (extended) or `FF` (vanilla) to end.</summary>
    public byte[] Encode()
    {
        bool extended = ExtendedList || Sprites.Any(s => s.Band != 0);
        var outb = new List<byte> { (byte)((Buoyancy << 6) | (extended ? 0x20 : 0) | (SpriteMemory & 0x1F)) };
        int band = 0;
        foreach (var s in Sprites)
        {
            if (s.Band != band) { outb.Add(0xFF); outb.Add((byte)s.Band); band = s.Band; }
            outb.Add((byte)(((s.Y & 0x0F) << 4) | ((s.Extra & 0x03) << 2)
                            | ((s.Screen & 0x10) >> 3) | ((s.Y & 0x10) >> 4)));
            outb.Add((byte)((s.XNibble << 4) | (s.Screen & 0x0F)));
            outb.Add((byte)s.Number);
            if (s.ExtraBytes is not null) outb.AddRange(s.ExtraBytes);
        }
        outb.Add(0xFF);
        if (extended) outb.Add(0xFE);
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

    /// <summary>PIXI custom sprite marker: black box with a red X (LM-style "no display
    /// data"). Rendering customs faithfully needs LM's .ssc/.dsc metadata — not emulation.</summary>
    internal static void DrawCustomBox(uint[] img, int W, int H, Sprite s, bool vert)
    {
        var (cx, cy) = s.Cell(vert);
        int px = cx * 16, py = cy * 16;
        if (px < 0 || py < 0 || px + 16 > W || py + 16 > H) return;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                bool border = y == 0 || y == 15 || x == 0 || x == 15;
                bool cross = Math.Abs(x - y) <= 1 || Math.Abs(x + y - 15) <= 1;
                img[(py + y) * W + (px + x)] =
                    border ? 0xFF606060u : cross ? 0xFF0000FFu : 0xFF000000u;   // ABGR red X on black
            }
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
