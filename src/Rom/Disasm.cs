using System.Text;

namespace PipeDream;

/// <summary>
/// Minimal 65816 linear-sweep disassembler for tracing LM's inserted ASM in a LoROM image
/// (the reverse-engineering workhorse). Tracks REP/SEP to size M/X-dependent immediates as
/// it goes. Ported from tools/dis65816.py so the RE loop needs no external Python.
/// </summary>
public static class Disasm
{
    // "MN,mode" per opcode 0x00..0xFF. mode drives operand size/formatting.
    private const string Table =
        "BRK,imm8 ORA,indx COP,imm8 ORA,sr TSB,dp ORA,dp ASL,dp ORA,indl PHP,imp ORA,imm_m ASL,imp PHD,imp TSB,abs ORA,abs ASL,abs ORA,long " +
        "BPL,rel8 ORA,indy ORA,ind ORA,sry TRB,dp ORA,dpx ASL,dpx ORA,indly CLC,imp ORA,absy INC,imp TCS,imp TRB,abs ORA,absx ASL,absx ORA,longx " +
        "JSR,abs AND,indx JSL,long AND,sr BIT,dp AND,dp ROL,dp AND,indl PLP,imp AND,imm_m ROL,imp PLD,imp BIT,abs AND,abs ROL,abs AND,long " +
        "BMI,rel8 AND,indy AND,ind AND,sry BIT,dpx AND,dpx ROL,dpx AND,indly SEC,imp AND,absy DEC,imp TSC,imp BIT,absx AND,absx ROL,absx AND,longx " +
        "RTI,imp EOR,indx WDM,imm8 EOR,sr MVP,blk EOR,dp LSR,dp EOR,indl PHA,imp EOR,imm_m LSR,imp PHK,imp JMP,abs EOR,abs LSR,abs EOR,long " +
        "BVC,rel8 EOR,indy EOR,ind EOR,sry MVN,blk EOR,dpx LSR,dpx EOR,indly CLI,imp EOR,absy PHY,imp TCD,imp JML,long EOR,absx LSR,absx EOR,longx " +
        "RTS,imp ADC,indx PER,rel16 ADC,sr STZ,dp ADC,dp ROR,dp ADC,indl PLA,imp ADC,imm_m ROR,imp RTL,imp JMP,absind ADC,abs ROR,abs ADC,long " +
        "BVS,rel8 ADC,indy ADC,ind ADC,sry STZ,dpx ADC,dpx ROR,dpx ADC,indly SEI,imp ADC,absy PLY,imp TDC,imp JMP,absindx ADC,absx ROR,absx ADC,longx " +
        "BRA,rel8 STA,indx BRL,rel16 STA,sr STY,dp STA,dp STX,dp STA,indl DEY,imp BIT,imm_m TXA,imp PHB,imp STY,abs STA,abs STX,abs STA,long " +
        "BCC,rel8 STA,indy STA,ind STA,sry STY,dpx STA,dpx STX,dpy STA,indly TYA,imp STA,absy TXS,imp TXY,imp STZ,abs STA,absx STZ,absx STA,longx " +
        "LDY,imm_x LDA,indx LDX,imm_x LDA,sr LDY,dp LDA,dp LDX,dp LDA,indl TAY,imp LDA,imm_m TAX,imp PLB,imp LDY,abs LDA,abs LDX,abs LDA,long " +
        "BCS,rel8 LDA,indy LDA,ind LDA,sry LDY,dpx LDA,dpx LDX,dpy LDA,indly CLV,imp LDA,absy TSX,imp TYX,imp LDY,absx LDA,absx LDX,absy LDA,longx " +
        "CPY,imm_x CMP,indx REP,imm8 CMP,sr CPY,dp CMP,dp DEC,dp CMP,indl INY,imp CMP,imm_m DEX,imp WAI,imp CPY,abs CMP,abs DEC,abs CMP,long " +
        "BNE,rel8 CMP,indy CMP,ind CMP,sry PEI,dp CMP,dpx DEC,dpx CMP,indly CLD,imp CMP,absy PHX,imp STP,imp JML,absindl CMP,absx DEC,absx CMP,longx " +
        "CPX,imm_x SBC,indx SEP,imm8 SBC,sr CPX,dp SBC,dp INC,dp SBC,indl INX,imp SBC,imm_m NOP,imp XBA,imp CPX,abs SBC,abs INC,abs SBC,long " +
        "BEQ,rel8 SBC,indy SBC,ind SBC,sry PEA,abs SBC,dpx INC,dpx SBC,indly SED,imp SBC,absy PLX,imp XCE,imp JSR,absindx SBC,absx INC,absx SBC,longx";

    private static readonly (string mn, string mode)[] Ops = Build();
    private static (string, string)[] Build()
    {
        var toks = Table.Split(' ');
        var ops = new (string, string)[256];
        for (int i = 0; i < 256; i++) { var t = toks[i].Split(','); ops[i] = (t[0], t[1]); }
        return ops;
    }

    private static int Size(string mode) => mode switch
    {
        "imp" => 0,
        "abs" or "absx" or "absy" or "rel16" or "absind" or "absindx" or "absindl" or "blk" => 2,
        "long" or "longx" => 3,
        _ => 1,   // imm8/dp/dpx/dpy/sr/sry/ind*/rel8
    };

    public static string Dis(Rom rom, int snes, int count, bool m8 = false, bool x8 = false)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < count; k++)
        {
            int fo = Rom.SnesToPc(snes) + rom.HeaderOffset;
            if (fo < 0 || fo >= rom.Data.Length) break;
            byte op = rom.Data[fo];
            var (mn, mode) = Ops[op];
            int n = mode == "imm_m" ? (m8 ? 1 : 2) : mode == "imm_x" ? (x8 ? 1 : 2) : Size(mode);
            int v = 0;
            for (int i = 0; i < n && fo + 1 + i < rom.Data.Length; i++) v |= rom.Data[fo + 1 + i] << (8 * i);
            if (mn == "REP") { if ((v & 0x20) != 0) m8 = false; if ((v & 0x10) != 0) x8 = false; }
            else if (mn == "SEP") { if ((v & 0x20) != 0) m8 = true; if ((v & 0x10) != 0) x8 = true; }

            string s;
            if (mode == "rel8")
            { int tgt = (snes & 0xFF0000) | ((snes + 2 + (v > 127 ? v - 256 : v)) & 0xFFFF); s = $"{mn} ${tgt:X6}"; }
            else if (mode == "rel16")
            { int tgt = (snes & 0xFF0000) | ((snes + 3 + (v > 32767 ? v - 65536 : v)) & 0xFFFF); s = $"{mn} ${tgt:X6}"; }
            else if (mode is "imm_m" or "imm_x" or "imm8") s = $"{mn} #${v.ToString($"X{n * 2}")}";
            else if (mode == "blk") s = $"{mn} ${rom.Data[fo + 2]:X2},${rom.Data[fo + 1]:X2}";
            else if (n == 0) s = mn;
            else
            {
                string pre = mode switch { "ind" or "indy" or "indx" or "absind" or "absindx" or "sry" => "(", "indl" or "indly" or "absindl" => "[", _ => "" };
                string post = mode switch { "ind" or "absind" => ")", "indx" or "absindx" => ",X)", "indl" or "absindl" => "]", _ => "" };
                string suff = mode switch { "dpx" or "absx" or "longx" => ",X", "dpy" or "absy" => ",Y", "sr" => ",S", "sry" => ",S),Y", "indy" => "),Y", "indly" => "],Y", _ => "" };
                s = $"{mn} {pre}${v.ToString($"X{Math.Max(n, 1) * 2}")}{post}{suff}";
            }

            var raw = new StringBuilder();
            for (int i = 0; i <= n && fo + i < rom.Data.Length; i++) raw.Append(rom.Data[fo + i].ToString("x2"));
            sb.AppendLine($"${snes:X6}: {raw,-10} {s}");
            snes = (snes & 0xFF0000) | ((snes + 1 + n) & 0xFFFF);
            if (mn is "RTS" or "RTL" or "RTI" or "JML" or "BRA" or "STP" && mode != "absindl")
                sb.AppendLine(new string('-', 40));
        }
        return sb.ToString();
    }
}
