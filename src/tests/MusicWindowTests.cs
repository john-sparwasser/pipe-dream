using Avalonia.Controls;
using Avalonia.Interactivity;
using PipeDream.Services;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The Music window and the song editor (reference/AMK_PORT.md): what they list and show after
/// session operations, which buttons wait for a selection, and that Save reaches the project.</summary>
public class MusicWindowTests
{
    private static readonly RoutedEventArgs Click = new(Button.ClickEvent);

    private static EditorSession Project()
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        return s;
    }

    private static MusicWindow Open(EditorSession s)
    {
        var w = new MusicWindow(s);
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static List<T> Rows<T>(ListBox list) => [.. ((System.Collections.IEnumerable)list.ItemsSource!).Cast<T>()];

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_music_window_lists_songs_and_samples_and_waits_for_a_selection()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        s.NewSong("good", out _);
        s.NewSong("bad", out _);
        s.SetSongText("bad.mml", "#0 c d e zzz\n");
        s.SetSongNumber("good.mml", 0x35, global: false);
        string brr = Path.Combine(Path.GetTempPath(), $"pd-{Guid.NewGuid():N}.brr");
        File.WriteAllBytes(brr, new byte[18]);
        s.ImportSampleFile(brr, out var sample);

        var w = Open(s);
        var songs = w.GetControl<ListBox>("SongList");
        var rows = Rows<MusicWindow.SongRow>(songs);
        Assert.Equal(["bad.mml", "good.mml"], rows.Select(r => r.Path));
        Assert.Equal(["", "local 35"], rows.Select(r => r.Number));
        Assert.Equal("error", rows[0].Size);
        Assert.EndsWith(" bytes", rows[1].Size);
        Assert.Equal([new MusicWindow.SampleRow(sample!, "18 bytes")], Rows<MusicWindow.SampleRow>(w.GetControl<ListBox>("SampleList")));

        string[] needsSong = ["EditButton", "NumberButton", "DeleteButton"];
        Assert.All(needsSong, b => Assert.False(w.GetControl<Button>(b).IsEnabled));
        Assert.False(w.GetControl<Button>("DeleteSampleButton").IsEnabled);
        songs.SelectedIndex = 1;
        Assert.All(needsSong, b => Assert.True(w.GetControl<Button>(b).IsEnabled));
        w.GetControl<ListBox>("SampleList").SelectedIndex = 0;
        w.GetControl<Button>("DeleteSampleButton").RaiseEvent(Click);
        Assert.Empty(s.MusicSamples);
        Assert.Equal($"deleted {sample}", w.GetControl<TextBlock>("Hint").Text);
        Assert.False(w.GetControl<Button>("DeleteSampleButton").IsEnabled);
        w.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_song_editor_shows_problems_for_a_bad_song_and_a_size_for_a_good_one()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        s.NewSong("tune", out _);
        s.SetSongText("tune.mml", "#0 c d e zzz\n");

        var w = new SongEditWindow(s, "tune.mml");
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = w.GetControl<ListBox>("Problems");
        var found = Rows<SongEditWindow.Problem>(list);
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.Equal(1, p.Line));
        Assert.Contains("problem", w.GetControl<TextBlock>("Size").Text);

        // Typing re-checks after a pause; CheckNow is that pause's work.
        var editor = w.GetControl<AvaloniaEdit.TextEditor>("Editor");
        editor.Text = "#amk 2\n#0 @0 v255 o5 l8\nc d e f\n";
        w.CheckNow();
        Assert.Empty(Rows<SongEditWindow.Problem>(list));
        Assert.Matches(@"^\d[\d,]* bytes$", w.GetControl<TextBlock>("Size").Text!);
        Assert.Matches(@"^#0 \d+$", w.GetControl<TextBlock>("Channels").Text!);
        if (Asar.Available)
        {
            Assert.Matches(@"^ARAM \d+\.\d of \d+\.\d KB$", w.GetControl<TextBlock>("AramText").Text!);
            Assert.InRange(w.GetControl<ProgressBar>("AramBar").Value, 0.001, 1);
        }

        w.GetControl<Button>("Save").RaiseEvent(Click);
        Assert.Equal("#amk 2\n#0 @0 v255 o5 l8\nc d e f\n", s.SongText("tune.mml"));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void sound_effects_are_listed_edited_and_emptied()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        string file = Path.Combine(Path.GetTempPath(), $"fx-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "$DA $0A $18 $A4");
        Assert.Equal($"1DF9 22 is {Path.GetFileName(file)}", s.ImportEffectFile(file, "1DF9", 0x22));
        Assert.StartsWith("an effect goes in", s.ImportEffectFile(file, "1DFA", 1));

        var w = Open(s);
        var list = w.GetControl<ListBox>("EffectList");
        var row = Assert.Single(Rows<MusicWindow.EffectRow>(list));
        Assert.Equal(("1DF9 22", "5 bytes"), (row.Where, row.Size));
        Assert.False(w.GetControl<Button>("EditEffectButton").IsEnabled);
        list.SelectedIndex = 0;
        Assert.True(w.GetControl<Button>("EditEffectButton").IsEnabled);

        var ed = new SongEditWindow(s, "1DF9", Path.GetFileName(file));
        ed.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("5 bytes", ed.GetControl<TextBlock>("Size").Text);
        Assert.False(ed.GetControl<Button>("ExportButton").IsVisible);
        ed.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "@10 c8 zz";
        ed.CheckNow();
        Assert.Equal("1 problem", ed.GetControl<TextBlock>("Size").Text);
        ed.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "@10 c8 d8";
        ed.GetControl<Button>("Save").RaiseEvent(Click);
        Assert.Equal("@10 c8 d8", s.EffectText("1DF9", Path.GetFileName(file)));

        w.GetControl<Button>("DeleteEffectButton").RaiseEvent(Click);
        Assert.Empty(s.MusicEffects);
        Assert.Null(s.EffectText("1DF9", Path.GetFileName(file)));               // no slot names it any more
        w.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void cancelling_the_song_editor_leaves_the_song_alone()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project();
        s.NewSong("tune", out _);
        string before = s.SongText("tune.mml")!;
        var w = new SongEditWindow(s, "tune.mml");
        w.Show();
        w.GetControl<AvaloniaEdit.TextEditor>("Editor").Text = "; nothing";
        w.GetControl<Button>("OpenButton");                       // present, even with no editor configured
        w.Close();
        Assert.Equal(before, s.SongText("tune.mml"));
    }
}
