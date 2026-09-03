using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// GFX pixel editing.
///
/// The load-bearing rule is copy-on-write: a stock ROM file forks on first touch into a project
/// copy stored under the SAME id, so every consumer — the level's tiles, the sprites, the Map16
/// sheet — sees the edit, and the existing import plumbing carries persistence and the build for
/// free. Allocating a new id instead (what an *import* does) would leave the level still drawing
/// the untouched original.
///
/// The controls also differ from the level canvas on purpose: left paints here. This mode uses
/// ordinary paint-program bindings — selecting is a tool, not the default gesture — and the
/// ImGui version does the same.
/// </summary>
public class GfxModeTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduigfx-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    private static EditorSession? Open()
    {
        if (!HaveRom) return null;
        var s = new EditorSession();
        return s.OpenRom(Vanilla) ? s : null;
    }

    // ---- the service ----

    [Fact]
    public void painting_a_stock_file_forks_it_under_the_same_id()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);
        Assert.Equal("stock", g.Status);

        // Paint a pixel a colour it is not already.
        int at = g.ColorAt(0, 0) ?? 0;
        g.Color = at == 3 ? 1 : 3;
        Assert.True(g.Paint(0, 0, out bool forked));
        Assert.True(forked, "the first touch of a stock file must fork it");
        Assert.Equal(g.Color, g.ColorAt(0, 0));
        Assert.Equal("imported", g.Status);      // ...under the same id, so consumers follow

        g.EndStroke();
        Assert.Equal(1, g.UndoDepth);
    }

    [Fact]
    public void undo_restores_a_whole_stroke_not_its_second_to_last_state()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);
        var before = Enumerable.Range(0, 8).Select(x => g.ColorAt(x, 0)!.Value).ToArray();

        // A row of 8 pixels shares plane bytes, so the stroke rewrites the same offsets over and
        // over. Restoring those in paint order lands on the second-to-last value and leaves most
        // of the stroke painted — the reason ApplyStroke walks backward.
        g.Color = before[0] == 5 ? 2 : 5;
        for (int x = 0; x < 8; x++) g.Paint(x, 0, out _);
        g.EndStroke();
        Assert.All(Enumerable.Range(0, 8), x => Assert.Equal(g.Color, g.ColorAt(x, 0)));

        Assert.True(g.Undo());
        var after = Enumerable.Range(0, 8).Select(x => g.ColorAt(x, 0)!.Value).ToArray();
        Assert.Equal(before, after);

        Assert.True(g.Redo());
        Assert.All(Enumerable.Range(0, 8), x => Assert.Equal(g.Color, g.ColorAt(x, 0)));
    }

    /// <summary>An uncommitted stroke must not survive a file switch as bytes with no undo entry
    /// covering them — it is reverted instead.</summary>
    [Fact]
    public void switching_file_mid_stroke_reverts_it()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);
        int before = g.ColorAt(2, 2)!.Value;
        g.Color = before == 4 ? 1 : 4;
        Assert.True(g.Paint(2, 2, out _));
        Assert.Equal(g.Color, g.ColorAt(2, 2));

        g.Open(0x15);                            // abort, not commit
        g.Open(0x14);
        Assert.Equal(before, g.ColorAt(2, 2));
        Assert.Equal(0, g.UndoDepth);
    }

    /// <summary>The eraser writes colour 0 — transparent in this format — and undoes as one stroke
    /// like any other. The eyedropper writes nothing at all: if it fell through to the pencil it
    /// would paint the pixel it was asked to read.</summary>
    [Fact]
    public void the_eraser_clears_a_pixel_and_the_dropper_writes_nothing()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);

        g.Color = 3;                                  // something to erase, whatever was there
        Assert.True(g.Paint(1, 1, out _));
        g.EndStroke();
        Assert.Equal(3, g.ColorAt(1, 1));

        g.Current = GfxEdit.Tool.Eraser;
        Assert.True(g.Paint(1, 1, out _));
        Assert.Equal(0, g.ColorAt(1, 1));
        g.EndStroke();
        Assert.True(g.Undo());
        Assert.Equal(3, g.ColorAt(1, 1));             // the erase was one undo entry

        int history = g.UndoDepth;
        g.Current = GfxEdit.Tool.Dropper;
        Assert.False(g.Paint(1, 1, out _));
        g.EndStroke();                                // an empty stroke closes to nothing
        Assert.Equal(3, g.ColorAt(1, 1));
        Assert.Equal(history, g.UndoDepth);
    }


    /// <summary>
    /// Depth is per FILE, not per ROM. Layer-3 graphics (0x28-0x2B) and 0x2F are 2bpp — the game
    /// streams them out with a routine that is not the tile uploader — so decoding them at the
    /// ROM's depth reads 24 bytes out of 16-byte tiles and draws noise. The list of exceptions
    /// comes from those routines (Gfx.IsTilePlanar3Bpp), not from sniffing file sizes: 0x800
    /// bytes is 128 2bpp tiles, 85 3bpp tiles or 64 4bpp tiles, and nothing in the file says
    /// which.
    /// </summary>
    [Fact]
    public void layer3_files_are_2bpp_whatever_the_rom_stores_everything_else_at()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;

        g.Open(0x14);                                  // ordinary FG tiles: the ROM's depth
        Assert.Equal(Gfx.RomBpp(s.Rom!), g.Bpp);
        Assert.Equal(7, g.MaxColor);                   // vanilla is 3bpp

        foreach (int file in new[] { 0x28, 0x29, 0x2A, 0x2B, 0x2F })
        {
            g.Open(file);
            Assert.Equal(2, g.Bpp);
            Assert.Equal(3, g.MaxColor);
            Assert.Equal(g.Bytes!.Length / 16, g.Layout.Tiles);   // 16 bytes a tile, not 24
        }

        // ...and the paint path follows it: colour clamps to the four the file can hold, and a
        // write lands in the planes that exist.
        g.Open(0x28);
        g.Color = 7;
        Assert.Equal(3, g.Color);
        Assert.True(g.Paint(0, 0, out _) || g.ColorAt(0, 0) == 3);
        g.EndStroke();
        Assert.Equal(3, g.ColorAt(0, 0));
    }

    /// <summary>The selection clipboard is colour indices, not plane bytes, so a copy taken in
    /// one file pastes into another — that is what makes cross-bin copy work. Cut and paste are
    /// each ONE undo entry, and a move commits as one entry too.</summary>
    [Fact]
    public void copy_cut_and_paste_cross_files_and_undo_as_single_strokes()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);

        // A known 2x2 block to carry around.
        g.Color = 1;
        foreach (var (x, y, c) in new[] { (0, 0, 1), (1, 0, 2), (0, 1, 3), (1, 1, 4) })
        { g.Color = c; g.Paint(x, y, out _); }
        g.EndStroke();

        g.Copy(0, 0, 2, 2);
        Assert.Equal((2, 2), (g.Clipboard!.Value.W, g.Clipboard!.Value.H));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, g.Clipboard!.Value.Px);

        // Cut clears the source to transparent, in one undo entry.
        int depth = g.UndoDepth;
        g.Cut(0, 0, 2, 2);
        Assert.Equal(0, g.ColorAt(0, 0));
        Assert.Equal(0, g.ColorAt(1, 1));
        Assert.Equal(depth + 1, g.UndoDepth);

        // Paste into ANOTHER file — the clipboard survived the switch.
        g.Open(0x15);
        Assert.True(g.Paste(8, 8, selBefore: (2, 2, 2, 2)));
        Assert.Equal(1, g.ColorAt(8, 8));
        Assert.Equal(4, g.ColorAt(9, 9));
        Assert.True(g.Undo());
        Assert.True(g.SelectionHint.Has);                      // the marquee walks back too
        Assert.Equal((2, 2, 2, 2), g.SelectionHint.Rect!.Value);
        Assert.True(g.Redo());
        Assert.True(g.SelectionHint.Has);                      // ...and forward to the paste
        Assert.Equal((8, 8, 2, 2), g.SelectionHint.Rect!.Value);
        Assert.True(g.Undo());
        Assert.True(g.Undo());                        // ...and back in the first file, the cut
        Assert.False(g.SelectionHint.Has);            // another file's rect must not move it
        g.Open(0x14);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, new[]
            { (byte)g.ColorAt(0, 0)!, (byte)g.ColorAt(1, 0)!, (byte)g.ColorAt(0, 1)!, (byte)g.ColorAt(1, 1)! });

        // A move: lift the block off the sheet, wander, drop. The lift leaves a hole and the
        // drop closes the SAME stroke, so it is one undo entry — and nowhere the block passed
        // over on the way was written, only where it landed.
        depth = g.UndoDepth;
        int was = g.ColorAt(4, 2)!.Value;             // whatever the stock sheet has there
        int passed = g.ColorAt(2, 0)!.Value;          // ...and where it merely travelled through
        var lifted = g.Lift(0, 0, 2, 2);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, lifted);
        Assert.Equal(depth, g.UndoDepth);             // the hole is still an open stroke
        Assert.True(g.Paste(4, 2, selBefore: (0, 0, 2, 2), px: (2, 2, lifted)));
        Assert.Equal(0, g.ColorAt(0, 0));             // the source is empty...
        Assert.Equal(0, g.ColorAt(1, 0));
        Assert.Equal(passed, g.ColorAt(2, 0));        // ...and the trip left no marks
        Assert.Equal(1, g.ColorAt(4, 2));
        Assert.Equal(4, g.ColorAt(5, 3));
        Assert.Equal(depth + 1, g.UndoDepth);
        Assert.True(g.Undo());
        Assert.Equal(1, g.ColorAt(0, 0));             // one undo restores the whole move
        Assert.Equal(was, g.ColorAt(4, 2));
        Assert.True(g.SelectionHint.Has);             // the marquee walks home with the pixels
        Assert.Equal((0, 0, 2, 2), g.SelectionHint.Rect!.Value);
    }

    [Fact]
    public void fill_floods_within_one_tile_only()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = s.GfxPixels!;
        g.Open(0x14);
        g.Current = GfxEdit.Tool.Fill;
        g.Color = 7;

        // Whatever tile 0 looked like, the flood cannot escape it: tile 1 starts at x=8.
        var neighbour = Enumerable.Range(0, 8).Select(y => g.ColorAt(8, y)!.Value).ToArray();
        g.Paint(0, 0, out _);
        g.EndStroke();
        Assert.Equal(neighbour, Enumerable.Range(0, 8).Select(y => g.ColorAt(8, y)!.Value).ToArray());
    }

    /// <summary>A pixel edit changes what every level draws with, so a committed stroke has to
    /// reach the composed level, not just the sheet.</summary>
    [Fact]
    public void a_committed_stroke_recomposes_the_level()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        // FG1 is the tileset's main foreground file, so its pixels are all over the level.
        int fg1 = s.GfxBins.First(b => b.Name == "FG1").File;
        var g = s.GfxPixels!;
        g.Open(fg1);

        var before = s.Scene!.TileCaches[0].Select(t => (uint[]?)t?.Clone()).ToArray();
        g.Color = (g.ColorAt(0, 0) ?? 0) == 6 ? 2 : 6;
        for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) g.Paint(x, y, out _);
        g.EndStroke();

        var after = s.Scene!.TileCaches[0];
        int changed = 0;
        for (int t = 0; t < after.Length && t < before.Length; t++)
            if (before[t] is { } b && after[t] is { } a && !b.SequenceEqual(a)) changed++;
        log.WriteLine($"GFX{fg1:X3} tile 0 repainted — {changed} Map16 tiles changed");
        Assert.True(changed > 0, "a committed GFX stroke never reached the level's tiles");
    }

    /// <summary>Import allocates a FRESH id, unlike an edit's copy-on-write fork — an import
    /// that reused an existing id would shadow a real ExGFX file other levels use. Pointing a bin
    /// at it is a separate step, so the two are asserted separately.</summary>
    [Fact]
    public void importing_allocates_a_new_id_and_a_bin_can_then_take_it()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);

        // 0x40 tiles of 4bpp is a whole number of planar tiles, which is what the detector wants.
        string bin = Path.Combine(dir, "test.bin");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(bin, new byte[0x40 * 32]);

        var (id, note) = s.ImportGfx(bin);
        log.WriteLine(note);
        Assert.Contains("imported", note);
        Assert.True(id >= 0x100, $"expected a fresh ExGFX id, got {id:X3}");
        Assert.Equal("test", s.GfxName(id));

        int bypWord = s.GfxBins.First(b => b.Name == "FG3").BypWord;
        s.SetGfxSlot(bypWord, id);
        Assert.Equal(id, s.GfxBins.First(b => b.Name == "FG3").File);
        Assert.Equal("custom", s.GfxBinNote(bypWord, id, def: 0));
    }

    /// <summary>A file named by the ExGFX### convention carries its own id: the number is used
    /// when free, and a second import of the same number falls back to auto-assignment.</summary>
    [Fact]
    public void import_named_by_the_exgfx_convention_keeps_its_number()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);

        Directory.CreateDirectory(dir);
        string bin = Path.Combine(dir, "ExGFX140.bin");
        File.WriteAllBytes(bin, new byte[0x40 * 32]);

        var (id, note) = s.ImportGfx(bin);
        log.WriteLine(note);
        Assert.Equal(0x140, id);
        Assert.Equal("ExGFX140", s.GfxName(id));

        var (again, note2) = s.ImportGfx(bin);       // 0x140 is taken now
        log.WriteLine(note2);
        Assert.True(again >= 0x100 && again != 0x140, $"expected a fallback id, got {again:X3}");
    }

    [Fact]
    public void a_rejected_import_says_why_and_changes_nothing()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        Directory.CreateDirectory(dir);
        string bin = Path.Combine(dir, "odd.bin");
        File.WriteAllBytes(bin, new byte[100]);          // not a whole number of planar tiles

        var bin0 = s.GfxBins.First(b => b.Name == "FG3");
        var (id, note) = s.ImportGfx(bin);
        log.WriteLine(note);
        Assert.Contains("rejected", note);
        Assert.True(id < 0);
        Assert.Equal(bin0.File, s.GfxBins.First(b => b.Name == "FG3").File);
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void gfx_mode_takes_the_canvas_and_the_drawer()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // The canvas IS the editor: the GFX sheet takes the region and the drawer follows it to
        // the paint colours rather than opening a second panel.
        Assert.True(w.GetControl<DockPanel>("GfxScroll").IsVisible);
        Assert.False(w.GetControl<DockPanel>("LevelPane").IsVisible);
        Assert.True(w.GetControl<DockPanel>("GfxToolPanel").IsVisible);
        Assert.False(w.GetControl<DockPanel>("TilesPanel").IsVisible);

        var sheet = w.GetControl<GfxCanvasView>("GfxCanvas");
        Assert.True(sheet.Tiles > 0, "the sheet never loaded a file");
    }

    /// <summary>Left-drag paints — the opposite of the level canvas, and deliberately so.</summary>
    [AvaloniaFact]
    public void left_drag_paints_pixels()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;
        g.Color = 5;                             // within a 3bpp ROM's range

        Point At(int x, int y) => view.TranslatePoint(
            new Point(x * view.Zoom + view.Zoom / 2, y * view.Zoom + view.Zoom / 2), w)!.Value;

        w.MouseDown(At(1, 1), MouseButton.Left);
        w.MouseMove(At(4, 1));
        w.MouseUp(At(4, 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // Painted with the armed colour, and interpolated across the drag rather than only
        // landing on the points a move event happened to sample.
        log.WriteLine($"drag painted colour {g.ColorAt(1, 1)}");
        Assert.All(new[] { 1, 2, 3, 4 }, x => Assert.Equal(5, g.ColorAt(x, 1)));
        Assert.Equal(1, g.UndoDepth);            // one drag, one undo entry
    }

    [AvaloniaFact]
    public void right_click_eyedrops_the_colour_under_the_cursor()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;
        // Find a pixel whose colour is not the armed one, so the pick is observable. Never a
        // TRANSPARENT one: colour 0 arms the eraser instead, which the next test covers.
        int? target = null;
        for (int x = 0; x < 32 && target is null; x++)
            for (int y = 0; y < 8; y++)
                if (g.ColorAt(x, y) is { } c && c != g.Color && c != 0) { target = x * 100 + y; break; }
        if (target is null) { log.WriteLine("SKIP: sheet is a single colour"); return; }
        int tx = target.Value / 100, ty = target.Value % 100;
        int want = g.ColorAt(tx, ty)!.Value;

        var at = view.TranslatePoint(new Point(tx * view.Zoom + 1, ty * view.Zoom + 1), w)!.Value;
        w.MouseDown(at, MouseButton.Right);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(want, g.Color);
        Assert.Equal(want, w.GetControl<PaletteGridView>("GfxColors").Selected);
    }

    /// <summary>Colour 0 is transparent, which is the ERASER's job and not a paint colour: an
    /// eyedrop that lands on it arms the eraser and leaves the paint colour alone, the swatch row
    /// offers the whole 16-colour palette row, and nothing can arm 0 as a colour.</summary>
    [AvaloniaFact]
    public void eyedropping_a_transparent_pixel_arms_the_eraser()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var swatches = w.GetControl<PaletteGridView>("GfxColors");
        var g = SessionOf(w).GfxPixels!;

        // The back half of the row is offered too — greyed where the ROM's depth cannot reach it.
        Assert.Equal(16, swatches.Cols);
        Assert.True(swatches.IsDisabled!(g.MaxColor + 1), "a colour past the ROM's depth was selectable");
        Assert.False(swatches.IsDisabled!(g.MaxColor), "the ROM's own top colour was disabled");

        g.Color = 0;                                  // refused: 0 is the eraser, not a colour
        Assert.NotEqual(0, g.Color);

        int? target = null;
        for (int x = 0; x < 128 && target is null; x++)
            for (int y = 0; y < 8; y++)
                if (g.ColorAt(x, y) == 0) { target = x * 100 + y; break; }
        if (target is null) { log.WriteLine("SKIP: no transparent pixel in this sheet"); return; }
        int before = g.Color;

        var at = view.TranslatePoint(new Point(target.Value / 100 * view.Zoom + 1,
                                              target.Value % 100 * view.Zoom + 1), w)!.Value;
        w.MouseDown(at, MouseButton.Right);
        w.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GfxEdit.Tool.Eraser, g.Current);
        Assert.Equal(before, g.Color);                 // the armed colour survived the pick
        Assert.Equal(0, swatches.Selected);            // ...and the ring moved to the eraser slot
    }

    /// <summary>Paste does not touch the file: the pixels float at the top-left corner, drag into
    /// place, and only the DROP writes bytes — one undo entry at the final position. Ctrl+Z on a
    /// still-floating paste just takes the float down. This is what makes undo sane: there is no
    /// intermediate paste-then-move history to walk back through.</summary>
    [AvaloniaFact]
    public void paste_floats_at_the_corner_and_drops_as_one_undo_entry()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;

        // Known pixels to carry, away from where the float will appear.
        foreach (var (x, y, c) in new[] { (16, 0, 1), (17, 0, 2), (16, 1, 3), (17, 1, 4) })
        { g.Color = c; g.Paint(x, y, out _); }
        g.EndStroke();
        g.Copy(16, 0, 2, 2);
        int depth = g.UndoDepth;
        int underFloat = g.ColorAt(0, 0)!.Value;
        int atDrop = g.ColorAt(10, 4)!.Value;

        w.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((0, 0, 2, 2), view.Float);
        Assert.Equal(depth, g.UndoDepth);             // nothing committed yet...
        Assert.Equal(underFloat, g.ColorAt(0, 0));    // ...and the sheet under it is untouched

        Point At(int x, int y) => view.TranslatePoint(
            new Point(x * view.Zoom + view.Zoom / 2, y * view.Zoom + view.Zoom / 2), w)!.Value;

        // Drag the float into place: still nothing in the bytes.
        w.MouseDown(At(0, 0), MouseButton.Left);
        w.MouseMove(At(10, 4));
        w.MouseUp(At(10, 4), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((10, 4, 2, 2), view.Float);
        Assert.Equal(depth, g.UndoDepth);

        // A click outside drops it where it rests — ONE undo entry.
        w.MouseDown(At(30, 3), MouseButton.Left);
        w.MouseUp(At(30, 3), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Float);
        Assert.Equal(depth + 1, g.UndoDepth);
        Assert.Equal(1, g.ColorAt(10, 4));
        Assert.Equal(4, g.ColorAt(11, 5));

        // Ctrl+Z takes the whole paste back in one step.
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(atDrop, g.ColorAt(10, 4));
        Assert.Equal(depth, g.UndoDepth);

        // A fresh paste that never lands: Esc discards it and the bytes never knew.
        w.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.Float);
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Float);
        Assert.Equal(depth, g.UndoDepth);
        Assert.Equal(underFloat, g.ColorAt(0, 0));
    }

    /// Choosing a GFX file and editing its pixels are ONE screen, reached from the GFX header
    /// mode. There used to be two: a "GFX" drawer tab listing the bins, and a separate GFX canvas
    /// mode that could only be entered from an Edit button inside it — so picking a file meant
    /// leaving the editor, and the tab was a dead end in every other mode.
    /// </summary>
    [AvaloniaFact]
    public void the_bins_live_with_the_editor_and_there_is_no_separate_gfx_tab()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        // The drawer's tabs are the LEVEL's views; GFX and Palette are canvas modes, not tabs.
        var tabs = w.GetControl<TabStrip>("PaletteTabs");
        Assert.Equal(3, tabs.ItemCount);
        Assert.DoesNotContain("GFX", tabs.Items.OfType<TabStripItem>().Select(t => $"{t.Content}"));

        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // GFX mode's drawer carries both halves: the paint colours AND every bin, each with its
        // preview, so the file you want is visible rather than a hex id to recall.
        Assert.True(w.GetControl<DockPanel>("GfxToolPanel").IsVisible);
        Assert.True(w.GetControl<PaletteGridView>("GfxColors").IsVisible);
        var bins = w.GetControl<StackPanel>("GfxBins");
        // ...plus two headings with their rules ("Layer 3", "Animation slots") and the four
        // ExAnimation source-file cards.
        Assert.Equal(SessionOf(w).GfxBins.Length + 8, bins.Children.Count);
        Assert.Equal(["Layer 3", "Animation slots"],
                     bins.Children.OfType<TextBlock>().Select(t => t.Text));
    }

    /// <summary>
    /// The layer-3 window is four ordinary bins, below SP4 and above the animation slots. They
    /// are the ones LM's Layer 3 GFX/Tilemap Bypass sets, and they ride in the same per-level
    /// record as the rest — behind their own enable bit (CONTRACT §12b).
    /// </summary>
    [AvaloniaFact]
    public void the_layer_3_bins_sit_below_sp4_and_default_to_gfx_28_2b()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        var names = s.GfxBins.Select(b => b.Name).ToArray();

        Assert.Equal(["SP4", "LG1", "LG2", "LG3", "LG4", "AN1"], names[9..15]);
        Assert.Equal([0x28, 0x29, 0x2A, 0x2B],
                     s.GfxBins.Where(b => b.Name.StartsWith("LG")).Select(b => b.File));
        // Record words 15..12, so LG1 is the LAST word, and the colours are the layer-3 block
        // at CGRAM 08 rather than a palette row of their own.
        Assert.Equal([15, 14, 13, 12], s.GfxBins.Where(b => b.Name.StartsWith("LG")).Select(b => b.BypWord));
        Assert.All(s.GfxBins.Where(b => b.Name.StartsWith("LG")), b => Assert.Equal(8, b.ColorOffset));
        // The bin carries the depth because the FILE cannot: an ExGFX in an LG slot is still
        // layer-3 data. 0 on every other bin means "read it at its own depth".
        Assert.All(s.GfxBins.Where(b => b.Name.StartsWith("LG")), b => Assert.Equal(2, b.Bpp));
        Assert.All(s.GfxBins.Where(b => !b.Name.StartsWith("LG")), b => Assert.Equal(0, b.Bpp));
    }

    /// <summary>A bin preview reads at the BIN's depth, so an imported sheet in an LG slot shows
    /// its 128 tiles rather than the 64 a 4bpp read would find.</summary>
    [AvaloniaFact]
    public void an_imported_layer_3_file_previews_2bpp_in_its_bin()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        s.Rom!.ImportedGfx[0x100] = new byte[0x800];
        s.SetGfxSlot(15, 0x100);                          // LG1

        var lg1 = s.GfxBins.Single(b => b.Name == "LG1");
        Assert.Equal(0x100, lg1.File);
        var (_, _, asBin) = s.GfxFileSheet(lg1.File, lg1.PalRow, lg1.ColorOffset, lg1.Bpp);
        var (_, _, asFile) = s.GfxFileSheet(lg1.File, lg1.PalRow);
        Assert.Equal(8 * 8, asBin);                       // 128 tiles, 16 per row
        // Read at the FILE's depth it is 85 tiles on a 3bpp base or 64 on a 4bpp one — either
        // way not 128, which is the whole reason the bin has to carry the depth.
        Assert.NotEqual(asBin, asFile);
    }

    /// <summary>Repointing one turns the bypass on for that level alone, leaves the other three
    /// on their vanilla files, and does NOT switch on the unrelated FG/BG/SP bypass.</summary>
    [AvaloniaFact]
    public void setting_a_layer_3_bin_enables_only_the_layer_3_bypass()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        s.ShowLevel(0x105);
        Assert.Null(s.Rom!.LmLayer3Gfx(0x105));

        s.SetGfxSlot(15, 0x30);                                   // LG1 → GFX 30
        Assert.Equal([0x30, 0x7F, 0x7F, 0x7F], s.Rom.LmLayer3Gfx(0x105)!);
        Assert.Equal([0x30, 0x29, 0x2A, 0x2B], Layer3.GfxFiles(s.Rom, 0x105));
        Assert.Equal(0x30, s.GfxBins.Single(b => b.Name == "LG1").File);

        Assert.Null(s.Rom.LmGfxBypass(0x105));                    // the other bypass stays off
        Assert.Null(s.Rom.LmLayer3Gfx(0x104));                    // and it is per level
    }

    /// <summary>Clicking a bin opens it in the editor beside it — the selection and the edit are
    /// the same gesture, which is the whole point of merging the two.</summary>
    [AvaloniaFact]
    public void clicking_a_bin_opens_that_file_in_the_editor()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var g = session.GfxPixels!;
        // A bin holding some other file, so opening it is observable.
        int i = Array.FindIndex(session.GfxBins, b => b.File != g.File && b.File != 0x7F);
        if (i < 0) { log.WriteLine("SKIP: every bin holds the open file"); return; }
        var target = session.GfxBins[i];

        var bins = w.GetControl<StackPanel>("GfxBins");
        var card = (Border)bins.Children[i];
        var at = card.TranslatePoint(new Point(4, 4), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(target.File, g.File);
        log.WriteLine($"clicked bin {target.Name} -> editor now on {g.File:X3}");
    }

    /// <summary>
    /// A committed pixel stroke rebuilds the scene, which REPLACES both layers' object editors.
    /// The window caches them, so without a re-adopt the level canvas goes on editing a
    /// discarded list: the object count dropped, the pixels never moved, and the edit was
    /// thrown away at the next adopt. "Delete does nothing" is what that looks like.
    /// </summary>
    [AvaloniaFact]
    public void a_gfx_commit_does_not_leave_the_level_canvas_on_a_discarded_editor()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var s = SessionOf(w);
        var canvas = w.GetControl<LevelView>("Canvas");

        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        s.GfxPixels!.Color = 5;
        Point AtPx(int x, int y) => view.TranslatePoint(
            new Point(x * view.Zoom + view.Zoom / 2, y * view.Zoom + view.Zoom / 2), w)!.Value;
        w.MouseDown(AtPx(1, 1), MouseButton.Left);
        w.MouseUp(AtPx(1, 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        w.GetControl<ToggleButton>("ModeLevel").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(s.Edit, canvas.Edit);

        // ...and the level canvas still repaints what it edits.
        Point AtCell(int x, int y) => canvas.TranslatePoint(
            new Point(x * 16 * canvas.Zoom + 8 - canvas.Origin.X,
                      y * 16 * canvas.Zoom + 8 - canvas.Origin.Y), w)!.Value;
        w.MouseDown(AtCell(0, 20), MouseButton.Left);
        w.MouseMove(AtCell(12, 25));
        w.MouseUp(AtCell(12, 25), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(canvas.Edit!.Selection);

        var before = (uint[])s.Phases[0]!.Clone();
        w.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, s.Phases[0]!);
    }

    /// <summary>The drawer and the header are two halves of one gesture: the clicked bin is the
    /// SELECTED slot — accent-bordered, and what Load fills. Highlighting "whichever bin holds the
    /// open file" instead lit up two cards whenever two bins shared a file, and left Load with no
    /// way to tell which of them was meant. An unused bin (0x7F) is selectable too: that is how it
    /// gets given a file at all.</summary>
    [AvaloniaFact]
    public void clicking_a_bin_selects_that_slot_and_only_that_slot()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var bins = w.GetControl<StackPanel>("GfxBins");
        // By colour, not thickness: the border keeps one width so the list does not reflow as the
        // selection moves.
        static bool Selected(Control c) => c is Border b && ReferenceEquals(b.BorderBrush, UiColors.Accent);
        // The "Animation slots" heading and rule sit between the tenth bin and AN1, so bins from
        // AN1 on are two children further down than their index.
        static int Card(int i) => i < 10 ? i : i + 2;

        void Click(int i)
        {
            var card = (Border)bins.Children[Card(i)];
            // A card further down the drawer is scrolled out of the clip, and a click at a point
            // the ScrollViewer is clipping hits nothing.
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var at = card.TranslatePoint(new Point(4, 4), w)!.Value;
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // A bin the level really uses, then one it does not — both must end up selected, alone.
        foreach (int i in new[] { Array.FindIndex(session.GfxBins, b => b.File != 0x7F),
                                  Array.FindIndex(session.GfxBins, b => b.File == 0x7F) })
        {
            if (i < 0) { log.WriteLine("SKIP: no such bin in this level"); continue; }
            Click(i);
            Assert.True(Selected(bins.Children[Card(i)]), $"bin {session.GfxBins[i].Name} did not select");
            Assert.Single(bins.Children, Selected);
            // An empty bin trades the sheet for the Load button rather than showing stale pixels.
            Assert.Equal(session.GfxBins[i].File == 0x7F,
                         w.GetControl<Button>("GfxEmptyLoad").IsVisible);
        }
    }

    /// <summary>With no bin selected there is nothing to edit, so the canvas is EMPTY — not the last
    /// file the editor happened to have open, which would read as some bin's contents. And with no
    /// sheet there is no pixel to hit, so the empty view cannot be painted on either.</summary>
    [AvaloniaFact]
    public void no_bin_selected_shows_an_empty_canvas()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var session = SessionOf(w);

        // A file no bin in this level holds, so entering the mode adopts no bin.
        int orphan = Enumerable.Range(0, 0x34).First(f => session.GfxBins.All(b => b.File != f));
        session.GfxPixels!.Open(orphan);
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var canvas = w.GetControl<GfxCanvasView>("GfxCanvas");
        Assert.Equal(0, canvas.Tiles);
        Assert.False(w.GetControl<Button>("GfxEmptyLoad").IsVisible);   // no bin to load INTO
        Assert.False(w.GetControl<Button>("GfxSave").IsEnabled);
        Assert.Equal("no bin selected — pick one in the drawer", w.GetControl<TextBlock>("GfxFileName").Text);

        // Clicking a bin fills it in.
        var bins = w.GetControl<StackPanel>("GfxBins");
        int i = Array.FindIndex(session.GfxBins, b => b.File != 0x7F);
        var card = (Border)bins.Children[i];
        card.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var at = card.TranslatePoint(new Point(4, 4), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(canvas.Tiles > 0, "selecting a bin did not bring its sheet back");
    }

    /// <summary>A bin can only be previewed in rows the game would actually load it under: SMW puts
    /// layer graphics in CGRAM rows 0-7 and sprite graphics in 8-15. Offering all sixteen let an SP
    /// sheet be painted against colours it can never be drawn with, which reads as the palette
    /// being wrong rather than the row being impossible.</summary>
    [AvaloniaFact]
    public void a_bins_palette_rows_are_limited_to_its_half_of_cgram()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var bins = w.GetControl<StackPanel>("GfxBins");
        var rows = w.GetControl<ComboBox>("GfxPalRow");

        void ClickBin(string name)
        {
            var card = (Border)bins.Children[Array.FindIndex(session.GfxBins, b => b.Name == name)];
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var at = card.TranslatePoint(new Point(4, 4), w)!.Value;
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        ClickBin("SP1");
        Assert.Equal(8, rows.ItemCount);
        Assert.Equal(8, rows.Items[0]);
        Assert.Equal(15, rows.Items[7]);
        Assert.InRange(session.GfxPixels!.PalRow, 8, 15);

        // Up from the top of the sprite rows stays in the sprite rows.
        for (int i = 0; i < 12; i++) w.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(8, session.GfxPixels!.PalRow);

        ClickBin("FG1");
        Assert.Equal(8, rows.ItemCount);
        Assert.Equal(0, rows.Items[0]);
        Assert.Equal(7, rows.Items[7]);
        Assert.InRange(session.GfxPixels!.PalRow, 0, 7);
    }

    /// <summary>
    /// A 2bpp file picks a palette GROUP, not a row — the same paradigm the Background footer and
    /// the palette page's "Layer 3 only" use, because it is the same fact: four colours tile
    /// CGRAM 00-1F eight ways, and eight groups is what a layer-3 tilemap word can name.
    ///
    /// The old model offered rows 0-1 and drew from `PalRow * 16 + colour`, which put the editor
    /// on CGRAM 00-03 while the drawer card for the very same bin used 08-0B. That is the bug
    /// this pins: BaseColor is now the one place row and offset are added up, so the sheet, the
    /// swatches and the bin preview cannot disagree.
    /// </summary>
    [AvaloniaFact]
    public void a_2bpp_file_picks_a_palette_group_of_four_rather_than_a_row_of_sixteen()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var rows = w.GetControl<ComboBox>("GfxPalRow");
        var swatches = w.GetControl<PaletteGridView>("GfxColors");
        Assert.Equal(16, swatches.Cols);                       // tile data: the whole row

        session.GfxPixels!.Open(Layer3.VanillaGfx[0]);         // GFX28, a layer-3 file
        session.GfxPixels.ViewAs(2);
        typeof(MainWindow).GetMethod("RefreshGfx",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Layer3.PaletteGroups, rows.ItemCount);
        Assert.Equal(Layer3.PaletteColors, swatches.Cols);
        Assert.Contains("CGRAM", w.GetControl<TextBlock>("GfxPalNote").Text);

        // Group 6 is CGRAM 18-1B: row 1, offset 8 — and BaseColor has to agree with both.
        rows.SelectedIndex = 6;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.GfxPixels.PalRow);
        Assert.Equal(8, session.GfxPixels.ColorOffset);
        Assert.Equal(0x18, session.GfxPixels.BaseColor);
        Assert.Equal(Layer3.PaletteBase(6), session.GfxPixels.BaseColor);
    }

    /// <summary>
    /// The palette group is remembered across the four layer-3 bins, and every one of them is
    /// PREVIEWED in it. They fill a single 512-tile window that one tilemap addresses, so "show
    /// layer 3 in group 6" is a statement about layer 3, not about LG2 — and cycling LG1-LG4 to
    /// compare them used to reset to each bin's own default on every click, which is also why
    /// the default could never have been worth keeping: all four declare the same one.
    /// </summary>
    [AvaloniaFact]
    public void the_layer_3_palette_group_is_remembered_across_the_four_lg_bins()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var bins = w.GetControl<StackPanel>("GfxBins");
        var rows = w.GetControl<ComboBox>("GfxPalRow");

        // By LABEL, not by index: the list carries headings between the groups, so the nth bin
        // is not the nth child once the layer-3 four are in reach.
        void ClickBin(string name)
        {
            var card = bins.Children.OfType<Border>().First(b =>
                b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == $"[{name}]"));
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var at = card.TranslatePoint(new Point(4, 4), w)!.Value;
            w.MouseDown(at, MouseButton.Left);
            w.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        ClickBin("LG1");
        Assert.Equal(Layer3.PaletteGroups, rows.ItemCount);    // groups, not rows
        Assert.Equal(2, rows.SelectedIndex);                   // layer 3's own first block

        rows.SelectedIndex = 6;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Layer3.PaletteBase(6), session.GfxPixels!.BaseColor);

        ClickBin("LG3");                                       // a different bin, same picture
        Assert.Equal(6, rows.SelectedIndex);
        Assert.Equal(Layer3.PaletteBase(6), session.GfxPixels!.BaseColor);

        // ...and a bin that is NOT layer 3 keeps its own row: the memory is layer 3's alone.
        ClickBin("FG1");
        Assert.Equal(16, w.GetControl<PaletteGridView>("GfxColors").Cols);
        ClickBin("LG2");
        Assert.Equal(6, rows.SelectedIndex);
    }

    /// <summary>Up/Down cycle the paint palette row while drawing — the row is the thing you change
    /// most, and the combo box being the state is what carries it to the editor, the sheet and the
    /// drawer's preview of the selected bin in one go. It clamps rather than wrapping: row 0 is the
    /// end of the list, not the way to row 15.</summary>
    [AvaloniaFact]
    public void up_and_down_cycle_the_palette_row_in_graphics_mode()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        var rows = w.GetControl<ComboBox>("GfxPalRow");
        int start = rows.SelectedIndex;

        w.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(start + 1, rows.SelectedIndex);
        Assert.Equal(start + 1, session.GfxPixels!.PalRow);      // the editor repaints in that row

        for (int i = 0; i <= start + 1; i++)
            w.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, rows.SelectedIndex);
        Assert.Equal(0, session.GfxPixels!.PalRow);
    }

    /// <summary>F walks the tools in the order the bar shows them, left to right, wrapping —
    /// and it works with the focus on a bar button, not only on the sheet.</summary>
    [AvaloniaFact]
    public void f_cycles_the_tools_in_bar_order_from_anywhere_in_the_mode()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var g = SessionOf(w).GfxPixels!;

        // The bar's tool toggles, in visual order, each mapped to the tool it arms. The flyout
        // variants (outline / filled) are one tool with a setting, not stops on the cycle.
        var byName = new Dictionary<string, GfxEdit.Tool>
        {
            ["GfxSelect"] = GfxEdit.Tool.Select, ["GfxErase"] = GfxEdit.Tool.Eraser,
            ["GfxFill"] = GfxEdit.Tool.Fill, ["GfxPencil"] = GfxEdit.Tool.Pencil,
            ["GfxDropper"] = GfxEdit.Tool.Dropper, ["GfxRect"] = GfxEdit.Tool.Rect,
            ["GfxEllipse"] = GfxEdit.Tool.Ellipse, ["GfxLine"] = GfxEdit.Tool.Line,
        };
        var bar = w.GetControl<Border>("GfxEditorBar").GetVisualDescendants().OfType<ToggleButton>()
                   .Where(t => t.Name is { } n && byName.ContainsKey(n)).Select(t => byName[t.Name!]).ToList();
        Assert.Equal(byName.Count, bar.Count);                       // every tool is on the bar once

        // Focus a bar button, as a click on it would, then press F round the whole bar.
        w.GetControl<ToggleButton>("GfxSelect").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(ReferenceEquals(w.FocusManager?.GetFocusedElement(), w.GetControl<GfxCanvasView>("GfxCanvas")));

        var start = g.Current;
        int at = bar.IndexOf(start);
        Assert.True(at >= 0);
        var seen = new List<GfxEdit.Tool>();
        for (int i = 0; i < bar.Count; i++)
        {
            w.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            seen.Add(g.Current);
        }
        var expected = Enumerable.Range(1, bar.Count).Select(k => bar[(at + k) % bar.Count]).ToList();
        Assert.Equal(expected, seen);
        Assert.Equal(start, g.Current);                              // a full lap lands back home
    }

    /// <summary>The wheel zooms the sheet one slider step a notch, about the pixel under the
    /// cursor, and the gutter slider follows because the step goes through it.</summary>
    [AvaloniaFact]
    public void the_wheel_zooms_the_sheet_about_the_cursor_and_the_slider_follows()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var zoom = w.GetControl<Slider>("ZoomSlider");
        Assert.True(view.Tiles > 0, "the sheet never loaded a file");
        // Start where the sheet outgrows the viewport: anchoring needs somewhere to scroll to,
        // and at the default 8x the whole sheet fits.
        zoom.Value = 1500;
        Dispatcher.UIThread.RunJobs();
        double zoom0 = view.Zoom;
        Assert.Equal(15, zoom0);

        var local = new Point(30 * zoom0 + 1, 30 * zoom0 + 1);     // on screen, well into the sheet
        var at = view.TranslatePoint(local, w)!.Value;
        var px = view.PixelAt(local);
        Assert.NotNull(px);

        w.MouseWheel(at, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom0 + 1, view.Zoom);
        Assert.Equal(view.Zoom * 100, zoom.Value);
        Assert.Equal(px, view.PixelAt(w.TranslatePoint(at, view)!.Value));

        w.MouseWheel(at, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom0, view.Zoom);
        Assert.Equal(px, view.PixelAt(w.TranslatePoint(at, view)!.Value));
    }

    /// <summary>One zoom control, two canvases at wildly different scales: each mode keeps its own
    /// value and range, and gets it back on the way in. Sharing one number would land the pixel
    /// editor at 200% (unusable) or the level at 1200%.</summary>
    [AvaloniaFact]
    public void the_gutter_zoom_is_per_canvas_mode_and_is_remembered()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var zoom = w.GetControl<Slider>("ZoomSlider");
        var level = w.GetControl<LevelView>("Canvas");
        var sheet = w.GetControl<GfxCanvasView>("GfxCanvas");
        double levelPct = zoom.Value;

        void Mode(string name) =>
            w.GetControl<ToggleButton>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Mode("ModeGfx");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1600, zoom.Maximum);              // whole screen pixels per GFX pixel
        Assert.Equal(sheet.Zoom * 100, zoom.Value);

        zoom.Value = 1200;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(12, sheet.Zoom);
        Assert.Equal(levelPct / 100, level.Zoom);      // the level's zoom was left alone

        Mode("ModeLevel");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(800, zoom.Maximum);
        Assert.Equal(levelPct, zoom.Value);

        Mode("ModeGfx");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1200, zoom.Value);
        Assert.Equal(12, sheet.Zoom);
    }

    /// <summary>Explicit save: a fork of a STOCK file moves out to its own named ExGFX id, the
    /// stock file comes back, the level's bin follows the new file, and the .pdp on disk carries
    /// it. Undo still works afterwards, which is the part the id move can silently break.</summary>
    [Fact]
    public void saving_a_forked_stock_file_writes_a_named_exgfx_and_repoints_the_bin()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);

        var g = s.GfxPixels!;
        int stock = s.GfxBins[0].File;                  // FG1 — a bin the level really draws from
        g.Open(stock);
        int before = g.ColorAt(0, 0)!.Value;
        g.Color = before == 3 ? 1 : 3;
        Assert.True(g.Paint(0, 0, out _));
        g.EndStroke();

        Assert.True(s.GfxIsStock, "a stock file has no ExGFX id yet");
        log.WriteLine(s.SaveGfx("test-clouds"));

        Assert.NotEqual(stock, g.File);
        Assert.True(g.File >= 0x100);
        Assert.Equal("test-clouds", s.GfxName(g.File));
        Assert.Equal(g.Color, g.ColorAt(0, 0));
        Assert.Equal(before, StockPixel(s, stock));     // the stock file itself is unedited again
        Assert.Equal(g.File, s.GfxBins[0].File);        // the bin follows the saved file
        Assert.False(s.GfxDirty, "a save left the editor still claiming unsaved edits");
        Assert.False(s.GfxIsStock);               // ...and a second save just writes it

        var reopened = Project.Open(s.Project!.FilePath);
        Assert.True(reopened.Data.Gfx.ContainsKey(g.File.ToString("X3")));
        Assert.Equal("test-clouds", reopened.Data.GfxNames[g.File.ToString("X3")]);
        Assert.False(reopened.Data.Gfx.ContainsKey(stock.ToString("X3")), "the stock fork stayed behind");

        Assert.True(g.Undo());                          // history followed the id
        Assert.Equal(before, g.ColorAt(0, 0));
    }

    /// <summary>Save As from an already-CUSTOM file: a second id appears under the typed name,
    /// the source file keeps its own bytes and name, and the bin follows the copy.</summary>
    [Fact]
    public void save_as_copies_a_custom_file_to_a_new_id_and_leaves_the_source_alone()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);

        var g = s.GfxPixels!;
        g.Open(s.GfxBins[0].File);
        g.Color = g.ColorAt(0, 0) == 3 ? 1 : 3;
        Assert.True(g.Paint(0, 0, out _));
        g.EndStroke();
        log.WriteLine(s.SaveGfx("original"));           // fork out: now a custom file
        int source = g.File;

        log.WriteLine(s.SaveGfxAs("the-copy"));

        Assert.NotEqual(source, g.File);                // the editor follows the copy
        Assert.Equal("the-copy", s.GfxName(g.File));
        Assert.Equal("original", s.GfxName(source));    // the source kept its name...
        Assert.Equal(g.ColorAt(0, 0), StockPixel(s, source));  // ...and its bytes
        Assert.Equal(g.File, s.GfxBins[0].File);        // the bin repointed to the copy

        int copyBefore = g.ColorAt(0, 0)!.Value;
        g.Color = copyBefore == 3 ? 1 : 3;
        Assert.True(g.Paint(0, 0, out _));              // editing the copy...
        g.EndStroke();
        Assert.Equal(copyBefore, StockPixel(s, source)); // ...must not bleed into the source
    }

    /// <summary>The Rect tool: outline touches only the border, filled touches every cell, and
    /// either way the whole shape is ONE undo entry — which is the reason it exists rather than
    /// dragging the pencil around four edges. Corners are accepted in any order.</summary>
    [Fact]
    public void rect_draws_an_outline_or_a_fill_as_a_single_undo_entry()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        var g = s.GfxPixels!;
        g.Open(s.GfxBins[0].File);

        // A colour that is not already everywhere in the box, so every write is observable.
        g.Color = g.ColorAt(2, 2) == 3 ? 1 : 3;
        int want = g.Color;
        var before = Snapshot(g, 1, 1, 6, 6);
        int depth = g.UndoDepth;

        // Dragged bottom-right to top-left: the corners are a box, not an order.
        g.RectFilled = false;
        Assert.True(g.PaintRect(5, 5, 2, 2, out _));
        g.EndStroke();
        Assert.Equal(depth + 1, g.UndoDepth);                  // one entry for the whole shape

        for (int y = 2; y <= 5; y++)
            for (int x = 2; x <= 5; x++)
            {
                bool edge = x == 2 || x == 5 || y == 2 || y == 5;
                if (edge) Assert.Equal(want, g.ColorAt(x, y));
                else Assert.Equal(before[(y - 1) * 6 + (x - 1)], g.ColorAt(x, y));  // interior untouched
            }
        Assert.True(g.Undo());
        Assert.Equal(before[(2 - 1) * 6 + (2 - 1)], g.ColorAt(2, 2));

        g.RectFilled = true;
        Assert.True(g.PaintRect(2, 2, 5, 5, out _));
        g.EndStroke();
        for (int y = 2; y <= 5; y++)
            for (int x = 2; x <= 5; x++)
                Assert.Equal(want, g.ColorAt(x, y));           // interior too, this time
    }

    /// <summary>The Ellipse tool: round rather than square (the box corners stay untouched), the
    /// outline is a CLOSED ring, filled covers the middle, and either way it is one undo entry.
    /// Degenerate boxes degrade sensibly instead of drawing nothing.</summary>
    [Fact]
    public void ellipse_draws_a_closed_ring_or_a_disc_as_a_single_undo_entry()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        var g = s.GfxPixels!;
        g.Open(s.GfxBins[0].File);
        g.Color = g.ColorAt(4, 4) == 3 ? 1 : 3;
        int want = g.Color;

        // An 8x8 circle in a box at (2,2)-(9,9).
        var before = Snapshot(g, 2, 2, 8, 8);
        int depth = g.UndoDepth;
        g.EllipseFilled = false;
        Assert.True(g.PaintEllipse(9, 9, 2, 2, out _));      // corners in either order
        g.EndStroke();
        Assert.Equal(depth + 1, g.UndoDepth);

        // Round, not square: the box's corner pixels are outside the ellipse.
        foreach (var (cx, cy) in new[] { (2, 2), (9, 2), (2, 9), (9, 9) })
            Assert.Equal(before[(cy - 2) * 8 + (cx - 2)], g.ColorAt(cx, cy));
        // A ring: the middle is untouched, and the top/bottom/left/right extremes are on it.
        Assert.Equal(before[(5 - 2) * 8 + (5 - 2)], g.ColorAt(5, 5));
        foreach (var (ex, ey) in new[] { (5, 2), (5, 9), (2, 5), (9, 5) })
            Assert.Equal(want, g.ColorAt(ex, ey));
        // Closed: every painted pixel has a painted 8-neighbour, so the ring has no gaps.
        for (int y = 2; y <= 9; y++)
            for (int x = 2; x <= 9; x++)
            {
                if (g.ColorAt(x, y) != want) continue;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && g.ColorAt(x + dx, y + dy) == want) n++;
                Assert.True(n >= 2, $"ring pixel ({x},{y}) has {n} neighbours — the outline is broken");
            }

        Assert.True(g.Undo());
        g.EllipseFilled = true;
        Assert.True(g.PaintEllipse(2, 2, 9, 9, out _));
        g.EndStroke();
        Assert.Equal(want, g.ColorAt(5, 5));                 // the middle is covered now
        Assert.Equal(before[0], g.ColorAt(2, 2));            // ...and the corners still are not

        // Degenerate boxes: a 1x1 drag is a dot rather than nothing at all.
        Assert.True(g.PaintEllipse(20, 20, 20, 20, out _));
        g.EndStroke();
        Assert.Equal(want, g.ColorAt(20, 20));
    }

    /// <summary>A line is the one shape whose two ends are not a box: \ and / have the same
    /// bounding box, so a drag reported as a box could only ever draw one of them.</summary>
    [Fact]
    public void line_runs_between_its_two_ends_whichever_way_it_was_dragged()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        var g = s.GfxPixels!;
        g.Open(s.GfxBins[0].File);
        g.Current = GfxEdit.Tool.Line;

        var before = Snapshot(g, 2, 2, 8, 8);
        g.Color = Enumerable.Range(1, g.MaxColor).First(c => !before.Contains(c));
        int want = g.Color;
        int depth = g.UndoDepth;

        // Down-right: the leading diagonal, so the anti-diagonal's corners stay untouched.
        Assert.True(g.PaintLine(2, 2, 9, 9, out _));
        g.EndStroke();
        Assert.Equal(depth + 1, g.UndoDepth);              // one entry for the whole line
        for (int i = 0; i < 8; i++) Assert.Equal(want, g.ColorAt(2 + i, 2 + i));
        Assert.Equal(before[7], g.ColorAt(9, 2));          // the other diagonal is not drawn
        Assert.True(g.Undo());

        // Up-right: same bounding box, the other diagonal — and dragged end-first at that.
        Assert.True(g.PaintLine(2, 9, 9, 2, out _));
        g.EndStroke();
        for (int i = 0; i < 8; i++) Assert.Equal(want, g.ColorAt(2 + i, 9 - i));
        Assert.Equal(before[0], g.ColorAt(2, 2));
        Assert.True(g.Undo());

        // Connected at a shallow slope: every pixel touches the next one.
        Assert.True(g.PaintLine(0, 0, 30, 6, out _));
        g.EndStroke();
        var px = g.ShapePixels(0, 0, 30, 6).ToList();
        for (int i = 1; i < px.Count; i++)
            Assert.True(Math.Abs(px[i].X - px[i - 1].X) <= 1 && Math.Abs(px[i].Y - px[i - 1].Y) <= 1,
                        $"the line jumps from {px[i - 1]} to {px[i]}");
        Assert.Equal((0, 0), px[0]);
        Assert.Equal((30, 6), px[^1]);                     // both ends are ON the line

        // A click that never moves is a dot, not nothing.
        Assert.True(g.PaintLine(20, 20, 20, 20, out _));
        g.EndStroke();
        Assert.Equal(want, g.ColorAt(20, 20));
    }

    /// <summary>The whole point of the live preview: it is drawn from ShapePixels, so if that
    /// ever drifts from what the paint writes, the preview starts lying about where the drag
    /// will land.</summary>
    [Fact]
    public void the_shape_preview_lists_exactly_the_pixels_the_paint_writes()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        var g = s.GfxPixels!;
        g.Open(s.GfxBins[0].File);

        foreach (var (tool, filled) in new[]
                 { (GfxEdit.Tool.Rect, false), (GfxEdit.Tool.Rect, true),
                   (GfxEdit.Tool.Ellipse, false), (GfxEdit.Tool.Ellipse, true),
                   (GfxEdit.Tool.Line, false) })
        {
            g.Current = tool;
            g.RectFilled = g.EllipseFilled = filled;
            // A colour nothing in the box already is, so every write shows up as a change.
            var before = Snapshot(g, 2, 2, 8, 8);
            int free = Enumerable.Range(1, g.MaxColor).FirstOrDefault(c => !before.Contains(c));
            Assert.True(free > 0, "the box uses every colour — pick another box");
            g.Color = free;

            // The same drag the paint gets: a line's ends are not interchangeable, so the
            // preview has to be asked exactly what the release will be asked.
            var preview = g.ShapePixels(2, 2, 9, 9).ToHashSet();
            Assert.True(g.PaintShape(2, 2, 9, 9, out _), $"{tool} filled={filled} painted nothing");
            g.EndStroke();

            var changed = new HashSet<(int, int)>();
            for (int y = 2; y <= 9; y++)
                for (int x = 2; x <= 9; x++)
                    if (g.ColorAt(x, y) != before[(y - 2) * 8 + (x - 2)]) changed.Add((x, y));

            Assert.Equal(preview.OrderBy(p => p).ToArray(), changed.OrderBy(p => p).ToArray());
            Assert.True(g.Undo());
        }
    }


    /// <summary>

    /// <summary>
    /// Dragging a selection LIFTS it: the block leaves a hole, rides above the sheet, and eats
    /// nothing it passes over. Only the drop — a click elsewhere — writes, as one undo entry.
    /// Before this, releasing the mouse committed wherever the block happened to be, so a move
    /// that changed its mind had already destroyed the pixels it landed on first.
    /// </summary>
    [AvaloniaFact]
    public void dragging_a_selection_floats_it_and_only_the_drop_writes()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("GfxSelect").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;
        Point At(int x, int y) => view.TranslatePoint(
            new Point(x * view.Zoom + view.Zoom / 2, y * view.Zoom + view.Zoom / 2), w)!.Value;

        // A known 2x2 block, and what sits where it will pass through and where it will land.
        foreach (var (x, y, c) in new[] { (0, 0, 1), (1, 0, 2), (0, 1, 3), (1, 1, 4) })
        { g.Color = c; g.Paint(x, y, out _); }
        g.EndStroke();
        var passed = Snapshot(g, 6, 6, 2, 2);
        var landing = Snapshot(g, 12, 9, 2, 2);
        int depth = g.UndoDepth;

        view.Selection = (0, 0, 2, 2);
        Dispatcher.UIThread.RunJobs();

        // Grab it and wander: over one spot, then on to another.
        w.MouseDown(At(0, 0), MouseButton.Left);
        w.MouseMove(At(6, 6));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((6, 6, 2, 2), view.Float);        // it is on the floating layer now
        Assert.Equal(passed, Snapshot(g, 6, 6, 2, 2)); // ...and has eaten nothing under it
        w.MouseMove(At(12, 9));
        w.MouseUp(At(12, 9), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((12, 9, 2, 2), view.Float);
        Assert.Equal(passed, Snapshot(g, 6, 6, 2, 2));    // the place it changed its mind about
        Assert.Equal(landing, Snapshot(g, 12, 9, 2, 2));  // release is not a commitment either
        Assert.Equal(depth, g.UndoDepth);
        Assert.Equal(new[] { 0, 0, 0, 0 }, Snapshot(g, 0, 0, 2, 2));   // only the hole it left

        // A click elsewhere drops it: ONE undo entry for the whole move.
        w.MouseDown(At(30, 3), MouseButton.Left);
        w.MouseUp(At(30, 3), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Float);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Snapshot(g, 12, 9, 2, 2));
        Assert.Equal(passed, Snapshot(g, 6, 6, 2, 2));
        Assert.Equal(depth + 1, g.UndoDepth);
        Assert.True(g.Undo());
        Assert.Equal(new[] { 1, 2, 3, 4 }, Snapshot(g, 0, 0, 2, 2));   // home in one step
        Assert.Equal(landing, Snapshot(g, 12, 9, 2, 2));

        // Esc on a lifted block is the same deal in reverse: the hole fills back in.
        g.Redo();
        view.Selection = (12, 9, 2, 2);
        Dispatcher.UIThread.RunJobs();
        depth = g.UndoDepth;
        w.MouseDown(At(12, 9), MouseButton.Left);
        w.MouseMove(At(20, 20));
        w.MouseUp(At(20, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Float);
        Assert.Equal((12, 9, 2, 2), view.Selection);                   // the marquee goes home
        Assert.Equal(new[] { 1, 2, 3, 4 }, Snapshot(g, 12, 9, 2, 2));
        Assert.Equal(depth, g.UndoDepth);                              // and nothing happened
    }

    /// Rotate and flip act on the marquee: they are greyed until the select tool holds one, the
    /// quarter turns need a SQUARE one (w×h turned is h×w, with nowhere to put it), and Ctrl
    /// while rubber-banding forces a square so getting one is not a pixel-counting exercise.
    /// </summary>
    [AvaloniaFact]
    public void rotate_and_flip_act_on_the_marquee_and_ctrl_drags_a_square()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("GfxSelect").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;
        var (rotL, rotR) = (w.GetControl<Button>("GfxRotL"), w.GetControl<Button>("GfxRotR"));
        var (flipH, flipV) = (w.GetControl<Button>("GfxFlipH"), w.GetControl<Button>("GfxFlipV"));

        // Nothing selected: all four are dead.
        Assert.False(rotL.IsEnabled || rotR.IsEnabled || flipH.IsEnabled || flipV.IsEnabled);

        Point At(int x, int y) => view.TranslatePoint(
            new Point(x * view.Zoom + view.Zoom / 2, y * view.Zoom + view.Zoom / 2), w)!.Value;

        // A plain drag: 6 wide, 3 tall. All four act on it — a quarter turn takes the marquee
        // round with the pixels rather than needing a square to fit back into.
        w.MouseDown(At(4, 4), MouseButton.Left);
        w.MouseMove(At(9, 6));
        w.MouseUp(At(9, 6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((4, 4, 6, 3), view.Selection);
        Assert.True(rotL.IsEnabled && rotR.IsEnabled && flipH.IsEnabled && flipV.IsEnabled);

        // The same 6x3 drag with Ctrl held, clear of the last one: the shorter axis wins, so the
        // square lands inside the drag.
        w.MouseDown(At(20, 10), MouseButton.Left);
        w.MouseMove(At(25, 12), RawInputModifiers.Control);
        w.MouseUp(At(25, 12), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((20, 10, 3, 3), view.Selection);
        Assert.True(rotL.IsEnabled && rotR.IsEnabled);

        // A turn happens ON THE FLOATING LAYER, like a move: the block is lifted, turns in the
        // air, and only the drop writes. Nothing under it is disturbed on the way.
        void Drop()
        {
            w.MouseDown(At(40, 20), MouseButton.Left);
            w.MouseUp(At(40, 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // A known 2x2 block to turn.
        foreach (var (x, y, c) in new[] { (0, 0, 1), (1, 0, 2), (0, 1, 3), (1, 1, 4) })
        { g.Color = c; g.Paint(x, y, out _); }
        g.EndStroke();
        int depth = g.UndoDepth;

        foreach (var (btn, want) in new[] { (flipH, new[] { 2, 1, 4, 3 }), (flipV, [3, 4, 1, 2]),
                                            (rotR, [3, 1, 4, 2]), (rotL, [2, 4, 1, 3]) })
        {
            view.Selection = (0, 0, 2, 2);
            Dispatcher.UIThread.RunJobs();
            btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((0, 0, 2, 2), view.Float);        // up in the air, not on the sheet
            Assert.Equal(depth, g.UndoDepth);
            Drop();
            Assert.Equal(want, Snapshot(g, 0, 0, 2, 2));
            Assert.Equal(depth + 1, g.UndoDepth);          // one gesture, one undo entry
            Assert.True(g.Undo());
            Assert.Equal(new[] { 1, 2, 3, 4 }, Snapshot(g, 0, 0, 2, 2));
        }

        // A NON-square block: 3 wide, 1 tall. Turning it right stands it on end — the float
        // comes round to 1x3 and pivots about its own centre — and what it now covers is
        // untouched until it lands. This is the whole reason a turn floats: in place, it would
        // have to clear the footprint it no longer covers AND write over three fresh pixels.
        foreach (var (x, c) in new[] { (8, 1), (9, 2), (10, 3) })
        { g.Color = c; g.Paint(x, 4, out _); }
        g.EndStroke();
        var under = new[] { g.ColorAt(9, 3)!.Value, g.ColorAt(9, 5)!.Value };   // what it will cover
        depth = g.UndoDepth;
        view.Selection = (8, 4, 3, 1);
        Dispatcher.UIThread.RunJobs();

        rotR.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((9, 3, 1, 3), view.Float);
        Assert.Equal(under, new[] { g.ColorAt(9, 3)!.Value, g.ColorAt(9, 5)!.Value });      // still there while it hovers
        Assert.Equal(depth, g.UndoDepth);

        // Turn it again before dropping: still nothing written, and it is back on its side.
        rotR.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((8, 4, 3, 1), view.Float);
        Assert.Equal(under, new[] { g.ColorAt(9, 3)!.Value, g.ColorAt(9, 5)!.Value });
        Assert.Equal(depth, g.UndoDepth);

        // Esc puts the whole thing back as if none of it happened.
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Float);
        Assert.Equal((8, 4, 3, 1), view.Selection);
        Assert.Equal(new[] { 1, 2, 3 }, Snapshot(g, 8, 4, 3, 1));
        Assert.Equal(depth, g.UndoDepth);

        // ...and once dropped, a half turn is one undo entry: the row reversed in place.
        rotR.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        rotR.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Drop();
        Assert.Equal(new[] { 3, 2, 1 }, Snapshot(g, 8, 4, 3, 1));
        Assert.Equal(under, new[] { g.ColorAt(9, 3)!.Value, g.ColorAt(9, 5)!.Value });      // the pixels it hovered over survived
        Assert.Equal(depth + 1, g.UndoDepth);
        Assert.True(g.Undo());
        Assert.Equal(new[] { 1, 2, 3 }, Snapshot(g, 8, 4, 3, 1));

        // A tool that is not Select leaves the marquee alone but takes the buttons away: these
        // act on what the pointer holds, and the pencil holds nothing.
        w.GetControl<ToggleButton>("GfxPencil").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(flipH.IsEnabled || rotR.IsEnabled);
    }


    /// <summary>
    /// The depth box under the canvas re-reads the open file at another depth. Nothing in a raw
    /// planar file says how deep it is, so the detected depth has to be arguable with — and the
    /// argument is per file: opening another one drops it, or every file after a 2bpp layer-3
    /// file would decode as 2bpp too.
    /// </summary>
    [AvaloniaFact]
    public void the_depth_box_re_reads_the_file_and_resets_when_it_changes()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var box = w.GetControl<ComboBox>("GfxBpp");
        var view = w.GetControl<GfxCanvasView>("GfxCanvas");
        var g = SessionOf(w).GfxPixels!;

        // A vanilla tile file. It is STORED as three planes, but three planes is not something
        // the SNES displays — the upload expands them — so the box says what it will look like:
        // 4bpp tile data.
        Assert.Equal(3, g.Bpp);
        Assert.Equal(0, box.SelectedIndex);
        int tiles3 = g.Layout.Tiles;

        // Read it as 2bpp instead: 16 bytes a tile, so the same bytes are half again as many
        // tiles, and the sheet the canvas holds grows with them.
        box.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, g.Bpp);
        Assert.Equal(3, g.MaxColor);
        Assert.Equal(tiles3 * 3 / 2, g.Layout.Tiles);
        Assert.Equal(g.Layout.Tiles, view.Tiles);

        // Another file drops the argument — and a layer-3 file knows its own depth.
        g.Open(0x28);
        RefreshOf(w);
        Assert.Null(g.BppOverride);
        Assert.Equal(2, g.Bpp);
        Assert.Equal(1, box.SelectedIndex);
    }

    /// <summary>Re-run the window's GFX refresh, the way every real file switch does.</summary>
    private static void RefreshOf(MainWindow w)
    {
        typeof(MainWindow).GetMethod("RefreshGfx",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(w, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The colour indices of a w×h box, row-major from (x,y).</summary>
    private static int[] Snapshot(GfxEdit g, int x, int y, int w, int h)
    {
        var px = new int[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++) px[j * w + i] = g.ColorAt(x + i, y + j) ?? -1;
        return px;
    }

    /// <summary>Pixel (0,0) of a file as the ROM resolves it right now.</summary>
    private static int StockPixel(EditorSession s, int file)
    {
        var g = s.GfxPixels!;
        int keep = g.File;
        g.Open(file);
        int c = g.ColorAt(0, 0)!.Value;
        g.Open(keep);
        return c;
    }

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;
}
