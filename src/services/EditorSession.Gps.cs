namespace PipeDream.Services;

// EditorSession — custom blocks: importing a GPS folder into the project.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>Import a GPS folder: its assembly into the project's gps/ (GpsInsert.Import), its
    /// list.txt into the project — each tile's block into <see cref="ProjectFile.Blocks"/>,
    /// replacing what was there, and each acts-like the list gives into the Map16 editor, where
    /// it is an ordinary acts-as edit (one undo step per value). Returns the status.</summary>
    public string ImportGps(string folder)
    {
        if (Project is null) return "no project open";
        Guard("import the GPS folder", () =>
        {
            string? problem = GpsInsert.Import(folder, Project.GpsFolder, out var list);
            if (problem is not null) { Report("GPS import: " + problem); return; }

            Project.Data.Blocks = list.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("X3"), kv => kv.Value.File);
            var acts = list.Where(kv => kv.Value.ActsLike >= 0).GroupBy(kv => kv.Value.ActsLike).ToList();
            string actsNote = acts.Count == 0 ? ""
                : Map16 is { HasActsAs: true }
                    ? $", {acts.Sum(g => g.Count())} acts-like value(s) into the Map16 editor"
                    : $"; {acts.Sum(g => g.Count())} acts-like value(s) NOT applied (the base has no acts-like table)";
            if (Map16 is { HasActsAs: true })
                foreach (var g in acts) Map16.SetActsAs(g.Select(kv => kv.Key), g.Key);
            Project.MarkDirty();
            Project.Save();
            Report($"imported GPS folder: {list.Count} block tile(s){actsNote}");
        }, Project.GpsFolder);
        return Status;
    }
}
