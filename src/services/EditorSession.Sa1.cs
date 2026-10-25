namespace PipeDream.Services;

// EditorSession — SA-1: the ASM ▸ SA-1 switch (reference/SA1_PORT.md). SA-1 is a build switch: the
// session ROM, rendering and every reader stay LoROM, and only the build applies the pack
// (RomBuilder.InstallTarget). The switch is a pinned reference in ProjectFile.BuiltinPatches;
// switching it marks the project dirty and is not undoable, as a patch's on/off is not.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    /// <summary>Whether the project builds for SA-1.</summary>
    public bool Sa1On => Project?.Data.Sa1 is not null;

    /// <summary>Why SA-1 cannot be switched on for this project, or null when it can. The prep's SA-1
    /// form is generated against one prep version, and a base used as provided has no prep at all.</summary>
    public string? Sa1Blocker => Project is null ? "no project open"
        : Project.Data.BaseRom.PrepVersion == 0 ? "this project's base ROM is used as provided, and SA-1 needs a base Pipe Dream prepared"
        : Project.Data.BaseRom.PrepVersion != LmSa1Forms.PrepVersion ? $"SA-1 needs the base at prep v{LmSa1Forms.PrepVersion} (File → Upgrade base)"
        : !Asar.Available ? "SA-1 needs the Asar library, which was not found"
        : null;

    /// <summary>Switch SA-1 on or off. On downloads the pack the first time (once per machine) and
    /// pins its version; off keeps the pin, so switching back builds what it built before. Returns
    /// the status, which says what failed.</summary>
    public async Task<string> SetSa1(bool on)
    {
        if (Project is null) return Report2("no project open");
        var list = Project.Data.BuiltinPatches;
        var entry = list.FirstOrDefault(b => b.Id == BuiltinPatches.Sa1.Id);
        if (!on)
        {
            if (entry is not { Enabled: true }) return Report2("SA-1 is already off");
            entry.Enabled = false;
            Project.MarkDirty();
            return Report2("SA-1 off: the next build is plain LoROM");
        }
        if (Sa1Blocker is { } why) return Report2("SA-1 not switched on: " + why);
        if (entry is { Enabled: true }) return Report2("SA-1 is already on");
        var pack = entry is null ? BuiltinPatches.Sa1 : BuiltinPatches.Find(entry.Id, entry.Version);
        if (pack is null) return Report2($"SA-1 not switched on: this project pins SA-1 Pack v{entry!.Version}, which this version of Pipe Dream does not know");
        if (await BuiltinPatches.Install(pack) is { } err) return Report2("SA-1 not switched on: " + err);
        if (entry is null) list.Add(new() { Id = pack.Id, Version = pack.Version, Sha256 = pack.Sha256 });
        else entry.Enabled = true;
        Project.MarkDirty();
        return Report2($"SA-1 on: the next build applies the {pack.Name} v{pack.Version}");
    }
}
