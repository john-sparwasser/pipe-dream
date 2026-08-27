namespace PipeDream;

/// <summary>
/// Prep v10's entrance table: a main and a midway position per level, as plain 16-bit pixels
/// instead of the vanilla screen-plus-two-indices.
///
/// Eight bytes per level — main X, main Y, midway X, midway Y — and bit 15 of a Y word is the
/// only thing that says a record is in use. Clear means "this entrance is wherever vanilla's
/// tables put it", which is what every level starts as and what an untouched base stays as.
/// <see cref="RomPrep"/>'s stubs read exactly this, so the layout lives here and the stamp
/// there rather than the two sharing arithmetic by coincidence.
/// </summary>
public static class FreeEntrance
{
    private const int Active = 0x8000;

    private static int Offset(Rom rom, int level, bool midway)
        => RomPrep.Entrance2Pc + rom.HeaderOffset + (level & 0x1FF) * 8 + (midway ? 4 : 0);

    /// <summary>Whether this base can express a free position at all — v10's stubs are what
    /// read the table, so without them writing to it would change nothing in the game.</summary>
    public static bool Supported(Rom rom) => rom.HasFreeEntrancePositions;

    /// <summary>The stored position, or null when this entrance is still on vanilla's grid.</summary>
    public static (int X, int Y)? Read(Rom rom, int level, bool midway)
    {
        if (!Supported(rom)) return null;
        int at = Offset(rom, level, midway);
        if (at + 4 > rom.Data.Length) return null;
        int y = rom.Data[at + 2] | (rom.Data[at + 3] << 8);
        return (y & Active) == 0 ? null : (rom.Data[at] | (rom.Data[at + 1] << 8), y & 0x7FFF);
    }

    /// <summary>Place this entrance freely. Returns false when the base cannot express it or
    /// already says exactly that.</summary>
    public static bool Write(Rom rom, int level, bool midway, int x, int y)
    {
        if (!Supported(rom)) return false;
        if (Read(rom, level, midway) is { } had && had.X == x && had.Y == y) return false;
        int at = Offset(rom, level, midway);
        rom.Data[at] = (byte)x; rom.Data[at + 1] = (byte)(x >> 8);
        rom.Data[at + 2] = (byte)y; rom.Data[at + 3] = (byte)((y >> 8) | (Active >> 8));
        return true;
    }

    /// <summary>The level's whole 8-byte record, for the project to carry. Both entrances travel
    /// together because they share a slot and splitting them would only invite one to be saved
    /// without the other.</summary>
    public static byte[] Bytes(Rom rom, int level)
    {
        var b = new byte[8];
        if (Supported(rom)) Array.Copy(rom.Data, Offset(rom, level, midway: false), b, 0, 8);
        return b;
    }

    /// <summary>Put a captured record back, on a base that can use it. Silently does nothing on
    /// one that cannot — the vanilla entrance underneath is still valid, which is exactly what
    /// makes carrying this in the project safe.</summary>
    public static void SetBytes(Rom rom, int level, byte[] bytes)
    {
        if (Supported(rom) && bytes.Length == 8)
            Array.Copy(bytes, 0, rom.Data, Offset(rom, level, midway: false), 8);
    }

    /// <summary>Hand this entrance back to vanilla's tables.</summary>
    public static bool Clear(Rom rom, int level, bool midway)
    {
        if (!Supported(rom) || Read(rom, level, midway) is null) return false;
        int at = Offset(rom, level, midway);
        Array.Clear(rom.Data, at, 4);
        return true;
    }
}
