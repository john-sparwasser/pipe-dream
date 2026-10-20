using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>
/// Our shared routines (data/spritedriver/routines) against PIXI's, run side by side.
///
/// Ours are written from reference/PIXI_ROUTINES.md and SMW's code, never from PIXI's (PIXI is
/// GPL); PIXI's copy is only the oracle here, read at test time from a project of the user's
/// ($PIPEDREAM_SMW_ROOT/BigEye/Sprites/routines) and never shipped. Each routine runs from many
/// random states — all of RAM, A/X/Y, the low flags, the data bank, caller frames on the stack —
/// on the vanilla ROM, once with ours and once with PIXI's, and everything a caller can see must
/// come out the same: registers, flags, stack pointer, how it returned, all 128 KB of RAM. What a
/// routine leaves as a by-product (the spec's word) is masked, per routine, with the reason.
/// </summary>
public class PixiRoutineTests(ITestOutputHelper log)
{
    private const int Trials = 2000;
    /// <summary>How one routine is entered and what of its exit is by-product.</summary>
    private sealed record Case(
        int MaxSlot = 0x0B,                      // X is a slot 0..MaxSlot
        int FlagsMask = 0xBF,                    // V is a by-product everywhere
        bool CompareB = false,                   // the accumulator's high byte, only where the spec makes it exact
        bool CompareA = true,                    // false where the spec leaves A a by-product
        Action<Cpu65816, Random>? Setup = null); // anything the spec needs on entry beyond random

    /// <summary>Flags where only C (and the widths) are in the contract: N and Z are by-products.</summary>
    private const int NoNZ = 0x3D;

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["Aiming"] = new(CompareB: true, Setup: Aim),                                 // B = Vx's high byte
        ["CapeContact"] = new(FlagsMask: NoNZ, CompareA: false, Setup: NearCape),     // C, X, Y are the contract
        ["ChangeMap16"] = new(CompareA: false, Setup: Map16Block),                   // A: not in the contract (C, P, X, Y are)
        ["CheckForContactAlternate"] = new(FlagsMask: NoNZ, CompareA: false, Setup: Boxes),   // N, Z, A: the last compare's
        ["CircleX"] = new(CompareB: true),
        ["CircleY"] = new(CompareB: true),
        ["ExtendedCapeClipping"] = new(MaxSlot: 9, FlagsMask: NoNZ, CompareA: false, Setup: ExtendedNearCape),
        ["ExtendedGetDrawInfo"] = new(MaxSlot: 7, Setup: ExtendedNearCamera),        // 8-9 read past SMW's table
        ["ExtendedHurt"] = new(MaxSlot: 9, FlagsMask: NoNZ, CompareA: false, Setup: ExtendedOnPlayer),
        ["ExtendedSpeed"] = new(MaxSlot: 9, Setup: SmallMode),
        ["FinishOAMWrite"] = new(CompareB: true, Setup: Tiles),                      // B = 0
        ["FireballContact"] = new(CompareA: false, Setup: Fireballs),
        ["GetDrawInfo"] = new(CompareB: true, Setup: NearCamera),                    // B = $01 on the way back
        ["GetMap16"] = new(CompareB: true, Setup: Map16Block),                       // B = the tile's high byte
        ["LoseYoshi"] = new(CompareB: true),
        ["Random"] = new(CompareB: true, Setup: AnyWidths),                          // B kept
        ["SetPlayerClippingAlternate"] = new(CompareB: true, Setup: AnyAWidth),      // B = $03
        ["SetSpriteClippingAlternate"] = new(CompareB: true),                        // B = $0B
        ["ShooterMain"] = new(MaxSlot: 7, Setup: Shooter),
        ["SpawnExtended"] = new(Setup: FreeExtended),
        ["SpawnSmoke"] = new(Setup: FreeSmoke),
        ["SpawnSprite"] = new(Setup: FreeSlot),
        ["SpawnSpriteSafe"] = new(Setup: FreeSlot),
        ["Star"] = new(FlagsMask: 0xBE),                                             // C: not in the contract (only N, Z, from DB)
        ["SubHorzPos"] = new(),
        ["SubOffScreen"] = new(Setup: NearCamera),
        ["SubVertPos"] = new(),
    };

    private static void AnyWidths(Cpu65816 cpu, Random r) => cpu.PresetP((cpu.Registers.P & ~0x30) | (r.Next(4) << 4));
    private static void AnyAWidth(Cpu65816 cpu, Random r) => cpu.PresetP((cpu.Registers.P & ~0x20) | (r.Next(2) << 5));

    private static void Word(byte[] ram, int at, int v) { ram[at] = (byte)v; ram[at + 1] = (byte)(v >> 8); }

    /// <summary>Half the time two boxes that may touch: near each other, small.</summary>
    private static void Boxes(Cpu65816 cpu, Random r)
    {
        AnyAWidth(cpu, r);
        if (r.Next(2) == 0) return;
        var ram = cpu.Ram7E;
        int x = r.Next(0x10000), y = r.Next(0x10000);
        Word(ram, 0x00, x); Word(ram, 0x02, y); Word(ram, 0x04, r.Next(0x40)); Word(ram, 0x06, r.Next(0x40));
        Word(ram, 0x08, x + r.Next(-0x40, 0x40)); Word(ram, 0x0A, y + r.Next(-0x40, 0x40)); Word(ram, 0x0C, r.Next(0x40)); Word(ram, 0x0E, r.Next(0x40));
    }

    /// <summary>Half the time the cape spinning by sprite (or extended sprite) X, on its layer.</summary>
    private static void NearCape(Cpu65816 cpu, Random r)
    {
        if (r.Next(2) == 0) return;
        var ram = cpu.Ram7E;
        int x = cpu.Registers.X & 0xFF;
        ram[0x13E8] = 1; ram[0x15D0 + x] = ram[0x154C + x] = ram[0x1FE2 + x] = 0;
        ram[0x74] = 0; ram[0x13F9] = ram[0x1632 + x];
        int cx = r.Next(0x1000), cy = r.Next(0x200);
        Word(ram, 0x13E9, cx); Word(ram, 0x13EB, cy);
        int sx = cx + r.Next(-0x20, 0x20), sy = cy + r.Next(-0x20, 0x20);
        ram[0xE4 + x] = (byte)sx; ram[0x14E0 + x] = (byte)(sx >> 8); ram[0xD8 + x] = (byte)sy; ram[0x14D4 + x] = (byte)(sy >> 8);
        ram[0x171F + x] = (byte)sx; ram[0x1733 + x] = (byte)(sx >> 8); ram[0x1715 + x] = (byte)sy; ram[0x1729 + x] = (byte)(sy >> 8);
    }

    /// <summary>NearCape, with the caller's box an extended sprite's size: offsets and sides under $20.</summary>
    private static void ExtendedNearCape(Cpu65816 cpu, Random r)
    {
        NearCape(cpu, r);
        for (int i = 0; i < 4; i++) cpu.Ram7E[i] = (byte)r.Next(0x20);
    }

    /// <summary>Any widths; half the time a short distance, so every band of it is reached.</summary>
    private static void Aim(Cpu65816 cpu, Random r)
    {
        AnyWidths(cpu, r);
        if (r.Next(2) == 0) return;
        int reach = 1 << r.Next(1, 10);
        Word(cpu.Ram7E, 0x00, r.Next(-reach, reach)); Word(cpu.Ram7E, 0x02, r.Next(-reach, reach));
    }

    /// <summary>Half the time a fireball or two near sprite X.</summary>
    private static void Fireballs(Cpu65816 cpu, Random r)
    {
        if (r.Next(2) == 0) return;
        var ram = cpu.Ram7E;
        int x = cpu.Registers.X & 0xFF, sx = ram[0xE4 + x] | ram[0x14E0 + x] << 8, sy = ram[0xD8 + x] | ram[0x14D4 + x] << 8;
        for (int f = 8; f <= 9; f++)
        {
            ram[0x170B + f] = (byte)(r.Next(3) == 0 ? 0 : 5);
            int fx = sx + r.Next(-0x18, 0x18), fy = sy + r.Next(-0x18, 0x18);
            ram[0x171F + f] = (byte)fx; ram[0x1733 + f] = (byte)(fx >> 8); ram[0x1715 + f] = (byte)fy; ram[0x1729 + f] = (byte)(fy >> 8);
        }
    }

    /// <summary>
    /// Half the time extended sprite X on the player. Never riding Yoshi: there SMW's $02A469
    /// reads Yoshi's speed by DB, which PIXI's leaves at 0 (bank 0's bytes, a bug) and ours sets
    /// to 2 as SMW does; that one difference is ours on purpose (reference/PIXI_ROUTINES.md).
    /// </summary>
    private static void ExtendedOnPlayer(Cpu65816 cpu, Random r)
    {
        var ram = cpu.Ram7E;
        ram[0x187A] = 0;
        if (r.Next(2) == 0) return;
        int x = cpu.Registers.X & 0xFF, px = ram[0x94] | ram[0x95] << 8, py = ram[0x96] | ram[0x97] << 8;
        int ex = px + r.Next(-0x10, 0x18), ey = py + r.Next(-0x10, 0x28);
        ram[0x171F + x] = (byte)ex; ram[0x1733 + x] = (byte)(ex >> 8); ram[0x1715 + x] = (byte)ey; ram[0x1729 + x] = (byte)(ey >> 8);
    }

    /// <summary>Half the time extended sprite X on or near the screen.</summary>
    private static void ExtendedNearCamera(Cpu65816 cpu, Random r)
    {
        if (r.Next(2) == 0) return;
        var ram = cpu.Ram7E;
        int x = cpu.Registers.X & 0xFF;
        int ex = (ram[0x1A] | ram[0x1B] << 8) + r.Next(-0x40, 0x140), ey = (ram[0x1C] | ram[0x1D] << 8) + r.Next(-0x60, 0x120);
        ram[0x171F + x] = (byte)ex; ram[0x1733 + x] = (byte)(ex >> 8); ram[0x1715 + x] = (byte)ey; ram[0x1729 + x] = (byte)(ey >> 8);
    }

    /// <summary>Mostly a real graphics routine's end: a few tiles, a size SMW uses, OAM in range.</summary>
    private static void Tiles(Cpu65816 cpu, Random r)
    {
        NearCamera(cpu, r);
        if (r.Next(4) == 0) return;
        var g = cpu.Registers;
        int[] sizes = [0x00, 0x02, 0xFF];
        cpu.PresetRegs(g.A & 0xFF00 | r.Next(16), g.X, sizes[r.Next(3)]);
        cpu.Ram7E[0x15EA + (g.X & 0xFF)] = (byte)(r.Next(0x40) * 4);
    }

    /// <summary>Most of the time a free smoke slot.</summary>
    private static void FreeSmoke(Cpu65816 cpu, Random r)
    {
        if (r.Next(4) != 0) cpu.Ram7E[0x17C0 + r.Next(4)] = 0;
    }

    /// <summary>Most of the time a free extended slot, and half the time a parent on the screen.</summary>
    private static void FreeExtended(Cpu65816 cpu, Random r)
    {
        var ram = cpu.Ram7E;
        int x = cpu.Registers.X & 0xFF;
        if (r.Next(4) != 0) ram[0x170B + r.Next(8)] = 0;
        if (r.Next(2) == 0) ram[0x15A0 + x] = ram[0x186C + x] = ram[0x15D0 + x] = 0;
    }

    /// <summary>Half the time shooter X timed out on the screen, with a free slot and SMW's sprite
    /// memory setting (its $02A9DE reads tables by it).</summary>
    private static void Shooter(Cpu65816 cpu, Random r)
    {
        var ram = cpu.Ram7E;
        ram[0x1692] = (byte)r.Next(0x10);
        if (r.Next(2) == 0) return;
        int x = cpu.Registers.X & 0xFF;
        ram[0x17AB + x] = 0;
        int sx = (ram[0x1A] | ram[0x1B] << 8) + r.Next(-0x20, 0x120), sy = (ram[0x1C] | ram[0x1D] << 8) + r.Next(-0x20, 0x100);
        ram[0x179B + x] = (byte)sx; ram[0x17A3 + x] = (byte)(sx >> 8); ram[0x178B + x] = (byte)sy; ram[0x1793 + x] = (byte)(sy >> 8);
        ram[0x14C8 + r.Next(12)] = 0;
    }

    /// <summary>
    /// Any widths, and most of the time a block that is in the level: a height under $13D7, and
    /// a screen that is a row of screens once a vertical layer swaps it.
    ///
    /// Always what SMW has: a level mode, and a layer it has Map16 for; a screen under $20; a
    /// short stripe buffer. Past those the pointers are garbage and the address (or $00C0FB's stripe
    /// writes) can land in low RAM, in each routine's own stack, which differ by construction.
    /// How the two wrap a mode or a screen past a byte was checked value by value instead.
    /// </summary>
    private static void Map16Block(Cpu65816 cpu, Random r)
    {
        var g = cpu.Registers;
        cpu.PresetP((g.P & ~0x30) | (r.Next(4) << 4));
        var ram = cpu.Ram7E;
        int Read16(int a) => cpu.Read(0, a) | cpu.Read(0, a + 1) << 8;
        int layer, mode;
        do { layer = r.Next(2); mode = r.Next(0x20); }                // a layer this mode has Map16 for
        while (Read16(Read16(Read16(0xBEA8 + 2 * layer) + 2 * mode)) < 0xC800);
        ram[0x1933] = (byte)layer;
        ram[0x1925] = (byte)mode;
        cpu.PresetRegs(r.Next(0x200), g.X, g.Y);                            // a tile SMW has (ChangeMap16's; past $1FF $0FBE ends)
        int stripe = r.Next(0x40) * 0x10;
        cpu.Ram7F[0x837B] = (byte)stripe; cpu.Ram7F[0x837C] = (byte)(stripe >> 8);
        if (r.Next(4) != 0)
        {
            int height = r.Next(0x100, 0x400), y = r.Next(height), x = r.Next(0x2000);
            ram[0x13D7] = (byte)height; ram[0x13D8] = (byte)(height >> 8);
            ram[0x98] = (byte)y; ram[0x99] = (byte)(y >> 8);
            ram[0x9A] = (byte)x; ram[0x9B] = (byte)(x >> 8);
            if (r.Next(2) == 0) ram[0x9B] = (byte)r.Next(2);
        }
        ram[0x9B] &= 0x1F;
        // The screen the routine will use (a vertical layer swaps it with $99) must be one this
        // mode's table has; and $0FBE, which SMW fills at level load with each tile's pointer to
        // its 8x8s, points into ROM as it does there.
        bool vertical = ((layer == 0 ? ram[0x5B] : ram[0x5B] >> 1) & 1) != 0;
        int screen = vertical ? ram[0x99] : ram[0x9B];
        if (Read16(Read16(Read16(0xBEA8 + 2 * layer) + 2 * mode) + 3 * screen) < 0xC800)
            ram[vertical ? 0x99 : 0x9B] = 0;
        for (int t = 0; t < 0x200; t++) Word(ram, 0x0FBE + 2 * t, r.Next(0x8000, 0x10000));
    }

    /// <summary>Most of the time one slot free (random RAM leaves almost none), and $04, where
    /// SpawnSpriteSafe starts, a slot.</summary>
    private static void FreeSlot(Cpu65816 cpu, Random r)
    {
        if (r.Next(4) == 0) return;
        cpu.Ram7E[0x14C8 + r.Next(12)] = 0;
        cpu.Ram7E[0x04] = (byte)r.Next(12);
    }

    /// <summary>Most of the time a mode 0-4 loaded just before the call, Z as the load leaves it.</summary>
    private static void SmallMode(Cpu65816 cpu, Random r)
    {
        if (r.Next(4) == 0) return;
        var g = cpu.Registers;
        int a = r.Next(5);
        cpu.PresetRegs(g.A & 0xFF00 | a, g.X, g.Y);
        cpu.PresetP(a == 0 ? g.P | 0x02 : g.P & ~0x02);
    }

    /// <summary>Half the time, sprite X a little off each edge of the screen or on it: random
    /// positions are nearly all far off, and the paths that draw would go untested.</summary>
    private static void NearCamera(Cpu65816 cpu, Random r)
    {
        if (r.Next(2) == 0) return;
        var ram = cpu.Ram7E;
        int x = cpu.Registers.X & 0xFF;
        int sx = (ram[0x1A] | ram[0x1B] << 8) + r.Next(-0x80, 0x180), sy = (ram[0x1C] | ram[0x1D] << 8) + r.Next(-0x60, 0x120);
        ram[0xE4 + x] = (byte)sx; ram[0x14E0 + x] = (byte)(sx >> 8);
        ram[0xD8 + x] = (byte)sy; ram[0x14D4 + x] = (byte)(sy >> 8);
    }

    public static TheoryData<string> Ours()
    {
        var names = new TheoryData<string>();
        foreach (string path in SpriteInsert.BuiltinRoutines().Keys.Order())
            names.Add(path["routines/".Length..^4]);
        return names;
    }

    [PixiRoutineTheory]
    [MemberData(nameof(Ours))]
    public void ours_behaves_as_pixis(string name)
    {
        Assert.True(Cases.TryGetValue(name, out var c), $"{name} has no Case: say how it is entered");
        var (ours, oursAt) = Assemble(SpriteInsert.BuiltinRoutines(), name);
        var (pixis, pixisAt) = Assemble(PixiRoutineTheoryAttribute.Reference(), name);

        // The same trials every run (string.GetHashCode differs per process); set
        // PIPEDREAM_ROUTINE_SEED to try another sample of them.
        int seed0 = int.TryParse(Environment.GetEnvironmentVariable("PIPEDREAM_ROUTINE_SEED"), out int s) ? s : 0;
        var rng = new Random(name.Aggregate(17 + seed0, (h, ch) => h * 31 + ch) & 0x7FFFFFFF);
        int failures = 0;
        var exits = new SortedDictionary<string, int>();      // how the trials came back, to see the paths were taken
        for (int t = 0; t < Trials && failures < 5; t++)
        {
            int seed = rng.Next();
            var a = Run(ours, oursAt, c, seed);
            var b = Run(pixis, pixisAt, c, seed);
            string exit = b.Error ?? $"{(b.PBR == 0xFF ? "RTL" : "to a caller")} SP ${b.SP:X4} C{b.P & 1}";
            exits[exit] = exits.GetValueOrDefault(exit) + 1;
            if (Differences(a, b, c) is { Count: > 0 } diff)
            {
                failures++;
                log.WriteLine($"trial {t} (seed {seed}, entry {b.Entry}): {string.Join("; ", diff)}");
            }
        }
        log.WriteLine("exits: " + string.Join(", ", exits.Select(e => $"{e.Key} ×{e.Value}")));
        Assert.True(failures == 0, $"{name}: differs from PIXI's (first differences in the test output)");
    }

    private sealed record Exit(int A, int X, int Y, int P, int SP, int DBR, int PBR, byte[] Ram7E, byte[] Ram7F, string? Error, string Entry);

    private static Exit Run(Rom rom, int at, Case c, int seed)
    {
        var r = new Random(seed);
        var cpu = new Cpu65816(rom);
        r.NextBytes(cpu.Ram7E);
        r.NextBytes(cpu.Ram7F);
        // A bank that maps low RAM and holds the same ROM on both sides: the routines themselves
        // go into the expanded banks from $10, which differ.
        int dbr = r.Next(0x01, 0x10) | (r.Next(2) << 7);
        cpu.PresetDbr(dbr);
        cpu.PresetCallerFrames(3);
        cpu.PresetP(0x30 | (r.Next(0x100) & 0xCF));
        cpu.PresetRegs(r.Next(0x10000), r.Next(c.MaxSlot + 1), r.Next(0x100));
        c.Setup?.Invoke(cpu, r);
        var on = cpu.Registers;
        string entry = $"A ${on.A:X4} X ${on.X:X2} Y ${on.Y:X2} P ${on.P:X2} DB ${on.DBR:X2}";
        string? error = null;
        try { cpu.CallLong(at, 200_000, anyBank: true); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { error = e.GetType().Name; }
        var g = cpu.Registers;
        return new(g.A, g.X, g.Y, g.P, g.SP, g.DBR, g.PBR, cpu.Ram7E, cpu.Ram7F, error, entry);
    }

    private static List<string> Differences(Exit a, Exit b, Case c)
    {
        var d = new List<string>();
        if (a.Error is not null && a.Error == b.Error) return d;           // both ran into the weeds: nothing to compare
        if (a.Error != b.Error) d.Add($"ours {a.Error ?? "returned"}, PIXI's {b.Error ?? "returned"}");
        int aMask = !c.CompareA ? 0 : c.CompareB ? 0xFFFF : 0xFF;
        void Reg(string n, int x, int y, int mask) { if ((x & mask) != (y & mask)) d.Add($"{n} ours ${x & mask:X} PIXI's ${y & mask:X}"); }
        Reg("A", a.A, b.A, aMask);
        Reg("X", a.X, b.X, 0xFFFF);
        Reg("Y", a.Y, b.Y, 0xFFFF);
        Reg("P", a.P, b.P, c.FlagsMask);
        Reg("SP", a.SP, b.SP, 0xFFFF);
        Reg("DB", a.DBR, b.DBR, 0xFF);
        Reg("PB", a.PBR, b.PBR, 0xFF);
        foreach (var (bank, x, y) in new[] { (0x7E, a.Ram7E, b.Ram7E), (0x7F, a.Ram7F, b.Ram7F) })
            for (int i = 0; i < 0x10000 && d.Count < 12; i++)
                // Below the stack pointer is what the routine pushed and pulled for itself: dead.
                if (x[i] != y[i] && !(bank == 0x7E && i >= 0x100 && i <= Math.Min(a.SP, b.SP)))
                    d.Add($"${bank:X2}{i:X4} ours ${x[i]:X2} PIXI's ${y[i]:X2}");
        return d;
    }

    /// <summary>A vanilla ROM, expanded for freespace, with every routine of <paramref name="files"/>
    /// (they call each other); returns it and where <paramref name="name"/> landed.</summary>
    private static (Rom Rom, int At) Assemble(Dictionary<string, string> files, string name)
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        rom.ExpandTo(0x100000);
        // The driver's "give this slot its custom tables" hook, which spawning a custom sprite calls: a
        // stand-in that clobbers $00-$02 as the real one does (the spawns must keep them).
        byte[] hook = [0xA9, 0xAA, 0x85, 0x00, 0x85, 0x01, 0x85, 0x02, 0x6B];   // LDA #$AA : STA $00 : STA $01 : STA $02 : RTL
        hook.CopyTo(rom.Data, rom.FileOffset(0x0187A7));
        var at = SpriteInsert.AssembleRoutines(rom, files, out string? error);
        Assert.True(error is null, error);
        return (rom, at[name]);
    }
}

/// <summary>Runs when Asar, the vanilla ROM and PIXI's routines (as a user's project carries
/// them) are all on this machine; PIXI's copy is the oracle, so without it there is no test.</summary>
public sealed class PixiRoutineTheoryAttribute : TheoryAttribute
{
    private static string Folder => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects", "BigEye", "Sprites", "routines");

    public PixiRoutineTheoryAttribute()
    {
        if (!Asar.Available) Skip = "Asar library not found (set PIPEDREAM_ASAR to libasar's path)";
        else if (!File.Exists(TestRom.RealRomPath)) Skip = "real ROM not present: " + TestRom.RealRomPath;
        else if (!Directory.Exists(Folder)) Skip = "PIXI's routines not present: " + Folder;
    }

    /// <summary>PIXI's routines, keyed as an imported folder's are.</summary>
    internal static Dictionary<string, string> Reference()
        => Directory.GetFiles(Folder, "*.asm").ToDictionary(f => "routines/" + Path.GetFileName(f), File.ReadAllText);
}
