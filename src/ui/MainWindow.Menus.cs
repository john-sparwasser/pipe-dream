using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PipeDream.Ui;

/// <summary>
/// The menu bar's File and Help items: projects, saving, building, the emulator, the base
/// ROM, updates — and the native file pickers they open. Edit and View items live with what
/// they act on (undo in MainWindow.axaml.cs, the level toggles in MainWindow.Level.cs).
/// </summary>
public partial class MainWindow
{
    private MenuItem recentMenu = null!, upgradePrepItem = null!, spriteOverlayItem = null!,
                     animateItem = null!, runEmulatorItem = null!, layer3PreviewItem = null!;

    /// <summary>Menu items whose state follows the session, and the shortcut captions.</summary>
    private void WireMenus()
    {
        // ---- menu items that depend on state ----
        recentMenu = this.GetControl<MenuItem>("RecentMenu");
        upgradePrepItem = this.GetControl<MenuItem>("UpgradePrepItem");
        runEmulatorItem = this.GetControl<MenuItem>("RunEmulatorItem");
        spriteOverlayItem = this.GetControl<MenuItem>("SpriteOverlayItem");
        animateItem = this.GetControl<MenuItem>("AnimateItem");
        layer3PreviewItem = this.GetControl<MenuItem>("Layer3PreviewItem");
        if (session.PreviewLayer3) layer3PreviewItem.Icon = new TextBlock { Text = "✓" };
        SetAnimating(true);             // tiles animate as the game does; View ▸ Animate tiles stops it
        // Rebuilt when the menu opens rather than kept in sync: the recent list changes behind
        // this window's back (a project opened elsewhere in the session reorders it), and pruning
        // entries whose files have gone needs a disk check that has no business running per frame.
        this.GetControl<Menu>("MainMenu").Opened += (_, _) => RefreshFileMenu();

        // A HotKey is what FIRES; the caption a MenuItem draws is its InputGesture, and Avalonia
        // derives neither from the other. One pass so every shortcut the menu shows is one that
        // works — and a new item only needs a HotKey.
        foreach (var item in this.GetLogicalDescendants().OfType<MenuItem>())
            if (item.HotKey is { } hotKey && item.InputGesture is null) item.InputGesture = hotKey;
    }

    /// <summary>Fill in the parts of the File and View menus that depend on state: the recent
    /// list, whether a prep upgrade is available, and the two view checkmarks.</summary>
    private void RefreshFileMenu()
    {
        // Says which emulator F4 will use — the one set, or "emulator" until one is found/chosen.
        runEmulatorItem.Header = $"_Run in {session.EmulatorName ?? "emulator"}";
        var items = new List<MenuItem>();
        foreach (string path in session.RecentProjects)
        {
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await OpenProjectPath(path);
            items.Add(item);
        }
        recentMenu.ItemsSource = items;
        recentMenu.IsEnabled = items.Count > 0;

        upgradePrepItem.Header = $"Upgrade base to prep v{EditorSession.PrepVersion}";
        upgradePrepItem.IsEnabled = session.CanUpgradeBasePrep;
        spriteOverlayItem.Icon = session.ShowSprites ? new TextBlock { Text = "✓" } : null;
        animateItem.Icon = animate is null ? null : new TextBlock { Text = "✓" };
    }

    // ---- projects ----

    private async void OnOpenProject(object? sender, RoutedEventArgs e) => await OpenProjectFlow();

    private async Task OpenProjectFlow()
    {
        if (await PickFile("Open project", ProjectType) is not { } p) return;
        await OpenProjectPath(p);
    }

    /// <summary>
    /// Open a project, offering the recovery flow when its base ROM is missing or mismatched. That
    /// is not an error path but the NORMAL one for someone else's project: a .pdp is shareable on
    /// its own and the base ROM copy beside it deliberately is not.
    /// </summary>
    private async Task OpenProjectPath(string pdp)
    {
        if (session.OpenProject(pdp))
        {
            AdoptSession();
            levelBox.SelectedIndex = session.LevelNum;
            return;
        }
        if (session.PendingBaseProblem is null) return;      // a real failure, not a missing base

        while (session.PendingBaseProblem is { } problem)
        {
            var dlg = new LocateBaseWindow(session.PendingProjectName ?? "project", problem,
                                           session.PendingBaseDescription);
            await dlg.ShowDialog(this);
            if (dlg.Located is not { } rom) { session.CancelPendingOpen(); return; }
            if (session.AdoptPendingBase(rom) is null) break;
        }
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e) => await NewProjectFlow();

    /// <summary>File ▸ New Project from ROM: the same flow, but the base is always asked for.
    /// With a vanilla ROM configured, New Project takes it silently — the right default for a
    /// fresh hack, and no way at all to bring in an .smc you already have.</summary>
    private async void OnNewProjectFromRom(object? sender, RoutedEventArgs e) => await NewProjectFlow(pickRom: true);

    /// <summary>New project: pick the folder to create it in, then the base ROM. A verified
    /// vanilla base is prepped automatically, which is why no "prep?" question is asked.</summary>
    private async Task NewProjectFlow(bool pickRom = false)
    {
        await SettleBeforeNativeDialog();
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the new project", AllowMultiple = false,
        });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } folder) return;

        string? baseRom = !pickRom && EditorSession.FileExists(session.VanillaRomPath)
            ? session.VanillaRomPath : await PickFile("Choose the base ROM", RomType);
        if (baseRom is null) return;

        session.NewProject(EditorSession.ProjectFolderFor(folder, baseRom), baseRom);
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    /// <summary>Debug ▸ Clear project edits: wipe the .pdp back to its base-ROM pin, behind a
    /// confirm — the fast path to retesting a flow from a clean project.</summary>
    private async void OnClearProject(object? sender, RoutedEventArgs e)
    {
        if (session.ProjectName is not { } name) return;
        var dlg = new ConfirmWindow("Clear project edits",
            $"Discard every edit in '{name}'? Levels, Map16, GFX, palettes and entrances all "
            + "reset to the base ROM. This cannot be undone.", "Clear");
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed || !session.ClearProjectEdits()) return;
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    // ---- save, build, run ----

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        CommitGfxFloat();                    // a paste still adrift belongs in what gets saved
        session.Save();
        gfxSave.IsEnabled = session.GfxDirty;    // Ctrl+S saved the pixels too
        UpdateTitle();
    }

    /// <summary>
    /// Build, and SAY what happened. The status was going nowhere at all — nothing in the window
    /// shows session.Status — so a build looked identical whether it wrote a ROM or refused to,
    /// and every "stays editor-only" warning the builder raises was invisible. That is the worst
    /// possible failure for a feature the base cannot carry: the edit is in the project, the
    /// build drops it on the floor, and the only way to find out was to run the game.
    /// </summary>
    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        session.Build();
        UpdateTitle();
        await ConfirmWindow.Notice("Build ROM", session.Status).ShowDialog(this);
    }

    private async void OnExportBps(object? sender, RoutedEventArgs e)
    {
        session.ExportBps();
        UpdateTitle();
        await new ConfirmWindow("Export BPS", session.Status, "OK").ShowDialog(this);
    }

    /// <summary>F4, as in Lunar Magic: build and run. Problems come up in a dialog because
    /// the status line is easy to miss when nothing visibly happened.</summary>
    private async void OnRunEmulator(object? sender, RoutedEventArgs e)
    {
        var problem = session.RunInEmulator();
        UpdateTitle();
        RefreshFileMenu();                       // auto-found emulator now has a name
        if (problem is not null) await new ConfirmWindow("Run in emulator", problem, "OK").ShowDialog(this);
    }

    private async void OnSetVanilla(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Choose your verified vanilla SMW ROM", RomType) is not { } p) return;
        session.SetVanillaRom(p);
    }

    private async void OnSetEmulator(object? sender, RoutedEventArgs e)
    {
        var exe = new FilePickerFileType("Emulator") { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] };
        if (await PickFile("Choose the emulator for Run in emulator (F4)", exe) is not { } p) return;
        session.SetEmulator(p);
        RefreshFileMenu();
    }

    /// <summary>Dev only: build, then hand the result to Lunar Magic — the check that a base
    /// we prepped is one LM can actually open. A failure has nowhere else to appear (the status
    /// line is not on screen), so it gets said out loud.</summary>
    private async void OnOpenLunarMagic(object? sender, RoutedEventArgs e)
    {
        if (session.OpenInLunarMagic() is not { } problem) { UpdateTitle(); return; }
        UpdateTitle();
        await new ConfirmWindow("Lunar Magic", problem, "OK").ShowDialog(this);
    }

    private void OnUpgradePrep(object? sender, RoutedEventArgs e)
    {
        session.UpgradeBasePrep();
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private async void OnRomInfo(object? sender, RoutedEventArgs e)
    {
        var dlg = new RomInfoWindow(session.RomInfo());
        await dlg.ShowDialog(this);
    }

    /// <summary>Course Bot: named entry levels, managed in a modal. Opening one jumps the
    /// editor to its slot through the level box, which drives the whole ShowLevel flow.</summary>
    private async void OnCourseBot(object? sender, RoutedEventArgs e)
    {
        if (!session.HasProject)
        {
            return;
        }
        var dlg = new CourseBotWindow(session);
        await dlg.ShowDialog(this);
        if (dlg.Picked is { } lv && lv != levelBox.SelectedIndex) levelBox.SelectedIndex = lv;
        else AdoptSession();          // a delete may have reverted the level on screen
        UpdateTitle();
    }

    /// <summary>Help → Check for updates. Shows the update window when there is one; with no status
    /// line to write to, "you are up to date" and a failed check both pass in silence.</summary>
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await session.FindUpdate(userAsked: true) is { } found)
            {
                await UpdateWindow.Prompt(this, session, found);
                return;
            }
        }
        catch { /* Help ▸ Check for updates: a failed check is nothing the user can act on */ }
    }

    /// <summary>A file the session could not read or write, in front of the user: which file,
    /// why, and what to try. Posted rather than shown inline because it can fire from inside
    /// another handler or an open dialog, and a modal opened re-entrantly there is the freeze the
    /// native-picker fix documents. Before the window is up the status line has it.</summary>
    internal void ShowProblem(FileProblem p) => Dispatcher.UIThread.Post(async () =>
    {
        if (!IsVisible) return;
        await ConfirmWindow.Notice(p.Title, p.Message).ShowDialog(this);
    });

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // ---- native file pickers ----

    private static FilePickerFileType RomType => new("SNES ROM") { Patterns = ["*.smc", "*.sfc"] };
    private static FilePickerFileType ProjectType => new("pipe-dream project") { Patterns = ["*.pdp"] };

    private async Task<string?> PickFile(string title, FilePickerFileType type)
    {
        await SettleBeforeNativeDialog();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, FileTypeFilter = [type],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Where to WRITE a file, the mirror of <see cref="PickFile"/>. Same settle-first
    /// rule: the nested message loop a native dialog runs will hang the app if it starts inside
    /// the input event that asked for it.</summary>
    private async Task<string?> PickSaveFile(string title, string suggested, FilePickerFileType type)
    {
        await SettleBeforeNativeDialog();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, FileTypeChoices = [type],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// Let the input event that asked for a native dialog finish before the dialog's nested
    /// message loop starts. A file picker opened straight from a MenuItem click FROZE the app
    /// on Windows: the menu popup is still tearing down (capture held, popup closing) when the
    /// picker's modal loop takes over the thread, and neither side can finish — the picker
    /// window is never shown and the main window stops answering input. Yielding to Background
    /// priority runs everything the click queued (popup close, capture release, layout) first.
    /// Reproduced with File → New Project…; a picker from a plain Button never hangs, which is
    /// why the startup chooser and the first-run Browse were immune.
    /// </summary>
    private static async Task SettleBeforeNativeDialog()
        => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
               static () => { }, Avalonia.Threading.DispatcherPriority.Background);
}
