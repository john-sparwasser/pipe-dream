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

    /// <summary>The routine Lunar Magic's ExAnimation engines call through their first
    /// `JSL $000003`, or null to keep LM's call as it is.</summary>
    public abstract int? ExAnimTrampoline { get; }

    /// <summary>The prep's stamp list of <paramref name="version"/> for this target.</summary>
    public abstract List<(int Pc, byte[] Bytes)> PrepStamps(int version);

    /// <summary>The build's last step over a prepped image of <paramref name="version"/>; the
    /// errors that stopped it, none when it is done.</summary>
    public abstract IReadOnlyList<string> Finish(Rom rom, int version);
}

/// <summary>A plain LoROM build: RAM where vanilla has it, and nothing after the prep.</summary>
public sealed class LoRomTarget : RomTarget
{
    internal LoRomTarget() { }
    public override RomMap Map => RomMap.LoRom;
    public override int Ram(int addr) => addr;
    public override int RamLong(int addr) => addr;
    public override int? ExAnimTrampoline => null;
    public override List<(int Pc, byte[] Bytes)> PrepStamps(int version) => RomPrep.BuildStamps(version, this);
    public override IReadOnlyList<string> Finish(Rom rom, int version) => [];
}

/// <summary>
/// An SA-1 build on the SA-1 Pack in <see cref="PackDir"/>: RAM where the pack moved it
/// (<see cref="Sa1Remap"/>, its sprite tables read from the pack), the prep's SA-1 form, and the
/// pack applied last with the prep restamped over it (<see cref="RomPrep.ApplySa1"/>).
/// </summary>
public sealed class Sa1Target(string packDir) : RomTarget
{
    public string PackDir => packDir;
    public Sa1Remap Remap { get; } = Sa1Remap.FromSpriteTables(File.ReadAllText(Path.Combine(packDir, "more_sprites", "sprite_tables.asm")));

    public override RomMap Map => RomMap.Sa1;
    public override int Ram(int addr) => addr < 0x2000 ? Remap.Abs(addr) : addr;
    public override int RamLong(int addr) => Remap.Long(addr);
    public override int? ExAnimTrampoline => LmExAnimEngine.Sa1TrampolineSnes;
    public override List<(int Pc, byte[] Bytes)> PrepStamps(int version) => RomPrep.Sa1Stamps(version, this);

    public override IReadOnlyList<string> Finish(Rom rom, int version)
    {
        var r = Asar.Patch(rom, Path.Combine(packDir, "sa1.asm"));
        if (!r.Ok) return r.Errors;
        RomPrep.ApplySa1(rom, version, this, r.Blocks.Select(b => (b.Snes, b.Length)));
        return [];
    }
}
