namespace PipeDream;

/// <summary>
/// Tiny 65816 emitter for RomPrep's inserted code. Named methods for the ~40 opcodes the
/// prep stamps use, string labels with rel8/abs16 fixups, and fixed-address assertions
/// (several instruction addresses are pinned by the LunarMagic.cs scanner contracts).
/// Produces the frozen byte arrays RomPrep stamps — clean-room authored from the documented
/// semantics (CONTRACT §7/§9d/§11, LEVEL_PIPELINE_NOTES §E/§F), never transcribed.
/// </summary>
public sealed class Asm(int orgSnes)
{
    private readonly List<byte> code = new();
    private readonly Dictionary<string, int> labels = new();               // name → snes addr
    private readonly List<(int Off, string Label, bool Rel)> fix = new();  // rel8 / abs16 fixups

    public int Org => orgSnes;
    /// <summary>SNES address of the next emitted byte.</summary>
    public int Pc => orgSnes + code.Count;

    /// <summary>Resolve fixups and return the stamp bytes.</summary>
    public byte[] Bytes()
    {
        foreach (var (off, label, rel) in fix)
        {
            if (!labels.TryGetValue(label, out int t))
                throw new InvalidOperationException($"undefined label '{label}'");
            if (rel)
            {
                int d = t - (orgSnes + off + 1);
                if (d is < -128 or > 127) throw new InvalidOperationException($"branch to '{label}' out of range ({d})");
                code[off] = (byte)d;
            }
            else { code[off] = (byte)t; code[off + 1] = (byte)(t >> 8); }
        }
        return code.ToArray();
    }

    private Asm E(params byte[] b) { code.AddRange(b); return this; }
    private Asm Imm16(byte op, int v) => E(op, (byte)v, (byte)(v >> 8));

    /// <summary>Fail if the next byte would not land at <paramref name="snes"/> — the scanner
    /// contracts pin instruction addresses, and drift must break the build, not the ROM.</summary>
    public Asm AssertAt(int snes)
        => Pc == snes ? this : throw new InvalidOperationException($"expected ${snes:X6}, at ${Pc:X6}");

    /// <summary>Pad with 0xFF (blends with the surrounding vanilla freespace) up to a fixed entry point.</summary>
    public Asm PadTo(int snes) => PadTo(snes, 0xFF);

    /// <summary>Pad with an explicit filler. 0xEA (NOP) is the one to use when the padding sits
    /// INSIDE a routine and execution falls through it, where 0xFF would execute as garbage.</summary>
    public Asm PadTo(int snes, byte fill)
    {
        if (Pc > snes) throw new InvalidOperationException($"overran ${snes:X6}, at ${Pc:X6}");
        while (Pc < snes) code.Add(fill);
        return this;
    }

    public Asm Label(string name) { labels[name] = Pc; return this; }

    /// <summary>The SNES address a label landed on — for a caller that has to write a jump to it
    /// from OUTSIDE this block (a hijack at a fixed site pointing into the code being built).</summary>
    public int LabelAt(string name) => labels[name];

    // ---- loads/stores ----
    public Asm LdaImm8(int v) => E(0xA9, (byte)v);
    public Asm LdaImm16(int v) => Imm16(0xA9, v);
    public Asm LdaDp(int d) => E(0xA5, (byte)d);
    public Asm StaDp(int d) => E(0x85, (byte)d);
    public Asm LdaAbs(int a) => Imm16(0xAD, a);
    public Asm StaAbs(int a) => Imm16(0x8D, a);
    public Asm LdaAbsX(int a) => Imm16(0xBD, a);
    public Asm LdaIndLong(int d) => E(0xA7, (byte)d);     // LDA [dp]
    public Asm LdaAbsY(int a) => Imm16(0xB9, a);
    public Asm StaAbsX(int a) => Imm16(0x9D, a);
    public Asm StaAbsY(int a) => Imm16(0x99, a);
    public Asm LdaLongX(int a) => E(0xBF, (byte)a, (byte)(a >> 8), (byte)(a >> 16));
    public Asm StaLongX(int a) => E(0x9F, (byte)a, (byte)(a >> 8), (byte)(a >> 16));
    public Asm LdaIndLongY(int d) => E(0xB7, (byte)d);   // LDA [dp],Y
    public Asm StaIndLongY(int d) => E(0x97, (byte)d);   // STA [dp],Y
    public Asm LdyImm8(int v) => E(0xA0, (byte)v);
    public Asm LdyImm16(int v) => Imm16(0xA0, v);
    public Asm LdyDp(int d) => E(0xA4, (byte)d);
    public Asm StyDp(int d) => E(0x84, (byte)d);
    public Asm LdxImm8(int v) => E(0xA2, (byte)v);
    public Asm LdxImm16(int v) => Imm16(0xA2, v);
    public Asm StxDp(int d) => E(0x86, (byte)d);
    public Asm LdaDpX(int d) => E(0xB5, (byte)d);
    public Asm StzDp(int d) => E(0x64, (byte)d);
    public Asm StzAbs(int a) => Imm16(0x9C, a);

    // ---- arithmetic/logic ----
    public Asm AdcImm8(int v) => E(0x69, (byte)v);
    public Asm AdcImm16(int v) => Imm16(0x69, v);
    public Asm AdcDp(int d) => E(0x65, (byte)d);
    public Asm AdcAbs(int a) => Imm16(0x6D, a);
    public Asm SbcImm16(int v) => Imm16(0xE9, v);
    public Asm SbcDp(int d) => E(0xE5, (byte)d);
    public Asm AndImm8(int v) => E(0x29, (byte)v);
    public Asm AndImm16(int v) => Imm16(0x29, v);
    public Asm AndDp(int d) => E(0x25, (byte)d);
    public Asm OraDp(int d) => E(0x05, (byte)d);
    public Asm OraIndLong(int d) => E(0x07, (byte)d);    // ORA [dp]
    public Asm OraAbsX(int a) => Imm16(0x1D, a);
    public Asm CmpImm8(int v) => E(0xC9, (byte)v);
    public Asm CmpImm16(int v) => Imm16(0xC9, v);
    public Asm CpyImm16(int v) => Imm16(0xC0, v);
    public Asm CpxImm16(int v) => Imm16(0xE0, v);
    public Asm Asl() => E(0x0A);
    public Asm Lsr() => E(0x4A);
    public Asm Rol() => E(0x2A);
    /// <summary>BIT #imm — tests bits WITHOUT touching A, which is the whole reason to use it
    /// over AND when the accumulator is still needed afterwards.</summary>
    public Asm BitImm8(int v) => E(0x89, (byte)v);
    public Asm BitImm16(int v) => Imm16(0x89, v);
    public Asm Clc() => E(0x18);
    public Asm Sec() => E(0x38);
    public Asm IncA() => E(0x1A);
    public Asm DecA() => E(0x3A);
    /// <summary>Raw data bytes (inline tables).</summary>
    public Asm Db(params byte[] bytes) => E(bytes);
    public Asm DecDp(int d) => E(0xC6, (byte)d);
    public Asm IncDp(int d) => E(0xE6, (byte)d);
    public Asm Iny() => E(0xC8);
    public Asm Inx() => E(0xE8);
    public Asm Dey() => E(0x88);
    public Asm Dex() => E(0xCA);

    // ---- transfers/stack/flags ----
    public Asm Tax() => E(0xAA);
    public Asm Tay() => E(0xA8);
    public Asm Txa() => E(0x8A);
    public Asm Tya() => E(0x98);
    public Asm Xba() => E(0xEB);
    public Asm Pha() => E(0x48);
    public Asm Pla() => E(0x68);
    public Asm Phx() => E(0xDA);
    public Asm Plx() => E(0xFA);
    public Asm Phy() => E(0x5A);
    public Asm Ply() => E(0x7A);
    public Asm Phb() => E(0x8B);
    public Asm Plb() => E(0xAB);
    public Asm Phk() => E(0x4B);
    public Asm Php() => E(0x08);
    public Asm Plp() => E(0x28);
    public Asm Rep(int f) => E(0xC2, (byte)f);
    public Asm Sep(int f) => E(0xE2, (byte)f);
    public Asm Nop() => E(0xEA);

    // ---- flow ----
    public Asm Rts() => E(0x60);
    public Asm Rtl() => E(0x6B);
    public Asm Jsr(int abs16) => Imm16(0x20, abs16);
    public Asm JsrL(string label) { E(0x20, 0, 0); fix.Add((code.Count - 2, label, false)); return this; }
    public Asm Jsl(int snes) => E(0x22, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16));
    public Asm Jml(int snes) => E(0x5C, (byte)snes, (byte)(snes >> 8), (byte)(snes >> 16));
    public Asm Jmp(string label) { E(0x4C, 0, 0); fix.Add((code.Count - 2, label, false)); return this; }
    public Asm JmpAbs(int abs16) => Imm16(0x4C, abs16);

    private Asm Rel(byte op, string l) { E(op, 0); fix.Add((code.Count - 1, l, true)); return this; }
    public Asm Bra(string l) => Rel(0x80, l);
    public Asm Beq(string l) => Rel(0xF0, l);
    public Asm Bne(string l) => Rel(0xD0, l);
    public Asm Bcc(string l) => Rel(0x90, l);
    public Asm Bcs(string l) => Rel(0xB0, l);
    public Asm Bmi(string l) => Rel(0x30, l);
    public Asm Bpl(string l) => Rel(0x10, l);
}
