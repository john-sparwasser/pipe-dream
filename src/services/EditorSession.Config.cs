namespace PipeDream.Services;

// EditorSession — what reaches the user's settings or the process rather than the ROM: the
// vanilla ROM, update checks, per-user view preferences, and the command line. The rest of the
// class: EditorSession.cs and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- vanilla ROM ----
    /// <summary>Remember the user's verified vanilla ROM (used to prep new project bases).</summary>
    public void SetVanillaRom(string path)
    {
        Config.VanillaRomPath = path;
        SaveConfig();
    }

    /// <summary>
    /// Whether a ROM is the known-good vanilla image, and what that means for the user. The hash
    /// is taken headerless, so a copier-header copy of the same ROM still verifies. A mismatch is
    /// a warning rather than a refusal: an LM-prepared base works fully, it just has to be the
    /// exact file collaborators use, because the project pins its hash either way.
    /// </summary>
    public static string DescribeRom(string? path)
    {
        if (path is null || !File.Exists(path)) return "";
        try
        {
            return RomHash.HeaderlessSha256File(path) == RomHash.VanillaUsSha256
                ? "Verified: vanilla Super Mario World (U). A base copy is prepared automatically "
                + "for full editing — Map16, tile placement, palettes and sprites."
                : "Warning: not the known vanilla US ROM. It is used as-is (an LM-prepared base "
                + "works fully), and collaborators will need this exact file.";
        }
        catch (Exception e) when (FileProblem.IsFile(e))
        {
            var problem = FileProblem.From(e, "read the ROM", path);   // inline in the dialog, so prose not a title
            return $"{problem.Why} {problem.Next}";
        }
        catch (Exception e) { return "Could not read file: " + e.Message; }
    }

    // ---- updates ----
    // The UI cannot reach the config or the filesystem (ArchitectureTests enforces it), so the
    // whole update path is exposed here: settings, the check, the download, the install.

    /// <summary>Whether startup asks GitHub about newer releases.</summary>
    public bool CheckForUpdates
    {
        get => Config.CheckForUpdates;
        set { Config.CheckForUpdates = value; SaveConfig(); }
    }

    /// <summary>
    /// A newer release, or null for nothing to offer. <paramref name="userAsked"/> forces the
    /// request; otherwise it is rate-limited to once a day and honours the setting, so calling
    /// this on every startup is free.
    /// </summary>
    public async Task<UpdateInfo?> FindUpdate(bool userAsked, CancellationToken ct = default)
    {
        if (!UpdateCheck.Supported)
        {
            if (userAsked) Report("updates: no build is published for this platform yet");
            return null;
        }
        if (!UpdateCheck.Due(userAsked, Config.CheckForUpdates, Config.LastUpdateCheckUtc, DateTime.UtcNow))
            return null;

        // Stamped before the result is known: a check that went out counts, or a machine that is
        // offline for a week would retry on every single launch.
        Config.LastUpdateCheckUtc = DateTime.UtcNow;
        SaveConfig();

        return await UpdateCheck.Latest(UpdateCheck.Current, Config.SkippedUpdate,
                                        OperatingSystem.IsWindows(), ct);
    }

    /// <summary>Never offer this version again. A later one still gets through.</summary>
    public void SkipUpdate(UpdateInfo u)
    {
        Config.SkippedUpdate = u.Display;
        SaveConfig();
    }

    /// <summary>The running build's version, for the about/update dialog.</summary>
    public string CurrentVersion => UpdateCheck.Current.ToString(3);

    public Task<string> DownloadUpdate(UpdateInfo u, IProgress<double>? progress = null,
                                       CancellationToken ct = default)
        => UpdateCheck.Download(u, progress, ct);

    /// <summary>Start the install. The caller must close the app immediately after a null
    /// return — see <see cref="UpdateCheck.Apply"/>.</summary>
    public string? ApplyUpdate(string downloadedFile) => UpdateCheck.Apply(downloadedFile);

    /// <summary>True on the very first run, before the config knows where a vanilla ROM lives.</summary>
    public bool NeedsVanillaRom => Config.VanillaRomPath is null;

    /// <summary>How the GFX browser lists files ("names", "list" or "cards"), remembered
    /// per user like the update-check switch above.</summary>
    public string GfxBrowserView
    {
        get => Config.GfxBrowserView;
        set { Config.GfxBrowserView = value; SaveConfig(); }
    }

    // ---- command line ----
    // The ROM tools run in the same executable as the editor. They are storage-layer work, so
    // they are reached through here rather than from the window: the presentation layer stays
    // unable to call storage, and the process entry point stays a composition root.

    /// <summary>The switch that forces command-line mode with no command to run — it prints the
    /// available ones. A recognised command implies it, so both spellings work.</summary>
    public const string HeadlessSwitch = "--headless";

    /// <summary>Whether these arguments mean "do not open a window".</summary>
    public static bool IsCommandLine(string[] args)
        => args.Contains(HeadlessSwitch) || DebugCommands.Names.Any(args.Contains);

    /// <summary>Run the ROM command in <paramref name="args"/> and return its exit code, or
    /// print what is available when only the switch was given.</summary>
    public static int RunCommandLine(string[] args)
    {
        if (DebugCommands.TryDispatch(args) is { } code) return code;
        Console.Error.WriteLine("pipe-dream — ROM tools\n");
        Console.Error.WriteLine("Commands:");
        foreach (string name in DebugCommands.Names) Console.Error.WriteLine("  " + name);
        Console.Error.WriteLine($"\nWithout {HeadlessSwitch} or one of these, the editor opens.");
        return 1;
    }
}
