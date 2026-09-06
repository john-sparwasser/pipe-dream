namespace PipeDream.Services;

// EditorSession — the overworld: the map as the ROM holds it, and the layer 2 editor over it,
// for the Overworld canvas. Read from the open ROM once and kept until the ROM changes. The
// rest of the session's state is in EditorSession.cs.
public sealed partial class EditorSession
{
    private Overworld? overworld;
    private TilemapEdit? owMap;

    /// <summary>The open ROM's overworld, or null without a ROM.</summary>
    public Overworld? Overworld
    {
        get
        {
            if (Rom is not { } r) return null;
            if (overworld?.Rom != r) { overworld = new Overworld(r); owMap = null; }
            return overworld;
        }
    }

    /// <summary>The canvas in 16x16 cells: the main map's 32 rows over the submap map's 32.</summary>
    public const int OwCols = Overworld.Cols, OwRows = 2 * Overworld.Rows;
    /// <summary>The same canvas in 8x8 cells, which is what layer 2 is made of.</summary>
    public const int Ow8Cols = 2 * OwCols, Ow8Rows = 2 * OwRows;

    /// <summary>
    /// Layer 2 as an editable tilemap: 128 rows of 64 8x8 words, the main map over the submap
    /// map. A commit lands the words in the ROM's edited copy (the same array the overworld
    /// draws from) and in the project, so the canvas, the saved file and the build agree.
    /// </summary>
    public TilemapEdit? OwMap
    {
        get
        {
            if (Overworld is not { } ow) return null;
            if (owMap is not null) return owMap;
            var cells = new int[ow.Layer2.Length];
            for (int i = 0; i < cells.Length; i++) cells[i] = ow.Layer2[i];
            var map = new TilemapEdit(cells, Ow8Cols, Ow8Rows, 8,
                                      (c, r) => Overworld.Layer2Index(c, r % (2 * Overworld.Rows), r >= 2 * Overworld.Rows));
            map.Committed += () =>
            {
                for (int i = 0; i < cells.Length; i++) ow.Layer2[i] = (ushort)cells[i];
                if (Project is not null)
                {
                    Project.Data.Overworld.Layer2 = Convert.ToBase64String(ProjectSession.BytesOf(ow.Layer2));
                    Project.MarkDirty();
                }
            };
            return owMap = map;
        }
    }

    /// <summary>Whether the overworld's layer 2 differs from the base ROM's.</summary>
    public bool OwEdited => Project?.Data.Overworld.Layer2 is not null;

    /// <summary>An 8x8 canvas cell (row-major over <see cref="Ow8Cols"/>): the layer 2 word the
    /// editor holds there, and nothing else — what the Tiles tab paints and moves.</summary>
    public uint[]? Ow8CellPixels(int cell)
    {
        if (Overworld is not { } ow || OwMap is not { } map) return null;
        int cx = cell % Ow8Cols, cy = cell / Ow8Cols, my = cy % (2 * Overworld.Rows);
        return ow.TilePixels(map.At(cx, cy), Overworld.SubmapAt(cx >> 1, my >> 1, cy >= 2 * Overworld.Rows));
    }

    /// <summary>A layer 2 word as it would show at an 8x8 canvas cell — in that cell's submap
    /// colours — for a block floating over the map before it is dropped.</summary>
    public uint[]? Ow8WordPixels(int word, int col, int row)
    {
        if (Overworld is not { } ow) return null;
        int my = row % (2 * Overworld.Rows);
        return ow.TilePixels(word, Overworld.SubmapAt(col >> 1, my >> 1, row >= 2 * Overworld.Rows));
    }

    /// <summary>Layer 1 over an 8x8 canvas cell — the level tiles and paths the land is seen
    /// through on the Tiles tab, drawn but not edited there.</summary>
    public uint[]? Ow8OverlayPixels(int col, int row)
        => Overworld?.Layer1QuarterPixels(col, row % (2 * Overworld.Rows), row >= 2 * Overworld.Rows);

    /// <summary>A 16x16 canvas cell (row-major over <see cref="OwCols"/>) as it shows on the map.</summary>
    public uint[]? OwCellPixels(int cell)
        => Overworld?.CellPixels(cell % OwCols, cell / OwCols % Overworld.Rows, cell / OwCols >= Overworld.Rows);

    /// <summary>The layer 1 tile under a 16x16 canvas cell, or -1.</summary>
    public int OwLayer1At(int col, int row)
        => Overworld?.Layer1At(col, row % Overworld.Rows, row >= Overworld.Rows) ?? -1;

    /// <summary>The 8x8 tiles layer 2 can use — the two FG files at tiles 0x000-0x0FF — drawn in
    /// a palette row, for the drawer. Vanilla's land lives in FG1/FG2; FG3/FG4 hold layer 1.</summary>
    public const int OwSheetTiles = 0x100;
    public uint[]? OwSheetPixels(int tile, int palRow)
        => tile < OwSheetTiles ? Overworld?.TilePixels(tile | (palRow & 7) << 10, 0) : null;

    /// <summary>The graphics files the overworld loads, for the Graphics drawer.</summary>
    public (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] OverworldGfxBins
        => Rom is { } r ? Overworld.GfxSlots(r) : [];
}
