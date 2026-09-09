namespace PipeDream.Services;

// EditorSession — the Tiles tab's canvas is wider than the two maps. Lunar Magic's Layer 2 8x8
// editor keeps two more areas beside them, editable in that mode and no other: the event pieces
// to the right of the main map, and layer 1's Map16 definitions below the submaps. This file says
// where each area sits and which words live there; the map itself is EditorSession.Overworld.cs.

public sealed partial class EditorSession
{
    /// <summary>What a canvas cell holds. <see cref="OwArea.None"/> is the empty desk between and
    /// around the areas — a cell there draws nothing and takes no paint.</summary>
    public enum OwArea { None, Map, EventPieces, Layer1Defs }

    /// <summary>The two maps: 64x64 8x8 cells each, stacked.</summary>
    public const int Ow8MapCols = 2 * OwCols, Ow8MapRows = 2 * OwRows;

    /// <summary>Where the other two areas start, as Lunar Magic places them (measured off its own
    /// status bar 2026-09-08): the event pieces immediately right of the maps, the layer 1
    /// definitions immediately below them.</summary>
    public const int OwEventCol = Ow8MapCols, OwDefsRow = Ow8MapRows;

    /// <summary>The whole canvas. The width is fixed — the event area's two columns are — while
    /// the height allows two Map16 pages of layer 1 tiles; a ROM with fewer shows fewer rows
    /// (<see cref="Ow8VisibleRows"/>), and the cells past them belong to no area.</summary>
    public static readonly int Ow8Cols = OwEventCol + Overworld.EventArea.Cols;
    public const int Ow8Rows = OwDefsRow + 2 * (0x200 / Overworld.Layer1DefsAcross);

    /// <summary>How much of the canvas this ROM actually fills: the maps, then a row of cells per
    /// row of layer 1 tiles it can name.</summary>
    public int Ow8VisibleRows
        => Overworld is { } ow
           ? Math.Max(OwDefsRow + 2 * ((ow.Map16Count + Overworld.Layer1DefsAcross - 1) / Overworld.Layer1DefsAcross),
                      Overworld.EventArea.Rows)
           : Ow8MapRows;

    /// <summary>
    /// Which area a canvas cell belongs to, and the word's index within that area's array. The
    /// three arrays are different tables in the ROM but the same thing on the canvas: an 8x8 word,
    /// tile number and its palette, flips and priority.
    /// </summary>
    public OwArea OwAreaAt(int col, int row, out int index)
    {
        index = -1;
        if (Overworld is not { } ow) return OwArea.None;
        if (OwMapCell(col, row, out int cx, out int cy, out bool sub))
        {
            index = Overworld.Layer2Index(cx, cy, sub);
            return OwArea.Map;
        }
        if (col >= OwEventCol && row < Overworld.EventArea.Rows)
        {
            index = Overworld.EventIndexOf(col - OwEventCol, row);
            return index < 0 ? OwArea.None : OwArea.EventPieces;
        }
        if (row >= OwDefsRow && col < 2 * Overworld.Layer1DefsAcross)
        {
            int tile = (row - OwDefsRow) / 2 * Overworld.Layer1DefsAcross + col / 2;
            if (tile >= ow.Map16Count) return OwArea.None;
            index = 4 * tile + (col & 1) * 2 + (row & 1);          // TL BL TR BR, as the defs run
            return OwArea.Layer1Defs;
        }
        return OwArea.None;
    }

    /// <summary>Where each area's words start in the editor's one cell array — layer 2, then the
    /// event pieces, then the definitions, then one spare slot for everything off the areas.</summary>
    private int OwAreaBase(OwArea area, Overworld ow) => area switch
    {
        OwArea.Map => 0,
        OwArea.EventPieces => ow.Layer2.Length,
        OwArea.Layer1Defs => ow.Layer2.Length + Overworld.EventCells,
        _ => OwCellCount(ow) - 1,
    };

    private static int OwCellCount(Overworld ow) => ow.Layer2.Length + Overworld.EventCells + ow.Layer1Defs.Length + 1;

    /// <summary>The slot in the editor's cell array a canvas cell edits.</summary>
    private int OwCellIndex(int col, int row)
    {
        if (Overworld is not { } ow) return 0;
        var area = OwAreaAt(col, row, out int index);
        return area == OwArea.None ? OwCellCount(ow) - 1 : OwAreaBase(area, ow) + index;
    }

    /// <summary>The label the Areas view draws on each region, with the rectangle it covers in
    /// canvas cells. Lunar Magic names none of them — it expects you to know — which is why this
    /// exists at all.</summary>
    public IEnumerable<(string Name, int X, int Y, int W, int H)> OwAreas()
    {
        yield return ("Main map", 0, 0, Ow8MapCols, Ow8MapRows / 2);
        string[] submaps = ["Yoshi's Island", "Vanilla Dome", "Forest of Illusion", "Valley of Bowser", "Special World", "Star Road"];
        int[] rowStarts = [0, Overworld.SubmapRow8Middle, Overworld.SubmapRow8Bottom, Ow8MapRows / 2];
        for (int half = 0; half < 2; half++)
            for (int band = 0; band < 3; band++)
            {
                int submap = 1 + band + 3 * half;                   // SubmapAtRow8's numbering, in its own order
                yield return (submaps[submap - 1], half * Ow8MapCols / 2, Ow8MapRows / 2 + rowStarts[band],
                              Ow8MapCols / 2, rowStarts[band + 1] - rowStarts[band]);
            }
        // The 2x2s sit under the 6x6s, so their labels stack too.
        int sixRows = Overworld.Event2Row;
        yield return ("Event pieces 6x6", OwEventCol, 0, Overworld.Event6Across * Overworld.Event6Size, sixRows);
        yield return ("Event pieces 2x2", OwEventCol, sixRows,
                      Overworld.Event2Across * Overworld.Event2Size,
                      Overworld.EventCellOf(Overworld.EventCells - 1).Y + 1 - sixRows);
        if (Overworld is { } ow)
            yield return ("Layer 1 tiles", 0, OwDefsRow, 2 * Overworld.Layer1DefsAcross,
                          2 * ((ow.Map16Count + Overworld.Layer1DefsAcross - 1) / Overworld.Layer1DefsAcross));
    }
}
