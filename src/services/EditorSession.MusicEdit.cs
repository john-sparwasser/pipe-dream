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
