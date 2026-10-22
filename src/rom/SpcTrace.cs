namespace PipeDream;

/// <summary>A voice keyed on, with the registers it was keyed on with.</summary>
public sealed record KeyOn(long Cycle, int Voice, byte VolL, byte VolR, int Pitch, byte Srcn, byte Adsr1, byte Adsr2, byte Gain)
{
    public override string ToString() =>
        $"t={Cycle,9} KON v{Voice} vol={VolL:X2}/{VolR:X2} pitch={Pitch:X4} srcn={Srcn:X2} adsr={Adsr1:X2}{Adsr2:X2} gain={Gain:X2}";
}

/// <summary>
/// An SPC700 run reduced to what a listener hears of it: each key-on with the registers it was keyed on with, each key-off,
/// and every other DSP write in order. Two engines playing one song are compared on this, not on their code
/// (reference/AMK_PORT.md): the same notes at the same times with the same pitch, volume and envelope.
/// </summary>
public sealed class SpcTrace
{
    public readonly List<KeyOn> KeyOns = [];
    public readonly List<(long Cycle, int Mask)> KeyOffs = [];
    /// <summary>The writes that are not per-voice note events: master and echo volume, FIR, flags, directory, echo start and delay.</summary>
    public readonly List<(long Cycle, int Reg, byte Value)> Global = [];
    /// <summary>Every write to a voice's own registers ($v0-$v7: volume, pitch, sample, envelope), for what changes during a note.</summary>
    public readonly List<(long Cycle, int Reg, byte Value)> Voice = [];

    /// <summary>The value voice register <paramref name="reg"/> held at <paramref name="cycle"/> (the last write at or before it; 0 before any).</summary>
    public int At(int reg, long cycle)
    {
        if (byReg is null)
        {
            byReg = new List<(long, byte)>[0x80];
            foreach (var (c, r, v) in Voice) (byReg[r] ??= []).Add((c, v));
        }
        var list = byReg[reg];
        if (list is null) return 0;
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (list[mid].Item1 <= cycle) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found < 0 ? 0 : list[found].Item2;
    }
    private List<(long, byte)>[]? byReg;

    /// <summary>Run <paramref name="cpu"/> for <paramref name="cycles"/> and trace the DSP writes it made.</summary>
    public static SpcTrace Run(Spc700 cpu, long cycles)
    {
        var shadow = (byte[])cpu.Dsp.Clone();
        cpu.DspWrites = [];
        long start = cpu.Cycles;
        cpu.Run(cycles);
        var t = new SpcTrace();
        foreach (var (cycle, reg, value) in cpu.DspWrites)
        {
            shadow[reg] = value;
            long at = cycle - start;
            if (reg == 0x4C)
            {
                for (int v = 0; v < 8; v++)
                    if ((value & (1 << v)) != 0)
                    {
                        int b = v * 16;
                        t.KeyOns.Add(new KeyOn(at, v, shadow[b], shadow[b + 1], shadow[b + 2] | shadow[b + 3] << 8, shadow[b + 4], shadow[b + 5], shadow[b + 6], shadow[b + 7]));
                    }
            }
            else if (reg == 0x5C) { if (value != 0) t.KeyOffs.Add((at, value)); }
            else if ((reg & 0x0F) <= 7) t.Voice.Add((at, reg, value));
            else if ((reg & 0x0F) > 7 || reg is 0x0C or 0x1C or 0x2C or 0x3C or 0x6C or 0x7C) t.Global.Add((at, reg, value));
        }
        return t;
    }
}
