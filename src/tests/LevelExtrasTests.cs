using Avalonia.Controls;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>The level bar's UberASM and Music drop-downs (ui/MainWindow.LevelExtras.cs).</summary>
public class LevelExtrasTests
{
    private static (MainWindow W, Services.EditorSession S)? Open()
    {
        if (PreppedRom.Path is not { } path) return null;
        Program.RomPath = path;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var s = (Services.EditorSession)typeof(MainWindow)
            .GetField("session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        return (w, s);
    }

    private static StackPanel Drop(MainWindow w, string button)
    {
        var b = w.GetControl<Button>(button);
        var flyout = (Flyout)b.Flyout!;
        flyout.ShowAt(b);
        Dispatcher.UIThread.RunJobs();
        return (StackPanel)flyout.Content!;
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_music_drop_down_picks_the_headers_song()
    {
        if (Open() is not var (w, s)) return;
        var radios = Drop(w, "LevelMusicButton").Children.OfType<RadioButton>().ToList();
        Assert.Equal(8, radios.Count);
        int now = s.Header!.Value.Music;
        Assert.True(radios[now].IsChecked);
        int other = (now + 1) % 8;
        radios[other].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(other, s.Header!.Value.Music);
        w.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_uberasm_drop_down_wants_a_project()
    {
        if (Open() is not var (w, _)) return;
        var panel = Drop(w, "LevelUberButton");
        Assert.Contains(panel.Children.OfType<TextBlock>(), t => t.Text == "Open a project first.");
        w.Close();
    }
}
