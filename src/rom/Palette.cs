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
        // The smasher's colours: layer 3's own setup copies them over CGRAM 0C-0F (Layer3.LoadsSmasherColours).
        if (level >= 0 && Layer3.LoadsSmasherColours(rom, h.Tileset, Layer3.Option(rom, level)))
            Load(Layer3.SmasherCgram, Layer3.SmasherColours, 4, 1);
        cg[0x64] = Glint();

        for (int i = 0; i < 256; i++) p.Rgba[i] = ToRgba(cg[i]);
        return p;
    }

    /// <summary>
    /// The overworld's palette for one submap, built the way the game builds it. The overworld
    /// load ($00A5BC) runs the ordinary level palette loader first — with whatever header bytes
    /// the last level left, so the intro level's are the ones a new game arrives with — and then
    /// lays the overworld's own colours over it ($00AD25): palettes 4-7 colours 1-7 from the
    /// submap's 0x38-byte set at $00B3D8 (the special-world-passed set at $00B732 instead),
    /// palettes 2-7 colours 9-F from $00B528, sprite palettes 8-F colours 1-7 from $00B57C,
    /// layer 3's colours 8-F of palettes 0-1 from $00B5EC. Layer 1 draws in palettes 0-3, whose
    /// colour 1 is the loader's white and colours 8-F are these overrides; path fading borrows
    /// their colours 1-7 while a tile is revealed and puts them back.
    /// </summary>
    public static Palette LoadOverworld(Rom rom, int submap, bool specialPassed = false)
        => LoadOverworld(rom, OverworldLoads(rom, submap, specialPassed), rom.OwPaletteEdits);

    /// <summary>The palette the VANILLA loader gives a submap, edits aside, whether or not the ROM
    /// carries Lunar Magic's table — the vanilla tables stay where they were under it. It is what
    /// prep v32 filled that table with, level C7's leftovers and all.</summary>
    public static Palette LoadVanillaOverworld(Rom rom, int submap, bool specialPassed)
        => LoadOverworld(rom, VanillaOverworldLoads(rom, submap, specialPassed), []);

    /// <summary>Where the game keeps Mario's palette, the ten colours it uploads to CGRAM 86-8F.</summary>
    public const int MarioPalette = 0x00B2C8;

    /// <summary>
    /// A submap's palette as Lunar Magic's first overworld palette save writes it into its table:
    /// the vanilla overworld loads where they write, 0000 everywhere else — the level loader's
    /// leftovers, rows 0-3 colours 0-7 among them, which is why LM's editor shows those black —
    /// and Mario's colours at 86-8F, which the game re-uploads every frame anyway. Measured against
    /// the tables of five LM hacks (2026-10-18): QuickieWorld's differs from this in 3 of its 3584
    /// colours and RHR Thumbshredder's in 4; Sheffy 2, Super Hark Bros and Super Riff World differ
    /// in the land colours of rows 4-7 their authors recoloured; no colour differs in all five.
    /// Prep v33 fills the table with it.
    /// </summary>
    public static ushort[] LmConvertedOverworld(Rom rom, int submap, bool specialPassed)
    {
        var cg = new ushort[256];
        foreach (var load in VanillaOverworldLoads(rom, submap, specialPassed))
            LoadColors(rom, cg, load, []);
        for (int i = 0; i < 10; i++) cg[0x86 + i] = RomWord(rom, MarioPalette + i * 2);
        return cg;
    }

    private static Palette LoadOverworld(Rom rom, ColorLoad[] loads, Dictionary<int, ushort> edits)
    {
        const int introLevel = 0xC7;
        var p = Load(rom, new LevelHeader(rom.Data.AsSpan(rom.FileOffset(rom.Layer1Pointer(introLevel)), 5)));
        var cg = p.Bgr;
        foreach (var load in loads)
            LoadColors(rom, cg, load, edits);
        for (int i = 0; i < 256; i++) p.Rgba[i] = ToRgba(cg[i]);
        return p;
    }

    /// <summary>One LoadColors call of $00AD25: <c>N</c> words from <c>Src</c> into each of
    /// <c>Rows</c> palette rows, starting at CGRAM <c>Dest</c> and stepping a row at a time.</summary>
    private readonly record struct ColorLoad(int Dest, int Src, int N, int Rows);

    /// <summary>The overworld's four colour loads for a submap, in the game's order. The first is
    /// the submap's own set (DATA_00AD1E picks it, DATA_00ABDF says where it starts); the other
    /// three are one table for every submap. Under LM's overworld palettes it is one load of all
    /// 256 colours from the submap's own palette (<see cref="LunarMagic.LmOwPaletteTable"/>).</summary>
    private static ColorLoad[] OverworldLoads(Rom rom, int submap, bool specialPassed)
        => rom.LmOwPaletteTable is var table and >= 0
            ? [new(0, table + (submap + (specialPassed ? Overworld.Submaps : 0)) * 0x200, 16, 16)]
            : VanillaOverworldLoads(rom, submap, specialPassed);

    private static ColorLoad[] VanillaOverworldLoads(Rom rom, int submap, bool specialPassed)
    {
        int set = rom.Data[rom.FileOffset(0x00AD1E) + submap];                       // DATA_00AD1E
        int setOff = rom.ReadValue(0x00ABDF + set * 2, 2);                            // DATA_00ABDF
        return [new(0x41, (specialPassed ? 0x00B732 : 0x00B3D8) + setOff, 7, 4),
                new(0x29, 0x00B528, 7, 6),
                new(0x81, 0x00B57C, 7, 8),
                new(0x08, 0x00B5EC, 8, 2)];
    }

    /// <summary>
    /// The SNES address of the word a submap's CGRAM <paramref name="index"/> is loaded from, or
    /// null for a colour the overworld does not set — those are the level loader's leftovers,
    /// which belong to level C7 and not to the map. Editing an overworld colour IS writing this
    /// word: Lunar Magic edits the vanilla tables in place (reference/OVERWORLD.md §5 table) until
    /// its first overworld palette save, and its own table after — where every colour has a word.
    /// <paramref name="specialPassed"/> asks after the palette once the special world is passed.
    /// </summary>
    public static int? OverworldSource(Rom rom, int submap, int index, bool specialPassed = false)
    {
        foreach (var (dest, src, n, rows) in OverworldLoads(rom, submap, specialPassed))
            for (int r = 0; r < rows; r++)
                if (index - (dest + r * 16) is var c && c >= 0 && c < n) return src + (r * n + c) * 2;
        return null;
    }

    /// <summary>The submaps that load their colour from <paramref name="snes"/> — all seven for
    /// the shared tables, one or two for a submap's own set (Vanilla Dome and Valley of Bowser
    /// share theirs), always one under Lunar Magic's table. Same index in each, since every
    /// submap lays its colours out alike.</summary>
    public static int[] OverworldSharers(Rom rom, int index, int snes, bool specialPassed = false)
        => [.. Enumerable.Range(0, Overworld.Submaps).Where(s => OverworldSource(rom, s, index, specialPassed) == snes)];

    /// <summary>
    /// Where a colour word of the VANILLA tables lives in Lunar Magic's table: every (submap,
    /// colour) the vanilla loader fills from <paramref name="vanillaSnes"/>, in both halves, as
    /// table addresses. A shared word lands in all fourteen palettes; a set only the
    /// special-world-passed half loads lands in that half only. Empty when the ROM has no table
    /// or the word is not one the vanilla loader reads. How an edit made before prep v32 follows
    /// the colour it changed.
    /// </summary>
    public static IEnumerable<int> TableWordsFor(Rom rom, int vanillaSnes)
    {
        if (rom.LmOwPaletteTable is not (var table and >= 0)) yield break;
        for (int half = 0; half < 2; half++)
            for (int s = 0; s < Overworld.Submaps; s++)
                foreach (var (dest, src, n, rows) in VanillaOverworldLoads(rom, s, half == 1))
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < n; c++)
                            if (src + (r * n + c) * 2 == vanillaSnes && dest + r * 16 + c is var i and < 256)
                                yield return table + (s + half * Overworld.Submaps) * 0x200 + i * 2;
    }

    /// <summary>Whether <paramref name="snes"/> is a colour word some submap loads, before the
    /// special world is passed or after.</summary>
    public static bool IsOverworldColor(Rom rom, int snes)
        => Enumerable.Range(0, 2 * Overworld.Submaps)
                     .Any(k => Enumerable.Range(0, 256).Any(i =>
                         OverworldSource(rom, k % Overworld.Submaps, i, k >= Overworld.Submaps) == snes));

    /// <summary>The game's LoadColors ($00ACFF). A word with an entry in <paramref name="edits"/>
    /// (SNES address → BGR555) loads the edit instead of the ROM's bytes.</summary>
    private static void LoadColors(Rom rom, ushort[] cg, ColorLoad load, Dictionary<int, ushort> edits)
    {
        for (int r = 0; r < load.Rows; r++)
            for (int c = 0; c < load.N; c++)
            {
                int i = load.Dest + r * 16 + c, at = load.Src + (r * load.N + c) * 2;
                if (i >= 256) continue;
                cg[i] = edits.TryGetValue(at, out var v) ? v : RomWord(rom, at);
            }
    }

    /// <summary>The colour word the ROM itself holds at a SNES address, edits aside.</summary>
    public static ushort RomWord(Rom rom, int snes)
    {
        int fo = rom.FileOffset(snes);
        return (ushort)(rom.Data[fo] | (rom.Data[fo + 1] << 8));
    }

    /// <summary>SNES 15-bit BGR555 → RGBA8888 (0xAABBGGRR in memory: R,G,B,A bytes).</summary>
    public static uint ToRgba(ushort c)
    {
        int r = c & 0x1F, g = (c >> 5) & 0x1F, b = (c >> 10) & 0x1F;
        uint R = (uint)(r << 3 | r >> 2), G = (uint)(g << 3 | g >> 2), B = (uint)(b << 3 | b >> 2);
        return 0xFF000000u | (B << 16) | (G << 8) | R;
    }

    /// <summary>
    /// 8-bit-per-channel RGB → the nearest SNES BGR555. The inverse of <see cref="ToRgba"/>:
    /// it ROUNDS rather than truncating, so <c>ToBgr555(ToRgba(x)) == x</c> for all 32768
    /// colours. A `>> 3` truncation would fail that — 255 would come back as 31 but 248 would
    /// too, and a colour picker built on it would drift a step every time it read its own
    /// output back.
    /// </summary>
    public static ushort ToBgr555(byte r, byte g, byte b)
        => (ushort)((Five(b) << 10) | (Five(g) << 5) | Five(r));

    private static int Five(byte v) => (v * 31 + 127) / 255;

    /// <summary>The same, from a packed RGBA word as <see cref="ToRgba"/> produces.</summary>
    public static ushort ToBgr555(uint rgba)
        => ToBgr555((byte)(rgba & 0xFF), (byte)((rgba >> 8) & 0xFF), (byte)((rgba >> 16) & 0xFF));

    /// <summary>
    /// <paramref name="count"/> colours from <paramref name="from"/> to <paramref name="to"/>,
    /// both ends included and kept exactly, the ones between evenly spaced to the eye. Blended in
    /// OKLab, a perceptual space: black to white comes out as greys that step evenly in
    /// lightness rather than bunching at the dark end as sRGB numbers do, and green to blue
    /// passes through a clean teal instead of the greyed middle a straight RGB blend gives.
    /// Each colour is rounded to the nearest one the SNES can show.
    /// </summary>
    public static ushort[] Gradient(ushort from, ushort to, int count)
    {
        var g = new ushort[Math.Max(count, 0)];
        if (count == 0) return g;
        var (a, b) = (ToOklab(from), ToOklab(to));
        for (int k = 0; k < count; k++)
        {
            if (k == 0) { g[k] = from; continue; }
            if (k == count - 1) { g[k] = to; continue; }
            double t = (double)k / (count - 1);
            g[k] = FromOklab((a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t));
        }
        return g;
    }

    // OKLab (Björn Ottosson, 2020): linear-light RGB through an LMS cone space, cube-rooted.
    private static (double L, double A, double B) ToOklab(ushort bgr)
    {
        uint c = ToRgba(bgr);
        double r = ToLinear((c & 0xFF) / 255.0), g = ToLinear((c >> 8 & 0xFF) / 255.0), b = ToLinear((c >> 16 & 0xFF) / 255.0);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static ushort FromOklab((double L, double A, double B) o)
    {
        double l = Math.Pow(o.L + 0.3963377774 * o.A + 0.2158037573 * o.B, 3);
        double m = Math.Pow(o.L - 0.1055613458 * o.A - 0.0638541728 * o.B, 3);
        double s = Math.Pow(o.L - 0.0894841775 * o.A - 1.2914855480 * o.B, 3);
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        return ToBgr555(ToByte(r), ToByte(g), ToByte(b));
    }

    private static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>Linear light back to an sRGB byte, clamped: a blend between two colours the SNES
    /// can show can still land a hair outside the gamut.</summary>
    private static byte ToByte(double v)
    {
        v = Math.Clamp(v, 0, 1);
        v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(v * 255);
    }
}
