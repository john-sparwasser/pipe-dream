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
/// The left drawer: which pane it shows (the Tiles bank and lasso, the sprite and object
/// catalogs, the Palette pane, or a canvas mode's own tools) and how wide it is per pane.
/// The Palette pane's controls are wired here too, since it lives in the drawer; its
/// behaviour is in MainWindow.Palette.cs.
/// </summary>
public partial class MainWindow
{
    private TabStrip paletteTabs = null!;
    private DockPanel spritePanel = null!, objectPanel = null!, palettePanel = null!;

    private ListBox spriteList = null!, objectList = null!;
    private CheckBox loadedOnly = null!;
    private TextBlock spFilesLabel = null!, objectHint = null!;

    /// <summary>The left drawer: the Tiles bank and lasso, the sprite/object tabs, the Palette pane, and the pane sizing.</summary>
    private void WireDrawer()
    {
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
        // A lassoed block in the drawer is a brush like one grabbed from the level.
        palette.BrushPicked += (_, b) =>
        {
            selLabel.Text = $"{b.W}x{b.H} tiles";
            SetBrush(b.Tiles, b.W, b.H);
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
        paletteReset = this.GetControl<Button>("PaletteReset");
        palScopeTabs = this.GetControl<TabStrip>("PalScopeTabs");
        palSubmapRow = this.GetControl<StackPanel>("PalSubmapRow");
        palSubmap = this.GetControl<ComboBox>("PalSubmap");
        foreach (var name in SubmapNames) palSubmap.Items.Add(name);
        palSubmap.SelectedIndex = 0;
        palScopeTabs.SelectionChanged += (_, _) => ApplyPaletteScope();
        palSubmap.SelectionChanged += (_, _) => { if (palettePanel.IsVisible) RefreshPaletteTab(); };

        paletteGrid.IsEdited = i => !PaletteScopeOverworld && session.IsPaletteEdited(i);
        paletteGrid.Describe = SwatchRgb;       // the hover tip is the colour; the readout below says the rest
        paletteGrid.SelectionChanged += (_, i) => { paletteBg.Select(-1); ShowPaletteColor(i); OpenPicker(); };
        // The background colour (CGRAM 0) lives in its own swatch above the grid; selection is
        // still paletteGrid.Selected — the swatch just points it at index 0.
        paletteBg = this.GetControl<PaletteGridView>("PaletteBg");
        paletteGrid.HideFirst = true;
        paletteBg.IsEdited = _ => !PaletteScopeOverworld && session.IsPaletteEdited(0);
        paletteBg.Describe = _ => SwatchRgb(0);
        // The grid fits the drawer, so the drawer's width is its zoom (splitter or Alt/Cmd+wheel,
        // like every drawer sheet); the lone background swatch above it keeps the same cell size.
        paletteGrid.FitWidth = true;
        paletteGrid.Headers = true;
        paletteGrid.SizeChanged += (_, _) => { paletteBg.Cell = paletteGrid.Cell; paletteBg.InvalidateMeasure(); };
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

        drawer.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) OnDrawerVisibilityChanged();
        };

        ApplyDrawerPane(Pane.Level);
        drawer.AddHandler(PointerWheelChangedEvent, DrawerWheel, RoutingStrategies.Tunnel);
    }

    // ---- drawer tabs ----

    /// <summary>
    /// The drawer tab and the canvas edit mode are two views of ONE thing, as in the ImGui
    /// editor: the Sprites tab means you are editing sprites, Map16 and Objects mean you are
    /// editing layer 1. Picking a tab therefore switches the mode (and drops the selection that
    /// belonged to the old one), which is why Esc also moves the tab.
    /// </summary>
    private void OnPaletteTab()
    {
        // An overlay mode outranks the tabs: it took the canvas from whichever layer was being
        // edited, and a drawer tab is not how you leave it — the toggle is.
        if (canvas.Mode is LevelView.EditMode.Exits or LevelView.EditMode.Entrances)
        { RefreshDrawer(); return; }

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
        bool owMode = modeOverworld.IsChecked == true;
        bool paletteMode = modePalette.IsChecked == true;
        bool modal = map16Mode || gfxMode || animMode || bgMode || owMode || paletteMode;   // a canvas mode owning the drawer
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
        owToolPanel.IsVisible = owMode;
        animPaletteBar.IsVisible = animMode;    // its gutter palette, like the Map16 and GFX ones
        gfxPaletteBar.IsVisible = gfxMode;      // canvas-side, but the same mode decides it
        m16PaletteBar.IsVisible = map16Mode;    // its opposite number, same gutter
        bgPaletteBar.IsVisible = bgMode;        // four swatches wide there, not sixteen
        spritePanel.IsVisible = tab == 1;
        objectPanel.IsVisible = tab == 2;
        palettePanel.IsVisible = paletteMode;
        if (spritePanel.IsVisible) EnsureSpriteCatalog();
        if (objectPanel.IsVisible) EnsureObjectCatalog();
        if (palettePanel.IsVisible) RefreshPaletteTab();
    }

    /// <summary>Sprite thumbnails are drawn with THIS level's SP GFX and palette, so the catalog
    /// belongs to the level; the session decides when it is stale.</summary>
    private void EnsureSpriteCatalog()
    {
        // SpriteCatalog() rebuilds only when the session dropped its list, so asking it is what
        // detects staleness: the same list back means the rows are still good.
        var (items, files) = session.SpriteCatalog();
        if (items.Count == 0) return;
        if (spriteCatalog is not null && ReferenceEquals(items, spriteCatalogSource)
            && spriteList.ItemsSource is not null) return;
        spriteCatalogSource = items;
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

    private List<CatalogRow>? spriteCatalog, objectCatalog;
    /// <summary>The session list the sprite rows were wrapped from. The session drops its list
    /// whenever the level, its GFX or its palette change; the rows here have to follow it, or
    /// the list box (emptied on every adopt) is never refilled.</summary>
    private IReadOnlyList<CatalogItem>? spriteCatalogSource;
    private int objectCatalogTileset = -1;

    // ---- drawer width, per pane ----

    /// <summary>Point the drawer at a pane: bank the width the outgoing one was left at, then take
    /// the incoming one's — its own remembered width, or what its content needs the first time.
    /// Re-running it for the CURRENT pane is how a content resize (the Map16 tile zoom) re-floors
    /// the drawer without discarding a splitter drag.</summary>
    private void ApplyDrawerPane(Pane pane)
    {
        var col = split.ColumnDefinitions[0];
        if (col.Width.IsAbsolute && col.Width.Value > 0) drawerWidths[drawerPane] = col.Width.Value;
        drawerPane = pane;
        (col.MinWidth, col.MaxWidth) = DrawerRange(pane);
        if (drawer.IsVisible) col.Width = new GridLength(WantedDrawerWidth(pane));
    }

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

    private double WantedDrawerWidth(Pane pane)
        => Math.Max(drawerWidths.GetValueOrDefault(pane), NaturalDrawerWidth(pane));

    /// <summary>Which thing the drawer is holding. Not the same as the canvas mode by accident —
    /// each mode's drawer shows different content, and they are nowhere near the same width.</summary>
    private enum Pane { Level, Map16, Graphics, Background, Animations, Overworld }

    private Pane drawerPane = Pane.Level;

    /// <summary>Where each pane was last left. Absent = never seen, so it opens at its content
    /// width; a splitter drag is remembered per pane rather than dragging all three.</summary>
    private readonly Dictionary<Pane, double> drawerWidths = [];

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
    /// <summary>The Overworld drawer's five tabs at their 82px minimum, six apart, in a bar padded
    /// ten each side — the drawer can never be narrower than its own header.</summary>
    private const double OwTabsWidth = 5 * 82 + 4 * 6 + 2 * 10;

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
        Pane.Overworld => OwTabsWidth,         // five tabs across, wider than a tile row
        _ => Map16PaletteView.ContentWidth(Map16PaletteView.DefaultZoom),
    };

    /// <summary>The widest the drawer goes for a sheet that fits its width: a Map16 row at its
    /// largest tile size (the CHR grid, half as wide at 1x, reaches 12x in the same room).</summary>
    private static readonly double DrawerCeiling = DrawerChrome + Map16PaletteView.ContentWidth(Map16PaletteView.MaxZoom);

    /// <summary>How far the splitter may go for a pane. The panes whose content FITS the drawer —
    /// the level's Tiles picker, the Map16 editor's CHR grid, the background's Map16 sheet, the GFX
    /// bin cards' stretched previews — have a range, because for them the width is the zoom: the
    /// ends are the smallest and largest worth having. Level and Background floor at 1x tiles; the
    /// Map16 pane floors at its control row, which is wider; Graphics at a 2x bin card. The
    /// Animations pane floors at its content and has no ceiling.</summary>
    private (double Min, double Max) DrawerRange(Pane pane) => pane switch
    {
        Pane.Level or Pane.Background => (DrawerChrome + Map16PaletteView.ContentWidth(Map16PaletteView.MinZoom), DrawerCeiling),
        Pane.Map16 or Pane.Graphics or Pane.Overworld => (NaturalDrawerWidth(pane), DrawerCeiling),
        _ => (NaturalDrawerWidth(pane), double.PositiveInfinity),
    };

    private double drawerWheel;   // fractional wheel not yet spent: a trackpad sends a notch in pieces

    /// <summary>Alt/Cmd+wheel anywhere over the drawer zooms its sheet — by resizing the drawer,
    /// since every sheet that zooms here fits its width (the splitter is the same control). One
    /// notch is half a Map16 tile scale of width, inside the pane's range and short of three
    /// quarters of the window so the canvas is never squeezed out. Tunnelling on the drawer so the
    /// chord is taken before the sheet's scroll viewer spends it; a pane without a range (the
    /// animations) has nothing that grows, so there the wheel is left alone.</summary>
    private void DrawerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!(e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
        if (double.IsPositiveInfinity(DrawerRange(drawerPane).Max)) return;
        e.Handled = true;
        drawerWheel += e.Delta.Y;
        int notches = (int)drawerWheel;
        drawerWheel -= notches;
        if (notches == 0) return;

        var col = split.ColumnDefinitions[0];
        double max = Math.Min(col.MaxWidth, split.Bounds.Width * 0.75);
        col.Width = new GridLength(Math.Clamp(col.Width.Value + Math.Sign(notches) * Map16PaletteView.ZoomStep * Map16Layout.Cols * 16,
                                              col.MinWidth, Math.Max(col.MinWidth, max)));
    }
}
