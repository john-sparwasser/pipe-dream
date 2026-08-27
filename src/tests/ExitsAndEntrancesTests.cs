using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Screen exits and secondary entrances.
///
/// Exits are objects in the layer-1 stream that draw NO tiles, so they are invisible on the
/// canvas and this list is the only way to reach them — which makes the round trip (read a
/// level's exits, edit, write them back, read the same values) the thing worth pinning.
///
/// The two halves are stored quite differently and that is easy to get wrong: an exit lives in
/// the level's own object stream, while the entrance record it points at is GLOBAL, written
/// straight into the ROM with only its index recorded in the project.
/// </summary>
public class ExitsAndEntrancesTests(ITestOutputHelper log) : IDisposable
{
    private readonly ITestOutputHelper log = log;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "pduiexit-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(Vanilla);

    private static EditorSession? Open(int level = 0x105)
    {
        if (!HaveRom) return null;
        var s = new EditorSession();
        if (!s.OpenRom(Vanilla)) return null;
        s.ShowLevel(level);
        return s;
    }

    [Fact]
    public void exits_round_trip_through_the_object_stream()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var e = s.Edit!;

        var exits = e.ReadExits();
        log.WriteLine($"level $105 has {exits.Count} exit(s)");

        // Add one and read it back — a fresh exit sits on the screen it governs.
        exits.Add(new LevelExit { Screen = 3, Destination = 0x25, Secondary = true });
        Assert.True(e.WriteExits(exits));

        var again = e.ReadExits();
        Assert.Equal(exits.Count, again.Count);
        var added = again.Single(x => x.Screen == 3 && x.Destination == 0x25);
        Assert.True(added.Secondary);
        Assert.False(added.Water);
        Assert.False(added.LmForm);
    }

    /// <summary>The whole table is one undo step: retyping destinations must not cost one undo
    /// entry per keystroke.</summary>
    [Fact]
    public void applying_the_table_is_a_single_undo_step()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var e = s.Edit!;
        int depth = e.UndoDepth;

        var exits = e.ReadExits();
        exits.Add(new LevelExit { Screen = 5, Destination = 0x11 });
        exits.Add(new LevelExit { Screen = 6, Destination = 0x12 });
        Assert.True(e.WriteExits(exits));
        Assert.Equal(depth + 1, e.UndoDepth);

        Assert.True(e.Undo());
        Assert.DoesNotContain(e.ReadExits(), x => x.Screen == 5 && x.Destination == 0x11);
    }

    [Fact]
    public void an_unchanged_table_records_nothing()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var e = s.Edit!;
        int depth = e.UndoDepth;
        Assert.False(e.WriteExits(e.ReadExits()));    // read and write back untouched
        Assert.Equal(depth, e.UndoDepth);
    }

    /// <summary>Removing every exit is a real edit, not a no-op — a level can legitimately have
    /// none.</summary>
    [Fact]
    public void an_emptied_table_removes_the_exit_objects()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var e = s.Edit!;
        if (e.ReadExits().Count == 0) { log.WriteLine("SKIP: level has no exits to remove"); return; }

        Assert.True(e.WriteExits([]));
        Assert.Empty(e.ReadExits());
        Assert.True(e.Undo());
        Assert.NotEmpty(e.ReadExits());
    }

    // ---- secondary entrances ----

    [Fact]
    public void an_entrance_write_lands_in_the_rom_and_is_recorded_in_the_project()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
        s.ShowLevel(0x105);

        const int index = 0x0BB;
        var before = s.ReadEntrance(index)!.Value;
        var edited = before with { MarioX = (before.MarioX + 1) & 7, DestinationLevel = 0x25 };

        Assert.True(s.WriteEntrance(index, edited));
        Assert.Equal(edited, s.ReadEntrance(index));
        Assert.False(s.WriteEntrance(index, edited));      // already says that

        // The index is captured in the project; the BYTES are re-read from the ROM at save time,
        // which is what makes undo and redo need no extra bookkeeping.
        s.Save();
        var reopened = Project.Open(s.Project!.FilePath);
        Assert.Contains(index.ToString("X3"), reopened.Data.Entrances.Keys);
    }

    /// <summary>The index is 9 bits: an exit gives the low byte and bit 8 comes from the submap
    /// flag, so $0BB and $1BB are different records reached by the same exit byte.</summary>
    [Fact]
    public void the_submap_pair_is_a_different_record()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var main = s.ReadEntrance(0x0BB)!.Value;
        var sub = s.ReadEntrance(0x1BB)!.Value;

        Assert.True(s.WriteEntrance(0x0BB, main with { MarioY = (main.MarioY + 3) & 15 }));
        Assert.Equal(sub, s.ReadEntrance(0x1BB));          // the pair is untouched
    }

    [Fact]
    public void an_out_of_range_index_is_refused_rather_than_wrapping()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        Assert.Null(s.ReadEntrance(-1));
        Assert.Null(s.ReadEntrance(EditorSession.SecondaryEntranceCount));
        Assert.False(s.WriteEntrance(EditorSession.SecondaryEntranceCount, default));
    }

    // ---- through the window ----

    [AvaloniaFact]
    public void the_exits_window_stages_rows_and_applies_them_together()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var exits = new List<LevelExit>
        {
            new() { Screen = 1, Destination = 0x20 },
            new() { Screen = 2, Destination = 0x21, Secondary = true },
        };
        var w = new LevelExitsWindow(exits);
        w.Show();
        Dispatcher.UIThread.RunJobs();

        // Nothing is committed until Apply, and Apply hands back the whole table at once.
        Assert.Null(w.Applied);
        w.GetControl<Button>("ApplyButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(w.Applied);
        Assert.Equal(2, w.Applied!.Count);
        Assert.Equal(0x20, w.Applied[0].Destination);
        Assert.True(w.Applied[1].Secondary);
    }

    /// <summary>The everyday route: a canvas MODE rather than a table. Arming it takes the
    /// canvas away from whichever layer was being edited, hands the view the exit table to badge
    /// the screens with, and turns a click into "this screen" — which is what the small
    /// destination prompt hangs off.</summary>
    [AvaloniaFact]
    public void exits_mode_takes_the_canvas_over_from_the_layer_toggles()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var toggle = w.GetControl<ToggleButton>("ExitsMode");
        var canvas = w.GetControl<LevelView>("Canvas");
        var layerOne = w.GetControl<ToggleButton>("LayerOne");

        toggle.IsChecked = true;
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LevelView.EditMode.Exits, canvas.Mode);
        Assert.False(layerOne.IsEnabled);      // the layer being edited is not in play
        log.WriteLine($"badging {canvas.Exits.Count} exit(s)");

        // A drawer tab must NOT take the canvas back — only the toggle does.
        w.GetControl<TabStrip>("PaletteTabs").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Exits, canvas.Mode);

        toggle.IsChecked = false;
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(LevelView.EditMode.Exits, canvas.Mode);
        Assert.True(layerOne.IsEnabled);
        Assert.Empty(canvas.Exits);            // nothing to draw once the mode is gone
    }

    /// <summary>A left click on the canvas reports the screen it landed on — the hook the
    /// destination prompt hangs off. Nothing else in the canvas may act on that click.</summary>
    [AvaloniaFact]
    public void a_click_in_exits_mode_reports_the_screen_and_edits_nothing()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }

        // The view on its own, not the window: MainWindow answers this event with a MODAL
        // prompt, which a headless click would sit in forever.
        var bitmap = new LevelBitmap();
        bitmap.SetImages(s.Phases, s.PxW, s.PxH, 0);
        var canvas = new LevelView { Source = bitmap, Edit = s.Edit, Mode = LevelView.EditMode.Exits };
        var w = new Window { Content = canvas, Width = 900, Height = 600 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        int? got = null;
        canvas.ExitScreenClicked += (_, screen) => got = screen;

        // Cell (20, 4) is screen 1: screens are 16 cells wide.
        var at = canvas.TranslatePoint(new Point(20 * 16 * canvas.Zoom + 8, 4 * 16 * canvas.Zoom + 8), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, got);
        Assert.Empty(s.Edit!.Selection);           // no object was picked up on the way
    }

    /// <summary>A destination typed as a whole level number keeps the byte the ROM reads. It
    /// used to be CLAMPED, so $105 landed as $FF — a destination nobody asked for. Bit 8 is not
    /// this field's to carry: it comes from the submap the player is on.</summary>
    [AvaloniaFact]
    public void a_full_level_number_keeps_its_low_byte_instead_of_pinning_to_ff()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = new LevelExitsWindow([new LevelExit { Screen = 1, Destination = 0x20 }]);
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var dest = w.GetVisualDescendants().OfType<TextBox>().Skip(1).First();
        dest.Text = "105";
        w.GetControl<Button>("ApplyButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0x05, w.Applied![0].Destination);
    }

    /// <summary>
    /// Entrances mode marks every place the level puts Mario, and a marker can be dragged. It
    /// does NOT land where it was dropped: the ROM stores a screen and two indices, so the drop
    /// snaps to the nearest spot that can be expressed — and the marker comes back at that spot,
    /// not the cursor's.
    /// </summary>
    [AvaloniaFact]
    public void an_entrance_marker_drags_to_the_nearest_spot_the_rom_can_store()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var before = s.Entrances();
        log.WriteLine($"level $105: {string.Join(", ", before.Select(e => $"{e.Label} @ {e.X:X3},{e.Y:X3}"))}");
        Assert.Contains(before, e => e.Kind == EntranceKind.Main);
        Assert.Contains(before, e => e.Kind == EntranceKind.Midway);

        // Drop the main entrance somewhere arbitrary and read back where it actually landed.
        var main = before.First(e => e.Kind == EntranceKind.Main);
        Assert.True(s.MoveEntrance(EntranceKind.Main, main.Index, 0x347, 0x0C4));

        var moved = s.Entrances().First(e => e.Kind == EntranceKind.Main);
        Assert.Equal(3, moved.X >> 8);                       // the screen it was dropped on
        Assert.NotEqual(0x347, moved.X);                     // ...but snapped within it
        Assert.Equal(EntrancePlacement.Y(Rom.Load(Vanilla), EntrancePlacement.NearestY(Rom.Load(Vanilla), 0x0C4)),
                     moved.Y);
        // Idempotent: dropping it exactly where it already is changes nothing.
        Assert.False(s.MoveEntrance(EntranceKind.Main, moved.Index, moved.X, moved.Y));
    }

    /// <summary>Vanilla's midway entrance stores ONLY a screen — its position inside that screen
    /// is the main entrance's. So a midway marker moves a screen at a time, and dragging it
    /// vertically has nowhere to be written.</summary>
    [AvaloniaFact]
    public void a_midway_marker_can_only_change_its_screen()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var mid = s.Entrances().First(e => e.Kind == EntranceKind.Midway);
        Assert.True(mid.ScreenOnly);

        Assert.True(s.MoveEntrance(EntranceKind.Midway, mid.Index, 0x512, 0x180));
        var after = s.Entrances().First(e => e.Kind == EntranceKind.Midway);
        var main = s.Entrances().First(e => e.Kind == EntranceKind.Main);
        Assert.Equal(5, after.X >> 8);                       // the screen moved...
        Assert.Equal(main.X & 0xFF, after.X & 0xFF);         // ...the offset inside it did not
        Assert.Equal(main.Y, after.Y);                       // and Y is the main entrance's
    }

    /// <summary>Entrances and Exits are the two halves of a connection and both want the canvas,
    /// so they are exclusive with each other as well as with the layer being edited.</summary>
    [AvaloniaFact]
    public void the_two_overlay_modes_hand_the_canvas_to_each_other()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = Vanilla;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var exits = w.GetControl<ToggleButton>("ExitsMode");
        var entrances = w.GetControl<ToggleButton>("EntrancesMode");
        var canvas = w.GetControl<LevelView>("Canvas");
        var layerOne = w.GetControl<ToggleButton>("LayerOne");
        void Click(ToggleButton b, bool on)
        {
            b.IsChecked = on;
            b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        Click(exits, true);
        Assert.Equal(LevelView.EditMode.Exits, canvas.Mode);

        Click(entrances, true);                       // arming one disarms the other
        Assert.Equal(LevelView.EditMode.Entrances, canvas.Mode);
        Assert.False(exits.IsChecked);
        Assert.False(layerOne.IsEnabled);             // the layer is still not in play
        Assert.Empty(canvas.Exits);                   // and the exit badges are gone with it

        Click(entrances, false);
        Assert.NotEqual(LevelView.EditMode.Entrances, canvas.Mode);
        Assert.True(layerOne.IsEnabled);
    }

    /// <summary>The badge is a link, not decoration: a click ON it says "follow this exit" and
    /// a click anywhere else on the same screen still means "edit this exit". Both come out of
    /// the same press, so the badge has to win.</summary>
    [AvaloniaFact]
    public void the_destination_badge_takes_the_click_before_the_screen_under_it()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }

        var bitmap = new LevelBitmap();
        bitmap.SetImages(s.Phases, s.PxW, s.PxH, 0);
        var canvas = new LevelView
        {
            Source = bitmap, Edit = s.Edit, Mode = LevelView.EditMode.Exits, Zoom = 1,
            Exits = [(Screen: 1, Dest: 0x105, LmForm: false)],
        };
        var w = new Window { Content = canvas, Width = 900, Height = 600 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        // The badges are a RENDER artifact — they exist where they were last drawn, which is the
        // only place a click can sensibly test against. No frame, no badges.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        int? followed = null, edited = null;
        canvas.ExitBadgeClicked += (_, screen) => followed = screen;
        canvas.ExitScreenClicked += (_, screen) => edited = screen;

        var badge = Assert.Single(canvas.Badges);
        Assert.Equal(1, badge.Screen);
        // It belongs to screen 1's top-right corner: screens are 16 cells of 16px.
        double right = 2 * 16 * 16 * canvas.Zoom;
        Assert.InRange(badge.Box.Right, right - 40, right);
        var onBadge = canvas.TranslatePoint(badge.Box.Center, w)!.Value;
        w.MouseDown(onBadge, MouseButton.Left);
        w.MouseUp(onBadge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, followed);
        Assert.Null(edited);

        // The same screen, well clear of the badge, still opens the editor.
        followed = null;
        var onScreen = canvas.TranslatePoint(new Point(badge.Box.X - 40, badge.Box.Bottom + 80), w)!.Value;
        w.MouseDown(onScreen, MouseButton.Left);
        w.MouseUp(onScreen, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, edited);
        Assert.Null(followed);
    }

    /// <summary>A click reports the SCREEN it landed on, which is what the exit table is keyed
    /// by. Vertical levels stack their screens down the same column, so the axis swaps.</summary>
    [AvaloniaFact]
    public void a_cell_maps_to_the_screen_that_owns_it()
    {
        var v = new LevelView();
        Assert.Equal(0, v.ScreenOf((15, 20)));
        Assert.Equal(1, v.ScreenOf((16, 0)));
        Assert.Equal(3, v.ScreenOf((60, 5)));

        v.Vertical = true;
        Assert.Equal(0, v.ScreenOf((15, 15)));
        Assert.Equal(2, v.ScreenOf((3, 32)));
    }

    [AvaloniaFact]
    public void cancelling_the_exits_window_applies_nothing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        var w = new LevelExitsWindow([new LevelExit { Screen = 1, Destination = 0x20 }]);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<Button>("CancelButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(w.Applied);
    }
}
