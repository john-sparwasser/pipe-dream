namespace PipeDream.Services;

// EditorSession — after the save: building the ROM, opening it in Lunar Magic or an emulator,
// and exporting a patch. Saving itself (Save, StashCurrent) is in EditorSession.cs.
// The rest of the class: the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- building and running ----

    public string Build()
    {
        if (Project is null) return "no project open";
        if (!Guard("save the project", Project.Save, Project.FilePath)) return Status;
        Guard("build the ROM", () =>
        {
            var (status, path) = RomBuilder.Build(Project);
            Report(path is null ? status : $"built {Path.GetFileName(path)} — {status}");
        }, Path.Combine(Project.Folder, "build"));
        return Status;
    }

    /// <summary>
    /// Dev only: build the project and open the result in Lunar Magic. Prep's stated
    /// requirement is that what we stamp is what LM reads (CONTRACT §0 tracks the divergences),
    /// and the loop for checking that was "build, find the file, drag it onto LM" — this is the
    /// same thing in one click. Returns a problem, or null when LM was launched.
    ///
    /// The path comes from the reference-ROM root (PIPEDREAM_SMW_ROOT), which is where the
    /// gated tests already expect Lunar Magic to live — no new setting to keep in sync.
    /// </summary>
    public string? OpenInLunarMagic()
    {
        if (Project is null) return "no project open";
        Build();
        string rom = Path.Combine(Project.Folder, "build", Project.Name + ".smc");
        if (!File.Exists(rom)) return Status;                  // the build already said why
        string exe = ReferenceRoms.Resource(Path.Combine("Lunar Magic", "Lunar Magic.exe"));
        if (!File.Exists(exe)) return $"Lunar Magic is not at {exe}";
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe, rom) { UseShellExecute = true });
        }
        catch (Exception ex) { return $"could not start Lunar Magic: {ex.Message}"; }
        Report($"opened {Path.GetFileName(rom)} in Lunar Magic");
        return null;
    }

    public string? EmulatorPath => Config.EmulatorPath;
    /// <summary>The emulator's name for the menu ("Mesen", "snes9x"), null until one is set.</summary>
    public string? EmulatorName => Config.EmulatorPath is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    public void SetEmulator(string? path)
    {
        Config.EmulatorPath = path;
        SaveConfig();
    }

    /// <summary>File → Run in emulator (F4), Lunar Magic's habit: build, then launch the ROM in
    /// Mesen — the configured one, or the first Mesen.exe found in the usual places (remembered
    /// once found). Not the OS's .smc association: on an LM user's machine that IS Lunar Magic.
    /// Returns a problem, or null when the emulator was launched.</summary>
    public string? RunInEmulator()
    {
        if (Project is null) return "no project open";
        Build();
        string rom = Path.Combine(Project.Folder, "build", Project.Name + ".smc");
        if (!File.Exists(rom)) return Status;                  // the build already said why
        string? emu = Config.EmulatorPath;
        if (emu is not null && !File.Exists(emu)) return $"emulator not found at {emu} — File → Set emulator…";
        if (emu is null && FindMesen() is { } found) { emu = found; SetEmulator(found); }
        if (emu is null) return "no emulator found — File → Set emulator… (Mesen.exe)";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(emu, $"\"{rom}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { return $"could not start {Path.GetFileName(emu)}: {ex.Message}"; }
        Report($"running {Path.GetFileName(rom)} in {Path.GetFileNameWithoutExtension(emu)}");
        return null;
    }

    /// <summary>Mesen.exe where people keep it: next to the user's home, its installer's
    /// %LOCALAPPDATA% folder, Program Files, or anywhere on PATH.</summary>
    private static string? FindMesen()
    {
        string exe = OperatingSystem.IsWindows() ? "Mesen.exe" : "Mesen";
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mesen"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Mesen"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    public string ExportBps()
    {
        if (Project is null) return "no project open";
        Project.Save();
        var (status, _) = RomBuilder.ExportBps(Project, Config.VanillaRomPath);
        Report(status);
        return Status;
    }
}
