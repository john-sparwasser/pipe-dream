using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace PipeDream.Ui;

/// <summary>
/// Phase-1 shell. Deliberately the same paradigm as the ImGui editor: the CANVAS is the
/// editor and fills the window, a left palette drawer feeds it, and other editors are canvas
/// MODES reached from the header — never extra panels competing for the drawer.
///
/// Still a shell: it renders and navigates real levels, but does not edit. Painting, undo and
/// the project layer arrive with the phases that port them.
///
/// Controls are resolved by name rather than through XAML-generated fields — explicit, and
/// it does not depend on the code generator having run.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LevelBitmap bitmap = new();
    private readonly EditorSession session = new();
    private Rom? rom;
    private LevelScene? scene;
    private int levelNum = 0x105;

    private LevelEdit? edit;

    private LevelView canvas = null!;
    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!, tileZoom = null!;
    private TextBlock status = null!, hover = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!;
    private Grid split = null!;
    private ToggleButton modeLevel = null!, modeMap16 = null!, modeGfx = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        canvas = this.GetControl<LevelView>("Canvas");
        palette = this.GetControl<Map16PaletteView>("Palette");
        levelBox = this.GetControl<ComboBox>("LevelBox");
        bankBox = this.GetControl<ComboBox>("BankBox");
        zoomSlider = this.GetControl<Slider>("ZoomSlider");
        tileZoom = this.GetControl<Slider>("TileZoom");
        split = this.GetControl<Grid>("Split");
        status = this.GetControl<TextBlock>("Status");
        hover = this.GetControl<TextBlock>("Hover");
        zoomLabel = this.GetControl<TextBlock>("ZoomLabel");
        selLabel = this.GetControl<TextBlock>("SelLabel");
        drawer = this.GetControl<Border>("Drawer");
        modeLevel = this.GetControl<ToggleButton>("ModeLevel");
        modeMap16 = this.GetControl<ToggleButton>("ModeMap16");
        modeGfx = this.GetControl<ToggleButton>("ModeGfx");

        canvas.Source = bitmap;
        canvas.PointerMoved += (_, _) => UpdateHover();

        // RIGHT drag stamps the drawer's tile, one undo entry per stroke (ImGui parity: the
        // left button belongs to selection).
        canvas.CellPainted += (_, c) =>
        {
            if (edit is null) return;
            if (edit.TilePlacementBlocked is { } why) { status.Text = why; return; }
            if (edit.Paint(c.X, c.Y, palette.Selected)) PushDirty();
        };
        canvas.StrokeEnded += (_, _) =>
        {
            edit?.EndStroke();   // cells become DM16 objects here; the grid is re-rendered
            PushDirty();
            UpdateStatus();
        };
        canvas.DuplicateRequested += (_, c) =>
        {
            if (edit?.DuplicateSelected(c.X, c.Y) == true) { PushDirty(); UpdateStatus(); }
        };
        canvas.DeleteRequested += (_, _) =>
        {
            if (edit?.DeleteSelected() == true) { PushDirty(); UpdateStatus(); }
        };
        canvas.GrabRequested += (_, g) =>
        {
            if (edit is null) return;
            var (tiles, w, h) = edit.GrabTiles(g.X, g.Y, g.W, g.H);
            brush = (tiles, w, h);
            status.Text = $"grabbed {w}x{h} tiles as the brush";
        };
        canvas.SelectionChanged += (_, _) => UpdateStatus();

        zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            canvas.Zoom = zoomSlider.Value;
            zoomLabel.Text = $"{zoomSlider.Value:0}x";
            canvas.InvalidateVisual();
            canvas.InvalidateMeasure();
        };
        zoomLabel.Text = "2x";

        bankBox.SelectionChanged += (_, _) =>
        {
            palette.Bank = Math.Max(0, bankBox.SelectedIndex);
            palette.InvalidateVisual();
        };
        palette.SelectionChanged += (_, tile) => selLabel.Text = $"0x{tile:X4}";

        tileZoom.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            palette.Zoom = tileZoom.Value;
            palette.InvalidateMeasure();
            palette.InvalidateVisual();
            FitDrawerToPalette();
        };

        for (int i = 0; i < Rom.LevelCount; i++) levelBox.Items.Add($"${i:X3}");
        levelBox.SelectionChanged += OnLevelChanged;

        drawer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) OnDrawerVisibilityChanged();
        };

        palette.Zoom = tileZoom.Value;
        FitDrawerToPalette();

        KeyDown += OnWindowKeyDown;
        // Wheel scrolls the level sideways (Shift: vertically) — the canvas decides, the
        // scroll viewer applies, since it owns the offsets.
        canvas.ScrollRequested += (_, d) =>
        {
            var sv = this.GetControl<ScrollViewer>("CanvasScroll");
            sv.Offset = new Vector(Math.Max(0, sv.Offset.X + d.Dx), Math.Max(0, sv.Offset.Y + d.Dy));
        };

        string? path = Program.RomPath is { } p && File.Exists(p) ? p
                     : File.Exists(DefaultRom()) ? DefaultRom() : null;
        if (path is not null) LoadRom(path);
    }

    private static string DefaultRom() => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private void LoadRom(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!session.OpenRom(path)) { status.Text = session.Status; return; }
        composeMs = sw.Elapsed.TotalMilliseconds;
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    /// <summary>Pull the window's views onto whatever the session currently holds. One path
    /// for every way the session can change — opening a ROM, opening a project, switching
    /// level — so a new entry point cannot forget half the refresh.</summary>
    private void AdoptSession()
    {
        rom = session.Rom;
        scene = session.Scene;
        edit = session.Edit;
        levelNum = session.LevelNum;
        canvas.Edit = edit;
        canvas.Vertical = rom is not null && scene is not null && rom.IsVerticalMode(scene.Level.Header.LevelMode);
        if (scene is null || rom is null) return;

        bitmap.SetImages(scene.Phases, scene.Width, scene.Height, 0);
        canvas.InvalidateMeasure();
        canvas.InvalidateVisual();

        var (px, w, h) = scene.Sheet();
        palette.SetSheet(px, w, h, rom.Map16TileCount);
        UpdateStatus();
        UpdateTitle();
    }

    private void ShowLevel(int num)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.ShowLevel(num);
        composeMs = sw.Elapsed.TotalMilliseconds;
        AdoptSession();
    }

    private void UpdateTitle()
        => Title = session.Project is { } p
            ? $"pipe-dream — {p.Name}{(session.HasUnsavedWork ? " *" : "")}"
            : session.RomPath is { } r ? $"pipe-dream — {Path.GetFileName(r)} (no project)"
            : "pipe-dream";

    private double composeMs;

    /// <summary>Multi-tile stamp brush from a Ctrl+drag grab; null = the drawer's single tile.
    /// ponytail: stamping still places the selected tile — wiring the multi-tile brush through
    /// Dm16Saver.FromBrush is the next step and needs the brush preview with it.</summary>
    private (ushort[] Tiles, int W, int H)? brush;

    /// <summary>Global keys, matching the ImGui editor: Ctrl+Z undo, Ctrl+Shift+Z redo, and
    /// Esc leaving a non-Level canvas mode before it touches selection.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            bool ok = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? edit?.Redo() == true
                                                                 : edit?.Undo() == true;
            if (ok) { PushDirty(); UpdateStatus(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (modeLevel.IsChecked != true) OnMode(modeLevel, new RoutedEventArgs());
            else if (edit is { Selection.Count: > 0 })
            { edit.Selection.Clear(); canvas.InvalidateVisual(); UpdateStatus(); }
            e.Handled = true;
        }
    }

    private void UpdateStatus()
    {
        if (scene is null) return;
        // Object count comes from the EDIT, not the parsed level: painting appends objects,
        // and watching that number move is the clearest sign the stroke really became data.
        int objs = edit?.Objects.Count ?? scene.Level.Objects.Count;
        string undoNote = edit is { UndoDepth: > 0 } ? $"   {edit.UndoDepth} edit(s)" : "";
        status.Text = $"level ${levelNum:X3}   {scene.Width}x{scene.Height}px   " +
                      $"{objs} objects   composed in {composeMs:F0}ms{undoNote}";
    }

    private void UpdateHover()
    {
        if (canvas.HoverCell is { } c && scene is not null)
        {
            int tile = scene.Grid.Get(c.X, c.Y);
            hover.Text = tile == Map16Grid.Empty
                ? $"({c.X,3},{c.Y,2})  empty"
                : $"({c.X,3},{c.Y,2})  tile 0x{tile:X3}";
        }
        else hover.Text = "";
    }

    /// <summary>Push the cells an edit touched into the bitmap. The composition already
    /// happened in the scene's phase images, so this is only the copy — and because the
    /// bitmap takes whole images, a repaint is one 13MB push rather than per-cell blits.
    /// If that ever shows up in a profile, LevelBitmap grows a dirty-rect upload.</summary>
    private void PushDirty()
    {
        if (scene is null || edit is null) return;
        if (edit.TakeDirty().Count == 0) return;
        scene.RedrawOverlay();      // sprites straddle cells; a per-cell recompose clips them
        bitmap.SetImages(scene.Phases, scene.Width, scene.Height, 0);
        canvas.InvalidateVisual();
    }

    // ---- handlers referenced from XAML ----

    private static FilePickerFileType RomType => new("SNES ROM") { Patterns = ["*.smc", "*.sfc"] };
    private static FilePickerFileType ProjectType => new("pipe-dream project") { Patterns = ["*.pdp"] };

    private async Task<string?> PickFile(string title, FilePickerFileType type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, FileTypeFilter = [type],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void OnOpenRom(object? sender, RoutedEventArgs e)
    {
        // A real native file dialog, which ImGui cannot do — it draws its own.
        if (await PickFile("Open SMW ROM", RomType) is { } p) LoadRom(p);
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Open project", ProjectType) is not { } p) return;
        session.OpenProject(p);
        status.Text = session.Status;
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    /// <summary>New project: pick the folder to create it in, then the base ROM. A verified
    /// vanilla base is prepped automatically, which is why no "prep?" question is asked.</summary>
    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the new project", AllowMultiple = false,
        });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } folder) return;

        string? baseRom = session.Config.VanillaRomPath is { } v && File.Exists(v)
            ? v : await PickFile("Choose the base ROM", RomType);
        if (baseRom is null) return;

        // Project.Create refuses to overwrite an existing base, so give it its own folder and
        // step the name until one is free rather than failing on the second project.
        string stem = Path.GetFileNameWithoutExtension(baseRom) + "-project";
        string target = Path.Combine(folder, stem);
        for (int n = 2; Directory.Exists(target); n++) target = Path.Combine(folder, $"{stem}-{n}");

        session.NewProject(target, baseRom);
        status.Text = session.Status;
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        status.Text = session.Save();
        UpdateTitle();
    }

    private void OnBuild(object? sender, RoutedEventArgs e)
    {
        status.Text = session.Build();
        UpdateTitle();
    }

    private void OnExportBps(object? sender, RoutedEventArgs e)
    {
        status.Text = session.ExportBps();
        UpdateTitle();
    }

    private async void OnSetVanilla(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Choose your verified vanilla SMW ROM", RomType) is not { } p) return;
        session.Config.VanillaRomPath = p;
        session.Config.Save();
        status.Text = "vanilla ROM set — new projects will prep from it";
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (edit?.Undo() == true) { PushDirty(); UpdateStatus(); }
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        if (edit?.Redo() == true) { PushDirty(); UpdateStatus(); }
    }

    private void OnTogglePalette(object? sender, RoutedEventArgs e) => drawer.IsVisible = !drawer.IsVisible;

    /// <summary>Hiding the drawer has to collapse its grid column too, or the canvas keeps
    /// its old width and the space just goes blank. Driven off the visibility property rather
    /// than the menu handler, so any caller gets the same behaviour — the width the user
    /// dragged the splitter to is remembered and restored.</summary>
    private void OnDrawerVisibilityChanged()
    {
        var cols = split.ColumnDefinitions;
        if (drawer.IsVisible)
        {
            cols[0].Width = new GridLength(drawerWidth);
            cols[1].Width = GridLength.Auto;
        }
        else
        {
            if (cols[0].Width.IsAbsolute && cols[0].Width.Value > 0) drawerWidth = cols[0].Width.Value;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
        }
        split.InvalidateMeasure();
    }

    private double drawerWidth = DrawerWidthFor(2);

    /// <summary>
    /// Chrome around the palette content inside the drawer: the drawer's right border plus
    /// the scroll viewer's vertical scrollbar, which is always present because the sheet is
    /// 512 rows tall. Without allowing for it the scrollbar sits ON the last tile column.
    /// </summary>
    private const double DrawerChrome = 1 + 18;

    private static double DrawerWidthFor(double tileZoom)
        => Map16PaletteView.ContentWidth(tileZoom) + DrawerChrome;

    /// <summary>Size the drawer to hold a whole row of Map16 tiles. The splitter can still
    /// widen it; this only ever sets the width that stops tiles being cut off.</summary>
    private void FitDrawerToPalette()
    {
        drawerWidth = DrawerWidthFor(palette.Zoom);
        var col = split.ColumnDefinitions[0];
        col.MinWidth = drawerWidth;
        if (drawer.IsVisible && (!col.Width.IsAbsolute || col.Width.Value < drawerWidth))
            col.Width = new GridLength(drawerWidth);
    }

    private void OnToggleGrid(object? sender, RoutedEventArgs e)
    {
        canvas.ShowGrid = !canvas.ShowGrid;
        canvas.InvalidateVisual();
    }

    private void OnLevelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (levelBox.SelectedIndex < 0) return;
        levelNum = levelBox.SelectedIndex;
        ShowLevel(levelNum);
    }

    // Radio behaviour without a group: exactly one canvas mode is active.
    private void OnMode(object? sender, RoutedEventArgs e)
    {
        foreach (var b in new[] { modeLevel, modeMap16, modeGfx })
            b.IsChecked = ReferenceEquals(b, sender);
        status.Text = ReferenceEquals(sender, modeLevel)
            ? $"level ${levelNum:X3}"
            : $"{(sender as ToggleButton)?.Content} mode — not ported yet (canvas mode, same window)";
    }
}
