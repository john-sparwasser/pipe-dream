using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The colour picker that replaced the three 0-31 sliders, restoring what the ImGui editor had
/// through ColorPicker3.
///
/// The claim these defend is that the picker is NATIVE to BGR555 — the SNES stores five bits
/// per channel, and a 24-bit picker over a 15-bit palette lands a step away from the colour you
/// aimed at. Everything the picker produces must be a colour the hardware can make, and reading
/// its own output back must not move it.
/// </summary>
public class ColorPickerTests(ITestOutputHelper log)
{
    /// <summary>
    /// The whole no-hidden-quantisation claim rests on this: ToBgr555 must be a true inverse of
    /// ToRgba across the entire 15-bit space. A `>> 3` truncation passes for 0 and 31 and fails
    /// in between, so nothing short of exhaustive is worth running.
    /// </summary>
    [Fact]
    public void every_snes_colour_survives_the_round_trip_to_rgb_and_back()
    {
        for (int c = 0; c < 0x8000; c++)
            Assert.Equal((ushort)c, Palette.ToBgr555(Palette.ToRgba((ushort)c)));
    }

    /// <summary>Hue is preserved through HSV in both directions; a picker whose hue drifts as
    /// you drag towards white is the failure this catches.</summary>
    [Fact]
    public void hsv_round_trips_through_rgb()
    {
        for (int i = 0; i < 360; i += 7)
        {
            double h = i / 360.0;
            var (r, g, b) = ColorPickerView.FromHsv(h, 1, 1);
            var (h2, s2, v2) = ColorPickerView.ToHsv(r, g, b);
            Assert.True(Math.Abs(h2 - h) < 0.01, $"hue {h} came back as {h2}");
            Assert.True(s2 > 0.99 && v2 > 0.99, $"s={s2} v={v2}");
        }
    }

    private static (Window W, ColorPickerPanel P) Show()
    {
        // Instantiated directly rather than through the flyout: the flyout carries no logic, and
        // this is the same way the secondary windows are tested.
        var panel = new ColorPickerPanel();
        var window = new Window { Width = 500, Height = 500, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel);
    }

    [AvaloniaFact]
    public void dragging_the_square_reports_a_colour_the_snes_can_actually_show()
    {
        var (window, panel) = Show();
        panel.Begin(0x0000);

        var picked = new List<ushort>();
        panel.ColorChanged += (_, c) => picked.Add(c);

        var view = panel.GetControl<ColorPickerView>("Picker");
        // Top-right of the square is full saturation, full value — the pure hue.
        var at = view.TranslatePoint(new Point(view.SquareSize - 2, 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(picked);
        Assert.Equal(picked[^1], panel.Bgr);
        // Nothing outside the 15-bit space can come out, by construction — and reading the
        // result back has to leave it where it is.
        Assert.True(panel.Bgr < 0x8000);
        Assert.Equal(panel.Bgr, Palette.ToBgr555(Palette.ToRgba(panel.Bgr)));
        log.WriteLine($"picked {panel.Bgr:X4}");
    }

    /// <summary>Dragging the crosshair must not drag the HUE with it. Round-tripping the
    /// quantised colour back into H/S/V is what would do that: near black or near grey, many
    /// hues collapse onto one colour, so the hue you chose would be forgotten the moment you
    /// dragged towards a corner.</summary>
    [AvaloniaFact]
    public void the_hue_survives_a_drag_down_to_black_and_back()
    {
        var (window, panel) = Show();
        panel.Begin(0x0000);
        var view = panel.GetControl<ColorPickerView>("Picker");

        Point At(double x, double y) => view.TranslatePoint(new Point(x, y), window)!.Value;

        // Pick a hue on the strip, then a bright saturated colour from the square.
        var hue = At(view.SquareSize + view.Gap + view.StripWidth / 2, view.SquareSize * 0.35);
        window.MouseDown(hue, MouseButton.Left);
        window.MouseUp(hue, MouseButton.Left);
        window.MouseDown(At(view.SquareSize - 2, 2), MouseButton.Left);
        window.MouseUp(At(view.SquareSize - 2, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        ushort bright = panel.Bgr;
        Assert.NotEqual(0, bright);

        // Drag to the bottom (value 0 = black) and back to the same corner.
        window.MouseDown(At(view.SquareSize - 2, 2), MouseButton.Left);
        window.MouseMove(At(view.SquareSize - 2, view.SquareSize - 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, panel.Bgr);                    // black, whatever the hue was

        window.MouseMove(At(view.SquareSize - 2, 2));
        window.MouseUp(At(view.SquareSize - 2, 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(bright, panel.Bgr);               // the hue came back with it
    }

    [AvaloniaFact]
    public void the_sliders_and_the_square_stay_in_step()
    {
        var (_, panel) = Show();
        panel.Begin(0x0000);

        panel.GetControl<Slider>("PalR").Value = 31;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x001F, panel.Bgr);
        Assert.Equal(0x001F, panel.GetControl<ColorPickerView>("Picker").Bgr);

        // ...and back the other way: setting the colour moves the sliders.
        panel.Bgr = 0x7C00;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, panel.GetControl<Slider>("PalR").Value);
        Assert.Equal(31, panel.GetControl<Slider>("PalB").Value);
    }

    [Theory]
    [InlineData("7C1F", 0x7C1F)]        // BGR555, the form LM and this editor's readouts use
    [InlineData("#7C1F", 0x7C1F)]
    [InlineData("FF0000", 0x001F)]      // RRGGBB: pure red quantises to r=31
    [InlineData("FFFFFF", 0x7FFF)]
    [InlineData("000000", 0x0000)]
    public void hex_entry_takes_bgr555_and_rrggbb(string text, int expected)
        => Assert.Equal((ushort)expected, ColorPickerPanel.ParseHex(text));

    [Theory]
    [InlineData("")]
    [InlineData("7C")]                  // half-typed
    [InlineData("ZZZZ")]
    [InlineData("7C1F0")]
    public void half_typed_hex_is_ignored_rather_than_guessed(string text)
        => Assert.Null(ColorPickerPanel.ParseHex(text));
}
