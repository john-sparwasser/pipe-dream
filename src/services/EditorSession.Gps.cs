namespace PipeDream.Services;

// EditorSession — custom blocks: importing a GPS folder into the project.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>Copy a GPS folder into the project's gps/ (GpsInsert.Import). Returns the status.</summary>
    public string ImportGps(string folder)
    {
        if (Project is null) return "no project open";
        Guard("import the GPS folder", () =>
        {
            string? problem = GpsInsert.Import(folder, Project.GpsFolder);
            int tiles = problem is null ? GpsInsert.ParseList(File.ReadAllText(Path.Combine(Project.GpsFolder, "list.txt"))).Count : 0;
            Report(problem is null ? $"imported GPS folder: {tiles} tile(s) in list.txt" : "GPS import: " + problem);
        }, Project.GpsFolder);
        return Status;
    }
}
