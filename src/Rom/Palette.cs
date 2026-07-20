namespace PipeDream;

/// <summary>
/// Level palette (256 CGRAM colors = 16 palettes × 16). Replicates the ROM's LoadPalette
/// ($00ABED): assembles vanilla shared palette data (bank $00) using the level header's
/// palette indices. See CONTRACT.md §6c. Colors are SNES BGR555; RGBA is precomputed.
/// </summary>
public sealed class Palette
{
    public readonly ushort[] Bgr = new ushort[256];
    public readonly uint[] Rgba = new uint[256];

    /// <summary>Color 0 of each 16-color palette is the shared/transparent slot.</summary>
    public static bool IsTransparent(int colorIndex) => (colorIndex & 0x0F) == 0;

    // Row-offset selector table $00ABD3 (indexed by a palette-setting nibble).
    private const int Abd3 = 0x00ABD3;

    public static Palette Load(Rom rom, LevelHeader h, int level = -1, int animPhase = 0)
    {
        var p = new Palette();
        var cg = p.Bgr;

        // Global palette exanimation ($00A418): CGRAM color 0x64 (row 6 color 4) is
        // rewritten every 4 frames from MorePalettes ($00B60C) — the gold/white glint.
        // Our 4 display phases (8 frames each) sample byte offsets 0/4/8/12 of the cycle.
        ushort Glint() => (ushort)rom.ReadValue(0x00B60C + ((animPhase & 3) * 4), 2);

        // LM per-level custom palette (CONTRACT §7e): a full CGRAM image replaces the
        // vanilla assembly entirely; word 0 of the blob is the back-area color.
        if (level >= 0 && rom.LmCustomPalette(level) is (var back, var colors))
        {
            Array.Copy(colors, cg, 256);
            cg[0] = back;
            cg[0x64] = Glint();                            // NMI overwrite applies regardless
            for (int i = 0; i < 256; i++) p.Rgba[i] = ToRgba(cg[i]);
            return p;
        }

        void Fill(int destColor, ushort val, int rows)
        {
            for (int r = 0; r < rows; r++) { int i = destColor + r * 16; if (i < 256) cg[i] = val; }
        }
        void Load(int destColor, int srcSnes, int numColors, int rows)
        {
            int fo = rom.FileOffset(srcSnes);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < numColors; c++)
                {
                    ushort col = (ushort)(rom.Data[fo] | (rom.Data[fo + 1] << 8));
                    fo += 2;
                    int i = destColor + r * 16 + c;
                    if (i < 256) cg[i] = col;
                }
        }
        int Off(int nibble) => rom.Data[rom.FileOffset(Abd3) + (nibble & 0x0F)];

        Fill(0x01, 0x7FDD, 8);                         // color 1 white, object palettes 0-7
        Fill(0x81, 0x7FFF, 8);                         // color 1 white, sprite palettes 8-F
        Load(0x08, 0x00B170, 8, 2);                    // layer-3 colors 8-F, palettes 0-1
        Load(0x42, 0x00B250, 6, 10);                   // colors 2-7, palettes 4-D (shared)
        // backdrop (CGRAM 0) = back-area color
        int bc = rom.FileOffset(0x00B0A0) + (h.BackAreaColor & 0x0F) * 2;
        cg[0] = (ushort)(rom.Data[bc] | (rom.Data[bc + 1] << 8));
        Load(0x22, 0x00B190 + Off(h.FgPalette), 6, 2); // FG colors 2-7, palettes 2-3
        Load(0xE2, 0x00B318 + Off(h.SpritePalette), 6, 2); // sprite colors 2-7, palettes E-F
        Load(0x02, 0x00B0B0 + Off(h.BgPalette), 6, 2); // BG colors 2-7, palettes 0-1
        Load(0x29, 0x00B674, 7, 3);                    // colors 9-F, object palettes 2-4
        Load(0x99, 0x00B674, 7, 3);                    // colors 9-F, sprite palettes 9-B
        cg[0x64] = Glint();

        for (int i = 0; i < 256; i++) p.Rgba[i] = ToRgba(cg[i]);
        return p;
    }

    /// <summary>SNES 15-bit BGR555 → RGBA8888 (0xAABBGGRR in memory: R,G,B,A bytes).</summary>
    public static uint ToRgba(ushort c)
    {
        int r = c & 0x1F, g = (c >> 5) & 0x1F, b = (c >> 10) & 0x1F;
        uint R = (uint)(r << 3 | r >> 2), G = (uint)(g << 3 | g >> 2), B = (uint)(b << 3 | b >> 2);
        return 0xFF000000u | (B << 16) | (G << 8) | R;
    }
}
