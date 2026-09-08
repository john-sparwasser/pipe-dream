namespace PipeDream;

/// <summary>
/// The overworld as SMW stores it, read from the ROM and drawn the way Lunar Magic draws it:
/// one canvas, the main map on top and the map that holds all six submaps beneath it.
///
/// Two maps, each 32x32 16x16-tiles. The main map is index 0x000-0x3FF; the six submaps share
/// ONE second map (index 0x400-0x7FF), laid out two columns by three rows, and a "submap" is
/// only a fixed camera plus a palette — the tiles are all in the same table. Layer 2 is the
/// land, 8x8 tiles from two RLE streams; layer 1 is the level tiles, clouds and Mario's
/// invisible path tiles, one byte per 16x16 cell, drawn through the overworld's own Map16
/// table. Lunar Magic moves the layer 2 streams and the Map16 table when it saves an
/// overworld, and adds a second byte to every layer 1 cell; <see cref="Tables"/> reads where
/// this ROM keeps them out of the loader's own operands, so an LM-edited map reads as LM
/// wrote it (reference/OVERWORLD.md §11; bank 04 traced in reference/smw-disasm).
///
/// This file is the map's shape: where the vanilla tables live, the two layers, and how a cell
/// is addressed. The rest is one file per concern —
///   Overworld.Tables.cs        where THIS ROM keeps the tables Lunar Magic relocates
///   Overworld.Layers.cs        how the layers are stored, decoded and written back
///   Overworld.Graphics.cs      tiles, palettes, animation and the pixels of a cell
///   Overworld.Paths.cs         what a layer 1 tile does underfoot, and LM's pictures for it
///   Overworld.Levels.cs        the level each tile enters and the event its exit fires
///   Overworld.Transitions.cs   warps, exit tiles and Koopa Kid drops
///   Overworld.Events.cs        the pieces an event lays on the land
/// </summary>
public sealed partial class Overworld
{
    // ---- where the tables live (vanilla; Tables.Of says where THIS ROM keeps them) ----
    public const int Layer1Tilemap = 0x0CF7DF;         // 0x800 bytes: tile number per 16x16 cell
    public const int Map16Defs = 0x05D000;             // 8 bytes per tile: words TL, BL, TR, BR
    public const int VanillaMap16Count = 0xC1;         // the next table starts at $05D608
    public const int Layer2Low = 0x04A533;             // RLE stream of 8x8 tile numbers
    public const int Layer2High = 0x04C02B;            // RLE stream of their property bytes

    /// <summary>Where this ROM keeps its tables.</summary>
    public Tables At { get; }

    /// <summary>How many Map16 tiles layer 1 can name: 0xC1 in vanilla, two pages once LM has saved.</summary>
    public int Map16Count => At.Map16Count;

    /// <summary>GFX list row for the main map; submap n uses row Tileset + n (GFX1C 1D 08 1E on
    /// every one of them in vanilla, so the rows exist for Lunar Magic to bypass per submap).</summary>
    public const int Tileset = 0x11;

    public const int SpriteSet = 0x11;

    public const int Cols = 32, Rows = 32;             // one map, in 16x16 tiles
    public const int MapTiles = Cols * Rows;           // 0x400
    public const int Submaps = 7;                      // main + six

    public Rom Rom { get; }

    /// <summary>Layer 1, 0x800 cells: main map then the submap map, in the engine's index order.
    /// Vanilla's byte from $0CF7DF, under the high byte LM keeps in its $7FC800 blob.</summary>
    public ushort[] Layer1 { get; }

    /// <summary>Layer 2, 0x2000 words: two 64x64 8x8 tilemaps in SNES screen order. The ROM's
    /// edited copy when the project has one, so an edit shows here and builds from one array.</summary>
    public ushort[] Layer2 { get; }

    private readonly Map16.Word[][] defs;

    public Overworld(Rom rom)
    {
        Rom = rom;
        At = Tables.Of(rom);
        Layer1 = rom.OwLayer1 ??= ReadLayer1(rom, At);
        Translevels = HasLevelTable ? rom.OwTranslevels ??= ReadLevelTable(rom, At) : new byte[2 * MapTiles];
        if (!HasLevelTable) Renumber();
        Layer2 = rom.OwLayer2 ??= DecodeLayer2(rom);
        BaseEventTable = rom.OwBaseEvents ??= rom.Data.AsSpan(rom.FileOffset(BaseEvents), BaseEventCount).ToArray();
        ExitDirTable = rom.OwExitDirs ??= rom.Data.AsSpan(rom.FileOffset(ExitDirs), ExitDirCount).ToArray();
        defs = new Map16.Word[Map16Count][];
        int d = rom.FileOffset(At.Map16Defs);
        for (int t = 0; t < Map16Count; t++)
            defs[t] = [.. Enumerable.Range(0, 4).Select(q =>
                new Map16.Word((ushort)(rom.Data[d + t * 8 + q * 2] | (rom.Data[d + t * 8 + q * 2 + 1] << 8))))];
    }

    /// <summary>The engine's layer 1 index for a 16x16 cell ($049885): the map is four 16x16-tile
    /// screens, TL TR BL BR, each 0x100 bytes.</summary>
    public static int Layer1Index(int x, int y, bool submapMap)
        => (x & 0xF) | ((x & 0x10) << 4) | ((y & 0xF) << 4) | ((y & 0x10) != 0 ? 0x200 : 0) | (submapMap ? MapTiles : 0);

    /// <summary>The layer 2 word index for an 8x8 cell (0-63 each way): a 64x64 SNES tilemap is
    /// four 32x32 screens of 0x400 words, TL TR BL BR — the same quadrant order as layer 1.</summary>
    public static int Layer2Index(int cx, int cy, bool submapMap)
        => ((cy >> 5) * 2 + (cx >> 5)) * 0x400 + (cy & 31) * 32 + (cx & 31) + (submapMap ? 0x1000 : 0);

    public int Layer1At(int x, int y, bool submapMap) => Layer1[Layer1Index(x, y, submapMap)];

    /// <summary>
    /// Which submap a cell belongs to, for its palette. The six submaps sit on the second map
    /// two columns by three rows; their fixed cameras ($049A0C) show the left column from tile
    /// X 0 and the right from X 16, and the rows overlap on screen, so the split is a choice.
    /// Lunar Magic's, read off its own pixels on 2026-09-06, changes palette at 8x8 rows 22 and
    /// 43 of the lower map as it draws it — see <see cref="SubmapAtRow8"/>. This 16x16 form, for
    /// layer 1 art in the map's own grid, rounds those to tiles 11 and 21.
    /// </summary>
    public static int SubmapAt(int x, int y, bool submapMap)
        => !submapMap ? 0 : 1 + (y < 11 ? 0 : y < 21 ? 1 : 2) + (x >= 16 ? 3 : 0);

    /// <summary>The submap for a cell of the lower map by its DRAWN 8x8 column and row on Lunar
    /// Magic's canvas: the palette changes at rows 22 and 43, and at column 32.</summary>
    public const int SubmapRow8Middle = 22, SubmapRow8Bottom = 43;

    public static int SubmapAtRow8(int col8, int row8)
        => 1 + (row8 < SubmapRow8Middle ? 0 : row8 < SubmapRow8Bottom ? 1 : 2) + (col8 >= 32 ? 3 : 0);
}
