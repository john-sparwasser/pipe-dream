namespace PipeDream;

// Overworld — Lunar Magic's custom overworld sprite list: where it lives, how an entry is packed,
// and the write-back. The game ignores the list until an overworld sprite system (BOWSIE's, or
// Lui37's VLDC9 patch it grew from) reads it; Lunar Magic and Pipe Dream only place the sprites.

public sealed partial class Overworld
{
    /// <summary>
    /// Lunar Magic's custom sprite list (reference/lm-help/html/info_custom_ov_sprite.htm): a
    /// 24-bit pointer here, 0 or $FFFFFF for none. The block it names opens with seven words, one
    /// per submap, each an offset from the block's start to that submap's entries; each list ends
    /// with a zero word. An entry is three bytes then its extra bytes —
    /// <c>xnnnnnnn yyyxxxxx zzzzzyyy</c>, sprite number 1-7F, X and Y in 8px steps over the map
    /// (0-63; the six submaps' on the submap map), Z the height above it in 8px steps.
    /// </summary>
    public const int SpriteList = 0x0EF55D;

    /// <summary>The extra-byte size table for overworld sprites (LM 3.51+): <c>dl table : db $42</c>.
    /// One byte per sprite number, the entry's whole length. Without the $42 every entry is four
    /// bytes long — one extra byte — as before LM had the table.</summary>
    public const int SpriteSizes = 0x0DE18C;

    /// <summary>Lunar Magic's limit, per submap.</summary>
    public const int MaxSpritesPerSubmap = 0x18;

    /// <summary>One placed sprite. <paramref name="Submap"/> is the list it is in (0 the main map,
    /// 1-6 the others in Lunar Magic's order) — the six share one map, so where a
    /// sprite stands does not say which list it belongs to.</summary>
    public sealed record OwSprite(int Submap, int Number, int X, int Y, int Z, byte[] Extra);

    /// <summary>The list as the editor holds it: the ROM's edited copy, so an edit shows here and
    /// builds from one list.</summary>
    public List<OwSprite> Sprites => Rom.OwSprites ??= ReadSprites(Rom);

    /// <summary>How many extra bytes a sprite number's entries carry in this ROM.</summary>
    public static int SpriteExtraCount(Rom rom, int number)
    {
        if (rom.OwSpriteExtra is { } over) return over[number & 0x7F];
        var d = rom.Data;
        int p = rom.FileOffset(SpriteSizes);
        if (d[p + 3] != 0x42) return 1;
        // LM 3.51-3.59 kept a byte for sprite 00 at the front; 3.60 dropped it (BOWSIE's vldc9.asm
        // reads table-1 from 3.60 on). A ROM with no LM version string is ours, and reads as 3.60+.
        int v = rom.FileOffset(0x0FF0B4);
        bool lm35x = d[v] == '3' && d[v + 2] == '5' && d[v + 3] >= '1';
        int table = rom.FileOffset(d[p] | d[p + 1] << 8 | d[p + 2] << 16);
        return Math.Max(0, d[table + ((lm35x ? number : number - 1) & 0x7F)] - 3);
    }

    public static List<OwSprite> ReadSprites(Rom rom)
    {
        var d = rom.Data;
        int p = rom.FileOffset(SpriteList), at = d[p] | d[p + 1] << 8 | d[p + 2] << 16;
        return at is 0 or 0xFFFFFF ? [] : UnpackSprites(rom, d, rom.FileOffset(at));
    }

    /// <summary>A packed block (<see cref="PackSprites"/>) back into entries, from
    /// <paramref name="b"/> in <paramref name="d"/>.</summary>
    public static List<OwSprite> UnpackSprites(Rom rom, byte[] d, int b = 0)
    {
        var list = new List<OwSprite>();
        for (int m = 0; m < Submaps; m++)
        {
            int e = b + (d[b + 2 * m] | d[b + 2 * m + 1] << 8);
            // ponytail: stops at LM's limit, not just the zero word, so a broken offset reads as
            // garbage on one map rather than walking off the ROM.
            for (int n = 0; n < MaxSpritesPerSubmap && e + 1 < d.Length && (d[e] | d[e + 1]) != 0; n++)
            {
                int b0 = d[e], b1 = d[e + 1], b2 = d[e + 2], number = b0 & 0x7F;
                int extra = SpriteExtraCount(rom, number);
                list.Add(new(m, number, b0 >> 7 | (b1 & 0x1F) << 1, b1 >> 5 | (b2 & 7) << 3, b2 >> 3,
                             d.AsSpan(e + 3, extra).ToArray()));
                e += 3 + extra;
            }
        }
        return list;
    }

    /// <summary>The list as Lunar Magic stores it: the seven offsets, then each submap's entries
    /// and its zero word. An entry's extra bytes are cut or zero-padded to what its number takes.</summary>
    public static byte[] PackSprites(Rom rom, IReadOnlyList<OwSprite> sprites)
    {
        var block = new List<byte>(new byte[2 * Submaps]);
        for (int m = 0; m < Submaps; m++)
        {
            block[2 * m] = (byte)block.Count;
            block[2 * m + 1] = (byte)(block.Count >> 8);
            foreach (var s in sprites.Where(s => s.Submap == m))
            {
                block.Add((byte)(s.Number & 0x7F | (s.X & 1) << 7));
                block.Add((byte)((s.X >> 1) & 0x1F | (s.Y & 7) << 5));
                block.Add((byte)((s.Y >> 3) & 7 | (s.Z & 0x1F) << 3));
                var extra = new byte[SpriteExtraCount(rom, s.Number)];
                s.Extra.AsSpan(0, Math.Min(extra.Length, s.Extra.Length)).CopyTo(extra);
                block.AddRange(extra);
            }
            block.Add(0); block.Add(0);
        }
        return [.. block];
    }

    /// <summary>Write the list back: the old block is released when it is ours (RATS-tagged, as
    /// LM's is) and the new one allocated in one bank — the game adds the offsets to a 16-bit
    /// pointer. An empty list writes the vanilla $FFFFFF, or 0 under BOWSIE, whose loader takes only a zero bank as no list
    /// and would read the sprites from $FFFFFF. Returns a reason when it cannot fit.</summary>
    public static string? WriteSprites(Rom rom, IReadOnlyList<OwSprite> sprites)
    {
        int p = rom.FileOffset(SpriteList), old = rom.Data[p] | rom.Data[p + 1] << 8 | rom.Data[p + 2] << 16;
        if (old is not (0 or 0xFFFFFF)) RatsWriter.Release(rom, old);
        int at = OwSpriteInsert.Present(rom) ? 0 : 0xFFFFFF;
        if (sprites.Count > 0)
        {
            try { at = RatsWriter.Allocate(rom, PackSprites(rom, sprites), avoidBankCross: true); }
            catch (InvalidOperationException e) { return "overworld sprites: " + e.Message; }
        }
        rom.Data[p] = (byte)at; rom.Data[p + 1] = (byte)(at >> 8); rom.Data[p + 2] = (byte)(at >> 16);
        return null;
    }
}
