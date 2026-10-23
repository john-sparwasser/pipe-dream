using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace PipeDream.Ui;

// MainWindow — the level bar's UberASM and Music drop-downs: what level code this level runs (its line in the UberASM list)
// and which of the header's eight songs it plays. Each is filled as it opens, so it always shows the project as it is. The
// UberASM window and Level Properties edit the same things in full.
public partial class MainWindow
{
    /// <summary>A check box per level/*.asm file, ticked when this level runs it; ticking edits the level's own line. What *
    /// runs before it is shown, not ticked: it is every level's, edited in the UberASM window.</summary>
    private void OnLevelUberOpening(object? sender, EventArgs e)
    {
        if (sender is not Flyout { Content: StackPanel panel } flyout) return;
        panel.Children.Clear();
        panel.Children.Add(Heading($"UberASM — level {session.LevelNum:X}"));
        if (!session.HasProject) { panel.Children.Add(Note("Open a project first.")); return; }

        var files = session.UberFiles.Where(f => f.Path.StartsWith("level/") && f.Path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) panel.Children.Add(Note("The project has no level code yet."));
        var mine = session.UberCalls("level", session.LevelNum);
        foreach (var f in files)
        {
            string path = f.Path;
            var box = new CheckBox { Content = path["level/".Length..], IsChecked = mine.Any(c => c.File == path && !c.Skip) };
            box.IsCheckedChanged += (_, _) =>
            {
                var calls = session.UberCalls("level", session.LevelNum).ToList();
                if (box.IsChecked == true) { if (!calls.Any(c => c.File == path && !c.Skip)) calls.Add(new UberCall { File = path }); }
                else calls.RemoveAll(c => c.File == path && !c.Skip);
                session.SetUberCalls("level", session.LevelNum, calls);
                UpdateTitle();
            };
            panel.Children.Add(box);
        }
        if (session.UberLine("level", UberEntry.All) is { Length: > 0 } star)
            panel.Children.Add(Note($"Every level (*) also runs: {star}"));
        var manage = new Button { Content = "UberASM…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        manage.Click += (s, a) => { flyout.Hide(); OnUberAsm(s, a); };
        panel.Children.Add(manage);
    }

    /// <summary>The header's eight songs, named by the project's (or SMW's numbers), the level's own picked; picking one sets the
    /// header as Level Properties does.</summary>
    private void OnLevelMusicOpening(object? sender, EventArgs e)
    {
        if (sender is not Flyout { Content: StackPanel panel } flyout) return;
        panel.Children.Clear();
        panel.Children.Add(Heading($"Music — level {session.LevelNum:X}"));
        if (session.Header is not { } header) { panel.Children.Add(Note("Open a level first.")); return; }
        var names = session.LevelOptionChoices.Music;
        for (int i = 0; i < names.Length; i++)
        {
            int choice = i;
            var radio = new RadioButton { Content = names[i], GroupName = "LevelMusic", IsChecked = header.Music == i };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true || session.Header is not { } h || h.Music == choice) return;
                session.ApplyHeader(h with { Music = choice });
                AdoptSession();
                UpdateTitle();
                flyout.Hide();
            };
            panel.Children.Add(radio);
        }
        panel.Children.Add(Note("The level header holds one of these eight; ASM ▸ Music… has the songs themselves."));
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = Avalonia.Media.FontWeight.SemiBold };

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, FontSize = 12, Opacity = 0.75 };
        return t;
    }
}
