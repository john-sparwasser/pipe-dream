namespace PipeDream;

/// <summary>
/// Re-reads the ROM tables a project CAPTURES rather than tracks: Map16 definitions, the
/// acts-like table, and the entrance records. Those edits are written straight into the
/// session ROM, so the project stores which slots were touched and their bytes are re-read
/// from the ROM here at save time. That is what makes undo/redo free for them — the ROM is
/// the single source of truth, and nothing has to journal each change — and it also means an
/// extended-Map16 slot that relocated during page allocation still saves its current bytes.
///
/// Split out of LevelSession because none of it needs session state: given a ROM and a
/// project it is a pure refresh.
/// </summary>
internal static class ProjectCapture
{
    /// <summary>Refresh every captured slot's bytes from <paramref name="rom"/> into
    /// <paramref name="data"/>. <paramref name="tileset"/> resolves the per-tileset aliasing
    /// of extended Map16 offsets.</summary>
    internal static void Refresh(Rom rom, ProjectFile data, int tileset)
    {
        RefreshMap16(rom, data, tileset);
        RefreshEntrances(rom, data);
    }

    private static void RefreshMap16(Rom rom, ProjectFile data, int tileset)
    {
        var m = data.Map16;
        foreach (var addr in m.Slots.Keys.ToArray())
        {
            int fo = rom.FileOffset(Convert.ToInt32(addr, 16));
            m.Slots[addr] = Convert.ToHexString(rom.Data.AsSpan(fo, 8));
        }
        foreach (var t in m.Ext.Keys.ToArray())
        {
            int fo = Map16.DefFileOffset(rom, tileset, Convert.ToInt32(t, 16));
            if (fo >= 0) m.Ext[t] = Convert.ToHexString(rom.Data.AsSpan(fo, 8));
        }
        if (rom.LmActsAsBase > 0)
            foreach (var t in m.ActsAs.Keys.ToArray())
            {
                int fo = rom.FileOffset(rom.LmActsAsBase + Convert.ToInt32(t, 16) * 2);
                m.ActsAs[t] = rom.Data[fo] | (rom.Data[fo + 1] << 8);
            }
    }

    private static void RefreshEntrances(Rom rom, ProjectFile data)
    {
        foreach (var idx in data.Entrances.Keys.ToArray())
            data.Entrances[idx] =
                Convert.ToHexString(rom.ReadSecondaryEntrance(Convert.ToInt32(idx, 16)).ToBytes());
        foreach (var (levelHex, state) in data.Levels)
            if (state.MainEntrance is not null)
                state.MainEntrance =
                    Convert.ToHexString(rom.ReadMainEntrance(Convert.ToInt32(levelHex, 16)).ToBytes());
    }
}
