namespace PipeDream;

// Overworld — the level each layer 1 cell enters: vanilla's scan order or Lunar Magic's per-tile
// table, the level and base event a translevel maps to, and the table's write-back.

public sealed partial class Overworld
{
    /// <summary>The translevel (0-0x5F) each layer 1 cell enters, by index: Lunar Magic's
    /// per-tile table when the ROM has one (the ROM's edited copy, so a move carries the number
    /// and the build writes it), else what the vanilla scan at $04D7F2 computes — every
    /// 0x56-0x80 tile numbered 1, 2, 3… in index order.</summary>
    public byte[] Translevels { get; }

    /// <summary>Whether the level a tile enters is the tile's own (LM's table) rather than its
    /// place in the scan — and so travels with the tile when it moves.</summary>
    public bool HasLevelTable => At.LevelTableBlob != 0;

    public int TranslevelAt(int x, int y, bool submapMap) => Translevels[Layer1Index(x, y, submapMap)];

    /// <summary>The level a translevel enters ($05D8A2): 1-0x24 as they are, 0x25-0x5F as 0x101-0x13B.</summary>
    public static int LevelOf(int translevel) => translevel == 0 ? 0 : translevel < 0x25 ? translevel : (translevel - 0x24) | 0x100;

    /// <summary>The event a translevel's normal exit fires ($05D608, read at $05D9CC; a secret
    /// exit adds its number), $FF = none. 0x80 bytes; the ROM's edited copy, so the dialog, the
    /// badges and the build all read one array.</summary>
    public const int BaseEvents = 0x05D608, BaseEventCount = 0x80;
    public byte[] BaseEventTable { get; }

    public int BaseEventOf(int translevel) => BaseEventTable[translevel & 0x7F] is var e && e != 0xFF ? e : -1;

    /// <summary>
    /// Which direction passing a level opens, per translevel ($04D678, copied to $7ED800 by the
    /// scan at $04D841): two bits per exit, 7-6 the normal one and 5-4 / 3-2 / 1-0 secret exits
    /// 1-3, each 0 up, 1 down, 2 left, 3 right. 0x60 bytes — one per translevel the game can
    /// hold. The ROM's edited copy, as <see cref="BaseEventTable"/> is.
    ///
    /// Lunar Magic's own overworld save moves this out: its rewrite of the scan at $04D7F2 jumps
    /// over the copy loop entirely and fills the RAM from a packed block of its own. So the table
    /// is live exactly while <see cref="HasLevelTable"/> is false — on a base of ours, not on a
    /// ROM LM has saved (measured 2026-09-08: an LM save of a v17 base left $04D678 untouched
    /// while the dialog's direction changed).
    /// </summary>
    public const int ExitDirs = 0x04D678, ExitDirCount = 0x60;
    public byte[] ExitDirTable { get; }

    /// <summary>Whether the exit-direction table this ROM reads is the vanilla one we can write.</summary>
    public bool HasVanillaExitDirs => !HasLevelTable;

    /// <summary>Exit 0 is the normal one, 1-3 the secret exits: the direction each opens.</summary>
    public int ExitDirOf(int translevel, int exit)
        => (uint)translevel < ExitDirCount && (uint)exit < 4 ? (ExitDirTable[translevel] >> (6 - exit * 2)) & 3 : 0;

    public void SetExitDir(int translevel, int exit, int dir)
    {
        if ((uint)translevel >= ExitDirCount || (uint)exit >= 4) return;
        int shift = 6 - exit * 2;
        ExitDirTable[translevel] = (byte)(ExitDirTable[translevel] & ~(3 << shift) | (dir & 3) << shift);
    }

    public void SetBaseEvent(int translevel, int e) => BaseEventTable[translevel & 0x7F] = (byte)(e < 0 ? 0xFF : e);

    /// <summary>The four directions an exit can open, in the order the two-bit field numbers them
    /// — and the order Lunar Magic's dropdowns list them.</summary>
    public static readonly string[] DirectionNames = ["Up", "Down", "Left", "Right"];

    /// <summary>Write the two per-translevel tables back where the game reads them. Both are
    /// fixed-size and in place, so neither can fail to fit.</summary>
    public static void WriteExitDirs(Rom rom, byte[] table)
        => table.AsSpan(0, ExitDirCount).CopyTo(rom.Data.AsSpan(rom.FileOffset(ExitDirs)));

    public static void WriteBaseEvents(Rom rom, byte[] table)
        => table.AsSpan(0, BaseEventCount).CopyTo(rom.Data.AsSpan(rom.FileOffset(BaseEvents)));

    /// <summary>Lunar Magic's per-tile level table, out of its LZ2 blob.</summary>
    private static byte[] ReadLevelTable(Rom rom, Tables at)
        => Gfx.Lz2Decompress(rom.Data, rom.FileOffset(at.LevelTableBlob), 0x1000).AsSpan(0, 2 * MapTiles).ToArray();

    /// <summary>Number the level tiles the way vanilla's scan at $04D7F2 does — 1, 2, 3… in map
    /// order — so an edited map renumbers itself as the game would. Run after a layer 1 edit on
    /// a ROM without LM's table; with the table the numbers are the tiles' own and move with them.</summary>
    public void Renumber()
    {
        if (HasLevelTable) return;
        Array.Clear(Translevels);
        for (int i = 0, n = 1; i < Translevels.Length; i++)
            if ((Layer1[i] & 0xFF) >= 0x56 && (Layer1[i] & 0xFF) <= 0x80) Translevels[i] = (byte)n++;
    }

    /// <summary>Write an edited per-tile level table into Lunar Magic's blob, where the ROM has one
    /// and the packed table fits where the old one sat. Returns a reason otherwise. ponytail: no
    /// relocation, as with the layers.</summary>
    public static string? WriteLevelTable(Rom rom, byte[] translevels)
    {
        var at = Tables.Of(rom);
        if (at.LevelTableBlob == 0) return "the level table needs Lunar Magic's per-tile table; this ROM has none";
        int blob = rom.FileOffset(at.LevelTableBlob);
        int room = Gfx.Lz2Length(rom.Data, blob);
        var packed = Gfx.Lz2Compress(translevels);
        if (packed.Length > room) return $"the level table packs to {packed.Length} bytes, and Lunar Magic's blob has room for {room}";
        packed.CopyTo(rom.Data, blob);
        return null;
    }

    /// <summary>
    /// The reveal list ($04DA1D → $04DA33, walked at $04DA83): the tile an event turns a hidden
    /// tile into — 0x6E becomes the level tile 0x66, and so on for 22 pairs (the last, 0x54 →
    /// 0x23, is two tiles wide). Lunar Magic's "Future Layer 1 Tiles" draws a hidden tile as what
    /// it becomes, translucent; so does <see cref="Layer1Art"/>. -1 for a tile no event changes.
    /// </summary>
    public const int RevealSources = 0x04DA1D, RevealTargets = 0x04DA33, RevealCount = 22;
    public int RevealedTile(int tile)
    {
        int s = Rom.FileOffset(RevealSources), t = Rom.FileOffset(RevealTargets);
        for (int i = 0; i < RevealCount; i++) if (Rom.Data[s + i] == tile) return Rom.Data[t + i];
        return -1;
    }
}
