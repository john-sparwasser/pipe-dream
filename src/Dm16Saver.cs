namespace PipeDream;

/// <summary>
/// Direct Map16 brush decomposition: turn a tile brush into DM16 objects (LM parity —
/// placed tiles ARE objects in the stream, resizable like any other). Pure logic, no UI.
/// (ROM persistence moved to RomBuilder — the project's Build pipeline.)
/// </summary>
public static class Dm16Saver
{
    /// <summary>
    /// Decompose a brush stamped at cell (cx,cy) into DM16 objects: maximal single-tile
    /// rectangles when the brush is uniform, else per-row runs of equal tiles, capped at
    /// the extended Form B limits (128 wide x 256 tall). Empty/marker cells are skipped
    /// (stamping never erases — same as LM). Vertical levels: screen = 16-row band,
    /// b1 bit4 = right half.
    /// </summary>
    public static List<LevelObject> FromBrush(ushort[] tiles, int w, int h, int cx, int cy, bool vert)
    {
        var outl = new List<LevelObject>();
        // FG lookup range only — Empty/marker cells and BG-space tiles (0x4000+) skipped.
        bool Ok(ushort t) => t != Map16Grid.Empty && (t & ObjectEngine.Marker) == 0 && t < 0x1000;

        // Open rectangles from the previous row, keyed by (x0, len, tile), grown downward.
        var open = new List<(int x0, int len, ushort tile, int y0, int hh)>();
        for (int j = 0; j <= h; j++)                     // extra pass flushes the last row
        {
            var runs = new List<(int x0, int len, ushort tile)>();
            for (int i = 0; i < w && j < h; )
            {
                ushort t = tiles[j * w + i];
                if (!Ok(t)) { i++; continue; }
                int x0 = i;
                while (i < w && tiles[j * w + i] == t && i - x0 < 128 &&
                       !(vert && cx + i == 16 && i > x0))   // vertical: runs can't cross the half seam
                    i++;
                runs.Add((x0, i - x0, t));
            }
            var next = new List<(int, int, ushort, int, int)>();
            foreach (var o in open)
            {
                int k = runs.FindIndex(r => r.x0 == o.x0 && r.len == o.len && r.tile == o.tile);
                if (k >= 0 && o.hh < 256) { next.Add((o.x0, o.len, o.tile, o.y0, o.hh + 1)); runs.RemoveAt(k); }
                else outl.Add(Dm16At(o.tile, cx + o.x0, cy + o.y0, o.len, o.hh, vert));
            }
            foreach (var r in runs) next.Add((r.x0, r.len, r.tile, j, 1));
            open = next;
        }
        return outl;
    }

    private static LevelObject Dm16At(int tile, int cx, int cy, int w, int h, bool vert)
    {
        int screen = vert ? cy >> 4 : (cx >> 4) & 0x1F;
        int y = vert ? (cy & 15) | (cx >= 16 ? 0x10 : 0) : Math.Clamp(cy, 0, 0x1F);
        return LevelObject.MakeDm16(tile, screen, cx & 15, y, w, h);
    }

}
