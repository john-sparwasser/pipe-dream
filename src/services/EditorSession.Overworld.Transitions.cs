namespace PipeDream.Services;

// EditorSession — the Transitions tab: the ways from one map to another, linked and unlinked a
// tile at a time. Two kinds, two tables, one gesture: a star or pipe warps the player across
// (Overworld.Warps), an exit tile walks him across (Overworld.ExitPaths). The tables themselves
// are Overworld.Transitions.cs; the map they sit on is EditorSession.Overworld.cs.

public sealed partial class EditorSession
{
    /// <summary>One end of a transition, at a 16x16 layer 1 cell: which table it lives in, the
    /// slot it uses, where it comes out, and the slot that comes back — -1 for a one-way trip,
    /// Lunar Magic's "N/A".</summary>
    public readonly record struct OwTransition(
        bool Exit, int X, int Y, bool SubmapMap, int Slot, int DestX, int DestY, bool DestSubmapMap, int Partner);

    /// <summary>Whether a cell can hold a transition, and which kind: a star or pipe tile warps,
    /// an exit tile walks. Null for anything else. A tile that already holds an entry keeps its
    /// kind even if it has since been painted over — an entry nothing can reach is still the
    /// user's to unlink.</summary>
    public bool? OwLinkableAt(int x, int y)
    {
        if (Overworld is not { } ow || (uint)x >= Overworld.Cols || (uint)y >= 2 * Overworld.Rows) return null;
        int tile = ow.Layer1At(x, y % Overworld.Rows, y >= Overworld.Rows);
        if (Overworld.IsWarpTile(tile)) return false;
        if (ow.KindOf(tile) == Overworld.PathKind.Exit) return true;
        return OwTransitionAt(x, y)?.Exit;
    }

    /// <summary>The transition sourced at a cell, or null when none is.</summary>
    public OwTransition? OwTransitionAt(int x, int y)
    {
        if (Overworld is not { } ow || (uint)x >= Overworld.Cols || (uint)y >= 2 * Overworld.Rows) return null;
        bool sub = y >= Overworld.Rows;
        int my = y % Overworld.Rows, submap = Overworld.SubmapAt(x, my, sub);
        foreach (var w in ow.Warps)
            if (w.Submap == submap && w.X == x && w.Y == my)
                return new OwTransition(false, x, my, sub, w.Index, w.DestX >> 4, w.DestY >> 4, w.DestSubmap != 0, w.DestIndex);
        foreach (var e in ow.ExitPaths)
            if (e.Submap == submap && e.X == x && e.Y == my)
                return new OwTransition(true, x, my, sub, e.Index, e.DestX, e.DestY, e.DestSubmap != 0, e.DestIndex);
        return null;
    }

    /// <summary>
    /// Link two tiles: each gets a way to the other, which is what a pipe pair or a pair of exit
    /// tiles is — Lunar Magic's Alt+Left on the two, without the modifier. A tile that already
    /// holds an entry has that one re-pointed; a tile that does not takes a free slot. Null when
    /// it worked, else the reason.
    ///
    /// Both tiles must be the same kind: the two tables are different shapes and the game reaches
    /// them by different means, so a pipe cannot come out of an exit tile.
    /// </summary>
    public string? LinkOwTransitions(int ax, int ay, int bx, int by)
    {
        if (Rom is not { } rom || Overworld is not { } ow) return "no overworld";
        if (ax == bx && ay == by) return "a transition needs two tiles";
        if (OwLinkableAt(ax, ay) is not { } aExit || OwLinkableAt(bx, by) is not { } bExit)
            return "a transition sits on a star, a pipe or an exit tile";
        if (aExit != bExit) return "a star or pipe links to a star or pipe, and an exit tile to an exit tile";
        return aExit ? LinkOwExits(rom, ow, ax, ay, bx, by) : LinkOwWarps(rom, ow, ax, ay, bx, by);
    }

    /// <summary>Take a tile's transition away, and the one coming back with it: a pipe with one
    /// end left is a trip the player cannot return from. Null when it worked.</summary>
    public string? UnlinkOwTransition(int x, int y)
    {
        if (Rom is not { } rom || Overworld is not { } ow) return "no overworld";
        if (OwTransitionAt(x, y) is not { } t) return "no transition on this tile";
        foreach (int slot in new[] { t.Slot, t.Partner })
        {
            if (slot < 0) continue;
            if (t.Exit) ow.ClearExitPath(slot); else ow.ClearWarp(slot);
        }
        SaveOwTransitions(rom, ow, t.Exit);
        Rebuild("transition unlinked");
        return null;
    }

    /// <summary>Two stars or pipes, pointing at each other. Both slots are claimed before either
    /// is written: half a link is worse than none, and the last free slot could otherwise go to
    /// the first direction and strand the second.</summary>
    private string? LinkOwWarps(Rom rom, Overworld ow, int ax, int ay, int bx, int by)
    {
        int slotA = OwTransitionAt(ax, ay)?.Slot ?? ow.FreeWarpSlot();
        if (slotA < 0) return "every warp slot is taken — unlink one first";
        int slotB = OwTransitionAt(bx, by)?.Slot ?? ow.FreeWarpSlot(besides: slotA);
        if (slotB < 0) return "every warp slot is taken — unlink one first";
        ow.SetWarp(slotA, Submap(ax, ay), ax, ay % Overworld.Rows, Submap(bx, by), bx, by % Overworld.Rows);
        ow.SetWarp(slotB, Submap(bx, by), bx, by % Overworld.Rows, Submap(ax, ay), ax, ay % Overworld.Rows);
        SaveOwTransitions(rom, ow, exit: false);
        Rebuild("warp linked");
        return null;
    }

    /// <summary>
    /// Two exit tiles, each walking the player onto the other. An exit entry is a tile AND the way
    /// the player is travelling through it — the "side to enter from" Lunar Magic asks for — and
    /// the map already says it: an exit tile is the end of a path, so the player leaves by the
    /// side the path does not use. A tile with no path beside it, or paths on two sides, leaves
    /// nothing to read, and this refuses rather than guessing a direction that would drop him
    /// through the wrong edge.
    /// </summary>
    private string? LinkOwExits(Rom rom, Overworld ow, int ax, int ay, int bx, int by)
    {
        int dirA = ow.TravelDirOf(ax, ay % Overworld.Rows, ay >= Overworld.Rows);
        int dirB = ow.TravelDirOf(bx, by % Overworld.Rows, by >= Overworld.Rows);
        if (dirA < 0 || dirB < 0)
            return "an exit tile needs a path on exactly one side, so the editor can tell which way the player walks through it";
        int slotA = OwTransitionAt(ax, ay)?.Slot ?? ow.FreeExitSlot();
        if (slotA < 0) return "every exit slot is taken — unlink one first";
        int slotB = OwTransitionAt(bx, by)?.Slot ?? ow.FreeExitSlot(besides: slotA);
        if (slotB < 0) return "every exit slot is taken — unlink one first";
        ow.SetExitPath(slotA, Submap(ax, ay), ax, ay % Overworld.Rows, dirA, Submap(bx, by), bx, by % Overworld.Rows);
        ow.SetExitPath(slotB, Submap(bx, by), bx, by % Overworld.Rows, dirB, Submap(ax, ay), ax, ay % Overworld.Rows);
        SaveOwTransitions(rom, ow, exit: true);
        Rebuild("exit path linked");
        return null;
    }

    /// <summary>The submap a cell of the editor's grid belongs to (0 = the main map).</summary>
    private static int Submap(int x, int y)
        => Overworld.SubmapAt(x, y % Overworld.Rows, y >= Overworld.Rows);

    /// <summary>The edited table into the ROM the editor draws from and into the project, as the
    /// level tile's tables go.</summary>
    private void SaveOwTransitions(Rom rom, Overworld ow, bool exit)
    {
        if (exit) Overworld.WriteExits(rom, ow.ExitTable);
        else Overworld.WriteWarps(rom, ow.WarpTable);
        if (Project is null) return;
        if (exit) Project.Data.Overworld.Exits = Convert.ToBase64String(ow.ExitTable);
        else Project.Data.Overworld.Warps = Convert.ToBase64String(ow.WarpTable);
        Project.MarkDirty();
    }
}
