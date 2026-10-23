using System.Globalization;
using System.Text.RegularExpressions;

namespace PipeDream;

/// <summary>
/// Where the SA-1 Pack moves SMW's RAM (reference/SA1_PORT.md, "The pack"): a vanilla address in,
/// the address the same variable has on an SA-1 build out. It is what lets code written for the
/// LoROM game — ours, and Lunar Magic's that the prep carries — say the same thing on SA-1.
///
/// The general moves are the pack's design (its docs/memory-map-summary.html and programming.html)
/// and fixed here: direct page $0000-$00FF to I-RAM $3000, $0100-$1FFF to BW-RAM $6100-$7FFF, the
/// Map16 buffers to banks $40/$41. The sprite tables are read from the installed pack's
/// more_sprites/sprite_tables.asm, since that list is the part a release may change: the More
/// Sprites remap moves each 12-slot table on its own (status $14C8 → $3242), some of them in or out
/// of the direct page.
///
/// Only RAM moves. Hardware registers ($2000-$5FFF) and ROM (≥ $8000) keep their addresses, and a
/// direct-page operand keeps its byte because the pack sets D = $3000 — except in sprite code, for
/// the sprite tables it moved, where <see cref="SpriteDp"/> says so.
/// </summary>
public sealed class Sa1Remap
{
    /// <summary>Vanilla bus address → SA-1 bus address, for every byte of every sprite table.</summary>
    private readonly Dictionary<int, int> sprite;

    public Sa1Remap(IReadOnlyDictionary<int, int> spriteTables) => sprite = new(spriteTables);

    /// <summary>The 12 slots of each <c>%define_sprite_table(name, vanilla, sa1, more_sprites)</c>,
    /// vanilla to More Sprites. A direct-page address on either side is the I-RAM byte it names.</summary>
    public static Sa1Remap FromSpriteTables(string asm)
    {
        var map = new Dictionary<int, int>();
        foreach (Match m in Regex.Matches(asm, @"^\s*%define_sprite_table\(\s*\w+\s*,\s*\$([0-9A-Fa-f]+)\s*,\s*\$[0-9A-Fa-f]+\s*,\s*\$([0-9A-Fa-f]+)\s*\)", RegexOptions.Multiline))
        {
            int from = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber), to = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
            for (int i = 0; i < 12; i++) map[from + i] = Bus(to) + i;
        }
        return new Sa1Remap(map);
    }

    /// <summary>A direct-page byte (with D = 0, as SMW runs) as the bus address it reaches on SA-1.</summary>
    private static int Bus(int a) => a < 0x100 ? 0x3000 | a : a;

    /// <summary>A 16-bit data operand, read with the data bank in a LoROM bank ($00-$3F/$80-$BF).</summary>
    public int Abs(int a)
    {
        if (sprite.TryGetValue(a, out int s)) return s;
        if (a < 0x100) return 0x3000 | a;
        if (a < 0x2000) return 0x6000 | a;
        return a;
    }

    /// <summary>
    /// A direct-page operand: the byte to use instead, or null when the variable left the direct page
    /// (a sprite table now in I-RAM past $30FF) and the instruction must become absolute. Only for
    /// code that means a sprite table by it: $9E-$EF are also scratch outside sprite code, and there
    /// the pack, and Lunar Magic's SA-1 code, leave them alone — <see cref="Code"/> does too.
    /// </summary>
    public int? SpriteDp(int d)
    {
        if (!sprite.TryGetValue(d, out int s)) return d;
        return s is >= 0x3000 and <= 0x30FF ? s & 0xFF : null;
    }

    /// <summary>A 24-bit data operand.</summary>
    public int Long(int a)
    {
        int bank = a >> 16, lo = a & 0xFFFF;
        bool wramLow = lo < 0x2000 && (bank is 0x7E || bank < 0x40 || bank is >= 0x80 and < 0xC0);
        if (bank == 0x7E && lo is >= 0x1938 and <= 0x19B7) return 0x418A00 + lo - 0x1938;   // the sprite load table
        if (wramLow)
        {
            // Through a LoROM bank the mirror stays usable ($00:1234 → $00:7234); bank $7E has no
            // $6000 window, so BW-RAM is reached as itself ($7E:1234 → $40:1234).
            int m = Abs(lo);
            if (bank != 0x7E) return (a & 0xFF0000) | m;
            return m is >= 0x6000 and < 0x8000 ? 0x400000 | (m - 0x6000) : m;
        }
        if (bank == 0x7E && lo >= 0xC800) return 0x400000 | lo;                          // Map16 low bytes
        if (bank == 0x7F && lo >= 0xC800) return 0x410000 | lo;                          // Map16 high bytes
        if (bank == 0x7F && lo is >= 0x9A7B and <= 0x9C7A) return 0x418800 + lo - 0x9A7B;  // Wiggler's segments
        if (bank == 0x70 && lo < 0x0800) return 0x41C000 + lo;                           // the save file
        if (bank == 0x70 && lo < 0x2800) return 0x41A000 + lo - 0x0800;
        return a;
    }

    /// <summary>
    /// The SA-1 form of a run of 65816 code entered with the given M/X: every absolute and long RAM
    /// operand moved, the length of every instruction kept. Direct-page operands keep their byte
    /// (D is $3000 on SA-1). A write to the direct-page register is added to
    /// <paramref name="problems"/> (as "+offset: why") and left as it was.
    ///
    /// What a sweep cannot see is not moved: a RAM address built from immediates (`LDA #$7E : STA
    /// $02`, a `#$1BE6` pointer), data between the instructions, an M/X change on a branch it did not
    /// follow. Lunar Magic's SA-1 code changes all three, which is why the prep carries LM's own SA-1
    /// bytes rather than this function's output for LM's routines (reference/SA1_PORT.md).
    /// </summary>
    public byte[] Code(byte[] code, bool m8, bool x8, List<string>? problems = null)
    {
        var o = (byte[])code.Clone();
        foreach (var i in Disasm.Walk(code, m8, x8))
        {
            int at = i.At + 1, v = 0;
            for (int k = 0; k < i.OperandBytes; k++) v |= code[at + k] << (8 * k);
            switch (i.Mode)
            {
                // A jump's or a push's 16-bit operand is not a RAM address; (abs) and [abs] read a
                // pointer from bank 0, which is.
                case "abs" or "absx" or "absy" when i.Mn is not ("JMP" or "JSR" or "PEA"):
                case "absind" or "absindl":
                    if (v < 0x2000) Put(o, at, Abs(v), 2);
                    break;
                case "long" or "longx" when i.Mn is not ("JSL" or "JML"):
                    Put(o, at, Long(v), 3);
                    break;
            }
            if (i.Mn is "TCD" or "PLD") problems?.Add($"+{i.At:X}: {i.Mn} sets the direct page, which the pack keeps at $3000");
        }
        return o;
    }

    private static void Put(byte[] o, int at, int v, int n)
    {
        for (int k = 0; k < n; k++) o[at + k] = (byte)(v >> (8 * k));
    }
}
