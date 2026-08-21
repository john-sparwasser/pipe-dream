using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    private Avalonia.Controls.Shapes.Path gfxKind = null!;
    private Button gfxSave = null!, gfxEmptyLoad = null!;
    private TextBlock gfxFileName = null!, gfxFileNote = null!;
    private ToggleButton gfxPencil = null!, gfxFill = null!, gfxErase = null!, gfxDropper = null!;
    private DockPanel gfxToolPanel = null!, gfxScroll = null!;
    private Border gfxPaletteBar = null!;
    private StackPanel gfxBins = null!;
    private ComboBox gfxPalRow = null!;
    private PaletteGridView gfxColors = null!;
    private MenuItem recentMenu = null!, upgradePrepItem = null!, spriteOverlayItem = null!,
                     animateItem = null!;
    private PaletteGridView paletteGrid = null!;
    private TextBlock paletteNote = null!, paletteIndex = null!;

    /// <summary>The colour picker and the flyout that shows it over the clicked swatch. The
    /// panel is held directly rather than reached through the flyout, whose content lives in its
    /// own name scope — and so the tests can drive it without opening a popup.</summary>
    internal readonly ColorPickerPanel picker = new();
    private readonly Flyout pickerFlyout = new() { Placement = PlacementMode.Pointer };
    private ListBox spriteList = null!, objectList = null!;
    private CheckBox loadedOnly = null!;
    private TextBlock spFilesLabel = null!, objectHint = null!;
    private Grid split = null!;
    private ToggleButton modeLevel = null!, modeMap16 = null!, modeGfx = null!;
    private ToggleButton layerOne = null!, layerTwo = null!;
    private Button addLayer2 = null!, dropLayer2 = null!;
    private TextBlock layer2Note = null!;
    private Border map16Props = null!;
    private TextBlock m16SelLabel = null!, m16ActsNote = null!, m16Unallocated = null!;
    private StackPanel m16Fields = null!;
    private TextBox m16Acts = null!;
    private CheckBox m16Priority = null!;
    private ComboBox m16Palette = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Top-left, not the OS's pick: at 1500x900 the default placement can hang off screen.
        Position = new PixelPoint(0, 0);

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
        // Moving and resizing raise this too, and they change PIXELS — without the push the
        // objects stayed where they were drawn and the edit looked like it had not happened.
        // RefreshPixels is a no-op when nothing is dirty, so a plain selection costs nothing.
        canvas.SelectionChanged += (_, _) => { PushDirty(); UpdateStatus(); };
        canvas.SampleRequested += (_, p) =>
        {
            if (session.SampleCgramIndex(p.X, p.Y) is not { } idx)
            {
                status.Text = "no CGRAM colour matches that pixel";
                return;
            }
            // Land the user where they can act on it: the Palette tab, that swatch selected.
            paletteTabs.SelectedIndex = PaletteTabIndex;
            paletteGrid.Select(idx);
            ShowPaletteColor(idx);
            status.Text = $"picked {DescribeSwatch(idx)}";
        };
        // A sprite edit changes what the overlay draws, so the level has to recompose. The
        // adopt comes from SceneRebuilt, below.
        canvas.SpritesChanged += (_, _) => { session.RefreshSprites(); PushSpritePixels(); };
        // A live drag step shifts cached overlay pixels in place instead of rebuilding the
        // scene, so only the bitmap upload is left to do here.
        canvas.SpritesMoved += (_, d) => { session.MoveSprites(d.Dx, d.Dy); PushSpritePixels(); };

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

        // ---- Map16 properties inspector ----
        map16Props = this.GetControl<Border>("Map16Props");
        m16SelLabel = this.GetControl<TextBlock>("M16SelLabel");
        m16Fields = this.GetControl<StackPanel>("M16Fields");
        m16Unallocated = this.GetControl<TextBlock>("M16Unallocated");
        m16Acts = this.GetControl<TextBox>("M16Acts");
        m16ActsNote = this.GetControl<TextBlock>("M16ActsNote");
        m16Priority = this.GetControl<CheckBox>("M16Priority");
        m16Palette = this.GetControl<ComboBox>("M16Palette");
        for (int i = 0; i < 8; i++) m16Palette.Items.Add($"{i}");

        map16Canvas.SelectionChanged += (_, _) => RefreshMap16Props();
        map16Canvas.TilePicked += (_, _) => RefreshMap16Props();
        // Committed on Enter or on leaving the field, not per keystroke: half a hex number is
        // still a number, and every commit rewrites the ROM.
        m16Acts.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyM16Acts(); e.Handled = true; } };
        m16Acts.LostFocus += (_, _) => ApplyM16Acts();
        m16Priority.IsCheckedChanged += (_, _) =>
        {
            if (loadingM16Props) return;
            bool on = m16Priority.IsChecked == true;
            map16?.Transform(map16Canvas.SelectedTiles(),
                             w => (ushort)(on ? w.Raw | 0x2000 : w.Raw & ~0x2000));
        };
        m16Palette.SelectionChanged += (_, _) =>
        {
            if (loadingM16Props || m16Palette.SelectedIndex < 0) return;
            int row = m16Palette.SelectedIndex;
            map16?.Transform(map16Canvas.SelectedTiles(),
                             w => (ushort)((w.Raw & ~0x1C00) | (row << 10)));
        };

        // The slider is in PERCENT; the canvas scales by a factor.
        zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoom();

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
        paletteNote = this.GetControl<TextBlock>("PaletteNote");
        paletteIndex = this.GetControl<TextBlock>("PaletteIndex");

        paletteGrid.IsEdited = session.IsPaletteEdited;
        paletteGrid.Describe = DescribeSwatch;
        paletteGrid.SelectionChanged += (_, i) => { ShowPaletteColor(i); OpenPicker(); };
        picker.ColorChanged += (_, c) => OnPickerColor(c);
        pickerFlyout.Content = picker;
        // The open picker IS the undo boundary, as it was in the ImGui editor: everything done
        // between opening and dismissing it is one entry, however many colours the drag crossed.
        pickerFlyout.Opened += (_, _) => session.BeginPaletteStroke();
        pickerFlyout.Closed += (_, _) =>
        {
            session.EndPaletteStroke();
            AdoptSession();                // the phases and sheets the live drag skipped
        };

        // ---- GFX canvas mode ----
        gfxScroll = this.GetControl<DockPanel>("GfxScroll");
        gfxCanvas = this.GetControl<GfxCanvasView>("GfxCanvas");
        gfxKind = this.GetControl<Avalonia.Controls.Shapes.Path>("GfxKind");
        gfxFileName = this.GetControl<TextBlock>("GfxFileName");
        gfxFileNote = this.GetControl<TextBlock>("GfxFileNote");
        gfxSave = this.GetControl<Button>("GfxSave");
        gfxEmptyLoad = this.GetControl<Button>("GfxEmptyLoad");
        gfxPencil = this.GetControl<ToggleButton>("GfxPencil");
        gfxFill = this.GetControl<ToggleButton>("GfxFill");
        gfxErase = this.GetControl<ToggleButton>("GfxErase");
        gfxDropper = this.GetControl<ToggleButton>("GfxDropper");
        gfxToolPanel = this.GetControl<DockPanel>("GfxToolPanel");
        gfxPaletteBar = this.GetControl<Border>("GfxPaletteBar");
        gfxBins = this.GetControl<StackPanel>("GfxBins");
        gfxPalRow = this.GetControl<ComboBox>("GfxPalRow");
        gfxColors = this.GetControl<PaletteGridView>("GfxColors");
        gfxColors.Rows = 1;
        gfxColors.Cell = 20;

        for (int i = 0; i < 16; i++) gfxPalRow.Items.Add($"{i}");
        gfxPalRow.SelectedIndex = 2;
        gfxPalRow.SelectionChanged += (_, _) =>
        {
            if (session.GfxPixels is not { } g) return;
            g.PalRow = Math.Max(0, gfxPalRow.SelectedIndex);
            RefreshGfx();
        };
        gfxColors.SelectionChanged += (_, i) =>
        {
            if (session.GfxPixels is { } g) g.Color = i;
        };

        gfxCanvas.PixelPainted += (_, p) =>
        {
            if (session.GfxPixels is not { } g) return;
            // The eyedropper takes rather than paints, so left-click with it does what right-click
            // does with every other tool.
            if (g.Current == GfxEdit.Tool.Dropper) { PickGfxColor(p.X, p.Y); return; }
            if (!g.Paint(p.X, p.Y, out bool forked)) return;
            if (forked) status.Text = $"GFX{g.File:X3} forked into the project — "
                                    + "edits shadow the base file everywhere";
            RefreshGfxSheet();                    // live feedback, without a level recompose
        };
        gfxCanvas.StrokeEnded += (_, _) =>
        {
            session.GfxPixels?.EndStroke();
            gfxSave.IsEnabled = session.GfxDirty;         // the stroke is what there is to save
        };
        gfxCanvas.ColorPicked += (_, p) => PickGfxColor(p.X, p.Y);
        // F cycles the four tools in enum order rather than toggling two.
        gfxCanvas.ToolToggled += (_, _) =>
        {
            if (session.GfxPixels is { } g)
                SetGfxTool((GfxEdit.Tool)(((int)g.Current + 1) % 4));
        };
        gfxCanvas.ZoomStepped += (_, d) => StepZoom(d);

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

        // A rebuild swaps in a new scene and new layer editors, and the caches here (edit,
        // canvas.Edit, the bitmap's phase images) all point at the old ones until this runs.
        // Without it a GFX pixel commit — which rebuilds — left the canvas editing a discarded
        // object list: the delete happened, nothing on screen changed, and the edit was lost.
        session.SceneRebuilt += (_, _) => AdoptSession();

        // An explicit ROM argument opens projectless — that is the test suite's and the
        // command line's hatch, not a user path. A normal launch starts empty and the
        // startup chooser asks for a project.
        if (Program.RomPath is { } romArg && EditorSession.FileExists(romArg)) LoadRom(romArg);

        // Startup dialogs wait for the window to actually be up — a modal owned by an unshown
        // window has nothing to centre on. Only on a real desktop: a headless test run has no
        // one to answer them and would block forever.
        if (Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            Opened += OnFirstOpened;
    }

    /// <summary>
    /// The startup sequence, one modal at a time so they never stack: first run's vanilla-ROM
    /// prompt, then the last project reopened (or the chooser when there is none), then the
    /// once-a-day update check. The check is fired
    /// and forgotten — nothing is shown unless there really is a newer release: a startup that
    /// says "you are up to date" every morning is noise, and one that reports a failed check is
    /// reporting something the user cannot act on.
    /// </summary>
    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        if (session.NeedsVanillaRom)
        {
            var dlg = new FirstRunWindow();
            await dlg.ShowDialog(this);
            if (dlg.Chosen is { } rom)
            {
                session.SetVanillaRom(rom);
                status.Text = "vanilla ROM set — new projects will prep from it";
            }
        }

        // Pick up where the last session left off. The recent list is pruned of anything that has
        // moved or been deleted, so its head is the last project that can actually be opened —
        // and a base-ROM problem still routes through the recovery flow rather than being
        // swallowed. Anything that leaves nothing open falls through to the chooser.
        if (!session.HasRom && session.RecentProjects.FirstOrDefault() is { } last)
            await OpenProjectPath(last);

        if (!session.HasRom) await PromptForProject();

        try
        {
            if (await session.FindUpdate(userAsked: false) is { } found)
                await UpdateWindow.Prompt(this, session, found);
        }
        catch { /* a check must never be why the editor failed to start */ }
    }

    /// <summary>
    /// The chooser loops until something is actually open: cancelling a picker lands back here
    /// rather than in a dead editor. Dismissing the chooser itself is the way out — the File
    /// menu can do everything it can.
    /// </summary>
    private async Task PromptForProject()
    {
        while (!session.HasRom)
        {
            var dlg = new StartWindow(session.RecentProjects);
            await dlg.ShowDialog(this);
            if (dlg.OpenRecent is { } pdp) await OpenProjectPath(pdp);
            else if (dlg.CreateNew) await NewProjectFlow();
            else if (dlg.OpenExisting) await OpenProjectFlow();
            else return;
        }
    }

    /// <summary>Help → Check for updates. Unlike the startup check this one always answers,
    /// because the user asked a question.</summary>
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        status.Text = "checking for updates…";
        try
        {
            if (await session.FindUpdate(userAsked: true) is { } found)
            {
                await UpdateWindow.Prompt(this, session, found);
                return;
            }
            status.Text = $"Pipe Dream {session.CurrentVersion} — no newer build available";
        }
        catch (Exception ex) { status.Text = "update check failed: " + ex.Message; }
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
        canvas.InvalidateVisual();
    }

    /// <summary>Global keys, matching the ImGui editor: Ctrl+Z undo, Ctrl+Shift+Z redo, Esc
    /// leaving a non-Level canvas mode before it touches selection, and - / = zooming.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            bool redo = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            // Undo follows what you are LOOKING AT. Each editor keeps its own history — a single
            // stack across all of them is a bigger piece of work, and undoing a level edit while
            // looking at pixels would be worse than this.
            //
            // The Palette tab is checked first because it is a drawer tab rather than a canvas
            // mode: with it open the canvas is still in Level mode, so testing the mode first
            // would send Ctrl+Z to the level while the user is editing colours.
            if (paletteTabs.SelectedIndex == PaletteTabIndex)
            {
                // Close any open stroke FIRST, so what the picker has already done becomes the
                // entry that undo then takes back. (This used to re-apply the last picked colour
                // through a stale pending value, which turned the second Ctrl+Z into a redo.)
                session.EndPaletteStroke();
                if (redo ? session.PaletteRedo() : session.PaletteUndo())
                {
                    AdoptSession();
                    status.Text = redo ? "palette redo" : "palette undo";
                }
            }
            else if (modeGfx.IsChecked == true)
            {
                if (redo ? session.GfxPixels?.Redo() == true : session.GfxPixels?.Undo() == true)
                    RefreshGfx();
            }
            else if (modeMap16.IsChecked == true)
            {
                if (redo ? map16?.Redo() == true : map16?.Undo() == true) RefreshMap16Sheet();
            }
            // Sprite mode has its own history — without this branch Ctrl+Z in sprite mode fell
            // through and silently rewound the OBJECT stack instead.
            else if (canvas.Mode == LevelView.EditMode.Sprites && session.Sprites is { } sp)
            {
                if (redo ? sp.Redo() : sp.Undo())
                {
                    session.RefreshSprites();
                    PushSpritePixels();
                    status.Text = redo ? "sprite redo" : "sprite undo";
                }
            }
            else if (redo ? edit?.Redo() == true : edit?.Undo() == true)
            {
                PushDirty();
                UpdateStatus();
            }
            e.Handled = true;
        }
        // Browser bindings, and the same keys the GFX canvas's [ ] do for its own sheet: the
        // zoom keys always act on whatever canvas is showing.
        else if (e.Key is Key.OemMinus or Key.Subtract or Key.OemPlus or Key.Add)
        {
            int dir = e.Key is Key.OemMinus or Key.Subtract ? -1 : 1;
            StepZoom(dir);
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

    /// <summary>One tick of zoom, in the slider's own units — the slider IS the zoom state, so
    /// stepping it keeps the label and whichever canvas it drives in step for free.</summary>
    private void StepZoom(int dir)
    {
        zoomSlider.Value = Math.Clamp(zoomSlider.Value + dir * zoomSlider.TickFrequency,
                                      zoomSlider.Minimum, zoomSlider.Maximum);
        status.Text = $"zoom {zoomSlider.Value:0}%";
    }

    // The gutter slider drives whichever canvas is showing, but 200% is a sane level zoom and a
    // useless pixel zoom, so each mode keeps its own value and its own range. GFX starts at 8
    // screen pixels per GFX pixel, which is what the ImGui editor opened at.
    private double levelZoomPct = 200, gfxZoomPct = 800;

    /// <summary>Point the zoom control at a mode: its range, its step, and the value it was left
    /// at. Call it AFTER the mode flags flip — <see cref="ApplyZoom"/> reads them.</summary>
    private void ApplyZoomTarget(bool gfx)
    {
        // Read the wanted value first: narrowing the range coerces Value, which lands in the
        // remembered field on the way through.
        double want = gfx ? gfxZoomPct : levelZoomPct;
        (zoomSlider.Minimum, zoomSlider.Maximum, zoomSlider.TickFrequency) =
            gfx ? (400.0, 1600.0, 100.0)      // whole screen pixels per GFX pixel, 4x to 16x
                : (100.0, 800.0, 10.0);
        zoomSlider.Value = want;
        ApplyZoom();                          // in case the value never changed
    }

    /// <summary>Push the slider's percent onto the canvas it is driving, and remember it there.</summary>
    private void ApplyZoom()
    {
        double pct = zoomSlider.Value;
        zoomLabel.Text = $"{pct:0}%";
        if (modeGfx?.IsChecked == true)
        {
            gfxZoomPct = pct;
            gfxCanvas.Zoom = pct / 100.0;
            gfxCanvas.InvalidateMeasure();
            gfxCanvas.InvalidateVisual();
        }
        else
        {
            levelZoomPct = pct;
            canvas.Zoom = pct / 100.0;
            canvas.InvalidateVisual();
            canvas.InvalidateMeasure();
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

    /// <summary>Upload after a sprite edit: the session repainted the phases in place, so only
    /// the bitmap needs pushing — no sheet or drawer is affected by a sprite list change.</summary>
    private void PushSpritePixels()
    {
        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateVisual();
    }

    // ---- handlers referenced from XAML ----

    private static FilePickerFileType RomType => new("SNES ROM") { Patterns = ["*.smc", "*.sfc"] };
    private static FilePickerFileType ProjectType => new("pipe-dream project") { Patterns = ["*.pdp"] };

    /// <summary>Pick a GFX file by sight. Returns null when the browser was cancelled.</summary>
    private async Task<int?> PickGfxFile(string purpose)
    {
        var dlg = new GfxBrowserWindow(session, purpose);
        await dlg.ShowDialog(this);
        return dlg.Picked;
    }

    /// <summary>
    /// Load a graphics file. With a drawer bin selected this is the two-sided gesture: the file
    /// REPLACES that bin for this level (a Super GFX Bypass override, recorded in the project) and
    /// opens in the editor. With no bin selected it only opens — Load must not rewire a level
    /// slot nobody pointed at.
    /// </summary>
    private async void OnBrowseGfx(object? sender, RoutedEventArgs e)
    {
        var slot = session.GfxBins.Where(b => b.BypWord == gfxSlot)
                          .Select(b => ((string Name, int PalRow)?)(b.Name, b.PalRow))
                          .FirstOrDefault();
        if (await PickGfxFile(slot is { } s ? $"Load into this level's {s.Name} bin"
                                            : "Open a graphics file in the tile editor") is not { } picked)
            return;

        if (slot is { } bin)
        {
            status.Text = session.SetGfxSlot(gfxSlot, picked);
            gfxPalRow.SelectedIndex = bin.PalRow;
            AdoptSession();                     // the level draws through the new file now
        }
        session.GfxPixels?.Open(picked);
        RefreshGfx();
    }

    /// <summary>Save the edited sheet as a custom ExGFX. A stock file is being forked out into one
    /// for the first time, so it needs a name — an existing custom file already has both.</summary>
    private async void OnSaveGfx(object? sender, RoutedEventArgs e)
    {
        string name = "";
        if (session.GfxIsStock)
        {
            var dlg = new TextPromptWindow("Name for the new ExGFX file", "");
            await dlg.ShowDialog(this);
            if (dlg.Result is not { } picked) return;          // cancelled: nothing saved
            name = picked;
        }
        status.Text = session.SaveGfx(name);
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
            status.Text = session.Status;
            AdoptSession();
            levelBox.SelectedIndex = session.LevelNum;
            return;
        }
        status.Text = session.Status;
        if (session.PendingBaseProblem is null) return;      // a real failure, not a missing base

        while (session.PendingBaseProblem is { } problem)
        {
            var dlg = new LocateBaseWindow(session.PendingProjectName ?? "project", problem,
                                           session.PendingBaseDescription);
            await dlg.ShowDialog(this);
            if (dlg.Located is not { } rom) { session.CancelPendingOpen(); return; }
            if (session.AdoptPendingBase(rom) is null) break;
        }
        status.Text = session.Status;
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e) => await NewProjectFlow();

    /// <summary>New project: pick the folder to create it in, then the base ROM. A verified
    /// vanilla base is prepped automatically, which is why no "prep?" question is asked.</summary>
    private async Task NewProjectFlow()
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
        gfxSave.IsEnabled = session.GfxDirty;    // Ctrl+S saved the pixels too
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

    /// <summary>Course Bot: named entry levels, managed in a modal. Opening one jumps the
    /// editor to its slot through the level box, which drives the whole ShowLevel flow.</summary>
    private async void OnCourseBot(object? sender, RoutedEventArgs e)
    {
        if (!session.HasProject)
        {
            status.Text = "open a project first — Course Bot lives in the .pdp";
            return;
        }
        var dlg = new CourseBotWindow(session);
        await dlg.ShowDialog(this);
        status.Text = session.Status;
        if (dlg.Picked is { } lv && lv != levelBox.SelectedIndex) levelBox.SelectedIndex = lv;
        else AdoptSession();          // a delete may have reverted the level on screen
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
        AdoptSession();
        // AFTER the adopt: it ends in UpdateStatus, which was overwriting this the moment it was
        // set — including the one message that explains why the click did nothing.
        if (note.Length > 0) status.Text = note;
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
        // Deliberately NOT disabled when layer 2 is a background image. Most levels are one, so
        // the button spent most of its life greyed out and clicking it did nothing at all —
        // whereas SetEditLayer already has the answer ("use +L2 to give it an object layer") and
        // can only say it if the click gets through.
        layerTwo.IsChecked = session.EditLayer == 1;
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
        session.LivePhase = phase;      // the phase a live recolour has to keep current
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
    /// <summary>Which drawer tab is the Palette one (see the TabStrip in the XAML).</summary>
    internal const int PaletteTabIndex = 3;

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

        // The tabs choose what the drawer shows FOR THE LEVEL. Map16 and GFX modes own the
        // drawer outright, so a tab strip whose every option is inert only invites a click that
        // does nothing.
        paletteTabs.IsVisible = !modal;

        this.GetControl<ScrollViewer>("PaletteScroll").IsVisible = tab == 0;
        this.GetControl<DockPanel>("ChrPanel").IsVisible = map16Mode;
        gfxToolPanel.IsVisible = gfxMode;
        gfxPaletteBar.IsVisible = gfxMode;      // canvas-side, but the same mode decides it
        spritePanel.IsVisible = tab == 1;
        objectPanel.IsVisible = tab == 2;
        palettePanel.IsVisible = tab == 3;
        // The bank/size row drives the Map16 sheet in both the picker and the Map16 canvas;
        // it means nothing to a catalog or to the pixel editor.
        paletteBar.IsVisible = map16Mode || tab == 0;

        if (spritePanel.IsVisible) EnsureSpriteCatalog();
        if (objectPanel.IsVisible) EnsureObjectCatalog();
        if (palettePanel.IsVisible) RefreshPaletteTab();
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

    private void SetGfxTool(GfxEdit.Tool tool)
    {
        if (session.GfxPixels is { } g) g.Current = tool;
        gfxPencil.IsChecked = tool == GfxEdit.Tool.Pencil;
        gfxFill.IsChecked = tool == GfxEdit.Tool.Fill;
        gfxErase.IsChecked = tool == GfxEdit.Tool.Eraser;
        gfxDropper.IsChecked = tool == GfxEdit.Tool.Dropper;
    }

    private void OnGfxTool(object? sender, RoutedEventArgs e)
        => SetGfxTool(ReferenceEquals(sender, gfxFill) ? GfxEdit.Tool.Fill
                    : ReferenceEquals(sender, gfxErase) ? GfxEdit.Tool.Eraser
                    : ReferenceEquals(sender, gfxDropper) ? GfxEdit.Tool.Dropper
                    : GfxEdit.Tool.Pencil);

    /// <summary>Take the colour under a sheet pixel as the paint colour — the eyedropper tool and
    /// the right-click shortcut are the same act.</summary>
    private void PickGfxColor(int px, int py)
    {
        if (session.GfxPixels?.ColorAt(px, py) is not { } c) return;
        session.GfxPixels.Color = c;
        gfxColors.Select(c);
    }

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
        // The file, by name where it has one. The badge says which kind it is, so the note is
        // only the id — and only when the name is not already showing it.
        bool stock = session.GfxIsStock;
        gfxFileName.Text = g.Name ?? $"GFX{g.File:X3}";
        gfxFileNote.Text = g.Name is null ? "" : $"GFX{g.File:X3}";
        gfxKind.Data = (StreamGeometry)this.FindResource(stock ? "IconCircleCheck" : "IconStar")!;
        gfxKind.Classes.Set("custom", !stock);
        ToolTip.SetTip(gfxKind, stock ? "a base ROM graphics file" : "a custom ExGFX file");
        gfxSave.IsEnabled = session.GfxDirty;
        gfxEmptyLoad.IsVisible = g.Layout.Tiles == 0;      // nothing to paint on — offer Load
        SetGfxTool(g.Current);
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

        RefreshGfxBins();          // the bins list IS the file picker now
    }

    /// <summary>
    /// The GFX drawer: one block per VRAM bin — what it holds, how it got there, and the three ways
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

            // No per-bin Import/Browse buttons: the header's Load covers both, and ten cards each
            // carrying two buttons buried the thing the card is actually for — its sheet.
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(head);

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

            // The whole block IS the "select this bin" target — selecting a bin and editing its
            // file are the same gesture, so a separate Edit button would be a second way to do one
            // thing. The selected bin carries the accent border, as a selected swatch does, and it
            // is what the header's Load fills.
            bool open = bin.BypWord == gfxSlot;
            var card = new Border
            {
                Child = block,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(open ? 1.5 : 1),
                BorderBrush = open ? UiColors.Accent : this.FindResource("BorderBrush") as IBrush,
                // Transparent, never null: a null background is not hit-testable, so the card
                // would take no clicks except on the controls inside it.
                Background = open ? UiColors.SelectionFill : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            // An UNUSED bin (0x7F) is clickable too: selecting it is how it gets given something.
            card.PointerPressed += (_, e) =>
            {
                // Not when the click landed on the id box or a button inside the card.
                if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
                if (e.Source is Visual t && t.FindAncestorOfType<TextBox>() is not null) return;
                gfxSlot = bypWord;
                EditGfxFile(file, palRow);
            };
            gfxBins.Children.Add(card);
        }
    }

    /// <summary>The drawer bin the header's Load fills, as its bypass word. -1 = none, and then
    /// Load only opens a file for editing.</summary>
    private int gfxSlot = -1;

    /// <summary>Open a bin's file in the GFX canvas mode. An unused bin (0x7F) resolves nowhere and
    /// is opened all the same: the canvas then shows its Load button instead of the last file's
    /// pixels, which is the honest answer to "what is in this bin".</summary>
    private void EditGfxFile(int file, int palRow)
    {
        if (session.GfxPixels is not { } g) return;
        g.Open(file);
        gfxPalRow.SelectedIndex = palRow;
        OnMode(modeGfx, new RoutedEventArgs());
    }

    // ---- palette tab ----

    /// <summary>Guard against the picker firing while it is being LOADED from a selection —
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

    /// <summary>The readout under the swatch grid. Deliberately does NOT touch the picker: every
    /// commit recomposes and refreshes this tab, and pushing the colour back into an open picker
    /// would re-derive H/S/V from the quantised value, jumping the crosshair and losing the hue
    /// mid-drag. Loading the picker is <see cref="OpenPicker"/>'s job and happens once, on open.</summary>
    private void ShowPaletteColor(int index)
        => paletteIndex.Text = index < 0 ? "pick a colour" : DescribeSwatch(index);

    /// <summary>The swatch hover text, as the ImGui grid had it.</summary>
    private string DescribeSwatch(int index)
        => $"0x{index:X2} r{index >> 4} c{index & 15}  {session.PaletteBgr(index):X4}"
         + (session.IsPaletteEdited(index) ? "  (edited)" : "");

    /// <summary>Load the picker with the clicked swatch and pop it over the cursor — ImGui
    /// opened its ColorPicker3 in a popup on the swatch, and that is the gesture being restored.
    /// BGR555 is five bits per channel and the picker works in that space directly, so nothing
    /// is quantised behind the user's back the way a 24-bit picker would.</summary>
    private void OpenPicker()
    {
        if (paletteGrid.Selected < 0) return;
        loadingSwatch = true;
        picker.Begin(session.PaletteBgr(paletteGrid.Selected));
        loadingSwatch = false;
        pickerFlyout.ShowAt(paletteGrid, showAtPointer: true);
    }

    /// <summary>
    /// Apply a picked colour to the level, live. There is no debounce: a colour change now
    /// recomposes only the phase on screen and reuses its buffer, which is ~26ms rather than the
    /// ~75ms a full scene rebuild cost, so it can keep up with the drag. The picker also only
    /// raises this when the QUANTISED colour actually changes, which caps it at 32 steps an axis.
    ///
    /// Only the level image and this tab are refreshed. The Map16 sheet and the rest of the
    /// drawer are recoloured too, but nobody is looking at them mid-drag; AdoptSession brings
    /// them up to date when the picker closes.
    /// </summary>
    private void OnPickerColor(ushort bgr)
    {
        if (loadingSwatch || paletteGrid.Selected < 0) return;
        if (!session.SetPaletteColor(paletteGrid.Selected, bgr)) return;

        bitmap.SetImages(session.Phases, session.PxW, session.PxH, canvas.Phase);
        canvas.InvalidateVisual();
        RefreshPaletteTab();
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
        ApplyZoomTarget(gfx);          // the gutter control follows the canvas it is driving

        RefreshDrawer();
        map16Props.IsVisible = map16;
        if (map16)
        {
            RefreshMap16Sheet();
            RefreshMap16Props();
            map16Canvas.Focus();
            status.Text = "Map16 — right-drag stamps the 8x8 brush; X/Y/P flip the quadrant under the cursor";
        }
        else if (gfx)
        {
            // Entered from the header rather than a bin click: adopt whichever bin holds the file
            // the editor is already on, so the drawer shows what Load would replace.
            if (gfxSlot < 0 && session.GfxPixels is { } gp)
                gfxSlot = session.GfxBins.Where(b => b.File == gp.File)
                                 .Select(b => (int?)b.BypWord).FirstOrDefault() ?? -1;
            RefreshGfx();
            gfxCanvas.Focus();
            status.Text = "GFX — left paints, right picks a colour, F switches tool, [ ] zooms";
        }
        else UpdateStatus();
        canvas.InvalidateVisual();
    }

    // ---- Map16 properties inspector ----

    /// <summary>Guard so filling the fields from the selection does not read back as edits.</summary>
    private bool loadingM16Props;

    /// <summary>
    /// Show the selected tile's properties. The controls reflect the FIRST tile of a selection and
    /// apply to all of it — the ImGui behaviour, and the only sane one when a lasso can cover
    /// tiles that disagree.
    /// </summary>
    private void RefreshMap16Props()
    {
        if (map16 is not { } m16) return;
        var tiles = map16Canvas.SelectedTiles().ToList();
        int first = tiles[0];
        m16SelLabel.Text = tiles.Count > 1
            ? $"{tiles.Count} tiles selected"
            : $"tile 0x{first:X4}";

        var def = m16.ReadDef(first);
        m16Fields.IsVisible = def is not null;
        m16Unallocated.IsVisible = def is null;
        if (def is null) return;

        loadingM16Props = true;
        // Acts-like is an FG concept and needs LM's table; say which is missing rather than
        // showing a box that does nothing.
        bool acts = m16.HasActsAs && first < 0x4000;
        m16Acts.IsEnabled = acts;
        m16Acts.Text = m16.ActsAs(first) is { } a ? $"{a:X3}" : "";
        m16ActsNote.Text = acts ? "" : first >= 0x4000 ? "n/a for BG tiles" : "no LM acts-like table";
        m16Priority.IsChecked = def[0].Priority;
        m16Palette.SelectedIndex = def[0].Palette;
        loadingM16Props = false;
    }

    private void ApplyM16Acts()
    {
        if (loadingM16Props || map16 is not { } m16) return;
        if (!int.TryParse(m16Acts.Text, System.Globalization.NumberStyles.HexNumber, null, out int v))
        { RefreshMap16Props(); return; }
        if (m16.SetActsAs(map16Canvas.SelectedTiles(), v))
            status.Text = $"acts-like ← 0x{v & 0x3FFF:X3}";
    }

    private void OnFlipX(object? sender, RoutedEventArgs e) => FlipM16(vertical: false);
    private void OnFlipY(object? sender, RoutedEventArgs e) => FlipM16(vertical: true);

    private void FlipM16(bool vertical)
    {
        map16?.Flip(map16Canvas.SelectedTiles(), vertical);
        RefreshMap16Props();
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
        session.RecomposeAfterMap16();
        AdoptSession();
        RefreshMap16Sheet();
    }
}
