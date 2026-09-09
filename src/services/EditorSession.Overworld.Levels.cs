namespace PipeDream.Services;

// EditorSession — what a level tile on the overworld carries besides its picture: which level it
// enters, the event passing it fires, and the direction each exit opens. Lunar Magic's "Modify
// Level Tile Settings" dialog, read and written per tile. The map itself is EditorSession.Overworld.cs.

public sealed partial class EditorSession
{
    /// <summary>
    /// Everything Lunar Magic's Modify Level Tile Settings dialog holds for one level tile, with
    /// a flag per group saying whether this base can hold an edit to it — a ROM Lunar Magic has
    /// saved keeps the directions in a packed block of its own, and a base of ours numbers its
    /// levels by scan order rather than per tile, so the two are never both editable.
    /// </summary>
    public readonly record struct OwLevelTile(
        int X, int Y, bool SubmapMap, int Translevel, int Level, int BaseEvent, int[] ExitDirs,
        bool LevelEditable, bool DirsEditable);

    /// <summary>The settings of the level tile at a 16x16 layer 1 cell, or null when that cell
    /// holds no level tile.</summary>
    public OwLevelTile? OwLevelTileAt(int x, int y)
    {
        if (Overworld is not { } ow || (uint)x >= Overworld.Cols || (uint)y >= 2 * Overworld.Rows) return null;
        bool sub = y >= Overworld.Rows;
        int my = y % Overworld.Rows;
        if (ow.KindOf(ow.Layer1At(x, my, sub)) != Overworld.PathKind.Level) return null;
        int tl = ow.TranslevelAt(x, my, sub);
        return new OwLevelTile(x, my, sub, tl, Overworld.LevelOf(tl), ow.BaseEventOf(tl),
                               [.. Enumerable.Range(0, 4).Select(e => ow.ExitDirOf(tl, e))],
                               ow.HasLevelTable, ow.HasVanillaExitDirs);
    }

    /// <summary>
    /// Apply a tile's settings: the base event and the four exit directions go into the ROM's
    /// edited tables so the badges and a build follow, and into the project under their own keys.
    /// Both tables are per TRANSLEVEL, which is per level — Lunar Magic says the same in its own
    /// dialog ("You can only set one base event per level number"), so two tiles sharing a level
    /// share these. False with a report when nothing here can be written.
    ///
    /// The level number goes first, where the ROM has a table to hold it, because the other two
    /// are keyed by it: a dialog that changed both means the event and the directions belong to
    /// the level now in the box, as they do in Lunar Magic.
    /// </summary>
    public bool SetOwLevelTile(OwLevelTile before, int level, int baseEvent, int[] exitDirs)
    {
        if (Rom is not { } rom || Overworld is not { } ow) return false;
        int tl = before.Translevel;
        bool renumbered = false;
        if (before.LevelEditable && level != before.Level)
        {
            tl = Overworld.TranslevelOf(level);
            if (tl < 0) { Report("an overworld tile enters level 001-024 or 101-13B"); return false; }
            // Through the layer 1 map, not the array behind it: the map holds the number above the
            // tile and is what a later commit writes back, so a number set around it would be
            // undone by the next brush stroke. Undo comes along for free.
            var map = OwLayer1!;
            int row = before.Y + (before.SubmapMap ? Overworld.Rows : 0);
            map.Stamp(before.X, row, map.At(before.X, row) & 0xFFFF | tl << OwLevelShift);
            map.EndStroke();
            renumbered = true;
        }
        if (tl == 0) { Report("this tile has no level number yet"); return false; }
        bool changed = renumbered || ow.BaseEventOf(tl) != baseEvent;
        ow.SetBaseEvent(tl, baseEvent);
        if (before.DirsEditable)
            for (int e = 0; e < 4 && e < exitDirs.Length; e++)
            {
                changed |= ow.ExitDirOf(tl, e) != exitDirs[e];
                ow.SetExitDir(tl, e, exitDirs[e]);
            }
        if (!changed) return true;
        Overworld.WriteBaseEvents(rom, ow.BaseEventTable);
        if (before.DirsEditable) Overworld.WriteExitDirs(rom, ow.ExitDirTable);
        if (Project is not null)
        {
            Project.Data.Overworld.BaseEvents = Convert.ToBase64String(ow.BaseEventTable);
            if (before.DirsEditable) Project.Data.Overworld.ExitDirs = Convert.ToBase64String(ow.ExitDirTable);
            Project.MarkDirty();
        }
        Rebuild("level tile settings");
        return true;
    }
}
