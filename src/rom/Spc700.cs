namespace PipeDream;

/// <summary>
/// An SPC700 interpreter: 64 KB of ARAM, the four I/O ports to the main CPU, the three timers and a DSP
/// register file that records what is written to it and when. It exists to run the music engine
/// (AMK_PORT.md) against AddmusicK's output and to say what the DSP was told, as <see cref="Cpu65816"/> does
/// for the main CPU. It makes no sound, has no IPL ROM and does not model the DSP's voices.
/// Cycle counts are the documented ones: branches taken add 2; there is no bus-wait or page-cross penalty.
/// </summary>
public sealed class Spc700
{
    public readonly byte[] Ram = new byte[0x10000];
    public readonly byte[] Dsp = new byte[0x80];
    public int A, X, Y, Sp, Pc;
    public long Cycles;
    public bool Halted;                                  // SLEEP or STOP

    /// <summary>What the SPC reads at $F4-$F7 (the main CPU's side) and what it wrote there.</summary>
    public readonly byte[] FromCpu = new byte[4], ToCpu = new byte[4];

    /// <summary>Every DSP register write: cycle, register, value. Null to not record.</summary>
    public List<(long Cycle, int Reg, byte Value)>? DspWrites = [];
    /// <summary>Every write to the four output ports ($F4-$F7), when a run asks for them.</summary>
    public List<(long Cycle, int Port, byte Value)>? PortWrites;

    // PSW: N V P B H I Z C
    private bool fN, fV, fP, fB, fH, fI, fZ, fC;
    public int Psw
    {
        get => (fN ? 0x80 : 0) | (fV ? 0x40 : 0) | (fP ? 0x20 : 0) | (fB ? 0x10 : 0) | (fH ? 8 : 0) | (fI ? 4 : 0) | (fZ ? 2 : 0) | (fC ? 1 : 0);
        set { fN = (value & 0x80) != 0; fV = (value & 0x40) != 0; fP = (value & 0x20) != 0; fB = (value & 0x10) != 0; fH = (value & 8) != 0; fI = (value & 4) != 0; fZ = (value & 2) != 0; fC = (value & 1) != 0; }
    }

    private int dspAddr;
    private int control;
    private readonly int[] target = new int[3], stage1 = new int[3], stage2 = new int[3], output = new int[3];
    private static readonly int[] TimerPeriod = [128, 128, 16];

    public static Spc700 FromSpc(SpcFile f)
    {
        var s = new Spc700 { A = f.A, X = f.X, Y = f.Y, Sp = f.Sp, Pc = f.Pc, Psw = f.Psw };
        Array.Copy(f.Ram, s.Ram, 0x10000);
        Array.Copy(f.Dsp, s.Dsp, 0x80);
        // The snapshot's control and timer registers sit in RAM at $F1 and $FA-$FC: replay them.
        s.dspAddr = f.Ram[0xF2];
        s.control = 0; s.Write(0xF1, f.Ram[0xF1]);
        for (int i = 0; i < 3; i++) s.target[i] = f.Ram[0xFA + i];
        // The ports hold what the main CPU last wrote, which is what the dump recorded at $F4-$F7.
        for (int i = 0; i < 4; i++) s.FromCpu[i] = f.Ram[0xF4 + i];
        return s;
    }

    // ---- memory ----

    public byte Read(int a)
    {
        a &= 0xFFFF;
        if (a < 0xF0 || a > 0xFF) return Ram[a];
        switch (a)
        {
            case 0xF2: return (byte)dspAddr;
            case 0xF3: return Dsp[dspAddr & 0x7F];
            case >= 0xF4 and <= 0xF7: return FromCpu[a - 0xF4];
            case 0xFD or 0xFE or 0xFF: { int t = a - 0xFD; int v = output[t]; output[t] = 0; return (byte)v; }
            default: return Ram[a];
        }
    }

    public void Write(int a, int v)
    {
        a &= 0xFFFF; byte b = (byte)v;
        Ram[a] = b;                                                          // I/O writes land in RAM too, as on hardware
        if (a < 0xF0 || a > 0xFF) return;
        switch (a)
        {
            case 0xF1:
                for (int t = 0; t < 3; t++)
                    if ((b & (1 << t)) != 0 && (control & (1 << t)) == 0) { stage1[t] = 0; stage2[t] = 0; output[t] = 0; }
                if ((b & 0x10) != 0) { FromCpu[0] = FromCpu[1] = 0; }
                if ((b & 0x20) != 0) { FromCpu[2] = FromCpu[3] = 0; }
                control = b;
                break;
            case 0xF2: dspAddr = b; break;
            case 0xF3:
                if (dspAddr < 0x80)
                {
                    Dsp[dspAddr] = b;
                    DspWrites?.Add((Cycles, dspAddr, b));
                }
                break;
            case >= 0xF4 and <= 0xF7: ToCpu[a - 0xF4] = b; PortWrites?.Add((Cycles, a - 0xF4, b)); break;
            case >= 0xFA and <= 0xFC: target[a - 0xFA] = b; break;
        }
    }

    private void Tick(int cycles)
    {
        Cycles += cycles;
        for (int t = 0; t < 3; t++)
        {
            if ((control & (1 << t)) == 0) continue;
            stage1[t] += cycles;
            while (stage1[t] >= TimerPeriod[t])
            {
                stage1[t] -= TimerPeriod[t];
                if (++stage2[t] == (target[t] == 0 ? 256 : target[t])) { stage2[t] = 0; output[t] = (output[t] + 1) & 15; }
            }
        }
    }

    // ---- registers and flags ----

    private int Dp(int off) => (fP ? 0x100 : 0) + (off & 0xFF);
    private byte Fetch() => Read(Pc++ & 0xFFFF);
    private int Fetch16() { int lo = Fetch(); return lo | Fetch() << 8; }
    private void Push(int v) { Write(0x100 + Sp, v); Sp = (Sp - 1) & 0xFF; }
    private int Pull() { Sp = (Sp + 1) & 0xFF; return Read(0x100 + Sp); }
    private int Nz(int v) { v &= 0xFF; fN = (v & 0x80) != 0; fZ = v == 0; return v; }
    private int Ya { get => Y << 8 | A; set { A = value & 0xFF; Y = (value >> 8) & 0xFF; } }

    private int Adc(int a, int b)
    {
        int r = a + b + (fC ? 1 : 0);
        fH = ((a & 0xF) + (b & 0xF) + (fC ? 1 : 0)) > 0xF;
        fV = (~(a ^ b) & (a ^ r) & 0x80) != 0;
        fC = r > 0xFF;
        return Nz(r);
    }
    private int Sbc(int a, int b) => Adc(a, b ^ 0xFF);
    private int Cmp(int a, int b) { int r = a - b; fC = r >= 0; return Nz(r); }
    // kind: 0 OR, 1 AND, 2 EOR, 3 CMP (leaves a alone), 4 ADC, 5 SBC
    private int Alu(int op, int a, int b) => op switch
    {
        0 => Nz(a | b), 1 => Nz(a & b), 2 => Nz(a ^ b), 3 => CmpKeep(a, b), 4 => Adc(a, b), _ => Sbc(a, b),
    };
    private int CmpKeep(int a, int b) { Cmp(a, b); return a; }      // CMP leaves its destination alone

    // Addressing: the operand's address (or -1 for A-less forms). Each returns the effective address.
    private int AddrDp() => Dp(Fetch());
    private int AddrDpX() => Dp(Fetch() + X);
    private int AddrAbs() => Fetch16();
    private int AddrAbsX() => (Fetch16() + X) & 0xFFFF;
    private int AddrAbsY() => (Fetch16() + Y) & 0xFFFF;
    private int AddrIndX() { int d = Fetch() + X; return Read(Dp(d)) | Read(Dp(d + 1)) << 8; }
    private int AddrIndY() { int d = Fetch(); return ((Read(Dp(d)) | Read(Dp(d + 1)) << 8) + Y) & 0xFFFF; }

    private void Branch(bool take, ref int cyc)
    {
        sbyte r = (sbyte)Fetch();
        if (take) { Pc = (Pc + r) & 0xFFFF; cyc += 2; }
    }

    // Cycle counts, 8-bit, from the SPC700 documentation (branch penalties are added in Step).
    private static readonly byte[] Cyc = Table(
        "2 8 4 5 3 4 3 6 2 6 5 4 5 4 6 8 " + "2 8 4 5 4 5 5 6 5 5 6 5 2 2 4 6 " +
        "2 8 4 5 3 4 3 6 2 6 5 4 5 4 5 4 " + "2 8 4 5 4 5 5 6 5 5 6 5 2 2 3 8 " +
        "2 8 4 5 3 4 3 6 2 6 4 4 5 4 6 6 " + "2 8 4 5 4 5 5 6 5 5 4 5 2 2 4 3 " +
        "2 8 4 5 3 4 3 6 2 6 4 4 5 4 5 5 " + "2 8 4 5 4 5 5 6 5 5 5 5 2 2 3 6 " +
        "2 8 4 5 3 4 3 6 2 6 5 4 5 2 4 5 " + "2 8 4 5 4 5 5 6 5 5 5 5 2 2 12 5 " +
        "3 8 4 5 3 4 3 6 2 6 4 4 5 2 4 4 " + "2 8 4 5 4 5 5 6 5 5 5 5 2 2 3 4 " +
        "3 8 4 5 4 5 4 7 2 5 6 4 5 2 4 9 " + "2 8 4 5 5 6 6 7 4 5 5 5 2 2 6 3 " +
        "2 8 4 5 3 4 3 6 2 4 5 3 4 3 4 3 " + "2 8 4 5 4 5 5 6 3 4 5 4 2 2 4 3");

    private static byte[] Table(string s) => [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(byte.Parse)];

    public void Run(long cycles)
    {
        long stop = Cycles + cycles;
        while (!Halted && Cycles < stop) Step();
    }

    /// <summary>Run until <paramref name="until"/> says stop or <paramref name="maxCycles"/> pass.</summary>
    public void RunUntil(Func<Spc700, bool> until, long maxCycles)
    {
        long stop = Cycles + maxCycles;
        while (!Halted && Cycles < stop && !until(this)) Step();
    }

    public void Step()
    {
        int op = Fetch(), cyc = Cyc[op];
        int a, b, r, d;
        switch (op)
        {
            // ---- the eight ALU rows: OR AND EOR CMP ADC SBC, in the common addressing modes ----
            case 0x08 or 0x28 or 0x48 or 0x68 or 0x88 or 0xA8: A = AluOp(op, A, Fetch()); break;
            case 0x04 or 0x24 or 0x44 or 0x64 or 0x84 or 0xA4: A = AluOp(op, A, Read(AddrDp())); break;
            case 0x05 or 0x25 or 0x45 or 0x65 or 0x85 or 0xA5: A = AluOp(op, A, Read(AddrAbs())); break;
            case 0x06 or 0x26 or 0x46 or 0x66 or 0x86 or 0xA6: A = AluOp(op, A, Read(Dp(X))); break;
            case 0x07 or 0x27 or 0x47 or 0x67 or 0x87 or 0xA7: A = AluOp(op, A, Read(AddrIndX())); break;
            case 0x14 or 0x34 or 0x54 or 0x74 or 0x94 or 0xB4: A = AluOp(op, A, Read(AddrDpX())); break;
            case 0x15 or 0x35 or 0x55 or 0x75 or 0x95 or 0xB5: A = AluOp(op, A, Read(AddrAbsX())); break;
            case 0x16 or 0x36 or 0x56 or 0x76 or 0x96 or 0xB6: A = AluOp(op, A, Read(AddrAbsY())); break;
            case 0x17 or 0x37 or 0x57 or 0x77 or 0x97 or 0xB7: A = AluOp(op, A, Read(AddrIndY())); break;
            case 0x09 or 0x29 or 0x49 or 0x69 or 0x89 or 0xA9:           // op dd,ds
                { int s = Dp(Fetch()), t = Dp(Fetch()); RmwAlu(op, t, Read(s)); break; }
            case 0x19 or 0x39 or 0x59 or 0x79 or 0x99 or 0xB9:           // op (X),(Y)
                RmwAlu(op, Dp(X), Read(Dp(Y))); break;
            case 0x18 or 0x38 or 0x58 or 0x78 or 0x98 or 0xB8:           // op d,#i
                { int i = Fetch(); RmwAlu(op, Dp(Fetch()), i); break; }

            // ---- moves ----
            case 0xE8: A = Nz(Fetch()); break;
            case 0xCD: X = Nz(Fetch()); break;
            case 0x8D: Y = Nz(Fetch()); break;
            case 0xE4: A = Nz(Read(AddrDp())); break;
            case 0xE5: A = Nz(Read(AddrAbs())); break;
            case 0xE6: A = Nz(Read(Dp(X))); break;
            case 0xE7: A = Nz(Read(AddrIndX())); break;
            case 0xF4: A = Nz(Read(AddrDpX())); break;
            case 0xF5: A = Nz(Read(AddrAbsX())); break;
            case 0xF6: A = Nz(Read(AddrAbsY())); break;
            case 0xF7: A = Nz(Read(AddrIndY())); break;
            case 0xBF: A = Nz(Read(Dp(X))); X = (X + 1) & 0xFF; break;
            case 0xF8: X = Nz(Read(AddrDp())); break;
            case 0xE9: X = Nz(Read(AddrAbs())); break;
            case 0xF9: X = Nz(Read(Dp(Fetch() + Y))); break;
            case 0xEB: Y = Nz(Read(AddrDp())); break;
            case 0xEC: Y = Nz(Read(AddrAbs())); break;
            case 0xFB: Y = Nz(Read(AddrDpX())); break;
            case 0xC4: Write(AddrDp(), A); break;
            case 0xC5: Write(AddrAbs(), A); break;
            case 0xC6: Write(Dp(X), A); break;
            case 0xC7: Write(AddrIndX(), A); break;
            case 0xD4: Write(AddrDpX(), A); break;
            case 0xD5: Write(AddrAbsX(), A); break;
            case 0xD6: Write(AddrAbsY(), A); break;
            case 0xD7: Write(AddrIndY(), A); break;
            case 0xAF: Write(Dp(X), A); X = (X + 1) & 0xFF; break;
            case 0xD8: Write(AddrDp(), X); break;
            case 0xC9: Write(AddrAbs(), X); break;
            case 0xD9: Write(Dp(Fetch() + Y), X); break;
            case 0xCB: Write(AddrDp(), Y); break;
            case 0xCC: Write(AddrAbs(), Y); break;
            case 0xDB: Write(AddrDpX(), Y); break;
            case 0x8F: { int i = Fetch(); Write(Dp(Fetch()), i); break; }
            case 0xFA: { int s = Dp(Fetch()); Write(Dp(Fetch()), Read(s)); break; }
            case 0x7D: A = Nz(X); break;
            case 0xDD: A = Nz(Y); break;
            case 0x5D: X = Nz(A); break;
            case 0xFD: Y = Nz(A); break;
            case 0x9D: X = Nz(Sp); break;
            case 0xBD: Sp = X; break;
            case 0xBA: { int p = AddrDp(); Ya = Read(p) | Read(p & 0x100 | (p + 1) & 0xFF) << 8; Nz16(Ya); break; }
            case 0xDA: { int p = AddrDp(); Write(p, A); Write(p & 0x100 | (p + 1) & 0xFF, Y); break; }

            // ---- compares with X and Y ----
            case 0xC8: Cmp(X, Fetch()); break;
            case 0x3E: Cmp(X, Read(AddrDp())); break;
            case 0x1E: Cmp(X, Read(AddrAbs())); break;
            case 0xAD: Cmp(Y, Fetch()); break;
            case 0x7E: Cmp(Y, Read(AddrDp())); break;
            case 0x5E: Cmp(Y, Read(AddrAbs())); break;

            // ---- read-modify-write: ASL ROL LSR ROR DEC INC ----
            case 0x1C: A = Shift(0, A); break;
            case 0x3C: A = Shift(1, A); break;
            case 0x5C: A = Shift(2, A); break;
            case 0x7C: A = Shift(3, A); break;
            case 0x9C: A = Nz(A - 1); break;
            case 0xBC: A = Nz(A + 1); break;
            case 0x1D: X = Nz(X - 1); break;
            case 0x3D: X = Nz(X + 1); break;
            case 0xDC: Y = Nz(Y - 1); break;
            case 0xFC: Y = Nz(Y + 1); break;
            case 0x0B or 0x2B or 0x4B or 0x6B: Rmw(AddrDp(), (op >> 5) & 3); break;
            case 0x0C or 0x2C or 0x4C or 0x6C: Rmw(AddrAbs(), (op >> 5) & 3); break;
            case 0x1B or 0x3B or 0x5B or 0x7B: Rmw(AddrDpX(), (op >> 5) & 3); break;
            case 0x8B: RmwInc(AddrDp(), -1); break;
            case 0x8C: RmwInc(AddrAbs(), -1); break;
            case 0x9B: RmwInc(AddrDpX(), -1); break;
            case 0xAB: RmwInc(AddrDp(), 1); break;
            case 0xAC: RmwInc(AddrAbs(), 1); break;
            case 0xBB: RmwInc(AddrDpX(), 1); break;

            // ---- 16-bit ----
            case 0x3A or 0x1A:
                { int p = AddrDp(), q = p & 0x100 | (p + 1) & 0xFF; int v = (Read(p) | Read(q) << 8) + (op == 0x3A ? 1 : -1); v &= 0xFFFF; Write(p, v); Write(q, v >> 8); Nz16(v); break; }
            case 0x7A or 0x9A or 0x5A:
                {
                    int p = AddrDp(), q = p & 0x100 | (p + 1) & 0xFF; int m = Read(p) | Read(q) << 8, y = Ya;
                    if (op == 0x5A) { int c = y - m; fC = c >= 0; Nz16(c & 0xFFFF); break; }
                    int lo, hi;
                    if (op == 0x7A) { fC = false; lo = Adc(A, m & 0xFF); hi = Adc(Y, m >> 8); }
                    else { fC = true; lo = Sbc(A, m & 0xFF); hi = Sbc(Y, m >> 8); }
                    Ya = lo | hi << 8; fZ = Ya == 0;
                    break;
                }
            case 0xCF: { int v = Y * A; Ya = v; Nz(Y); break; }
            case 0x9E: Div(); break;
            case 0x9F: A = Nz((A << 4 | A >> 4) & 0xFF); break;
            case 0xDF:
                if (fC || A > 0x99) { A += 0x60; fC = true; }
                if (fH || (A & 15) > 9) A += 6;
                A = Nz(A); break;
            case 0xBE:
                if (!fC || A > 0x99) { A -= 0x60; fC = false; }
                if (!fH || (A & 15) > 9) A -= 6;
                A = Nz(A); break;

            // ---- bit operations ----
            case 0x0A: { var (ad, bit) = MemBit(); fC |= (Read(ad) >> bit & 1) != 0; break; }
            case 0x2A: { var (ad, bit) = MemBit(); fC |= (Read(ad) >> bit & 1) == 0; break; }
            case 0x4A: { var (ad, bit) = MemBit(); fC &= (Read(ad) >> bit & 1) != 0; break; }
            case 0x6A: { var (ad, bit) = MemBit(); fC &= (Read(ad) >> bit & 1) == 0; break; }
            case 0x8A: { var (ad, bit) = MemBit(); fC ^= (Read(ad) >> bit & 1) != 0; break; }
            case 0xAA: { var (ad, bit) = MemBit(); fC = (Read(ad) >> bit & 1) != 0; break; }
            case 0xCA: { var (ad, bit) = MemBit(); int m = Read(ad); Write(ad, fC ? m | 1 << bit : m & ~(1 << bit)); break; }
            case 0xEA: { var (ad, bit) = MemBit(); Write(ad, Read(ad) ^ 1 << bit); break; }
            case 0x0E or 0x4E:                                           // TSET1 / TCLR1 !a
                { int ad = AddrAbs(), m = Read(ad); Nz(A - m); Write(ad, op == 0x0E ? m | A : m & ~A); break; }
            case 0x02 or 0x22 or 0x42 or 0x62 or 0x82 or 0xA2 or 0xC2 or 0xE2:
                { int ad = AddrDp(); Write(ad, Read(ad) | 1 << (op >> 5)); break; }
            case 0x12 or 0x32 or 0x52 or 0x72 or 0x92 or 0xB2 or 0xD2 or 0xF2:
                { int ad = AddrDp(); Write(ad, Read(ad) & ~(1 << (op >> 5))); break; }
            case 0x03 or 0x23 or 0x43 or 0x63 or 0x83 or 0xA3 or 0xC3 or 0xE3:
                { int m = Read(AddrDp()); Branch((m >> (op >> 5) & 1) != 0, ref cyc); break; }
            case 0x13 or 0x33 or 0x53 or 0x73 or 0x93 or 0xB3 or 0xD3 or 0xF3:
                { int m = Read(AddrDp()); Branch((m >> (op >> 5) & 1) == 0, ref cyc); break; }

            // ---- branches and jumps ----
            case 0x10: Branch(!fN, ref cyc); break;
            case 0x30: Branch(fN, ref cyc); break;
            case 0x50: Branch(!fV, ref cyc); break;
            case 0x70: Branch(fV, ref cyc); break;
            case 0x90: Branch(!fC, ref cyc); break;
            case 0xB0: Branch(fC, ref cyc); break;
            case 0xD0: Branch(!fZ, ref cyc); break;
            case 0xF0: Branch(fZ, ref cyc); break;
            case 0x2F: Branch(true, ref cyc); cyc = 4; break;
            case 0x2E: { int m = Read(AddrDp()); Branch(A != m, ref cyc); break; }
            case 0xDE: { int m = Read(AddrDpX()); Branch(A != m, ref cyc); break; }
            case 0x6E: { int ad = AddrDp(); int m = (Read(ad) - 1) & 0xFF; Write(ad, m); Branch(m != 0, ref cyc); break; }
            case 0xFE: Y = (Y - 1) & 0xFF; Branch(Y != 0, ref cyc); break;
            case 0x5F: Pc = Fetch16(); break;
            case 0x1F: { int ad = (Fetch16() + X) & 0xFFFF; Pc = Read(ad) | Read(ad + 1) << 8; break; }
            case 0x3F: { int t = Fetch16(); Push(Pc >> 8); Push(Pc); Pc = t; break; }
            case 0x4F: { int t = 0xFF00 | Fetch(); Push(Pc >> 8); Push(Pc); Pc = t; break; }
            case 0x6F: { int lo = Pull(); Pc = lo | Pull() << 8; break; }
            case 0x7F: { Psw = Pull(); int lo = Pull(); Pc = lo | Pull() << 8; break; }
            case 0x0F: { Push(Pc >> 8); Push(Pc); Push(Psw); fB = true; fI = false; Pc = Read(0xFFDE) | Read(0xFFDF) << 8; break; }
            case 0x01 or 0x11 or 0x21 or 0x31 or 0x41 or 0x51 or 0x61 or 0x71 or 0x81 or 0x91 or 0xA1 or 0xB1 or 0xC1 or 0xD1 or 0xE1 or 0xF1:
                { Push(Pc >> 8); Push(Pc); int v = 0xFFDE - 2 * (op >> 4); Pc = Read(v) | Read(v + 1) << 8; break; }

            // ---- stack and flags ----
            case 0x2D: Push(A); break;
            case 0x4D: Push(X); break;
            case 0x6D: Push(Y); break;
            case 0x0D: Push(Psw); break;
            case 0xAE: A = Pull(); break;
            case 0xCE: X = Pull(); break;
            case 0xEE: Y = Pull(); break;
            case 0x8E: Psw = Pull(); break;
            case 0x20: fP = false; break;
            case 0x40: fP = true; break;
            case 0x60: fC = false; break;
            case 0x80: fC = true; break;
            case 0xED: fC = !fC; break;
            case 0xE0: fV = false; fH = false; break;
            case 0xA0: fI = true; break;
            case 0xC0: fI = false; break;
            case 0x00: break;
            case 0xEF or 0xFF: Halted = true; break;                      // SLEEP, STOP
            default: throw new NotSupportedException($"SPC700 opcode {op:X2} at {Pc - 1:X4}");
        }
        Pc &= 0xFFFF;
        Tick(cyc);
    }

    private int AluOp(int op, int a, int b) => Alu(op >> 5, a, b);
    private void RmwAlu(int op, int addr, int src)
    {
        int kind = op >> 5, dst = Read(addr);
        int r = Alu(kind, dst, src);
        if (kind != 3) Write(addr, r);                                      // CMP does not store
    }

    private int Shift(int kind, int v)
    {
        int r;
        switch (kind)
        {
            case 0: fC = (v & 0x80) != 0; r = v << 1; break;
            case 1: { int c = fC ? 1 : 0; fC = (v & 0x80) != 0; r = v << 1 | c; break; }
            case 2: fC = (v & 1) != 0; r = v >> 1; break;
            default: { int c = fC ? 0x80 : 0; fC = (v & 1) != 0; r = v >> 1 | c; break; }
        }
        return Nz(r);
    }
    private void Rmw(int addr, int kind) => Write(addr, Shift(kind, Read(addr)));
    private void RmwInc(int addr, int d) => Write(addr, Nz(Read(addr) + d));
    private void Nz16(int v) { fN = (v & 0x8000) != 0; fZ = (v & 0xFFFF) == 0; }

    private (int Addr, int Bit) MemBit() { int v = Fetch16(); return (v & 0x1FFF, v >> 13); }

    private void Div()
    {
        int ya = Ya;
        fH = (X & 15) <= (Y & 15);
        fV = Y >= X;
        if (Y < X << 1) { A = ya / X; Y = ya % X; }
        else { int t = ya - (X << 9); int dv = 256 - X; A = 255 - t / dv; Y = X + t % dv; }
        A &= 0xFF; Y &= 0xFF;
        Nz(A);
    }
}
