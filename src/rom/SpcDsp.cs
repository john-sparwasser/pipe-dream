namespace PipeDream;

/// <summary>
/// The S-DSP, the SNES sound chip, one 32 kHz stereo sample at a time, written from nocash's fullsnes (the "SNES APU DSP"
/// chapters): BRR decoding, the pitch counter with pitch modulation, 4-point Gaussian interpolation, ADSR and GAIN envelopes,
/// noise, the mixer, and echo with its 8-tap FIR. It reads its registers from <see cref="Spc700.Dsp"/> and the samples and echo
/// buffer from <see cref="Spc700.Ram"/>; <see cref="Spc700"/> tells it about KON and ENDX writes. Not cycle-exact: everything a
/// sample does happens at once, KON and KOFF are polled every other sample, and the rate counters have no per-rate phase.
/// </summary>
public sealed class SpcDsp
{
    private readonly Spc700 cpu;
    private readonly Voice[] voices = [new(), new(), new(), new(), new(), new(), new(), new()];
    private int konLatch, sampleCount, rateCounter, noise = 0x4000;
    private readonly int[] firL = new int[8], firR = new int[8];
    private int firIndex, echoIndex, echoLength;

    public SpcDsp(Spc700 cpu)
    {
        this.cpu = cpu;
        cpu.Audio = this;
    }

    private sealed class Voice
    {
        public int Addr, KonDelay, Counter, Env, Mode, Out, Bent;            // Mode: 0 release, 1 attack, 2 decay, 3 sustain
        public long Decoded;                                                   // samples decoded since key-on
        public readonly short[] Ring = new short[32];
        public int Old, Older;                                                 // BRR filter history
        public bool Keyed;
    }

    /// <summary>Called by <see cref="Spc700"/> when the program writes a DSP register.</summary>
    internal void Written(int reg, byte value)
    {
        if (reg == 0x4C) konLatch |= value;
        else if (reg == 0x7C) cpu.Dsp[0x7C] = 0;                             // any write clears every ENDX bit
    }

    private byte R(int reg) => cpu.Dsp[reg];
    private sbyte S(int reg) => (sbyte)cpu.Dsp[reg];

    /// <summary><paramref name="samples"/> samples of <paramref name="cpu"/> playing (32 SPC700 cycles each), left and right
    /// interleaved.</summary>
    public static short[] Render(Spc700 cpu, int samples)
    {
        var dsp = cpu.Audio ?? new SpcDsp(cpu);
        var pcm = new short[samples * 2];
        long due = cpu.Cycles;
        for (int i = 0; i < samples; i++)
        {
            due += 32;
            cpu.Run(due - cpu.Cycles);
            (pcm[2 * i], pcm[2 * i + 1]) = dsp.Sample();
        }
        return pcm;
    }

    /// <summary>16-bit stereo PCM at 32 kHz as a .wav file.</summary>
    public static byte[] Wav(short[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(32000); w.Write(32000 * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }

    /// <summary>One 32 kHz sample of output, left and right.</summary>
    public (short L, short R) Sample()
    {
        rateCounter = rateCounter == 0 ? 0x77FF : rateCounter - 1;
        bool poll = (sampleCount++ & 1) == 0;
        int flg = R(0x6C);
        if (Fires(flg & 0x1F)) noise = (noise >> 1) & 0x3FFF | ((noise ^ noise >> 1) & 1) << 14;
        int non = R(0x3D), eon = R(0x4D), pmon = R(0x2D);
        if (poll)
        {
            int koff = R(0x5C);
            for (int v = 0; v < 8; v++)
            {
                if ((koff >> v & 1) != 0) voices[v].Mode = 0;
                if ((konLatch >> v & 1) != 0) KeyOn(v);
            }
            konLatch = 0;
        }

        int mainL = 0, mainR = 0, echoL = 0, echoR = 0;
        for (int v = 0; v < 8; v++)
        {
            var x = voices[v];
            if ((flg & 0x80) != 0) { x.Mode = 0; x.Env = 0; }
            int o = VoiceSample(v, x, (non >> v & 1) != 0, v > 0 && (pmon >> v & 1) != 0 ? voices[v - 1].Out : (int?)null);
            int l = o * S(v << 4) >> 6, r = o * S(v << 4 | 1) >> 6;
            mainL = Clamp16(mainL + l); mainR = Clamp16(mainR + r);
            if ((eon >> v & 1) != 0) { echoL = Clamp16(echoL + l); echoR = Clamp16(echoR + r); }
        }

        // Echo: the oldest entry of the ring through the FIR, then the new one written over it.
        int at = (R(0x6D) << 8) + echoIndex * 4;
        if (echoIndex == 0) echoLength = Math.Max(1, (R(0x7D) & 0x0F) << 9);
        firIndex = (firIndex + 1) & 7;
        firL[firIndex] = (short)(cpu.Ram[at & 0xFFFF] | cpu.Ram[(at + 1) & 0xFFFF] << 8) >> 1;
        firR[firIndex] = (short)(cpu.Ram[(at + 2) & 0xFFFF] | cpu.Ram[(at + 3) & 0xFFFF] << 8) >> 1;
        int fl = Fir(firL), fr = Fir(firR);
        int inL = Clamp16(echoL + (fl * S(0x0D) >> 7)) & ~1, inR = Clamp16(echoR + (fr * S(0x0D) >> 7)) & ~1;
        if ((flg & 0x20) == 0)
        {
            cpu.Ram[at & 0xFFFF] = (byte)inL; cpu.Ram[(at + 1) & 0xFFFF] = (byte)(inL >> 8);
            cpu.Ram[(at + 2) & 0xFFFF] = (byte)inR; cpu.Ram[(at + 3) & 0xFFFF] = (byte)(inR >> 8);
        }
        if (++echoIndex >= echoLength) echoIndex = 0;

        int outL = Clamp16((mainL * S(0x0C) >> 7) + (fl * S(0x2C) >> 7));
        int outR = Clamp16((mainR * S(0x1C) >> 7) + (fr * S(0x3C) >> 7));
        if ((flg & 0x40) != 0) outL = outR = 0;
        return ((short)outL, (short)outR);
    }

    private int Fir(int[] buf)
    {
        int sum = 0;
        for (int k = 0; k < 7; k++) sum += buf[(firIndex - 7 + k) & 7] * S(k << 4 | 0x0F) >> 6;
        sum = (short)sum;                                                     // the first seven wrap
        return Clamp16(sum + (buf[firIndex] * S(0x7F) >> 6));
    }

    private void KeyOn(int v)
    {
        var x = voices[v];
        int dir = R(0x5D) << 8, srcn = R(v << 4 | 4);
        x.Addr = cpu.Ram[(dir + srcn * 4) & 0xFFFF] | cpu.Ram[(dir + srcn * 4 + 1) & 0xFFFF] << 8;
        x.KonDelay = 5; x.Counter = 0; x.Env = 0; x.Mode = 1; x.Decoded = 0; x.Old = x.Older = 0; x.Keyed = true;
        Array.Clear(x.Ring);
        cpu.Dsp[0x7C] &= (byte)~(1 << v);
    }

    /// <summary>The voice's 15-bit output after its envelope (also kept for OUTX and the next voice's pitch modulation).</summary>
    private int VoiceSample(int v, Voice x, bool noisy, int? modulator)
    {
        int outv = 0;
        if (x.KonDelay > 0) x.KonDelay--;
        else if (x.Keyed)
        {
            int whole = x.Counter >> 12;
            while (x.Decoded < whole + 4) DecodeBlock(v, x);
            int f = (x.Counter >> 4) & 0xFF;
            int sample;
            if (noisy) sample = (short)(noise << 1) >> 1;
            else
            {
                int s0 = x.Ring[whole & 31], s1 = x.Ring[(whole + 1) & 31], s2 = x.Ring[(whole + 2) & 31], s3 = x.Ring[(whole + 3) & 31];
                int sum = Gauss[0xFF - f] * s0 >> 10;
                sum += Gauss[0x1FF - f] * s1 >> 10;
                sum += Gauss[0x100 + f] * s2 >> 10;
                sum = (short)sum;
                sum = Clamp16(sum + (Gauss[f] * s3 >> 10));
                sample = sum >> 1;
            }
            Envelope(v, x);
            outv = sample * x.Env >> 11;
            int step = (R(v << 4 | 2) | R(v << 4 | 3) << 8) & 0x3FFF;
            if (modulator is { } m) step = step * ((m >> 4) + 0x400) >> 10;
            x.Counter += Math.Min(step, 0x3FFF);
            // Keep the counter from overflowing: both it and the decoded count move back by a multiple of the ring's size.
            if (whole >= 0x1000) { x.Counter -= 0x1000 << 12; x.Decoded -= 0x1000; }
        }
        x.Out = outv;
        cpu.Dsp[v << 4 | 8] = (byte)(x.Env >> 4);
        cpu.Dsp[v << 4 | 9] = (byte)(outv >> 7);
        return outv;
    }

    private void DecodeBlock(int v, Voice x)
    {
        int header = cpu.Ram[x.Addr & 0xFFFF];
        int shift = header >> 4, filter = (header >> 2) & 3;
        for (int i = 0; i < 16; i++)
        {
            int b = cpu.Ram[(x.Addr + 1 + i / 2) & 0xFFFF];
            int nibble = (i & 1) == 0 ? (sbyte)b >> 4 : (sbyte)(b << 4) >> 4;
            int s = shift <= 12 ? nibble << shift >> 1 : (nibble >> 3) << 12 >> 1;
            s += filter switch
            {
                1 => x.Old + (-x.Old >> 4),
                2 => x.Old * 2 + (-x.Old * 3 >> 5) - x.Older + (x.Older >> 4),
                3 => x.Old * 2 + (-x.Old * 13 >> 6) - x.Older + (x.Older * 3 >> 4),
                _ => 0,
            };
            s = Math.Clamp(s, -0x8000, 0x7FFF);
            s = (short)(s << 1) >> 1;                                           // clipped to 15 bits, losing the sign as the chip does
            x.Older = x.Old; x.Old = s;
            x.Ring[x.Decoded++ & 31] = (short)s;
        }
        if ((header & 1) != 0)
        {
            cpu.Dsp[0x7C] |= (byte)(1 << v);
            int dir = R(0x5D) << 8, srcn = R(v << 4 | 4);
            x.Addr = cpu.Ram[(dir + srcn * 4 + 2) & 0xFFFF] | cpu.Ram[(dir + srcn * 4 + 3) & 0xFFFF] << 8;
            if ((header & 2) == 0) { x.Mode = 0; x.Env = 0; }
        }
        else x.Addr += 9;
    }

    private void Envelope(int v, Voice x)
    {
        int adsr1 = R(v << 4 | 5), adsr2 = R(v << 4 | 6), gain = R(v << 4 | 7);
        int env = x.Env;
        if (x.Mode == 0) env -= 8;
        else if ((adsr1 & 0x80) != 0)
        {
            switch (x.Mode)
            {
                case 1:
                    int rate = (adsr1 & 0x0F) * 2 + 1;
                    if (Fires(rate)) env += rate == 31 ? 1024 : 32;
                    break;
                case 2:
                    if (Fires(((adsr1 >> 4) & 7) * 2 + 16)) env -= ((env - 1) >> 8) + 1;
                    break;
                default:
                    if (Fires(adsr2 & 0x1F)) env -= ((env - 1) >> 8) + 1;
                    break;
            }
        }
        else if ((gain & 0x80) == 0) env = (gain & 0x7F) * 16;
        else if (Fires(gain & 0x1F))
            env += ((gain >> 5) & 3) switch
            {
                0 => -32,
                1 => -(((env - 1) >> 8) + 1),
                2 => 32,
                _ => x.Bent < 0x600 ? 32 : 8,
            };
        if (x.Mode == 1 && env >= 0x7E0) x.Mode = 2;
        if (x.Mode == 2 && env <= ((adsr2 >> 5) + 1) * 0x100) x.Mode = 3;
        x.Bent = env & 0x7FF;                                                     // bent increase looks at this, clipped
        x.Env = Math.Clamp(env, 0, 0x7FF);
    }

    private static readonly int[] RatePeriod =
        [0, 2048, 1536, 1280, 1024, 768, 640, 512, 384, 320, 256, 192, 160, 128, 96, 80, 64, 48, 40, 32, 24, 20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1];

    /// <summary>Whether a 5-bit rate steps on this sample (rate 0 never does).</summary>
    private bool Fires(int rate) => rate != 0 && rateCounter % RatePeriod[rate] == 0;

    private static int Clamp16(int v) => Math.Clamp(v, -0x8000, 0x7FFF);

    /// <summary>The interpolation table in the chip's ROM, as fullsnes lists it.</summary>
    private static readonly int[] Gauss =
    [
        0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,0x000,
        0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x001,0x002,0x002,0x002,0x002,0x002,
        0x002,0x002,0x003,0x003,0x003,0x003,0x003,0x004,0x004,0x004,0x004,0x004,0x005,0x005,0x005,0x005,
        0x006,0x006,0x006,0x006,0x007,0x007,0x007,0x008,0x008,0x008,0x009,0x009,0x009,0x00A,0x00A,0x00A,
        0x00B,0x00B,0x00B,0x00C,0x00C,0x00D,0x00D,0x00E,0x00E,0x00F,0x00F,0x00F,0x010,0x010,0x011,0x011,
        0x012,0x013,0x013,0x014,0x014,0x015,0x015,0x016,0x017,0x017,0x018,0x018,0x019,0x01A,0x01B,0x01B,
        0x01C,0x01D,0x01D,0x01E,0x01F,0x020,0x020,0x021,0x022,0x023,0x024,0x024,0x025,0x026,0x027,0x028,
        0x029,0x02A,0x02B,0x02C,0x02D,0x02E,0x02F,0x030,0x031,0x032,0x033,0x034,0x035,0x036,0x037,0x038,
        0x03A,0x03B,0x03C,0x03D,0x03E,0x040,0x041,0x042,0x043,0x045,0x046,0x047,0x049,0x04A,0x04C,0x04D,
        0x04E,0x050,0x051,0x053,0x054,0x056,0x057,0x059,0x05A,0x05C,0x05E,0x05F,0x061,0x063,0x064,0x066,
        0x068,0x06A,0x06B,0x06D,0x06F,0x071,0x073,0x075,0x076,0x078,0x07A,0x07C,0x07E,0x080,0x082,0x084,
        0x086,0x089,0x08B,0x08D,0x08F,0x091,0x093,0x096,0x098,0x09A,0x09C,0x09F,0x0A1,0x0A3,0x0A6,0x0A8,
        0x0AB,0x0AD,0x0AF,0x0B2,0x0B4,0x0B7,0x0BA,0x0BC,0x0BF,0x0C1,0x0C4,0x0C7,0x0C9,0x0CC,0x0CF,0x0D2,
        0x0D4,0x0D7,0x0DA,0x0DD,0x0E0,0x0E3,0x0E6,0x0E9,0x0EC,0x0EF,0x0F2,0x0F5,0x0F8,0x0FB,0x0FE,0x101,
        0x104,0x107,0x10B,0x10E,0x111,0x114,0x118,0x11B,0x11E,0x122,0x125,0x129,0x12C,0x130,0x133,0x137,
        0x13A,0x13E,0x141,0x145,0x148,0x14C,0x150,0x153,0x157,0x15B,0x15F,0x162,0x166,0x16A,0x16E,0x172,
        0x176,0x17A,0x17D,0x181,0x185,0x189,0x18D,0x191,0x195,0x19A,0x19E,0x1A2,0x1A6,0x1AA,0x1AE,0x1B2,
        0x1B7,0x1BB,0x1BF,0x1C3,0x1C8,0x1CC,0x1D0,0x1D5,0x1D9,0x1DD,0x1E2,0x1E6,0x1EB,0x1EF,0x1F3,0x1F8,
        0x1FC,0x201,0x205,0x20A,0x20F,0x213,0x218,0x21C,0x221,0x226,0x22A,0x22F,0x233,0x238,0x23D,0x241,
        0x246,0x24B,0x250,0x254,0x259,0x25E,0x263,0x267,0x26C,0x271,0x276,0x27B,0x280,0x284,0x289,0x28E,
        0x293,0x298,0x29D,0x2A2,0x2A6,0x2AB,0x2B0,0x2B5,0x2BA,0x2BF,0x2C4,0x2C9,0x2CE,0x2D3,0x2D8,0x2DC,
        0x2E1,0x2E6,0x2EB,0x2F0,0x2F5,0x2FA,0x2FF,0x304,0x309,0x30E,0x313,0x318,0x31D,0x322,0x326,0x32B,
        0x330,0x335,0x33A,0x33F,0x344,0x349,0x34E,0x353,0x357,0x35C,0x361,0x366,0x36B,0x370,0x374,0x379,
        0x37E,0x383,0x388,0x38C,0x391,0x396,0x39B,0x39F,0x3A4,0x3A9,0x3AD,0x3B2,0x3B7,0x3BB,0x3C0,0x3C5,
        0x3C9,0x3CE,0x3D2,0x3D7,0x3DC,0x3E0,0x3E5,0x3E9,0x3ED,0x3F2,0x3F6,0x3FB,0x3FF,0x403,0x408,0x40C,
        0x410,0x415,0x419,0x41D,0x421,0x425,0x42A,0x42E,0x432,0x436,0x43A,0x43E,0x442,0x446,0x44A,0x44E,
        0x452,0x455,0x459,0x45D,0x461,0x465,0x468,0x46C,0x470,0x473,0x477,0x47A,0x47E,0x481,0x485,0x488,
        0x48C,0x48F,0x492,0x496,0x499,0x49C,0x49F,0x4A2,0x4A6,0x4A9,0x4AC,0x4AF,0x4B2,0x4B5,0x4B7,0x4BA,
        0x4BD,0x4C0,0x4C3,0x4C5,0x4C8,0x4CB,0x4CD,0x4D0,0x4D2,0x4D5,0x4D7,0x4D9,0x4DC,0x4DE,0x4E0,0x4E3,
        0x4E5,0x4E7,0x4E9,0x4EB,0x4ED,0x4EF,0x4F1,0x4F3,0x4F5,0x4F6,0x4F8,0x4FA,0x4FB,0x4FD,0x4FF,0x500,
        0x502,0x503,0x504,0x506,0x507,0x508,0x50A,0x50B,0x50C,0x50D,0x50E,0x50F,0x510,0x511,0x511,0x512,
        0x513,0x514,0x514,0x515,0x516,0x516,0x517,0x517,0x517,0x518,0x518,0x518,0x518,0x518,0x519,0x519
    ];
}
