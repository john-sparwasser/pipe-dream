namespace PipeDream;

/// <summary>
/// Persists the tile-paint overlay (grid cells that differ from the object-render baseline)
/// to a ROM copy as Direct Map16 objects, merged into the level's object stream and written
/// through RATS/repoint. Pure orchestration over Rom/Level — no UI. Returns a status line
/// and whether the edit committed (so the caller can reset its baseline).
/// </summary>
public static class Dm16Saver
{
    public static (string Status, bool Committed) Save(
        Rom rom, Level level, int levelNum, Map16Grid grid, Map16Grid baseGrid, string? romPath)
    {
        if (!rom.HasDm16Hijack)
            return ("ROM lacks LM Direct Map16 ASM — open a ROM saved by LM.", false);

        // Collect changed, non-empty cells as DM16 objects grouped by screen.
        var byScreen = new Dictionary<int, List<LevelObject>>();
        int edits = 0;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int t = grid.Get(x, y);
                if (t == baseGrid.Get(x, y)) continue;
                if ((t & ObjectEngine.Marker) != 0) continue;             // marker: skip
                int place = t == Map16Grid.Empty ? 0x025 : t;             // erase = blank sky tile
                int screen = x / 16;
                if (!byScreen.TryGetValue(screen, out var lst)) byScreen[screen] = lst = new();
                lst.Add(LevelObject.MakeDm16(place, screen, x % 16, y));
                edits++;
            }
        if (edits == 0) return ("no edits to save", false);

        // Merge into the original object list: insert each screen's DM16 objects right after
        // that screen's last original object (keeps the original new-screen flags valid).
        var merged = new List<LevelObject>();
        var placed = new HashSet<int>();
        var objs = level.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            merged.Add(objs[i]);
            int next = i + 1 < objs.Count ? objs[i + 1].Screen : -1;
            if (objs[i].Screen != next && !placed.Contains(objs[i].Screen) &&
                byScreen.TryGetValue(objs[i].Screen, out var lst))
            { merged.AddRange(lst); placed.Add(objs[i].Screen); }
        }
        int skipped = byScreen.Where(kv => !placed.Contains(kv.Key)).Sum(kv => kv.Value.Count);

        try
        {
            byte[] data = level.Encode(rom, merged);
            int addr;
            try { addr = rom.AllocateRats(data); }
            catch { rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, rom.ActualRomSize * 2))); addr = rom.AllocateRats(data); }
            rom.SetLayer1Pointer(levelNum, addr);
            string outp = System.IO.Path.ChangeExtension(romPath, ".edited.smc");
            rom.SaveAs(outp);
            return ($"saved {edits} edits -> {System.IO.Path.GetFileName(outp)}" +
                    (skipped > 0 ? $"  ({skipped} on empty screens skipped)" : ""), true);
        }
        catch (Exception e) { return ("save failed: " + e.Message, false); }
    }
}
