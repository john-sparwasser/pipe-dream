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

// One drawer bin as the session lists it: its name, the palette row and colour offset it
// loads under, its bypass word, its LM definition, the file in it and its depth.
using GfxBin = (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp);

/// <summary>
/// GFX mode: the pixel canvas, its tool bar and palette bar, the floating paste/lift layer,
/// and the drawer's bin cards that pick which file is open. The canvas is
/// <see cref="GfxCanvasView"/>; pixels are edited through the session's GfxEdit.
/// </summary>
public partial class MainWindow
{
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

    /// <summary>The drawer bin the header's Load fills, as its bypass word. -1 = none, and then
    /// Load only opens a file for editing.</summary>
    private int gfxSlot = -1;

    /// <summary>GFX mode: the pixel canvas, its tools and its palette bar.</summary>
    private void WireGfx()
    {
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
        gfxCanvas.ToolToggled += (_, _) => CycleGfxTool();
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
    }

    private string GfxReadout()
    {
        if (gfxSlot < 0 || gfxCanvas.Hover is not { } p || session.GfxPixels is not { } g) return "";
        return $"{g.Name ?? $"GFX{g.File:X3}"}  tile 0x{(p.Y / 8) * 16 + p.X / 8:X2}  px ({p.X & 7},{p.Y & 7})";
    }

    // ---- opening, loading and saving files ----

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

    // ---- the floating layer: pastes and lifted selections ----

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

    // ---- tools ----

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

    /// <summary>The tools in the order the bar shows them, left to right — which is the order
    /// F walks them. The enum's order is not this one, and cycling by enum value had F jumping
    /// around the bar. Every tool is here, so none is unreachable from the key.</summary>
    private static readonly GfxEdit.Tool[] GfxToolBarOrder =
    [
        GfxEdit.Tool.Select, GfxEdit.Tool.Eraser, GfxEdit.Tool.Fill, GfxEdit.Tool.Pencil,
        GfxEdit.Tool.Dropper, GfxEdit.Tool.Rect, GfxEdit.Tool.Ellipse, GfxEdit.Tool.Line,
    ];

    /// <summary>F: the next tool along the bar, wrapping.</summary>
    private void CycleGfxTool()
    {
        if (session.GfxPixels is not { } g) return;
        int at = Array.IndexOf(GfxToolBarOrder, g.Current);
        SetGfxTool(GfxToolBarOrder[(at + 1) % GfxToolBarOrder.Length]);
    }

    private void OnGfxTool(object? sender, RoutedEventArgs e)
        => SetGfxTool(ReferenceEquals(sender, gfxFill) ? GfxEdit.Tool.Fill
                    : ReferenceEquals(sender, gfxErase) ? GfxEdit.Tool.Eraser
                    : ReferenceEquals(sender, gfxDropper) ? GfxEdit.Tool.Dropper
                    : ReferenceEquals(sender, gfxSelect) ? GfxEdit.Tool.Select
                    : ReferenceEquals(sender, gfxLine) ? GfxEdit.Tool.Line
                    : GfxEdit.Tool.Pencil);

    // ---- the paint palette row ----

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

    private (int First, int Count) gfxRows = (-1, 0);

    /// <summary>Fill the row picker with what this bin allows and land on the nearest legal row to
    /// the one being painted with. The items ARE the row numbers, so a list starting at 8 does not
    /// make index 0 mean row 0.</summary>
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

    // ---- refreshing the mode ----

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

    /// <summary>The file the selection rectangle was made on: its coordinates mean nothing in
    /// another sheet, so switching files drops it. The CLIPBOARD survives — that is the point.</summary>
    private int gfxSelectionFile = -1;

    /// <summary>Everything the GFX mode shows for the current file: the sheet, the badge, the
    /// paint colours and the bin jump list.</summary>
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
        RefreshGfxHeader(g, none);
        SetGfxTool(g.Current);
        RefreshGfxPalRows(GfxPalValue);   // the rows this bin allows, before anything reads one
        // The depth box shows what the sheet is being READ as, override or not — so switching
        // files moves it back to whatever that file is, which is what dropping the override did.
        // A 3bpp-stored file reads as tile data, and tile data displays 4bpp: same entry.
        refillingGfxRows = true;
        gfxBpp.SelectedIndex = g.Bpp == 2 ? 1 : 0;
        refillingGfxRows = false;
        RefreshGfxSheet(none);
        RefreshGfxColors(g);

        RefreshGfxBins();          // the bins list IS the file picker now
    }

    /// <summary>The badge, the file name and the Save / Save As / Load buttons for the open file.</summary>
    private void RefreshGfxHeader(GfxEdit g, bool none)
    {
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
    }

    /// <summary>The gutter's paint colours: the row (or, for layer 3, the group) the file is drawn in.</summary>
    private void RefreshGfxColors(GfxEdit g)
    {
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
        // The overworld's own files close the list: not this level's VRAM, but graphics the
        // pixel editor paints all the same. Bypass words 0x70+, so Load never mistakes one for
        // a level slot (it only repoints words it finds in the level's bins).
        bins.AddRange(session.OverworldGfxBins);
        foreach (var bin in bins)
        {
            // Headed groups after the ten VRAM bins: the level's layer-3 window, the animation
            // slots, then the overworld. LG1-LG4 are real bins with a real bypass — LM's Layer 3
            // GFX/Tilemap Bypass — they just live behind their own enable bit (CONTRACT §12b).
            if (bin.Name is "LG1" or "AN1" || bin.BypWord == 0x70)
            {
                var sep = new TextBlock { Text = bin.Name == "LG1" ? "Layer 3" : bin.Name == "AN1" ? "Animation slots" : "Overworld",
                                          Margin = new Thickness(0, 8, 0, 0) };
                sep.Classes.Add("subject");
                gfxBins.Children.Add(sep);
                gfxBins.Children.Add(new Border { Height = 1, Background = (IBrush)this.FindResource("BorderBrush")!, Margin = new Thickness(0, 0, 0, 2) });
            }
            gfxBins.Children.Add(GfxBinCard(bin));
        }
    }

    /// <summary>One bin's card: a head band naming the bin, its kind and its file, the sheet
    /// preview under it, and the accent border when it is the bin the editor has open.</summary>
    private Border GfxBinCard(GfxBin bin)
    {
        int bypWord = bin.BypWord, palRow = bin.PalRow, file = bin.File, palOff = bin.ColorOffset;
        bool altFile = bypWord is >= 0x60 and <= 0x63;   // an ExAnimation source file, opened by its own id
        int openFile = altFile ? Convert.ToInt32(bin.Name[1..], 16) : file;   // "E60" → 0x60
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

        AddGfxBinPreview(block, bin);

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
            Cursor = UiCursors.Hand,
        };
        // An UNUSED bin (0x7F) is clickable too: selecting it is how it gets given something.
        card.PointerPressed += (_, _) =>
        {
            gfxSlot = bypWord;
            EditGfxFile(openFile, palRow, bin.Bpp, palOff);
        };
        return card;
    }

    /// <summary>The card's sheet preview, drawn in the row the bin actually shows in.</summary>
    private void AddGfxBinPreview(StackPanel block, GfxBin bin)
    {
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
    }
}
