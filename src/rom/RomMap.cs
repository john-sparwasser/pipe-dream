namespace PipeDream;

/// <summary>
/// How an image addresses its ROM: SNES address ↔ headerless PC offset. A <see cref="Rom"/> takes
/// its map from its header ($FFD5), read live, so an image the SA-1 Pack was just applied to
/// addresses as SA-1 at once. The two agree below 2MB in banks $00-$3F, where the prep and
/// everything else of ours lives; they part above it.
/// </summary>
public abstract class RomMap
{
    public static readonly RomMap LoRom = new LoRomMap();
    public static readonly RomMap Sa1 = new Sa1Map();

    /// <summary>The map a header's map-mode byte declares: SA-1 for $23, LoROM otherwise.</summary>
    public static RomMap ForMapMode(byte mapMode) => mapMode == 0x23 ? Sa1 : LoRom;

    public abstract int SnesToPc(int snes);
    public abstract int PcToSnes(int pc);
}

/// <summary>LoROM: 32KB per bank, banks $80+ mirroring $00+ (FastROM), so $40+ past 2MB.</summary>
public sealed class LoRomMap : RomMap
{
    internal LoRomMap() { }

    public override int SnesToPc(int snes)
        => ((snes & 0x7F0000) >> 1) | (snes & 0x7FFF);

    /// <summary>Headerless PC offset → LoROM SNES address (the high half of bank pc / 32KB).</summary>
    public override int PcToSnes(int pc)
        => ((pc << 1) & 0x7F0000) | (pc & 0x7FFF) | 0x8000;
}

/// <summary>
/// SA-1 under the Super MMC's default mapping (what the SA-1 Pack sets): the first 2MB LoROM-style
/// in banks $00-$3F, the next 2MB the same way in $80-$BF, and all 4MB HiROM-style in $C0-$FF.
/// Banks $40-$7F are BW-RAM and work RAM, not ROM.
/// </summary>
public sealed class Sa1Map : RomMap
{
    internal Sa1Map() { }

    public override int SnesToPc(int snes)
    {
        int bank = (snes >> 16) & 0xFF;
        if (bank >= 0xC0) return ((bank - 0xC0) << 16) | (snes & 0xFFFF);
        if (bank is >= 0x40 and < 0x80)
            throw new ArgumentOutOfRangeException(nameof(snes), $"${snes:X6} is RAM on SA-1, not ROM");
        return ((bank & 0x80) != 0 ? 0x200000 : 0) | ((bank & 0x3F) << 15) | (snes & 0x7FFF);
    }

    public override int PcToSnes(int pc)
        => (pc >= 0x200000 ? 0x800000 : 0) | (((pc & 0x1FFFFF) << 1) & 0x3F0000) | (pc & 0x7FFF) | 0x8000;
}
