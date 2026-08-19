using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The theme resolves for real controls in real interaction states.
///
/// Written after a crash the other UI tests could not see: Fluent's SystemAccentColor* keys
/// are COLOUR resources, and the theme supplied SolidColorBrushes for them. Nothing failed at
/// startup — the bad cast only happened when a control resolved its pointer-over style, so
/// the app died on mouse-over of a slider. Layout and click tests never hover anything, which
/// is precisely the gap these close.
/// </summary>
public class ThemeTests
{
    /// <summary>Every key the theme overrides, with the type Fluent expects for it. A brush
    /// where a colour belongs (or the reverse) is a runtime cast error, not a build error.</summary>
    [AvaloniaTheory]
    [InlineData("SystemAccentColor", typeof(Color))]
    [InlineData("SystemAccentColorLight1", typeof(Color))]
    [InlineData("SystemAccentColorLight2", typeof(Color))]
    [InlineData("SystemAccentColorLight3", typeof(Color))]
    [InlineData("SystemAccentColorDark1", typeof(Color))]
    [InlineData("SystemAccentColorDark2", typeof(Color))]
    [InlineData("SystemAccentColorDark3", typeof(Color))]
    [InlineData("SystemControlBackgroundAltHighBrush", typeof(SolidColorBrush))]
    [InlineData("AccentBrush", typeof(SolidColorBrush))]
    [InlineData("PanelBrush", typeof(SolidColorBrush))]
    [InlineData("VoidBrush", typeof(SolidColorBrush))]
    [InlineData("TextDimBrush", typeof(SolidColorBrush))]
    public void theme_resources_have_the_type_their_consumers_expect(string key, Type expected)
    {
        Assert.NotNull(Application.Current);
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"{key} is not defined");
        Assert.IsAssignableFrom(expected, value);
    }

    /// <summary>Hover every interactive control in the shell. Pointer-over is when Fluent
    /// pulls its accent tokens, so this is the state that crashed.</summary>
    [AvaloniaFact]
    public void hovering_the_shells_controls_does_not_throw()
    {
        var window = new Window { Width = 900, Height = 600 };
        var panel = new StackPanel();
        var slider = new Slider { Width = 120, Minimum = 0, Maximum = 10, Value = 5 };
        var combo = new ComboBox { Width = 120 };
        combo.Items.Add("one");
        combo.Items.Add("two");
        var toggle = new ToggleButton { Content = "mode" };
        var button = new Button { Content = "go" };
        var check = new CheckBox { Content = "on" };
        foreach (var c in new Control[] { slider, combo, toggle, button, check }) panel.Children.Add(c);
        window.Content = panel;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var c in new Control[] { slider, combo, toggle, button, check })
        {
            var mid = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window);
            if (mid is null) continue;
            window.MouseMove(mid.Value);        // throws if a theme token is the wrong type
            Dispatcher.UIThread.RunJobs();
        }

        // And a press/release, which pulls the "pressed" tokens too.
        var target = toggle.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseDown(target, MouseButton.Left);
        window.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The same sweep over the ACTUAL shell, so a control added later is covered
    /// without anyone remembering to list it here.</summary>
    [AvaloniaFact]
    public void hovering_every_control_in_the_real_window_does_not_throw()
    {
        string rom = Path.Combine(
            Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
            ".resources", "SMW.smc");
        if (!File.Exists(rom)) return;
        Program.RomPath = rom;

        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var c in w.GetVisualDescendants().OfType<Control>())
        {
            if (c.Bounds.Width < 2 || c.Bounds.Height < 2) continue;
            if (c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w) is not { } p) continue;
            if (p.X < 0 || p.Y < 0 || p.X > w.Width || p.Y > w.Height) continue;
            w.MouseMove(p);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
