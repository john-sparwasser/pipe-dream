using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>One problem <see cref="AsmLint"/> found: where in the text, and what to say.</summary>
public sealed record AsmIssue(int Offset, int Length, string Message);

/// <summary>
/// A quick syntax check of block ASM (Asar's 65816) for the block editor's squiggles: line by
/// line, no symbols, no assembly. It catches what a typo makes — an unknown instruction, a missing
/// or extra operand, a size suffix Asar has no meaning for, a malformed number, an unclosed quote
/// or bracket — and stays quiet about anything it cannot judge without assembling (defines that
/// expand to code, macro bodies, labels). Asar at build time remains the last word.
/// </summary>
public static class AsmLint
{
    private static readonly HashSet<string> Implied = Words(
        "CLC CLD CLI CLV DEX DEY INX INY NOP PHA PHB PHD PHK PHP PHX PHY PLA PLB PLD PLP PLX PLY " +
        "RTI RTL RTS SEC SED SEI STP TAX TAY TCD TCS TDC TSC TSX TXA TXS TXY TYA TYX WAI XBA XCE");
    /// <summary>The operand may be left off: the accumulator forms, and the signature byte.</summary>
    private static readonly HashSet<string> OptionalOperand = Words("ASL LSR ROL ROR INC DEC BRK COP WDM");
    private static readonly HashSet<string> NeedsOperand = Words(
        "ADC AND BCC BCS BEQ BIT BMI BNE BPL BRA BRL BVC BVS CMP CPX CPY EOR JML JMP JSL JSR LDA LDX LDY " +
        "MVN MVP ORA PEA PEI PER REP SBC SEP STA STX STY STZ TRB TSB");
    private static readonly HashSet<string> Directives = Words(
        "org base freecode freedata freespace autoclean prot cleaned align incsrc incbin includeonce " +
        "include db dw dl dd fill fillbyte fillword filllong filldword pad padbyte padword padlong " +
        "paddword skip warnpc macro endmacro if elseif else endif while endwhile for endfor print " +
        "error warn assert namespace pushpc pullpc pushbase pullbase pushns popns lorom hirom exlorom " +
        "exhirom sa1rom fullsa1rom sfxrom norom arch math check bank function struct endstruct global " +
        "undef table cleartable rep spcblock endspcblock startpos dpbase optimize bankcross warnings " +
        "pushtable pulltable");

    private static HashSet<string> Words(string s) => new(s.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Hex = new(@"\$[0-9A-Fa-f]*[G-Zg-z_]\w*|\$(?![0-9A-Fa-f])", RegexOptions.Compiled);
    private static readonly Regex Binary = new(@"(?<![\w)\]])%[01]*[2-9][0-9]*\b", RegexOptions.Compiled);

    public static List<AsmIssue> Check(string text)
    {
        var issues = new List<AsmIssue>();
        int lineStart = 0;
        foreach (string line in text.Split('\n'))
        {
            CheckLine(line.TrimEnd('\r'), lineStart, issues);
            lineStart += line.Length + 1;
        }
        return issues;
    }

    /// <summary>A line: its comment cut off, then split into statements on " : " outside quotes.</summary>
    private static void CheckLine(string line, int at, List<AsmIssue> issues)
    {
        var (parts, quote) = Split(line);
        if (quote != '\0')
        {
            int q = line.LastIndexOf(quote);
            issues.Add(new(at + q, line.Length - q, "this quote is never closed"));
            return;
        }
        foreach (var (s, e) in parts) CheckStatement(line[s..e], at + s, issues);
    }

    /// <summary>Every statement of <paramref name="text"/> with its 1-based line, comments cut off
    /// and " : " separators split, trimmed; a line with an unclosed quote gives none.</summary>
    public static IEnumerable<(int Line, string Text)> Statements(string text)
    {
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw.TrimEnd('\r');
            var (parts, quote) = Split(line);
            if (quote != '\0') continue;
            foreach (var (s, e) in parts)
                if (line[s..e].Trim() is { Length: > 0 } stmt) yield return (n, stmt);
        }
    }

    /// <summary>A line's statements as (start, end) spans up to its comment, and the quote left open
    /// at its end ('\0' when none).</summary>
    private static (List<(int Start, int End)> Parts, char OpenQuote) Split(string line)
    {
        var parts = new List<(int Start, int End)>();
        int start = 0;
        char quote = '\0';
        int i = 0;
        for (; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == ';') break;
            // Asar's separator is a colon with whitespace on both sides; "Label:" is not one.
            if (c == ':' && i > 0 && char.IsWhiteSpace(line[i - 1]) && i + 1 < line.Length && char.IsWhiteSpace(line[i + 1]))
            {
                parts.Add((start, i));
                start = i + 1;
            }
        }
        parts.Add((start, i));
        return (parts, quote);
    }

    private static void CheckStatement(string stmt, int at, List<AsmIssue> issues)
    {
        string body = stmt.TrimStart();
        at += stmt.Length - body.Length;
        body = body.TrimEnd();
        if (body.Length == 0) return;
        if (body.Contains('<')) return;                          // a macro body's <arg>: judged once expanded

        CheckBrackets(body, at, issues);
        string bare = Regex.Replace(body, "\"[^\"]*\"|'[^']*'", m => new string(' ', m.Length));   // numbers outside quotes
        foreach (Match m in Hex.Matches(bare)) issues.Add(new(at + m.Index, Math.Max(1, m.Length), "not a hex number"));
        foreach (Match m in Binary.Matches(bare)) issues.Add(new(at + m.Index, m.Length, "not a binary number"));

        // Label definitions open the statement and may carry one after them: "Above: LDA #1". A
        // macro's own labels start with ? and need no colon (?loop, ?+).
        var label = Regex.Match(body, @"^(?:[A-Za-z_?#][\w.?]*:|\.+[A-Za-z_]\w*:?|\?[A-Za-z_]\w*|\??[+\-]+(?=\s|$))\s*");
        if (label.Success)
        {
            if (label.Length < body.Length) CheckStatement(body[label.Length..], at + label.Length, issues);
            return;
        }
        if (body[0] is '!' or '%' or '{' or '}' or '@' or '"' or '\'') return;   // defines, macro calls, braces
        if (Regex.IsMatch(body, @"^[A-Za-z_][\w.]*\s*[:+\-*/#?]?=")) return;      // label = value

        var word = Regex.Match(body, @"^([A-Za-z_]\w*)(\.\w+)?");
        if (!word.Success) { issues.Add(new(at, body.Length, "not an instruction")); return; }
        string name = word.Groups[1].Value, suffix = word.Groups[2].Value;
        string operand = body[word.Length..].Trim();
        if (Directives.Contains(name)) return;

        bool implied = Implied.Contains(name), optional = OptionalOperand.Contains(name), needs = NeedsOperand.Contains(name);
        if (!implied && !optional && !needs)
        {
            issues.Add(new(at, word.Groups[1].Length, $"unknown instruction {name}"));
            return;
        }
        if (suffix.Length > 0 && !Regex.IsMatch(suffix, @"^\.[bwl]$", RegexOptions.IgnoreCase))
            issues.Add(new(at + word.Groups[2].Index, suffix.Length, $"{suffix} is not a size: .b, .w or .l"));
        if (implied && operand.Length > 0)
            issues.Add(new(at + body.IndexOf(operand, word.Length, StringComparison.Ordinal), operand.Length, $"{name.ToUpperInvariant()} takes no operand"));
        if (needs && operand.Length == 0)
            issues.Add(new(at, word.Groups[1].Length, $"{name.ToUpperInvariant()} needs an operand"));
        if (operand == "#")
            issues.Add(new(at + body.LastIndexOf('#'), 1, "# needs a value"));
    }

    /// <summary>() and [] in a statement must pair up, outside quotes.</summary>
    private static void CheckBrackets(string body, int at, List<AsmIssue> issues)
    {
        var open = new Stack<int>();
        char quote = '\0';
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') open.Push(i);
            else if (c is ')' or ']')
            {
                if (open.Count == 0 || "([".IndexOf(body[open.Peek()]) != ")]".IndexOf(c))
                {
                    issues.Add(new(at + i, 1, $"this {c} has nothing to close"));
                    return;
                }
                open.Pop();
            }
        }
        if (open.Count > 0) issues.Add(new(at + open.Peek(), body.Length - open.Peek(), $"this {body[open.Peek()]} is never closed"));
    }
}
