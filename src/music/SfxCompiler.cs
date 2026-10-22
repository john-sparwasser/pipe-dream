using System.Text.RegularExpressions;

namespace PipeDream.Music;

/// <summary>
/// AddmusicK sound-effect files to the bytecode the engine plays (SMW's own, reference/AMK_ENGINE.md § Sound effects). The
/// stock effects are raw hex; so is most of what people write. <c>#asm</c> blocks and <c>#jsr</c> are left out: the two
/// stock effects that use them (pause and unpause) do it to stop the music, which our engine does itself for $1DF9 $11/$12.
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
        foreach (Match m in Regex.Matches(text, @";[^\r\n]*|\$([0-9A-Fa-f]{2})(?![0-9A-Fa-f])|\S+"))
        {
            if (m.Value.StartsWith(';')) continue;
            if (m.Groups[1].Success) { bytes.Add(Convert.ToByte(m.Groups[1].Value, 16)); continue; }
            problems.Add($"\"{m.Value}\": only hex ($xx) is understood in sound effects so far");
        }
        if (!noEnd) bytes.Add(0x00);
        return (bytes.ToArray(), problems);
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
