using System.Text;

namespace PipeDream.Music;

/// <summary>Our SPC700 engine (data/musicdriver/main.asm), assembled with Asar's spc700 mode onto a scratch buffer: the bytes that
/// go at ARAM $0400 and its labels.</summary>
public static class MusicEngine
{
    private static readonly string VirtualDir = Path.Combine(Path.GetTempPath(), "pipedream-music");

    public static string Source(string name)
    {
        using var s = typeof(MusicEngine).Assembly.GetManifestResourceStream("musicdriver/" + name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The engine's bytes and labels, or the assembler's errors.</summary>
    public static (byte[] Code, IReadOnlyDictionary<string, int> Labels, string[] Errors) Assemble()
    {
        var rom = Rom.FromBytes(new byte[0x80000]);
        string path = Path.Combine(VirtualDir, "main.asm");
        var r = Asar.Patch(rom, path, new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes(Source("main.asm")) });
        if (!r.Ok) return ([], new Dictionary<string, int>(), r.Errors);
        int end = r.Labels["EngineEnd"];
        return (rom.Data[..(end - MusicLayout.EngineBase)], r.Labels, []);
    }
}
