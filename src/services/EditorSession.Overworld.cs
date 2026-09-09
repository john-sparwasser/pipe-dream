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
    /// The canvas is wider and taller than the two maps on the Tiles tab, where Lunar Magic keeps
    /// two more areas beside them: EditorSession.Overworld.Areas.cs has the whole shape.
    public const int OwSubDx = 2, OwSubDy = 1;

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
    /// The Tiles tab's canvas as one editable tilemap: layer 2's two maps, and beside them the two
    /// areas Lunar Magic keeps in this mode alone — the event pieces and layer 1's Map16
    /// definitions (EditorSession.Overworld.Areas.cs). All three are 8x8 words, so one brush, one
    /// lasso and one undo stack serve them; the cells array holds them end to end and a commit
    /// takes each part back to its own table, the ROM's edited copies and the project. A cell on
    /// no area maps to one spare slot at the end that a commit never copies.
    /// </summary>
    public TilemapEdit? OwMap
    {
        get
        {
            if (Overworld is not { } ow) return null;
            if (owMap is not null) return owMap;
            var (events, defs) = (ow.EventPieces, ow.Layer1Defs);
            var cells = new int[OwCellCount(ow)];
            int at = 0;
            foreach (var part in new[] { ow.Layer2, events, defs })
                foreach (ushort w in part) cells[at++] = w;
            var map = new TilemapEdit(cells, Ow8Cols, Ow8Rows, 8, OwCellIndex);
            map.Committed += () =>
            {
                bool land = Copy(cells, 0, ow.Layer2), pieces = Copy(cells, ow.Layer2.Length, events);
                bool tiles = Copy(cells, ow.Layer2.Length + events.Length, defs);
                if (Project is null || !(land || pieces || tiles)) return;
                if (land) Project.Data.Overworld.Layer2 = Convert.ToBase64String(ProjectSession.BytesOf(ow.Layer2));
                if (pieces) Project.Data.Overworld.EventPieces = Convert.ToBase64String(ProjectSession.BytesOf(events));
                if (tiles) Project.Data.Overworld.Layer1Defs = Convert.ToBase64String(ProjectSession.BytesOf(defs));
                Project.MarkDirty();
            };
            return owMap = map;
        }
    }

    /// <summary>Take one part of the cell array back to its own words, and say whether any of them
    /// moved — only an area that changed is worth writing into the project.</summary>
    private static bool Copy(int[] cells, int from, ushort[] to)
    {
        bool changed = false;
        for (int i = 0; i < to.Length; i++)
        {
            if (to[i] == (ushort)cells[from + i]) continue;
            to[i] = (ushort)cells[from + i];
            changed = true;
        }
        return changed;
    }

    /// <summary>Whether the overworld's layer 2 differs from the base ROM's.</summary>
    public bool OwEdited => Project?.Data.Overworld.Layer2 is not null;
    public bool OwLayer1Edited => Project?.Data.Overworld.Layer1 is not null;

    /// <summary>
    /// Layer 1 as an editable tilemap of 16x16 cells: the main map's 32 rows over the submap
    /// map's, each cell the Map16 tile there — what the Paths &amp; Levels tab places and moves.
    /// On a ROM with Lunar Magic's per-tile level table, a cell's value also carries the level
    /// the tile enters (<see cref="OwLevelShift"/>), so every gesture — move, copy, delete, undo —
    /// carries the number with the tile for free; a tile placed from the drawer enters level 0
    /// until the Levels editor says otherwise. A commit lands in the ROM's edited copies (the
    /// arrays the overworld draws from), renumbers vanilla's levels, and goes into the project
    /// for the build (Overworld.WriteLayer1, WriteLevelTable).
    /// </summary>
    public const int OwL1Cols = Overworld.Cols, OwL1Rows = 2 * Overworld.Rows;
    /// <summary>Where a layer 1 cell's value keeps its translevel, above the 16-bit tile.</summary>
    public const int OwLevelShift = 16;
    public TilemapEdit? OwLayer1
    {
        get
        {
            if (Overworld is not { } ow) return null;
            if (owLayer1 is not null) return owLayer1;
            var cells = new int[ow.Layer1.Length];
            for (int i = 0; i < cells.Length; i++) cells[i] = ow.Layer1[i] | (ow.HasLevelTable ? ow.Translevels[i] << OwLevelShift : 0);
            var map = new TilemapEdit(cells, OwL1Cols, OwL1Rows, 16,
                                      (x, y) => Overworld.Layer1Index(x, y % Overworld.Rows, y >= Overworld.Rows));
            map.Committed += () =>
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    ow.Layer1[i] = (ushort)cells[i];
                    if (ow.HasLevelTable) ow.Translevels[i] = (byte)(cells[i] >> OwLevelShift);
                }
                ow.Renumber();
                if (Project is not null)
                {
                    Project.Data.Overworld.Layer1 = Convert.ToBase64String(ProjectSession.BytesOf(ow.Layer1));
                    if (ow.HasLevelTable) Project.Data.Overworld.Translevels = Convert.ToBase64String(ow.Translevels);
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
        var img = Quarter(ow.Layer1Art(tile, OwSubmapShown(col, row)), cx, cy);
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
        if (Overworld is not { } ow || OwMap is not { } map) return null;
        // The event pieces and the layer 1 definitions are drawn in the main map's colours, as
        // Lunar Magic draws them until you point its selector at another submap.
        return OwAreaAt(col, row, out _) switch
        {
            OwArea.Map => ow.TilePixels(map.At(col, row), OwSubmapShown(col, row)),
            // An event cell no piece draws with holds vanilla's meaningless grey; the X says
            // "empty" as Lunar Magic's does (Overworld.BlankEventWord).
            OwArea.EventPieces when map.At(col, row) == Overworld.BlankEventWord => ow.FillerPixels(),
            OwArea.EventPieces or OwArea.Layer1Defs => ow.TilePixels(map.At(col, row), 0),
            // No table reaches here, so there is nothing to edit and nothing to draw: the desk
            // shows through, which is what the cell is.
            _ => null,
        };
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
        if (Overworld is not { } ow || OwAreaAt(col, row, out _) is var area && area is OwArea.None) return null;
        // A blank event cell in flight draws as the X it draws at rest, so a dragged block does
        // not turn grey on the way and back again on landing.
        if (area is OwArea.EventPieces && word == Overworld.BlankEventWord) return ow.FillerPixels();
        return ow.TilePixels(word, OwMapCell(col, row, out _, out _, out _) ? OwSubmapShown(col, row) : 0);
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

    /// <summary>The 8x8 tiles the overworld can use, drawn in a palette row, for the drawer. All
    /// four FG files: vanilla's land is in FG1/FG2 (0x000-0x0FF) and layer 1's tiles in FG3/FG4
    /// (0x100-0x1FF), and the Tiles tab now edits both — the land and, beside it, the Map16
    /// definitions layer 1 is made of.</summary>
    public const int OwSheetTiles = 0x200;
    public uint[]? OwSheetPixels(int tile, int palRow)
        => tile < OwSheetTiles ? Overworld?.TilePixels(tile | (palRow & 7) << 10, 0) : null;

    /// <summary>Which submap the Graphics drawer's Overworld group is showing — 0 the main map,
    /// 1-6 the six others, Lunar Magic's Submap GFX order.</summary>
    public int OwGfxSubmap
    {
        get => owGfxSubmap;
        set => owGfxSubmap = (uint)value < Overworld.Submaps ? value : 0;
    }
    private int owGfxSubmap;

    /// <summary>The graphics files <see cref="OwGfxSubmap"/> loads, for the Graphics drawer.</summary>
    public (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] OverworldGfxBins
        => Rom is { } r ? Overworld.GfxSlots(r, OwGfxSubmap) : [];

    /// <summary>The names the submap picker offers, in the same order as the record entries.</summary>
    public static readonly string[] OwSubmapNames =
        ["Overworld Map", "Yoshi's Island", "Vanilla Dome", "Forest of Illusion",
         "Bowser's Valley", "Special World", "Star Road"];

    /// <summary>
    /// Point one of <see cref="OwGfxSubmap"/>'s graphics slots at a different file — Lunar
    /// Magic's Overworld ▸ Submap GFX, recorded in the project as a per-submap record. Only that
    /// submap changes; the others keep the vanilla list, which is the whole point of the hack.
    /// </summary>
    public string SetOwGfxSlot(int word, int file)
    {
        if (Rom is null) return "no ROM open";
        if (file is < 0 or > 0xFFF) return "GFX ids run 000-FFF";
        if (word is < 0 or > 11) return "not a graphics slot";
        int submap = OwGfxSubmap;
        Rom.GfxSlotOverrides[(RomPrep.OwGfxRecordIndex + submap, word)] = file;
        if (Project is { } p)
        {
            var slots = p.Data.Overworld.GfxSlots.TryGetValue(submap.ToString(), out var s) ? s : (p.Data.Overworld.GfxSlots[submap.ToString()] = []);
            slots[word] = file;
            p.MarkDirty();
        }
        Overworld?.InvalidateGfx(submap);
        return $"{OwSubmapNames[submap]} slot ← GFX{file:X3}" + (GfxName(file) is { } n ? $" \"{n}\"" : "");
    }
}
