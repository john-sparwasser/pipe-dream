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

    private LevelView canvas = null!;
    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!;
    private TextBlock status = null!, hover = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!;
    private ToggleButton modeLevel = null!, modeMap16 = null!, modeGfx = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        canvas = this.GetControl<LevelView>("Canvas");
        palette = this.GetControl<Map16PaletteView>("Palette");
        levelBox = this.GetControl<ComboBox>("LevelBox");
        bankBox = this.GetControl<ComboBox>("BankBox");
        zoomSlider = this.GetControl<Slider>("ZoomSlider");
        status = this.GetControl<TextBlock>("Status");
        hover = this.GetControl<TextBlock>("Hover");
        zoomLabel = this.GetControl<TextBlock>("ZoomLabel");
        selLabel = this.GetControl<TextBlock>("SelLabel");
        drawer = this.GetControl<Border>("Drawer");
        modeLevel = this.GetControl<ToggleButton>("ModeLevel");
        modeMap16 = this.GetControl<ToggleButton>("ModeMap16");
        modeGfx = this.GetControl<ToggleButton>("ModeGfx");

        canvas.Source = bitmap;
        canvas.CellPressed += (_, c) => hover.Text = $"cell ({c.X}, {c.Y})";
        canvas.PointerMoved += (_, e) =>
        {
            if (canvas.CellAt(e.GetPosition(canvas)) is { } c && scene is not null)
            {
                int tile = scene.Grid.Get(c.X, c.Y);
                hover.Text = tile == Map16Grid.Empty
                    ? $"({c.X}, {c.Y})  empty"
                    : $"({c.X}, {c.Y})  tile 0x{tile:X3}";
            }
            else hover.Text = "";
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

        for (int i = 0; i < Rom.LevelCount; i++) levelBox.Items.Add($"${i:X3}");
        levelBox.SelectionChanged += OnLevelChanged;

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

            status.Text = $"level ${num:X3}   {scene.Width}x{scene.Height}px   " +
                          $"{scene.Level.Objects.Count} objects   composed in {ms:F0}ms";
        }
        catch (Exception ex) { status.Text = $"level ${num:X3}: {ex.Message}"; }
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

    private void OnTogglePalette(object? sender, RoutedEventArgs e) => drawer.IsVisible = !drawer.IsVisible;

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
