using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// The static half of the SA-1 compatibility report (reference/SA1_PORT.md, phase 4): what a
/// resource's ASM means for an SA-1 build, read from its text before anything is assembled.
///
/// The SA-1 Pack moves SMW's RAM (direct page to $3000, $0100-$1FFF to $6100-$7FFF, the sprite
/// tables to its 22-slot layout, the big $7E/$7F buffers to $40/$41), and the SA-1 CPU — which runs
/// sprites and blocks there — reaches neither work RAM nor the SNES's multiplier. Code written with
/// the defines (`$1DFB|!addr`, `!14C8,x`) follows the move; a literal address does not. So a
/// statement is judged by what it addresses; code inside either branch of an SA-1 test (`if !sa1 …
/// else …`) was written for one mapping on purpose and is not judged at all.
///
/// It reads text, not machine code: a define it cannot resolve, a macro, an address computed at
/// assembly are not judged, and an `org` into code the pack rewrites is the build's to find (its
/// overlap check owns the pack's blocks).
/// </summary>
public static class Sa1Compat
{
    /// <summary>Where a resource's code runs, which decides what it may touch: the SA-1 CPU (sprites,
    /// blocks), the SNES CPU (UberASM's code points), or whichever calls it (a patch).</summary>
    public enum Cpu { Sa1, Snes, Either }

    public enum Fit { Ready, Check, Breaks }

    /// <summary>One statement and why it matters.</summary>
    public sealed record Finding(string File, int Line, string Code, string Why)
    {
        public override string ToString() => $"{File}:{Line}: {Code} — {Why}";
    }

    /// <summary>What a resource's files hold: what breaks on SA-1, what may (<see cref="Check"/>),
    /// and what needs SA-1, which matters with the switch off.</summary>
    public sealed record Result(IReadOnlyList<Finding> Breaks, IReadOnlyList<Finding> Check, IReadOnlyList<Finding> NeedsSa1)
    {
        public Fit OnSa1 => Breaks.Count > 0 ? Fit.Breaks : Check.Count > 0 ? Fit.Check : Fit.Ready;
        public bool NeedsSa1Build => NeedsSa1.Count > 0;
    }

    /// <summary>SMW's sprite tables (12 slots), which the pack moves for 22: indexed by slot, so a
    /// literal one reads another table on SA-1.</summary>
    private static readonly HashSet<int> SpriteTables =
    [
        0x9E, 0xAA, 0xB6, 0xC2, 0xD8, 0xE4,
        0x14C8, 0x14D4, 0x14E0, 0x14EC, 0x14F8, 0x1504, 0x1510, 0x151C, 0x1528, 0x1534, 0x1540, 0x154C,
        0x1558, 0x1564, 0x1570, 0x157C, 0x1588, 0x1594, 0x15A0, 0x15AC, 0x15B8, 0x15C4, 0x15D0, 0x15DC,
        0x15EA, 0x15F6, 0x1602, 0x160E, 0x161A, 0x1626, 0x1632, 0x163E, 0x164A, 0x1656, 0x1662, 0x166E,
        0x167A, 0x1686, 0x186C, 0x187B, 0x190F, 0x1FD6, 0x1FE2,
    ];

    /// <summary>Instructions whose operand is a place to jump, not data: a 16-bit one names code in
    /// the bank, so it is not SMW's RAM.</summary>
    private static readonly HashSet<string> Jumps = new(["JMP", "JSR", "JML", "JSL", "BRA", "BRL", "BCC", "BCS", "BEQ", "BNE",
        "BMI", "BPL", "BVC", "BVS", "PEA", "PER", "MVN", "MVP"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Sa1Marker = new(@"!(sa1|SA1|fullsa1)\b|read1\(\s*\$00FFD5\s*\)", RegexOptions.Compiled);
    private static readonly Regex Literal = new(@"^\s*\$([0-9A-Fa-f]{2,6})\s*$", RegexOptions.Compiled);

    public static Result Scan(IEnumerable<(string File, string Text)> files, Cpu cpu)
    {
        List<Finding> breaks = [], check = [], needs = [];
        var all = files.Select(f => (f.File, Statements: Statements(f.Text))).ToList();
        // !name = $1234: a literal behind a name is as literal as one written out — unless the name is
        // given inside an SA-1 test, as sa1def-style files give !E4 one value per mapping. Across all
        // of a resource's files, since defines usually live in an included one.
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        var switched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, statements) in all)
            foreach (var (_, s, conditional, _) in statements)
                if (Regex.Match(s, @"^!([A-Za-z_]\w*)\s*:?=\s*(.+)$") is { Success: true } d)
                {
                    if (conditional) switched.Add(d.Groups[1].Value);
                    else if (Literal.IsMatch(d.Groups[2].Value)) defines[d.Groups[1].Value] = d.Groups[2].Value.Trim();
                }
        foreach (string name in switched) defines.Remove(name);
        foreach (var (file, statements) in all) ScanFile(file, statements, defines, cpu, breaks, check, needs);
        return new(breaks, check, needs);
    }

    /// <summary>A file's statements (labels off), each with whether it sits inside either branch of an
    /// SA-1 test (`if !sa1`, `if read1($00FFD5) == $23`, their `else`): code there was written for one
    /// mapping on purpose, so it is judged for neither. And whether it is in a macro's body, which runs
    /// only where the macro is used: a library of SA-1 helpers (invoke_sa1) needs SA-1 only if called.</summary>
    private static List<(int Line, string Text, bool Conditional, bool InMacro)> Statements(string text)
    {
        var list = new List<(int, string, bool, bool)>();
        bool inMacro = false;
        var tests = new Stack<bool>();                                // true: an SA-1 test
        bool other = false;                                           // under `arch spc700` (AddmusicK's): not 65816 code
        foreach (var (line, raw) in AsmLint.Statements(text))
        {
            string s = StripLabels(raw);
            string word = Regex.Match(s, @"^[A-Za-z_]\w*").Value.ToLowerInvariant();
            if (word == "arch") { other = !Regex.IsMatch(s, @"^arch\s+65816\b", RegexOptions.IgnoreCase); continue; }
            if (other) continue;
            switch (word)
            {
                case "macro": inMacro = true; continue;
                case "endmacro": inMacro = false; continue;
                case "if": tests.Push(Sa1Marker.IsMatch(s)); continue;
                case "elseif": if (tests.Count > 0 && !tests.Peek()) { tests.Pop(); tests.Push(Sa1Marker.IsMatch(s)); } continue;
                case "else": continue;
                case "endif": if (tests.Count > 0) tests.Pop(); continue;
            }
            if (s.Length > 0) list.Add((line, s, tests.Contains(true), inMacro));
        }
        return list;
    }

    private static void ScanFile(string file, List<(int Line, string Text, bool Conditional, bool InMacro)> statements, Dictionary<string, string> defines,
                                 Cpu cpu, List<Finding> breaks, List<Finding> check, List<Finding> needs)
    {
        foreach (var (line, s, conditional, inMacro) in statements)
        {
            if (conditional || s.Contains('<')) continue;             // one mapping's code; a macro's <arg>, judged once expanded
            var word = Regex.Match(s, @"^([A-Za-z_]\w*)(\.[bwlBWL])?\b");
            string name = word.Success ? word.Groups[1].Value.ToLowerInvariant() : "";
            string rest = word.Success ? s[word.Length..].Trim() : "";
            void Add(List<Finding> to, string why) => to.Add(new(file, line, s, why));

            if (name == "lorom") { Add(check, "sets LoROM addressing, which past 2MB is not where an SA-1 ROM puts things"); continue; }
            if (name is "sa1rom" or "fullsa1rom") { if (!inMacro) Add(needs, "sets SA-1 addressing"); continue; }
            if (!word.Success || rest.Length == 0) continue;
            if (!AsmMnemonics.Contains(name)) continue;               // a directive, a define, a macro

            string operand = rest;
            string via = "";
            if (Regex.Match(operand, @"^([#\[(]?)!([A-Za-z_]\w*)(.*)$") is { Success: true } use && defines.TryGetValue(use.Groups[2].Value, out var lit))
            {
                operand = use.Groups[1].Value + lit + use.Groups[3].Value;
                via = $" (through !{use.Groups[2].Value})";
            }
            string mnemonic = name.ToUpperInvariant();

            if (operand.StartsWith('#'))
            {
                if (mnemonic is "LDX" or "LDY" && Regex.IsMatch(operand, @"^#\$0?[Bb]\s*$"))
                    Add(check, "starts a loop over 12 sprite slots? SA-1 has 22 (!SprSize)" + via);
                continue;
            }
            var num = Regex.Match(operand, @"^[\[(]?\$([0-9A-Fa-f]+)(.*)$");
            if (!num.Success) continue;
            string digits = num.Groups[1].Value, after = num.Groups[2].Value.TrimStart();
            if (after.StartsWith('|') || after.StartsWith('+') && after.Contains('!')) continue;   // $1DFB|!addr: follows the move
            int value = Convert.ToInt32(digits, 16);
            bool indexed = Regex.IsMatch(after, @"^[)\]]?\s*,\s*[xXyY]");
            // A jump's operand is code, but for JMP ($xxxx) / JMP [$xxxx], which read a pointer from bank 0
            // — and JMP/JSR ($xxxx,x) reads its pointer from the program bank, so that is code too.
            bool jump = Jumps.Contains(mnemonic) && (!operand.StartsWith('(') && !operand.StartsWith('[') || indexed && operand.StartsWith('('));

            foreach (string why in OnSa1(digits.Length, value, indexed, jump, cpu)) Add(why.StartsWith('?') ? check : breaks, why.TrimStart('?') + via);
            if (!inMacro && NeedsSa1(digits.Length, value, jump) is { } need) Add(needs, need + via);
        }
    }

    /// <summary>What an address operand means on an SA-1 build; a reason starting "?" is a doubt, not a break.</summary>
    private static IEnumerable<string> OnSa1(int digits, int value, bool indexed, bool jump, Cpu cpu)
    {
        if (digits <= 2)
        {
            if (indexed && SpriteTables.Contains(value)) yield return $"sprite table ${value:X2} by its 12-slot address; SA-1 moves the sprite tables for 22 slots (!{value:X2})";
            yield break;
        }
        if (jump && digits == 4) yield break;
        if (digits == 4 && value is >= 0x0181 and <= 0x018A) yield break;   // the pack's call into the SNES CPU: NeedsSa1's
        int bank = digits == 6 ? value >> 16 : -1, addr = value & 0xFFFF;
        if (digits == 6 && addr == 0 && indexed)
        {
            // $000000,x / $7E0000,x: a bank's start plus a 16-bit index, so the index says where. Bank
            // 00 is then usually ROM (SMW's pointers); 7E/7F is work RAM whatever the index.
            if (bank is 0x7E or 0x7F)
                yield return cpu == Cpu.Sa1 ? "work RAM, which the SA-1 CPU (sprites, blocks) cannot reach"
                                            : "?work RAM by a 16-bit index: SA-1 moves SMW's RAM and buffers out of it (!BankA)";
            yield break;
        }
        bool wram = digits == 4 || bank is 0x00 or 0x80 or 0x7E;

        if (wram && addr < 0x0100)
            yield return digits == 6
                ? $"the direct page by a long address; SA-1 keeps it at $00{0x3000 + addr:X4}"
                : $"the direct page by an absolute address; SA-1 keeps it at ${0x3000 + addr:X4} (|!dp)";
        else if (wram && addr < 0x2000)
            yield return SpriteTables.Contains(addr)
                ? $"sprite table ${addr:X4} by its 12-slot address; SA-1 moves the sprite tables for 22 slots (!{addr:X4})"
                : digits == 6
                    ? $"SMW's RAM at its LoROM address; SA-1 keeps it at $40{addr:X4} (|!BankA)"
                    : $"SMW's RAM at its LoROM address; SA-1 keeps it at ${0x6000 + addr:X4} (|!addr)";
        else if (bank is 0x7E or 0x7F)
        {
            if (cpu == Cpu.Sa1) yield return "work RAM, which the SA-1 CPU (sprites, blocks) cannot reach";
            else if (bank == 0x7E && addr >= 0xC800 || bank == 0x7F && addr >= 0xC800)
                yield return $"SMW's buffer, which SA-1 moves to ${(bank == 0x7E ? 0x40 : 0x41):X2}{addr:X4}";
            else if (bank == 0x7F && addr is >= 0x9A7B and <= 0x9C7A)
                yield return $"SMW's buffer, which SA-1 moves to $41{0x8800 + addr - 0x9A7B:X4}";
        }
        if (digits == 4 && (addr is >= 0x4202 and <= 0x4206 or >= 0x4214 and <= 0x4217 || addr is 0x211B or 0x211C or >= 0x2134 and <= 0x2136))
        {
            string what = addr < 0x4000 ? "the PPU's multiplier" : "the SNES CPU's multiplier/divider";
            if (cpu == Cpu.Sa1) yield return $"{what}, which the SA-1 CPU (sprites, blocks) cannot reach ($2250)";
            else if (cpu == Cpu.Either) yield return $"?{what}: the SA-1 CPU cannot reach it, if this runs there";
        }
    }

    /// <summary>What only an SA-1 ROM has: its registers, I-RAM, BW-RAM.</summary>
    private static string? NeedsSa1(int digits, int value, bool jump)
    {
        if (digits == 4 && !jump)
        {
            // SA-1 code asks the SNES CPU to run something through $0183-$0185 and waits on $018A: I-RAM
            // $3183…, as the SA-1 CPU sees $0000-$07FF (the pack's programming guide).
            if (value is >= 0x0181 and <= 0x018A) return "the SA-1 Pack's call into the SNES CPU";
            if (value is >= 0x2200 and <= 0x23FF) return "an SA-1 register";
            if (value is >= 0x3000 and <= 0x37FF) return "SA-1's I-RAM";
            if (value is >= 0x6000 and <= 0x7FFF) return "SA-1's BW-RAM (the $6000 window)";
        }
        if (digits == 6 && value >> 16 is 0x40 or 0x41) return "SA-1's BW-RAM";
        return null;
    }

    /// <summary>A statement without the labels in front of it: "Name:", ".sub", "+", "?loop".</summary>
    private static string StripLabels(string s)
    {
        while (Regex.Match(s, @"^(?:[A-Za-z_?][\w.?]*:(?!=)|\.+[A-Za-z_]\w*:?|\??[+\-]+(?=\s|$))\s*") is { Success: true, Length: > 0 } m)
            s = s[m.Length..];
        return s.Trim();
    }

    private static readonly HashSet<string> AsmMnemonics = new(
        ("ADC AND ASL BCC BCS BEQ BIT BMI BNE BPL BRA BRL BVC BVS CMP COP CPX CPY DEC EOR INC JML JMP JSL JSR " +
         "LDA LDX LDY LSR MVN MVP ORA PEA PEI PER ROL ROR SBC STA STX STY STZ TRB TSB").Split(' '),
        StringComparer.OrdinalIgnoreCase);
}
