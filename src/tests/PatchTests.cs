using Avalonia.Controls;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The project's Asar patches (reference/PATCHES.md): kept in the .pdp with the files they
/// include, applied at build in list order when enabled, and reported — not refused — when they
/// write over bytes something else wrote.
/// </summary>
public class PatchTests
{
    private static ProjectFile.PatchEntry Patch(string name, string asm, bool enabled = true) =>
        new() { Name = name, Source = asm, Enabled = enabled };

    /// <summary>A project on a 1MB test ROM, used as provided (prep version 0).</summary>
    private static Project NewProject()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName, rom = Path.Combine(dir, "base.sfc");
        File.WriteAllBytes(rom, TestRom.Create(size: 0x100000).Data);
        return Project.Create(Path.Combine(dir, "proj"), rom);
    }

    [Fact]
    public void a_patch_and_its_files_round_trip_through_the_project_file()
    {
        var data = new ProjectFile();
        var p = Patch("fast.asm", "org $008000\ndb $01\n", enabled: false);
        p.Files["tables/data.bin"] = [0x00, 0xFF, 0x80];
        data.Patches.Add(p);
        data.Patches.Add(Patch("second.asm", ""));

        var back = ProjectFile.FromJson(data.ToJson());

        Assert.Equal(["fast.asm", "second.asm"], back.Patches.Select(x => x.Name));   // order kept
        Assert.False(back.Patches[0].Enabled);
        Assert.Equal(p.Source, back.Patches[0].Source);
        Assert.Equal([0x00, 0xFF, 0x80], back.Patches[0].Files["tables/data.bin"]);
        Assert.Empty(ProjectFile.FromJson("{}").Patches);                             // an older .pdp
    }

    [Fact]
    public void importing_a_patch_carries_what_it_includes()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-patch-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "main.asm"), """
            incsrc "sub/defs.asm"
            incsrc ../outside.asm
            incbin gfx.bin:0-10
            incsrc "!path/made.asm"
            """);
        File.WriteAllText(Path.Combine(dir, "sub", "defs.asm"), "incsrc more.asm\nincbin \"tab.bin\"\n");   // relative to sub/
        File.WriteAllText(Path.Combine(dir, "sub", "more.asm"), "; more\n");
        File.WriteAllBytes(Path.Combine(dir, "sub", "tab.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "gfx.bin"), [9, 9]);

        var files = PatchInsert.Includes(Path.Combine(dir, "main.asm"), out var missing);

        Assert.Equal(["gfx.bin", "sub/defs.asm", "sub/more.asm", "sub/tab.bin"], files.Keys.Order());
        Assert.Equal([1, 2, 3], files["sub/tab.bin"]);
        Assert.Equal(["../outside.asm", "!path/made.asm"], missing);
    }

    [Fact]
    public void a_patch_describes_itself_with_its_first_comment()
    {
        Assert.Equal("Makes the game run in FastROM", PatchInsert.Description("fast.asm", "\n; Makes the game run in FastROM\norg $008000\n"));
        Assert.Null(PatchInsert.Description("fast.asm", "org $008000\n; not first\n"));
        Assert.Null(PatchInsert.Description("fast.asm", Services.EditorSession.PatchTemplate("fast")));   // the template's says only the name
    }

    [Fact]
    public void an_overlap_names_its_owner_and_the_first_shared_byte()
    {
        List<PatchInsert.Owned> owned =
        [
            new("Pipe Dream's ROM hooks", Rom.SnesToPc(0x00A2B0), 4),
            new("patch a.asm", Rom.SnesToPc(0x108000), 2),
        ];

        Assert.Equal(["writes $00A2B0, which Pipe Dream's ROM hooks also writes"],
                     PatchInsert.Overlaps(owned, [new(0x00A2A0, 0x13)]));
        Assert.Equal(["writes $108001, which patch a.asm also writes"],
                     PatchInsert.Overlaps(owned, [new(0x908001, 1)]));                  // a FastROM mirror is the same byte
        Assert.Empty(PatchInsert.Overlaps(owned, [new(0x00A2B4, 8)]));
    }

    [AsarFact]
    public void a_patch_assembles_from_memory_with_its_files_and_an_error_names_the_file()
    {
        var rom = TestRom.Create(size: 0x100000);
        var p = Patch("tab.asm", "org $108000\nincsrc \"sub/more.asm\"\n");
        p.Files["sub/more.asm"] = "incbin \"data.bin\"\n"u8.ToArray();
        p.Files["sub/data.bin"] = [0xDE, 0xAD];

        var r = PatchInsert.Apply(rom, p);

        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal([0xDE, 0xAD], rom.Data[rom.FileOffset(0x108000)..][..2]);

        var before = rom.Data.ToArray();
        p.Files["sub/more.asm"] = "LDA.q #0\n"u8.ToArray();
        r = PatchInsert.Apply(rom, p);
        Assert.False(r.Ok);
        string error = Assert.Single(r.Errors);
        Assert.True(error.Replace('\\', '/').StartsWith("sub/more.asm:"), error);           // relative to the patch, not a temp path
        Assert.Equal(before, rom.Data);
    }

    [AsarFact]
    public void the_build_applies_the_enabled_patches_in_order_and_warns_on_overlaps()
    {
        var p = NewProject();
        p.Data.Patches.Add(Patch("a.asm", "org $108000\ndb $AA,$AA\n"));
        p.Data.Patches.Add(Patch("b.asm", "org $108001\ndb $BB\n"));
        p.Data.Patches.Add(Patch("off.asm", "org $108002\ndb $CC\n", enabled: false));

        var (status, built) = RomBuilder.Build(p);
        Assert.True(built is not null, status);
        var rom = Rom.Load(built!);
        Assert.Equal([0xAA, 0xBB], rom.Data[rom.FileOffset(0x108000)..][..2]);              // b after a
        Assert.NotEqual(0xCC, rom.ReadByte(0x108002));                                       // off stays off
        Assert.Contains("patch b.asm: writes $108001, which patch a.asm also writes", status);

        (p.Data.Patches[0], p.Data.Patches[1]) = (p.Data.Patches[1], p.Data.Patches[0]);
        (status, built) = RomBuilder.Build(p);
        Assert.Equal(0xAA, Rom.Load(built!).ReadByte(0x108001));                             // now a wins

        p.Data.Patches.Add(Patch("bad.asm", "LDA.q #0\n"));
        (status, built) = RomBuilder.Build(p);
        Assert.Null(built);
        Assert.StartsWith("build failed: patch bad.asm: ", status);
    }

    /// <summary>A patch's freespace is RATS-tagged, so what the build allocates after it lands elsewhere.</summary>
    [AsarFact]
    public void the_build_does_not_allocate_over_a_patchs_freespace()
    {
        var rom = TestRom.Create(size: 0x100000);
        var r = PatchInsert.Apply(rom, Patch("free.asm", "freedata\nTab:\ndb $12,$34,$56\nprint hex(Tab)\n"));
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        int tab = Rom.SnesToPc(r.Written.Single(b => b.Length > 8).Snes) + 8;

        int at = Rom.SnesToPc(RatsWriter.Allocate(rom, new byte[0x100]));

        Assert.True(at + 0x100 <= tab - 8 || at >= tab + 3, $"allocated at {at:X} over the patch's table at {tab:X}");
        Assert.Equal([0x12, 0x34, 0x56], rom.Data[(rom.HeaderOffset + tab)..][..3]);
    }

    [AsarFact]
    public void a_patch_over_a_prep_hook_is_a_warning_not_a_failure()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var p = Project.Create(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla);
        var (hook, _) = RomPrep.StampedRanges(p.Data.BaseRom.PrepVersion).First();
        p.Data.Patches.Add(Patch("hijack.asm", $"org ${hook:X6}\ndb $EA\n"));

        var (status, built) = RomBuilder.Build(p);

        Assert.True(built is not null, status);
        Assert.Contains($"patch hijack.asm: writes ${hook:X6}, which Pipe Dream's ROM hooks also writes", status);
    }

    [Fact]
    public void the_session_adds_imports_toggles_moves_and_deletes_patches()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        Assert.Empty(s.Patches);

        Assert.Equal("added fast.asm", s.NewPatch("fast", out var made));
        Assert.Equal("fast.asm", made);
        Assert.Equal("fast.asm is already in the project", s.NewPatch("fast.asm", out _));
        Assert.Equal("a patch name is letters, digits, _ and -", s.NewPatch("../x", out _));
        s.SetPatchSource("fast.asm", "; FastROM\norg $008000\n");
        Assert.Equal("; FastROM\norg $008000\n", s.PatchSource("fast.asm"));

        string src = Path.Combine(dir, "downloads");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "retry v1.2.asm"), "; Retry\nincsrc retry_config.asm\nincsrc gone.asm\n");
        File.WriteAllText(Path.Combine(src, "retry_config.asm"), "!x = 1\n");
        Assert.Equal("added retry_v1_2.asm with 1 included file(s); not found beside it: gone.asm",
                     s.ImportPatchFile(Path.Combine(src, "retry v1.2.asm"), out made));
        Assert.Equal("retry_v1_2.asm", made);

        Assert.Equal([("fast.asm", true, "FastROM", 0), ("retry_v1_2.asm", true, "Retry", 1)],
                     s.Patches.Select(r => (r.Name, r.Enabled, r.Description, r.Files)));

        Assert.Equal("fast.asm disabled", s.SetPatchEnabled("fast.asm", false));
        Assert.False(s.Patches[0].Enabled);
        Assert.Equal("retry_v1_2.asm moved to 1 of 2", s.MovePatch("retry_v1_2.asm", -1));
        Assert.Equal(["retry_v1_2.asm", "fast.asm"], s.Patches.Select(r => r.Name));
        Assert.Equal("deleted fast.asm", s.DeletePatch("fast.asm"));
        Assert.Equal("fast.asm is not in the project", s.DeletePatch("fast.asm"));
        Assert.Equal(["retry_v1_2.asm"], s.Patches.Select(r => r.Name));
    }

    /// <summary>The Patches window lists the patches in build order; a row's checkbox turns one
    /// off, Move up / Move down reorder with the ends disabled, and the editor saves into the patch.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_patches_window_toggles_reorders_and_edits()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewPatch("first", out _);
        s.NewPatch("second", out _);
        s.SetPatchSource("second.asm", "; Second one\n");
        var click = new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent);

        var w = new Ui.PatchesWindow(s);
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = w.GetControl<Avalonia.Controls.ListBox>("List");
        List<Ui.PatchesWindow.Row> Rows() => [.. ((System.Collections.IEnumerable)list.ItemsSource!).Cast<Ui.PatchesWindow.Row>()];
        Assert.Equal([new("first.asm", true, null, ""), new("second.asm", true, "Second one", "")], Rows());
        Assert.False(w.GetControl<Avalonia.Controls.Button>("UpButton").IsEnabled);    // nothing selected

        var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list)
            .OfType<Avalonia.Controls.CheckBox>().First(b => b.DataContext is Ui.PatchesWindow.Row { Name: "first.asm" });
        box.IsChecked = false;
        box.RaiseEvent(click);
        Assert.False(s.Patches[0].Enabled);
        Assert.Equal("first.asm disabled", w.GetControl<Avalonia.Controls.TextBlock>("Hint").Text);

        list.SelectedIndex = 1;
        Assert.True(w.GetControl<Avalonia.Controls.Button>("UpButton").IsEnabled);
        Assert.False(w.GetControl<Avalonia.Controls.Button>("DownButton").IsEnabled);  // already last
        w.GetControl<Avalonia.Controls.Button>("UpButton").RaiseEvent(click);
        Assert.Equal(["second.asm", "first.asm"], s.Patches.Select(p => p.Name));
        Assert.Equal("second.asm", ((Ui.PatchesWindow.Row)list.SelectedItem!).Name);   // the selection follows it

        var edit = new Ui.BlockEditWindow("first.asm", s.PatchSource("first.asm")!, asm => s.SetPatchSource("first.asm", asm));
        Assert.Equal("first.asm", edit.Title);
        edit.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "; edited";
        edit.GetControl<Avalonia.Controls.Button>("Save").RaiseEvent(click);
        Assert.Equal("; edited", s.PatchSource("first.asm"));
        w.Close();
    }
}
