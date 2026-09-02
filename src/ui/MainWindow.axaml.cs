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
    private ToggleButton grain16 = null!, grain8 = null!;

    /// <summary>Which CGRAM row the 8x8 picker draws in and stamps with. ponytail: a constant
    /// while the Map16 drawer has no controls — the row picker, and the flip/priority flags that
    /// sat beside it, came off the drawer header pending a new home. The flags are still
    /// reachable on the canvas itself, where X / Y / P toggle them per quadrant.</summary>
    private const int ChrPalRow = 2;
    /// <summary>Map16 definition editing. Owned by the session, because it is rebuilt whenever
    /// the level's tileset changes and the window has no way to know when that happened.</summary>
    private Map16Edit? map16 => session.Map16;

    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!;
    private TextBlock readout = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!;
    private TabStrip paletteTabs = null!;
    private DockPanel spritePanel = null!, objectPanel = null!, palettePanel = null!;
    private CheckBox paletteLayer3 = null!;
    private GfxCanvasView gfxCanvas = null!;
    private Avalonia.Controls.Shapes.Path gfxKind = null!;
    private Button gfxSave = null!, gfxSaveAs = null!, gfxEmptyLoad = null!;
    private TextBlock gfxFileName = null!;
    private ToggleButton gfxPencil = null!, gfxFill = null!, gfxErase = null!, gfxDropper = null!,
                         gfxSelect = null!, gfxRect = null!, gfxEllipse = null!, gfxLine = null!;
    private ToggleButton gfxRectOutlineBtn = null!, gfxRectFilledBtn = null!,
                         gfxEllipseOutlineBtn = null!, gfxEllipseFilledBtn = null!;
    private Avalonia.Controls.Shapes.Path gfxRectIcon = null!, gfxEllipseIcon = null!;
    private Button gfxRotL = null!, gfxRotR = null!, gfxFlipH = null!, gfxFlipV = null!;
    private DockPanel gfxToolPanel = null!, gfxScroll = null!;
    private Border gfxPaletteBar = null!;
    private StackPanel gfxBins = null!;
    private ComboBox gfxPalRow = null!, gfxBpp = null!;
    private PaletteGridView gfxColors = null!;
    private TextBlock gfxPalNote = null!;
    private MenuItem recentMenu = null!, upgradePrepItem = null!, spriteOverlayItem = null!,
                     animateItem = null!, runEmulatorItem = null!, layer3PreviewItem = null!;
    private PaletteGridView paletteGrid = null!, paletteBg = null!;
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
    private ToggleButton modeAnim = null!, modeBg = null!;
    private DockPanel animPane = null!, animToolPanel = null!;
    private DockPanel bgPane = null!, bgToolPanel = null!;
    private ToggleButton bgLayer2 = null!, bgLayer3 = null!;
    private Button bgOptions = null!, bgImportMap = null!, bgExportMap = null!, bgTilemaps = null!;
    private TilemapView bgView = null!, bgSheet = null!;

    /// <summary>What a stamp writes: a BG Map16 tile on layer 2, a whole BG3 word on layer 3
    /// (palette group 2 by default — the eyedropper is how any other one is picked up).</summary>
    private int bgBrush = 0x100, bgBrushL3 = 2 << 10;
    private TextBlock bgNote = null!, bgDrawerTitle = null!, bgPalNote = null!;
    private Border bgPaletteBar = null!;
    private Button bgApplyPal = null!;
    private ComboBox bgPalRow = null!;
    private PaletteGridView bgColors = null!;
    private bool loadingBgPalRow;
    private StackPanel animGfx = null!, animBody = null!;
    private TextBlock animTitle = null!, animListTitle = null!;
    private Button animDelete = null!, animReassign = null!, animEmptyAdd = null!;
    private CheckBox animAdvanced = null!;
    private StackPanel animPreviewBody = null!;
    private ToggleButton animLevelBtn = null!, animGlobalBtn = null!;
    private ComboBox animFile = null!, animPalRow = null!;
    private Border animPaletteBar = null!;
    private PaletteGridView animColors = null!;
    private ToggleButton layerOne = null!, layerTwo = null!, exitsMode = null!, entrancesMode = null!;
    private Button dropLayer2 = null!;
    private TextBlock layer2Note = null!;
    private TextBlock m16SelLabel = null!, m16ActsNote = null!, m16Unallocated = null!;
    private StackPanel m16Fields = null!;
    private TextBox m16Acts = null!;
    private ToggleButton m16Priority = null!;
    private ComboBox m16Palette = null!;
    private Border m16PaletteBar = null!;
    private PaletteGridView m16Colors = null!;

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
        split = this.GetControl<Grid>("Split");
        readout = this.GetControl<TextBlock>("Readout");
        zoomLabel = this.GetControl<TextBlock>("ZoomLabel");
        selLabel = this.GetControl<TextBlock>("SelLabel");
        drawer = this.GetControl<Border>("Drawer");
        modeLevel = this.GetControl<ToggleButton>("ModeLevel");
        modeMap16 = this.GetControl<ToggleButton>("ModeMap16");
        modeGfx = this.GetControl<ToggleButton>("ModeGfx");
        modeAnim = this.GetControl<ToggleButton>("ModeAnim");
        modeBg = this.GetControl<ToggleButton>("ModeBg");
        bgPane = this.GetControl<DockPanel>("BgPane");
        bgToolPanel = this.GetControl<DockPanel>("BgToolPanel");
        bgLayer2 = this.GetControl<ToggleButton>("BgLayer2");
        bgLayer3 = this.GetControl<ToggleButton>("BgLayer3");
        bgOptions = this.GetControl<Button>("BgOptions");
        bgImportMap = this.GetControl<Button>("BgImportMap");
        bgExportMap = this.GetControl<Button>("BgExportMap");
        bgTilemaps = this.GetControl<Button>("BgTilemaps");
        bgTilemaps.Click += (_, _) => ShowTilemapMenu();
        bgView = this.GetControl<TilemapView>("BgView");
        bgSheet = this.GetControl<TilemapView>("BgSheet");
        bgSheet.PickOnLeft = true;
        bgSheet.FitWidth = true;          // the sheet is the drawer's width, not a fixed 256px
        // Wired ONCE: RefreshBg runs on every phase tick and mode switch, and re-subscribing
        // there would stack a handler per refresh. The handlers look the layer up instead.
        bgView.Painted += (_, c) => BgPaint(c.Col, c.Row);
        bgView.StrokeEnded += (_, _) => BgStrokeEnded();
        bgView.SelectionChanged += (_, _) => RefreshBgNote();
        bgView.SelectionDragged += (_, d) => BgSelectionDragged(d);
        bgSheet.Picked += (_, c) => BgBrushPicked(c.Col, c.Row);
        bgNote = this.GetControl<TextBlock>("BgNote");
        bgDrawerTitle = this.GetControl<TextBlock>("BgDrawerTitle");
        bgPaletteBar = this.GetControl<Border>("BgPaletteBar");
        bgApplyPal = this.GetControl<Button>("BgApplyPal");
        bgPalNote = this.GetControl<TextBlock>("BgPalNote");
        bgColors = this.GetControl<PaletteGridView>("BgColors");
        bgColors.Rows = 1;
        bgColors.Cell = 20;
        bgColors.Selectable = false;       // a tilemap word picks a GROUP, not a colour in it
        bgColors.ShowHoverIndex = false;
        bgPalRow = this.GetControl<ComboBox>("BgPalRow");
        for (int i = 0; i < Layer3.PaletteGroups; i++) bgPalRow.Items.Add($"{i}");
        bgPalRow.SelectionChanged += (_, _) =>
        {
            if (loadingBgPalRow || bgPalRow.SelectedIndex < 0) return;
            bgBrushL3 = bgBrushL3 & ~0x1C00 | bgPalRow.SelectedIndex << 10;
            RefreshBg();                   // the drawer sheet draws in the brush's group
        };
        // The gutter answers "what palette is this cell using", so it has to follow the cursor.
        bgView.PointerMoved += (_, _) => UpdateReadout();
        bgView.PointerExited += (_, _) => UpdateReadout();
        animPane = this.GetControl<DockPanel>("AnimPane");
        animToolPanel = this.GetControl<DockPanel>("AnimToolPanel");
        animGfx = this.GetControl<StackPanel>("AnimGfx");
        animBody = this.GetControl<StackPanel>("AnimBody");
        animTitle = this.GetControl<TextBlock>("AnimTitle");
        animDelete = this.GetControl<Button>("AnimDelete");
        animReassign = this.GetControl<Button>("AnimReassign");
        animEmptyAdd = this.GetControl<Button>("AnimEmptyAdd");
        animPreviewBody = this.GetControl<StackPanel>("AnimPreviewBody");
        this.GetControl<ScrollViewer>("AnimBodyScroll").Background = UiColors.DeskPattern;   // the timeline on the desk
        animListTitle = this.GetControl<TextBlock>("AnimListTitle");
        animLevelBtn = this.GetControl<ToggleButton>("AnimLevel");
        animGlobalBtn = this.GetControl<ToggleButton>("AnimGlobal");
        animFile = this.GetControl<ComboBox>("AnimFile");
        animAdvanced = this.GetControl<CheckBox>("AnimAdvanced");
        animAdvanced.IsCheckedChanged += (_, _) => RefreshAnim();
        animPalRow = this.GetControl<ComboBox>("AnimPalRow");
        for (int i = 0; i < 16; i++) animPalRow.Items.Add($"{i}");   // all sixteen: a destination can be sprite VRAM too
        animPalRow.SelectedIndex = 2;
        animPaletteBar = this.GetControl<Border>("AnimPaletteBar");
        animColors = this.GetControl<PaletteGridView>("AnimColors");
        animColors.Rows = 1;
        animColors.Cell = 20;
        animColors.Selectable = false;     // shows the row; the tiles over the destination choose it
        // The list's source file is part of its record: changing it rewrites the record with the
        // same slots. The palette row is display-only.
        animFile.SelectionChanged += (_, _) =>
        {
            if (loadingAnimHeader || animFile.SelectedIndex < 0 || !session.HasLevel) return;
            if (animFile.SelectedIndex != session.ExAnimAltFile(animGlobal))
            { session.SetExAnim(animGlobal, session.ExAnimSlots(animGlobal), animFile.SelectedIndex); RefreshAnim(); }
        };
        animPalRow.SelectionChanged += (_, _) => { if (modeAnim.IsChecked == true) RefreshAnim(); };
        layerOne = this.GetControl<ToggleButton>("LayerOne");
        layerTwo = this.GetControl<ToggleButton>("LayerTwo");
        exitsMode = this.GetControl<ToggleButton>("ExitsMode");
        entrancesMode = this.GetControl<ToggleButton>("EntrancesMode");
        dropLayer2 = this.GetControl<Button>("DropLayer2");
        layer2Note = this.GetControl<TextBlock>("Layer2Note");

        canvas.Source = bitmap;
        canvas.PointerMoved += (_, _) => UpdateReadout();
        canvas.PointerExited += (_, _) => UpdateReadout();

        // RIGHT drag stamps the drawer's tile, one undo entry per stroke (ImGui parity: the
        // left button belongs to selection).
        canvas.CellPainted += (_, c) =>
        {
            if (edit is null) return;
            if (edit.TilePlacementBlocked is { } why) return;
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
        };
        canvas.DuplicateRequested += (_, c) =>
        {
            if (edit?.DuplicateSelected(c.X, c.Y) == true) PushDirty();
        };
        canvas.PlaceRequested += (_, c) =>
        {
            if (edit is null || canvas.CatalogObject < 0) return;
            edit.PlaceObject(canvas.CatalogObject, c.X, c.Y);
            PushDirty();
        };
        canvas.DeleteRequested += (_, _) =>
        {
            if (edit?.DeleteSelected() == true) PushDirty();
        };
        canvas.GrabRequested += (_, g) =>
        {
            if (edit is null) return;
            var (tiles, w, h) = edit.GrabTiles(g.X, g.Y, g.W, g.H);
            SetBrush(tiles, w, h);
        };
        // Moving and resizing raise this too, and they change PIXELS — without the push the
        // objects stayed where they were drawn and the edit looked like it had not happened.
        // RefreshPixels is a no-op when nothing is dirty, so a plain selection costs nothing.
        canvas.SelectionChanged += (_, _) => PushDirty();;
        canvas.ExitScreenClicked += async (_, screen) => await EditScreenExit(screen);
        canvas.ExitBadgeClicked += (_, screen) => FollowExit(screen);
        canvas.EntranceMoved += (_, m) =>
        {
            // The drop position is where the cursor was; the session snaps it to what the ROM
            // can store, so the markers are re-read rather than trusting the drag.
            session.MoveEntrance(m.Kind, m.Index, m.X, m.Y);
            RefreshEntranceMarkers();
            UpdateTitle();
        };
        canvas.EntranceEditRequested += async (_, en) =>
        {
            if (en.Kind == EntranceKind.Secondary) await ShowEntrance(en.Index);
            else if (session.MainEntrance is { } me && session.Rom is { } rom)
            {
                var dlg = new EntranceWindow(me, en.Kind, rom.HasFreeMidwayPosition);
                await dlg.ShowDialog(this);
                if (dlg.Applied is { } applied) session.ApplyEntry(applied);
            }
            RefreshEntranceMarkers();
            UpdateTitle();
        };
        canvas.SampleRequested += (_, p) =>
        {
            if (session.SampleCgramIndex(p.X, p.Y) is not { } idx)
            {
                return;
            }
            // Land the user where they can act on it: the Palette tab, that swatch selected.
            paletteTabs.SelectedIndex = PaletteTabIndex;
            paletteGrid.Select(idx);
            paletteBg.Select(idx == 0 ? 0 : -1);
            ShowPaletteColor(idx);
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
        grain16 = this.GetControl<ToggleButton>("Grain16");
        grain8 = this.GetControl<ToggleButton>("Grain8");
        chr.BrushChanged += (_, _) =>
        {
            AdoptChrBrush();
            // Picking an 8x8 tile IS the 8x8 grain — the pick means nothing at the other one, so
            // taking one switches rather than arming a brush that right-click will ignore.
            SetGrain(quad8: true);
        };
        map16Canvas.QuadPainted += (_, q) =>
        {
            if (map16 is null) return;
            // Painting an empty page CREATES it; the allocation relocates the def region, so
            // it has to happen before the quadrant offset is taken.
            if (map16.EnsurePage(q.Tile) is { } why) return;
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
        // A refused move or copy wrote nothing, so there is no stroke to seal — and sealing is
        // what repaints the sheet, so the success path must not skip it.
        map16Canvas.MoveRequested += (_, m) =>
        {
            if (map16?.MoveTiles(map16Canvas.Bank, m.X, m.Y, m.W, m.H, m.Dx, m.Dy) is not null) return;
            map16?.EndStroke();
        };
        map16Canvas.DuplicateRequested += (_, m) =>
        {
            if (map16?.CopyQuads(map16Canvas.Bank, m.X, m.Y, m.W, m.H, m.Dx, m.Dy) is not null) return;
            map16?.EndStroke();
        };
        // Subscribed once, on the session: a committed definition change invalidates the tile
        // caches behind the level, the picker and the sheet alike.
        session.Map16Committed += (_, _) => OnMap16Committed();

        // ---- Map16 tile fields, in the canvas header ----
        m16SelLabel = this.GetControl<TextBlock>("M16SelLabel");
        m16Fields = this.GetControl<StackPanel>("M16Fields");
        m16Unallocated = this.GetControl<TextBlock>("M16Unallocated");
        m16Acts = this.GetControl<TextBox>("M16Acts");
        m16ActsNote = this.GetControl<TextBlock>("M16ActsNote");
        m16Priority = this.GetControl<ToggleButton>("M16Priority");
        m16Palette = this.GetControl<ComboBox>("M16Palette");
        for (int i = 0; i < 8; i++) m16Palette.Items.Add($"{i}");
        m16PaletteBar = this.GetControl<Border>("M16PaletteBar");
        m16Colors = this.GetControl<PaletteGridView>("M16Colors");
        m16Colors.Rows = 1;
        m16Colors.Cell = 20;
        m16Colors.Selectable = false;      // it shows the row, it does not choose within it

        map16Canvas.SelectionChanged += (_, _) => RefreshMap16Props();
        map16Canvas.TilePicked += (_, _) => RefreshMap16Props();
        // Clicking the desk beside the sheet drops the selection. The canvas is only as wide as
        // the tile column, so a click that misses it never reaches it at all — the miss has to be
        // caught out here. A scrollbar is not a miss.
        this.GetControl<ScrollViewer>("Map16Scroll").PointerPressed += (_, e) =>
        {
            if (e.Source is not Control src || ReferenceEquals(src, map16Canvas)
                || src.FindAncestorOfType<ScrollBar>() is not null) return;
            map16Canvas.ClearSelection();
            chr.ClearSelection();          // one deselect covers both surfaces, at either grain
            AdoptChrBrush();               // ...including the footprint the cursor was drawing
            RefreshMap16Props();
        };
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
            RefreshM16Colors(row);
        };

        // The slider is in PERCENT; the canvas scales by a factor.
        zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoomTarget();                // one source of truth for the range, step and value

        bankBox.SelectionChanged += (_, _) =>
        {
            palette.Bank = Math.Max(0, bankBox.SelectedIndex);
            palette.InvalidateVisual();
        };
        var pagesBox = this.GetControl<CheckBox>("PagesBox");
        pagesBox.IsCheckedChanged += (_, _) =>
        {
            palette.ShowPages = pagesBox.IsChecked == true;
            palette.InvalidateVisual();
        };
        palette.SelectionChanged += (_, tile) =>
        {
            selLabel.Text = $"0x{tile:X4}";
            SetBrush(null, 1, 1);          // picking a tile replaces a grabbed brush
        };

        // ---- drawer tabs: Map16 tiles / sprite catalog / object catalog ----
        paletteTabs = this.GetControl<TabStrip>("PaletteTabs");
        spritePanel = this.GetControl<DockPanel>("SpritePanel");
        objectPanel = this.GetControl<DockPanel>("ObjectPanel");
        spriteList = this.GetControl<ListBox>("SpriteList");
        objectList = this.GetControl<ListBox>("ObjectList");
        loadedOnly = this.GetControl<CheckBox>("LoadedOnly");
        spFilesLabel = this.GetControl<TextBlock>("SpFiles");
        objectHint = this.GetControl<TextBlock>("ObjectHint");

        palettePanel = this.GetControl<DockPanel>("PalettePanel");
        paletteLayer3 = this.GetControl<CheckBox>("PaletteLayer3");
        paletteLayer3.PointerEntered += (_, _) => PreviewLayer3Palette(true);
        paletteLayer3.PointerExited += (_, _) => PreviewLayer3Palette(false);
        paletteGrid = this.GetControl<PaletteGridView>("PaletteGrid");
        paletteNote = this.GetControl<TextBlock>("PaletteNote");
        paletteIndex = this.GetControl<TextBlock>("PaletteIndex");

        paletteGrid.IsEdited = session.IsPaletteEdited;
        paletteGrid.Describe = DescribeSwatch;
        paletteGrid.SelectionChanged += (_, i) => { paletteBg.Select(-1); ShowPaletteColor(i); OpenPicker(); };
        // The background colour (CGRAM 0) lives in its own swatch above the grid; selection is
        // still paletteGrid.Selected — the swatch just points it at index 0.
        paletteBg = this.GetControl<PaletteGridView>("PaletteBg");
        paletteGrid.HideFirst = true;
        paletteBg.IsEdited = _ => session.IsPaletteEdited(0);
        paletteBg.Describe = _ => DescribeSwatch(0);
        paletteBg.SelectionChanged += (_, _) => { paletteGrid.Select(0); ShowPaletteColor(0); OpenPicker(); };
        picker.ColorChanged += (_, c) => OnPickerColor(c);
        pickerFlyout.Content = picker;
        // The open picker IS the undo boundary, as it was in the ImGui editor: everything done
        // between opening and dismissing it is one entry, however many colours the drag crossed.
        // Park the animation on phase 0 for the stroke: a live recolour only recomposes the phase
        // ON SCREEN, and phase 0 is the one the session reads a colour back from — animating
        // through the drag would recolour a phase and then show three that still hold the old
        // colour. The timer holds still until the picker closes (see SetAnimating).
        pickerFlyout.Opened += (_, _) => { SetPhase(0); session.BeginPaletteStroke(); };
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
        gfxSave = this.GetControl<Button>("GfxSave");
        gfxSaveAs = this.GetControl<Button>("GfxSaveAs");
        gfxEmptyLoad = this.GetControl<Button>("GfxEmptyLoad");
        gfxPencil = this.GetControl<ToggleButton>("GfxPencil");
        gfxFill = this.GetControl<ToggleButton>("GfxFill");
        gfxErase = this.GetControl<ToggleButton>("GfxErase");
        gfxDropper = this.GetControl<ToggleButton>("GfxDropper");
        gfxSelect = this.GetControl<ToggleButton>("GfxSelect");
        gfxRect = this.GetControl<ToggleButton>("GfxRect");
        gfxRectIcon = this.GetControl<Avalonia.Controls.Shapes.Path>("GfxRectIcon");
        gfxEllipse = this.GetControl<ToggleButton>("GfxEllipse");
        gfxLine = this.GetControl<ToggleButton>("GfxLine");
        gfxEllipseIcon = this.GetControl<Avalonia.Controls.Shapes.Path>("GfxEllipseIcon");
        gfxRectOutlineBtn = this.GetControl<ToggleButton>("GfxRectOutlineBtn");
        gfxRectFilledBtn = this.GetControl<ToggleButton>("GfxRectFilledBtn");
        gfxEllipseOutlineBtn = this.GetControl<ToggleButton>("GfxEllipseOutlineBtn");
        gfxEllipseFilledBtn = this.GetControl<ToggleButton>("GfxEllipseFilledBtn");
        gfxRotL = this.GetControl<Button>("GfxRotL");
        gfxRotR = this.GetControl<Button>("GfxRotR");
        gfxFlipH = this.GetControl<Button>("GfxFlipH");
        gfxFlipV = this.GetControl<Button>("GfxFlipV");
        gfxToolPanel = this.GetControl<DockPanel>("GfxToolPanel");
        gfxPaletteBar = this.GetControl<Border>("GfxPaletteBar");
        gfxBins = this.GetControl<StackPanel>("GfxBins");
        gfxPalRow = this.GetControl<ComboBox>("GfxPalRow");
        gfxBpp = this.GetControl<ComboBox>("GfxBpp");
        gfxColors = this.GetControl<PaletteGridView>("GfxColors");
        gfxPalNote = this.GetControl<TextBlock>("GfxPalNote");
        gfxColors.Rows = 1;
        gfxColors.Cell = 20;

        gfxPalRow.SelectionChanged += (_, _) =>
        {
            if (refillingGfxRows || session.GfxPixels is not { } g) return;
            if (gfxPalRow.SelectedItem is not int value) return;
            SetGfxPalValue(g, value);
            RefreshGfx();
        };
        // The two depths the SNES actually DISPLAYS: 4bpp for FG/BG and sprite tiles, 2bpp for
        // layer 3. SMW storing most files as three planes is a storage fact, not a display one
        // — the upload expands them to four, with plane 3 zero, which is exactly what leaves
        // colours 8-15 unreachable until the base is converted. So "4 bpp" means "read this as
        // tile data", at whatever stride this ROM stores tile data at.
        gfxBpp.ItemsSource = new List<object> { "4 bpp", "2 bpp" };
        gfxBpp.SelectionChanged += (_, _) =>
        {
            if (refillingGfxRows || session.GfxPixels is not { } g) return;
            if (gfxBpp.SelectedIndex is not (0 or 1)) return;
            g.ViewAs(gfxBpp.SelectedIndex == 1 ? 2 : 4);
            RefreshGfx();
        };
        gfxColors.ShowHoverIndex = true;
        // The back half of the row exists on the SNES (tiles display 4bpp) but a 3bpp-stored
        // file has no plane to hold colours 8-15, so they show greyed rather than absent.
        gfxColors.IsDisabled = i => i > (session.GfxPixels?.MaxColor ?? 15);
        gfxColors.Describe = i => i == 0 ? "transparent — the eraser paints this"
            : i > (session.GfxPixels?.MaxColor ?? 15)
                ? session.GfxPixels?.Bpp == 2
                    ? $"colour {i} — layer 3 is 2bpp, so this file holds colours 0-3"
                    : $"colour {i} — this base still stores three bit planes, so the file has "
                      + "nothing to hold colours 8-15 in"
                : $"colour {i}";
        gfxColors.SelectionChanged += (_, i) =>
        {
            // Index 0 IS the eraser: it is the transparent slot, so choosing it means "paint
            // transparent" and the tool that does that is the one to switch to.
            if (i == 0) { SetGfxTool(GfxEdit.Tool.Eraser); return; }
            if (session.GfxPixels is { } g) g.Color = i;
        };

        gfxCanvas.PixelPainted += (_, p) =>
        {
            if (session.GfxPixels is not { } g) return;
            // The eyedropper takes rather than paints, so left-click with it does what right-click
            // does with every other tool.
            if (g.Current == GfxEdit.Tool.Dropper) { PickGfxColor(p.X, p.Y); return; }
            if (!g.Paint(p.X, p.Y, out bool forked)) return;
            RefreshGfxSheet();                    // live feedback, without a level recompose
        };
        gfxCanvas.StrokeEnded += (_, _) =>
        {
            session.GfxPixels?.EndStroke();
            gfxSave.IsEnabled = session.GfxDirty;         // the stroke is what there is to save
        };
        // A rectangle is one gesture and one undo entry: the canvas reports the shape, the
        // editor writes every pixel into a single stroke and closes it.
        gfxCanvas.ShapeDragged += (_, r) =>
        {
            if (session.GfxPixels is not { } g) return;
            if (!g.PaintShape(r.X0, r.Y0, r.X1, r.Y1, out bool _)) return;
            g.EndStroke();
            RefreshGfxSheet();
            AdoptSession();                      // the level's tiles change with the pixels
            gfxSave.IsEnabled = session.GfxDirty;
        };
        // The live preview asks the SAME routine the drag will paint with, so what is on the
        // glass while dragging is exactly what lands on release.
        gfxCanvas.ShapeInk = d => session.GfxPixels is not { } g ? null
            : (g.ShapePixels(d.X0, d.Y0, d.X1, d.Y1),
               session.PaletteRgba[g.BaseColor + g.Color]);
        gfxCanvas.ColorPicked += (_, p) => PickGfxColor(p.X, p.Y);
        // F cycles the tools in enum order rather than toggling two. Counted off the enum so
        // adding a tool cannot leave the last one unreachable.
        gfxCanvas.ToolToggled += (_, _) =>
        {
            if (session.GfxPixels is { } g)
                SetGfxTool((GfxEdit.Tool)(((int)g.Current + 1) % Enum.GetValues<GfxEdit.Tool>().Length));
        };
        // Grabbing a selection LIFTS it onto the floating layer, exactly as a paste arrives
        // there: the block leaves a hole where it was and rides above everything else until it
        // is dropped, so passing it over pixels does not eat them and letting go is not a
        // commitment. The drop — a click elsewhere, or any way out of the mode — is the edit.
        gfxCanvas.SelectionMoveStarted += (_, r) => LiftGfxSelection(r);
        gfxCanvas.FloatDropRequested += (_, _) => CommitGfxFloat();
        // Rotate and flip act on the marquee, so they follow it wherever it changes.
        gfxCanvas.SelectionChanged += (_, _) => RefreshGfxXform();
        gfxCanvas.ZoomStepped += (_, d) => StepZoom(d);
        // Every canvas feeds the same gutter readout; exiting blanks it.
        foreach (var c in new Control[] { map16Canvas, gfxCanvas })
        {
            c.PointerMoved += (_, _) => UpdateReadout();
            c.PointerExited += (_, _) => UpdateReadout();
        }
        gfxCanvas.PalRowStepped += (_, d) => StepGfxPalRow(d);

        paletteTabs.SelectionChanged += (_, _) => OnPaletteTab();
        loadedOnly.IsCheckedChanged += (_, _) => ApplySpriteFilter();
        spriteList.SelectionChanged += (_, _) =>
        {
            if (spriteList.SelectedItem is not CatalogRow it) { canvas.CatalogSprite = -1; return; }
            canvas.CatalogSprite = it.Number;
        };
        objectList.SelectionChanged += (_, _) =>
        {
            if (objectList.SelectedItem is not CatalogRow it) { canvas.CatalogObject = -1; return; }
            canvas.CatalogObject = it.Number;
            canvas.InvalidateVisual();
        };

        for (int i = 0; i < EditorSession.LevelCount; i++) levelBox.Items.Add($"${i:X3}");
        levelBox.SelectionChanged += OnLevelChanged;

        drawer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) OnDrawerVisibilityChanged();
        };

        ApplyDrawerPane(Pane.Level);   // sized for the picker's own zoom, which nothing drives yet

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

        this.GetControl<MenuItem>("DebugMenu").IsVisible = Program.DevMode;

        // An explicit ROM argument opens projectless — that is the test suite's and the
        // command line's hatch, not a user path. A .pdp argument is a PROJECT and waits for
        // OnFirstOpened: opening one can need recovery dialogs, which need the window up.
        // A normal launch starts empty and the startup chooser asks for a project.
        if (Program.RomPath is { } romArg && !IsProjectPath(romArg)
            && EditorSession.FileExists(romArg)) LoadRom(romArg);

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
    private static bool IsProjectPath(string p) => p.EndsWith(".pdp", StringComparison.OrdinalIgnoreCase);

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        // --vanilla configures the base ROM before anything can ask for it — the dev launch's
        // way of never seeing the first-run prompt, even on a config the test suite reset.
        //
        // Only when nothing usable is saved. The flag exists to SKIP that prompt, never to
        // overwrite the ROM picked in File → Set vanilla ROM… — and since --dev now supplies
        // it from PIPEDREAM_SMW_ROOT, an unconditional set would silently rewrite the saved
        // path on every F5. A saved path that has since gone missing is still repaired.
        if (!EditorSession.FileExists(session.VanillaRomPath)
            && Program.VanillaPath is { } van && EditorSession.FileExists(van)) session.SetVanillaRom(van);
        if (session.NeedsVanillaRom)
        {
            var dlg = new FirstRunWindow();
            await dlg.ShowDialog(this);
            if (dlg.Chosen is { } rom)
            {
                session.SetVanillaRom(rom);
            }
        }

        // A .pdp argument opens that project — and in dev mode one that does not exist yet is
        // created from the vanilla ROM, so the F5 profile works before any project was made.
        if (!session.HasRom && Program.RomPath is { } arg && IsProjectPath(arg))
        {
            if (!EditorSession.FileExists(arg) && Program.DevMode
                && EditorSession.FileExists(session.VanillaRomPath))
            {
                session.NewProject(Path.GetDirectoryName(Path.GetFullPath(arg))!, session.VanillaRomPath!);
                AdoptSession();
                levelBox.SelectedIndex = session.LevelNum;
            }
            else await OpenProjectPath(arg);
        }

        // Pick up where the last session left off. The recent list is pruned of anything that has
        // moved or been deleted, so its head is the last project that can actually be opened —
        // and a base-ROM problem still routes through the recovery flow rather than being
        // swallowed. Anything that leaves nothing open falls through to the chooser. An explicit
        // argument that failed to open must NOT silently fall back to some other project.
        if (!session.HasRom && Program.RomPath is null && session.RecentProjects.FirstOrDefault() is { } last)
            await OpenProjectPath(last);

        if (!session.HasRom) await PromptForProject();

        // A dev launch runs source newer than any release — an update prompt is only noise.
        // Help ▸ Check for updates still works; that is an explicit ask.
        if (Program.DevMode) return;

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
        string? problem = null;
        while (!session.HasRom)
        {
            var dlg = new StartWindow(session.RecentProjects, problem);
            string? before = session.Status;
            await dlg.ShowDialog(this);
            if (dlg.OpenRecent is { } pdp) await OpenProjectPath(pdp);
            else if (dlg.CreateNew) await NewProjectFlow();
            else if (dlg.OpenExisting) await OpenProjectFlow();
            else return;
            // The status line is not on screen yet, so an attempt's report is only visible if the
            // chooser carries it back — otherwise a failed create looks like the dialog ignored you.
            problem = !session.HasRom && session.Status != before ? session.Status : null;
        }
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

    private void LoadRom(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!session.OpenRom(path)) return;
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
        RefreshExitBadges();               // another level, another exit table
        RefreshEntranceMarkers();          // ...and another set of entrances

        if (!session.HasLevel) return;

        bitmap.SetImages(session.Phases, session.PxW, session.PxH, 0);
        canvas.InvalidateMeasure();
        canvas.InvalidateVisual();

        var (px, w, h) = session.SheetPhases();
        palette.SetSheet(px, w, h, session.Map16TileCount);
        palette.SetPlaceholder(session.PlaceholderPhases());

        // Catalogs are rendered with the level's own GFX and palette, so the session has
        // already dropped them; the list has to let go of the old items too.
        spriteList.ItemsSource = null;
        RefreshDrawer();
        RefreshLayerBar();

        // The other canvas modes show THIS level's graphics too: the GFX editor follows the
        // selected bin into the new level's file (an ExAnimation source file 60-63 is ROM-wide
        // and stays put), and the Animations page lists the new level's slots.
        if (modeGfx?.IsChecked == true && session.GfxPixels is { } gp)
        {
            var bin = session.GfxBins.FirstOrDefault(b => b.BypWord == gfxSlot);
            if (bin.Name is not null && bin.File != gp.File)
            {
                CommitGfxFloat();
                gp.Open(bin.File);
                (gp.PalRow, gp.ColorOffset) = GfxPalFor(bin.Bpp, bin.PalRow, bin.ColorOffset);
            }
            RefreshGfx();
        }
        if (modeAnim?.IsChecked == true) RefreshAnim();
        if (modeBg?.IsChecked == true) RefreshBg();
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
        => Title = (session.ProjectName is { } name
            ? $"pipe-dream — {name}{(session.HasUnsavedWork ? " *" : "")}"
            : session.RomFileName is { } file ? $"pipe-dream — {file} (no project)"
            : "pipe-dream") + (Program.DevMode ? "  [dev]" : "");

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
        if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None)
        {
            OnRunEmulator(this, e);              // Lunar Magic's F4
            e.Handled = true;
            return;
        }
        // File → Save. The menu item's InputGesture only DRAWS "Ctrl+S"; Avalonia does not
        // register a gesture from it, so the key has to be handled here like F4. This is a
        // bubbling handler, so a focused text box still gets first refusal.
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            OnSave(this, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UndoRedo(redo: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        // GFX selection clipboard. The clipboard lives in GfxEdit as colour indices, so a copy
        // in one bin pastes into whichever bin is open when Ctrl+V lands. A focused TextBox (a
        // bin's id field) keeps its own Ctrl+C/X/V.
        else if (modeGfx.IsChecked == true && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                 && e.Key is Key.C or Key.X or Key.V && session.GfxPixels is { } gp
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (e.Key == Key.V)
            {
                // Paste FLOATS: the pixels ride above the sheet at the corner until dragged
                // into place; only the drop writes bytes, as ONE undo entry. A float already
                // adrift drops where it lies first.
                CommitGfxFloat();
                if (gp.Clipboard is { } c && gp.Layout.Tiles > 0)
                {
                    SetGfxTool(GfxEdit.Tool.Select);   // the float is dragged, so arm the tool
                    gfxFloat = (null, c.Px);      // a paste has no home to go back to
                    gfxCanvas.ShowFloat(GfxFloatPixels(gp, c.W, c.H, c.Px), c.W, c.H);
                }
            }
            else if (gfxCanvas.Selection is { } s)
            {
                if (e.Key == Key.C) gp.Copy(s.X, s.Y, s.W, s.H);
                else gp.Cut(s.X, s.Y, s.W, s.H);
                RefreshGfxSheet();
                gfxSave.IsEnabled = session.GfxDirty;
            }
            e.Handled = true;
        }
        // Delete on a Map16 selection resets the tiles to the base ROM's definitions. A focused
        // TextBox (the acts-like field) keeps its own Delete.
        else if (e.Key == Key.Delete && modeMap16.IsChecked == true
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (session.ResetMap16Tiles(map16Canvas.SelectedTiles())) RefreshMap16Props();
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
            // First Esc in GFX mode throws away an un-dropped paste or drops the selection;
            // the next one leaves the mode.
            if (modeGfx.IsChecked == true && gfxCanvas.Float is not null)
                DiscardGfxFloat();
            else if (modeGfx.IsChecked == true && gfxCanvas.Selection is not null)
                gfxCanvas.Selection = null;
            // Same shape in the Background tab: the first Esc drops the lasso, so the drawer's
            // tile is armed again; only the next one leaves the mode.
            else if (modeBg.IsChecked == true && bgView.Selection is not null)
                bgView.ClearSelection();
            else if (modeLevel.IsChecked != true) OnMode(modeLevel, new RoutedEventArgs());
            // The overlay modes are modes you can be IN, so Esc is how you leave one — before it
            // gets as far as the layer/sprite cycle, which has no meaning while one is armed.
            else if (canvas.Mode is LevelView.EditMode.Exits or LevelView.EditMode.Entrances)
            {
                exitsMode.IsChecked = entrancesMode.IsChecked = false;
                ApplyOverlayMode(exitsMode);
            }
            else if (brush is not null) SetBrush(null, 1, 1);
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
    }

    // The gutter slider drives whichever canvas is showing, but one percent cannot suit all
    // three, so each mode keeps its own value and its own range. The level opens at 1:1 — the
    // whole point of the level view is how much of the level you can see at once; the Map16
    // sheet opens at 3x, since a 16-tile-wide column at 1:1 is a sliver; and GFX at 8 screen
    // pixels per GFX pixel, which is what the ImGui editor opened at.
    private double levelZoomPct = 100, gfxZoomPct = 800, map16ZoomPct = 300;

    /// <summary>Point the zoom control at a mode: its range, its step, and the value it was left
    /// at. Call it AFTER the mode flags flip — this and <see cref="ApplyZoom"/> read them.</summary>
    private void ApplyZoomTarget()
    {
        bool gfx = modeGfx?.IsChecked == true;
        // Read the wanted value first: narrowing the range coerces Value, which lands in the
        // remembered field on the way through.
        double want = gfx ? gfxZoomPct : modeMap16?.IsChecked == true ? map16ZoomPct : levelZoomPct;
        // The level steps in 10%: a fractional zoom is drawn filtered rather than nearest, so it
        // stays clean (LevelView.Unsampled). The GFX sheet steps in whole multiples instead —
        // pixel editing wants the pixel you click to be exactly the pixel you paint.
        (zoomSlider.Minimum, zoomSlider.Maximum, zoomSlider.TickFrequency) =
            gfx ? (400.0, 1600.0, 100.0)      // whole screen pixels per GFX pixel, 4x to 16x
                : (100.0, 800.0, 10.0);
        zoomSlider.Value = want;
        ApplyZoom();                          // in case the value never changed
    }

    /// <summary>Push the slider's percent onto the canvas it is driving, and remember it there.
    /// The percent is taken at face value — how a fractional one gets DRAWN is the canvas's call
    /// (see <see cref="LevelView.Unsampled"/>).</summary>
    private void ApplyZoom()
    {
        double pct = zoomSlider.Value;
        double zoom = pct / 100.0;
        zoomLabel.Text = $"{pct:0}%";
        if (modeGfx?.IsChecked == true)
        {
            gfxZoomPct = pct;
            gfxCanvas.Zoom = zoom;
            gfxCanvas.InvalidateMeasure();
            gfxCanvas.InvalidateVisual();
        }
        else if (modeMap16?.IsChecked == true)
        {
            // The Map16 sheet is 16x16 cells like the level, so it shares the level's range and
            // 10% step — but not its remembered value: the two are browsed at different sizes.
            map16ZoomPct = pct;
            map16Canvas.Zoom = zoom;
            map16Canvas.InvalidateMeasure();
            map16Canvas.InvalidateVisual();
        }
        else
        {
            levelZoomPct = pct;
            canvas.Zoom = zoom;
            canvas.InvalidateVisual();
            canvas.InvalidateMeasure();
        }
    }

    /// <summary>
    /// The gutter readout: what is under the cursor, in the terms of whichever canvas is showing —
    /// a level cell and its Map16 tile, a Map16 tile and what it acts as, a GFX tile and pixel.
    ///
    /// Blank when the cursor is off the canvas. A last-hovered value that sticks reads as the thing
    /// you are pointing at NOW, which is how a stale tile number gets copied into a bug report.
    /// </summary>
    private void UpdateReadout()
        => readout.Text = modeGfx.IsChecked == true ? GfxReadout()
                        : modeBg.IsChecked == true ? BgReadout()
                        : modeMap16.IsChecked == true ? Map16Readout()
                        : LevelReadout();

    private string LevelReadout()
    {
        if (canvas.HoverCell is not { } c) return "";
        if (session.TileAt(c.X, c.Y) is not { } tile) return $"({c.X,3},{c.Y,2})  empty";
        string acts = map16?.ActsAs(tile) is { } a ? $"  acts 0x{a:X3}" : "";
        return $"({c.X,3},{c.Y,2})  tile 0x{tile:X3}{acts}";
    }

    /// <summary>Which tile is hovered is already in the header, so the gutter spends its width on
    /// the thing nothing else says: what the tile's acts-as code makes it DO. Codes the table has
    /// nothing sourced for read as the bare number — see <see cref="ActsAs"/>.</summary>
    private string Map16Readout()
    {
        if (map16Canvas.HoverQuad is not { } h || map16 is not { } m16) return "";
        if (m16.ReadDef(h.Tile) is null) return "unallocated";
        // Same two reasons the header greys its acts-as box: say which, rather than going blank
        // and leaving the gutter looking broken.
        if (m16.ActsAs(h.Tile) is not { } a)
            return h.Tile >= 0x4000 ? "acts-like: n/a for BG tiles" : "acts-like: no LM table";
        string what = ActsAs.Describe(a);
        return what.Length > 0 ? $"acts 0x{a:X3}  {what}" : $"acts 0x{a:X3}";
    }

    private string GfxReadout()
    {
        if (gfxSlot < 0 || gfxCanvas.Hover is not { } p || session.GfxPixels is not { } g) return "";
        return $"{g.Name ?? $"GFX{g.File:X3}"}  tile 0x{(p.Y / 8) * 16 + p.X / 8:X2}  px ({p.X & 7},{p.Y & 7})";
    }

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
        CommitGfxFloat();                    // before the sheet under it can change
        if (gfxSlot is >= 0x60 and <= 0x63)
        {
            // An ExAnimation source file: Load imports raw 4bpp tiles INTO it (up to 32KB),
            // rather than repointing a bin — there is no bin, slots read the file by offset.
            if (await PickFile($"Import raw 4bpp tiles into ExGFX{gfxSlot:X2}", new FilePickerFileType("GFX") { Patterns = ["*.bin"] }) is not { } path
                || !session.ImportExAnimSource(gfxSlot - 0x60, path)) return;
            session.GfxPixels?.Open(gfxSlot);
            RefreshGfx();
            return;
        }
        var slot = session.GfxBins.Where(b => b.BypWord == gfxSlot)
                          .Select(b => ((string Name, int PalRow, int Bpp, int ColorOffset)?)(b.Name, b.PalRow, b.Bpp, b.ColorOffset))
                          .FirstOrDefault();
        if (await PickGfxFile(slot is { } s ? $"Load into this level's {s.Name} bin"
                                            : "Open a graphics file in the tile editor") is not { } picked)
            return;

        if (slot is { } bin)
        {
            session.SetGfxSlot(gfxSlot, picked);
            if (session.GfxPixels is { } gp)
                (gp.PalRow, gp.ColorOffset) = GfxPalFor(bin.Bpp, bin.PalRow, bin.ColorOffset);
            AdoptSession();                     // the level draws through the new file now
        }
        session.GfxPixels?.Open(picked);
        // The bin's depth outlives the file that was in it: a fresh ExGFX loaded into an LG slot
        // is layer-3 data whatever the ROM stores everything else at. Open() reset the override.
        if (slot is { Bpp: > 0 } l3) session.GfxPixels?.ViewAs(l3.Bpp);
        RefreshGfx();
    }

    /// <summary>Save the edited sheet as a custom ExGFX. A stock file is being forked out into one
    /// for the first time, so it needs a name — an existing custom file already has both.</summary>
    private async void OnSaveGfx(object? sender, RoutedEventArgs e)
    {
        CommitGfxFloat();                    // a paste still adrift belongs in what gets saved
        string name = "";
        if (session.GfxIsStock)
        {
            var dlg = new TextPromptWindow("Name for the new ExGFX file",
                session.GfxPixels is { } gp ? session.DefaultGfxName(gp.File) : "");
            await dlg.ShowDialog(this);
            if (dlg.Result is not { } picked) return;          // cancelled: nothing saved
            name = picked;
        }
        session.SaveGfx(name);
        RefreshGfx();
    }

    /// <summary>Save As: fork the open sheet into a NEW custom ExGFX under a typed name. The
    /// source file keeps its bytes; the editor and this level's bins follow the copy.</summary>
    private async void OnSaveGfxAs(object? sender, RoutedEventArgs e)
    {
        if (session.GfxPixels is not { } g) return;
        CommitGfxFloat();                    // a paste still adrift belongs in what gets saved
        var dlg = new TextPromptWindow("Name for the new ExGFX file", session.DefaultGfxName(g.File));
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } name) return;          // cancelled: nothing saved
        session.SaveGfxAs(name);
        RefreshGfx();
    }

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

    /// <summary>New project: pick the folder to create it in, then the base ROM. A verified
    /// vanilla base is prepped automatically, which is why no "prep?" question is asked.</summary>
    private async Task NewProjectFlow()
    {
        await SettleBeforeNativeDialog();
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the new project", AllowMultiple = false,
        });
        if (dirs.Count == 0 || dirs[0].TryGetLocalPath() is not { } folder) return;

        string? baseRom = EditorSession.FileExists(session.VanillaRomPath)
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

    /// <summary>Dev only: build, then hand the result to Lunar Magic — the check that a base
    /// we prepped is one LM can actually open. A failure has nowhere else to appear (the status
    /// line is not on screen), so it gets said out loud.</summary>
    private async void OnOpenLunarMagic(object? sender, RoutedEventArgs e)
    {
        if (session.OpenInLunarMagic() is not { } problem) { UpdateTitle(); return; }
        UpdateTitle();
        await new ConfirmWindow("Lunar Magic", problem, "OK").ShowDialog(this);
    }

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
        await new ConfirmWindow("Build ROM", session.Status, "OK").ShowDialog(this);
    }

    private async void OnExportBps(object? sender, RoutedEventArgs e)
    {
        session.ExportBps();
        UpdateTitle();
        await new ConfirmWindow("Export BPS", session.Status, "OK").ShowDialog(this);
    }

    /// <summary>Level header + main entrance, staged in a dialog and applied in one go: every
    /// header field forces a full reparse, so live-applying a slider would be unusable.</summary>
    private async void OnLevelProperties(object? sender, RoutedEventArgs e) => await EditLevelProperties();

    private async Task EditLevelProperties()
    {
        if (session.Header is not { } header || session.MainEntrance is not { } entrance) return;
        var dlg = new LevelPropertiesWindow(header, entrance, session.HasHeaderOverride);
        await dlg.ShowDialog(this);

        if (dlg.RevertRequested) { session.RevertHeader(); AdoptSession(); return; }
        // An entrance change repaints too. Most of its fields are spawn bookkeeping the canvas
        // never draws, but the Layer 3 option is in there — without this, giving a level a
        // layer 3 wrote the byte and left the Background tab still saying it has none.
        if (dlg.AppliedEntry is { } en && en != entrance) { session.ApplyEntry(en); AdoptSession(); }
        if (dlg.AppliedHeader is { } h && h != header)
        {
            session.ApplyHeader(h);
            AdoptSession();
        }
        UpdateTitle();
    }

    /// <summary>The graphics header off the GFX drawer: the level's tileset ("layer 1") and
    /// sprite set. Same staged-apply path as the properties dialog — a header change reparses.</summary>
    private async void OnGfxHeader(object? sender, RoutedEventArgs e)
    {
        if (session.Header is not { } h) return;
        var (layer1, sprites) = session.GfxHeaderChoices();
        if (layer1.Count == 0) return;
        var dlg = new GfxHeaderWindow(layer1, h.Tileset, sprites, h.SpriteSet);
        await dlg.ShowDialog(this);
        if (dlg.Result is { } r && (r.Tileset != h.Tileset || r.SpriteSet != h.SpriteSet))
        {
            session.ApplyHeader(h with { Tileset = r.Tileset, SpriteSet = r.SpriteSet });
            AdoptSession();
        }
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

    /// <summary>F4, as in Lunar Magic: build and run. Problems come up in a dialog because
    /// the status line is easy to miss when nothing visibly happened.</summary>
    private async void OnRunEmulator(object? sender, RoutedEventArgs e)
    {
        var problem = session.RunInEmulator();
        UpdateTitle();
        RefreshFileMenu();                       // auto-found emulator now has a name
        if (problem is not null) await new ConfirmWindow("Run in emulator", problem, "OK").ShowDialog(this);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Screen exits, staged in a table and applied as one object edit. "Entrance…" hands
    /// off to the entrance record the exit points at, applying the table on the way so nothing
    /// typed is lost.</summary>
    /// <summary>
    /// Arm or disarm the canvas's exits mode. It TAKES OVER from the layer being edited rather
    /// than sitting beside it: while it is on, the layer toggles are dead, the canvas paints no
    /// selection, and a click means "this screen", not "this object".
    /// </summary>
    private void OnExitsMode(object? sender, RoutedEventArgs e) => ApplyOverlayMode(exitsMode);
    private void OnEntrancesMode(object? sender, RoutedEventArgs e) => ApplyOverlayMode(entrancesMode);

    /// <summary>
    /// Arm or disarm one of the level's overlay modes. They are the two halves of a connection —
    /// where a level leads and where it is entered — and each TAKES OVER from the layer being
    /// edited, so they are exclusive with each other as well: arming one disarms the other,
    /// rather than leaving two modes both claiming the canvas.
    /// </summary>
    private void ApplyOverlayMode(ToggleButton clicked)
    {
        if (clicked.IsChecked == true)
            foreach (var other in new[] { exitsMode, entrancesMode })
                if (!ReferenceEquals(other, clicked)) other.IsChecked = false;

        bool exits = exitsMode.IsChecked == true, entrances = entrancesMode.IsChecked == true;
        canvas.Mode = exits ? LevelView.EditMode.Exits
                    : entrances ? LevelView.EditMode.Entrances
                    : paletteTabs.SelectedIndex == 1 ? LevelView.EditMode.Sprites
                                                     : LevelView.EditMode.Objects;
        layerOne.IsEnabled = layerTwo.IsEnabled = !exits && !entrances;
        edit?.Selection.Clear();
        canvas.Sprites?.Selection.Clear();
        RefreshExitBadges();
        RefreshEntranceMarkers();
    }

    /// <summary>Re-read where this level's entrances put Mario. Cheap — a main record, a midway
    /// screen and a scan of the entrance table — so it runs after every move rather than being
    /// kept in step by hand.</summary>
    private void RefreshEntranceMarkers()
    {
        canvas.Entrances = canvas.Mode == LevelView.EditMode.Entrances ? session.Entrances() : [];
        // ponytail: built once per window from the first level's palette; Mario's own 10 colours
        // come from the ROM, only row 8's shared colours 1-5 could differ between levels.
        if (canvas.MarioIcon is null && session.Rom is { } rom && session.Scene?.Palettes[0] is { } pal
            && PlayerGfx.BigMarioStanding(rom, pal) is { } px)
            canvas.MarioIcon = LevelBitmap.FromPixels(px, 16, 32);
        canvas.InvalidateVisual();
    }

    /// <summary>
    /// Walk the connection: the badge names where a screen leads, so clicking it goes there.
    /// A secondary exit's destination is an INDEX into the entrance table rather than a level,
    /// so that one is resolved through the record — which is the whole reason the view hands
    /// back a screen number and lets this side work out what it means.
    /// </summary>
    private void FollowExit(int screen)
    {
        if (edit?.ReadExits().FirstOrDefault(x => x.Screen == screen) is not { } exit) return;
        int level = exit.Secondary && session.ReadEntrance(exit.Destination) is { } entrance
            ? entrance.DestinationLevel
            : exit.Destination;
        if (level < 0 || level >= EditorSession.LevelCount) return;
        levelBox.SelectedIndex = level;         // the picker IS the load path
    }

    /// <summary>Re-read the exit table the canvas draws its badges from. Cheap enough to run on
    /// every write — it is a handful of objects out of the layer-1 stream.</summary>
    private void RefreshExitBadges()
    {
        canvas.Exits = edit is null || canvas.Mode != LevelView.EditMode.Exits
            ? []
            : [.. edit.ReadExits().Select(x => (x.Screen, x.Destination, x.LmForm))];
        canvas.InvalidateVisual();
    }

    /// <summary>
    /// One screen's destination, asked for over the level itself. Everything else about an exit
    /// — the water and secondary flags, the LM word form — is left exactly as it was found;
    /// clearing the box removes the exit, which is the only other thing this view can mean.
    /// </summary>
    private async Task EditScreenExit(int screen)
    {
        if (edit is null) return;
        var exits = edit.ReadExits();
        var here = exits.FirstOrDefault(x => x.Screen == screen);
        // How wide the destination can be depends on the BASE. A v7-prepped (or LM-saved) ROM
        // takes the level's ninth bit from the exit's own flags, so the whole level range is
        // reachable; on anything older the ninth bit comes from the submap the player entered
        // from, and only the low byte means anything.
        bool high = session.ExitsReachHighLevels;
        int mask = here?.LmForm == true ? 0xFFFF : high ? 0x1FF : 0xFF;
        string range = here?.LmForm == true ? "0000-FFFF" : high ? "000-1FF" : "00-FF, low byte only";

        var dlg = new TextPromptWindow(
            $"Screen {screen:X2} exits to (hex level {range} — blank for none)",
            here is null ? "" : here.Destination.ToString(here.LmForm ? "X4" : high ? "X3" : "X2"));
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } text) return;

        text = text.Trim();
        if (text.Length == 0)
        {
            if (here is null) return;
            exits.Remove(here);
        }
        else if (int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int dest))
        {
            // MASKED, not clamped: the field is as wide as it is. Clamping turned $105 into
            // $FF — a level nobody asked for, written silently.
            dest &= mask;
            if (here is not null) here.Destination = dest;
            else exits.Add(new LevelExit { Screen = screen, Destination = dest });
        }
        else return;                       // not a number: the safe answer is to change nothing

        if (edit.WriteExits(exits)) PushDirty();
        RefreshExitBadges();
        UpdateTitle();
    }

    private async void OnSpriteData(object? sender, RoutedEventArgs e)
    {
        if (session.Sprites is not { } sp || sp.Selection.Count != 1) return;   // menu does nothing without exactly one selected sprite
        int i = sp.Selection.First();
        var dlg = new SpriteDataWindow(sp.Sprites.Sprites[i]);
        await dlg.ShowDialog(this);
        if (dlg.Applied is { } d && sp.SetData(i, d.Number, d.Extra, d.ExtraBytes))
        {
            session.RefreshSprites();
            PushSpritePixels();
            PushDirty();
        }
        UpdateTitle();
    }

    private async void OnLevelExits(object? sender, RoutedEventArgs e)
    {
        if (edit is null) return;
        var dlg = new LevelExitsWindow(edit.ReadExits());
        await dlg.ShowDialog(this);

        if (dlg.Applied is { } exits && edit.WriteExits(exits))
        {
            PushDirty();
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
        session.WriteEntrance(a.Index, a.Entrance);
        UpdateTitle();
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

    private void OnReloadLevel(object? sender, RoutedEventArgs e)
    {
        session.ReloadLevel();
        AdoptSession();
    }

    // ---- layer 2 ----

    private void OnEditLayer(object? sender, RoutedEventArgs e)
    {
        session.SetEditLayer(ReferenceEquals(sender, layerTwo) ? 1 : 0);
        AdoptSession();
    }

    private void OnDropLayer2(object? sender, RoutedEventArgs e)
    {
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
        // whereas SetEditLayer already has the answer and can only say it if the click gets
        // through.
        layerTwo.IsChecked = session.EditLayer == 1;
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
        session.UpgradeBasePrep();
        AdoptSession();
        levelBox.SelectedIndex = session.LevelNum;
    }

    private void OnToggleSprites(object? sender, RoutedEventArgs e)
    {
        session.ShowSprites = !session.ShowSprites;
        AdoptSession();
    }

    /// <summary>
    /// Cycle the four animation phases, as the game does. The phases are already composed — this
    /// only changes which one the bitmap shows, so it costs one image swap rather than a
    /// recompose, which is why it can run at a game-ish rate at all.
    /// </summary>
    private void OnToggleAnimate(object? sender, RoutedEventArgs e) => SetAnimating(animate is null);

    /// <summary>Draw the level's layer 3 on the level canvas. A recompose, not an overlay: it
    /// belongs BEHIND layer 2 unless the header gives it priority, and nothing painted over a
    /// finished canvas can go behind anything.</summary>
    private void OnToggleLayer3Preview(object? sender, RoutedEventArgs e)
    {
        if (!session.SetPreviewLayer3(!session.PreviewLayer3)) return;
        layer3PreviewItem.Icon = session.PreviewLayer3 ? new TextBlock { Text = "✓" } : null;
        AdoptSession();
    }

    /// <summary>Run or stop the phase cycle, and keep the menu's checkbox saying which it is.
    /// Stopping parks on phase 0, the state the level composes to.</summary>
    private void SetAnimating(bool on)
    {
        if (on == (animate is not null)) return;
        if (!on) { animate!.Stop(); animate = null; SetPhase(0); }
        else
        {
            animate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            // A palette stroke only keeps the phase ON SCREEN in step with the colour being
            // dragged (the other three are recomposed when the stroke ends), so stepping mid
            // drag would flick between the new colour and the old one.
            animate.Tick += (_, _) => { if (!session.InPaletteStroke) SetPhase((canvas.Phase + 1) & 3); };
            animate.Start();
        }
        animateItem.Icon = on ? new TextBlock { Text = "✓" } : null;
    }

    private DispatcherTimer? animate;

    /// <summary>LevelBitmap uploads a phase the first time it is asked for, so switching is just
    /// a repaint — there is nothing to push here.</summary>
    private void SetPhase(int phase)
    {
        canvas.Phase = phase;
        session.LivePhase = phase;      // the phase a live recolour has to keep current
        canvas.InvalidateVisual();

        // Every surface that draws composed tiles steps together — the drawer's Map16 sheet, the
        // Map16 editor's own sheet, and the 8x8 picker it builds tiles from. A tile that animates
        // in the level and sits still in the picker is the same tile drawn two ways.
        palette.Phase = map16Canvas.Phase = chr.Phase = phase;
        palette.InvalidateVisual();
        map16Canvas.InvalidateVisual();
        chr.InvalidateVisual();
        // The background draws composed tiles too, so it steps with them — but only while it is
        // the mode on screen; behind another mode it has nothing to repaint. Layer 3 is exempt:
        // its 2bpp GFX and its colours both sit outside anything that animates.
        if (modeBg?.IsChecked == true && bgLayer3.IsChecked != true) RefreshBg();
    }

    /// <summary>
    /// Undo follows what you are LOOKING AT. Each editor keeps its own history — a single
    /// stack across all of them is a bigger piece of work, and undoing a level edit while
    /// looking at pixels would be worse than this.
    ///
    /// The Palette tab is checked first because it is a drawer tab rather than a canvas
    /// mode: with it open the canvas is still in Level mode, so testing the mode first
    /// would send Ctrl+Z to the level while the user is editing colours.
    ///
    /// ONE dispatch for the key and the Edit menu. The menu items used to call the level-object
    /// editor's undo directly, whatever was on screen — so Edit ▸ Undo after a layer-3 stroke
    /// rewound nothing you could see, which from the outside was "layer 3 has no undo".
    /// </summary>
    private void UndoRedo(bool redo)
    {
        if (paletteTabs.SelectedIndex == PaletteTabIndex)
        {
            // Close any open stroke FIRST, so what the picker has already done becomes the
            // entry that undo then takes back. (This used to re-apply the last picked colour
            // through a stale pending value, which turned the second Ctrl+Z into a redo.)
            session.EndPaletteStroke();
            if (redo ? session.PaletteRedo() : session.PaletteUndo())
            {
                AdoptSession();
            }
        }
        else if (modeGfx.IsChecked == true)
        {
            // An un-dropped paste never reached the bytes, so undoing it is just taking the
            // float down — the history stays for the next Ctrl+Z.
            if (!redo && gfxCanvas.Float is not null)
                DiscardGfxFloat();
            else if (redo ? session.GfxPixels?.Redo() == true : session.GfxPixels?.Undo() == true)
            {
                // A cut/paste/move walks the marquee back (or forward) with its pixels.
                if (session.GfxPixels!.SelectionHint is (true, var rect))
                    gfxCanvas.Selection = rect;
                RefreshGfx();
            }
        }
        else if (modeMap16.IsChecked == true)
        {
            if (redo ? map16?.Redo() == true : map16?.Undo() == true) RefreshMap16Sheet();
        }
        // The background layers keep a history each, so undo follows the layer on screen
        // for the same reason it follows the canvas mode: rewinding the level's objects
        // while looking at a tilemap would be the wrong thing every time.
        else if (modeBg.IsChecked == true && BgLayerEdit is { } bgMap)
        {
            if (redo ? bgMap.Redo() : bgMap.Undo()) { RefreshBg(); UpdateTitle(); }
        }
        // Sprite mode has its own history — without this branch Ctrl+Z in sprite mode fell
        // through and silently rewound the OBJECT stack instead.
        else if (canvas.Mode == LevelView.EditMode.Sprites && session.Sprites is { } sp)
        {
            if (redo ? sp.Redo() : sp.Undo())
            {
                session.RefreshSprites();
                PushSpritePixels();
            }
        }
        else if (redo ? edit?.Redo() == true : edit?.Undo() == true)
        {
            PushDirty();
        }
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => UndoRedo(redo: false);

    private void OnRedo(object? sender, RoutedEventArgs e) => UndoRedo(redo: true);

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
            cols[0].Width = new GridLength(WantedDrawerWidth(drawerPane));
            cols[1].Width = GridLength.Auto;   // 1px: see the GridSplitter style
        }
        else
        {
            if (cols[0].Width.IsAbsolute && cols[0].Width.Value > 0)
                drawerWidths[drawerPane] = cols[0].Width.Value;
            cols[0].Width = new GridLength(0);
            cols[1].Width = new GridLength(0);
        }
        split.InvalidateMeasure();
    }

    /// <summary>
    /// Chrome around the palette content inside the drawer: the drawer's right border plus
    /// the scroll viewer's vertical scrollbar, which is always present because the sheet is
    /// 512 rows tall. Without allowing for it the scrollbar sits ON the last tile column.
    /// </summary>
    private const double DrawerChrome = 1 + 18;

    /// <summary>A GFX bin card is its sheet at 2x (a GFX file is 128 pixels across) plus the card's
    /// padding and border and the list's margin.</summary>
    private const double GfxBinCardWidth = 128 * 2 + 8 * 2 + 1 * 2 + 10 * 2;

    /// <summary>The Map16 drawer's CHR grid sizes its tiles to whatever width it is given, so the
    /// control row above it is the only thing with a width of its own — it sets the floor.</summary>
    private const double Map16BarWidth = 300;

    /// <summary>Which thing the drawer is holding. Not the same as the canvas mode by accident —
    /// each mode's drawer shows different content, and they are nowhere near the same width.</summary>
    private enum Pane { Level, Map16, Graphics, Background, Animations }

    private Pane drawerPane = Pane.Level;

    /// <summary>Where each pane was last left. Absent = never seen, so it opens at its content
    /// width; a splitter drag is remembered per pane rather than dragging all three.</summary>
    private readonly Dictionary<Pane, double> drawerWidths = [];

    /// <summary>What a pane's content actually needs: a whole Map16 tile row, the CHR grid's
    /// control row, or an uncut GFX bin card. The two CANVAS-mode panes open at the same width —
    /// the bin card needs less than the Map16 row, and two modes opening at different widths make
    /// the splitter jump as you switch between them.</summary>
    private double NaturalDrawerWidth(Pane pane) => DrawerChrome + pane switch
    {
        Pane.Map16 => Map16BarWidth,
        Pane.Graphics => Math.Max(GfxBinCardWidth, Map16BarWidth),
        Pane.Animations => Math.Max(GfxBinCardWidth, Map16BarWidth),
        Pane.Background => Map16BarWidth,      // a whole BG Map16 row, like the Map16 drawer
        _ => Map16PaletteView.ContentWidth(palette.Zoom),
    };

    private double WantedDrawerWidth(Pane pane)
        => Math.Max(drawerWidths.GetValueOrDefault(pane), NaturalDrawerWidth(pane));

    /// <summary>Point the drawer at a pane: bank the width the outgoing one was left at, then take
    /// the incoming one's — its own remembered width, or what its content needs the first time.
    /// Re-running it for the CURRENT pane is how a content resize (the Map16 tile zoom) re-floors
    /// the drawer without discarding a splitter drag.</summary>
    private void ApplyDrawerPane(Pane pane)
    {
        var col = split.ColumnDefinitions[0];
        if (col.Width.IsAbsolute && col.Width.Value > 0) drawerWidths[drawerPane] = col.Width.Value;
        drawerPane = pane;
        col.MinWidth = NaturalDrawerWidth(pane);
        if (drawer.IsVisible) col.Width = new GridLength(WantedDrawerWidth(pane));
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
        // An overlay mode outranks the tabs: it took the canvas from whichever layer was being
        // edited, and a drawer tab is not how you leave it — the toggle is.
        if (canvas.Mode is LevelView.EditMode.Exits or LevelView.EditMode.Entrances)
        { RefreshDrawer(); return; }

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
        bool animMode = modeAnim.IsChecked == true;
        bool bgMode = modeBg.IsChecked == true;
        bool modal = map16Mode || gfxMode || animMode || bgMode;   // a canvas mode owning the drawer
        int tab = modal ? -1 : Math.Max(0, paletteTabs.SelectedIndex);

        // The tabs choose what the drawer shows FOR THE LEVEL. Map16 and GFX modes own the
        // drawer outright, so a tab strip whose every option is inert only invites a click that
        // does nothing.
        paletteTabs.IsVisible = !modal;

        this.GetControl<DockPanel>("TilesPanel").IsVisible = tab == 0;
        this.GetControl<DockPanel>("ChrPanel").IsVisible = map16Mode;
        gfxToolPanel.IsVisible = gfxMode;
        animToolPanel.IsVisible = animMode;
        bgToolPanel.IsVisible = bgMode;
        animPaletteBar.IsVisible = animMode;    // its gutter palette, like the Map16 and GFX ones
        gfxPaletteBar.IsVisible = gfxMode;      // canvas-side, but the same mode decides it
        m16PaletteBar.IsVisible = map16Mode;    // its opposite number, same gutter
        bgPaletteBar.IsVisible = bgMode;        // four swatches wide there, not sixteen
        spritePanel.IsVisible = tab == 1;
        objectPanel.IsVisible = tab == 2;
        palettePanel.IsVisible = tab == 3;
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

    /// <summary>Colour indices as RGBA in the current palette row, transparent where the sheet
    /// should show through — what a float (a paste, or a lifted selection) is drawn with.</summary>
    private uint[] GfxFloatPixels(GfxEdit g, int w, int h, byte[] src)
    {
        var pal = session.PaletteRgba;
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = src[i] == 0 ? 0u : pal[g.BaseColor + Math.Min(src[i], (byte)g.MaxColor)];
        return px;
    }

    /// <summary>What is riding on the floating layer: its colour indices, and — for a LIFTED
    /// selection — where it was taken from, so its drop lands the right block and undoing that
    /// drop (or Esc) puts the marquee back home. Home is null for a paste, which came from no
    /// particular place and left no hole to fill back in.</summary>
    private ((int X, int Y, int W, int H)? Home, byte[] Px)? gfxFloat;

    /// <summary>Take the marquee's pixels off the sheet onto the floating layer. Every gesture
    /// that reshapes a selection — moving it, turning it — starts here, so none of them writes
    /// anything until the block is dropped.</summary>
    private void LiftGfxSelection((int X, int Y, int W, int H) r)
    {
        if (session.GfxPixels is not { } g) return;
        var px = g.Lift(r.X, r.Y, r.W, r.H);
        gfxFloat = (r, px);
        gfxCanvas.ShowFloat(GfxFloatPixels(g, r.W, r.H, px), r.W, r.H, r.X, r.Y);
        RefreshGfxSheet();               // the hole it left
    }

    /// <summary>Drop the float into the file where it rests — one undo entry, and the dropped
    /// block stays selected. A paste never touched the bytes and a lifted move wrote only the
    /// hole it left, so with none up there is nothing to do; that is what makes this safe to
    /// call at every way out of positioning.</summary>
    private void CommitGfxFloat()
    {
        if (gfxCanvas.Float is not { } f || session.GfxPixels is not { } g) return;
        g.Paste(f.X, f.Y, gfxFloat?.Home ?? (f.X, f.Y, f.W, f.H),
                gfxFloat is { } l ? (f.W, f.H, l.Px) : null);
        gfxFloat = null;
        gfxCanvas.ClearFloat();
        gfxCanvas.Selection = (f.X, f.Y, f.W, f.H);
        RefreshGfxSheet();
        gfxSave.IsEnabled = session.GfxDirty;
    }

    /// <summary>Take the float down WITHOUT landing it — Esc, or Ctrl+Z on one still adrift. A
    /// paste has nothing to undo; a lifted move has its hole open in the stroke, and aborting
    /// that is what puts the block back where it was grabbed from.</summary>
    private void DiscardGfxFloat()
    {
        var home = gfxFloat?.Home;
        gfxFloat = null;
        gfxCanvas.ClearFloat();
        if (home is null) { RefreshGfxXform(); return; }
        session.GfxPixels?.AbortStroke();
        gfxCanvas.Selection = home;
        RefreshGfxSheet();
    }

    private void SetGfxTool(GfxEdit.Tool tool)
    {
        if (tool != GfxEdit.Tool.Select) CommitGfxFloat();   // leaving the tool drops the paste
        if (session.GfxPixels is { } g) g.Current = tool;
        gfxPencil.IsChecked = tool == GfxEdit.Tool.Pencil;
        gfxFill.IsChecked = tool == GfxEdit.Tool.Fill;
        gfxErase.IsChecked = tool == GfxEdit.Tool.Eraser;
        gfxDropper.IsChecked = tool == GfxEdit.Tool.Dropper;
        gfxSelect.IsChecked = tool == GfxEdit.Tool.Select;
        gfxRect.IsChecked = tool == GfxEdit.Tool.Rect;
        gfxEllipse.IsChecked = tool == GfxEdit.Tool.Ellipse;
        gfxLine.IsChecked = tool == GfxEdit.Tool.Line;
        // The selection itself survives a tool change — copy still needs it — but only the
        // select tool drags it. The shape tools own the drag instead, so never both.
        gfxCanvas.Selecting = tool == GfxEdit.Tool.Select;
        gfxCanvas.Ranging = GfxEdit.IsShape(tool);
        // Both the bar icon and the picker show which variant is armed, so opening the dropdown
        // tells you where you are rather than only offering a choice.
        bool rf = session.GfxPixels?.RectFilled == true, ef = session.GfxPixels?.EllipseFilled == true;
        gfxRectIcon.Classes.Set("filled", rf);
        gfxEllipseIcon.Classes.Set("filled", ef);
        gfxRectOutlineBtn.IsChecked = !rf;
        gfxRectFilledBtn.IsChecked = rf;
        gfxEllipseOutlineBtn.IsChecked = !ef;
        gfxEllipseFilledBtn.IsChecked = ef;
        // The ring follows the tool: the eraser paints index 0, so that is the swatch in use.
        if (session.GfxPixels is { } sel)
            gfxColors.Select(tool == GfxEdit.Tool.Eraser ? 0 : sel.Color);
        RefreshGfxXform();      // the selection only counts while the select tool holds it
    }

    /// <summary>Rotate and flip need something to act on: the select tool armed with a marquee,
    /// or a block already up on the floating layer. Greyed rather than hidden, so they read as
    /// "not yet" rather than "not here".</summary>
    private void RefreshGfxXform()
        => gfxRotL.IsEnabled = gfxRotR.IsEnabled = gfxFlipH.IsEnabled = gfxFlipV.IsEnabled
            = gfxCanvas.Selecting && (gfxCanvas.Selection is not null || gfxCanvas.Float is not null);

    /// <summary>
    /// Turn the selection. It happens ON THE FLOATING LAYER: the block is lifted first (as
    /// grabbing it to move would), turns in the air, and only the drop writes — so turning it
    /// twice, or turning it and then changing your mind, costs the sheet underneath nothing.
    /// A quarter turn swaps the block's sides and pivots about its own centre, clamped to the
    /// sheet edge, so it stays where it was instead of swinging off its top-left corner.
    /// </summary>
    private void OnGfxXform(object? sender, RoutedEventArgs e)
    {
        if (session.GfxPixels is not { } g) return;
        if (gfxCanvas.Float is null && gfxCanvas.Selection is { } s) LiftGfxSelection(s);
        if (gfxCanvas.Float is not { } f || gfxFloat is not { } fl) return;

        var (nw, nh, px) = GfxEdit.Turn(f.W, f.H, fl.Px,
                        ReferenceEquals(sender, gfxRotL) ? GfxEdit.Xform.RotateLeft
                      : ReferenceEquals(sender, gfxRotR) ? GfxEdit.Xform.RotateRight
                      : ReferenceEquals(sender, gfxFlipH) ? GfxEdit.Xform.FlipH
                      : GfxEdit.Xform.FlipV);
        var (_, sw, sh) = g.Layout;
        gfxFloat = (fl.Home, px);
        gfxCanvas.ShowFloat(GfxFloatPixels(g, nw, nh, px), nw, nh,
                            Math.Clamp(f.X + (f.W - nw) / 2, 0, Math.Max(0, sw - nw)),
                            Math.Clamp(f.Y + (f.H - nh) / 2, 0, Math.Max(0, sh - nh)));
    }

    /// <summary>The Rect button both arms the tool and offers its two shapes — one click gets
    /// you drawing, and the same click shows the alternative rather than hiding it behind a
    /// caret nobody finds.</summary>
    private void OnGfxRect(object? sender, RoutedEventArgs e)
    {
        SetGfxTool(GfxEdit.Tool.Rect);
        if (sender is Control c) FlyoutBase.ShowAttachedFlyout(c);
    }

    private void OnGfxRectOutline(object? sender, RoutedEventArgs e) => SetShapeFilled(GfxEdit.Tool.Rect, false);
    private void OnGfxRectFilled(object? sender, RoutedEventArgs e) => SetShapeFilled(GfxEdit.Tool.Rect, true);

    /// <summary>The Ellipse button, same combo as Rect: arm the tool and show both shapes.</summary>
    private void OnGfxEllipse(object? sender, RoutedEventArgs e)
    {
        SetGfxTool(GfxEdit.Tool.Ellipse);
        if (sender is Control c) FlyoutBase.ShowAttachedFlyout(c);
    }

    private void OnGfxEllipseOutline(object? sender, RoutedEventArgs e) => SetShapeFilled(GfxEdit.Tool.Ellipse, false);
    private void OnGfxEllipseFilled(object? sender, RoutedEventArgs e) => SetShapeFilled(GfxEdit.Tool.Ellipse, true);

    private void SetShapeFilled(GfxEdit.Tool tool, bool filled)
    {
        if (session.GfxPixels is { } g)
        {
            if (tool == GfxEdit.Tool.Rect) g.RectFilled = filled;
            else g.EllipseFilled = filled;
        }
        SetGfxTool(tool);        // re-reads both flags onto the icons and the picker
        // A plain Flyout has no notion of "an item was chosen", unlike a MenuFlyout, so the
        // pick has to close it or it sits there over the canvas.
        var owner = tool == GfxEdit.Tool.Rect ? gfxRect : gfxEllipse;
        FlyoutBase.GetAttachedFlyout(owner)?.Hide();
    }

    private void OnGfxTool(object? sender, RoutedEventArgs e)
        => SetGfxTool(ReferenceEquals(sender, gfxFill) ? GfxEdit.Tool.Fill
                    : ReferenceEquals(sender, gfxErase) ? GfxEdit.Tool.Eraser
                    : ReferenceEquals(sender, gfxDropper) ? GfxEdit.Tool.Dropper
                    : ReferenceEquals(sender, gfxSelect) ? GfxEdit.Tool.Select
                    : ReferenceEquals(sender, gfxLine) ? GfxEdit.Tool.Line
                    : GfxEdit.Tool.Pencil);

    /// <summary>Step the paint palette row within what the selected bin is allowed. The combo box
    /// IS the state, so its own handler carries the change to the editor, the sheet and the drawer's
    /// preview of the selected bin.</summary>
    private void StepGfxPalRow(int delta)
    {
        int i = Math.Clamp(gfxPalRow.SelectedIndex + delta, 0, gfxPalRow.ItemCount - 1);
        if (i == gfxPalRow.SelectedIndex) return;
        gfxPalRow.SelectedIndex = i;
    }

    private bool refillingGfxRows;

    /// <summary>
    /// The palette rows the selected bin can legitimately use: SMW loads layer graphics under CGRAM
    /// rows 0-7 and sprite graphics under 8-15, so an FG/BG bin offering row 9 (or an SP bin
    /// offering row 2) is offering a preview the game will never draw. With no bin selected nothing
    /// constrains the choice, so all sixteen are there.
    /// </summary>
    private (int First, int Count) GfxRowRange()
        // A 2bpp file does not pick a ROW. It reads four colours, and four colours tile CGRAM
        // 00-1F eight ways — the same palette GROUPS the layer-3 tilemap names and the Background
        // and Palette pages now show. Offering rows 0-1 here was the old model, and it made the
        // editor colour an LG file from CGRAM 00-03 while the drawer card beside it used 08-0B.
        => GfxIsLayer3 ? (0, Layer3.PaletteGroups)
         : session.GfxBins.FirstOrDefault(b => b.BypWord == gfxSlot).Name switch
        {
            null => (0, 16),
            var n when n.StartsWith("SP") => (8, 8),
            _ => (0, 8),
        };

    /// <summary>Whether the open file is being READ as layer-3 graphics — the depth decides, not
    /// the bin, so a custom ExGFX switched to 2bpp gets the group picker too.</summary>
    private bool GfxIsLayer3 => session.GfxPixels?.Bpp == 2;

    /// <summary>The picker's value for the open file: a palette group when it is 2bpp, the
    /// 16-colour row otherwise. Group g is row g/4 with the offset (g%4)*4 — the two together
    /// are what <see cref="GfxEdit.BaseColor"/> adds up.</summary>
    private int GfxPalValue => session.GfxPixels is not { } g ? 0
        : GfxIsLayer3 ? g.PalRow * 4 + g.ColorOffset / Layer3.PaletteColors : g.PalRow;

    private void SetGfxPalValue(GfxEdit g, int value)
    {
        if (!GfxIsLayer3) { g.PalRow = value; g.ColorOffset = 0; return; }
        gfxLayer3Group = value;
        (g.PalRow, g.ColorOffset) = Layer3Pal(value);
    }

    /// <summary>
    /// The palette group every layer-3 file is SHOWN in — one setting for all four LG bins, not
    /// one each.
    ///
    /// The four bins are one picture: they fill a single 512-tile window that a tilemap addresses
    /// as one space, and the group is a property of the tilemap word rather than of the file. So
    /// picking a group means "show layer 3 in this", and cycling LG1-LG4 to compare them keeps
    /// it. Resetting to each bin's own default made every comparison start by re-picking, and
    /// since all four bins declare the same default, that default could never have been the
    /// thing worth remembering.
    ///
    /// Group 2 to start with: the first of the four CGRAM holds layer 3's own colours, and the
    /// value each LG bin used to carry.
    /// </summary>
    private int gfxLayer3Group = 2;

    private static (int Row, int Off) Layer3Pal(int group)
        => (group / 4, group % 4 * Layer3.PaletteColors);

    /// <summary>A bin's (row, offset) to draw in: the remembered group for a layer-3 bin, the
    /// bin's own for everything else.</summary>
    private (int Row, int Off) GfxPalFor(int bpp, int binRow, int binOff)
        => bpp == Layer3.Bpp ? Layer3Pal(gfxLayer3Group) : (binRow, binOff);

    /// <summary>Fill the row picker with what this bin allows and land on the nearest legal row to
    /// the one being painted with. The items ARE the row numbers, so a list starting at 8 does not
    /// make index 0 mean row 0.</summary>
    private (int First, int Count) gfxRows = (-1, 0);

    private void RefreshGfxPalRows(int row)
    {
        var want = GfxRowRange();
        row = Math.Clamp(row, want.First, want.First + want.Count - 1);
        refillingGfxRows = true;
        if (want != gfxRows)
        {
            gfxRows = want;
            gfxPalRow.ItemsSource = Enumerable.Range(want.First, want.Count).Cast<object>().ToList();
        }
        gfxPalRow.SelectedIndex = row - want.First;
        refillingGfxRows = false;
        if (session.GfxPixels is { } g) SetGfxPalValue(g, row);   // the clamp has to reach the editor
    }

    /// <summary>Take the colour under a sheet pixel as the paint colour — the eyedropper tool and
    /// the right-click shortcut are the same act. A TRANSPARENT pixel names no colour, so picking
    /// one switches to the eraser: that is the tool that puts transparency back.</summary>
    private void PickGfxColor(int px, int py)
    {
        if (session.GfxPixels?.ColorAt(px, py) is not { } c) return;
        if (c == 0) { SetGfxTool(GfxEdit.Tool.Eraser); return; }
        session.GfxPixels.Color = c;
        gfxColors.Select(c);
    }

    /// <summary>Re-decode the sheet only. This is the live-paint path, so it must NOT recompose
    /// the level — that happens once when the stroke ends. <paramref name="blank"/> draws nothing
    /// at all, which also makes the canvas untouchable: a zero-size sheet hit-tests to no pixel.</summary>
    private void RefreshGfxSheet(bool blank = false)
    {
        if (session.GfxPixels is not { } g) return;
        var (px, w, h) = blank ? ([], 0, 0) : session.GfxSheet();
        gfxCanvas.Tiles = blank ? 0 : g.Layout.Tiles;
        gfxCanvas.SetSheet(px, w, h);
    }

    /// <summary>Everything the GFX mode shows for the current file: the sheet, the badge, the
    /// paint colours and the bin jump list.</summary>
    /// <summary>The file the selection rectangle was made on: its coordinates mean nothing in
    /// another sheet, so switching files drops it. The CLIPBOARD survives — that is the point.</summary>
    private int gfxSelectionFile = -1;

    private void RefreshGfx()
    {
        if (session.GfxPixels is not { } g) return;
        if (g.File != gfxSelectionFile)
        {
            // Backstop only: every deliberate file switch commits the float first. A file that
            // changed some other way discards it — committing into the wrong sheet is worse.
            gfxCanvas.ClearFloat();
            gfxFloat = null;             // its home was in the file we just left
            gfxCanvas.Selection = null;
            gfxSelectionFile = g.File;
        }
        // No bin selected means nothing is being edited, so the view is EMPTY — showing whichever
        // file the editor happens to have open would read as some bin's contents.
        bool none = gfxSlot < 0;
        // The file, by name where it has one. The badge says which kind it is, so the note is
        // only the id — and only when the name is not already showing it.
        bool stock = session.GfxIsStock;
        bool empty = !none && g.File == 0x7F;      // 0x7F = "unused": neither stock nor custom
        gfxKind.IsVisible = !none;
        // ExGFX ids are primary keys, not labels: a named custom file shows only its name, and
        // the id is what unnamed files (stock or fresh imports) fall back to.
        gfxFileName.Text = none ? "no bin selected — pick one in the drawer"
            : empty ? "Empty" : g.Name ?? $"GFX{g.File:X3}";
        gfxKind.Data = (StreamGeometry)this.FindResource(
            empty ? "IconCircle" : stock ? "IconCircleCheck" : "IconStar")!;
        gfxKind.Classes.Set("custom", !stock && !empty);
        ToolTip.SetTip(gfxKind, empty ? "an empty slot"
            : stock ? "a base ROM graphics file" : "a custom ExGFX file");
        gfxSave.IsEnabled = !none && session.GfxDirty;
        // Not gated on dirty: forking a clean file under a new name is a legit use.
        gfxSaveAs.IsEnabled = !none && g.Layout.Tiles > 0;
        // Nothing to paint on: an empty BIN offers Load, no bin at all offers nothing.
        gfxEmptyLoad.IsVisible = !none && g.Layout.Tiles == 0;
        SetGfxTool(g.Current);
        RefreshGfxPalRows(GfxPalValue);   // the rows this bin allows, before anything reads one
        // The depth box shows what the sheet is being READ as, override or not — so switching
        // files moves it back to whatever that file is, which is what dropping the override did.
        // A 3bpp-stored file reads as tile data, and tile data displays 4bpp: same entry.
        refillingGfxRows = true;
        gfxBpp.SelectedIndex = g.Bpp == 2 ? 1 : 0;
        refillingGfxRows = false;
        RefreshGfxSheet(none);

        // For tile data, the WHOLE 16-colour row: a tile displays 4bpp on the SNES, so the back
        // half is part of the palette even where a 3bpp-stored file cannot reach it — IsDisabled
        // greys those rather than hiding them. For a 2bpp layer-3 file it is FOUR, the size of a
        // palette group, because there is no back half to grey: the other twelve belong to other
        // groups this file could equally be drawn in, and showing them as unreachable colours of
        // "this" palette was the wrong picture. Index 0 keeps the sheet's grey convention.
        int count = GfxIsLayer3 ? Layer3.PaletteColors : 16;
        var row = new uint[count];
        var pal = session.PaletteRgba;
        for (int i = 0; i < count; i++)
            row[i] = i == 0 ? 0xFF303030u : pal[g.BaseColor + i];
        gfxColors.Cols = count;
        gfxColors.Colors = row;
        gfxColors.InvalidateMeasure();
        gfxPalNote.Text = GfxIsLayer3
            ? $"CGRAM {g.BaseColor:X2}-{g.BaseColor + count - 1:X2}"
              + (Layer3.IsLayer3Palette(GfxPalValue) ? " — layer 3's own colours"
                                                     : " — the level's background palette")
            : "";
        gfxColors.Select(g.Current == GfxEdit.Tool.Eraser ? 0 : g.Color);

        RefreshGfxBins();          // the bins list IS the file picker now
    }

    /// <summary>
    /// The GFX drawer: one block per VRAM bin — what it holds and what kind of file that is.
    /// Repointing a bin happens through the editor bar's Load, not here, so the head is a label:
    /// [bin] [kind badge] [file name]. Built in code rather than bound, because it is ten
    /// near-identical composites and a template plus a view model for each would be more
    /// machinery than the thing it builds.
    /// </summary>
    private void RefreshGfxBins()
    {
        gfxBins.Children.Clear();
        // The ten VRAM bins, then two headed groups: the layer-3 window (LG1-LG4), and the
        // animation slots — AN1/AN2
        // (real bypass words) and the four ExAnimation source files 60-63, which are not bins at
        // all (nothing points a level at them; ExAnimation slots read them by offset) but ARE
        // graphics files the pixel editor can paint. Their "bypass word" is the file id itself
        // (0x60-0x63, clear of the real words 0-11): selecting one opens the file, and Load on it
        // imports a .bin into it. An absent one still opens — as a blank file to create.
        var bins = session.GfxBins.ToList();
        for (int i = 0; i < 4; i++)
            bins.Add(($"E{0x60 + i:X2}", 2, 0x60 + i, 0x7F, session.Rom is { } r && (r.ImportedGfx.ContainsKey(0x60 + i) || r.LmAltExGfx(i) > 0) ? 0x60 + i : 0x7F, 0, 0));
        foreach (var bin in bins)
        {
            int bypWord = bin.BypWord, palRow = bin.PalRow, file = bin.File, palOff = bin.ColorOffset;
            bool altFile = bypWord >= 0x60;
            int openFile = altFile ? Convert.ToInt32(bin.Name[1..], 16) : file;   // "E60" → 0x60
            // Two headed groups after the ten VRAM bins: the level's layer-3 window, then the
            // animation slots. LG1-LG4 are real bins with a real bypass — LM's Layer 3
            // GFX/Tilemap Bypass — they just live behind their own enable bit (CONTRACT §12b).
            if (bin.Name is "LG1" or "AN1")
            {
                var sep = new TextBlock { Text = bin.Name == "LG1" ? "Layer 3" : "Animation slots",
                                          Margin = new Thickness(0, 8, 0, 0) };
                sep.Classes.Add("subject");
                gfxBins.Children.Add(sep);
                gfxBins.Children.Add(new Border { Height = 1, Background = (IBrush)this.FindResource("BorderBrush")!, Margin = new Thickness(0, 0, 0, 2) });
            }
            bool empty = file == 0x7F;
            bool custom = !altFile && session.GfxBinNote(bypWord, file, bin.Def) == "custom";
            var kind = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "kind" },
                Data = (StreamGeometry)this.FindResource(
                    empty ? "IconCircle" : custom ? "IconStar" : "IconCircleCheck")!,
            };
            kind.Classes.Set("custom", custom);
            ToolTip.SetTip(kind, empty ? "an empty slot"
                : custom ? "a custom ExGFX file" : "a base ROM graphics file");

            var head = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = $"[{bin.Name}]", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                                    Width = 40, FontWeight = FontWeight.Bold,
                                    Foreground = (IBrush)this.FindResource("TextDimBrush")! },
                    kind,
                    new TextBlock { Text = empty ? (altFile ? "Empty — click to create" : "Empty") : session.GfxName(file) ?? $"GFX{file:X3}",
                                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
            };

            // No per-bin Import/Browse buttons: the header's Load covers both, and ten cards each
            // carrying two buttons buried the thing the card is actually for — its sheet.
            // The head sits in its own darker band spanning the card; the preview fills the rest.
            var block = new StackPanel();
            block.Children.Add(new Border
            {
                Child = head,
                Padding = new Thickness(8, 6),
                Background = (IBrush)this.FindResource("SurfaceBrush")!,
                CornerRadius = new CornerRadius(4, 4, 0, 0),   // inside the card's 5
            });

            // The SELECTED bin previews in the row being painted with, so the drawer and the editor
            // show the same colours; the others keep the row the level actually loads them under.
            // Every layer-3 bin previews in the group the editor is showing them in, so cycling
            // LG1-LG4 to compare them is a comparison rather than four different palettes.
            var (previewRow, previewOff) = bin.BypWord == gfxSlot && session.GfxPixels is { } sel
                ? (sel.PalRow, sel.ColorOffset)
                : GfxPalFor(bin.Bpp, bin.PalRow, bin.ColorOffset);
            var (px, w, h) = session.GfxFileSheet(bin.File, previewRow, previewOff, bin.Bpp);
            if (px.Length > 0)
                block.Children.Add(new PixelImage
                {
                    // Not an Image: it scales the bitmap itself, outside the one shared pixel
                    // rule, and any fractional zoom the stretch lands on is PixelBlit's job.
                    Source = LevelBitmap.FromPixels(px, w, h),
                    Stretch = true,
                    BottomCornerRadius = 4,
                });
            else
                block.Children.Add(new TextBlock { Text = "(empty)", Classes = { "mono" },
                                                   Margin = new Thickness(8, 6) });

            // The whole block IS the "select this bin" target — selecting a bin and editing its
            // file are the same gesture, so a separate Edit button would be a second way to do one
            // thing. The selected bin carries the accent border, as a selected swatch does, and it
            // is what the header's Load fills.
            bool open = bin.BypWord == gfxSlot;
            var card = new Border
            {
                Child = block,
                CornerRadius = new CornerRadius(5),
                // Same thickness selected or not: a thicker border relays the card and the whole
                // list jiggles as the selection moves. Colour and fill carry the state instead.
                BorderThickness = new Thickness(2),
                BorderBrush = open ? UiColors.Accent : this.FindResource("BorderBrush") as IBrush,
                // Transparent, never null: a null background is not hit-testable, so the card
                // would take no clicks except on the controls inside it.
                Background = open ? UiColors.SelectionFill : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            // An UNUSED bin (0x7F) is clickable too: selecting it is how it gets given something.
            card.PointerPressed += (_, _) =>
            {
                gfxSlot = bypWord;
                EditGfxFile(openFile, palRow, bin.Bpp, palOff);
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
    private void EditGfxFile(int file, int palRow, int bpp = 0, int palOff = 0)
    {
        if (session.GfxPixels is not { } g) return;
        CommitGfxFloat();                    // into the file it was floating over
        g.Open(file);
        (g.PalRow, g.ColorOffset) = GfxPalFor(bpp, palRow, palOff);
        // A bin can KNOW its depth where the file cannot: layer 3 is 2bpp because of where it is
        // loaded, so an ExGFX file a bypassed LG slot points at opens 2bpp too, not at the ROM's
        // depth. Open() cleared any previous override, so this is the one that sticks.
        if (bpp > 0) g.ViewAs(bpp);
        OnMode(modeGfx, new RoutedEventArgs());
    }

    // ---- palette tab ----

    /// <summary>Guard against the picker firing while it is being LOADED from a selection —
    /// otherwise picking a swatch immediately writes its own colour back as an "edit".</summary>
    private bool loadingSwatch;

    private void RefreshPaletteTab()
    {
        // Layer 3 is 2bpp and can name eight palette groups of four, so its whole reach is CGRAM
        // 00-1F. Four wide by eight tall over that range is group-major for free — grid row g IS
        // palette group g — so nothing has to remap indices: a swatch's position in this view and
        // its CGRAM number stay the same thing, and edits, tooltips and the picker are unchanged.
        bool l3 = paletteLayer3.IsChecked == true;
        var all = session.PaletteRgba;
        paletteGrid.Cols = l3 ? Layer3.PaletteColors : 16;
        paletteGrid.Rows = l3 ? Layer3.PaletteGroups : 16;
        paletteGrid.Colors = l3 ? [.. all.Take(Layer3.PaletteSpace)] : all;
        paletteGrid.InvalidateVisual();
        paletteBg.Colors = all is { Length: > 0 } pr ? [pr[0]] : [0xFF000000u];
        paletteBg.InvalidateVisual();
        // Just the provenance: which palette you are editing, and whether you have moved it. The
        // rest was a paragraph explaining a grid that explains itself.
        paletteNote.Text = (session.HasCustomPalette ? "LM custom palette" : "vanilla")
                         + (session.PaletteEditCount > 0 ? $"  —  {session.PaletteEditCount} edit(s)" : "");
        ShowPaletteColor(paletteGrid.Selected);
    }

    /// <summary>
    /// Pointing at "Layer 3 only" shows what it would do, on the grid: the eight palette groups
    /// it keeps get ringed, and the 224 colours it drops go under the disabled veil. Reading the
    /// effect off the thing it acts on beats pressing the toggle and comparing two pictures from
    /// memory — and the rings land on the groups, so the shape of the narrowed view is visible
    /// before you get there.
    ///
    /// Nothing to preview once it IS narrowed: at that point nothing is being filtered out.
    /// </summary>
    private void PreviewLayer3Palette(bool on)
        => paletteGrid.Preview = on && paletteLayer3.IsChecked != true
            ? [.. Enumerable.Range(0, Layer3.PaletteGroups)
                            .Select(g => (Layer3.PaletteBase(g), Layer3.PaletteColors, $"{g}"))]
            : null;

    /// <summary>Narrow the palette page to what layer 3 can reach, and back. A selection outside
    /// the narrowed range is DROPPED rather than clamped: clamping would silently move the picker
    /// to a colour the user never chose, and the next edit would land on it.</summary>
    private void OnPaletteLayer3Only(object? sender, RoutedEventArgs e)
    {
        if (paletteLayer3.IsChecked == true && paletteGrid.Selected >= Layer3.PaletteSpace)
            paletteGrid.Select(-1);
        // Pressing it while the pointer is still on it: the preview would otherwise stay up over
        // a grid that has already been narrowed.
        PreviewLayer3Palette(false);
        RefreshPaletteTab();
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

    /// <summary>Reset throws away every colour edit on the level at once and there is no undo on
    /// this tab, so it asks first — the one button here whose miss-click cannot be walked back.</summary>
    private async void OnResetPalette(object? sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmWindow("Reset palette",
            session.PaletteEditCount is var n and > 0
                ? $"Discard {n} colour edit(s) on this level and go back to its original palette?"
                : "Reset this level's palette to its original colours?", "Reset");
        await dlg.ShowDialog(this);
        if (!dlg.Confirmed || !session.ResetPalette()) return;
        AdoptSession();
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
    /// <summary>Give a canvas the keyboard once layout has caught up. Focusing a control in the
    /// same breath as making it visible silently does nothing — it is not in the tree yet — and
    /// then the mode's own keys (F, [ ], the palette arrows) go nowhere until it is clicked.</summary>
    private static void FocusWhenLaidOut(Control c) => Dispatcher.UIThread.Post(() => c.Focus());

    private void OnMode(object? sender, RoutedEventArgs e)
    {
        foreach (var b in new[] { modeLevel, modeMap16, modeGfx, modeBg, modeAnim })
            b.IsChecked = ReferenceEquals(b, sender);

        bool map16 = ReferenceEquals(sender, modeMap16);
        bool gfx = ReferenceEquals(sender, modeGfx);
        bool anim = ReferenceEquals(sender, modeAnim);
        bool bg = ReferenceEquals(sender, modeBg);
        // Leaving the pixel editor with a stroke still open must not leave bytes behind that no
        // undo entry covers, so it is reverted rather than committed. A floating paste is the
        // opposite case — deliberate content not yet in any bytes — so it is dropped first.
        if (!gfx) { CommitGfxFloat(); session.GfxPixels?.AbortStroke(); }

        this.GetControl<DockPanel>("LevelPane").IsVisible = !map16 && !gfx && !anim && !bg;
        this.GetControl<DockPanel>("Map16Pane").IsVisible = map16;
        gfxScroll.IsVisible = gfx;
        animPane.IsVisible = anim;
        bgPane.IsVisible = bg;
        edit?.Selection.Clear();
        map16Canvas.ClearSelection();
        ApplyZoomTarget();             // the gutter control follows the canvas it is driving
        ApplyDrawerPane(bg ? Pane.Background : anim ? Pane.Animations : gfx ? Pane.Graphics
                      : map16 ? Pane.Map16 : Pane.Level);

        RefreshDrawer();
        if (map16)
        {
            // Entering the mode adopts whatever the level's picker is armed with — but only on
            // the way IN. Re-adopting it on every sheet refresh moved the selection off the tile
            // you had just edited, so a property change deselected its own tile and the next one
            // went somewhere else entirely.
            map16Canvas.SelectedTile = palette.Selected;
            RefreshMap16Sheet();
            RefreshMap16Props();
            FocusWhenLaidOut(map16Canvas);
        }
        else if (gfx)
        {
            // Entered from the header rather than a bin click: adopt whichever bin holds the file
            // the editor is already on, so the drawer shows what Load would replace.
            if (gfxSlot < 0 && session.GfxPixels is { } gp)
                gfxSlot = session.GfxBins.Where(b => b.File == gp.File)
                                 .Select(b => (int?)b.BypWord).FirstOrDefault() ?? -1;
            RefreshGfx();
            FocusWhenLaidOut(gfxCanvas);
        }
        else if (anim) RefreshAnim();
        else if (bg) RefreshBg();
        if (!anim) { animPreview?.Stop(); animPreview = null; }   // no ticking behind another mode

        canvas.InvalidateVisual();
        // ...and again once layout has caught up: the repaint above can land while the canvas is
        // still marked invisible from the mode it is leaving, and that frame draws with whatever
        // the layout could tell it then.
        Dispatcher.UIThread.Post(canvas.InvalidateVisual);
    }

    // ---- Background canvas mode ----

    /// <summary>Radio behaviour for the two layers, the same hand-rolled pair the Animations
    /// bar uses for Global/Level.</summary>
    private void OnBgLayer(object? sender, RoutedEventArgs e)
    {
        bgLayer2.IsChecked = ReferenceEquals(sender, bgLayer2);
        bgLayer3.IsChecked = ReferenceEquals(sender, bgLayer3);
        RefreshBg();
    }

    /// <summary>Import a raw layer-3 tilemap for this level — LM's LT3 file, a flat 16-bit map.
    /// Editor-only until LM's tilemap-bypass slot is decoded, which the build says out loud;
    /// this is where you SEE it, which is most of what authoring one needs.</summary>
    /// <summary>Save the level's layer-3 tilemap to a file. Painting it is already saved with
    /// the project — this is for getting it OUT: into Lunar Magic, another level, or a backup.</summary>
    /// <summary>
    /// The Tilemaps menu, built fresh each time it opens so it always shows the library as it
    /// stands: the saved maps for the layer on screen (click one to apply it), then Save as…,
    /// then Delete. One menu for both layers — the layer toggle decides which half you see.
    /// </summary>
    private void ShowTilemapMenu()
    {
        int layer = bgLayer3.IsChecked == true ? 3 : 2;
        var names = session.TilemapPresets(layer).ToList();
        var menu = new MenuFlyout();
        foreach (string n in names)
        {
            var item = new MenuItem { Header = n };
            item.Click += (_, _) => { if (session.ApplyTilemapPreset(n)) { RefreshBg(); UpdateTitle(); } };
            menu.Items.Add(item);
        }
        if (names.Count == 0)
            menu.Items.Add(new MenuItem { Header = $"No saved layer {layer} tilemaps", IsEnabled = false });
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save this level's as…", IsEnabled = BgLayerEdit is not null };
        save.Click += async (_, _) => await SaveTilemapPreset(layer);
        menu.Items.Add(save);
        if (names.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete" };
            foreach (string n in names)
            {
                var item = new MenuItem { Header = n };
                item.Click += (_, _) => { if (session.DeleteTilemapPreset(n)) UpdateTitle(); };
                delete.Items.Add(item);
            }
            menu.Items.Add(delete);
        }
        menu.ShowAt(bgTilemaps);
    }

    private async Task SaveTilemapPreset(int layer)
    {
        var dlg = new TextPromptWindow($"Name for this layer {layer} tilemap",
                                       $"level {session.LevelNum:X3} layer {layer}");
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } name) return;              // cancelled: nothing saved
        if (session.SaveTilemapPreset(name, layer)) UpdateTitle();
    }

    private async void OnExportLayer3Tilemap(object? sender, RoutedEventArgs e)
    {
        if (await PickSaveFile("Export this level's layer-3 tilemap",
                               $"level{session.LevelNum:X3}.bin",
                               new FilePickerFileType("Tilemap") { Patterns = ["*.bin", "*.map"] }) is not { } path)
            return;
        session.ExportLayer3Tilemap(path);
        UpdateTitle();
    }

    private async void OnImportLayer3Tilemap(object? sender, RoutedEventArgs e)
    {
        if (await PickFile("Import a layer-3 tilemap",
                           new FilePickerFileType("Tilemap") { Patterns = ["*.bin", "*.map"] }) is not { } path)
            return;
        if (session.ImportLayer3Tilemap(path)) { AdoptSession(); UpdateTitle(); }
    }

    /// <summary>
    /// The level's layer-3 settings, off the Layer 3 bar rather than the level properties
    /// dialog: they are two bits in two different records, but they are one decision, and the
    /// place you make it should be the place you can see the result.
    ///
    /// The option is an ENTRANCE field and the priority a HEADER one, so they apply through
    /// different session calls — and a header change reparses the level, which is why it is
    /// applied second and the repaint happens once at the end.
    /// </summary>
    private async void OnLayer3Options(object? sender, RoutedEventArgs e)
    {
        if (session.Header is not { } header || session.MainEntrance is not { } entry) return;
        var adv = session.Layer3Advanced;
        var dlg = new Layer3OptionsWindow(Layer3.OptionNames, entry.Layer3Option,
                                          header.Layer3Priority != 0, session.Layer3HasTilemap,
                                          adv, session.Layer3AdvancedSupported);
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } r) return;

        if (r.Option != entry.Layer3Option) session.ApplyEntry(entry with { Layer3Option = r.Option });
        int priority = r.Priority ? 1 : 0;
        if (priority != header.Layer3Priority) session.ApplyHeader(header with { Layer3Priority = priority });
        if (r.Advanced != adv) session.ApplyLayer3Advanced(r.Advanced);
        AdoptSession();
        UpdateTitle();
    }

    /// <summary>
    /// Repaint the Background pane: the level's layer-2 background as a tile map, with the BG
    /// Map16 tiles it can address in the drawer.
    ///
    /// Layer 2 shows something only when this level's layer 2 IS a background image. When it is
    /// an object stream the pane says so and points at the Level canvas — the same division LM
    /// draws, and the reason its own Background editor is empty on those levels.
    /// </summary>
    private void RefreshBg()
    {
        bool layer3 = bgLayer3.IsChecked == true;
        bgDrawerTitle.Text = layer3 ? "Layer 3 tiles" : "BG Map16 — pages 80-81";
        bgOptions.IsVisible = bgImportMap.IsVisible = bgExportMap.IsVisible = layer3;
        bgOptions.IsEnabled = bgImportMap.IsEnabled = bgTilemaps.IsEnabled = session.HasLevel;
        // Export needs something to write; the other two are how you get there.
        bgExportMap.IsEnabled = session.Layer3Map is not null;
        bgView.Backdrop = session.PaletteRgba is { Length: > 0 } pal ? pal[0] : 0xFF000000u;
        bgSheet.Backdrop = bgView.Backdrop;

        void Empty(string note)
        {
            bgView.CellAt = null; bgView.Reshape(0, 0, 16);
            bgSheet.CellAt = null; bgSheet.Reshape(0, 0, 16);
            bgNoteBase = note; RefreshBgNote();
        }

        if (!session.HasLevel) { Empty(""); return; }
        int ph = canvas.Phase;

        if (layer3)
        {
            int opt = session.Layer3Option;
            if (session.Layer3Map is not { } map)
            {
                // Two different empty states, and saying which one it is IS the fix: "no layer 3"
                // alone left no way to tell a level that never asked for one from a level whose
                // mode has no tilemap to give it (vanilla's table covers modes 0-14, §12b).
                Empty(opt != 0
                    ? $"{Layer3.OptionNames[opt]}, but level mode {session.Header?.LevelMode} has no tilemap for it"
                    : session.Layer3TilemapImported
                        ? "a tilemap is imported, but this level's option is Blank Layer 3"
                        : "no layer 3 — give this level one with Layer 3 Options");
                return;
            }
            var palCounts = Layer3PaletteCounts(map);
            SeedBgBrushPalette(map, palCounts);
            bgView.CellAt = map.At;
            bgView.CellPixels = session.Layer3CellPixels;
            bgView.Zoom = 2;
            bgView.Reshape(map.Cols, map.Rows, map.CellPx);

            // The drawer is the SAME control in picker mode: its cells are tile numbers laid out
            // sixteen to a row, drawn in whatever palette group the brush carries, so the sheet
            // and the thing about to be painted match.
            bgSheet.CellAt = (c, r) => (bgBrushL3 & ~0x3FF) | (r * SheetCols + c);
            bgSheet.CellPixels = session.Layer3CellPixels;
            bgSheet.Selected = bgBrushL3 & 0x3FF;
            bgSheet.Reshape(SheetCols, Layer3.TileCount / SheetCols, 8);

            bgNoteBase = $"{Layer3.Cols}x{Layer3.Rows} tiles — {Layer3.OptionNames[opt]}"
                       + $", palettes {Layer3PalettesInUse(palCounts)}"
                       // "Did my painting stick?" is answered here, because the fork is invisible
                       // otherwise: until the first stroke this level is LOOKING AT a tilemap
                       // every level of its mode shares, and after it the level owns one. The bar
                       // is narrow and ellipsises, so the sentence goes in the tip below.
                       + (session.Layer3TilemapImported ? ", own tilemap" : ", shared")
                       + (session.Layer3Advanced is { } a3
                          ? $", scroll {Layer3.VScrollNames[a3.VScroll]}/{Layer3.HScrollNames[a3.HScroll]}"
                          : "");
            RefreshBgNote();
            return;
        }

        if (session.BgMap is not { } bg)
        {
            Empty("this level's layer 2 is an object stream — edit it on the Level canvas");
            return;
        }
        bgView.CellAt = bg.At;
        bgView.CellPixels = t => session.BgCellPixels(t, ph);
        bgView.Zoom = 2;
        bgView.Reshape(bg.Cols, bg.Rows, bg.CellPx);

        bgSheet.CellAt = (c, r) => r * SheetCols + c;
        bgSheet.CellPixels = t => session.BgCellPixels(t, ph);
        bgSheet.Selected = bgBrush & 0x1FF;
        bgSheet.Reshape(SheetCols, EditorSession.BgSheetTiles / SheetCols, 16);

        bgNoteBase = $"{EditorSession.BgCols}x{EditorSession.BgRows} tiles — two screens, repeats"
                   + (session.BgTilemapEdited ? ", edited" : "");
        RefreshBgNote();
    }

    /// <summary>
    /// Which palette groups the level's tilemap ACTUALLY names, for the note. An imported map
    /// is somebody else's file and the answer is not guessable from the level: this one turned
    /// out to be group 3 alone, and nothing on screen said so. Cells the map never wrote (-1)
    /// are not counted — they are the blank the build pads with, not a choice.
    /// </summary>
    private static int[] Layer3PaletteCounts(TilemapEdit map)
    {
        var counts = new int[Layer3.PaletteGroups];
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Cols; c++)
                if (map.At(c, r) is >= 0 and var w) counts[Layer3.PaletteOf(w)]++;
        return counts;
    }

    private static string Layer3PalettesInUse(int[] counts)
    {
        var used = Enumerable.Range(0, counts.Length).Where(g => counts[g] > 0).ToArray();
        return used.Length == 0 ? "none" : string.Join("/", used);
    }

    /// <summary>
    /// Start the brush on the palette the level's map actually uses, once per level. The drawer
    /// sheet draws every tile in the BRUSH's group, so a brush left on the default showed the
    /// whole sheet in the status bar's font colours while the canvas next to it was drawn in
    /// another — two pictures of the same tiles, disagreeing, with nothing saying why.
    ///
    /// Once per level, not per refresh: after that the group is the user's to pick, and
    /// re-seeding it would undo their choice on the next repaint.
    /// </summary>
    private int bgBrushSeededFor = -1;

    private void SeedBgBrushPalette(TilemapEdit map, int[] counts)
    {
        if (bgBrushSeededFor == session.LevelNum) return;
        bgBrushSeededFor = session.LevelNum;
        int best = 0;
        for (int g = 1; g < counts.Length; g++) if (counts[g] > counts[best]) best = g;
        if (counts[best] > 0) bgBrushL3 = bgBrushL3 & ~0x1C00 | best << 10;
    }

    /// <summary>
    /// The Background gutter palette. Layer 3 is 2bpp, so this shows FOUR colours — the whole
    /// point of giving the mode its own strip rather than reusing the sixteen-wide one, which
    /// would show twelve swatches the layer cannot draw and invite picking one.
    ///
    /// On layer 3 the picker is live: a tilemap word carries its own palette group, so this is
    /// what the next stamp writes into bits 10-12. On layer 2 it is inert and shows the BRUSH
    /// TILE's row instead — a BG Map16 tile carries its palette in its own definition, so the
    /// place to change it is the Map16 editor, and a live picker here would promise otherwise.
    /// </summary>
    private void RefreshBgPalette()
    {
        bool layer3 = bgLayer3.IsChecked == true;
        var pal = session.PaletteRgba;
        int group = layer3 ? Layer3.PaletteOf(bgBrushL3)
                  : map16?.BgTilePalette(bgBrush) ?? -1;
        int count = layer3 ? Layer3.PaletteColors : 16;
        int at = layer3 ? Layer3.PaletteBase(group) : group * 16;

        loadingBgPalRow = true;
        bgPalRow.SelectedIndex = layer3 ? group : -1;
        loadingBgPalRow = false;
        bgPalRow.IsEnabled = layer3;

        var colors = new uint[count];
        if (group >= 0 && pal.Length >= at + count)
            for (int i = 0; i < count; i++)
                // Colour 0 keeps the sheet's grey convention: in a tile it means transparent,
                // and a black swatch would read as a black you could paint with.
                colors[i] = i == 0 ? 0xFF303030u : pal[at + i];
        bgColors.Cols = count;
        bgColors.Colors = colors;
        bgColors.Describe = i => $"CGRAM {at + i:X2}" + (i == 0 ? " — transparent" : "");
        bgColors.InvalidateVisual();

        // The label says WHICH cells, because the two cases are a rectangle and the whole map and
        // the difference is not recoverable once pressed.
        bgApplyPal.IsVisible = layer3 && session.Layer3Map is not null;
        bgApplyPal.Content = bgView.Selection is { } s ? $"Apply to {s.W}x{s.H}" : "Apply to all";

        bgPalNote.Text = group < 0 ? ""
            : !layer3 ? $"CGRAM {at:X2}-{at + count - 1:X2} — the tile's own row; change it in Map16"
            : $"CGRAM {at:X2}-{at + count - 1:X2}"
              + (Layer3.IsLayer3Palette(group) ? " — layer 3's own colours"
                                              : " — the level's background palette, not layer 3's own");
    }

    /// <summary>
    /// Put the picked palette group on cells that already have tiles — the lasso's, or the whole
    /// map when there is no lasso. Only the group moves: the tile number, both flips and the
    /// priority bit stay, which is what makes this a recolour rather than a repaint.
    ///
    /// Cells the map never wrote (-1) are LEFT ALONE. Writing a group into one would turn the
    /// gaps into real words, and a flat file's gaps are exactly what <see cref="Layer3.ToBytes"/>
    /// pads with the blank tile — filling them here would put a tile everywhere the level meant
    /// to show nothing.
    /// </summary>
    private void OnApplyBgPalette(object? sender, RoutedEventArgs e)
    {
        if (session.Layer3Map is not { } map) return;
        var (x, y, w, h) = bgView.Selection ?? (0, 0, map.Cols, map.Rows);
        int group = Layer3.PaletteOf(bgBrushL3);
        bool changed = false;
        for (int r = y; r < y + h && r < map.Rows; r++)
            for (int c = x; c < x + w && c < map.Cols; c++)
                if (map.At(c, r) is >= 0 and var word)
                    changed |= map.Stamp(c, r, word & ~0x1C00 | group << 10);
        if (!changed || !map.EndStroke()) return;
        bgView.Invalidate();
        RefreshBg();
        UpdateTitle();
    }

    /// <summary>The Background gutter readout. "Which palette is this cell using" has no other
    /// answer in this mode — every cell carries its own group, so a single figure in the header
    /// could only ever be the brush's.</summary>
    private string BgReadout()
    {
        if (bgView.Hover is not { } h || BgLayerEdit is not { } map) return "";
        if (h.Col >= map.Cols || h.Row >= map.Rows) return "";
        int w = map.At(h.Col, h.Row);
        if (bgLayer3.IsChecked != true) return $"({h.Col,2},{h.Row,2})  tile 0x{w & 0x1FF:X3}";
        string flags = ((w & 0x2000) != 0 ? " pri" : "") + ((w & 0x4000) != 0 ? " flipX" : "")
                     + ((w & 0x8000) != 0 ? " flipY" : "");
        return $"({h.Col,2},{h.Row,2})  tile 0x{w & 0x3FF:X3}  pal {Layer3.PaletteOf(w)} "
             + $"(CGRAM {Layer3.PaletteBase(Layer3.PaletteOf(w)):X2})" + flags;
    }

    /// <summary>The drawer sheets are sixteen tiles to a row, as every other sheet here is.</summary>
    private const int SheetCols = 16;

    /// <summary>What the note says apart from the lasso. Kept so a lasso DRAG can update the
    /// note without going back through RefreshBg, which would recompose the whole grid on every
    /// cell the cursor crosses.</summary>
    private string bgNoteBase = "";

    private void RefreshBgNote()
    {
        // The tip carries what the ellipsised note cannot, and it is the question people arrive
        // with: painting a layer 3 needs no Save button of its own — the strokes ride the
        // project like every other level edit, and the ROM gets them at the next build.
        ToolTip.SetTip(bgNote, bgLayer3.IsChecked != true ? null
            : session.Layer3TilemapImported
                ? "This level has a layer-3 tilemap of its own. Painting it is saved with the "
                + "project (Ctrl+S) and written into the ROM when you build (F4)."
                : "This level is still showing the tilemap every level of its mode shares. The "
                + "first stroke gives it one of its own, so painting cannot disturb the others.");
        bgNote.Text = bgNoteBase
                    + (bgView.Selection is { } s ? $"  —  {s.W}x{s.H} selected, right-click to stamp" : "");
        // Every exit from RefreshBg lands here, and so does a lasso drag — which is what the
        // Apply button's label reads, so the gutter has to follow both.
        RefreshBgPalette();
    }

    // ---- painting a background ----
    // Left paints, right is the eyedropper, and the drawer's left click arms the brush. The
    // level and Map16 canvases stamp on the RIGHT because their left button runs a selection;
    // this mode has none, so left painting is the binding that leaves no button idle.

    private TilemapEdit? BgLayerEdit => bgLayer3.IsChecked == true ? session.Layer3Map : session.BgMap;

    private void BgPaint(int col, int row)
    {
        if (BgLayerEdit is not { } map) return;
        bool changed;
        if (bgView.Selection is { } sel)
        {
            // Read the whole rectangle BEFORE writing any of it: stamping a selection over
            // itself is the ordinary case (nudging a pattern along by a cell), and reading as
            // you write smears the first row across the rest.
            var copy = new int[sel.W * sel.H];
            for (int j = 0; j < sel.H; j++)
                for (int i = 0; i < sel.W; i++)
                    copy[j * sel.W + i] = map.At(sel.X + i, sel.Y + j);
            changed = false;
            for (int j = 0; j < sel.H; j++)
                for (int i = 0; i < sel.W; i++)
                    changed |= map.Stamp(col + i, row + j, copy[j * sel.W + i]);
        }
        else changed = map.Stamp(col, row, bgLayer3.IsChecked == true ? bgBrushL3 : bgBrush);
        if (changed) bgView.Invalidate();
    }

    /// <summary>
    /// A selection was dragged to a new place, or grown by a grip. Both are the same write: fill
    /// the new rectangle by REPEATING the old one, which for an equal-sized rectangle is a plain
    /// copy and for a grown one tiles the pattern out along whichever axis was dragged. The
    /// repeat is phased on the old rectangle's own origin, so the block that was there does not
    /// shift under the cursor while the space beside it fills in.
    ///
    /// A move then clears what it left behind — to -1 on layer 3, which is a word the tilemap
    /// never wrote and builds as the transparent tile, and to tile 0 on layer 2, which has no
    /// "unwritten": its cells are bytes and every one of them draws something.
    /// </summary>
    private void BgSelectionDragged(TilemapView.SelectionDrag d)
    {
        if (BgLayerEdit is not { } map) return;
        var (from, to) = (d.From, d.To);
        var src = new int[from.W * from.H];
        for (int j = 0; j < from.H; j++)
            for (int i = 0; i < from.W; i++)
                src[j * from.W + i] = map.At(from.X + i, from.Y + j);

        bool changed = false;
        if (d.Move)
            for (int j = 0; j < from.H; j++)
                for (int i = 0; i < from.W; i++)
                {
                    int c = from.X + i, r = from.Y + j;
                    if (c >= to.X && c < to.X + to.W && r >= to.Y && r < to.Y + to.H) continue;
                    changed |= map.Stamp(c, r, bgLayer3.IsChecked == true ? -1 : 0);
                }
        for (int r = to.Y; r < to.Y + to.H; r++)
            for (int c = to.X; c < to.X + to.W; c++)
            {
                var (sc, sr) = d.Source(c, r);
                changed |= map.Stamp(c, r, src[(sr - from.Y) * from.W + (sc - from.X)]);
            }
        if (!changed || !map.EndStroke()) return;
        bgView.Invalidate();
        RefreshBg();
        UpdateTitle();
    }

    /// <summary>Mouse up: the stroke becomes one undo entry and the level's data changes. A drag
    /// that painted nothing new settles into nothing, so it cannot clear the redo stack.</summary>
    private void BgStrokeEnded()
    {
        if (BgLayerEdit?.EndStroke() != true) return;
        RefreshBg();
        UpdateTitle();
    }

    /// <summary>Picking in the drawer DROPS the canvas lasso. The lasso outranks the drawer's
    /// tile when both exist, so leaving it up would make the pick do nothing at all — the
    /// precedence is about which is armed, and picking is how you arm the other one.</summary>
    private void BgBrushPicked(int col, int row)
    {
        int tile = row * SheetCols + col;
        if (bgLayer3.IsChecked == true)
        {
            if (tile >= Layer3.TileCount) return;
            bgBrushL3 = (bgBrushL3 & ~0x3FF) | tile;
        }
        else
        {
            if (tile >= EditorSession.BgSheetTiles) return;
            bgBrush = tile;
        }
        bgView.ClearSelection();
        RefreshBg();
    }

    // ---- Animations canvas mode ----

    /// <summary>Which list the timeline shows (the level's or the global one) and which of its
    /// slots is open on the right.</summary>
    private bool animGlobal = true;          // the global list opens first; Level is the toggle
    private int animSelected = -1;
    private DispatcherTimer? animPreview;
    private bool loadingAnimHeader;

    private void OnAnimList(object? sender, RoutedEventArgs e)
    {
        animGlobal = ReferenceEquals(sender, animGlobalBtn);
        animLevelBtn.IsChecked = !animGlobal;
        animGlobalBtn.IsChecked = animGlobal;
        animSelected = -1;
        RefreshAnim();
    }

    /// <summary>Add a slot NOW, decide later: it comes into being as one 8x8 with one frame, and
    /// every decision — type, trigger, destination, which tiles, how many frames — is made on the
    /// timeline it opens into.</summary>
    private void OnAnimAdd(object? sender, RoutedEventArgs e)
    {
        if (session.AddExAnimSlot(animGlobal) is not { } slot) return;
        animSelected = slot.Index;
        RefreshAnim();
    }

    /// <summary>Header button: drop the open slot from the list (the record is rewritten without it).</summary>
    private void OnAnimDelete(object? sender, RoutedEventArgs e)
    {
        if (animSelected < 0) return;
        session.SetExAnim(animGlobal, session.ExAnimSlots(animGlobal).Where(x => x.Index != animSelected).ToList(), session.ExAnimAltFile(animGlobal));
        animSelected = -1;
        RefreshAnim();
    }

    /// <summary>Header button: move the open slot to another slot number, picked in a modal
    /// from the numbers this list still has free.</summary>
    private async void OnAnimReassign(object? sender, RoutedEventArgs e)
    {
        var slots = session.ExAnimSlots(animGlobal);
        if (slots.All(s => s.Index != animSelected)) return;
        var free = Enumerable.Range(0, 0x20).Where(i => slots.All(s => s.Index != i)).ToList();
        if (free.Count == 0) return;                          // all 32 in use: nowhere to go
        var dlg = new SlotNumberWindow(animSelected, free);
        await dlg.ShowDialog(this);
        if (dlg.Result is not { } to) return;
        if (session.ReassignExAnimSlot(animGlobal, animSelected, to))
        {
            animSelected = to;
            RefreshAnim();
        }
    }

    /// <summary>The gutter's sixteen swatches for the preview row — the Map16 bar's logic.</summary>
    private void RefreshAnimColors(int row)
    {
        var colors = new uint[16];
        if (row >= 0 && session.PaletteRgba is { } pal && pal.Length >= (row + 1) * 16)
            for (int i = 0; i < 16; i++)
                colors[i] = i == 0 ? 0xFF303030u : pal[row * 16 + i];
        animColors.Cols = 16;
        animColors.Colors = colors;
        animColors.InvalidateVisual();
    }

    /// <summary>Write one slot back and redraw.</summary>
    private void PutSlot(ExAnimation.Slot slot)
    {
        animSelected = slot.Index;
        if (session.SetExAnimSlot(animGlobal, slot)) RefreshAnim();
    }

    /// <summary>
    /// The timeline: the left lists the list's slots in slot order (click one to open it), the
    /// right is the open slot's editor — type, trigger and destination inline, an animated preview
    /// at the game's 7.5 fps, and the frame strip: click a frame to pick its tiles on the source
    /// sheet, × to drop it, + at the end to add one. The header picks the list, adds slots, and
    /// sets the list's source file and the preview palette row.
    /// </summary>
    private void RefreshAnim()
    {
        animPreview?.Stop(); animPreview = null;
        animBody.Children.Clear(); animPreviewBody.Children.Clear();
        animGfx.Children.Clear();
        animEmptyAdd.IsVisible = false;
        if (session.Rom is not { } rom) return;
        bool ready = rom.LmExAnimBase >= 0;
        animTitle.Text = !ready ? "no ExAnimation engine — File → Upgrade base (prep v11)" : "";
        animListTitle.Text = animGlobal ? "Global slots" : $"Level {session.LevelNum:X3} slots";
        if (!ready) return;

        var slots = session.ExAnimSlots(animGlobal).OrderBy(s => s.Index).ToList();
        int alt = session.ExAnimAltFile(animGlobal);
        loadingAnimHeader = true;
        animFile.SelectedIndex = alt;
        loadingAnimHeader = false;
        int palRow = Math.Max(0, animPalRow.SelectedIndex);
        RefreshAnimColors(palRow);
        if (slots.All(s => s.Index != animSelected)) animSelected = slots.Count > 0 ? slots[0].Index : -1;

        // ---- left: the slots, in slot order ----
        if (slots.Count == 0)
        {
            animGfx.Children.Add(Dim("No slots yet — Add slot in the bar above."));
            animEmptyAdd.IsVisible = true;                    // ...or right here, centred on the desk
        }
        foreach (var s in slots)
        {
            bool open = s.Index == animSelected;
            var head = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = $"[{s.Index:X2}]", Width = 34, FontWeight = FontWeight.Bold, Foreground = (IBrush)this.FindResource("TextDimBrush")! });
            head.Children.Add(new TextBlock { Text = SlotTitle(s), TextTrimming = TextTrimming.CharacterEllipsis });
            var block = new StackPanel();
            block.Children.Add(new Border { Child = head, Padding = new Thickness(8, 6), Background = (IBrush)this.FindResource("SurfaceBrush")!, CornerRadius = new CornerRadius(4, 4, 0, 0) });
            var (px, w, h) = session.ExAnimFramePixels(s, 0, palRow);
            block.Children.Add(px.Length > 0
                ? new PixelImage { Source = LevelBitmap.FromPixels(px, w, h), Width = w * 4, Height = h * 4, Stretch = true, Margin = new Thickness(8, 6), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }
                : Mono(s.IsPalette ? $"palette {s.DestColor:X2} x{s.Colors}" : "(source not loaded)"));
            var card = new Border
            {
                Child = block, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2),
                BorderBrush = open ? UiColors.Accent : this.FindResource("BorderBrush") as IBrush,
                Background = open ? UiColors.SelectionFill : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            };
            int idx = s.Index;
            card.PointerPressed += (_, _) => { animSelected = idx; RefreshAnim(); };
            animGfx.Children.Add(card);
        }

        // ---- right: the open slot's editor ----
        animDelete.IsEnabled = slots.Any(s => s.Index == animSelected);   // the header's Delete acts on the open slot
        animReassign.IsEnabled = animDelete.IsEnabled;                    // ...and so does Reassign
        if (slots.FirstOrDefault(s => s.Index == animSelected) is not { Frames: not null } sel) return;

        // The decisions, inline. Each change writes the slot straight back — there is no OK.
        var row = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        // Simple mode culls both dropdowns to the everyday choices; Advanced shows the engine's
        // full catalog. A slot already using a culled value keeps it in the list — filtering the
        // DISPLAY must never rewrite the slot.
        bool adv = animAdvanced.IsChecked == true;
        var types = (adv ? ExAnimSlotWindow.Types
                         : ExAnimSlotWindow.Types.Where(t => t.Code is <= 0x08 or 0x0F or 0x11)).ToList();
        if (types.All(t => t.Code != sel.Type))
            types.Add(ExAnimSlotWindow.Types.First(t => t.Code == sel.Type));
        var type = new ComboBox { ItemsSource = types.Select(t => t.Name).ToList(), Width = 250,
                                  SelectedIndex = Math.Max(0, types.FindIndex(t => t.Code == sel.Type)) };
        type.SelectionChanged += (_, _) =>
        {
            int code = types[type.SelectedIndex].Code;
            if (code == sel.Type) return;
            // Tile ↔ palette keep nothing in common: a palette slot starts on colour 00 with its
            // frame words as colours; a tile slot goes back to tile 600. Tile ↔ tile keeps the frames.
            var s2 = sel with { Type = code };
            bool wasPal = sel.IsPalette, isPal = code >= ExAnimation.TypePalette;
            if (wasPal != isPal) s2 = s2 with { DestWord = 0, Frames = [.. Enumerable.Repeat((ushort)(isPal ? 0x7FFF : 0x7D00), sel.Frames.Length)] };
            PutSlot(s2);
        };
        var trigs = (adv ? ExAnimSlotWindow.Triggers
                         : ExAnimSlotWindow.Triggers.Where(t => t.Code <= 0x04)).ToList();   // None..Have Star
        if (trigs.All(t => t.Code != sel.Trigger))
            trigs.Add(ExAnimSlotWindow.Triggers.First(t => t.Code == sel.Trigger));
        var trig = new ComboBox { ItemsSource = trigs.Select(t => t.Name).ToList(), Width = 210,
                                  SelectedIndex = Math.Max(0, trigs.FindIndex(t => t.Code == sel.Trigger)) };
        trig.SelectionChanged += (_, _) =>
        {
            int code = trigs[trig.SelectedIndex].Code;
            if (code == sel.Trigger) return;
            // Going stateful doubles the list (the triggered half starts as a copy); going back keeps the first half.
            bool was = ExAnimation.TriggerDoubles(sel.Trigger), now = ExAnimation.TriggerDoubles(code);
            ushort[] frames = sel.Frames;
            if (!was && now) frames = [.. frames, .. frames];
            else if (was && !now) frames = frames[..Math.Min(sel.FrameCount, frames.Length)];
            PutSlot(sel with { Trigger = code, Frames = frames });
        };
        row.Children.Add(Labelled("type", type));
        row.Children.Add(Labelled("trigger", trig));
        if (sel.IsPalette)
        {
            var color = HexBox(sel.DestColor.ToString("X2"), 2, v => PutSlot(sel with { DestWord = (sel.DestWord & 0xFF00) | (v & 0xFF) }));
            var count = HexBox(sel.Colors.ToString(), 3, v => PutSlot(sel with { DestWord = (sel.DestWord & 0x80FF) | ((Math.Clamp(v, 1, 0x80) - 1) << 8) }), hex: false);
            row.Children.Add(Labelled("first colour", color));
            row.Children.Add(Labelled("colours", count));
        }
        else
        {
            // The destination is picked on the level's VRAM sheet; the button shows what sits there
            // now — the tiles the animation will overwrite — in the slot's own footprint.
            var (dpx, dw, dh) = session.ExAnimDestPixels(sel, palRow);
            var destFace = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            if (dpx.Length > 0) destFace.Children.Add(new PixelImage { Source = LevelBitmap.FromPixels(dpx, dw, dh), Width = dw * 3, Height = dh * 3, Stretch = true, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            destFace.Children.Add(Mono($"{sel.DestTile:X3}"));
            var dest = new Button { Content = destFace, Padding = new Thickness(8, 4) };
            ToolTip.SetTip(dest, "click to pick the destination on the level's VRAM sheet");
            dest.Click += async (_, _) =>
            {
                var pick = new TilePickerWindow(session, sel, palRow);
                await pick.ShowDialog(this);
                if (pick.Picked is { } t) PutSlot(sel with { DestWord = (sel.DestWord & 0x8000) | ExAnimation.LmTileToWord(t) });
            };
            row.Children.Add(Labelled("destination", dest));
        }
        animBody.Children.Add(row);
        string note = sel.Doubled ? "Stateful trigger: the first half of the frames plays untriggered, the second half once triggered."
                    : sel.Trigger >= ExAnimation.TriggerOneShot0 ? "One shot: plays through once when triggered, then stops."
                    : sel.Trigger >= ExAnimation.TriggerManual0 ? "Manual: shows whichever frame a custom block writes to $7FC070+n." : "";
        if (note.Length > 0) animBody.Children.Add(Dim(note));
        if (sel.IsPalette)
        {
            animBody.Children.Add(Dim(ExAnimation.HasFrameWords(sel.Type)
                ? "Palette slot: each frame is an SNES colour word (BGR555). Click a frame to type one."
                : "Palette rotation: no frame data — the frame count is the delay between steps."));
        }

        // ---- the frame strip ----
        var frames = new List<Avalonia.Media.Imaging.Bitmap>();
        var strip = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        int total = ExAnimation.HasFrameWords(sel.Type) ? sel.Frames.Length : 0;
        for (int f = 0; f < total; f++)
        {
            int fi = f;
            bool triggered = sel.Doubled && f >= sel.FrameCount;
            var col = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8, 10, 10) };
            // Label on the left, × pinned to the right — a full-width header band across the
            // card's top, the same treatment as the slot listing's card headers.
            var top = new DockPanel();
            if (sel.FrameCount > 1 && !triggered)
            {
                var x = new Button { Content = "×", Padding = new Thickness(5, 0), FontSize = 11,
                                     HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                ToolTip.SetTip(x, "remove this frame");
                x.Click += (_, _) => PutSlot(WithoutFrame(sel, fi));
                DockPanel.SetDock(x, Avalonia.Controls.Dock.Right);
                top.Children.Add(x);
            }
            var label = Mono((triggered ? "Triggered " : "Frame ") + $"{(f % sel.FrameCount) + 1}");
            label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 10, 0);           // room before the ×, two digits included
            label.Foreground = Brushes.White;                    // a header, not a dim annotation
            top.Children.Add(label);

            Control face;
            if (sel.IsPalette)
            {
                int bgr = sel.Frames[f];
                var sw = new Border { Width = 48, Height = 32, CornerRadius = new CornerRadius(3),
                                      Background = new SolidColorBrush(Color.FromRgb((byte)((bgr & 31) * 8), (byte)(((bgr >> 5) & 31) * 8), (byte)(((bgr >> 10) & 31) * 8))) };
                face = sw;
                col.Children.Add(FrameMat(face));
                col.Children.Add(Mono($"{bgr:X4}"));
            }
            else
            {
                var (px, w, h) = session.ExAnimFramePixels(sel, f, palRow);
                Avalonia.Media.Imaging.Bitmap? bmp = px.Length > 0 ? LevelBitmap.FromPixels(px, w, h) : null;
                if (bmp is not null && !triggered) frames.Add(bmp);
                face = bmp is not null
                    ? new PixelImage { Source = bmp, Width = w * 4, Height = h * 4, Stretch = true }
                    : new Border { Width = 64, Height = 32, Background = (IBrush)this.FindResource("SurfaceBrush")!, Child = Mono("pick…") };
                col.Children.Add(FrameMat(face));
                col.Children.Add(Mono($"tile {sel.SrcTile(f):X3}"));
            }
            // Lighter than the card body: the Surface tone sank into the desk pattern behind it.
            var head = new Border { Child = top, Padding = new Thickness(10, 5),
                                    CornerRadius = new CornerRadius(5, 5, 0, 0),
                                    Background = (IBrush)this.FindResource("BorderBrush")! };
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(col);
            var cardF = new Border { Child = stack, Margin = new Thickness(0, 0, 8, 8), MinWidth = 104,
                                     CornerRadius = new CornerRadius(5),
                                     Cursor = new Cursor(StandardCursorType.Hand),
                                     Background = this.FindResource("RaisedBrush") as IBrush };
            ToolTip.SetTip(cardF, sel.IsPalette ? "click to set this frame's colour" : "click to pick this frame's tiles on the source sheet");
            cardF.PointerPressed += async (_, _) => await PickFrame(sel, fi, palRow);
            // The whole card — band included — lightens under the pointer, so it reads as one button.
            var headBg = head.Background;
            cardF.PointerEntered += (_, _) => { cardF.Background = FrameCardHover; head.Background = FrameHeadHover; };
            cardF.PointerExited += (_, _) => { cardF.Background = this.FindResource("RaisedBrush") as IBrush; head.Background = headBg; };
            strip.Children.Add(cardF);
        }
        if (total > 0 && sel.FrameCount < 0x100)
        {
            // The + at the end of the timeline: a new frame, a copy of the last, in both halves
            // when the trigger keeps two.
            // The cta class carries the accent fill AND its lighter-blue hover — an inline
            // Background would win the base state but lose :pointerover to the template's brush,
            // which showed as a translucent grey on hover.
            var plus = new Button
            {
                Content = "Add Frame", Height = 48, Padding = new Thickness(14, 0),
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            };
            plus.Classes.Add("cta");
            ToolTip.SetTip(plus, "add a frame");
            plus.Click += (_, _) => PutSlot(WithAddedFrame(sel));
            strip.Children.Add(plus);
        }

        if (frames.Count > 0)
        {
            // The animated preview lives in the right drawer, scaled to fit its width.
            int scale = Math.Clamp(200 / frames[0].PixelSize.Width, 2, 8);
            var preview = new PixelImage { Source = frames[0], Width = frames[0].PixelSize.Width * scale, Height = frames[0].PixelSize.Height * scale, Stretch = true,
                                           HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            animPreviewBody.Children.Add(new Border { Child = preview, Padding = new Thickness(8), Background = (IBrush)this.FindResource("SurfaceBrush")!, CornerRadius = new CornerRadius(5),
                                                      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center });
            animPreviewBody.Children.Add(Dim($"{frames.Count} frame(s) at the game's rate (7.5 fps) → destination tile {sel.DestTile:X3}."));
            int at = 0;
            animPreview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 7.5) };
            animPreview.Tick += (_, _) => { at = (at + 1) % frames.Count; preview.Source = frames[at]; };
            animPreview.Start();
        }
        animBody.Children.Add(strip);

        TextBlock Dim(string t) { var b = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap }; b.Classes.Add("dim"); return b; }
        TextBlock Mono(string t) { var b = new TextBlock { Text = t }; b.Classes.Add("mono"); return b; }
        Control Labelled(string label, Control c)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            l.Classes.Add("dim");
            return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 6), Children = { l, c } };
        }
        // A small hex (or decimal) box that commits on Enter or focus loss, so typing does not rewrite the ROM per keystroke.
        TextBox HexBox(string text, int width, Action<int> commit, bool hex = true)
        {
            var box = new TextBox { Text = text, Width = 26 + width * 12 };
            box.Classes.Add("mono");
            void Commit()
            {
                try { int v = hex ? Convert.ToInt32(box.Text?.Trim(), 16) : int.Parse(box.Text?.Trim() ?? ""); if ((box.Text ?? "").Trim() != text) commit(v); }
                catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { box.Text = text; }
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            box.LostFocus += (_, _) => Commit();
            return box;
        }
        static string SlotTitle(ExAnimation.Slot s)
        {
            string type = ExAnimSlotWindow.Types.FirstOrDefault(t => t.Code == s.Type).Name ?? $"type {s.Type:X2}";
            string trig = s.Trigger == 0 ? "" : " · " + (ExAnimSlotWindow.Triggers.FirstOrDefault(t => t.Code == s.Trigger).Name ?? $"trigger {s.Trigger:X2}");
            return s.IsPalette ? $"{type} → colour {s.DestColor:X2}{trig}" : $"{type} → {s.DestTile:X3}{trig}";
        }
    }

    /// <summary>A frame's tiles are chosen on the source sheet; a palette frame's colour is typed.
    /// Picking from the alternate file flips the slot to alt-file sourcing (and back), since that is
    /// a slot-wide switch in the record — the other frames keep their words and get re-picked.</summary>
    private async Task PickFrame(ExAnimation.Slot sel, int f, int palRow)
    {
        if (sel.IsPalette)
        {
            var dlg = new TextPromptWindow("Frame colour — SNES colour word (BGR555, hex; 7FFF is white)", sel.Frames[f].ToString("X4"));
            await dlg.ShowDialog(this);
            if (dlg.Result is not { } txt) return;
            try { var fr = (ushort[])sel.Frames.Clone(); fr[f] = (ushort)Convert.ToInt32(txt.Trim(), 16); PutSlot(sel with { Frames = fr }); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { animTitle.Text = "not a hex colour"; }
            return;
        }
        // The footprint on the SHEET is always a consecutive run of the slot's tiles: the engine
        // DMAs a frame as one line from the source, so that is where the tiles live — a 16x16 is
        // drawn as four tiles in a row (TL TR BL BR), exactly as Lunar Magic asks. Nothing is
        // copied or packed; the frame word names the run directly.
        int alt = session.ExAnimAltFile(animGlobal);
        int[] footprint = Enumerable.Range(0, Math.Max(1, sel.TileCount)).ToArray();
        var pick = new TilePickerWindow(session, footprint, alt, palRow, sel.AltFile, animGlobal)
        {
            // "Edit…" on the alternate file: straight to the Graphics editor on that file, the
            // way clicking its E6x card there would.
            EditRequested = file => { gfxSlot = file; EditGfxFile(file, palRow); },
        };
        await pick.ShowDialog(this);
        if (pick.Picked is not { } tile) return;

        int word = ExAnimSlotWindow.TileToWord(tile, pick.PickedAlt, alt);
        if (word < 0) return;
        bool useAlt = pick.PickedAlt;
        var frames = (ushort[])sel.Frames.Clone();
        frames[f] = (ushort)word;
        int destWord = useAlt ? sel.DestWord | 0x8000 : sel.DestWord & 0x7FFF;
        PutSlot(sel with { Frames = frames, DestWord = destWord });
    }

    /// <summary>Hover tones for the frame cards: one step lighter than RaisedColor/BorderColor.</summary>
    private static readonly IBrush FrameCardHover = new SolidColorBrush(Color.Parse("#323947"));
    private static readonly IBrush FrameHeadHover = new SolidColorBrush(Color.Parse("#404757"));

    /// <summary>The 4px mat around a frame card's preview, so the pixels read as a framed
    /// thumbnail rather than art floating on the card.</summary>
    private Border FrameMat(Control face) => new()
    {
        Child = face, BorderThickness = new Thickness(4),
        BorderBrush = this.FindResource("BorderBrush") as IBrush,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };

    /// <summary>One more frame, a copy of the last — added to both halves of a doubled list.</summary>
    private static ExAnimation.Slot WithAddedFrame(ExAnimation.Slot s)
    {
        int n = s.FrameCount;
        var a = s.Frames.Take(n).ToList();
        a.Add(a.Count > 0 ? a[^1] : (ushort)0x7D00);
        if (s.Doubled)
        {
            var b = s.Frames.Skip(n).Take(n).ToList();
            b.Add(b.Count > 0 ? b[^1] : a[^1]);
            a.AddRange(b);
        }
        return s with { FrameCount = n + 1, Frames = [.. a] };
    }

    private static ExAnimation.Slot WithoutFrame(ExAnimation.Slot s, int f)
    {
        int n = s.FrameCount;
        if (n <= 1) return s;
        var a = s.Frames.Take(n).ToList(); a.RemoveAt(f);
        if (s.Doubled)
        {
            var b = s.Frames.Skip(n).Take(n).ToList();
            if (f < b.Count) b.RemoveAt(f);
            a.AddRange(b);
        }
        return s with { FrameCount = n - 1, Frames = [.. a] };
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

        // Nothing selected: the row stays put, greyed and blanked out. Hiding it would take the
        // bar's height with it, and there is nothing to say about a tile that is not there.
        m16SelLabel.IsEnabled = m16Fields.IsEnabled = tiles.Count > 0;
        if (tiles.Count == 0)
        {
            loadingM16Props = true;
            m16SelLabel.Text = "Tile ######";
            m16Acts.Text = "-";
            m16ActsNote.Text = "";
            m16Priority.IsChecked = false;
            m16Palette.SelectedIndex = -1;
            RefreshM16Colors(-1);
            m16Fields.IsVisible = true;
            m16Unallocated.IsVisible = false;
            loadingM16Props = false;
            return;
        }

        int first = tiles[0];
        m16SelLabel.Text = tiles.Count > 1
            ? $"{tiles.Count} tiles selected"
            : $"Tile 0x{first:X4}";

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
        RefreshM16Colors(def[0].Palette);
        loadingM16Props = false;
    }

    private void ApplyM16Acts()
    {
        if (loadingM16Props || map16 is not { } m16) return;
        if (!int.TryParse(m16Acts.Text, System.Globalization.NumberStyles.HexNumber, null, out int v))
        { RefreshMap16Props(); return; }
        m16.SetActsAs(map16Canvas.SelectedTiles(), v);
    }

    /// <summary>The sixteen colours a palette row draws with, in the Map16 gutter. Index 0 keeps
    /// the sheet's grey convention — in a tile it means transparent, and a black swatch would read
    /// as black. A row of -1 (nothing selected) leaves the strip empty rather than showing row 0's
    /// colours as if they were the selection's.</summary>
    private void RefreshM16Colors(int row)
    {
        var colors = new uint[16];
        if (row >= 0 && session.PaletteRgba is { } pal && pal.Length >= (row + 1) * 16)
            for (int i = 0; i < 16; i++)
                colors[i] = i == 0 ? 0xFF303030u : pal[row * 16 + i];
        m16Colors.Cols = 16;
        m16Colors.Colors = colors;
        m16Colors.InvalidateVisual();
    }

    /// <summary>The Map16 cursor draws the 8x8 brush's footprint, so it follows the picker —
    /// when the pick changes and when it is dropped.</summary>
    private void AdoptChrBrush()
    {
        map16Canvas.BrushW = chr.Brush.W;
        map16Canvas.BrushH = chr.Brush.H;
        map16Canvas.InvalidateVisual();
    }

    /// <summary>Radio behaviour without a group, as the canvas modes do it: exactly one grain.
    /// The canvas is the one that acts on it — this pair is only how it gets said.</summary>
    private void OnGrain(object? sender, RoutedEventArgs e) => SetGrain(ReferenceEquals(sender, grain8));

    private void SetGrain(bool quad8)
    {
        grain8.IsChecked = quad8;
        grain16.IsChecked = !quad8;
        map16Canvas.Grain = quad8 ? Map16CanvasView.TileGrain.Quad8 : Map16CanvasView.TileGrain.Tile16;
        map16Canvas.InvalidateVisual();
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
        var (px, w, h) = session.SheetPhases();
        map16Canvas.SetSheet(px, w, h, session.Map16TileCount);
        map16Canvas.SetPlaceholder(session.PlaceholderPhases());
        map16Canvas.Bank = Math.Max(0, bankBox.SelectedIndex);
        RebuildChrSheet();
    }

    private void RebuildChrSheet()
    {
        var (px, w, h) = session.ChrPhases(ChrPalRow);
        if (px[0] is not null) chr.SetSheet(px, w, h);
    }

    /// <summary>
    /// The Map16 word a brush cell stamps: the 8x8 tile number in the low 10 bits, then the
    /// palette row. This packing IS the Map16 format (CONTRACT §5), which is why the row lives
    /// with the brush rather than being applied afterwards — and why the flip and priority bits
    /// belong here too, once <see cref="ChrPalRow"/>'s controls have somewhere to live again.
    /// </summary>
    private ushort GfxBrushWord(int bx, int by)
        => (ushort)((chr.TileOfBrushCell(bx, by) & 0x3FF) | (ChrPalRow << 10));

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
