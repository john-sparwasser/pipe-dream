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
/// Palette editing.
///
/// The rule that matters: a colour is not a tint applied at the end, it is an INPUT to
/// composition. Every 16x16 tile's pixels are baked from the palette, so an edit has to be in
/// place before the tile caches are built — a swatch that changes while the level keeps showing
/// the ROM's colours is the failure this pins down.
/// </summary>
public class PaletteTabTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduipal-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    /// <summary>A colour that is definitely not what the ROM has there.</summary>
    private static ushort Different(ushort current) => (ushort)(current ^ 0x1F);

    /// <summary>The window's own idea of CGRAM 0x42, read back through the session.</summary>
    private static ushort s0x42(MainWindow w) => SessionOf(w).PaletteBgr(0x42);

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    [Fact]
    public void an_edited_colour_reaches_the_composed_tiles_not_just_the_swatch()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);

        // Colour 2 of palette row 4 is ordinary foreground, used by the terrain tiles.
        const int idx = 0x42;
        var before = s.Scene!.TileCaches[0].Select(t => (uint[]?)t?.Clone()).ToArray();
        ushort target = Different(s.PaletteBgr(idx));

        Assert.True(s.SetPaletteColor(idx, target));
        Assert.Equal(target, s.PaletteBgr(idx));
        Assert.True(s.IsPaletteEdited(idx));

        // The tile CACHES are what prove it: a swatch can change on its own, and the tiles are
        // baked from the palette. Which tiles use this CGRAM entry depends on the tileset, so
        // the claim is "some tile changed", not "this one did".
        var after = s.Scene!.TileCaches[0];
        int changed = 0;
        for (int t = 0; t < after.Length && t < before.Length; t++)
            if (before[t] is { } b && after[t] is { } a && !b.SequenceEqual(a)) changed++;
        log.WriteLine($"CGRAM 0x{idx:X2} -> {target:X4} changed {changed} tiles");
        Assert.True(changed > 0, "an edited colour never reached the tile caches");
    }

    [Fact]
    public void reset_puts_every_colour_back()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);

        ushort original = s.PaletteBgr(0x42);
        Assert.True(s.SetPaletteColor(0x42, Different(original)));
        Assert.Equal(1, s.PaletteEditCount);

        Assert.True(s.ResetPalette());
        Assert.Equal(0, s.PaletteEditCount);
        Assert.Equal(original, s.PaletteBgr(0x42));
        Assert.False(s.ResetPalette());          // nothing left to reset
    }

    /// <summary>Palette edits are per level, so switching level must not carry them over — and
    /// they are recorded in the project, so they must survive a save.</summary>
    [Fact]
    public void palette_edits_belong_to_the_level_and_survive_a_save()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }

        var a = new EditorSession();
        Assert.True(a.NewProject(Path.Combine(dir, "proj"), Vanilla), a.Status);
        string pdp = a.Project!.FilePath;
        a.ShowLevel(0x105);
        ushort target = Different(a.PaletteBgr(0x42));
        Assert.True(a.SetPaletteColor(0x42, target));

        a.ShowLevel(0x106);                       // a different level keeps its own colours
        Assert.Equal(0, a.PaletteEditCount);
        a.ShowLevel(0x105);
        Assert.Equal(target, a.PaletteBgr(0x42));

        a.Save();
        var b = new EditorSession();
        Assert.True(b.OpenProject(pdp), b.Status);
        b.ShowLevel(0x105);
        Assert.Equal(target, b.PaletteBgr(0x42));
        Assert.True(b.IsPaletteEdited(0x42));
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void the_palette_tab_edits_in_the_snes_colour_space()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetControl<DockPanel>("PalettePanel").IsVisible);

        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        // The channel sliders live in the picker now, next to the square and the hue strip.
        var r = w.picker.GetControl<Slider>("PalR");
        var g = w.picker.GetControl<Slider>("PalG");
        var b = w.picker.GetControl<Slider>("PalB");

        // Five bits per channel is what the hardware stores, so that is the slider range.
        Assert.Equal(31, r.Maximum);
        Assert.Equal(31, g.Maximum);
        Assert.Equal(31, b.Maximum);

        // Click swatch 0x42 (row 4, column 2) the way the user would. Picking it loads the
        // picker and must NOT count as an edit on its own.
        var at = grid.TranslatePoint(new Point(grid.HeaderSize + 2 * grid.Cell + grid.Cell / 2,
                                               grid.HeaderSize + 4 * grid.Cell + grid.Cell / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x42, grid.Selected);
        Assert.Contains("0x42", w.GetControl<TextBlock>("PaletteIndex").Text!);
        Assert.DoesNotContain("edit(s)", w.GetControl<TextBlock>("PaletteNote").Text!);

        Assert.Equal(s0x42(w), w.picker.Bgr);       // the picker opened on that swatch's colour

        // Moving a channel applies straight through to the level — no debounce, because a
        // recolour recomposes only the phase on screen and is fast enough to keep up.
        double before = r.Value;
        r.Value = before == 31 ? 0 : 31;
        Dispatcher.UIThread.RunJobs();
        ushort want = (ushort)((int)b.Value << 10 | (int)g.Value << 5 | (int)r.Value);
        Assert.Equal(want, s0x42(w));                       // the level really has it
        Assert.Equal(EditorSession.Rgba(want), grid.Colors[0x42]);   // and so does the swatch
        Assert.Contains("edit(s)", w.GetControl<TextBlock>("PaletteNote").Text!);
    }

    /// <summary>
    /// Clicking a swatch pops the picker over it, loaded with that colour — the ImGui gesture.
    /// This is the only place in the app that uses a Flyout, so it is worth pinning that the
    /// popup really mounts rather than silently doing nothing.
    /// </summary>
    [AvaloniaFact]
    public void clicking_a_swatch_opens_the_picker_on_that_colour()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        var at = grid.TranslatePoint(new Point(grid.HeaderSize + 2 * grid.Cell + grid.Cell / 2,
                                               grid.HeaderSize + 4 * grid.Cell + grid.Cell / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0x42, grid.Selected);
        Assert.Equal(s0x42(w), w.picker.Bgr);
        // Mounted in the flyout's presenter, which is what proves the popup actually opened.
        Assert.IsType<FlyoutPresenter>(w.picker.Parent);
    }

    /// <summary>
    /// "Layer 3 only" narrows the page to what a 2bpp layer-3 tile can actually name: CGRAM
    /// 00-1F, four wide so each ROW is one of the eight palette groups. Sifting for those 32 in a
    /// 16x16 block of 256 is the problem it exists to remove.
    ///
    /// The shape is load-bearing, not decoration: four-by-eight over that range is group-major
    /// for free, so a swatch's position and its CGRAM number stay the same thing and the picker,
    /// the edit markers and the tooltips need no remapping. A test on the colours alone would
    /// pass with the grid still sixteen wide and the rows meaning nothing.
    /// </summary>
    [AvaloniaFact]
    public void the_palette_page_narrows_to_layer_3s_thirty_two_colours_one_group_per_row()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        Assert.Equal(16, grid.Cols);
        Assert.Equal(256, grid.Count);

        w.GetControl<CheckBox>("PaletteLayer3").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Layer3.PaletteColors, grid.Cols);
        Assert.Equal(Layer3.PaletteGroups, grid.Rows);
        Assert.Equal(Layer3.PaletteSpace, grid.Count);
        // Row g is group g: the swatch at (0, g) is the CGRAM index that group's colour 0 sits at.
        for (int g = 0; g < Layer3.PaletteGroups; g++)
            Assert.Equal(Layer3.PaletteBase(g), g * grid.Cols);

        w.GetControl<CheckBox>("PaletteLayer3").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(256, grid.Count);
    }

    /// <summary>
    /// Pointing at the toggle previews it on the grid: the eight groups it keeps are ringed and
    /// the 224 it drops go under the veil, so the effect is readable without pressing anything.
    /// Once the grid IS narrowed there is nothing being filtered out, so the preview goes away —
    /// otherwise it would ring all eight rows of a view that is only those eight rows.
    /// </summary>
    [AvaloniaFact]
    public void hovering_the_layer_3_toggle_rings_the_colours_it_would_keep()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        Assert.Null(grid.Preview);

        Preview(w, true);
        var runs = grid.Preview!;
        Assert.Equal(Layer3.PaletteGroups, runs.Length);
        Assert.All(runs, r => Assert.Equal(Layer3.PaletteColors, r.Count));
        // Contiguous from 0, so between them the runs are exactly CGRAM 00-1F.
        Assert.Equal(Enumerable.Range(0, Layer3.PaletteGroups).Select(g => g * Layer3.PaletteColors),
                     runs.Select(r => r.Start));
        // Each run carries its group number, drawn in its first swatch — four colours is too few
        // to guess which group you are looking at from position alone once the rings are on.
        Assert.Equal(Enumerable.Range(0, Layer3.PaletteGroups).Select(g => $"{g}"),
                     runs.Select(r => r.Label));

        Preview(w, false);
        Assert.Null(grid.Preview);

        w.GetControl<CheckBox>("PaletteLayer3").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Preview(w, true);
        Assert.Null(grid.Preview);
    }

    private static void Preview(MainWindow w, bool on) => typeof(MainWindow)
        .GetMethod("PreviewLayer3Palette", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(w, [on]);

    /// <summary>
    /// Step-wise undo of palette edits, which the ImGui editor had and the Avalonia port
    /// dropped in favour of a Reset-everything button.
    ///
    /// The case that matters is undoing back past a colour's FIRST edit: the entry has to be
    /// REMOVED, not rewritten with the ROM's own colour, or the swatch keeps its edited marker
    /// and the level counts as touched forever.
    /// </summary>
    [Fact]
    public void palette_edits_undo_one_step_at_a_time()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);

        ushort original = s.PaletteBgr(0x42);
        ushort first = Different(original), second = (ushort)(first ^ 0x3E0);
        Assert.True(s.SetPaletteColor(0x42, first));
        Assert.True(s.SetPaletteColor(0x42, second));
        Assert.Equal(2, s.PaletteUndoDepth);

        Assert.True(s.PaletteUndo());
        Assert.Equal(first, s.PaletteBgr(0x42));

        Assert.True(s.PaletteUndo());
        Assert.Equal(original, s.PaletteBgr(0x42));
        Assert.False(s.IsPaletteEdited(0x42));      // gone, not overwritten with the ROM's colour
        Assert.Equal(0, s.PaletteEditCount);

        Assert.False(s.PaletteUndo());              // nothing left
        Assert.True(s.PaletteRedo());
        Assert.Equal(first, s.PaletteBgr(0x42));
        Assert.True(s.IsPaletteEdited(0x42));
    }

    /// <summary>
    /// A whole picker session is ONE undo entry, however many colours the drag crossed. The
    /// picker fires a change per quantised step, so without this a single drag across the
    /// saturation square would leave thirty entries to press Ctrl+Z through.
    /// </summary>
    [Fact]
    public void a_whole_picker_session_undoes_as_one_step()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);
        ushort original = s.PaletteBgr(0x42);

        s.BeginPaletteStroke();
        for (int i = 1; i <= 8; i++) Assert.True(s.SetPaletteColor(0x42, (ushort)(original ^ (i * 3))));
        Assert.Equal(0, s.PaletteUndoDepth);        // nothing recorded while the picker is open
        s.EndPaletteStroke();

        Assert.Equal(1, s.PaletteUndoDepth);
        Assert.True(s.PaletteUndo());
        Assert.Equal(original, s.PaletteBgr(0x42));
        Assert.False(s.IsPaletteEdited(0x42));
    }

    /// <summary>A picker opened and dismissed without landing anywhere new records nothing —
    /// otherwise every look at a colour would leave an empty entry in the history.</summary>
    [Fact]
    public void a_picker_session_that_changes_nothing_records_nothing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);
        ushort original = s.PaletteBgr(0x42);

        s.BeginPaletteStroke();
        Assert.True(s.SetPaletteColor(0x42, Different(original)));
        Assert.True(s.SetPaletteColor(0x42, original));    // ...and back again
        s.EndPaletteStroke();

        Assert.Equal(0, s.PaletteUndoDepth);
    }

    /// <summary>
    /// The reported bug: pressing Ctrl+Z twice undid once and then REDID the edit. The handler
    /// used to flush a stale "pending colour" before undoing, and after the first undo that
    /// pending value no longer matched the level, so flushing it re-applied the edit and pushed
    /// a fresh entry. Two presses must walk two steps back.
    /// </summary>
    [AvaloniaFact]
    public void ctrl_z_twice_walks_two_steps_back_rather_than_redoing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var session = SessionOf(w);
        ushort original = session.PaletteBgr(0x42);
        ushort first = Different(original), second = (ushort)(first ^ 0x3E0);

        // Two separate picker sessions, as two edits by hand would be.
        foreach (ushort c in new[] { first, second })
        {
            session.BeginPaletteStroke();
            Assert.True(session.SetPaletteColor(0x42, c));
            session.EndPaletteStroke();
        }
        Assert.Equal(second, session.PaletteBgr(0x42));
        Assert.Equal(2, session.PaletteUndoDepth);

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, session.PaletteBgr(0x42));

        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(original, session.PaletteBgr(0x42));   // NOT back to `second`
        Assert.False(session.IsPaletteEdited(0x42));

        // ...and redo still walks forward from there.
        w.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(first, session.PaletteBgr(0x42));
    }

    /// <summary>Reset is one history entry rather than a cliff, so it can be taken back.</summary>
    [Fact]
    public void reset_is_undoable()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);

        ushort target = Different(s.PaletteBgr(0x42));
        Assert.True(s.SetPaletteColor(0x42, target));
        Assert.True(s.ResetPalette());
        Assert.Equal(0, s.PaletteEditCount);

        Assert.True(s.PaletteUndo());
        Assert.Equal(target, s.PaletteBgr(0x42));
        Assert.Equal(1, s.PaletteEditCount);
    }

    /// <summary>Undo history is per level, like the edits themselves — undoing after a switch
    /// would write one level's colours into another's CGRAM.</summary>
    [Fact]
    public void switching_level_drops_the_palette_history()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);
        Assert.True(s.SetPaletteColor(0x42, Different(s.PaletteBgr(0x42))));
        Assert.Equal(1, s.PaletteUndoDepth);

        s.ShowLevel(0x106);
        Assert.Equal(0, s.PaletteUndoDepth);
        Assert.False(s.PaletteUndo());
    }

    /// <summary>
    /// The eyedropper: a composed pixel back to the CGRAM entry it came from. It goes through
    /// the Map16 tile's palette row rather than matching RGB alone, because the same colour sits
    /// in several rows — black is in all of them — and "which entry is this tile using" is the
    /// only answer worth giving.
    /// </summary>
    [Fact]
    public void the_eyedropper_finds_the_cgram_entry_a_pixel_came_from()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla), s.Status);
        s.ShowLevel(0x105);

        // Sample every pixel of a band across the level and check each answer against the
        // composed image: whatever index comes back must hold exactly that colour.
        int found = 0;
        for (int y = 0; y < 400; y += 7)
            for (int x = 0; x < 700; x += 11)
            {
                if (s.SampleCgramIndex(x, y) is not { } idx) continue;
                Assert.Equal(s.Phases[0]![y * s.PxW + x], s.PaletteRgba[idx]);
                found++;
            }
        log.WriteLine($"sampled {found} pixels back to a CGRAM index");
        Assert.True(found > 100, "the eyedropper matched almost nothing");
    }

    /// <summary>Alt+click on the level canvas is the eyedropper, and it must not also select,
    /// band or paint on the way through.</summary>
    [AvaloniaFact]
    public void alt_click_on_the_canvas_picks_the_colour_and_selects_nothing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var canvas = w.GetControl<LevelView>("Canvas");
        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        var session = SessionOf(w);

        // A point well inside the level, in level pixels → screen.
        const int lx = 200, ly = 300;
        var at = canvas.TranslatePoint(new Point(lx * canvas.Zoom, ly * canvas.Zoom), w)!.Value;
        int expected = session.SampleCgramIndex(lx, ly) ?? -1;
        Assert.True(expected >= 0, "nothing to sample at the test point");

        w.MouseDown(at, MouseButton.Left, RawInputModifiers.Alt);
        w.MouseUp(at, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(expected, grid.Selected);
        Assert.True(w.GetControl<ToggleButton>("ModePalette").IsChecked);
        Assert.Empty(session.Edit!.Selection);      // sampling is not selecting
    }

    /// <summary>Palette mode is not an edit mode: entering it must leave the canvas doing
    /// whatever it was doing, unlike the Sprites and Objects tabs.</summary>
    [AvaloniaFact]
    public void the_palette_tab_does_not_change_the_canvas_edit_mode()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = w.GetControl<LevelView>("Canvas");
        var tabs = w.GetControl<TabStrip>("PaletteTabs");

        tabs.SelectedIndex = 1;                     // sprite editing
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Sprites, canvas.Mode);

        w.GetControl<ToggleButton>("ModePalette").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // palette: no opinion
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Sprites, canvas.Mode);
    }
}
