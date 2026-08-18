using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace PipeDream.Ui;

/// <summary>Phase-0 spike shell: a level canvas, a zoom control, and a live frame time
/// readout — enough to see the composed level and to feel the interaction latency that a
/// benchmark number alone does not tell you about.</summary>
public class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = BuildWindow();
        base.OnFrameworkInitializationCompleted();
    }

    private static Window BuildWindow()
    {
        var view = new LevelView { Zoom = 2.0 };
        var status = new TextBlock { Margin = new Thickness(8, 4), Foreground = Brushes.Gainsboro };
        var bitmap = new LevelBitmap();
        view.Source = bitmap;

        // The reference-ROM helper is internal to the app assembly; the spike takes a path.
        string rom = Program.RomPath ?? Path.Combine(
            Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
            ".resources", "SMW.smc");
        if (File.Exists(rom))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scene = LevelScene.Build(Rom.Load(rom), Program.LevelNum);
            double compose = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            bitmap.SetImages(scene.Phases, scene.Width, scene.Height, 0);
            status.Text = $"level ${Program.LevelNum:X3}  {scene.Width}x{scene.Height}px   " +
                          $"compose 4 phases {compose:F0}ms   first upload {sw.Elapsed.TotalMilliseconds:F0}ms";
        }
        else status.Text = $"ROM not found: {rom}";

        view.CellPressed += (_, c) => status.Text = $"cell ({c.X}, {c.Y})  tile 0x{view.Source!.PxW:X}";

        var zoom = new Slider { Minimum = 1, Maximum = 6, Value = 2, Width = 160 };
        zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
            { view.Zoom = zoom.Value; view.InvalidateVisual(); }
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(new TextBlock { Text = "zoom", Margin = new Thickness(8, 4), Foreground = Brushes.Gray });
        bar.Children.Add(zoom);
        bar.Children.Add(status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = view });

        return new Window
        {
            Title = "pipe-dream (Avalonia spike)",
            Width = 1280,
            Height = 720,
            Background = Brushes.Black,
            Content = root,
        };
    }
}
