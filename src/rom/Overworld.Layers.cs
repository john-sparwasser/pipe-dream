namespace PipeDream;

// Overworld — how the two layers are stored and written back: layer 1's byte per cell (and
// Lunar Magic's high-byte table), layer 2's two RLE streams, and the room each has in the ROM.
// What the layers mean is in Overworld.cs; the level table beside layer 1 is in Overworld.Levels.cs.

public sealed partial class Overworld
{
    /// <summary>Layer 1 as the ROM holds it: vanilla's byte per cell at $0CF7DF under the high
    /// byte Lunar Magic keeps in its LZ2 table, where the ROM has one.</summary>
    private static ushort[] ReadLayer1(Rom rom, Tables at)
    {
        var lo = rom.Data.AsSpan(rom.FileOffset(Layer1Tilemap), 2 * MapTiles);
        var hi = at.Layer1HighBlob != 0 ? Gfx.Lz2Decompress(rom.Data, rom.FileOffset(at.Layer1HighBlob), 2 * MapTiles) : [];
        var words = new ushort[2 * MapTiles];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(lo[i] | (i < hi.Length ? hi[i] << 8 : 0));
        return words;
    }

    /// <summary>
    /// Write an edited layer 1 into the ROM: the low bytes in place at $0CF7DF, and the high
    /// bytes into Lunar Magic's table where the ROM has one and the packed table fits where the
    /// old one sat. Returns a reason when a tile from page 1 cannot be kept — a vanilla ROM has
    /// nowhere to put its high byte. ponytail: no relocation of LM's table, as with layer 2.
    /// </summary>
    public static string? WriteLayer1(Rom rom, ushort[] words)
    {
        // Decide before writing anything: a refusal must leave the ROM as it was, not with new
        // low bytes under the old high bytes — a map that draws with the wrong tiles.
        byte[]? packed = null;
        int blob = 0;
        if (words.Any(w => w > 0xFF))
        {
            var at = Tables.Of(rom);
            if (at.Layer1HighBlob == 0) return "layer 1 uses tiles 0x100+, which need Lunar Magic's high-byte table; this ROM has none";
            blob = rom.FileOffset(at.Layer1HighBlob);
            int room = Gfx.Lz2Length(rom.Data, blob);
            packed = Gfx.Lz2Compress([.. words.Select(w => (byte)(w >> 8))]);
            if (packed.Length > room) return $"layer 1's high bytes pack to {packed.Length} bytes, and Lunar Magic's table has room for {room}";
        }
        int lo = rom.FileOffset(Layer1Tilemap);
        for (int i = 0; i < 2 * MapTiles; i++) rom.Data[lo + i] = (byte)words[i];
        packed?.CopyTo(rom.Data, blob);
        return null;
    }

    /// <summary>
    /// The two layer 2 streams ($04DABA): a header byte with bit 7 clear copies the next n+1
    /// bytes, with bit 7 set repeats the next byte (n &amp; 0x7F)+1 times; each stream fills one
    /// byte of every word until 0x2000 words are out. Low bytes are tile numbers, high bytes
    /// the vhopppcc properties.
    /// </summary>
    public static ushort[] DecodeLayer2(Rom rom) => DecodeLayer2(rom, out _, out _);

    /// <summary>Decode, and say where each stream's bytes end — the room an edited map has.</summary>
    public static ushort[] DecodeLayer2(Rom rom, out int lowEnd, out int highEnd)
    {
        var words = new ushort[0x2000];
        int[] ends = new int[2];
        var at = Tables.Of(rom);
        foreach (var (snes, shift, k) in new[] { (at.Layer2Low, 0, 0), (at.Layer2High, 8, 1) })
        {
            int p = rom.FileOffset(snes), o = 0;
            while (o < words.Length)
            {
                int n = rom.Data[p++];
                if ((n & 0x80) == 0)
                    for (int i = 0; i <= n && o < words.Length; i++) words[o++] |= (ushort)(rom.Data[p++] << shift);
                else
                {
                    ushort v = (ushort)(rom.Data[p++] << shift);
                    for (int i = 0; i <= (n & 0x7F) && o < words.Length; i++) words[o++] |= v;
                }
            }
            ends[k] = p;
        }
        (lowEnd, highEnd) = (ends[0], ends[1]);
        return words;
    }

    /// <summary>The inverse of <see cref="DecodeLayer2"/> for one byte plane: runs of three or
    /// more repeat, everything else goes out as literals, both capped at the header's 128.</summary>
    public static byte[] EncodeStream(ReadOnlySpan<byte> plane)
    {
        var out_ = new List<byte>(plane.Length / 4);
        int i = 0;
        while (i < plane.Length)
        {
            int run = 1;
            while (i + run < plane.Length && run < 128 && plane[i + run] == plane[i]) run++;
            if (run >= 3) { out_.Add((byte)(0x80 | (run - 1))); out_.Add(plane[i]); i += run; continue; }
            int lit = 0;
            while (i + lit < plane.Length && lit < 128)
            {
                // Stop the literal where a run worth a repeat begins.
                int r = 1;
                while (i + lit + r < plane.Length && r < 3 && plane[i + lit + r] == plane[i + lit]) r++;
                if (r >= 3) break;
                lit++;
            }
            out_.Add((byte)(lit - 1));
            for (int k = 0; k < lit; k++) out_.Add(plane[i + k]);
            i += lit;
        }
        return [.. out_];
    }

    /// <summary>The two streams for a layer 2 map: tile numbers, then property bytes.</summary>
    public static (byte[] Low, byte[] High) EncodeLayer2(ushort[] words)
    {
        var lo = new byte[words.Length]; var hi = new byte[words.Length];
        for (int i = 0; i < words.Length; i++) { lo[i] = (byte)words[i]; hi[i] = (byte)(words[i] >> 8); }
        return (EncodeStream(lo), EncodeStream(hi));
    }

    /// <summary>
    /// Write an edited layer 2 into the ROM's own stream space. Each stream must fit where the
    /// ROM's one ended — the low stream runs up to the high stream's start, the high stream up
    /// to wherever the ROM's ends — because the loader hard-codes both addresses (LM's
    /// relocated ones included: the streams stay where <see cref="Tables.Of"/> found them).
    /// Returns a reason when it does not fit. ponytail: no relocation; a map that packs worse
    /// than the ROM's is refused rather than moved, until the loader's operands are re-pointed.
    /// </summary>
    public static string? WriteLayer2(Rom rom, ushort[] words)
    {
        DecodeLayer2(rom, out int lowEnd, out int highEnd);
        var (lo, hi) = EncodeLayer2(words);
        var at = Tables.Of(rom);
        int loAt = rom.FileOffset(at.Layer2Low), hiAt = rom.FileOffset(at.Layer2High);
        // Each stream's room is the stream's own length: the bytes it occupies are the only
        // ones known to be its. Measuring the low stream up to the high stream's start assumed
        // they sit side by side, which a relocated pair need not.
        int loRoom = lowEnd - loAt, hiRoom = highEnd - hiAt;
        if (lo.Length > loRoom || hi.Length > hiRoom)
            return $"the overworld's layer 2 packs to {lo.Length}+{hi.Length} bytes, and the ROM has room for {loRoom}+{hiRoom}";
        lo.CopyTo(rom.Data, loAt);
        hi.CopyTo(rom.Data, hiAt);
        return null;
    }
}
