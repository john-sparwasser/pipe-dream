using System.Text.Json;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Music, phase 1 (reference/AMK_PORT.md): AddmusicK's manifests read into one model and written back the
/// way AMK reads them, an AMK folder imported into the project and exported again, and the session's
/// library operations. No compiling or insertion yet.
/// </summary>
public class MusicTests
{
    private const string List = "Globals:\r\n01 test.mml\r\n1D Star.txt\r\n\r\nLocals:\r\n35 Dir/My Song.txt\r\n";
    private const string Groups = "#default\r\n{\r\n\t\"default/00 SMW @0.brr\"!\r\n\t\"default/0D SMW @14.brr\"\r\n}\r\n\r\n#AMM\r\n{\r\n\t\"EMPTY.brr\"!\r\n\t\"EMPTY.brr\"\r\n}\r\n";
    private const string Sfx = "SFX1DF9:\r\n1\t01 Hit head.txt\r\nA\t0A Get powerup.txt\r\n1A ?\t1A Grinder click 1.txt\r\n24 *\t22 Silence.txt\r\n\r\nSFX1DFC:\r\n14\t14 Unused SMW2 SFX.txt\r\n";

    private static string Temp() => Directory.CreateTempSubdirectory("pipedream-amk-").FullName;

    [Fact]
    public void the_song_list_reads_both_sections_and_writes_the_same_text()
    {
        var problems = new List<string>();
        var slots = AmkImport.ParseList(List, problems);

        Assert.Empty(problems);
        Assert.Equal([(1, "test.mml", true), (0x1D, "Star.txt", true), (0x35, "Dir/My Song.txt", false)],
                     slots.Select(s => (s.Number, s.Path, s.Global)).ToArray());
        Assert.Equal(List, AmkImport.FormatList(slots));
    }

    [Fact]
    public void sample_groups_keep_their_order_repeats_and_important_marks()
    {
        var problems = new List<string>();
        var groups = AmkImport.ParseGroups(Groups, problems);

        Assert.Empty(problems);
        Assert.Equal(["default", "AMM"], groups.Select(g => g.Name).ToArray());
        Assert.Equal([("default/00 SMW @0.brr", true), ("default/0D SMW @14.brr", false)], groups[0].Samples.Select(s => (s.Path, s.Important)).ToArray());
        Assert.Equal(2, groups[1].Samples.Count(s => s.Path == "EMPTY.brr"));            // a repeated sample is two entries
        Assert.Equal(Groups, AmkImport.FormatGroups(groups));
    }

    [Fact]
    public void sound_effects_read_their_flags_and_both_ports()
    {
        var problems = new List<string>();
        var (a, b) = AmkImport.ParseSfx(Sfx, problems);

        Assert.Empty(problems);
        Assert.Equal([(1, "", "01 Hit head.txt"), (0xA, "", "0A Get powerup.txt"), (0x1A, "?", "1A Grinder click 1.txt"), (0x24, "*", "22 Silence.txt")],
                     a.Select(s => (s.Slot, s.Flag, s.File)).ToArray());
        Assert.Equal([(0x14, "", "14 Unused SMW2 SFX.txt")], b.Select(s => (s.Slot, s.Flag, s.File)).ToArray());
        Assert.Equal(Sfx, AmkImport.FormatSfx(a, b));
    }

    [Fact]
    public void a_line_that_is_not_a_song_is_a_problem_not_a_crash()
    {
        var problems = new List<string>();
        var slots = AmkImport.ParseList("Globals:\r\nnot a song\r\n01 ok.mml\r\n", problems);
        Assert.Single(slots);
        Assert.Contains("line 2", Assert.Single(problems));
    }

    [Fact]
    public void the_project_file_carries_the_music_and_an_older_one_has_none()
    {
        var p = new ProjectFile();
        p.Music.Songs["a.mml"] = "#0 c4\n";
        p.Music.Samples["x.brr"] = [0, 0xFF, 9];
        p.Music.SongList.Add(new MusicState.SongSlot { Number = 0x35, Path = "a.mml", Global = true });
        p.Music.Groups.Add(new MusicState.SampleGroup { Name = "g", Samples = { new MusicState.SampleEntry { Path = "x.brr", Important = true } } });
        p.Music.Sfx1DFC.Add(new MusicState.SfxSlot { Slot = 0x14, Flag = "*", File = "f.txt" });
        p.Music.SfxSources["1DFC/f.txt"] = "$01\n";
        p.Music.Options = ["-v", "-s"];

        var back = ProjectFile.FromJson(p.ToJson());
        Assert.Equal(JsonSerializer.Serialize(p.Music), JsonSerializer.Serialize(back.Music));
        Assert.Equal([0, 0xFF, 9], back.Music.Samples["x.brr"]);
        Assert.True(ProjectFile.FromJson("{}").Music.IsEmpty);                      // an older .pdp
    }

    /// <summary>An AMK folder laid out as its docs say, with a file missing from each manifest.</summary>
    private static string Folder()
    {
        string d = Temp();
        File.WriteAllText(Path.Combine(d, "Addmusic_list.txt"), List);
        File.WriteAllText(Path.Combine(d, "Addmusic_sample groups.txt"), Groups);
        File.WriteAllText(Path.Combine(d, "Addmusic_sound effects.txt"), Sfx);
        File.WriteAllText(Path.Combine(d, "Addmusic_options.txt"), "-v\r\n-s\r\n");
        Directory.CreateDirectory(Path.Combine(d, "music", "Dir"));
        File.WriteAllText(Path.Combine(d, "music", "test.mml"), "#0 c4\r\nd4\n");
        File.WriteAllText(Path.Combine(d, "music", "Dir", "My Song.txt"), "#samples\n{\n\t\"x.brr\"\n}\n");
        File.WriteAllText(Path.Combine(d, "music", "notes.md"), "not a song");                    // not .txt or .mml
        Directory.CreateDirectory(Path.Combine(d, "samples", "default"));
        File.WriteAllBytes(Path.Combine(d, "samples", "default", "00 SMW @0.brr"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(d, "samples", "x.brr"), [4, 5]);
        Directory.CreateDirectory(Path.Combine(d, "1DF9"));
        File.WriteAllText(Path.Combine(d, "1DF9", "01 Hit head.txt"), "$01 $02\n");
        Directory.CreateDirectory(Path.Combine(d, "1DFC"));
        File.WriteAllText(Path.Combine(d, "1DFC", "14 Unused SMW2 SFX.txt"), "$03\n");
        return d;
    }

    [Fact]
    public void a_folder_reads_every_song_sample_and_effect_and_names_what_is_missing()
    {
        var f = AmkImport.ReadFolder(Folder());
        var m = f.Music;

        Assert.Equal(["Dir/My Song.txt", "test.mml"], m.Songs.Keys.Order().ToArray());      // folders by '/', notes.md left out
        Assert.Equal("#0 c4\r\nd4\n", m.Songs["test.mml"]);                                  // text as written, line ends and all
        Assert.Equal(["default/00 SMW @0.brr", "x.brr"], m.Samples.Keys.Order().ToArray());
        Assert.Equal(["1DF9/01 Hit head.txt", "1DFC/14 Unused SMW2 SFX.txt"], m.SfxSources.Keys.Order().ToArray());
        Assert.Equal(["-v", "-s"], m.Options);
        Assert.Contains(f.Problems, p => p.Contains("1D") && p.Contains("Star.txt"));        // listed, no file
        Assert.Contains(f.Problems, p => p.Contains("default/0D SMW @14.brr"));
        Assert.Contains(f.Problems, p => p.Contains("EMPTY.brr"));
        Assert.Contains(f.Problems, p => p.Contains("0A Get powerup.txt"));
        Assert.Throws<FileNotFoundException>(() => AmkImport.ReadFolder(Temp()));
    }

    [Fact]
    public void a_folder_written_and_read_again_is_the_same_music()
    {
        var first = AmkImport.ReadFolder(Folder()).Music;
        string out1 = Temp();
        AmkImport.WriteFolder(first, out1);
        var second = AmkImport.ReadFolder(out1).Music;

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal("#0 c4\r\nd4\n", File.ReadAllText(Path.Combine(out1, "music", "test.mml")));
        Assert.Equal(List, File.ReadAllText(Path.Combine(out1, "Addmusic_list.txt")));
    }

    [Fact]
    public void a_path_that_leaves_the_folder_is_refused_on_write()
    {
        var m = new MusicState();
        m.Songs["../escape.mml"] = "x";
        Assert.Throws<InvalidDataException>(() => AmkImport.WriteFolder(m, Temp()));
        Assert.False(AmkImport.SafePath("a/../b.mml"));
        Assert.False(AmkImport.SafePath("/abs.mml"));
        Assert.True(AmkImport.SafePath("Dir/My Song.txt"));
    }

    /// <summary>The strongest check there is that an import lost nothing: AddmusicK itself, given the
    /// folder we wrote instead of the one it came from, makes the same ARAM.</summary>
    [AmkFact]
    public void amk_makes_the_same_songs_from_the_folder_we_export()
    {
        string root = AmkOracle.Root!;
        var read = AmkImport.ReadFolder(root);
        Assert.NotEmpty(read.Music.Songs);

        // The tool with its own data taken out, then our export laid over it.
        string tool = Temp();
        foreach (string f in Directory.GetFiles(root)) File.Copy(f, Path.Combine(tool, Path.GetFileName(f)));
        foreach (string d in new[] { "asm" }) CopyTree(Path.Combine(root, d), Path.Combine(tool, d));
        Directory.CreateDirectory(Path.Combine(tool, "SPCs"));
        foreach (string f in new[] { "Addmusic_list.txt", "Addmusic_sample groups.txt", "Addmusic_sound effects.txt" }) File.Delete(Path.Combine(tool, f));
        AmkImport.WriteFolder(read.Music, tool);

        string[] songs = ["click.mml", "cal.mml", "ct_fanfare_2.opt.mml", "exp.mml", "ww_solo.mml"];
        var original = AmkOracle.Compile(root, songs);
        var ours = AmkOracle.Compile(tool, songs);
        foreach (string s in songs)
        {
            string name = Path.GetFileNameWithoutExtension(s);
            Assert.True(original[name].Ram.AsSpan().SequenceEqual(ours[name].Ram), $"{s}: ARAM differs");
            Assert.True(original[name].Dsp.AsSpan().SequenceEqual(ours[name].Dsp), $"{s}: DSP differs");
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    [RealRomFact]
    public void the_library_through_the_session()
    {
        var s = new Services.EditorSession();
        Assert.True(s.NewProject(Path.Combine(Temp(), "proj"), ReferenceRoms.Vanilla), s.Status);
        Assert.Empty(s.MusicSongs);

        Assert.Equal("added Tune.mml", s.NewSong("Tune", out var made));
        Assert.Equal("Tune.mml", made);
        Assert.Equal("tune.mml is already in the project", s.NewSong("tune.mml", out _));           // names compare without case
        Assert.Contains("#0", s.SongText("Tune.mml"));
        Assert.StartsWith("a song name is a file name", s.NewSong("../x.mml", out _));

        Assert.Equal("Tune.mml is global 35", s.SetSongNumber("Tune.mml", 0x35, true));
        s.NewSong("Other", out _);
        Assert.Equal("number 35 is Tune.mml's", s.SetSongNumber("Other.mml", 0x35, false));
        Assert.Equal("a song number is 01-FF", s.SetSongNumber("Other.mml", 0, false));
        Assert.Equal("Tune.mml is local 36", s.SetSongNumber("Tune.mml", 0x36, false));            // moved, not added
        Assert.Equal("local 36", s.MusicSongs.Single(x => x.Path == "Tune.mml").Number);
        Assert.Equal("", s.MusicSongs.Single(x => x.Path == "Other.mml").Number);
        s.SetSongText("Tune.mml", "#0 c4\n");
        Assert.Equal("#0 c4\n", s.SongText("Tune.mml"));

        string dir = Temp();
        File.WriteAllBytes(Path.Combine(dir, "voice.brr"), [9, 9]);
        File.WriteAllText(Path.Combine(dir, "Song.txt"), "#samples\n{\n\t\"voice.brr\"\n\t\"gone.brr\"\n}\n#0 c4\n");
        Assert.Equal("added Song.txt with 1 sample(s); not found beside it: gone.brr", s.ImportSongFile(Path.Combine(dir, "Song.txt"), out _));
        Assert.Equal("voice.brr is already in the project", s.ImportSampleFile(Path.Combine(dir, "voice.brr"), out _));   // the song brought it
        Assert.Equal("a sample is a .brr file, or a .bnk bank", s.ImportSampleFile(Path.Combine(dir, "Song.txt"), out _));

        string export = Path.Combine(Temp(), "out");
        Assert.StartsWith("wrote 3 song(s), 1 sample(s)", s.ExportAmkFolder(export));
        var s2 = new Services.EditorSession();
        Assert.True(s2.NewProject(Path.Combine(Temp(), "proj2"), ReferenceRoms.Vanilla), s2.Status);
        Assert.StartsWith("imported 3 song(s) (1 numbered), 1 sample(s)", s2.ImportAmkFolder(export));
        Assert.Equal("local 36", s2.MusicSongs.Single(x => x.Path == "Tune.mml").Number);

        Assert.Equal("Other.mml has no number", s.ClearSongNumber("Other.mml"));
        Assert.Equal("Tune.mml is in no slot", s.ClearSongNumber("Tune.mml"));
        Assert.Equal("deleted Other.mml", s.DeleteSong("Other.mml"));
        Assert.Equal("deleted voice.brr", s.DeleteSample("voice.brr"));
        Assert.Equal("Other.mml is not in the project", s.DeleteSong("Other.mml"));
        Assert.Equal("that folder has no Addmusic_list.txt — pick AddmusicK's own folder", s.ImportAmkFolder(Temp()));
    }
}
