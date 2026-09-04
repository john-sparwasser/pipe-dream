namespace PipeDream;

/// <summary>What a tile's acts-as number does to the player, as a shape.</summary>
public enum HitKind
{
    /// <summary>Nothing to stand on or bump: page-0 behaviours (air, coins, vines, water).</summary>
    None,
    /// <summary>Solid on all four sides.</summary>
    Solid,
    /// <summary>A ledge: solid from above only, passed through from the sides and below.</summary>
    Ledge,
    /// <summary>A slope: solid below a per-column surface, see <see cref="Hitbox.Surface"/>.</summary>
    Slope,
    /// <summary>The tile over a steep slope, whose shape is the slope's continuation — known
    /// only with the tile below it in hand (<see cref="Hitboxes.Above"/>).</summary>
    SlopeTop,
}

/// <summary>
/// One tile's hitbox. <paramref name="Surface"/> is per pixel column, 16 entries: the row the
/// solid part starts at, 0 (whole column) to 15, or 16 for an empty column. Null for the kinds
/// whose shape is the whole cell (or none of it).
/// </summary>
public readonly record struct Hitbox(HitKind Kind, bool Hurts, byte[]? Surface = null)
{
    public static readonly Hitbox Nothing = new(HitKind.None, false);
}

/// <summary>
/// SMW's collision classes, read the way bank 00 reads them (reference/smw-disasm, CODE_00EC24
/// onward). An acts-as number below 0x100 is the non-solid class. From 0x100 the LOW BYTE picks
/// the behaviour: under 0x11 a ledge, 0x11-0x6D a block (hurting for a few, some only in
/// certain tilesets — CODE_00F127), 0x6E-0xD7 a slope, 0xD8 and up the tile above a steep one.
///
/// Slopes are data, not code: a per-tile type table ($00E55E, or $00E5C8 for tilesets 0 and 7)
/// and a 16-column surface table per type ($00E632), both read from THIS ROM so a hack that
/// redraws a slope is described as it plays. A surface byte is the row the ground starts at;
/// negative means the ground is above the tile (the column is full and the tile above carries
/// the surface), 0x10 means below it (the column is empty).
/// </summary>
public static class Hitboxes
{
    private const int TypeTable = 0x00E55E, TypeTableAlt = 0x00E5C8, SurfaceTable = 0x00E632;

    public static Hitbox Of(Rom rom, int actsAs, int tileset)
    {
        if (actsAs is < 0x100 or > 0x1FF) return Hitbox.Nothing;
        int lo = actsAs & 0xFF;
        if (lo < 0x11) return new(HitKind.Ledge, false);
        if (lo < 0x6E) return new(HitKind.Solid, Hurts(lo, tileset));
        if (lo >= 0xD8) return new(HitKind.SlopeTop, false);

        var rows = Rows(rom, lo, tileset);
        var surface = new byte[16];
        for (int x = 0; x < 16; x++)
            surface[x] = (byte)(rows[x] < 0 ? 0 : Math.Min(16, (int)rows[x]));
        return new(HitKind.Slope, false, surface);
    }

    /// <summary>The tile above a slope, given what is below it: each column the slope's surface
    /// left above its own tile continues here, the rest is air. Nothing when the tile below is
    /// not a slope — a stray 0x1D8+ tile has no shape of its own.</summary>
    public static Hitbox Above(Rom rom, int belowActsAs, int tileset)
    {
        if (belowActsAs is < 0x16E or > 0x1D7) return Hitbox.Nothing;
        var rows = Rows(rom, belowActsAs & 0xFF, tileset);
        var surface = new byte[16];
        for (int x = 0; x < 16; x++) surface[x] = (byte)(rows[x] < 0 ? rows[x] + 16 : 16);
        return new(HitKind.Slope, false, surface);
    }

    private static sbyte[] Rows(Rom rom, int lo, int tileset)
    {
        int table = tileset is 0 or 7 ? TypeTableAlt : TypeTable;
        int type = rom.ReadByte(table + lo - 0x6E);
        var rows = new sbyte[16];
        for (int x = 0; x < 16; x++) rows[x] = (sbyte)rom.ReadByte(SurfaceTable + type * 16 + x);
        return rows;
    }

    /// <summary>CODE_00F127: the muncher always; spikes 0x59-0x5B in the castle and ghost-house
    /// tilesets; 0x5C and the 0x66-0x69 run in the castle tileset only.</summary>
    private static bool Hurts(int lo, int tileset) => lo switch
    {
        0x2F => true,
        >= 0x59 and <= 0x5B => tileset is 1 or 5 or 0xD,
        0x5C or (>= 0x66 and <= 0x69) => tileset == 1,
        _ => false,
    };
}
