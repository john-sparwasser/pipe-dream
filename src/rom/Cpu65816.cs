namespace PipeDream;

/// <summary>
/// Minimal 65816 interpreter — just enough to execute the ROM's level-loading and
/// object-handler code (banks 00/05/0D + LM's expanded banks) against emulated RAM.
/// Native mode only, no decimal, no interrupts. Reads ROM via LoROM mapping; banks
/// $7E/$7F are RAM; banks $00-$3F/$80-$BF mirror $7E low RAM below $2000. Writes
/// outside RAM are ignored (PPU/DMA registers are irrelevant for tile expansion).
/// </summary>
public sealed class Cpu65816(Rom rom)
{
    public readonly byte[] Ram7E = new byte[0x10000];
    public readonly byte[] Ram7F = new byte[0x10000];

    private int A, X, Y, SP = 0x01FF, DP;
    private int DBR, PBR, PC;
    private bool NF, VF, ZF, CF, M = true, XF = true;   // start in 8-bit M/X (SEP #$30 state)

    public byte Read(int bank, int addr)
    {
        bank &= 0xFF; addr &= 0xFFFF;
        if (bank == 0x7E) return Ram7E[addr];
        if (bank == 0x7F) return Ram7F[addr];
        if (addr < 0x2000 && (bank < 0x40 || (bank >= 0x80 && bank < 0xC0))) return Ram7E[addr];
        if (addr >= 0x8000 || bank >= 0x40)
        {
            int fo = rom.FileOffset(((bank & 0x7F) << 16) | (addr | 0x8000));
            if ((uint)fo < (uint)rom.Data.Length && addr >= 0x8000) return rom.Data[fo];
        }
        return 0;
    }

    /// <summary>Debug: when set, logs (PC, addr, value) for writes to $0200-$04FF.</summary>
    public List<(int Pc, int Addr, byte V)>? OamLog;
    /// <summary>Debug: when set, logs (PC, value) for every write to this one RAM address.</summary>
    public int WatchAddr = -1;
    public List<(int Pc, byte V)>? WatchLog;
    /// <summary>Debug: when set, logs writes to the sprite status table $14C8-$14D3.</summary>
    public List<(int Pc, int Addr, byte V)>? StatusLog;
    /// <summary>Debug: when set, records (pc, X, Y) per step while PBR is bank $02 (or mirror),
    /// capped at 400 entries — ordered, unlike PcHot.</summary>
    public List<(int Pc, int X, int Y)>? StepLog;

    /// <summary>Debug: when set, every byte written to the VRAM data port ($2118/$2119), in
    /// order. PPU writes are otherwise dropped, which leaves an upload routine testable only
    /// by its source buffer — this is what lets a test check the bytes it PRODUCED. A 16-bit
    /// store lands here as low byte then high byte, i.e. VRAM word order.</summary>
    public List<byte>? VramLog;

    /// <summary>
    /// Object attribution (set all three to enable): every RAM write also records which
    /// level-data record the loader is processing. The loader advances the stream pointer
    /// $65 past the 3-byte record BEFORE dispatching its handler (bank 05 CODE_058612),
    /// so the current record is StreamOwner[$65 - 3]. Ids are caller-defined, 0 = none.
    /// Only meaningful for streams injected in bank $7F (checked via $67).
    /// </summary>
    public ushort[]? StreamOwner;              // encoded-stream byte offset → record id
    public ushort[]? Owner7E, Owner7F;         // per-address last-writer record id
    /// <summary>When set (with the above), also logs every attributed tilemap-region write
    /// in order — the full writer history per cell, for overlap/z-order queries.</summary>
    public List<(int Bank, int Addr, ushort Id)>? WriteLog;

    private void Write(int bank, int addr, byte v)
    {
        bank &= 0xFF; addr &= 0xFFFF;
        if (WatchLog is not null && addr == WatchAddr && (bank == 0x7E || bank < 0x40 || (bank >= 0x80 && bank < 0xC0)))
            WatchLog.Add(((PBR << 16) | PC, v));
        if (OamLog is not null && addr is >= 0x200 and < 0x500 &&
            (bank == 0x7E || bank < 0x40 || (bank >= 0x80 && bank < 0xC0)))
            OamLog.Add(((PBR << 16) | PC, addr, v));
        if (StatusLog is not null && addr is >= 0x14C8 and < 0x15C8 &&
            (bank == 0x7E || bank < 0x40 || (bank >= 0x80 && bank < 0xC0)))
            StatusLog.Add(((PBR << 16) | PC, addr, v));
        // Hardware registers live in banks $00-$3F/$80-$BF only — NOT $7E/$7F, where $2118 is
        // ordinary work RAM and logging it would invent uploads that never happened.
        if (VramLog is not null && addr is 0x2118 or 0x2119
            && (bank < 0x40 || (bank >= 0x80 && bank < 0xC0)))
            VramLog.Add(v);
        if (StreamOwner is not null && Ram7E[0x67] == 0x7F)
        {
            int p = ((Ram7E[0x66] << 8) | Ram7E[0x65]) - 3;
            ushort id = (uint)p < (uint)StreamOwner.Length ? StreamOwner[p] : (ushort)0;
            if (bank == 0x7E) Owner7E![addr] = id;
            else if (bank == 0x7F) Owner7F![addr] = id;
            else if (addr < 0x2000 && (bank < 0x40 || (bank >= 0x80 && bank < 0xC0))) Owner7E![addr] = id;
            if (WriteLog is not null && id != 0 && (bank == 0x7F || (bank == 0x7E && addr >= 0xC800)))
                WriteLog.Add((bank, addr, id));
        }
        if (bank == 0x7E) Ram7E[addr] = v;
        else if (bank == 0x7F) Ram7F[addr] = v;
        else if (addr < 0x2000 && (bank < 0x40 || (bank >= 0x80 && bank < 0xC0))) Ram7E[addr] = v;
        // everything else (PPU/DMA/etc.) ignored
    }

    private int Read16(int bank, int addr) => Read(bank, addr) | (Read(bank, addr + 1) << 8);
    private int Read24(int bank, int addr) => Read16(bank, addr) | (Read(bank, addr + 2) << 16);
    private void Write16(int bank, int addr, int v)
    { Write(bank, addr, (byte)v); Write(bank, addr + 1, (byte)(v >> 8)); }

    private byte Fetch() { byte b = Read(PBR, PC); PC = (PC + 1) & 0xFFFF; return b; }
    private int Fetch16() { int v = Fetch(); return v | (Fetch() << 8); }

    private void Push(byte v) { Write(0, SP, v); SP = (SP - 1) & 0xFFFF; }
    private byte Pull() { SP = (SP + 1) & 0xFFFF; return Read(0, SP); }
    private void Push16(int v) { Push((byte)(v >> 8)); Push((byte)v); }
    private int Pull16() { int lo = Pull(); return lo | (Pull() << 8); }

    private int P
    {
        get => (CF ? 1 : 0) | (ZF ? 2 : 0) | (XF ? 0x10 : 0) | (M ? 0x20 : 0) | (VF ? 0x40 : 0) | (NF ? 0x80 : 0);
        set { CF = (value & 1) != 0; ZF = (value & 2) != 0; XF = (value & 0x10) != 0; M = (value & 0x20) != 0;
              VF = (value & 0x40) != 0; NF = (value & 0x80) != 0; if (XF) { X &= 0xFF; Y &= 0xFF; } }
    }

    private int MMask => M ? 0xFF : 0xFFFF;
    private int XMask => XF ? 0xFF : 0xFFFF;
    private void SetNZ(int v, int mask) { ZF = (v & mask) == 0; NF = (v & (mask == 0xFF ? 0x80 : 0x8000)) != 0; }

    // Effective address of the current operand as (bank, addr). Immediate handled separately.
    private (int bank, int addr) Ea(int mode)
    {
        switch (mode)
        {
            case 0: { int d = Fetch(); return (0, (DP + d) & 0xFFFF); }                          // dp
            case 1: { int d = Fetch(); return (0, (DP + d + X) & 0xFFFF); }                      // dp,X
            case 2: { int d = Fetch(); return (0, (DP + d + Y) & 0xFFFF); }                      // dp,Y
            case 3: return (DBR, Fetch16());                                                     // abs
            case 4: { int a = Fetch16() + X; return (DBR + (a >> 16), a & 0xFFFF); }             // abs,X
            case 5: { int a = Fetch16() + Y; return (DBR + (a >> 16), a & 0xFFFF); }             // abs,Y
            case 6: { int a = Fetch16(); return (Fetch(), a); }                                  // long
            case 7: { int a = Fetch16(); int b = Fetch(); int t = a + X; return (b + (t >> 16), t & 0xFFFF); } // long,X
            case 8: { int d = Fetch(); int p = (DP + d) & 0xFFFF; return (DBR, Read16(0, p)); }  // (dp)
            case 9: { int d = Fetch(); int p = (DP + d) & 0xFFFF; int t = Read16(0, p) + Y; return (DBR + (t >> 16), t & 0xFFFF); } // (dp),Y
            case 10: { int d = Fetch(); int p = (DP + d) & 0xFFFF; return (Read(0, p + 2), Read16(0, p)); }  // [dp]
            case 11: { int d = Fetch(); int p = (DP + d) & 0xFFFF; int t = Read16(0, p) + Y; return ((Read(0, p + 2) + (t >> 16)) & 0xFF, t & 0xFFFF); } // [dp],Y
            case 12: { int d = Fetch(); int p = (DP + d + X) & 0xFFFF; return (DBR, Read16(0, p)); } // (dp,X)
            case 13: { int d = Fetch(); return (0, (SP + d) & 0xFFFF); }                          // sr,S
            case 14: { int d = Fetch(); int p = (SP + d) & 0xFFFF; int t = Read16(0, p) + Y; return (DBR + (t >> 16), t & 0xFFFF); } // (sr,S),Y
            default: throw new InvalidOperationException();
        }
    }

    private int Load(int mode, int mask)
    {
        var (b, a) = Ea(mode);
        return (mask == 0xFF ? Read(b, a) : Read16(b, a)) & mask;
    }

    /// <summary>Preset the X register (e.g. the sprite slot index) before a call.</summary>
    public void PresetX(int v) => X = v & 0xFFFF;
    public void PresetY(int v) => Y = v & 0xFFFF;

    /// <summary>Preset the register widths a call is entered with. Hijacks placed mid-routine
    /// inherit the host code's REP/SEP state rather than a fresh one — the Map16 def lookup,
    /// for one, is entered with 16-bit index registers already selected and Y = tile*2.</summary>
    public void PresetWidths(bool m8, bool x8)
    { M = m8; XF = x8; if (x8) { X &= 0xFF; Y &= 0xFF; } }

    /// <summary>Preset A/X/Y for a routine that is entered with values in them (a hijack that
    /// receives its argument in A, an index already in Y).</summary>
    public void PresetRegs(int a, int x, int y) { A = a & 0xFFFF; X = x & 0xFFFF; Y = y & 0xFFFF; }

    /// <summary>The accumulator after a call, 16-bit view — for hijacks that return a value.</summary>
    public int Acc => A;

    /// <summary>Preset the data bank register. Bank-1 sprite code runs with DBR=1 in-game
    /// (PHK/PLB in the sprite loop); absolute table reads (SprTilemap etc.) depend on it.</summary>
    public void PresetDbr(int b) => DBR = b & 0xFF;

    /// <summary>Run a JSR to <paramref name="entrySnes"/> until its top-level RTS. Throws on overrun.</summary>
    public void CallNear(int entrySnes, int maxInstructions = 30_000_000)
    {
        int bank = (entrySnes >> 16) & 0xFF;
        PBR = bank; PC = entrySnes & 0xFFFF;
        Push16(0xFFFE);                                     // fake JSR frame: RTS lands on $FFFF
        long budget = maxInstructions;
        while (--budget > 0)
        {
            // Mirror-aware: hijack chains JML into $80+ mirrors (e.g. $82A82E), so the
            // top-level RTS can arrive with PBR = entry bank | $80.
            if (PC == 0xFFFF && (PBR & 0x7F) == (bank & 0x7F)) return;
            Step();
        }
        throw new InvalidOperationException("emulation overran instruction budget");
    }

    /// <summary>Run a JSL to <paramref name="entrySnes"/> until its top-level RTL. Throws on overrun.</summary>
    public void CallLong(int entrySnes, int maxInstructions = 30_000_000)
    {
        PBR = (entrySnes >> 16) & 0xFF; PC = entrySnes & 0xFFFF;
        Push(0xFF);                                         // fake JSL frame: RTL restores PBR=$FF
        Push16(0xFFFE);                                     // and lands on $FFFF
        long budget = maxInstructions;
        while (--budget > 0)
        {
            if (PC == 0xFFFF && PBR == 0xFF) return;
            Step();
        }
        throw new InvalidOperationException("emulation overran instruction budget");
    }

    /// <summary>Debug: when set, records every program-bank the emulation executes in.</summary>
    public HashSet<int>? BankTrace;
    /// <summary>Debug: when set, records instruction addresses executed outside the vanilla
    /// banks ($00-$02/$05/$07 and mirrors) — i.e. inside LM/tool-inserted code.</summary>
    public SortedSet<int>? PcHot;

    /// <summary>Debug: the last 64 instruction addresses executed, oldest first.</summary>
    public IEnumerable<int> RecentPcs => Enumerable.Range(0, 64).Select(i => ring[(ringPos + i) & 63]).Where(p => p != 0);
    private readonly int[] ring = new int[64];
    private int ringPos;

    private void Step()
    {
        ring[ringPos++ & 63] = (PBR << 16) | PC;
        BankTrace?.Add(PBR);
        if (PcHot is not null && ((PBR & 0x7F) is not (0x00 or 0x01 or 0x05 or 0x07)
                                  && !((PBR & 0x7F) == 0x02 && PC < 0xA7FC)))
            PcHot.Add((PBR << 16) | PC);
        if (StepLog is not null && (PBR & 0x7F) is 0x01 or 0x02 && StepLog.Count < 400)
            StepLog.Add(((PBR << 16) | PC, X, Y));
        byte op = Fetch();
        int m = MMask, xm = XMask, v;
        switch (op)
        {
            // ---- loads/stores ----
            case 0xA9: A = (A & ~m) | (m == 0xFF ? Fetch() : Fetch16()); SetNZ(A, m); break;
            case 0xA5: v = Load(0, m); goto lda; case 0xB5: v = Load(1, m); goto lda;
            case 0xAD: v = Load(3, m); goto lda; case 0xBD: v = Load(4, m); goto lda;
            case 0xB9: v = Load(5, m); goto lda; case 0xAF: v = Load(6, m); goto lda;
            case 0xBF: v = Load(7, m); goto lda; case 0xB2: v = Load(8, m); goto lda;
            case 0xB1: v = Load(9, m); goto lda; case 0xA7: v = Load(10, m); goto lda;
            case 0xB7: v = Load(11, m); goto lda; case 0xA1: v = Load(12, m); goto lda;
            case 0xA3: v = Load(13, m); goto lda; case 0xB3: v = Load(14, m); goto lda;
            lda: A = (A & ~m) | v; SetNZ(A, m); break;

            case 0xA2: X = (xm == 0xFF ? Fetch() : Fetch16()) & xm; SetNZ(X, xm); break;
            case 0xA6: X = Load(0, xm); SetNZ(X, xm); break; case 0xB6: X = Load(2, xm); SetNZ(X, xm); break;
            case 0xAE: X = Load(3, xm); SetNZ(X, xm); break; case 0xBE: X = Load(5, xm); SetNZ(X, xm); break;
            case 0xA0: Y = (xm == 0xFF ? Fetch() : Fetch16()) & xm; SetNZ(Y, xm); break;
            case 0xA4: Y = Load(0, xm); SetNZ(Y, xm); break; case 0xB4: Y = Load(1, xm); SetNZ(Y, xm); break;
            case 0xAC: Y = Load(3, xm); SetNZ(Y, xm); break; case 0xBC: Y = Load(4, xm); SetNZ(Y, xm); break;

            case 0x85: Store(0, A, m); break; case 0x95: Store(1, A, m); break;
            case 0x8D: Store(3, A, m); break; case 0x9D: Store(4, A, m); break;
            case 0x99: Store(5, A, m); break; case 0x8F: Store(6, A, m); break;
            case 0x9F: Store(7, A, m); break; case 0x92: Store(8, A, m); break;
            case 0x91: Store(9, A, m); break; case 0x87: Store(10, A, m); break;
            case 0x97: Store(11, A, m); break; case 0x81: Store(12, A, m); break;
            case 0x83: Store(13, A, m); break; case 0x93: Store(14, A, m); break;
            case 0x86: Store(0, X, xm); break; case 0x96: Store(2, X, xm); break; case 0x8E: Store(3, X, xm); break;
            case 0x84: Store(0, Y, xm); break; case 0x94: Store(1, Y, xm); break; case 0x8C: Store(3, Y, xm); break;
            case 0x64: Store(0, 0, m); break; case 0x74: Store(1, 0, m); break;
            case 0x9C: Store(3, 0, m); break; case 0x9E: Store(4, 0, m); break;

            // ---- arithmetic/logic ----
            case 0x69: Adc(m == 0xFF ? Fetch() : Fetch16(), m); break;
            case 0x65: Adc(Load(0, m), m); break; case 0x75: Adc(Load(1, m), m); break;
            case 0x6D: Adc(Load(3, m), m); break; case 0x7D: Adc(Load(4, m), m); break;
            case 0x79: Adc(Load(5, m), m); break; case 0x6F: Adc(Load(6, m), m); break;
            case 0x7F: Adc(Load(7, m), m); break; case 0x71: Adc(Load(9, m), m); break;
            case 0x67: Adc(Load(10, m), m); break; case 0x77: Adc(Load(11, m), m); break;
            case 0x72: Adc(Load(8, m), m); break;
            case 0xE9: Sbc(m == 0xFF ? Fetch() : Fetch16(), m); break;
            case 0xE5: Sbc(Load(0, m), m); break; case 0xF5: Sbc(Load(1, m), m); break;
            case 0xED: Sbc(Load(3, m), m); break; case 0xFD: Sbc(Load(4, m), m); break;
            case 0xF9: Sbc(Load(5, m), m); break; case 0xEF: Sbc(Load(6, m), m); break;
            case 0xFF: Sbc(Load(7, m), m); break; case 0xF1: Sbc(Load(9, m), m); break;
            case 0xE7: Sbc(Load(10, m), m); break; case 0xF7: Sbc(Load(11, m), m); break;
            case 0x29: A = (A & ~m) | ((A & m) & (m == 0xFF ? Fetch() : Fetch16())); SetNZ(A, m); break;
            case 0x25: AndM(Load(0, m), m); break; case 0x35: AndM(Load(1, m), m); break;
            case 0x2D: AndM(Load(3, m), m); break; case 0x3D: AndM(Load(4, m), m); break;
            case 0x39: AndM(Load(5, m), m); break; case 0x2F: AndM(Load(6, m), m); break;
            case 0x3F: AndM(Load(7, m), m); break; case 0x31: AndM(Load(9, m), m); break;
            case 0x27: AndM(Load(10, m), m); break; case 0x37: AndM(Load(11, m), m); break;
            case 0x09: A = (A & ~m) | ((A & m) | (m == 0xFF ? Fetch() : Fetch16())); SetNZ(A, m); break;
            case 0x05: OraM(Load(0, m), m); break; case 0x15: OraM(Load(1, m), m); break; case 0x03: OraM(Load(13, m), m); break;
            case 0x0D: OraM(Load(3, m), m); break; case 0x1D: OraM(Load(4, m), m); break;
            case 0x19: OraM(Load(5, m), m); break; case 0x0F: OraM(Load(6, m), m); break;
            case 0x1F: OraM(Load(7, m), m); break; case 0x11: OraM(Load(9, m), m); break;
            case 0x07: OraM(Load(10, m), m); break; case 0x17: OraM(Load(11, m), m); break;
            case 0x49: A = (A & ~m) | ((A & m) ^ (m == 0xFF ? Fetch() : Fetch16())); SetNZ(A, m); break;
            case 0x45: EorM(Load(0, m), m); break; case 0x4D: EorM(Load(3, m), m); break;
            case 0x5D: EorM(Load(4, m), m); break; case 0x59: EorM(Load(5, m), m); break;
            case 0x4F: EorM(Load(6, m), m); break; case 0x51: EorM(Load(9, m), m); break;

            case 0xC9: Cmp(A & m, m == 0xFF ? Fetch() : Fetch16(), m); break;
            case 0xC5: Cmp(A & m, Load(0, m), m); break; case 0xD5: Cmp(A & m, Load(1, m), m); break;
            case 0xCD: Cmp(A & m, Load(3, m), m); break; case 0xDD: Cmp(A & m, Load(4, m), m); break;
            case 0xD9: Cmp(A & m, Load(5, m), m); break; case 0xCF: Cmp(A & m, Load(6, m), m); break;
            case 0xDF: Cmp(A & m, Load(7, m), m); break; case 0xD1: Cmp(A & m, Load(9, m), m); break;
            case 0xC7: Cmp(A & m, Load(10, m), m); break; case 0xD7: Cmp(A & m, Load(11, m), m); break;
            case 0xE0: Cmp(X, xm == 0xFF ? Fetch() : Fetch16(), xm); break;
            case 0xE4: Cmp(X, Load(0, xm), xm); break; case 0xEC: Cmp(X, Load(3, xm), xm); break;
            case 0xC0: Cmp(Y, xm == 0xFF ? Fetch() : Fetch16(), xm); break;
            case 0xC4: Cmp(Y, Load(0, xm), xm); break; case 0xCC: Cmp(Y, Load(3, xm), xm); break;
            case 0x89: { int imm = m == 0xFF ? Fetch() : Fetch16(); ZF = ((A & m) & imm) == 0; break; }  // BIT #
            case 0x24: Bit(Load(0, m), m); break; case 0x2C: Bit(Load(3, m), m); break;
            case 0x34: Bit(Load(1, m), m); break; case 0x3C: Bit(Load(4, m), m); break;

            // ---- read-modify-write ----
            case 0x0A: A = (A & ~m) | Asl(A & m, m); break;
            case 0x06: Rmw(0, x2 => Asl(x2, m)); break; case 0x16: Rmw(1, x2 => Asl(x2, m)); break;
            case 0x0E: Rmw(3, x2 => Asl(x2, m)); break; case 0x1E: Rmw(4, x2 => Asl(x2, m)); break;
            case 0x4A: A = (A & ~m) | Lsr(A & m, m); break;
            case 0x46: Rmw(0, x2 => Lsr(x2, m)); break; case 0x56: Rmw(1, x2 => Lsr(x2, m)); break;
            case 0x4E: Rmw(3, x2 => Lsr(x2, m)); break; case 0x5E: Rmw(4, x2 => Lsr(x2, m)); break;
            case 0x2A: A = (A & ~m) | Rol(A & m, m); break;
            case 0x26: Rmw(0, x2 => Rol(x2, m)); break; case 0x2E: Rmw(3, x2 => Rol(x2, m)); break;
            case 0x36: Rmw(1, x2 => Rol(x2, m)); break; case 0x3E: Rmw(4, x2 => Rol(x2, m)); break;
            case 0x6A: A = (A & ~m) | Ror(A & m, m); break;
            case 0x66: Rmw(0, x2 => Ror(x2, m)); break; case 0x6E: Rmw(3, x2 => Ror(x2, m)); break;
            case 0x76: Rmw(1, x2 => Ror(x2, m)); break; case 0x7E: Rmw(4, x2 => Ror(x2, m)); break;
            case 0x1A: A = (A & ~m) | ((A + 1) & m); SetNZ(A, m); break;
            case 0x3A: A = (A & ~m) | ((A - 1) & m); SetNZ(A, m); break;
            case 0xE6: Rmw(0, x2 => { x2 = (x2 + 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xEE: Rmw(3, x2 => { x2 = (x2 + 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xF6: Rmw(1, x2 => { x2 = (x2 + 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xFE: Rmw(4, x2 => { x2 = (x2 + 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xC6: Rmw(0, x2 => { x2 = (x2 - 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xCE: Rmw(3, x2 => { x2 = (x2 - 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xD6: Rmw(1, x2 => { x2 = (x2 - 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xDE: Rmw(4, x2 => { x2 = (x2 - 1) & m; SetNZ(x2, m); return x2; }); break;
            case 0xE8: X = (X + 1) & xm; SetNZ(X, xm); break; case 0xCA: X = (X - 1) & xm; SetNZ(X, xm); break;
            case 0xC8: Y = (Y + 1) & xm; SetNZ(Y, xm); break; case 0x88: Y = (Y - 1) & xm; SetNZ(Y, xm); break;
            case 0x04: Rmw(0, x2 => { ZF = ((A & m) & x2) == 0; return x2 | (A & m); }); break;   // TSB
            case 0x0C: Rmw(3, x2 => { ZF = ((A & m) & x2) == 0; return x2 | (A & m); }); break;
            case 0x14: Rmw(0, x2 => { ZF = ((A & m) & x2) == 0; return x2 & ~(A & m); }); break;  // TRB
            case 0x1C: Rmw(3, x2 => { ZF = ((A & m) & x2) == 0; return x2 & ~(A & m); }); break;

            // ---- transfers ----
            case 0xAA: X = A & xm; SetNZ(X, xm); break; case 0x8A: A = (A & ~m) | (X & m); SetNZ(A, m); break;
            case 0xA8: Y = A & xm; SetNZ(Y, xm); break; case 0x98: A = (A & ~m) | (Y & m); SetNZ(A, m); break;
            case 0x9B: Y = X & xm; SetNZ(Y, xm); break; case 0xBB: X = Y & xm; SetNZ(X, xm); break;
            case 0xEB: A = ((A & 0xFF) << 8) | ((A >> 8) & 0xFF); SetNZ(A, 0xFF); break;   // XBA
            case 0x5B: DP = A & 0xFFFF; break; case 0x7B: A = DP; SetNZ(A, 0xFFFF); break;
            case 0x1B: SP = XF ? (0x0100 | (A & 0xFF)) : (A & 0xFFFF); break;
            case 0x3B: A = SP; SetNZ(A, 0xFFFF); break;
            case 0x9A: SP = XF ? (0x0100 | (X & 0xFF)) : X; break; case 0xBA: X = SP & xm; SetNZ(X, xm); break;

            // ---- stack ----
            case 0x48: if (M) Push((byte)A); else Push16(A); break;
            case 0x68: if (M) { A = (A & ~0xFF) | Pull(); SetNZ(A, 0xFF); } else { A = Pull16(); SetNZ(A, 0xFFFF); } break;
            case 0xDA: if (XF) Push((byte)X); else Push16(X); break;
            case 0xFA: X = XF ? Pull() : Pull16(); SetNZ(X, xm); break;
            case 0x5A: if (XF) Push((byte)Y); else Push16(Y); break;
            case 0x7A: Y = XF ? Pull() : Pull16(); SetNZ(Y, xm); break;
            case 0x08: Push((byte)P); break; case 0x28: P = Pull(); break;
            case 0x8B: Push((byte)DBR); break; case 0xAB: DBR = Pull(); SetNZ(DBR, 0xFF); break;
            case 0x4B: Push((byte)PBR); break;
            case 0x0B: Push16(DP); break; case 0x2B: DP = Pull16(); SetNZ(DP, 0xFFFF); break;
            case 0xF4: Push16(Fetch16()); break;                                              // PEA
            case 0xD4: { int d = Fetch(); Push16(Read16(0, (DP + d) & 0xFFFF)); } break;      // PEI
            case 0x62: { int d = Fetch16(); Push16((PC + d) & 0xFFFF); } break;               // PER

            // ---- flow ----
            case 0x4C: PC = Fetch16(); break;
            case 0x5C: { int a = Fetch16(); PBR = Fetch(); PC = a; } break;
            case 0x6C: { int a = Fetch16(); PC = Read16(0, a); } break;
            case 0x7C: { int a = (Fetch16() + X) & 0xFFFF; PC = Read16(PBR, a); } break;
            case 0xDC: { int a = Fetch16(); PC = Read16(0, a); PBR = Read(0, a + 2); } break;
            case 0x20: { int a = Fetch16(); Push16((PC - 1) & 0xFFFF); PC = a; } break;
            case 0xFC: { int a = (Fetch16() + X) & 0xFFFF; Push16((PC - 1) & 0xFFFF); PC = Read16(PBR, a); } break;
            case 0x22: { int a = Fetch16(); int b = Fetch(); Push((byte)PBR); Push16((PC - 1) & 0xFFFF); PBR = b; PC = a; } break;
            case 0x60: PC = (Pull16() + 1) & 0xFFFF; break;
            case 0x6B: { int a = Pull16(); PBR = Pull(); PC = (a + 1) & 0xFFFF; } break;
            case 0x40: P = Pull(); PC = Pull16(); PBR = Pull(); break;                         // RTI (native)
            case 0x80: Branch(true); break;
            case 0x82: { int d = Fetch16(); PC = (PC + (short)d) & 0xFFFF; } break;            // BRL
            case 0x10: Branch(!NF); break; case 0x30: Branch(NF); break;
            case 0x50: Branch(!VF); break; case 0x70: Branch(VF); break;
            case 0x90: Branch(!CF); break; case 0xB0: Branch(CF); break;
            case 0xD0: Branch(!ZF); break; case 0xF0: Branch(ZF); break;

            // ---- flags ----
            case 0x18: CF = false; break; case 0x38: CF = true; break;
            case 0x58: case 0x78: case 0xD8: case 0xF8: case 0xEA: break;                      // CLI/SEI/CLD/SED/NOP
            case 0xB8: VF = false; break;
            case 0xC2: P &= ~Fetch(); break; case 0xE2: P |= Fetch(); break;
            case 0xFB: break;                                                                  // XCE (stay native)

            case 0x54: case 0x44:                                                              // MVN/MVP
            {
                int dst = Fetch(), src = Fetch();
                DBR = dst;
                int count = (A & 0xFFFF) + 1;
                int step = op == 0x54 ? 1 : -1;
                while (count-- > 0)
                {
                    Write(dst, Y & 0xFFFF, Read(src, X & 0xFFFF));
                    X = (X + step) & 0xFFFF; Y = (Y + step) & 0xFFFF;
                }
                A = 0xFFFF;
                break;
            }

            default:
                throw new NotSupportedException($"opcode {op:X2} at {PBR:X2}:{(PC - 1) & 0xFFFF:X4}");
        }
    }

    private void Store(int mode, int val, int mask)
    {
        var (b, a) = Ea(mode);
        if (mask == 0xFF) Write(b, a, (byte)val); else Write16(b, a, val);
    }

    private void Rmw(int mode, Func<int, int> f)
    {
        var (b, a) = Ea(mode);
        if (M) Write(b, a, (byte)f(Read(b, a)));
        else Write16(b, a, f(Read16(b, a)));
    }

    private void Branch(bool take) { sbyte d = (sbyte)Fetch(); if (take) PC = (PC + d) & 0xFFFF; }

    private void Adc(int operand, int mask)
    {
        int a = A & mask, r = a + operand + (CF ? 1 : 0);
        int sign = mask == 0xFF ? 0x80 : 0x8000;
        VF = (~(a ^ operand) & (a ^ r) & sign) != 0;
        CF = r > mask;
        A = (A & ~mask) | (r & mask); SetNZ(A, mask);
    }

    private void Sbc(int operand, int mask) => Adc(~operand & mask, mask);

    private void Cmp(int reg, int operand, int mask)
    { int r = (reg & mask) - operand; CF = r >= 0; SetNZ(r & mask, mask); }

    private void AndM(int v2, int mask) { A = (A & ~mask) | ((A & mask) & v2); SetNZ(A, mask); }
    private void OraM(int v2, int mask) { A = (A & ~mask) | ((A & mask) | v2); SetNZ(A, mask); }
    private void EorM(int v2, int mask) { A = (A & ~mask) | ((A & mask) ^ v2); SetNZ(A, mask); }
    private void Bit(int v2, int mask)
    {
        ZF = ((A & mask) & v2) == 0;
        NF = (v2 & (mask == 0xFF ? 0x80 : 0x8000)) != 0;
        VF = (v2 & (mask == 0xFF ? 0x40 : 0x4000)) != 0;
    }

    private int Asl(int v2, int mask) { CF = (v2 & (mask == 0xFF ? 0x80 : 0x8000)) != 0; v2 = (v2 << 1) & mask; SetNZ(v2, mask); return v2; }
    private int Lsr(int v2, int mask) { CF = (v2 & 1) != 0; v2 = (v2 >> 1) & mask; SetNZ(v2, mask); return v2; }
    private int Rol(int v2, int mask) { bool c = CF; CF = (v2 & (mask == 0xFF ? 0x80 : 0x8000)) != 0; v2 = ((v2 << 1) | (c ? 1 : 0)) & mask; SetNZ(v2, mask); return v2; }
    private int Ror(int v2, int mask) { bool c = CF; CF = (v2 & 1) != 0; v2 = ((v2 >> 1) | (c ? (mask == 0xFF ? 0x80 : 0x8000) : 0)) & mask; SetNZ(v2, mask); return v2; }
}
