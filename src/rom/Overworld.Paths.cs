namespace PipeDream;

// Overworld — what a layer 1 tile does underfoot ($049140, $0492F2): the path kinds Mario walks,
// climbs and swims, the exit tiles, and Lunar Magic's own pictures for them.

public sealed partial class Overworld
{
    /// <summary>
    /// Path tiles are 0x01-0x55. Their pose byte at $049FEB (read at $0495EF) says how Mario
    /// moves on them: bit 3 swims (0x28-0x3E, 0x50), bit 4 climbs (0x3F-0x41, the ladder tiles,
    /// which also set $1B80 for the ladder speed), anything else walks — bit 2 only picks the
    /// front-facing walk frames, not a ladder. The ten tiles listed at $049426 are the exit tiles
    /// that fire the map-to-map check at $049A24. 0x56-0x82 are level tiles; 0x83-0x86 can be
    /// stood on but not entered. Checked tile by tile against Lunar Magic's "Layer 1 Mario
    /// Paths" view on 2026-09-06 (a grid ROM of every tile): green walk, black rungs climb, blue
    /// swim, red exit, an X where Mario stops without entering.
    /// </summary>
    public enum PathKind { None, Walk, Climb, Swim, Exit, Level, Stop }

    public const int PathPoses = 0x049FEB, ExitTilesList = 0x049426;

    /// <summary>
    /// Lunar Magic's own picture for a layer 1 path tile, 16x16 RGBA with 0 for transparent, or
    /// null for a tile it draws nothing special for. Lifted pixel for pixel from LM's "Layer 1
    /// Mario Paths" view on 2026-09-06: a ROM whose layer 1 was a grid of every tile, captured
    /// with the view off and on over two different stretches of land, "Future Layer 1 Tiles" off
    /// so the level tiles' octagons are opaque, keeping only the pixels both captures agreed on
    /// (the unused tiles 0x52-0x55 LM draws nothing opaque for; they fall back to
    /// <see cref="KindOf"/>'s fill). Level tiles all wear one complete octagon — their own fill,
    /// green or LM's blue for a level in water, with a black pixel round it — since the capture
    /// lost outline pixels wherever a level tile sat on other art; the four stop tiles wear one
    /// filled X over it, the shape the capture got whole.
    /// The same repair closed every other hole the capture left — a rung's middle pixel, the top
    /// of a sideways path, the fish's insides — so no picture has a pixel the land shows through
    /// where it should be drawn: a hole beside a coloured pixel takes the colour around it. OwPathGlyphs.bin: a palette count, RGB triples,
    /// then 256 palette indexes per tile for tiles 0x00-0x86.
    /// </summary>
    public static uint[]? PathGlyph(int tile)
    {
        var all = glyphs ??= LoadGlyphs();
        return tile >= 0 && tile < all.Length ? all[tile] : null;
    }

    private static uint[]?[]? glyphs;

    private static uint[]?[] LoadGlyphs()
    {
        var table = new uint[]?[0x87];
        try
        {
            using var s = typeof(Overworld).Assembly.GetManifestResourceStream("OwPathGlyphs.bin");
            if (s is null) return table;
            var d = new byte[s.Length];
            s.ReadExactly(d);
            int n = d[0], p = 1;
            var pal = new uint[n + 1];
            for (int i = 1; i <= n; i++, p += 3) pal[i] = 0xFF000000u | (uint)d[p + 2] << 16 | (uint)d[p + 1] << 8 | d[p];
            for (int t = 0; t < table.Length && p + 256 <= d.Length; t++, p += 256)
            {
                bool any = false;
                var px = new uint[256];
                for (int i = 0; i < 256; i++) if (d[p + i] != 0) { px[i] = pal[d[p + i]]; any = true; }
                if (any) table[t] = px;
            }
        }
        catch { /* no pictures, no crash: the kind fills stand in */ }
        return table;
    }

    public PathKind KindOf(int tile)
    {
        if (tile <= 0 || tile > 0x86) return PathKind.None;
        if (tile >= 0x83) return PathKind.Stop;
        if (tile >= 0x56) return PathKind.Level;
        var d = Rom.Data;
        int ex = Rom.FileOffset(ExitTilesList);
        for (int i = 0; i < 10; i++) if (d[ex + i] == tile) return PathKind.Exit;
        int pose = d[Rom.FileOffset(PathPoses) + tile - 1];
        return (pose & 0x10) != 0 ? PathKind.Climb : (pose & 8) != 0 ? PathKind.Swim : PathKind.Walk;
    }
}
