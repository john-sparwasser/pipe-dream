namespace PipeDream;

/// <summary>
/// What a build is FOR: plain LoROM, or SA-1 (reference/SA1_PORT.md). Everything that writes code
/// asks its target instead of asking "is this SA-1?" — where a RAM address lives, what the prep's
/// stamps are, what the build does last. The data a save writes (levels, Map16, palettes, the
/// overworld) is the same on both, so only these differ.
///
/// The editor's ROM is always LoROM; a target only changes the build's copy.
/// </summary>
public abstract class RomTarget
{
    public static readonly RomTarget LoRom = new LoRomTarget();

    /// <summary>How the finished image addresses its ROM.</summary>
    public abstract RomMap Map { get; }

    /// <summary>A 16-bit RAM operand ($0000-$1FFF) where this target keeps it.</summary>
    public abstract int Ram(int addr);

    /// <summary>A 24-bit RAM operand where this target keeps it.</summary>
    public abstract int RamLong(int addr);

    /// <summary>The DMA channel a vanilla transfer on <paramref name="channel"/> runs on.</summary>
    public abstract int DmaChannel(int channel);

    /// <summary>The routine Lunar Magic's ExAnimation engines call through their first
    /// `JSL $000003`, or null to keep LM's call as it is.</summary>
    public abstract int? ExAnimTrampoline { get; }

    /// <summary>The prep's stamp list of <paramref name="version"/> for this target.</summary>
    public abstract List<(int Pc, byte[] Bytes)> PrepStamps(int version);

    /// <summary>The build's first step, over a fresh copy of a base prepped at
    /// <paramref name="version"/>: make it this target's image, so that everything the build writes
    /// after is written for it. The errors that stopped it, or the blocks it wrote (SNES addresses),
    /// which the build's overlap check then owns.</summary>
    public abstract (IReadOnlyList<string> Errors, IReadOnlyList<Asar.WrittenBlock> Written) Install(Rom rom, int version);
}

/// <summary>A plain LoROM build: RAM where vanilla has it, and nothing after the prep.</summary>
public sealed class LoRomTarget : RomTarget
{
    internal LoRomTarget() { }
    public override RomMap Map => RomMap.LoRom;
    public override int Ram(int addr) => addr;
    public override int RamLong(int addr) => addr;
    public override int DmaChannel(int channel) => channel;
    public override int? ExAnimTrampoline => null;
    public override List<(int Pc, byte[] Bytes)> PrepStamps(int version) => RomPrep.BuildStamps(version, this);
    public override (IReadOnlyList<string> Errors, IReadOnlyList<Asar.WrittenBlock> Written) Install(Rom rom, int version) => ([], []);
}

/// <summary>
/// An SA-1 build on the SA-1 Pack in <see cref="PackDir"/>: RAM where the pack moved it
/// (<see cref="Sa1Remap"/>, its sprite tables read from the pack), the prep's SA-1 form, and the
/// pack applied first with the prep restamped over it (<see cref="RomPrep.ApplySa1"/>).
/// </summary>
public sealed class Sa1Target(string packDir) : RomTarget
{
    public string PackDir => packDir;
    public Sa1Remap Remap { get; } = Sa1Remap.FromSpriteTables(File.ReadAllText(Path.Combine(packDir, "more_sprites", "sprite_tables.asm")));

    public override RomMap Map => RomMap.Sa1;
    public override int Ram(int addr) => addr < 0x2000 ? Remap.Abs(addr) : addr;
    public override int RamLong(int addr) => Remap.Long(addr);
    /// <summary>The pack gives channel 1 to its window HDMA and moves vanilla's DMA on it to
    /// channel 2, registers and enable bit alike (remap/dma.asm: "NMI-DMA goes to channel 2").</summary>
    public override int DmaChannel(int channel) => channel == 1 ? 2 : channel;
    public override int? ExAnimTrampoline => LmExAnimEngine.Sa1TrampolineSnes;
    public override List<(int Pc, byte[] Bytes)> PrepStamps(int version) => RomPrep.Sa1Stamps(version, this);

    public override (IReadOnlyList<string> Errors, IReadOnlyList<Asar.WrittenBlock> Written) Install(Rom rom, int version)
    {
        if (version != LmSa1Forms.PrepVersion)
            return ([$"SA-1 needs the base at prep v{LmSa1Forms.PrepVersion}, and this one is at v{version}"], []);
        var r = Asar.Patch(rom, Path.Combine(packDir, "sa1.asm"));
        if (!r.Ok) return (r.Errors, []);
        RomPrep.ApplySa1(rom, version, this, r.Blocks.Select(b => (b.Snes, b.Length)));
        return ([], r.Blocks);
    }
}
