namespace PipeDream;

/// <summary>
/// Mario's own graphics, read the way the game reads them. The player sheet is the raw 4bpp blob
/// at the fixed pointer $00B8D8 (vanilla $088000, 0x5D00 bytes), DMA'd to VRAM per frame by
/// $00A300 from RAM $7E2000+. $00F636 turns a 16x16 player tile number t into that RAM address:
/// <c>$2000 + (t&amp;7)*0x40 + (t&gt;&gt;4)*0x400 + (t&amp;8 ? 0x4000 : 0)</c>, the two top 8x8s
/// there and the two bottom ones 0x200 further on.
/// </summary>
public static class PlayerGfx
{
    /// <summary>Big Mario standing, facing right (the sheet stores him facing left): frame 0 with TilesetIndex[big]=0x46 gives top
    /// tile DATA_00E00C[0x46]=$70 and bottom DATA_00E0CC[0x46]=$02; the palette is the 10 colours
    /// DATA_00E2A2 points at for powerup 1 ($00B2DC), landing on sprite palette 8 colours 6-15.
    /// Returns 16x32 RGBA pixels (0 = transparent), or null when the blob will not decompress.
    /// <paramref name="pal"/> supplies row 8's shared colours 1-5.</summary>
    public static uint[]? BigMarioStanding(Rom rom, Palette pal)
    {
        byte[] sheet;
        try
        {
            int bank = rom.ReadByte(0x00B890) << 16;
            sheet = Gfx.Lz2Decompress(rom.Data, rom.FileOffset(bank | rom.ReadValue(0x00B8D8, 2)));
        }
        catch { return null; }

        var colors = new uint[16];
        for (int i = 1; i < 6; i++) colors[i] = pal.Rgba[0x80 + i];
        int palAddr = rom.ReadValue(0x00E2A2 + 4, 2);                       // powerup 1, default palette
        for (int i = 0; i < 10; i++) colors[6 + i] = Palette.ToRgba((ushort)rom.ReadValue(palAddr + i * 2, 2));

        var px = new uint[16 * 32];
        foreach (var (tile, oy) in new[] { (0x70, 0), (0x02, 16) })
        {
            int off = (tile & 7) * 0x40 + (tile >> 4) * 0x400 + ((tile & 8) != 0 ? 0x4000 : 0);
            foreach (var (sub, ox, dy) in new[] { (0, 0, 0), (0x20, 8, 0), (0x200, 0, 8), (0x220, 8, 8) })
            {
                if (off + sub + 0x20 > sheet.Length) return null;
                var t = Gfx.DecodeTile(sheet, off + sub, 4);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        // The sheet stores him facing left; the game X-flips for right ($76), and so do we.
                        if (t[y * 8 + x] is var c && c != 0) px[(oy + dy + y) * 16 + (15 - ox - x)] = colors[c];
            }
        }
        return px;
    }
}
