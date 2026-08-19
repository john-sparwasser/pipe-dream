using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PipeDream.Ui;

/// <summary>
/// The editor window. Deliberately the same paradigm as the ImGui editor: the CANVAS is the
/// editor and fills the window, a left palette drawer feeds it, and other editors are canvas
/// MODES reached from the header — never extra panels competing for the drawer.
///
/// This class draws and takes input. It does NOT open files, read ROM bytes or decide what an
/// edit means — every one of those goes through <see cref="EditorSession"/> and the rest of the
/// services layer. ArchitectureTests keeps it that way.
///
/// Controls are resolved by name rather than through XAML-generated fields — explicit, and
/// it does not depend on the code generator having run.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LevelBitmap bitmap = new();
    private readonly EditorSession session = new();
    private int levelNum = 0x105;

    private LevelEdit? edit;

    private LevelView canvas = null!;
    private Map16CanvasView map16Canvas = null!;
    private ChrPaletteView chr = null!;
    private ComboBox palRowBox = null!;
    private CheckBox chrFlipX = null!, chrFlipY = null!, chrPrio = null!;
    /// <summary>Map16 definition editing. Owned by the session, because it is rebuilt whenever
    /// the level's tileset changes and the window has no way to know when that happened.</summary>
    private Map16Edit? map16 => session.Map16;

    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!, tileZoom = null!;
    private TextBlock status = null!, hover = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!, paletteBar = null!;
    private TabStrip paletteTabs = null!;
    private DockPanel spritePanel = null!, objectPanel = null!, palettePanel = null!;
    private GfxCanvasView gfxCanvas = null!;
    private TextBox gfxFileBox = null!;
    private TextBlock gfxFileNote = null!, gfxColorNote = null!;
    private ToggleButton gfxPencil = null!, gfxFill = null!;
    private ScrollViewer gfxBinPanel = null!;
    private DockPanel gfxToolPanel = null!, gfxScroll = null!;
    private StackPanel gfxBins = null!, gfxJumps = null!;
    private ComboBox gfxPalRow = null!;
    private PaletteGridView gfxColors = null!;
    private MenuItem recentMenu = null!, upgradePrepItem = null!, spriteOverlayItem = null!,
                     animateItem = null!;
    private PaletteGridView paletteGrid = null!;
    private Slider palR = null!, palG = null!, palB = null!;
    private TextBlock palRv = null!, palGv = null!, palBv = null!, paletteNote = null!, paletteIndex = null!;
    private ListBox spriteList = null!, objectList = null!;
    private CheckBox loadedOnly = null!;
    private TextBlock spFilesLabel = null!, objectHint = null!;
    private Grid split = null!;
    private ToggleButton modeLevel = null!, modeMap16 = null!, modeGfx = null!;
    private ToggleButton layerOne = null!, layerTwo = null!;
    private Button addLayer2 = null!, dropLayer2 = null!;
    private TextBlock layer2Note = null!;

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
        layerOne = this.GetControl<ToggleButton>("LayerOne");
        layerTwo = this.GetControl<ToggleButton>("LayerTwo");
        addLayer2 = this.GetControl<Button>("AddLayer2");
        dropLayer2 = this.GetControl<Button>("DropLayer2");
        layer2Note = this.GetControl<TextBlock>("Layer2Note");

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
            session.RefreshSprites();
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
        // Subscribed once, on the session: a committed definition change invalidates the tile
        // caches behind the level, the picker and the sheet alike.
        session.Map16Committed += (_, _) => OnMap16Committed();

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

        palettePanel = this.GetControl<DockPanel>("PalettePanel");
        paletteGrid = this.GetControl<PaletteGridView>("PaletteGrid");
        palR = this.GetControl<Slider>("PalR");
        palG = this.GetControl<Slider>("PalG");
        palB = this.GetControl<Slider>("PalB");
        palRv = this.GetControl<TextBlock>("PalRv");
        palGv = this.GetControl<TextBlock>("PalGv");
        palBv = this.GetControl<TextBlock>("PalBv");
        paletteNote = this.GetControl<TextBlock>("PaletteNote");
        paletteIndex = this.GetControl<TextBlock>("PaletteIndex");

        paletteGrid.IsEdited = session.IsPaletteEdited;
        paletteGrid.SelectionChanged += (_, i) => ShowPaletteColor(i);
        foreach (var s in new[] { palR, palG, palB })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) OnPaletteSlider(); };

        // ---- GFX canvas mode ----
        gfxScroll = this.GetControl<DockPanel>("GfxScroll");
        gfxCanvas = this.GetControl<GfxCanvasView>("GfxCanvas");
        gfxFileBox = this.GetControl<TextBox>("GfxFileBox");
        gfxFileNote = this.GetControl<TextBlock>("GfxFileNote");
        gfxPencil = this.GetControl<ToggleButton>("GfxPencil");
        gfxFill = this.GetControl<ToggleButton>("GfxFill");
        gfxBinPanel = this.GetControl<ScrollViewer>("GfxBinPanel");
        gfxToolPanel = this.GetControl<DockPanel>("GfxToolPanel");
        gfxBins = this.GetControl<StackPanel>("GfxBins");
        gfxJumps = this.GetControl<StackPanel>("GfxJumps");
        gfxPalRow = this.GetControl<ComboBox>("GfxPalRow");
        gfxColors = this.GetControl<PaletteGridView>("GfxColors");
        gfxColors.Rows = 1;
        gfxColors.Cell = 20;

        for (int i = 0; i < 16; i++) gfxPalRow.Items.Add($"row {i}");
        gfxPalRow.SelectedIndex = 2;
        gfxPalRow.SelectionChanged += (_, _) =>
        {
            if (session.GfxPixels is not { } g) return;
            g.PalRow = Math.Max(0, gfxPalRow.SelectedIndex);
            RefreshGfx();
        };
        gfxColorNote = this.GetControl<TextBlock>("GfxColorNote");
        gfxColors.SelectionChanged += (_, i) =>
        {
            if (session.GfxPixels is { } g) g.Color = i;
            gfxColorNote.Text = ColorNote(i);
        };

        // Enter commits a typed id, as the ImGui field does — a recompose per keystroke would
        // fire on every half-typed number.
        gfxFileBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitGfxFileBox();
            e.Handled = true;
        };
        gfxFileBox.LostFocus += (_, _) => CommitGfxFileBox();

        gfxCanvas.PixelPainted += (_, p) =>
        {
            if (session.GfxPixels is not { } g) return;
            if (!g.Paint(p.X, p.Y, out bool forked)) return;
            if (forked) status.Text = $"GFX{g.File:X3} forked into the project — "
                                    + "edits shadow the stock file everywhere";
            RefreshGfxSheet();                    // live feedback, without a level recompose
        };
        gfxCanvas.StrokeEnded += (_, _) => session.GfxPixels?.EndStroke();
        gfxCanvas.ColorPicked += (_, p) =>
        {
            if (session.GfxPixels?.ColorAt(p.X, p.Y) is not { } c) return;
            session.GfxPixels.Color = c;
            gfxColors.Select(c);
            gfxColorNote.Text = ColorNote(c);
        };
        gfxCanvas.ToolToggled += (_, _) => SetGfxTool(session.GfxPixels?.Current == GfxEdit.Tool.Pencil
                                                          ? GfxEdit.Tool.Fill : GfxEdit.Tool.Pencil);
        gfxCanvas.ZoomStepped += (_, d) => StepGfxZoom(d);

        paletteTabs.SelectionChanged += (_, _) => OnPaletteTab();
        loadedOnly.IsCheckedChanged += (_, _) => ApplySpriteFilter();
        spriteList.SelectionChanged += (_, _) =>
        {
            if (spriteList.SelectedItem is not CatalogRow it) { canvas.CatalogSprite = -1; return; }
            canvas.CatalogSprite = it.Number;
            // Placing needs sprite mode; saying so beats a right-click that silently paints.
            status.Text = canvas.Mode == LevelView.EditMode.Sprites
                ? $"sprite {it.Label} armed — right-click the level to place"
                : $"sprite {it.Label} armed — press Esc for sprite mode, then right-click";
        };
        objectList.SelectionChanged += (_, _) =>
        {
            if (objectList.SelectedItem is not CatalogRow it) { canvas.CatalogObject = -1; return; }
            canvas.CatalogObject = it.Number;
            // Outline where it will land, the same feedback a multi-tile brush gets.
            canvas.BrushW = it.W;
            canvas.BrushH = it.H;
            canvas.InvalidateVisual();
            status.Text = $"object {it.Label} armed — right-click the level to place";
        };

        for (int i = 0; i < EditorSession.LevelCount; i++) levelBox.Items.Add($"${i:X3}");
        levelBox.SelectionChanged += OnLevelChanged;

        drawer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) OnDrawerVisibilityChanged();
        };

        palette.Zoom = tileZoom.Value;
        FitDrawerToPalette();

        // ---- menu items that depend on state ----
        recentMenu = this.GetControl<MenuItem>("RecentMenu");
        upgradePrepItem = this.GetControl<MenuItem>("UpgradePrepItem");
        spriteOverlayItem = this.GetControl<MenuItem>("SpriteOverlayItem");
        animateItem = this.GetControl<MenuItem>("AnimateItem");
        // Rebuilt when the menu opens rather than kept in sync: the recent list changes behind
        // this window's back (a project opened elsewhere in the session reorders it), and pruning
        // entries whose files have gone needs a disk check that has no business running per frame.
        this.GetControl<Menu>("MainMenu").Opened += (_, _) => RefreshFileMenu();

        KeyDown += OnWindowKeyDown;
        // Wheel scrolls the level sideways (Shift: vertically) — the canvas decides, the
        // scroll viewer applies, since it owns the offsets.
        canvas.ScrollRequested += (_, d) =>
        {
            var sv = this.GetControl<ScrollViewer>("CanvasScroll");
            sv.Offset = new Vector(Math.Max(0, sv.Offset.X + d.Dx), Math.Max(0, sv.Offset.Y + d.Dy));
        };

        if (EditorSession.FindStartupRom(Program.RomPath) is { } path) LoadRom(path);
    }

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
        edit = session.Edit;
        levelNum = session.LevelNum;
        canvas.Edit = edit;
        canvas.Vertical = session.Vertical;
        canvas.Sprites = session.Sprites;
        if (!session.HasLevel) return;

        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateMeasure();
        canvas.InvalidateVisual();

        var (px, w, h) = session.Sheet();
        palette.SetSheet(px, w, h, session.Map16TileCount);

        // Catalogs are rendered with the level's own GFX and palette, so the session has
        // already dropped them; the list has to let go of the old items too.
        spriteList.ItemsSource = null;
        RefreshDrawer();
        RefreshLayerBar();
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
        => Title = session.ProjectName is { } name
            ? $"pipe-dream — {name}{(session.HasUnsavedWork ? " *" : "")}"
            : session.RomFileName is { } file ? $"pipe-dream — {file} (no project)"
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
            bool redo = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            // Undo follows the canvas mode. Each editor keeps its own history — a single stack
            // across all three is a bigger piece of work (see the ponytail note on the palette
            // tab), and undoing a level edit while looking at pixels would be worse than this.
            if (modeGfx.IsChecked == true)
            {
                if (redo ? session.GfxPixels?.Redo() == true : session.GfxPixels?.Undo() == true)
                    RefreshGfx();
            }
            else if (modeMap16.IsChecked == true)
            {
                if (redo ? map16?.Redo() == true : map16?.Undo() == true) RefreshMap16Sheet();
            }
            else if (redo ? edit?.Redo() == true : edit?.Undo() == true)
            {
                PushDirty();
                UpdateStatus();
            }
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
        if (!session.HasLevel) return;
        // Object count comes from the EDIT, not the parsed level: painting appends objects,
        // and watching that number move is the clearest sign the stroke really became data.
        string undoNote = edit is { UndoDepth: > 0 } ? $"   {edit.UndoDepth} edit(s)" : "";
        status.Text = $"level ${levelNum:X3}   {session.PxW}x{session.PxH}px   " +
                      $"{session.ObjectCount} objects   composed in {composeMs:F0}ms{undoNote}";
    }

    private void UpdateHover()
        => hover.Text = canvas.HoverCell is { } c
            ? session.TileAt(c.X, c.Y) is { } tile
                ? $"({c.X,3},{c.Y,2})  tile 0x{tile:X3}"
                : $"({c.X,3},{c.Y,2})  empty"
            : "";

    /// <summary>Push what an edit changed into the bitmap. The composition already happened in
    /// the session's phase images, so this is only the copy — and because the bitmap takes whole
    /// images, a repaint is one 13MB push rather than per-cell blits. If that ever shows up in a
    /// profile, LevelBitmap grows a dirty-rect upload.</summary>
    private void PushDirty()
    {
        if (!session.RefreshPixels()) return;
        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateVisual();
    }

    // ---- handlers referenced from XAML ----

    private static FilePickerFileType RomType => new("SNES ROM") { Patterns = ["*.smc", "*.sfc"] };
    private static FilePickerFileType ProjectType => new("pipe-dream project") { Patterns = ["*.pdp"] };
    private static FilePickerFileType BinType => new("Raw planar GFX") { Patterns = ["*.bin"] };

    /// <summary>Pick a GFX file by sight. Returns null when the browser was cancelled.</summary>
    private async Task<int?> PickGfxFile(string purpose)
    {
        var dlg = new GfxBrowserWindow(session, purpose);
        await dlg.ShowDialog(this);
        return dlg.Picked;
    }

    private async void OnBrowseGfx(object? sender, RoutedEventArgs e)
    {
        if (await PickGfxFile("Open GFX in the tile editor") is not { } picked) return;
        session.GfxPixels?.Open(picked);
        RefreshGfx();
    }

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

        string? baseRom = EditorSession.FileExists(session.VanillaRomPath)
            ? session.VanillaRomPath : await PickFile("Choose the base ROM", RomType);
        if (baseRom is null) return;

        session.NewProject(EditorSession.ProjectFolderFor(folder, baseRom), baseRom);
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
        if (session.Header is not { } header || session.MainEntrance is not { } entrance) return;
        var dlg = new LevelPropertiesWindow(header, entrance, session.HasHeaderOverride);
        await dlg.ShowDialog(this);

        if (dlg.RevertRequested) { session.RevertHeader(); AdoptSession(); status.Text = "header reverted"; return; }
        if (dlg.AppliedEntry is { } en) session.ApplyEntry(en);
        if (dlg.AppliedHeader is { } h && h != header)
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
        session.SetVanillaRom(p);
        status.Text = "vanilla ROM set — new projects will prep from it";
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Screen exits, staged in a table and applied as one object edit. "Entrance…" hands
    /// off to the entrance record the exit points at, applying the table on the way so nothing
    /// typed is lost.</summary>
    private async void OnLevelExits(object? sender, RoutedEventArgs e)
    {
        if (edit is null) return;
        var dlg = new LevelExitsWindow(edit.ReadExits());
        await dlg.ShowDialog(this);

        if (dlg.Applied is { } exits && edit.WriteExits(exits))
        {
            PushDirty();
            UpdateStatus();
            status.Text = $"{exits.Count} screen exit(s) applied";
        }
        if (dlg.OpenEntrance is { } at) await ShowEntrance(at);
        UpdateTitle();
    }

    private async Task ShowEntrance(int index)
    {
        if (!session.HasRom) return;
        var dlg = new SecondaryEntranceWindow(index, session.ReadEntrance);
        await dlg.ShowDialog(this);
        if (dlg.Applied is not { } a) return;
        status.Text = session.WriteEntrance(a.Index, a.Entrance)
            ? $"secondary entrance ${a.Index:X3} written — {Convert.ToHexString(a.Entrance.ToBytes())}"
            : $"secondary entrance ${a.Index:X3} unchanged";
        UpdateTitle();
    }

    /// <summary>Fill in the parts of the File and View menus that depend on state: the recent
    /// list, whether a prep upgrade is available, and the two view checkmarks.</summary>
    private void RefreshFileMenu()
    {
        var items = new List<MenuItem>();
        foreach (string path in session.RecentProjects)
        {
            var item = new MenuItem { Header = path };
            item.Click += (_, _) =>
            {
                session.OpenProject(path);
                status.Text = session.Status;
                AdoptSession();
                levelBox.SelectedIndex = session.LevelNum;
            };
            items.Add(item);
        }
        recentMenu.ItemsSource = items;
        recentMenu.IsEnabled = items.Count > 0;

        upgradePrepItem.Header = $"Upgrade base to prep v{EditorSession.PrepVersion}";
        upgradePrepItem.IsEnabled = session.CanUpgradeBasePrep;
        spriteOverlayItem.Icon = session.ShowSprites ? new TextBlock { Text = "✓" } : null;
        animateItem.Icon = animate is null ? null : new TextBlock { Text = "✓" };
    }

    private void OnReloadLevel(object? sender, RoutedEventArgs e)
    {
        session.ReloadLevel();
        AdoptSession();
        status.Text = $"level ${levelNum:X3} reloaded";
    }

    // ---- layer 2 ----

    private void OnEditLayer(object? sender, RoutedEventArgs e)
    {
        int want = ReferenceEquals(sender, layerTwo) ? 1 : 0;
        string note = session.SetEditLayer(want);
        if (note.Length > 0) status.Text = note;
        AdoptSession();
    }

    private async void OnPickBackground(object? sender, RoutedEventArgs e)
    {
        var dlg = new BackgroundPickerWindow(session.Backgrounds(), session.CurrentBackground);
        await dlg.ShowDialog(this);
        if (dlg.Picked is not { } lo16) return;
        status.Text = session.SetLayer2Background(lo16);
        AdoptSession();
    }

    private void OnAddLayer2(object? sender, RoutedEventArgs e)
    {
        status.Text = session.SetLayer2ObjectMode(true);
        AdoptSession();
    }

    private void OnDropLayer2(object? sender, RoutedEventArgs e)
    {
        status.Text = session.SetLayer2ObjectMode(false);
        AdoptSession();
    }

    /// <summary>Show which layer is live and which of the layer-2 conversions is available. The
    /// loudest case gets its own note: objects that exist on a level whose MODE never loads them
    /// would silently do nothing in-game.</summary>
    private void RefreshLayerBar()
    {
        layerOne.IsChecked = session.EditLayer == 0;
        layerTwo.IsChecked = session.EditLayer == 1;
        layerTwo.IsEnabled = session.Layer2Editable;
        addLayer2.IsVisible = !session.Layer2Editable;
        dropLayer2.IsVisible = session.Layer2FromProject;
        layer2Note.Text = session.Layer2Editable && !session.LevelModeReadsLayer2 && session.Header is { } h
            ? $"(mode {h.LevelMode:X2} ignores L2)" : "";
    }

    private async void OnRomInfo(object? sender, RoutedEventArgs e)
    {
        var dlg = new RomInfoWindow(session.RomInfo());
        await dlg.ShowDialog(this);
    }

    private void OnUpgradePrep(object? sender, RoutedEventArgs e)
    {
        status.Text = session.UpgradeBasePrep();
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private void OnToggleSprites(object? sender, RoutedEventArgs e)
    {
        session.ShowSprites = !session.ShowSprites;
        AdoptSession();
        status.Text = session.ShowSprites ? "sprite overlay on" : "sprite overlay off";
    }

    /// <summary>
    /// Cycle the four animation phases, as the game does. The phases are already composed — this
    /// only changes which one the bitmap shows, so it costs one image swap rather than a
    /// recompose, which is why it can run at a game-ish rate at all.
    /// </summary>
    private void OnToggleAnimate(object? sender, RoutedEventArgs e)
    {
        if (animate is not null) { animate.Stop(); animate = null; SetPhase(0); }
        else
        {
            animate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            animate.Tick += (_, _) => SetPhase((canvas.Phase + 1) & 3);
            animate.Start();
        }
        animateItem.Icon = animate is null ? null : new TextBlock { Text = "✓" };
        status.Text = animate is null ? "tile animation stopped" : "tile animation running";
    }

    private DispatcherTimer? animate;

    /// <summary>LevelBitmap uploads a phase the first time it is asked for, so switching is just
    /// a repaint — there is nothing to push here.</summary>
    private void SetPhase(int phase)
    {
        canvas.Phase = phase;
        canvas.InvalidateVisual();
    }

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

    private List<CatalogRow>? spriteCatalog, objectCatalog;
    private int objectCatalogTileset = -1;

    /// <summary>
    /// The drawer tab and the canvas edit mode are two views of ONE thing, as in the ImGui
    /// editor: the Sprites tab means you are editing sprites, Map16 and Objects mean you are
    /// editing layer 1. Picking a tab therefore switches the mode (and drops the selection that
    /// belonged to the old one), which is why Esc also moves the tab.
    /// </summary>
    private void OnPaletteTab()
    {
        // The Palette tab belongs to no edit mode (ImGui parity: its tab carries a null mode),
        // so opening it leaves the canvas doing whatever it was doing.
        var want = paletteTabs.SelectedIndex switch
        {
            1 => LevelView.EditMode.Sprites,
            0 or 2 => LevelView.EditMode.Objects,
            _ => canvas.Mode,
        };
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
        bool gfxMode = modeGfx.IsChecked == true;
        bool modal = map16Mode || gfxMode;          // a canvas mode that owns the drawer
        int tab = modal ? -1 : Math.Max(0, paletteTabs.SelectedIndex);

        this.GetControl<ScrollViewer>("PaletteScroll").IsVisible = tab == 0;
        this.GetControl<DockPanel>("ChrPanel").IsVisible = map16Mode;
        gfxToolPanel.IsVisible = gfxMode;
        spritePanel.IsVisible = tab == 1;
        objectPanel.IsVisible = tab == 2;
        palettePanel.IsVisible = tab == 3;
        gfxBinPanel.IsVisible = tab == 4;
        // The bank/size row drives the Map16 sheet in both the picker and the Map16 canvas;
        // it means nothing to a catalog or to the pixel editor.
        paletteBar.IsVisible = map16Mode || tab == 0;

        if (spritePanel.IsVisible) EnsureSpriteCatalog();
        if (objectPanel.IsVisible) EnsureObjectCatalog();
        if (palettePanel.IsVisible) RefreshPaletteTab();
        if (gfxBinPanel.IsVisible) RefreshGfxBins();
    }

    /// <summary>Sprite thumbnails are drawn with THIS level's SP GFX and palette, so the catalog
    /// belongs to the level; the session decides when it is stale.</summary>
    private void EnsureSpriteCatalog()
    {
        if (spriteCatalog is not null) return;
        var (items, files) = session.SpriteCatalog();
        if (items.Count == 0) return;
        spriteCatalog = CatalogRow.Wrap(items);
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
        spriteList.SelectedItem = spriteList.ItemsSource!.Cast<CatalogRow>()
                                            .FirstOrDefault(i => i.Number == armed);
        canvas.CatalogSprite = armed;
    }

    /// <summary>Object thumbnails come from running the object engine once per object number,
    /// which is slow enough to be worth doing only on the first view of the tab. The session
    /// caches them per TILESET — the same footprint renders identically in every level using it.</summary>
    private void EnsureObjectCatalog()
    {
        if (objectCatalog is not null && objectCatalogTileset == session.Tileset) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = session.ObjectCatalog();
        if (items.Count == 0) return;
        objectCatalog = CatalogRow.Wrap(items);
        objectCatalogTileset = session.Tileset;
        objectList.ItemsSource = objectCatalog;
        objectHint.Text = $"tileset {objectCatalogTileset} — {objectCatalog.Count} objects, "
                        + $"ready in {sw.Elapsed.TotalMilliseconds:F0}ms. "
                        + "Select one, then right-click the level to place it.";
    }

    // ---- GFX canvas mode and the GFX tab ----

    private static string ColorNote(int i)
        => i == 0 ? "colour 0 — transparent in-game" : $"colour {i}";

    private int gfxZoom = 1;                       // index into GfxEdit.Zooms

    private void CommitGfxFileBox()
    {
        if (session.GfxPixels is not { } g) return;
        if (!int.TryParse(gfxFileBox.Text, System.Globalization.NumberStyles.HexNumber, null, out int id))
        { gfxFileBox.Text = $"{g.File:X3}"; return; }
        if (id == g.File) return;
        g.Open(id);                                // aborts an uncommitted stroke on the way
        RefreshGfx();
    }

    private void SetGfxTool(GfxEdit.Tool tool)
    {
        if (session.GfxPixels is { } g) g.Current = tool;
        gfxPencil.IsChecked = tool == GfxEdit.Tool.Pencil;
        gfxFill.IsChecked = tool == GfxEdit.Tool.Fill;
    }

    private void OnGfxTool(object? sender, RoutedEventArgs e)
        => SetGfxTool(ReferenceEquals(sender, gfxFill) ? GfxEdit.Tool.Fill : GfxEdit.Tool.Pencil);

    private void StepGfxZoom(int delta)
    {
        gfxZoom = Math.Clamp(gfxZoom + delta, 0, GfxEdit.Zooms.Length - 1);
        gfxCanvas.Zoom = GfxEdit.Zooms[gfxZoom];
        gfxCanvas.InvalidateMeasure();
        gfxCanvas.InvalidateVisual();
    }

    private void OnGfxZoomIn(object? sender, RoutedEventArgs e) => StepGfxZoom(1);
    private void OnGfxZoomOut(object? sender, RoutedEventArgs e) => StepGfxZoom(-1);

    /// <summary>Re-decode the sheet only. This is the live-paint path, so it must NOT recompose
    /// the level — that happens once when the stroke ends.</summary>
    private void RefreshGfxSheet()
    {
        if (session.GfxPixels is not { } g) return;
        var (px, w, h) = session.GfxSheet();
        gfxCanvas.Tiles = g.Layout.Tiles;
        gfxCanvas.SetSheet(px, w, h);
    }

    /// <summary>Everything the GFX mode shows for the current file: the sheet, the badge, the
    /// paint colours and the bin jump list.</summary>
    private void RefreshGfx()
    {
        if (session.GfxPixels is not { } g) return;
        gfxFileBox.Text = $"{g.File:X3}";
        gfxFileNote.Text = $"({g.Status})" + (g.Name is { } n ? $" \"{n}\"" : "");
        SetGfxTool(g.Current);
        gfxCanvas.Zoom = GfxEdit.Zooms[gfxZoom];
        RefreshGfxSheet();

        // The row's colours as paint swatches — only as many as the ROM's depth can hold, since
        // a 3bpp file has no colour 8. Index 0 keeps the sheet's grey convention: in a tile it
        // means transparent, and a black swatch would read as the colour black.
        int count = g.MaxColor + 1;
        var row = new uint[count];
        var pal = session.PaletteRgba;
        for (int i = 0; i < count; i++)
            row[i] = i == 0 ? 0xFF303030u : pal[Math.Max(0, gfxPalRow.SelectedIndex) * 16 + i];
        gfxColors.Cols = count;
        gfxColors.Colors = row;
        gfxColors.InvalidateMeasure();
        gfxColors.Select(g.Color);
        gfxColorNote.Text = ColorNote(g.Color);

        RefreshGfxJumps();
    }

    /// <summary>The level's ten bins as jump buttons: the file you want is one click away rather
    /// than a hex id to remember.</summary>
    private void RefreshGfxJumps()
    {
        gfxJumps.Children.Clear();
        if (session.GfxPixels is not { } g) return;
        foreach (var bin in session.GfxBins)
        {
            var b = new Button
            {
                Content = $"{bin.Name}  {bin.File:X3}",
                Padding = new Thickness(8, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                IsEnabled = bin.File != 0x7F,
            };
            if (bin.File == g.File) b.Background = UiColors.Accent;
            int file = bin.File, palRow = bin.PalRow;
            b.Click += (_, _) =>
            {
                g.Open(file);
                gfxPalRow.SelectedIndex = palRow;   // colour through the bin's natural row
                RefreshGfx();
            };
            gfxJumps.Children.Add(b);
        }
    }

    /// <summary>
    /// The GFX tab: one block per VRAM bin — what it holds, how it got there, and the three ways
    /// to change it (type an id, pick a file, import a raw .bin). Built in code rather than
    /// bound, because it is ten near-identical composites and a template plus a view model for
    /// each would be more machinery than the thing it builds.
    /// </summary>
    private void RefreshGfxBins()
    {
        gfxBins.Children.Clear();
        foreach (var bin in session.GfxBins)
        {
            var idBox = new TextBox { Text = $"{bin.File:X3}", Width = 60 };
            int bypWord = bin.BypWord, palRow = bin.PalRow, file = bin.File;
            void Commit()
            {
                if (!int.TryParse(idBox.Text, System.Globalization.NumberStyles.HexNumber,
                                  null, out int id)) { idBox.Text = $"{file:X3}"; return; }
                if (id == file) return;
                status.Text = session.SetGfxSlot(bypWord, id);
                AdoptSession();
            }
            idBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            idBox.LostFocus += (_, _) => Commit();

            var head = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = $"[{bin.Name}]", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                    Width = 40, Classes = { "dim" } },
                    idBox,
                },
            };

            string note = session.GfxBinNote(bypWord, bin.File, bin.Def);
            if (note.Length > 0)
                head.Children.Add(new TextBlock { Text = $"({note})", Classes = { "mono" } });
            if (session.GfxName(bin.File) is { } gname)
                head.Children.Add(new TextBlock { Text = $"\"{gname}\"", Classes = { "mono" } });

            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
            };
            var import = new Button { Content = "Import…", Padding = new Thickness(8, 1) };
            import.Click += async (_, _) =>
            {
                if (await PickFile("Raw GFX (.bin)", BinType) is not { } path) return;
                status.Text = session.ImportGfx(bypWord, path);
                AdoptSession();
            };
            var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 1) };
            browse.Click += async (_, _) =>
            {
                if (await PickGfxFile("Select GFX for this bin") is not { } picked) return;
                status.Text = session.SetGfxSlot(bypWord, picked);
                AdoptSession();
            };
            var edit = new Button { Content = "Edit", Padding = new Thickness(8, 1),
                                    IsEnabled = bin.File != 0x7F };
            edit.Click += (_, _) => EditGfxFile(bin.File, palRow);
            buttons.Children.Add(import);
            buttons.Children.Add(browse);
            buttons.Children.Add(edit);

            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(head);
            block.Children.Add(buttons);

            var (px, w, h) = session.GfxFileSheet(bin.File, bin.PalRow);
            if (px.Length > 0)
                block.Children.Add(new Image
                {
                    Source = LevelBitmap.FromPixels(px, w, h),
                    Width = w * 2, Height = h * 2,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                });
            else
                block.Children.Add(new TextBlock { Text = "(empty)", Classes = { "mono" } });

            block.Children.Add(new Border
            {
                Height = 1, Background = this.FindResource("BorderBrush") as IBrush,
                Margin = new Thickness(0, 4, 0, 0),
            });
            gfxBins.Children.Add(block);
        }
    }

    /// <summary>Open a file in the GFX canvas mode — the "Edit" button on a bin, and how the
    /// jump list works too.</summary>
    private void EditGfxFile(int file, int palRow)
    {
        if (session.GfxPixels is not { } g) return;
        g.Open(file);
        gfxPalRow.SelectedIndex = palRow;
        OnMode(modeGfx, new RoutedEventArgs());
    }

    // ---- palette tab ----

    /// <summary>Guard against the sliders firing while they are being SET from a selection —
    /// otherwise picking a swatch immediately writes its own colour back as an "edit".</summary>
    private bool loadingSwatch;

    private void RefreshPaletteTab()
    {
        paletteGrid.Colors = session.PaletteRgba;
        paletteGrid.InvalidateVisual();
        paletteNote.Text = "CGRAM — rows 0-7 background and foreground, 8-F sprites.  "
                         + (session.HasCustomPalette ? "source: LM custom palette"
                                                     : "source: vanilla (header-assembled)")
                         + (session.PaletteEditCount > 0 ? $"  —  {session.PaletteEditCount} edit(s)" : "");
        ShowPaletteColor(paletteGrid.Selected);
    }

    /// <summary>Load a swatch into the sliders. BGR555 is five bits per channel, which is what
    /// the sliders show: 0-31 is the real colour space, so nothing is quantised behind the
    /// user's back the way a 24-bit picker would.</summary>
    private void ShowPaletteColor(int index)
    {
        if (index < 0)
        {
            paletteIndex.Text = "pick a colour";
            palRv.Text = palGv.Text = palBv.Text = "";
            return;
        }
        ushort bgr = session.PaletteBgr(index);
        loadingSwatch = true;
        palR.Value = bgr & 0x1F;
        palG.Value = (bgr >> 5) & 0x1F;
        palB.Value = (bgr >> 10) & 0x1F;
        loadingSwatch = false;
        paletteIndex.Text = $"0x{index:X2} r{index >> 4} c{index & 15}  {bgr:X4}";
        palRv.Text = $"{palR.Value:0}";
        palGv.Text = $"{palG.Value:0}";
        palBv.Text = $"{palB.Value:0}";
    }

    /// <summary>
    /// How long after the last slider movement the level is recomposed. A colour is an input to
    /// composition, so committing one costs a full recompose (~100ms) — firing that on every
    /// step of a drag would make the slider unusable. The swatch updates immediately, the level
    /// catches up when you stop, which is the same deal the ImGui editor made by deferring until
    /// its colour picker closed.
    /// </summary>
    internal const int PaletteCommitMs = 120;

    private DispatcherTimer? paletteCommit;
    private ushort pendingColor;

    private void OnPaletteSlider()
    {
        if (loadingSwatch || paletteGrid.Selected < 0) return;
        pendingColor = (ushort)(((int)palB.Value << 10) | ((int)palG.Value << 5) | (int)palR.Value);

        // Immediate feedback, cheaply: the swatch and the readouts, not the level.
        paletteGrid.Colors[paletteGrid.Selected] = EditorSession.Rgba(pendingColor);
        paletteGrid.InvalidateVisual();
        palRv.Text = $"{palR.Value:0}";
        palGv.Text = $"{palG.Value:0}";
        palBv.Text = $"{palB.Value:0}";
        paletteIndex.Text = $"0x{paletteGrid.Selected:X2} r{paletteGrid.Selected >> 4} "
                          + $"c{paletteGrid.Selected & 15}  {pendingColor:X4}";

        if (paletteCommit is null)
        {
            paletteCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PaletteCommitMs) };
            paletteCommit.Tick += (_, _) => CommitPaletteEdit();
        }
        paletteCommit.Stop();                      // restart: only the LAST move commits
        paletteCommit.Start();
    }

    /// <summary>Apply the previewed colour for real. Internal so tests can fire it directly —
    /// the headless dispatcher does not run timers, and the behaviour worth pinning is what a
    /// commit DOES, not that a DispatcherTimer ticks.</summary>
    internal void CommitPaletteEdit()
    {
        paletteCommit?.Stop();
        if (paletteGrid.Selected < 0) return;
        if (!session.SetPaletteColor(paletteGrid.Selected, pendingColor)) return;
        // AdoptSession is the one path that pulls every view onto the new scene, this tab
        // included.
        AdoptSession();
    }

    private void OnResetPalette(object? sender, RoutedEventArgs e)
    {
        if (!session.ResetPalette()) return;
        AdoptSession();
        status.Text = "palette edits dropped";
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
        bool gfx = ReferenceEquals(sender, modeGfx);
        // Leaving the pixel editor with a stroke still open must not leave bytes behind that no
        // undo entry covers, so it is reverted rather than committed.
        if (!gfx) session.GfxPixels?.AbortStroke();

        this.GetControl<ScrollViewer>("CanvasScroll").IsVisible = !map16 && !gfx;
        this.GetControl<ScrollViewer>("Map16Scroll").IsVisible = map16;
        gfxScroll.IsVisible = gfx;
        edit?.Selection.Clear();
        map16Canvas.ClearSelection();

        RefreshDrawer();
        if (map16)
        {
            RefreshMap16Sheet();
            map16Canvas.Focus();
            status.Text = "Map16 — right-drag stamps the 8x8 brush; X/Y/P flip the quadrant under the cursor";
        }
        else if (gfx)
        {
            RefreshGfx();
            gfxCanvas.Focus();
            status.Text = "GFX — left paints, right picks a colour, F switches tool, [ ] zooms";
        }
        else UpdateStatus();
        canvas.InvalidateVisual();
    }

    private void RefreshMap16Sheet()
    {
        if (!session.HasLevel) return;
        var (px, w, h) = session.Sheet();
        map16Canvas.SetSheet(px, w, h, session.Map16TileCount);
        map16Canvas.Bank = Math.Max(0, bankBox.SelectedIndex);
        map16Canvas.SelectedTile = palette.Selected;
        RebuildChrSheet();
    }

    private void RebuildChrSheet()
    {
        var (px, w, h) = session.ChrSheet(Math.Max(0, palRowBox.SelectedIndex));
        if (px.Length > 0) chr.SetSheet(px, w, h);
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
        if (!session.HasLevel) return;
        session.RecomposeScene();
        AdoptSession();
        RefreshMap16Sheet();
    }
}
