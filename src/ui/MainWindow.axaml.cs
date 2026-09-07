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
    // The window is split into partial files by canvas mode — MainWindow.Level.cs,
    // .Map16.cs, .Gfx.cs, .Background.cs, .Animation.cs, .Palette.cs, .Drawer.cs,
    // .Overlays.cs and .Menus.cs. Each owns its controls, its Wire* method and its
    // handlers. This file keeps what spans them: the session, the chrome, startup,
    // the mode switch, zoom, the gutter readout, undo dispatch and tile animation.

    private readonly LevelBitmap bitmap = new();
    private readonly EditorSession session = new();
    private int levelNum = 0x105;

    private LevelEdit? edit;

    private LevelView canvas = null!;

    private Map16PaletteView palette = null!;
    private ComboBox levelBox = null!, bankBox = null!;
    private Slider zoomSlider = null!;
    private TextBlock readout = null!, zoomLabel = null!, selLabel = null!;
    private Border drawer = null!;

    private Grid split = null!;
    private ToggleButton modeLevel = null!, modeMap16 = null!, modeGfx = null!;
    private ToggleButton modeAnim = null!, modeBg = null!, modePalette = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Top-left, not the OS's pick: at 1500x900 the default placement can hang off screen.
        Position = new PixelPoint(0, 0);
        MiddlePan.Attach(this);

        // One method per surface, in the order the surfaces depend on each other: the header
        // and gutter first, then each canvas mode, then what spans them. Every control is a
        // field, so a mode's method is where to look for how its controls are wired.
        WireChrome();
        WireLevel();
        WireBackground();
        WireAnimation();
        WireOverworld();
        WireMap16();
        WireGfx();
        WireDrawer();
        WireOverlays();
        WireMenus();
        WireZoom();

        KeyDown += OnWindowKeyDown;

        // A rebuild swaps in a new scene and new layer editors, and the caches here (edit,
        // canvas.Edit, the bitmap's phase images) all point at the old ones until this runs.
        // Without it a GFX pixel commit — which rebuilds — left the canvas editing a discarded
        // object list: the delete happened, nothing on screen changed, and the edit was lost.
        session.SceneRebuilt += (_, _) => AdoptSession();
        session.Problem += (_, p) => ShowProblem(p);

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

    /// <summary>The header, gutter and mode switch: what every mode below hangs off.</summary>
    private void WireChrome()
    {
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
        modePalette = this.GetControl<ToggleButton>("ModePalette");

        for (int i = 0; i < EditorSession.LevelCount; i++) levelBox.Items.Add($"${i:X3}");
        levelBox.SelectionChanged += OnLevelChanged;
    }

    // ---- startup and the session ----

    private static bool IsProjectPath(string p) => p.EndsWith(".pdp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The startup sequence, one modal at a time so they never stack: first run's vanilla-ROM
    /// prompt, then the last project reopened (or the chooser when there is none), then the
    /// once-a-day update check. The check is fired and forgotten — nothing is shown unless there
    /// really is a newer release: a startup that says "you are up to date" every morning is
    /// noise, and one that reports a failed check is reporting something the user cannot act on.
    /// </summary>
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
        if (spawnsToggle.IsChecked == true) ShowSpawns(true);    // thumbnails are per level
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
        var (bgPx, bgW, bgH) = session.BgSheetPhases();
        palette.SetBgSheet(bgPx, bgW, bgH);

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

    private void OnLevelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (levelBox.SelectedIndex < 0) return;
        levelNum = levelBox.SelectedIndex;
        ShowLevel(levelNum);
    }

    private void UpdateTitle()
        => Title = (session.ProjectName is { } name
            ? $"pipe-dream — {name}{(session.HasUnsavedWork ? " *" : "")}"
            : session.RomFileName is { } file ? $"pipe-dream — {file} (no project)"
            : "pipe-dream") + (Program.DevMode ? "  [dev]" : "");

    private double composeMs;

    // ---- canvas modes ----

    // Radio behaviour without a group: exactly one canvas mode is active. Switching drops
    // every mode's in-flight drag, as the ImGui view toggle does.
    private void OnMode(object? sender, RoutedEventArgs e)
    {
        foreach (var b in new[] { modeLevel, modeMap16, modeGfx, modePalette, modeBg, modeAnim, modeOverworld })
            b.IsChecked = ReferenceEquals(b, sender);

        bool map16 = ReferenceEquals(sender, modeMap16);
        bool gfx = ReferenceEquals(sender, modeGfx);
        bool anim = ReferenceEquals(sender, modeAnim);
        bool bg = ReferenceEquals(sender, modeBg);
        bool ow = ReferenceEquals(sender, modeOverworld);
        // Palette mode keeps the level pane: the colours are edited against the level they
        // colour, and the canvas goes on doing whatever it was doing (it is not an edit mode).
        // Leaving the pixel editor with a stroke still open must not leave bytes behind that no
        // undo entry covers, so it is reverted rather than committed. A floating paste is the
        // opposite case — deliberate content not yet in any bytes — so it is dropped first.
        if (!gfx) { CommitGfxFloat(); session.GfxPixels?.AbortStroke(); }

        this.GetControl<DockPanel>("LevelPane").IsVisible = !map16 && !gfx && !anim && !bg && !ow;
        this.GetControl<DockPanel>("Map16Pane").IsVisible = map16;
        gfxScroll.IsVisible = gfx;
        animPane.IsVisible = anim;
        bgPane.IsVisible = bg;
        owPane.IsVisible = ow;
        edit?.Selection.Clear();
        map16Canvas.ClearSelection();
        ApplyZoomTarget();             // the gutter control follows the canvas it is driving
        ApplyDrawerPane(ow ? Pane.Overworld : bg ? Pane.Background : anim ? Pane.Animations
                      : gfx ? Pane.Graphics : map16 ? Pane.Map16 : Pane.Level);

        RefreshDrawer();
        if (ReferenceEquals(sender, modePalette)) ApplyPaletteScope();   // its Overworld tab swaps the canvas
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
        else if (ow) RefreshOverworld();
        if (!anim) { animPreview?.Stop(); animPreview = null; }   // no ticking behind another mode

        canvas.InvalidateVisual();
        // ...and again once layout has caught up: the repaint above can land while the canvas is
        // still marked invisible from the mode it is leaving, and that frame draws with whatever
        // the layout could tell it then.
        Dispatcher.UIThread.Post(canvas.InvalidateVisual);
    }

    /// <summary>Give a canvas the keyboard once layout has caught up. Focusing a control in the
    /// same breath as making it visible silently does nothing — it is not in the tree yet — and
    /// then the mode's own keys (F, [ ], the palette arrows) go nowhere until it is clicked.</summary>
    private static void FocusWhenLaidOut(Control c) => Dispatcher.UIThread.Post(() => c.Focus());

    // ---- global keys ----

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
        // File → Save. Handled here rather than as the menu item's HotKey so that BOTH Ctrl+S and
        // Cmd+S save on a Mac (a HotKey is one gesture); the item's InputGesture only draws the
        // caption. This is a bubbling handler, so a focused text box still gets first refusal.
        if (e.Key == Key.S && Hotkeys.CommandOnly(e.KeyModifiers))
        {
            OnSave(this, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Z && Hotkeys.Command(e.KeyModifiers))
        {
            UndoRedo(redo: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        // Ctrl+Y is the redo the Edit menu has always advertised; until now nothing answered it.
        else if (e.Key == Key.Y && Hotkeys.CommandOnly(e.KeyModifiers))
        {
            UndoRedo(redo: true);
            e.Handled = true;
        }
        // F cycles the GFX tools from anywhere in the mode. The canvas handles it too when it
        // has focus; this is for when a bar button or the header took the focus with a click,
        // which left F doing nothing until the sheet was clicked again.
        else if (modeGfx.IsChecked == true && e.Key == Key.F && e.KeyModifiers == KeyModifiers.None
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            CycleGfxTool();
            e.Handled = true;
        }
        // GFX selection clipboard. The clipboard lives in GfxEdit as colour indices, so a copy
        // in one bin pastes into whichever bin is open when Ctrl+V lands. A focused TextBox (a
        // bin's id field) keeps its own Ctrl+C/X/V.
        else if (modeGfx.IsChecked == true && Hotkeys.Command(e.KeyModifiers)
                 && e.Key is Key.C or Key.X or Key.V && session.GfxPixels is { } gp
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            GfxClipboardKey(e, gp);
            e.Handled = true;
        }
        // Delete on a Map16 selection resets the tiles to the base ROM's definitions. A focused
        // TextBox (the acts-like field) keeps its own Delete.
        else if (e.Key is Key.Delete or Key.Back && modeMap16.IsChecked == true
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (session.ResetMap16Tiles(map16Canvas.SelectedTiles())) RefreshMap16Props();
            e.Handled = true;
        }
        // Delete on an overworld lasso empties it: layer 1 tiles go, land goes back to its
        // region's fill — whichever layer the tab edits.
        else if (e.Key is Key.Delete or Key.Back && modeOverworld.IsChecked == true
                 && FocusManager?.GetFocusedElement() is not TextBox)
        {
            OwDeleteSelection();
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
            EscapeKey();
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+C / X / V over the GFX sheet. A paste FLOATS: the pixels ride above the sheet
    /// at the corner until dragged into place; only the drop writes bytes, as ONE undo entry.</summary>
    private void GfxClipboardKey(KeyEventArgs e, GfxEdit gp)
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
    }

    /// <summary>Esc, one step at a time: drop what is in flight in the current mode, else leave the
    /// mode, else leave an overlay mode, else drop the brush, else cycle layer 1 <-> sprites.</summary>
    private void EscapeKey()
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
    }

    // ---- zoom ----

    /// <summary>The zoom slider and the Alt/Cmd+wheel zoom on every desk. Last: it reads every canvas.</summary>
    private void WireZoom()
    {
        // The slider is in PERCENT; the canvas scales by a factor.
        zoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) ApplyZoom();
        };
        ApplyZoomTarget();                // one source of truth for the range, step and value

        // Alt/Cmd+wheel zooms every viewport about the cursor. Where the canvas's own plain wheel
        // is already a zoom (GFX), the desk around it zooms on the plain wheel too — one gesture
        // for the whole viewport. The level, Map16 and background sheets scroll on the plain
        // wheel, so there only the chord zooms. Tunnelling, so it sees the event before the
        // scroll viewer (or the canvas) spends it.
        foreach (var (svName, content, deskZooms) in new (string, Control, bool)[]
                 { ("BgScroll", bgView, false), ("OwScroll", owView, false), ("GfxSheetScroll", gfxCanvas, true),
                   ("CanvasScroll", canvas, false), ("Map16Scroll", map16Canvas, false) })
        {
            var sv = this.GetControl<ScrollViewer>(svName);
            sv.AddHandler(PointerWheelChangedEvent, (_, e) => DeskWheel(sv, content, deskZooms, e), RoutingStrategies.Tunnel);
        }
    }

    // The gutter slider drives whichever canvas is showing, but one percent cannot suit all
    // three, so each mode keeps its own value and its own range. The level opens at 1:1 — the
    // whole point of the level view is how much of the level you can see at once; the Map16
    // sheet opens at 3x, since a 16-tile-wide column at 1:1 is a sliver; and GFX at 8 screen
    // pixels per GFX pixel, which is what the ImGui editor opened at.
    private double levelZoomPct = 100, gfxZoomPct = 800, map16ZoomPct = 300, bgZoomPct = 200, owZoomPct = 200;
    private bool owZoomFitted;

    /// <summary>The largest half-step zoom at which the 512 px-wide overworld sits in its viewport
    /// with no sideways scroll, or null before the pane has a width to measure against. The
    /// level pane's width stands in when the map has not been laid out yet: it fills the same slot.</summary>
    private double? FitOwZoom()
    {
        double width = Math.Max(this.GetControl<ScrollViewer>("OwScroll").Bounds.Width, this.GetControl<DockPanel>("LevelPane").Bounds.Width);
        double avail = width - 2 * 16 - 4;                                   // the view's margin, and a little slack for the scrollbar
        if (avail <= 0) return null;
        return Math.Clamp(Math.Floor(avail / (Overworld.Cols * 16) * 2) * 50, 100, 800);
    }

    /// <summary>Point the zoom control at a mode: its range, its step, and the value it was left
    /// at. Call it AFTER the mode flags flip — this and <see cref="ApplyZoom"/> read them.</summary>
    private void ApplyZoomTarget()
    {
        bool gfx = modeGfx?.IsChecked == true;
        // Read the wanted value first: narrowing the range coerces Value, which lands in the
        // remembered field on the way through.
        bool bg = modeBg?.IsChecked == true, ow = owPane?.IsVisible == true;   // the Palette drawer's Overworld tab shows it too
        // The map opens as wide as the viewport lets it without a sideways scroll: 512 px of map
        // at the largest half-step that fits. Once, on first showing; after that the slider rules.
        if (ow && !owZoomFitted && FitOwZoom() is { } fit) { owZoomPct = fit; owZoomFitted = true; }
        double want = gfx ? gfxZoomPct : modeMap16?.IsChecked == true ? map16ZoomPct
                    : bg ? bgZoomPct : ow ? owZoomPct : levelZoomPct;
        // The level steps in 10%: a fractional zoom is drawn filtered rather than nearest, so it
        // stays clean (LevelView.Unsampled). The GFX sheet steps in whole multiples instead —
        // pixel editing wants the pixel you click to be exactly the pixel you paint. The
        // background steps in halves, the same notch its wheel zoom takes.
        (zoomSlider.Minimum, zoomSlider.Maximum, zoomSlider.TickFrequency) =
            gfx ? (400.0, 1600.0, 100.0)      // whole screen pixels per GFX pixel, 4x to 16x
            : bg || ow ? (100.0, 800.0, 50.0)
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
        else if (modeBg?.IsChecked == true)
        {
            bgZoomPct = pct;
            bgView.Zoom = zoom;
            bgView.InvalidateMeasure();
            bgView.InvalidateVisual();
        }
        else if (owPane?.IsVisible == true)
        {
            owZoomPct = pct;
            owView.Zoom = zoom;
            owView.InvalidateMeasure();
            owView.InvalidateVisual();
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

    /// <summary>One tick of zoom, in the slider's own units — the slider IS the zoom state, so
    /// stepping it keeps the label and whichever canvas it drives in step for free.</summary>
    private void StepZoom(int dir)
    {
        zoomSlider.Value = Math.Clamp(zoomSlider.Value + dir * zoomSlider.TickFrequency,
                                      zoomSlider.Minimum, zoomSlider.Maximum);
    }

    private double deskWheel;   // fractional wheel not yet spent: a trackpad sends a notch in pieces

    /// <summary>
    /// The wheel over the DESK — the checkerboard around a zooming canvas — zooms that canvas,
    /// one slider step a notch, about the point under the cursor. Over the canvas itself the
    /// canvas does its own anchoring, and a scrollbar is not the desk. Zooming through the
    /// slider keeps it the one owner of the value.
    /// </summary>
    private void DeskWheel(ScrollViewer sv, Control content, bool deskZooms, PointerWheelEventArgs e)
    {
        if (e.Source is not Visual src || src.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
        bool overContent = ReferenceEquals(src, content) || content.IsVisualAncestorOf(src);
        if (!ZoomChord(e.KeyModifiers) && (overContent || !deskZooms)) return;
        deskWheel += e.Delta.Y;
        int notches = (int)deskWheel;
        deskWheel -= notches;
        e.Handled = true;
        if (notches == 0) return;

        // The point is taken relative to the canvas, off it or not: the canvas scales about its
        // own origin, so that point lands at p*f after, and the offset moves by the difference.
        // Layout first — an offset set against the old extent is clamped to it.
        var p = e.GetPosition(content);
        double before = zoomSlider.Value;
        StepZoom(Math.Sign(notches));
        double f = zoomSlider.Value / before;
        if (f == 1) return;
        sv.UpdateLayout();
        sv.Offset += new Vector(p.X * (f - 1), p.Y * (f - 1));
    }

    /// <summary>The zoom chord: Alt or Cmd (Meta). Not Ctrl — Ctrl+wheel belongs to the canvas
    /// underneath (the level's reorder), so it has to pass through untouched.</summary>
    private static bool ZoomChord(KeyModifiers m)
        => m.HasFlag(KeyModifiers.Alt) || m.HasFlag(KeyModifiers.Meta);

    // ---- the gutter readout ----

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
                        : owPane.IsVisible ? OwReadout()
                        : modeMap16.IsChecked == true ? Map16Readout()
                        : LevelReadout();

    // ---- undo, per editor ----

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
        if (modePalette.IsChecked == true)
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
            // The properties panel shows the selected tile's words and behaviour, which are
            // exactly what an undo changes.
            if (redo ? map16?.Redo() == true : map16?.Undo() == true) { RefreshMap16Sheet(); RefreshMap16Props(); }
        }
        // The background layers keep a history each, so undo follows the layer on screen
        // for the same reason it follows the canvas mode: rewinding the level's objects
        // while looking at a tilemap would be the wrong thing every time.
        else if (modeBg.IsChecked == true && BgLayerEdit is { } bgMap)
        {
            if (redo ? bgMap.Redo() : bgMap.Undo()) { RefreshBg(); UpdateTitle(); }
        }
        else if (modeOverworld.IsChecked == true && OwEditNow is { } owMap)
        {
            DropOwFloat();                  // a floating block lands first, so this undo takes it back
            if (redo ? owMap.Redo() : owMap.Undo()) { RefreshOverworld(); UpdateTitle(); }
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

    // ---- tile animation: the four phases ----

    /// <summary>
    /// Cycle the four animation phases, as the game does. The phases are already composed — this
    /// only changes which one the bitmap shows, so it costs one image swap rather than a
    /// recompose, which is why it can run at a game-ish rate at all.
    /// </summary>
    private void OnToggleAnimate(object? sender, RoutedEventArgs e) => SetAnimating(animate is null);

    /// <summary>Run or stop the phase cycle, and keep the menu's checkbox saying which it is.
    /// Stopping parks on phase 0, the state the level composes to, and the overworld on the
    /// frame Lunar Magic draws.</summary>
    private void SetAnimating(bool on)
    {
        if (on == (animate is not null)) return;
        if (!on) { animate!.Stop(); animate = null; SetPhase(0); AnimateOverworld(reset: true); }
        else
        {
            animate = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            // A palette stroke only keeps the phase ON SCREEN in step with the colour being
            // dragged (the other three are recomposed when the stroke ends), so stepping mid
            // drag would flick between the new colour and the old one.
            animate.Tick += (_, _) => { if (!session.InPaletteStroke) SetPhase((canvas.Phase + 1) & 3); AnimateOverworld(); };
            animate.Start();
        }
        animateItem.Icon = on ? new TextBlock { Text = "✓" } : null;
    }

    private DispatcherTimer? animate;

    /// <summary>Step the overworld's animated tiles by the eight game frames a tick stands for
    /// (140 ms is eight frames near enough), while the map is the canvas showing — the sparkles,
    /// the water and the lava run as the game runs them. Stopping parks them on Lunar Magic's
    /// frame whether the map is showing or not, so it never comes back mid-cycle.</summary>
    private void AnimateOverworld(bool reset = false)
    {
        if (session.Overworld is not { } ow || (!reset && owPane?.IsVisible != true)) return;
        ow.Animate(reset ? Overworld.LunarMagicCounter : ow.AnimationCounter + 8);
        owView.Invalidate();
        owSheet.Invalidate();
    }

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
}
