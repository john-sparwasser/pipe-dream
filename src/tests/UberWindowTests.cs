using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using PipeDream.Services;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>UberASM's window and the level's line in Level Properties (reference/UBERASM_PORT.md, phase 3).</summary>
public class UberWindowTests
{
    private static readonly RoutedEventArgs Click = new(Button.ClickEvent);

    private static EditorSession Project()
    {
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(Directory.CreateTempSubdirectory("pd-uberwin-").FullName, "p"), ReferenceRoms.Vanilla), s.Status);
        s.NewUberFile("level/waves", out _);
        s.NewUberFile("level/star.asm", out _);
        s.NewUberFile("gamemode/gm.asm", out _);
        return s;
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void lines_are_set_in_list_txts_own_syntax()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        Assert.Equal("level 105 runs level/waves.asm, -level/star.asm", s.SetUberLine("level", 0x105, "waves.asm : 01 (@300), -star.asm"));
        Assert.Equal("waves.asm : 01 2C 01, -star.asm", s.UberLine("level", 0x105));
        Assert.Equal("\"zz\" is not an extra byte", s.SetUberLine("level", 0x105, "waves.asm : zz"));
        Assert.Equal("level/gone.asm is not in the project", s.SetUberLine("level", 0x106, "gone.asm"));
        Assert.Equal("level 105 runs nothing", s.SetUberLine("level", 0x105, ""));
        Assert.Equal("a file is in level/, gamemode/, overworld/, library/, routines/ or other/ (\"level/waves.asm\")", s.NewUberFile("waves.asm", out _));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void run_in_adds_a_file_to_the_levels_it_names()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        Assert.Equal("level/waves.asm runs in level 105", s.RunUberFileIn("level/waves.asm", "105"));
        Assert.Equal("level/star.asm runs in level 105, 106, *", s.RunUberFileIn("level/star.asm", "105, 106 *"));
        s.RunUberFileIn("level/waves.asm", "105");                                       // already there: not twice
        Assert.Equal("waves.asm, star.asm", s.UberLine("level", 0x105));
        Assert.Equal("gamemode/gm.asm runs in gamemode 14", s.RunUberFileIn("gamemode/gm.asm", "14"));
        Assert.Equal("\"200\" is not a level number (hex) or *", s.RunUberFileIn("level/waves.asm", "200"));
        Assert.Equal("which levels? A number (hex), several, or *", s.RunUberFileIn("level/waves.asm", " "));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_window_lists_files_and_each_kinds_lines()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        s.SetUberLine("level", UberEntry.All, "star.asm");
        s.SetUberLine("level", 0x105, "waves.asm");
        s.SetUberLine("gamemode", 0x14, "gm.asm");
        var w = new UberAsmWindow(s);
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var files = ((System.Collections.IEnumerable)w.GetControl<ListBox>("FileList").ItemsSource!).Cast<UberAsmWindow.FileRow>().ToList();
        Assert.Equal([("gamemode/gm.asm", "game mode 14"), ("level/star.asm", "level *"), ("level/waves.asm", "level 105")], files.Select(f => (f.Path, f.RunsIn)));
        var lines = w.GetControl<ListBox>("LineList");
        Assert.Equal([("*", "star.asm"), ("105", "waves.asm")], ((System.Collections.IEnumerable)lines.ItemsSource!).Cast<UberAsmWindow.LineRow>().Select(l => (l.Where, l.Line)));
        w.GetControl<ComboBox>("KindBox").SelectedIndex = 1;                         // game modes
        Assert.Equal(["14"], ((System.Collections.IEnumerable)lines.ItemsSource!).Cast<UberAsmWindow.LineRow>().Select(l => l.Where));

        Assert.False(w.GetControl<Button>("RemoveLineButton").IsEnabled);
        lines.SelectedIndex = 0;
        w.GetControl<Button>("RemoveLineButton").RaiseEvent(Click);
        Assert.Empty(s.UberNumbers("gamemode"));
        Assert.Equal("gamemode 14 runs nothing", w.GetControl<TextBlock>("Hint").Text);
        w.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void level_properties_shows_the_levels_line_and_hands_back_the_edit()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var rom = Rom.Load(ReferenceRoms.Vanilla);
        var scene = LevelScene.Build(rom, 0x105, LevelScene.SpriteDraw.Skip);
        var dlg = new LevelPropertiesWindow(scene.Level.Header, rom.ReadMainEntrance(0x105), false, LevelChoices.Default, ("waves.asm", "star.asm"));
        dlg.Show();
        var box = dlg.GetLogicalDescendants().OfType<TextBox>().Single();
        Assert.Equal("waves.asm", box.Text);
        Assert.Contains(dlg.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "and before it, as every level: star.asm");
        box.Text = "waves.asm : 02 ";
        dlg.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Apply").RaiseEvent(Click);
        Assert.Equal("waves.asm : 02", dlg.AppliedUber);
    }
}
