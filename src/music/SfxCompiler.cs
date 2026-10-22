using System.Text.RegularExpressions;

namespace PipeDream.Music;

/// <summary>
/// AddmusicK sound-effect files to the bytecode the engine plays (SMW's own, reference/AMK_ENGINE.md § Sound effects). The
/// stock effects are raw hex; so is most of what people write. <c>#asm</c> blocks and <c>#jsr</c> are left out: the two
/// stock effects that use them (pause and unpause) do it to stop the music, which our engine does itself for $1DF9 $11/$12.
/// The note syntax compiles as AddmusicK 1.0.5's does (measured with tools/amk/amk_sfx_compile.py), not as its readme says:
/// every note writes its length; notes are all in octave 4 (<c>o</c>, <c>&lt;</c>, <c>&gt;</c> do nothing); <c>v</c>, <c>h</c>,
/// <c>q</c> and <c>&amp;</c> write nothing; <c>@n,m</c> reads m as decimal; a length past 127 is $7F and a tie for the rest.
/// </summary>
public static class SfxCompiler
{
    /// <param name="noEnd">The list's <c>?</c> flag: no $00 is added (the effect ends with $FE or $FF of its own).</param>
    public static (byte[] Bytes, List<string> Problems) Compile(string text, bool noEnd = false)
    {
        var problems = new List<string>();
        var bytes = new List<byte>();
        // Inline SPC700 is AddmusicK's engine's, not ours. The stock pause and unpause effects use it only to hold the song and let it go,
        // so a #jsr to code that sets !PauseMusic becomes our engine's own $FD 1 (pause) or $FD 0; anything else is reported.
        var asm = Regex.Matches(text, @"#asm\s+(\w+)\s*\{([^}]*)\}").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        text = Regex.Replace(text, @"#asm\s+\w+\s*\{[^}]*\}", " ");
        text = Regex.Replace(text, @"#jsr\s+(\w+)", m =>
        {
            if (asm.TryGetValue(m.Groups[1].Value, out var body) && body.Contains("!PauseMusic"))
                return Regex.IsMatch(body, @"inc\s+a[^!]*!PauseMusic") ? " $FD $01 " : " $FD $00 ";
            problems.Add($"#jsr {m.Groups[1].Value}: inline SPC700 runs only in AddmusicK's engine");
            return " ";
        });
        Notes(text, bytes, problems);
        if (!noEnd) bytes.Add(0x00);
        return (bytes.ToArray(), problems);
    }

    private static void Notes(string text, List<byte> bytes, List<string> problems)
    {
        int pos = 0, defaultLength = 24;
        bool triplet = false;
        char Peek(int k = 0) => pos + k < text.Length ? text[pos + k] : '\0';
        int? Int()
        {
            int start = pos;
            while (char.IsDigit(Peek())) pos++;
            return pos > start ? int.Parse(text.AsSpan(start, pos - start)) : null;
        }
        // A parameter written in hex digits is taken whole but read as decimal up to its first letter (AddmusicK's: "@10,1F" is 1).
        int? HexishDecimal()
        {
            int start = pos;
            while (Uri.IsHexDigit(Peek())) pos++;
            var digits = new string(text[start..pos].TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? int.Parse(digits) : null;
        }
        int Length()
        {
            double ticks;
            if (Peek() == '=') { pos++; ticks = Int() ?? 0; }
            else
            {
                int? d = Int();
                ticks = d is > 0 ? 192.0 / d.Value : defaultLength;
                double add = ticks;
                while (Peek() == '.') { pos++; add /= 2; ticks += add; }
            }
            if (triplet) ticks = ticks * 2 / 3;
            return (int)Math.Round(ticks);
        }
        void Emit(int ticks, byte what)
        {
            bytes.Add((byte)Math.Min(ticks, 0x7F)); bytes.Add(what);
            for (ticks -= 0x7F; ticks > 0; ticks -= 0x7F) { bytes.Add((byte)Math.Min(ticks, 0x7F)); bytes.Add(0xC6); }
        }
        while (pos < text.Length)
        {
            char c = char.ToLowerInvariant(Peek());
            if (char.IsWhiteSpace(c) || c == '&' || c == '<' || c == '>' || c == '|') { pos++; continue; }
            if (c == ';') { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
            if (c == '$' && Uri.IsHexDigit(Peek(1)) && Uri.IsHexDigit(Peek(2))) { bytes.Add(Convert.ToByte(text.Substring(pos + 1, 2), 16)); pos += 3; continue; }
            if (c == '{') { triplet = true; pos++; continue; }
            if (c == '}') { triplet = false; pos++; continue; }
            int at = pos;
            pos++;
            switch (c)
            {
                case >= 'a' and <= 'g':
                {
                    int note = "c d ef g a b".IndexOf(c);
                    if (Peek() == '+') { note++; pos++; } else if (Peek() == '-') { note--; pos++; }
                    Emit(Length(), (byte)(0xA4 + note));
                    break;
                }
                case 'r': Emit(Length(), 0xC7); break;
                case '^': Emit(Length(), 0xC6); break;
                case 'l': defaultLength = Int() is > 0 and var d ? 192 / d : defaultLength; break;
                case 'o' or 'h': if (Peek() == '-') pos++; Int(); break;
                case 'q': HexishDecimal(); break;
                case 'v': Int(); if (Peek() == ',') { pos++; Int(); } break;
                case '@':
                {
                    int n = Int() ?? 0;
                    bytes.Add(0xDA);
                    if (Peek() == ',') { pos++; bytes.Add((byte)(0x80 | (HexishDecimal() ?? 0) & 0x7F)); }   // noise: $80 | the clock
                    bytes.Add((byte)n);
                    break;
                }
                default:
                    while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
                    problems.Add($"\"{text[at..pos]}\": not understood in a sound effect");
                    break;
            }
        }
    }
}

/// <summary>A set of sound effects ready to place: SMW's effect instruments, and each port's effects by number (null: none).</summary>
public sealed record SoundEffects(byte[] Instruments, IReadOnlyList<byte[]?> Port1DF9, IReadOnlyList<byte[]?> Port1DFC)
{
    /// <summary>The block at <paramref name="at"/>: instruments, the $1DF9 table, the $1DFC table, then the data (an effect that is
    /// the same array as another, a <c>*</c> in the list, shares its bytes). Its <c>SfxInfo</c> is what the engine reads at $03F5.</summary>
    public (byte[] Block, byte[] Info) Place(int at)
    {
        int n9 = Port1DF9.Count, nC = Port1DFC.Count;
        int t9 = at + Instruments.Length, tC = t9 + 2 * n9, data = tC + 2 * nC;
        var block = new List<byte>(Instruments);
        block.AddRange(new byte[2 * (n9 + nC)]);
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        void Table(IReadOnlyList<byte[]?> list, int table)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not { } b) continue;
                if (!placed.TryGetValue(b, out int addr)) { addr = at + block.Count; placed[b] = addr; block.AddRange(b); }
                block[table - at + 2 * i] = (byte)addr; block[table - at + 2 * i + 1] = (byte)(addr >> 8);
            }
        }
        Table(Port1DF9, t9);
        Table(Port1DFC, tC);
        var info = new byte[] { (byte)at, (byte)(at >> 8), (byte)t9, (byte)(t9 >> 8), (byte)tC, (byte)(tC >> 8), (byte)n9, (byte)nC };
        return (block.ToArray(), info);
    }

    /// <summary>SMW's effect instruments, read from the ROM's own upload: 19 nine-byte entries at $5570 (the last two bytes are the
    /// first word of the table after them in SMW, $037F; the same in AddmusicK).</summary>
    public static byte[] SmwInstruments(Rom rom) => SmwSound.Upload(rom, SmwSound.EngineStream)[0x5570..0x561B];
}
