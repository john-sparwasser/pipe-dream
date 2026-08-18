namespace PipeDream;

/// <summary>
/// The layer-2 background image codec (CONTRACT §10). A background is an RLE stream living
/// in bank $0C, decoded into a buffer of BG Map16 def indices that the loader pre-fills with
/// the blank tile and then tiles horizontally across the level.
///
/// Two things constrain writing one back:
///
/// * The page byte is NOT in the data — the loader derives it from the pointer, page 1 when
///   the low 16 bits are >= $E8FE. So MOVING a background silently recolours every tile in
///   it. Rewriting in place is the only address-safe option, which is why
///   <see cref="Encode"/> is judged by whether it fits the original stream.
/// * The stream must live in bank $0C, because the loader hardcodes that bank; a RATS block
///   anywhere else is unreachable.
/// </summary>
public static class BgImage
{
    /// <summary>Buffer the loader fills: two 16x27 screens is 0x360 tiles, but it decodes up
    /// to 0x400 and the extra is harmless.</summary>
    public const int Tiles = 0x400;

    /// <summary>The tile the loader pre-fills with, so trailing runs of it need no bytes.</summary>
    public const byte Blank = 0x25;

    /// <summary>Bank $0C holds every vanilla background, and the loader reads only there.</summary>
    public const int Bank = 0x0C0000;

    /// <summary>Page byte for a stream at <paramref name="lo16"/> ($058046: `CPX #$E8FE : BCC`
    /// — inclusive, which is why level $10A sitting exactly on $E8FE is page 1).</summary>
    public static int PageFor(int lo16) => lo16 >= 0xE8FE ? 1 : 0;

    /// <summary>Every background a ROM actually uses, as (address, page, the levels pointing
    /// at it), in address order. Vanilla has 17 sharing them across ~150 levels. This is the
    /// set a level can be pointed at safely — each is a known-good stream at an address whose
    /// page it was authored for.</summary>
    public static List<(int Lo16, int Page, List<int> Levels)> Catalog(Rom rom)
    {
        var by = new Dictionary<int, List<int>>();
        for (int lvl = 0; lvl < Rom.LevelCount; lvl++)
        {
            if (!rom.Layer2IsBackground(lvl)) continue;
            int lo16 = rom.Layer2Pointer(lvl) & 0xFFFF;
            if (!by.TryGetValue(lo16, out var list)) by[lo16] = list = new List<int>();
            list.Add(lvl);
        }
        return by.OrderBy(kv => kv.Key)
                 .Select(kv => (kv.Key, PageFor(kv.Key), kv.Value))
                 .ToList();
    }

    /// <summary>Decode the stream at <paramref name="lo16"/> into low tile bytes.
    /// <paramref name="consumed"/> is the stream's byte length INCLUDING the FF FF
    /// terminator, which is what an in-place rewrite has to fit inside.</summary>
    public static byte[] Decode(Rom rom, int lo16, out int consumed)
    {
        var low = new byte[Tiles];
        Array.Fill(low, Blank);
        int start = rom.FileOffset(Bank | lo16);
        int p = start, o = 0;
        while (o < Tiles && p + 1 < rom.Data.Length)
        {
            int cmd = rom.Data[p++];
            if (cmd == 0xFF && rom.Data[p] == 0xFF) { p++; break; }
            int count = (cmd & 0x7F) + 1;
            if ((cmd & 0x80) != 0)
            {
                byte b = rom.Data[p++];
                for (int i = 0; i < count && o < Tiles; i++) low[o++] = b;
            }
            else
                for (int i = 0; i < count && o < Tiles; i++) low[o++] = rom.Data[p++];
        }
        consumed = p - start;
        return low;
    }

    /// <summary>Encode low tile bytes back to an RLE stream, terminator included.
    ///
    /// Greedy: a run costs 2 bytes and a literal costs 1 per byte plus a block header, so a
    /// run only pays from length 3 up — switching out of an open literal block to encode a
    /// pair would cost the same and then charge a fresh header for whatever follows.
    /// Trailing <see cref="Blank"/> is dropped because the loader already pre-filled it.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> low)
    {
        int end = low.Length;
        while (end > 0 && low[end - 1] == Blank) end--;

        var outb = new List<byte>(256);
        var lit = new List<byte>(128);
        void FlushLit()
        {
            for (int i = 0; i < lit.Count; i += 128)
            {
                int n = Math.Min(128, lit.Count - i);
                outb.Add((byte)(n - 1));                       // copy: bit7 clear, count-1
                for (int j = 0; j < n; j++) outb.Add(lit[i + j]);
            }
            lit.Clear();
        }

        for (int p = 0; p < end;)
        {
            int run = 1;
            while (p + run < end && low[p + run] == low[p] && run < 128) run++;
            // A 128-long run encodes its command as $FF; with $FF as the value that spells
            // the FF FF terminator, so shorten it and let the remainder encode separately.
            if (run == 128 && low[p] == 0xFF) run = 127;
            if (run >= 3)
            {
                FlushLit();
                outb.Add((byte)(0x80 | (run - 1)));
                outb.Add(low[p]);
                p += run;
            }
            else lit.Add(low[p++]);
        }
        FlushLit();
        outb.Add(0xFF);
        outb.Add(0xFF);
        return outb.ToArray();
    }
}
