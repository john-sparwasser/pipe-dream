using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
/// The controls also differ from the level canvas on purpose: left paints here. There is nothing
/// to select in a pixel sheet, so this mode uses ordinary paint-program bindings, and the ImGui
/// version does the same.
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
        Assert.False(w.GetControl<ScrollViewer>("CanvasScroll").IsVisible);
        Assert.True(w.GetControl<DockPanel>("GfxToolPanel").IsVisible);
        Assert.False(w.GetControl<ScrollViewer>("PaletteScroll").IsVisible);

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
        // Find a pixel whose colour is not the armed one, so the pick is observable.
        int? target = null;
        for (int x = 0; x < 32 && target is null; x++)
            for (int y = 0; y < 8; y++)
                if (g.ColorAt(x, y) is { } c && c != g.Color) { target = x * 100 + y; break; }
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

        // The drawer's tabs are the LEVEL's views; GFX is not among them any more.
        var tabs = w.GetControl<TabStrip>("PaletteTabs");
        Assert.Equal(4, tabs.ItemCount);
        Assert.DoesNotContain("GFX", tabs.Items.OfType<TabStripItem>().Select(t => $"{t.Content}"));

        w.GetControl<ToggleButton>("ModeGfx").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // GFX mode's drawer carries both halves: the paint colours AND every bin, each with its
        // preview, so the file you want is visible rather than a hex id to recall.
        Assert.True(w.GetControl<DockPanel>("GfxToolPanel").IsVisible);
        Assert.True(w.GetControl<PaletteGridView>("GfxColors").IsVisible);
        var bins = w.GetControl<StackPanel>("GfxBins");
        Assert.Equal(SessionOf(w).GfxBins.Length, bins.Children.Count);
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
        static bool Selected(Control c) => ReferenceEquals(((Border)c).BorderBrush, UiColors.Accent);

        void Click(int i)
        {
            var card = (Border)bins.Children[i];
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
            Assert.True(Selected(bins.Children[i]), $"bin {session.GfxBins[i].Name} did not select");
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
        Assert.Equal("no bin selected", w.GetControl<TextBlock>("GfxFileName").Text);

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
