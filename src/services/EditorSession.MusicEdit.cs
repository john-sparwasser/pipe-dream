namespace PipeDream.Services;

// EditorSession — a song in the user's own text editor, as a block or patch opens.
public sealed partial class EditorSession
{
    /// <summary>Open a song in the user's text editor (<see cref="OpenBlockExternally"/>): a temp copy,
    /// saves read back on the next <see cref="Tick"/>.</summary>
    public string OpenSongExternally(string path)
    {
        if (SongText(path) is not { } mml) return Report2(Project is null ? "no project open" : $"{path} is not in the project");
        return OpenExternally("song:" + path, Path.GetFileName(path), mml, text => SetSongText(path, text));
    }

    /// <summary>The ARAM a local song may use and how much of it <paramref name="mml"/> takes, as the build would lay it out.</summary>
    public (int Used, int Room, string? Error) SongAram(string mml)
    {
        if (Project is null || Rom is null) return (0, 0, "no project open");
        if (!Asar.Available) return (0, 0, "no ARAM figure without the Asar library");
        return MusicInsert.Aram(Rom, Project.Data, mml);
    }

    /// <summary>The song <paramref name="mml"/> (as it stands in the editor) as an .spc at <paramref name="file"/>.</summary>
    public string ExportSpc(string path, string mml, string file)
    {
        if (Project is null || Rom is null) return Report2("no project open");
        var (spc, errors) = MusicInsert.Spc(Rom, Project.Data, path, mml);
        if (spc is null) return Report2(errors.FirstOrDefault() ?? "the song does not compile");
        File.WriteAllBytes(file, spc.ToBytes());
        return Report2($"wrote {Path.GetFileName(file)}");
    }
}
