using Avalonia.Controls;
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
