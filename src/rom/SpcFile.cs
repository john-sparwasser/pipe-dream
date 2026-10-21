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

    private static string Text(byte[] d, int at, int len)
        => Encoding.ASCII.GetString(d, at, len).TrimEnd('\0', ' ');
}
