using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// The startup chooser: nothing is editable until a project is open, so the first thing the
/// editor asks is which one. Closing it without choosing leaves the empty editor up — the File
/// menu can do everything this window can, so it is a shortcut, not a gate.
/// </summary>
public partial class StartWindow : Window
{
    /// <summary>Exactly one of these is set when a choice was made; all stay unset on dismiss.</summary>
    public bool CreateNew { get; private set; }
    public bool OpenExisting { get; private set; }
    public string? OpenRecent { get; private set; }

    public StartWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="problem">Why the last attempt did not open anything — shown here because the
    /// status line is not on screen yet, and a chooser that silently reappears reads as a bug.</param>
    public StartWindow(IReadOnlyList<string> recentProjects, string? problem = null) : this()
    {
        if (problem is not null) { var t = this.GetControl<TextBlock>("Problem"); t.Text = problem; t.IsVisible = true; }
        var list = this.GetControl<StackPanel>("RecentList");
        foreach (string path in recentProjects)
        {
            var b = new Button
            {
                Content = path,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Background = Avalonia.Media.Brushes.Transparent,
            };
            b.Click += (_, _) => { OpenRecent = path; Close(); };
            list.Children.Add(b);
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e) { CreateNew = true; Close(); }
    private void OnOpen(object? sender, RoutedEventArgs e) { OpenExisting = true; Close(); }
}
