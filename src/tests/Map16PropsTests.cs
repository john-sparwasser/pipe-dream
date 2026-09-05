using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The Map16 properties inspector: what a tile acts as, its palette row, priority and flips.
///
/// These apply to a SELECTION, and the rule that matters is that the controls reflect the first
/// tile and write to all of them — the only sane behaviour when a lasso can cover tiles that
/// disagree — with the whole set landing in one undo entry.
/// </summary>
public class Map16PropsTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    /// <summary>A fresh prepped copy per test: these write definitions and acts-like entries,
    /// and a shared ROM would make the order of the tests matter.</summary>
    private static Map16Edit? Edit()
        => PreppedRom.Fork() is { } mine ? new Map16Edit(Rom.Load(mine), tileset: 1, project: null) : null;

    [Fact]
    public void a_palette_change_applies_to_every_selected_tile_as_one_undo_step()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        int[] tiles = [0x100, 0x101, 0x110, 0x111];
        foreach (int t in tiles) Assert.Null(m16.EnsurePage(t));
        m16.EndStroke();
        int depth = m16.UndoDepth;

        m16.Transform(tiles, w => (ushort)((w.Raw & ~0x1C00) | (5 << 10)));
        Assert.Equal(depth + 1, m16.UndoDepth);            // one entry for the whole block
        foreach (int t in tiles)
            Assert.All(m16.ReadDef(t)!, w => Assert.Equal(5, w.Palette));

        Assert.True(m16.Undo());
        foreach (int t in tiles)
            Assert.All(m16.ReadDef(t)!, w => Assert.NotEqual(5, w.Palette));
    }

    /// <summary>Priority is a per-quadrant bit, so setting it on a tile means setting it on all
    /// four — a checkbox that only touched the top-left would be silently half-applied.</summary>
    [Fact]
    public void priority_applies_to_all_four_quadrants()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x130;
        Assert.Null(m16.EnsurePage(tile));
        m16.EndStroke();

        m16.Transform([tile], w => (ushort)(w.Raw | 0x2000));
        Assert.All(m16.ReadDef(tile)!, w => Assert.True(w.Priority));

        m16.Transform([tile], w => (ushort)(w.Raw & ~0x2000));
        Assert.All(m16.ReadDef(tile)!, w => Assert.False(w.Priority));
    }

    /// <summary>A flip has to swap the quadrant PAIRS and toggle the flip flag. Doing only one
    /// mirrors the arrangement but not the art, or the art but not the arrangement — and either
    /// looks almost right, which is the worst kind of wrong.</summary>
    [Fact]
    public void flipping_swaps_the_quadrants_and_the_flags_together()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x120;
        Assert.Null(m16.EnsurePage(tile));
        // Four distinguishable quadrants, all flags clear.
        for (int q = 0; q < 4; q++) m16.StampQuad(tile, q, (ushort)(0x10 + q));
        m16.EndStroke();

        m16.Flip([tile], vertical: false);
        var def = m16.ReadDef(tile)!;
        // Visual order is TL, TR, BL, BR: a horizontal flip swaps left and right within each row.
        Assert.Equal(0x11, def[0].Raw & 0x3FF);
        Assert.Equal(0x10, def[1].Raw & 0x3FF);
        Assert.Equal(0x13, def[2].Raw & 0x3FF);
        Assert.Equal(0x12, def[3].Raw & 0x3FF);
        Assert.All(def, w => Assert.True((w.Raw & 0x4000) != 0, "the X-flip flag was not toggled"));

        m16.Flip([tile], vertical: false);                 // and it is its own inverse
        var back = m16.ReadDef(tile)!;
        Assert.Equal(0x10, back[0].Raw & 0x3FF);
        Assert.All(back, w => Assert.True((w.Raw & 0x4000) == 0));
    }

    [Fact]
    public void a_vertical_flip_swaps_the_rows_and_toggles_the_y_flag()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x121;
        Assert.Null(m16.EnsurePage(tile));
        for (int q = 0; q < 4; q++) m16.StampQuad(tile, q, (ushort)(0x20 + q));
        m16.EndStroke();

        m16.Flip([tile], vertical: true);
        var def = m16.ReadDef(tile)!;
        Assert.Equal(0x22, def[0].Raw & 0x3FF);            // bottom row moves to the top
        Assert.Equal(0x23, def[1].Raw & 0x3FF);
        Assert.Equal(0x20, def[2].Raw & 0x3FF);
        Assert.Equal(0x21, def[3].Raw & 0x3FF);
        Assert.All(def, w => Assert.True((w.Raw & 0x8000) != 0, "the Y-flip flag was not toggled"));
    }

    /// <summary>Acts-like is an FG concept and needs Lunar Magic's table: BG tiles have no entry
    /// at all, and writing one would run past the end of the table.</summary>
    [Fact]
    public void acts_like_is_refused_for_bg_tiles()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        log.WriteLine($"acts-like table present: {m16.HasActsAs}");
        Assert.Null(m16.ActsAs(0x4000));
        Assert.False(m16.SetActsAs([0x4000], 0x130));
    }

    [Fact]
    public void acts_like_round_trips_for_fg_tiles()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        if (!m16.HasActsAs) { log.WriteLine("SKIP: base has no LM acts-like table"); return; }

        int was = m16.ActsAs(0x100)!.Value;
        Assert.True(m16.SetActsAs([0x100], 0x130));
        Assert.Equal(0x130, m16.ActsAs(0x100));
        Assert.False(m16.SetActsAs([0x100], 0x130));       // already says that

        // Behaviour is undone and redone like art, one entry per change.
        Assert.True(m16.Undo());
        Assert.Equal(was, m16.ActsAs(0x100));
        Assert.True(m16.Redo());
        Assert.Equal(0x130, m16.ActsAs(0x100));
        Assert.Empty(m16.CommittedTiles!);                 // and still reports nothing visual

        // The value is masked to the table's 14 bits rather than overflowing into the next entry.
        Assert.True(m16.SetActsAs([0x100], 0xFFFF));
        Assert.Equal(0x3FFF, m16.ActsAs(0x100));
    }

    /// <summary>Hovering a tile on the sheet names its behaviour in a card at the canvas corner,
    /// which goes away with the pointer.</summary>
    [AvaloniaFact]
    public void hovering_a_tile_shows_what_it_acts_as_in_the_corner_card()
    {
        if (PreppedRom.Path is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<Avalonia.Controls.Primitives.ToggleButton>("ModeMap16")
         .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        var tip = w.GetControl<Border>("M16ActsTip");
        var text = w.GetControl<TextBlock>("M16ActsTipText");
        Assert.False(tip.IsVisible);

        var name = w.GetControl<TextBlock>("M16TileTipText");

        w.MouseMove(sheet.TranslatePoint(new Point(4, 4), w)!.Value);      // tile 0x000: LM calls it animated water
        Dispatcher.UIThread.RunJobs();
        Assert.True(tip.IsVisible);
        log.WriteLine(text.Text);
        Assert.Matches("^[0-9A-F]{3}( - .+)?$", text.Text);                // the ID, then the table's word if it has one
        Assert.Equal("Water with an animated surface.", name.Text);

        // Tile 0x02B (row 2, column 11) is the coin in every tileset, and the card says so.
        w.MouseMove(sheet.TranslatePoint(new Point(11 * 16 * sheet.Zoom + 4, 2 * 16 * sheet.Zoom + 4), w)!.Value);
        Dispatcher.UIThread.RunJobs();
        Assert.True(name.IsVisible);
        Assert.StartsWith("An ordinary coin", name.Text);
        Assert.Equal(HorizontalAlignment.Right, tip.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Bottom, tip.VerticalAlignment);

        // With Alt (or Cmd) held the card leaves the corner for the tile's upper right, so it
        // can be read while sweeping: its left edge past the tile's right edge, its bottom
        // above the tile's top. Let go and it is back in the corner.
        var coin = sheet.TranslatePoint(new Point(11 * 16 * sheet.Zoom + 4, 2 * 16 * sheet.Zoom + 4), w)!.Value;
        w.MouseMove(coin, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        var desk = w.GetControl<Panel>("Map16Desk");
        var tileTopRight = sheet.TranslatePoint(new Point(12 * 16 * sheet.Zoom, 2 * 16 * sheet.Zoom), desk)!.Value;
        Assert.Equal(HorizontalAlignment.Left, tip.HorizontalAlignment);
        Assert.True(tip.Margin.Left >= tileTopRight.X);
        Assert.True(desk.Bounds.Height - tip.Margin.Bottom <= tileTopRight.Y);
        w.MouseMove(coin);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(HorizontalAlignment.Right, tip.HorizontalAlignment);
        Assert.Equal(new Thickness(14), tip.Margin);

        w.MouseMove(new Point(1, 1));                         // off the sheet
        Dispatcher.UIThread.RunJobs();
        Assert.False(tip.IsVisible);
    }

    /// <summary>The acts-as field commits as soon as a whole value is typed, and a shorter entry
    /// still lands on the tile it was typed for when the user clicks another tile — the click
    /// reloads the row before the field loses focus, which used to throw the entry away.</summary>
    [AvaloniaFact]
    public void typing_an_acts_as_value_applies_it_and_clicking_away_keeps_it()
    {
        if (PreppedRom.Fork() is not { } p) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = p;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ToggleButton>("ModeMap16").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var m16 = SessionOf(w).Map16!;
        if (!m16.HasActsAs) { log.WriteLine("SKIP: base has no LM acts-like table"); return; }

        var sheet = w.GetControl<Map16CanvasView>("Map16Canvas");
        double ts = 16 * sheet.Zoom;
        Point Tile(int col, int row) => sheet.TranslatePoint(new Point(col * ts + 8, row * ts + 8), w)!.Value;
        w.MouseDown(Tile(3, 2), MouseButton.Left);
        w.MouseUp(Tile(3, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x23, sheet.SelectedTile);

        var acts = w.GetControl<TextBox>("M16Acts");
        acts.Focus();
        acts.Text = "130";                                   // three digits: a whole value, committed now
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x130, m16.ActsAs(0x23));

        acts.Text = "25";                                    // short, so it waits...
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x130, m16.ActsAs(0x23));
        w.MouseDown(Tile(4, 2), MouseButton.Left);           // ...and a click elsewhere lands it
        w.MouseUp(Tile(4, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x24, sheet.SelectedTile);
        Assert.Equal(0x025, m16.ActsAs(0x23));               // on the tile it was typed for
        Assert.Equal($"{m16.ActsAs(0x24):X3}", acts.Text);   // and the row now shows the new tile
    }

    private static EditorSession SessionOf(MainWindow w) => (EditorSession)typeof(MainWindow)
        .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    /// <summary>A custom tile has no sentence of its own in the table, so the card borrows the
    /// one for whatever it acts as.</summary>
    [Fact]
    public void a_custom_tile_describes_as_what_it_acts_as()
    {
        Assert.Equal("", Map16Tiles.Describe(0x305, 1));
        Assert.Contains("cement", Map16Tiles.Describe(0x130, 1));
    }
}
