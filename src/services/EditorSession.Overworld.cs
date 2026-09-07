namespace PipeDream.Services;

// EditorSession — the overworld: the map as the ROM holds it, and the layer 2 editor over it,
// for the Overworld canvas. Read from the open ROM once and kept until the ROM changes. The
// rest of the session's state is in EditorSession.cs.
public sealed partial class EditorSession
{
    private Overworld? overworld;
    private TilemapEdit? owMap, owLayer1;

    /// <summary>The open ROM's overworld, or null without a ROM.</summary>
    public Overworld? Overworld
    {
        get
        {
            if (Rom is not { } r) return null;
            if (overworld?.Rom != r) { overworld = new Overworld(r); owMap = null; owLayer1 = null; }
            return overworld;
        }
    }

    /// <summary>Each map in 16x16 cells; the canvas stacks the two, so it is twice as tall.</summary>
    public const int OwCols = Overworld.Cols, OwRows = 2 * Overworld.Rows;
    /// <summary>
    /// The canvas in the 8x8 cells layer 2 is made of, laid out as Lunar Magic lays it out: the
    /// main map over the submap map, each 64x64, and the lower map ROTATED two cells right and
    /// one down — its tilemap's last two columns and last row wrap round to its left edge and top,
    /// as a hardware scroll of 16px, 8px would show them. Measured against LM's window and against
    /// the game in Mesen on 2026-09-06: the game draws the plain grid, so this is LM's canvas,
    /// copied because the two editors must agree on what is where. Layer 1 rides the same shift,
    /// so the wrapped strips carry land only.
    /// </summary>
    public const int OwSubDx = 2, OwSubDy = 1;
    public const int Ow8Cols = 2 * OwCols, Ow8Rows = 2 * OwRows;

    /// <summary>The map cell under a canvas cell: which map, and its 8x8 column and row there,
    /// the submap map's rotation undone. False outside the canvas.</summary>
    public static bool OwMapCell(int col, int row, out int cx, out int cy, out bool submapMap)
    {
        const int mapRows = 2 * Overworld.Rows, mask = 2 * Overworld.Cols - 1;
        submapMap = row >= mapRows;
        cx = submapMap ? (col - OwSubDx) & mask : col;
        cy = submapMap ? (row - mapRows - OwSubDy) & mask : row;
        return (uint)col < 2 * Overworld.Cols && (uint)row < 2 * mapRows;
    }

    /// <summary>Whether a canvas cell has layer 1 over it: the wrapped strips at the lower map's
    /// left and top are land that scrolled round, with no level tiles or paths of their own.</summary>
    public static bool OwHasLayer1(int col, int row)
        => row < 2 * Overworld.Rows || (col >= OwSubDx && row - 2 * Overworld.Rows >= OwSubDy);

    /// <summary>
    /// Layer 2 as an editable tilemap over the canvas. A commit lands the words in the ROM's
    /// edited copy (the same array the overworld draws from) and in the project, so the canvas,
    /// the saved file and the build agree. A cell outside the canvas maps to one spare slot past
    /// the map's words that a commit never copies.
    /// </summary>
    public TilemapEdit? OwMap
    {
        get
        {
            if (Overworld is not { } ow) return null;
            if (owMap is not null) return owMap;
            var cells = new int[ow.Layer2.Length + 1];
            for (int i = 0; i < ow.Layer2.Length; i++) cells[i] = ow.Layer2[i];
            var map = new TilemapEdit(cells, Ow8Cols, Ow8Rows, 8,
                                      (c, r) => OwMapCell(c, r, out int cx, out int cy, out bool sub) ? Overworld.Layer2Index(cx, cy, sub) : ow.Layer2.Length);
            map.Committed += () =>
            {
                for (int i = 0; i < ow.Layer2.Length; i++) ow.Layer2[i] = (ushort)cells[i];
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
    public bool OwLayer1Edited => Project?.Data.Overworld.Layer1 is not null;

    /// <summary>
    /// Layer 1 as an editable tilemap of 16x16 cells: the main map's 32 rows over the submap
    /// map's, each cell the Map16 tile there — what the Paths &amp; Levels tab places and moves.
    /// A commit lands in the ROM's edited copy (the array the overworld draws from), recounts
    /// vanilla's level numbers, and goes into the project for the build (Overworld.WriteLayer1).
    /// </summary>
    public const int OwL1Cols = Overworld.Cols, OwL1Rows = 2 * Overworld.Rows;
    public TilemapEdit? OwLayer1
    {
        get
        {
            if (Overworld is not { } ow) return null;
            if (owLayer1 is not null) return owLayer1;
            var cells = new int[ow.Layer1.Length];
            for (int i = 0; i < cells.Length; i++) cells[i] = ow.Layer1[i];
            var map = new TilemapEdit(cells, OwL1Cols, OwL1Rows, 16,
                                      (x, y) => Overworld.Layer1Index(x, y % Overworld.Rows, y >= Overworld.Rows));
            map.Committed += () =>
            {
                for (int i = 0; i < cells.Length; i++) ow.Layer1[i] = (ushort)cells[i];
                ow.ReadTranslevels();
                if (Project is not null)
                {
                    Project.Data.Overworld.Layer1 = Convert.ToBase64String(ProjectSession.BytesOf(ow.Layer1));
                    Project.MarkDirty();
                }
            };
            return owLayer1 = map;
        }
    }

    /// <summary>The 16x16 layer 1 cell under a canvas cell, in <see cref="OwLayer1"/>'s grid
    /// (rows 32+ are the submap map); false over the wrapped strips, which carry no layer 1.</summary>
    public static bool OwLayer1Cell(int col, int row, out int x, out int y)
    {
        x = y = 0;
        if (!OwHasLayer1(col, row) || !OwMapCell(col, row, out int cx, out int cy, out bool sub)) return false;
        x = cx >> 1; y = (cy >> 1) + (sub ? Overworld.Rows : 0);
        return true;
    }

    /// <summary>The canvas cell at a layer 1 cell's top-left corner: the lower map's grid sits a
    /// cell right and a cell down of the canvas's, as Lunar Magic draws it.</summary>
    public static (int Col, int Row) OwLayer1Origin(int x, int y)
        => y < Overworld.Rows ? (2 * x, 2 * y) : (2 * x + OwSubDx, 2 * y + OwSubDy);

    /// <summary>The canvas cells a layer 1 tile covers — the block a gesture on the Paths &amp;
    /// Levels tab snaps to. Over the wrapped strips a cell is its own block, and nothing there
    /// takes a tile.</summary>
    public static (int X, int Y, int W, int H) OwLayer1Block(int col, int row)
    {
        if (!OwLayer1Cell(col, row, out int x, out int y)) return (col, row, 1, 1);
        var (c, r) = OwLayer1Origin(x, y);
        return (c, r, 2, Math.Min(2, Ow8Rows - r));            // the lower map's last row is half off the canvas
    }

    /// <summary>A layer 1 tile as it would show over a canvas cell — its quarter in that cell's
    /// colours, the path picture's quarter over it while paths show — for a block dragged over
    /// the map before it lands. Null for no tile.</summary>
    public uint[]? Ow8TileOverlay(int tile, int col, int row, bool paths)
    {
        if (tile < 0 || Overworld is not { } ow || !OwMapCell(col, row, out int cx, out int cy, out _)) return null;
        var img = Quarter(ow.Map16Pixels(tile, OwSubmapShown(col, row)), cx, cy);
        if (paths && Overworld.PathGlyph(tile) is { } g) Over(img, Quarter(g, cx, cy));
        return img;
    }

    /// <summary>The 8x8 quarter of a 16x16 picture that an 8x8 map cell shows.</summary>
    private static uint[] Quarter(uint[] tile16, int cx, int cy)
    {
        var img = new uint[64];
        int ox = (cx & 1) * 8, oy = (cy & 1) * 8;
        for (int y = 0; y < 8; y++) Array.Copy(tile16, (oy + y) * 16 + ox, img, y * 8, 8);
        return img;
    }

    private static void Over(uint[] under, uint[] top)
    {
        for (int i = 0; i < under.Length; i++) if (top[i] != 0) under[i] = top[i];
    }

    /// <summary>An 8x8 canvas cell (row-major over <see cref="Ow8Cols"/>): the layer 2 word the
    /// editor holds there, and nothing else — what the Tiles tab paints and moves.</summary>
    public uint[]? Ow8CellPixels(int cell)
    {
        int col = cell % Ow8Cols, row = cell / Ow8Cols;
        if (Overworld is not { } ow || OwMap is not { } map || !OwMapCell(col, row, out _, out _, out _)) return null;
        return ow.TilePixels(map.At(col, row), OwSubmapShown(col, row));
    }

    /// <summary>The submap whose colours a canvas cell wears: by where it is DRAWN, so the wrapped
    /// strips at the lower map's left and top take the palette of the submap they sit beside,
    /// as in Lunar Magic, not that of the far side their words came from.</summary>
    private static int OwSubmapShown(int col, int row)
        => row < 2 * Overworld.Rows ? 0 : Overworld.SubmapAtRow8(col, row - 2 * Overworld.Rows);

    /// <summary>A layer 2 word as it would show at an 8x8 canvas cell — in that cell's submap
    /// colours — for a block floating over the map before it is dropped.</summary>
    public uint[]? Ow8WordPixels(int word, int col, int row)
    {
        if (Overworld is not { } ow || !OwMapCell(col, row, out _, out _, out _)) return null;
        return ow.TilePixels(word, OwSubmapShown(col, row));
    }

    /// <summary>Layer 1 over an 8x8 canvas cell — the level tiles and paths the land is seen
    /// through, drawn but never carried by a lasso.</summary>
    public uint[]? Ow8OverlayPixels(int col, int row)
        => Overworld is { } ow && OwHasLayer1(col, row) && OwMapCell(col, row, out int cx, out int cy, out bool sub) ? ow.Layer1QuarterPixels(cx, cy, sub) : null;

    /// <summary>The quarter of Lunar Magic's picture for the path tile over an 8x8 canvas cell, or null.</summary>
    public uint[]? Ow8GlyphPixels(int col, int row)
        => Overworld is { } ow && OwHasLayer1(col, row) && OwMapCell(col, row, out int cx, out int cy, out bool sub)
           && Overworld.PathGlyph(ow.Layer1At(cx >> 1, cy >> 1, sub)) is { } g ? Quarter(g, cx, cy) : null;

    /// <summary>The overlay over an 8x8 canvas cell: layer 1's quarter, the path picture's quarter
    /// over it, whichever the view has on.</summary>
    public uint[]? Ow8Overlay(int col, int row, bool layer1, bool paths)
    {
        var under = layer1 ? Ow8OverlayPixels(col, row) : null;
        if (!paths || Ow8GlyphPixels(col, row) is not { } g) return under;
        if (under is null) return g;
        var img = (uint[])under.Clone();
        Over(img, g);
        return img;
    }

    /// <summary>The layer 1 tile under an 8x8 canvas cell, or -1 where there is none.</summary>
    public int OwLayer1At(int col, int row)
        => Overworld is { } ow && OwHasLayer1(col, row) && OwMapCell(col, row, out int cx, out int cy, out bool sub) ? ow.Layer1At(cx >> 1, cy >> 1, sub) : -1;

    /// <summary>The 8x8 tiles layer 2 can use — the two FG files at tiles 0x000-0x0FF — drawn in
    /// a palette row, for the drawer. Vanilla's land lives in FG1/FG2; FG3/FG4 hold layer 1.</summary>
    public const int OwSheetTiles = 0x100;
    public uint[]? OwSheetPixels(int tile, int palRow)
        => tile < OwSheetTiles ? Overworld?.TilePixels(tile | (palRow & 7) << 10, 0) : null;

    /// <summary>The graphics files the overworld loads, for the Graphics drawer.</summary>
    public (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] OverworldGfxBins
        => Rom is { } r ? Overworld.GfxSlots(r) : [];
}
