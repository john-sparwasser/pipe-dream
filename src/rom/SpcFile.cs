using System.Text;

namespace PipeDream;

/// <summary>
/// An .spc file: a snapshot of the SPC700's world (64 KB of ARAM, the 128 DSP registers, the CPU
/// registers) with ID666 text tags. The music compiler's output is compared against the ones
/// AddmusicK writes (AMK_PORT.md), and the interpreter in <see cref="Spc700"/> starts from one.
/// </summary>
public sealed class SpcFile
{
    public const int Size = 0x10200;
    private const string Magic = "SNES-SPC700 Sound File Data v0.30";

    public byte[] Ram = new byte[0x10000];
    public byte[] Dsp = new byte[0x80];
    public int Pc, A, X, Y, Psw, Sp;
    public string Title = "", Game = "", Dumper = "", Comment = "", Artist = "";
    /// <summary>ID666's play time before the fade (0: not given) and the fade's length.</summary>
    public int Seconds, FadeMs = 10_000;

    public static SpcFile Parse(byte[] d)
    {
        if (d.Length < Size || Encoding.ASCII.GetString(d, 0, Magic.Length) != Magic)
            throw new InvalidDataException("not an .spc file");
        var f = new SpcFile
        {
            Pc = d[0x25] | d[0x26] << 8, A = d[0x27], X = d[0x28], Y = d[0x29], Psw = d[0x2A], Sp = d[0x2B],
        };
        if (d[0x23] == 0x1A)                                         // ID666 tags present
        {
            f.Title = Text(d, 0x2E, 32); f.Game = Text(d, 0x4E, 32); f.Dumper = Text(d, 0x6E, 16);
            f.Comment = Text(d, 0x7E, 32); f.Artist = Text(d, 0xB1, 32);
        }
        Array.Copy(d, 0x100, f.Ram, 0, 0x10000);
        Array.Copy(d, 0x10100, f.Dsp, 0, 0x80);
        return f;
    }

    /// <summary>The file: header, registers, ID666 text tags, ARAM, the DSP registers, and the 64 bytes under the boot ROM.</summary>
    public byte[] ToBytes()
    {
        var d = new byte[Size];
        Encoding.ASCII.GetBytes(Magic).CopyTo(d, 0);
        d[0x21] = d[0x22] = d[0x23] = 0x1A;                          // the third: ID666 tags present
        d[0x24] = 30;
        d[0x25] = (byte)Pc; d[0x26] = (byte)(Pc >> 8);
        d[0x27] = (byte)A; d[0x28] = (byte)X; d[0x29] = (byte)Y; d[0x2A] = (byte)Psw; d[0x2B] = (byte)Sp;
        Put(d, 0x2E, 32, Title); Put(d, 0x4E, 32, Game); Put(d, 0x6E, 16, Dumper); Put(d, 0x7E, 32, Comment);
        Put(d, 0xA9, 3, Seconds > 0 ? Seconds.ToString() : ""); Put(d, 0xAC, 5, FadeMs.ToString()); Put(d, 0xB1, 32, Artist);
        Ram.CopyTo(d, 0x100);
        Dsp.CopyTo(d, 0x10100);
        Array.Copy(Ram, 0xFFC0, d, 0x101C0, 0x40);
        return d;
    }

    private static void Put(byte[] d, int at, int len, string s)
    {
        var b = Encoding.ASCII.GetBytes(s);
        Array.Copy(b, 0, d, at, Math.Min(len, b.Length));
    }

    private static string Text(byte[] d, int at, int len)
        => Encoding.ASCII.GetString(d, at, len).TrimEnd('\0', ' ');
}
