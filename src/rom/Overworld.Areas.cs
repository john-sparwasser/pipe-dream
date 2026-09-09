namespace PipeDream;

// Overworld — the two areas Lunar Magic shows beside the maps in its Layer 2 8x8 editor and in no
// other mode: the layer 2 event pieces' own tiles, and layer 1's Map16 definitions. Both are 8x8
// words like the land, so the same brush paints them; both are fixed tables written in place.

public sealed partial class Overworld
{
    /// <summary>
    /// The layer 2 event pieces: 0xD00 8x8 cells, the tile byte at $0C8000 and the property byte
    /// in an RLE stream at $0C8D00 (the same encoding as layer 2's two streams), joined here into
    /// words. The first 0x900 bytes are 64 pieces of 6x6, the rest 256 of 2x2 — the split the
    /// game itself makes, taking a step's src &gt;= 0x900 as a 2x2 ($04E4D0 / $04E520).
    /// </summary>
    public const int EventTiles = 0x0C8000, EventProps = 0x0C8D00, EventCells = 0xD00, Event6Bytes = 0x900;

    /// <summary>
    /// How the pieces are laid out: the 6x6s eight across, then the 2x2s twenty-four across
    /// BELOW them rather than in a column beside them, so the area is one 48-wide strip instead
    /// of two. Lunar Magic puts the two columns side by side (measured off its own status bar
    /// 2026-09-08 — its piece index steps by 1 across and by 0x18 down); stacking them keeps the
    /// canvas narrower, and which cell holds which piece byte is this editor's own arrangement,
    /// not something the ROM cares about.
    /// </summary>
    public const int Event6Size = 6, Event2Size = 2, Event6Across = 8, Event2Across = 24;

    /// <summary>Where the 2x2s begin: under the last row of 6x6s.</summary>
    public static int Event2Row => Event6Bytes / (Event6Size * Event6Size) / Event6Across * Event6Size;

    /// <summary>
    /// The tile Lunar Magic lays over every cell of its 8x8 canvas that no table reaches — the
    /// "X tiles" its help talks about: 8x8 tile 0x7A, a black cross on blue, in palette row 5.
    /// Read off LM's own status bar and pixels on a vanilla ROM (2026-09-08): it reports tile 7A
    /// under the cursor out there, and 0x7A's pixels are colours 1 and 4, which row 5 paints
    /// black on the sea's blue. Chrome, not data — nothing is stored at those cells.
    /// </summary>
    public const int FillerWord = 0x7A | 5 << 10;

    /// <summary>
    /// The word vanilla leaves in every event cell no piece draws with: tile 0x0BA in palette row
    /// 3, a mottled grey. It means nothing — Lunar Magic's own blank out there is its X tile — and
    /// a screenful of grey noise reads as data rather than as empty space, so the editor draws
    /// this as the X too. Display only: the bytes stay as the ROM has them until something paints
    /// over them, and a cell painted with something else draws what it was given.
    /// </summary>
    public const int BlankEventWord = 0x0BA | 3 << 10;

    private uint[]? fillerPx;

    /// <summary>
    /// The filler drawn from the FILE's graphics rather than the map's. Tile 0x7A is one of the
    /// eight cycling animated slots (0x78-0x7F), so the map's copy of it is whichever frame of
    /// GFX14 is showing — a cross that flickers is worse than no marker at all. The X is what the
    /// file holds underneath, which is what Lunar Magic shows out there.
    /// </summary>
    public uint[] FillerPixels()
    {
        if (fillerPx is { } done) return done;
        var src = Gfx.FgTiles.Load(Rom, Tileset, levelAnimation: false).Fetch(FillerWord & 0x3FF);
        var p = PaletteOf(0);
        var img = new uint[64];
        for (int i = 0; i < 64; i++)
            img[i] = src[i] == 0 ? p.Rgba[0] | 0xFF000000 : p.Rgba[(FillerWord >> 10 & 7) * 16 + src[i]];
        return fillerPx = img;
    }

    public ushort[] EventPieces => Rom.OwEventPieces ??= ReadEventPieces(out _);

    private ushort[] ReadEventPieces(out int propsEnd)
    {
        var words = new ushort[EventCells];
        int t = Rom.FileOffset(EventTiles);
        for (int i = 0; i < EventCells; i++) words[i] = Rom.Data[t + i];
        int p = Rom.FileOffset(EventProps), o = 0;
        while (o < EventCells)
        {
            int n = Rom.Data[p++];
            if ((n & 0x80) == 0)
                for (int i = 0; i <= n && o < EventCells; i++) words[o++] |= (ushort)(Rom.Data[p++] << 8);
            else
            {
                ushort v = (ushort)(Rom.Data[p++] << 8);
                for (int i = 0; i <= (n & 0x7F) && o < EventCells; i++) words[o++] |= v;
            }
        }
        propsEnd = p;
        return words;
    }

    /// <summary>Write the pieces back: the tiles in place, and the properties re-packed into the
    /// stream the ROM's own occupied. A reason when the properties pack larger than that — as with
    /// layer 2, nothing is relocated.</summary>
    public static string? WriteEventPieces(Rom rom, ushort[] words)
    {
        var ow = new Overworld(rom);
        ow.ReadEventPieces(out int propsEnd);
        int t = rom.FileOffset(EventTiles), p = rom.FileOffset(EventProps);
        var props = new byte[EventCells];
        for (int i = 0; i < EventCells && i < words.Length; i++)
        {
            rom.Data[t + i] = (byte)words[i];
            props[i] = (byte)(words[i] >> 8);
        }
        var packed = EncodeStream(props);
        int room = propsEnd - p;
        if (packed.Length > room)
            return $"the event pieces' properties pack to {packed.Length} bytes, and the ROM has room for {room}";
        packed.CopyTo(rom.Data, p);
        return null;
    }

    /// <summary>The cell a piece byte is drawn at on Lunar Magic's canvas, relative to the event
    /// area's own top-left: the 6x6 column, then the 2x2 column beside it.</summary>
    public static (int X, int Y) EventCellOf(int index)
    {
        if (index < Event6Bytes)
        {
            int piece = index / (Event6Size * Event6Size), k = index % (Event6Size * Event6Size);
            return (piece % Event6Across * Event6Size + k % Event6Size,
                    piece / Event6Across * Event6Size + k / Event6Size);
        }
        int i2 = index - Event6Bytes, p2 = i2 / (Event2Size * Event2Size), k2 = i2 % (Event2Size * Event2Size);
        return (p2 % Event2Across * Event2Size + k2 % Event2Size,
                Event2Row + p2 / Event2Across * Event2Size + k2 / Event2Size);
    }

    /// <summary>The piece byte drawn at a cell of the event area, or -1 where nothing is.</summary>
    public static int EventIndexOf(int x, int y)
    {
        if (y < Event2Row)
        {
            if (x >= Event6Across * Event6Size) return -1;
            int piece = y / Event6Size * Event6Across + x / Event6Size;
            if (piece >= Event6Bytes / (Event6Size * Event6Size)) return -1;
            return piece * Event6Size * Event6Size + y % Event6Size * Event6Size + x % Event6Size;
        }
        if (x >= Event2Across * Event2Size) return -1;
        int y2 = y - Event2Row;
        int p2 = y2 / Event2Size * Event2Across + x / Event2Size;
        int at = Event6Bytes + p2 * Event2Size * Event2Size + y2 % Event2Size * Event2Size + x % Event2Size;
        return at < EventCells ? at : -1;
    }

    /// <summary>The widest and tallest the two stacked bands run — the area's size in 8x8 cells.</summary>
    public static (int Cols, int Rows) EventArea
        => (Math.Max(Event6Across * Event6Size, Event2Across * Event2Size),
            EventCellOf(EventCells - 1).Y + 1);

    /// <summary>
    /// Layer 1's Map16 definitions as 8x8 words — four per 16x16 tile, TL BL TR BR, the order
    /// <see cref="Map16.Compose"/> takes them in. Lunar Magic draws them below the submaps,
    /// sixteen tiles across, and lets the 8x8 brush edit them there (measured 2026-09-08).
    /// The ROM's edited copy, so an edit shows on every map at once.
    /// </summary>
    public const int Layer1DefsAcross = 16;

    public ushort[] Layer1Defs => Rom.OwLayer1Defs ??= ReadLayer1Defs();

    private ushort[] ReadLayer1Defs()
    {
        var words = new ushort[Map16Count * 4];
        int at = Rom.FileOffset(At.Map16Defs);
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)(Rom.Data[at + 2 * i] | Rom.Data[at + 2 * i + 1] << 8);
        return words;
    }

    /// <summary>Write the definitions back where the loader reads them — a fixed table, so this
    /// cannot fail to fit.</summary>
    public static void WriteLayer1Defs(Rom rom, ushort[] words)
    {
        var at = Tables.Of(rom);
        int p = rom.FileOffset(at.Map16Defs);
        int n = Math.Min(words.Length, at.Map16Count * 4);
        for (int i = 0; i < n; i++) { rom.Data[p + 2 * i] = (byte)words[i]; rom.Data[p + 2 * i + 1] = (byte)(words[i] >> 8); }
    }
}
