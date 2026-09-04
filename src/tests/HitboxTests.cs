using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Hitboxes come from SMW's own collision classes and slope tables (Hitboxes.cs cites the code).
/// Pinned against the vanilla ROM: the classes by acts-as range, the hurting blocks by tileset,
/// and a few slopes whose shape is known from the game.
/// </summary>
public class HitboxTests(ITestOutputHelper log)
{
    private static Rom? Vanilla()
    {
        string p = Path.Combine(Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
                                ".resources", "SMW.smc");
        return File.Exists(p) ? Rom.Load(p) : null;
    }

    [Fact]
    public void the_classes_follow_the_acts_as_ranges()
    {
        if (Vanilla() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Equal(HitKind.None, Hitboxes.Of(rom, 0x025, 0).Kind);       // air
        Assert.Equal(HitKind.None, Hitboxes.Of(rom, 0x02B, 0).Kind);       // coin: not a wall
        Assert.Equal(HitKind.Ledge, Hitboxes.Of(rom, 0x100, 0).Kind);
        Assert.Equal(HitKind.Ledge, Hitboxes.Of(rom, 0x110, 0).Kind);
        Assert.Equal(HitKind.Solid, Hitboxes.Of(rom, 0x111, 0).Kind);
        Assert.Equal(HitKind.Solid, Hitboxes.Of(rom, 0x130, 0).Kind);      // cement
        Assert.Equal(HitKind.Slope, Hitboxes.Of(rom, 0x16E, 0).Kind);
        Assert.Equal(HitKind.Slope, Hitboxes.Of(rom, 0x1D7, 0).Kind);
        Assert.Equal(HitKind.SlopeTop, Hitboxes.Of(rom, 0x1D8, 0).Kind);
        Assert.Equal(HitKind.None, Hitboxes.Of(rom, 0x200, 0).Kind);       // beyond the game's own table
    }

    [Fact]
    public void what_hurts_depends_on_the_tileset_the_way_the_game_checks()
    {
        if (Vanilla() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.True(Hitboxes.Of(rom, 0x12F, 0).Hurts);                     // muncher, anywhere
        Assert.False(Hitboxes.Of(rom, 0x130, 0).Hurts);
        Assert.True(Hitboxes.Of(rom, 0x159, 1).Hurts);                     // castle spikes
        Assert.True(Hitboxes.Of(rom, 0x15A, 5).Hurts);                     // ghost house spikes
        Assert.False(Hitboxes.Of(rom, 0x159, 0).Hurts);                    // the same number in Normal 1 is scenery
        Assert.True(Hitboxes.Of(rom, 0x166, 1).Hurts);
        Assert.False(Hitboxes.Of(rom, 0x166, 0).Hurts);                    // a bush
    }

    /// <summary>The first slope tile is the gentle rise: ground from row 15 at the left to row 12
    /// at the right, four columns a step. Its 45° cousin runs the whole tile.</summary>
    [Fact]
    public void slope_surfaces_come_from_the_roms_tables()
    {
        if (Vanilla() is not { } rom) { log.WriteLine("SKIP: no ROM"); return; }
        var gentle = Hitboxes.Of(rom, 0x16E, 0).Surface!;
        Assert.Equal([15, 15, 15, 15, 14, 14, 14, 14, 13, 13, 13, 13, 12, 12, 12, 12], gentle.Select(b => (int)b));

        var steep45 = Hitboxes.Of(rom, 0x1AA, 0).Surface!;                 // type 0C: 15 down to 0
        Assert.Equal(Enumerable.Range(0, 16).Select(x => 15 - x), steep45.Select(b => (int)b));

        // The very steep pair: the lower tile is a full column everywhere its surface is above
        // it, and the tile over it carries that surface — 45° again, from the lower one's table.
        // Tileset 1 reads the $E55E table; tilesets 0 and 7 have their own, which differs in
        // exactly this stretch of tiles.
        int lower = 0x16E + Array.IndexOf(Enumerable.Range(0, 0x6A).Select(i => rom.ReadByte(0x00E55E + i)).ToArray(), (byte)0x12);
        Assert.All(Hitboxes.Of(rom, lower, 1).Surface!, b => Assert.Equal(0, b));
        var top = Hitboxes.Above(rom, lower, 1);
        Assert.Equal(HitKind.Slope, top.Kind);
        Assert.Equal(Enumerable.Range(0, 16).Select(x => 15 - x), top.Surface!.Select(b => (int)b));
        Assert.NotEqual(Hitboxes.Of(rom, lower, 1).Surface, Hitboxes.Of(rom, lower, 0).Surface);

        // Nothing above a non-slope.
        Assert.Equal(HitKind.None, Hitboxes.Above(rom, 0x130, 0).Kind);
    }

    /// <summary>Both bars carry a Hitboxes toggle, and each arms its own canvas — the level's
    /// resolving cells, the sheet's resolving tiles — and disarms it again.</summary>
    [AvaloniaFact]
    public void the_hitbox_toggles_arm_their_canvases()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var level = w.GetControl<LevelView>("Canvas");
        var toggle = w.GetControl<ToggleButton>("HitboxesToggle");
        Assert.Null(level.Hitboxes);
        toggle.IsChecked = true;
        Assert.NotNull(level.Hitboxes);
        Assert.Equal(HitKind.None, level.Hitboxes!(-1, -1).Kind);          // off the grid is air, not a crash
        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var m16 = w.GetControl<ToggleButton>("M16Hitboxes");
        Assert.True(m16.IsChecked);                                        // one setting, both buttons
        Assert.NotNull(sheet.Hitboxes);
        Assert.Equal(HitKind.Solid, sheet.Hitboxes!(0x130).Kind);
        Assert.Equal(HitKind.Ledge, sheet.Hitboxes!(0x100).Kind);

        m16.IsChecked = false;                                             // off from the other bar
        Assert.False(toggle.IsChecked);
        Assert.Null(level.Hitboxes);
        Assert.Null(sheet.Hitboxes);
    }
}
