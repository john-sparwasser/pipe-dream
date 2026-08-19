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
    private Map16CanvasView map16Canvas = null!;
    private ChrPaletteView chr = null!;
    private ComboBox palRowBox = null!;
    private CheckBox chrFlipX = null!, chrFlipY = null!, chrPrio = null!;
    private Map16Edit? map16;
    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!, tileZoom = null!;
    private TextBlock status = null!, hover = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!, paletteBar = null!;
    private TabStrip paletteTabs = null!;
    private DockPanel spritePanel = null!, objectPanel = null!;
    private ListBox spriteList = null!, objectList = null!;
    private CheckBox loadedOnly = null!;
    private TextBlock spFilesLabel = null!, objectHint = null!;
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
            // A grabbed multi-tile brush wins over the drawer's single selected tile.
            bool changed = brush is { } b
                ? edit.PaintBrush(c.X, c.Y, b.Tiles, b.W, b.H)
                : edit.Paint(c.X, c.Y, palette.Selected);
            if (changed) PushDirty();
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
        canvas.PlaceRequested += (_, c) =>
        {
            if (edit is null || canvas.CatalogObject < 0) return;
            edit.PlaceObject(canvas.CatalogObject, c.X, c.Y);
            PushDirty();
            UpdateStatus();
        };
        canvas.DeleteRequested += (_, _) =>
        {
            if (edit?.DeleteSelected() == true) { PushDirty(); UpdateStatus(); }
        };
        canvas.GrabRequested += (_, g) =>
        {
            if (edit is null) return;
            var (tiles, w, h) = edit.GrabTiles(g.X, g.Y, g.W, g.H);
            SetBrush(tiles, w, h);
            status.Text = $"grabbed {w}x{h} tiles as the brush — Esc or pick a tile to drop it";
        };
        canvas.SelectionChanged += (_, _) => UpdateStatus();
        canvas.SpritesChanged += (_, _) =>
        {
            // A sprite edit changes what the overlay draws, so the level has to recompose.
            session.RecomposeSprites(canvas.Sprites?.Sprites);
            AdoptSession();
        };

        // ---- Map16 canvas mode ----
        map16Canvas = this.GetControl<Map16CanvasView>("Map16Canvas");
        chr = this.GetControl<ChrPaletteView>("Chr");
        palRowBox = this.GetControl<ComboBox>("PalRowBox");
        chrFlipX = this.GetControl<CheckBox>("ChrFlipX");
        chrFlipY = this.GetControl<CheckBox>("ChrFlipY");
        chrPrio = this.GetControl<CheckBox>("ChrPrio");
        for (int i = 0; i < 8; i++) palRowBox.Items.Add($"{i}");
        palRowBox.SelectedIndex = 2;
        palRowBox.SelectionChanged += (_, _) => { RebuildChrSheet(); RefreshMap16Sheet(); };
        chr.BrushChanged += (_, _) =>
        {
            map16Canvas.BrushW = chr.Brush.W;
            map16Canvas.BrushH = chr.Brush.H;
            map16Canvas.InvalidateVisual();
        };
        map16Canvas.QuadPainted += (_, q) =>
        {
            if (map16 is null) return;
            // Painting an empty page CREATES it; the allocation relocates the def region, so
            // it has to happen before the quadrant offset is taken.
            if (map16.EnsurePage(q.Tile) is { } why) { status.Text = why; return; }
            map16.StampQuad(q.Tile, q.Quad, GfxBrushWord(q.Bx, q.By));
        };
        map16Canvas.StrokeEnded += (_, _) => map16?.EndStroke();
        map16Canvas.QuadFlagToggled += (_, f) =>
        {
            if (map16?.ReadDef(f.Tile) is not { } def) return;
            map16.StampQuad(f.Tile, f.Quad, (ushort)(def[f.Quad].Raw ^ f.Bit));
            map16.EndStroke();
        };
        map16Canvas.TilePicked += (_, tile) =>
        {
            selLabel.Text = $"0x{tile:X4}";
            SetBrush(null, 1, 1);
        };
        map16Canvas.MoveRequested += (_, m) =>
        {
            if (map16?.MoveTiles(map16Canvas.Bank, m.X, m.Y, m.W, m.H, m.Dx, m.Dy) is { } why)
                status.Text = why;
            map16?.EndStroke();
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
        palette.SelectionChanged += (_, tile) =>
        {
            selLabel.Text = $"0x{tile:X4}";
            SetBrush(null, 1, 1);          // picking a tile replaces a grabbed brush
        };

        tileZoom.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            palette.Zoom = tileZoom.Value;
            palette.InvalidateMeasure();
            palette.InvalidateVisual();
            FitDrawerToPalette();
        };

        // ---- drawer tabs: Map16 tiles / sprite catalog / object catalog ----
        paletteTabs = this.GetControl<TabStrip>("PaletteTabs");
        paletteBar = this.GetControl<Border>("PaletteBar");
        spritePanel = this.GetControl<DockPanel>("SpritePanel");
        objectPanel = this.GetControl<DockPanel>("ObjectPanel");
        spriteList = this.GetControl<ListBox>("SpriteList");
        objectList = this.GetControl<ListBox>("ObjectList");
        loadedOnly = this.GetControl<CheckBox>("LoadedOnly");
        spFilesLabel = this.GetControl<TextBlock>("SpFiles");
        objectHint = this.GetControl<TextBlock>("ObjectHint");

        paletteTabs.SelectionChanged += (_, _) => OnPaletteTab();
        loadedOnly.IsCheckedChanged += (_, _) => ApplySpriteFilter();
        spriteList.SelectionChanged += (_, _) =>
        {
            if (spriteList.SelectedItem is not CatalogItem it) { canvas.CatalogSprite = -1; return; }
            canvas.CatalogSprite = it.Number;
            // Placing needs sprite mode; saying so beats a right-click that silently paints.
            status.Text = canvas.Mode == LevelView.EditMode.Sprites
                ? $"sprite {it.Label} armed — right-click the level to place"
                : $"sprite {it.Label} armed — press Esc for sprite mode, then right-click";
        };
        objectList.SelectionChanged += (_, _) =>
        {
            if (objectList.SelectedItem is not CatalogItem it) { canvas.CatalogObject = -1; return; }
            canvas.CatalogObject = it.Number;
            // Outline where it will land, the same feedback a multi-tile brush gets.
            canvas.BrushW = it.W;
            canvas.BrushH = it.H;
            canvas.InvalidateVisual();
            status.Text = $"object {it.Label} armed — right-click the level to place";
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
        // Sprite editing works on the scene's parsed list; the overlay is the hit target.
        canvas.Sprites = scene?.Sprites is { } sd
            ? new SpriteEdit(sd, scene.Overlay, canvas.Vertical) : null;
        if (scene is null || rom is null) return;

        bitmap.SetImages(scene.Phases, scene.Width, scene.Height, 0);
        canvas.InvalidateMeasure();
        canvas.InvalidateVisual();

        var (px, w, h) = scene.Sheet();
        palette.SetSheet(px, w, h, rom.Map16TileCount);
        // The Map16 editor writes defs straight into the session ROM, so it is rebuilt with
        // the level: a new tileset means new def offsets.
        map16 = new Map16Edit(rom, scene.Level.Header.Tileset, session.Project);
        map16.Committed += OnMap16Committed;

        // Catalogs are rendered with the level's own GFX and palette, so they are stale now.
        // The object catalog only depends on the tileset; EnsureObjectCatalog checks that.
        spriteCatalog = null;
        spriteList.ItemsSource = null;
        RefreshDrawer();
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

    /// <summary>Multi-tile stamp brush from a Ctrl+drag grab; null = the drawer's single tile.</summary>
    private (ushort[] Tiles, int W, int H)? brush;

    private void SetBrush(ushort[]? tiles, int w, int h)
    {
        // Arming the brush disarms the object catalog, as the ImGui editor does — right-click
        // means one thing at a time. Both halves are set: clearing the list is what the user
        // sees, and clearing the canvas is what actually disarms — the list's own handler does
        // not fire when nothing was selected in it.
        objectList.SelectedIndex = -1;
        canvas.CatalogObject = -1;
        brush = tiles is null ? null : (tiles, w, h);
        // The canvas outlines the footprint under the cursor, so a 4x3 brush is visible
        // before it is committed rather than after.
        canvas.BrushW = tiles is null ? 1 : w;
        canvas.BrushH = tiles is null ? 1 : h;
        canvas.InvalidateVisual();
    }

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
            else if (brush is not null) { SetBrush(null, 1, 1); status.Text = "brush dropped"; }
            else
            {
                // Esc cycles Layer 1 <-> sprite selection, as the ImGui editor does, and
                // drops whatever was selected on the way.
                canvas.Mode = canvas.Mode == LevelView.EditMode.Objects
                    ? LevelView.EditMode.Sprites : LevelView.EditMode.Objects;
                edit?.Selection.Clear();
                canvas.Sprites?.Selection.Clear();
                canvas.InvalidateVisual();
                // Bring the matching drawer tab along (ImGui parity): the tab and the mode are
                // the same state, so leaving the tab behind would show a sprite catalog while
                // the canvas edits objects.
                paletteTabs.SelectedIndex = canvas.Mode == LevelView.EditMode.Sprites ? 1 : 0;
                status.Text = canvas.Mode == LevelView.EditMode.Sprites
                    ? "sprite mode — left-drag selects by what a sprite DRAWS, right-click places"
                    : $"level ${levelNum:X3}";
            }
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

    /// <summary>Level header + main entrance, staged in a dialog and applied in one go: every
    /// header field forces a full reparse, so live-applying a slider would be unusable.</summary>
    private async void OnLevelProperties(object? sender, RoutedEventArgs e)
    {
        if (rom is null || scene is null) return;
        var dlg = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(levelNum),
                                            session.HasHeaderOverride);
        await dlg.ShowDialog(this);

        if (dlg.RevertRequested) { session.RevertHeader(); AdoptSession(); status.Text = "header reverted"; return; }
        if (dlg.AppliedEntry is { } en) session.ApplyEntry(en);
        if (dlg.AppliedHeader is { } h && h != scene.Level.Header)
        {
            session.ApplyHeader(h);
            AdoptSession();
            status.Text = $"header applied — {Convert.ToHexString(h.ToBytes())}";
        }
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

    // ---- drawer tabs ----

    private List<CatalogItem>? spriteCatalog, objectCatalog;
    private int objectCatalogTileset = -1;

    /// <summary>
    /// The drawer tab and the canvas edit mode are two views of ONE thing, as in the ImGui
    /// editor: the Sprites tab means you are editing sprites, Map16 and Objects mean you are
    /// editing layer 1. Picking a tab therefore switches the mode (and drops the selection that
    /// belonged to the old one), which is why Esc also moves the tab.
    /// </summary>
    private void OnPaletteTab()
    {
        var want = paletteTabs.SelectedIndex == 1 ? LevelView.EditMode.Sprites
                                                  : LevelView.EditMode.Objects;
        if (canvas.Mode != want)
        {
            canvas.Mode = want;
            edit?.Selection.Clear();
            canvas.Sprites?.Selection.Clear();
            canvas.InvalidateVisual();
        }
        RefreshDrawer();
    }

    /// <summary>
    /// Show whichever drawer content the current state calls for. Two things decide it: the
    /// CANVAS mode (Map16 editing always feeds from the 8x8 GFX picker, whatever tab is
    /// selected) and otherwise the drawer tab. One method for both, because splitting the
    /// decision across the tab handler and the mode handler is how a panel ends up visible in
    /// a mode that cannot use it.
    /// </summary>
    private void RefreshDrawer()
    {
        bool map16Mode = modeMap16.IsChecked == true;
        int tab = map16Mode ? 0 : Math.Max(0, paletteTabs.SelectedIndex);

        this.GetControl<ScrollViewer>("PaletteScroll").IsVisible = !map16Mode && tab == 0;
        this.GetControl<DockPanel>("ChrPanel").IsVisible = map16Mode;
        spritePanel.IsVisible = !map16Mode && tab == 1;
        objectPanel.IsVisible = !map16Mode && tab == 2;
        // The bank/size row drives the Map16 sheet in both the picker and the Map16 canvas;
        // it means nothing to a catalog.
        paletteBar.IsVisible = map16Mode || tab == 0;

        if (spritePanel.IsVisible) EnsureSpriteCatalog();
        if (objectPanel.IsVisible) EnsureObjectCatalog();
    }

    /// <summary>Sprite thumbnails are drawn with THIS level's SP GFX and palette, so the
    /// catalog belongs to the level and is rebuilt when the level changes.</summary>
    private void EnsureSpriteCatalog()
    {
        if (spriteCatalog is not null || rom is null || scene is null) return;
        spriteCatalog = Catalog.Sprites(rom, scene, levelNum, out var files);
        spFilesLabel.Text = $"SP {string.Join(" ", files.Select(f => f.ToString("X2")))}";
        ApplySpriteFilter();
    }

    private void ApplySpriteFilter()
    {
        if (spriteCatalog is null) return;
        int armed = canvas.CatalogSprite;
        spriteList.ItemsSource = loadedOnly.IsChecked == true
            ? spriteCatalog.Where(i => i.Loaded).ToList() : spriteCatalog;
        // Re-select whatever was armed, so toggling the filter does not silently unarm it.
        spriteList.SelectedItem = spriteList.ItemsSource!.Cast<CatalogItem>()
                                            .FirstOrDefault(i => i.Number == armed);
        canvas.CatalogSprite = armed;
    }

    /// <summary>Object thumbnails come from running the object engine once per object number,
    /// which is slow enough to be worth doing only on the first view of the tab and only when
    /// the TILESET changes — the same footprint renders identically in every level using it.</summary>
    private void EnsureObjectCatalog()
    {
        if (rom is null || scene is null) return;
        if (objectCatalog is not null && objectCatalogTileset == scene.Level.Header.Tileset) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        objectCatalog = Catalog.Objects(rom, scene);
        objectCatalogTileset = scene.Level.Header.Tileset;
        objectList.ItemsSource = objectCatalog;
        objectHint.Text = $"tileset {objectCatalogTileset} — {objectCatalog.Count} objects, "
                        + $"built in {sw.Elapsed.TotalMilliseconds:F0}ms. "
                        + "Select one, then right-click the level to place it.";
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

    // Radio behaviour without a group: exactly one canvas mode is active. Switching drops
    // every mode's in-flight drag, as the ImGui view toggle does.
    private void OnMode(object? sender, RoutedEventArgs e)
    {
        foreach (var b in new[] { modeLevel, modeMap16, modeGfx })
            b.IsChecked = ReferenceEquals(b, sender);

        bool map16 = ReferenceEquals(sender, modeMap16);
        this.GetControl<ScrollViewer>("CanvasScroll").IsVisible = !map16;
        this.GetControl<ScrollViewer>("Map16Scroll").IsVisible = map16;
        edit?.Selection.Clear();
        map16Canvas.ClearSelection();

        RefreshDrawer();
        if (map16) { RefreshMap16Sheet(); map16Canvas.Focus(); status.Text = "Map16 — right-drag stamps the 8x8 brush; X/Y/P flip the quadrant under the cursor"; }
        else if (ReferenceEquals(sender, modeLevel)) UpdateStatus();
        else status.Text = "GFX mode — not ported yet (canvas mode, same window)";
        canvas.InvalidateVisual();
    }

    private void RefreshMap16Sheet()
    {
        if (scene is null || rom is null) return;
        var (px, w, h) = scene.Sheet();
        map16Canvas.SetSheet(px, w, h, rom.Map16TileCount);
        map16Canvas.Bank = Math.Max(0, bankBox.SelectedIndex);
        map16Canvas.SelectedTile = palette.Selected;
        RebuildChrSheet();
    }

    private void RebuildChrSheet()
    {
        if (rom is null || scene?.Palettes[0] is not { } pal) return;
        chr.Build(rom, scene.Level.Header, levelNum, 0, pal, Math.Max(0, palRowBox.SelectedIndex));
    }

    /// <summary>
    /// The Map16 word a brush cell stamps: the 8x8 tile number in the low 10 bits, then the
    /// palette row and the flip/priority flags from the drawer. This packing IS the Map16
    /// format (CONTRACT §5), which is why the flags live with the brush rather than being
    /// applied afterwards.
    /// </summary>
    private ushort GfxBrushWord(int bx, int by)
        => (ushort)((chr.TileOfBrushCell(bx, by) & 0x3FF)
                    | (Math.Max(0, palRowBox.SelectedIndex) << 10)
                    | (chrPrio.IsChecked == true ? 0x2000 : 0)
                    | (chrFlipX.IsChecked == true ? 0x4000 : 0)
                    | (chrFlipY.IsChecked == true ? 0x8000 : 0));

    /// <summary>Rebuild everything a committed Map16 edit invalidates: the tile caches feed
    /// both the level canvas and the picker, so a def change has to reach all three.</summary>
    private void OnMap16Committed()
    {
        if (rom is null || scene is null) return;
        session.RecomposeScene();
        AdoptSession();
        RefreshMap16Sheet();
    }
}
