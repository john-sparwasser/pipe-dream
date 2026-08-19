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

        w.GetControl<TabStrip>("PaletteTabs").SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.GetControl<DockPanel>("PalettePanel").IsVisible);

        var grid = w.GetControl<PaletteGridView>("PaletteGrid");
        var r = w.GetControl<Slider>("PalR");
        var g = w.GetControl<Slider>("PalG");
        var b = w.GetControl<Slider>("PalB");

        // Five bits per channel is what the hardware stores, so that is the slider range.
        Assert.Equal(31, r.Maximum);
        Assert.Equal(31, g.Maximum);
        Assert.Equal(31, b.Maximum);

        // Click swatch 0x42 (row 4, column 2) the way the user would. Picking it loads the
        // sliders and must NOT count as an edit on its own.
        var at = grid.TranslatePoint(new Point(2 * grid.Cell + grid.Cell / 2,
                                               4 * grid.Cell + grid.Cell / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x42, grid.Selected);
        Assert.Contains("0x42", w.GetControl<TextBlock>("PaletteIndex").Text!);
        Assert.DoesNotContain("edit(s)", w.GetControl<TextBlock>("PaletteNote").Text!);

        // Moving a slider previews immediately and commits after a pause: a commit is a full
        // recompose, so it must not fire on every step of a drag.
        double before = r.Value;
        r.Value = before == 31 ? 0 : 31;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("edit(s)", w.GetControl<TextBlock>("PaletteNote").Text!);
        Assert.Equal(EditorSession.Rgba((ushort)((int)b.Value << 10 | (int)g.Value << 5 | (int)r.Value)),
                     grid.Colors[0x42]);          // ...but the swatch already shows it

        w.CommitPaletteEdit();                      // what the debounce timer does on its tick
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("edit(s)", w.GetControl<TextBlock>("PaletteNote").Text!);
        Assert.True(w.GetControl<PaletteGridView>("PaletteGrid").Colors[0x42] != default);
    }

    /// <summary>The Palette tab is not an edit mode: opening it must leave the canvas doing
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

        tabs.SelectedIndex = 3;                     // palette: no opinion
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Sprites, canvas.Mode);
    }
}
