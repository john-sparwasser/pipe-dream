using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        // Paint the drawer's selected tile, one undo entry per stroke.
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

        string? path = Program.RomPath is { } p && File.Exists(p) ? p
                     : File.Exists(DefaultRom()) ? DefaultRom() : null;
        if (path is not null) LoadRom(path);
    }

    private static string DefaultRom() => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private void LoadRom(string path)
    {
        try
        {
            rom = Rom.Load(path);
            levelNum = Program.LevelNum;
            levelBox.SelectedIndex = levelNum;      // fires OnLevelChanged → ShowLevel
            if (scene is null) ShowLevel(levelNum); // ...unless the index was already there
        }
        catch (Exception ex) { status.Text = "could not open: " + ex.Message; }
    }

    private void ShowLevel(int num)
    {
        if (rom is null) return;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            scene = LevelScene.Build(rom, num);
            double ms = sw.Elapsed.TotalMilliseconds;

            bitmap.SetImages(scene.Phases, scene.Width, scene.Height, 0);
            canvas.InvalidateMeasure();
            canvas.InvalidateVisual();

            var (px, w, h) = scene.Sheet();
            palette.SetSheet(px, w, h, rom.Map16TileCount);

            edit = new LevelEdit(rom, scene, scene.Level.Objects);
            composeMs = ms;
            UpdateStatus();
        }
        catch (Exception ex) { status.Text = $"level ${num:X3}: {ex.Message}"; }
    }

    private double composeMs;

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

    private async void OnOpenRom(object? sender, RoutedEventArgs e)
    {
        // A real native file dialog, which ImGui cannot do — it draws its own.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SMW ROM",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SNES ROM") { Patterns = ["*.smc", "*.sfc"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) LoadRom(p);
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
