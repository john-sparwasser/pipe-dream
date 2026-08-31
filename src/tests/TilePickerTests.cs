using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The ExAnimation tile picker (reference/EXANIMATION.md §8): clicking a tile anywhere on the
/// sheet — including far down it — returns that tile, numbered from the sheet's base, and a
/// destination pick refuses a spot where the slot's footprint would run off the valid range.
/// </summary>
public class TilePickerTests(ITestOutputHelper log)
{
    private readonly ITestOutputHelper log = log;

    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects", ".resources", "SMW.smc");

    private static EditorSession? Open()
    {
        if (!File.Exists(Vanilla)) return null;
        var s = new EditorSession();
        if (!s.OpenRom(Vanilla)) return null;
        s.ShowLevel(0x105);
        return s;
    }

    /// <summary>Screen point of the centre of sheet tile (col, row), in window coordinates.</summary>
    private static Point TileCentre(TilePickerWindow w, int col, int row)
    {
        var sheet = w.GetControl<PixelImage>("Sheet");
        double cell = sheet.Bounds.Width / 16;                   // 16 tiles across, whatever the scale
        // A row past the viewport has to be scrolled to first, as a user would — a click at a
        // point the ScrollViewer is clipping hits nothing.
        var sv = sheet.FindAncestorOfType<ScrollViewer>()!;
        sv.Offset = new Vector(0, Math.Max(0, row * cell - sv.Viewport.Height / 2));
        Dispatcher.UIThread.RunJobs();
        return sheet.TranslatePoint(new Point(col * cell + cell / 2, row * cell + cell / 2), w)!.Value;
    }

    [AvaloniaFact]
    public void clicking_a_tile_low_on_the_source_sheet_picks_that_tile()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var w = new TilePickerWindow(s, [0, 1, 2, 3], altIndex: 0, palRow: 2, preferAlt: false, global: false);   // a row of 4
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ComboBox>("Source").SelectedIndex = 0;      // this test clicks on the AN1 sheet
        Dispatcher.UIThread.RunJobs();

        var at = TileCentre(w, 4, 10);                          // row 10: well below the first screenful
        w.MouseDown(at, MouseButton.Left);                     // the pick closes the window on press
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0x600 + 10 * 16 + 4, w.Picked);
        Assert.False(w.PickedAlt);
    }

    [AvaloniaFact]
    public void destination_pick_returns_the_vram_tile_and_refuses_an_overflowing_footprint()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var slot = new ExAnimation.Slot(0, ExAnimation.Type16x16, 0, 1, 0, [0x7D00], 0);   // 2x2 footprint
        var w = new TilePickerWindow(s, slot, palRow: 2);
        w.Show();
        Dispatcher.UIThread.RunJobs();

        // Row 0x2F is the last valid destination row (tiles 2F0-2FF); a 2x2 block from it would
        // need row 0x30, so the click must be refused — and one row up must succeed.
        var bad = TileCentre(w, 3, 0x2F);
        w.MouseDown(bad, MouseButton.Left); w.MouseUp(bad, MouseButton.Left);   // refused: still open
        Dispatcher.UIThread.RunJobs();
        Assert.Null(w.Picked);

        var good = TileCentre(w, 3, 0x2E);
        w.MouseDown(good, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0x2E * 16 + 3, w.Picked);
    }

    /// <summary>The alternate-file source offers Edit… (to the Graphics editor) instead of a
    /// "go import it" hint, and Mario's sheet is not in the list.</summary>
    [AvaloniaFact]
    public void alt_file_source_offers_edit_and_mario_is_not_listed()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var w = new TilePickerWindow(s, [0], altIndex: 0, palRow: 2, preferAlt: true, global: false);
        int asked = -1;
        w.EditRequested = f => asked = f;
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var source = w.GetControl<ComboBox>("Source");
        Assert.DoesNotContain(source.Items.Cast<string>(), n => n.Contains("Mario"));
        Assert.Contains("file 60", (string)source.SelectedItem!);
        Assert.True(w.GetControl<Button>("EditFile").IsVisible);
        Assert.Contains("Edit", w.GetControl<TextBlock>("Hint").Text);   // empty on a fresh base

        source.SelectedIndex = 0;                                         // AN1: not ours to paint
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.GetControl<Button>("EditFile").IsVisible);

        source.SelectedIndex = source.Items.Count - 1;
        Dispatcher.UIThread.RunJobs();
        var edit = w.GetControl<Button>("EditFile");
        edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // Click fires on release; the window closes on it
        Assert.Equal(0x60, asked);
    }

    /// <summary>The source dropdown opens where custom animations live: a global list's own
    /// file 60-63; a level list falls back there too when no AN2 bypass file is set. AN1 (the
    /// remapped vanilla sheet) is never the default.</summary>
    [AvaloniaFact]
    public void frame_source_defaults_to_the_custom_file_not_an1()
    {
        if (Open() is not { } s) { log.WriteLine("SKIP: no ROM"); return; }
        var g = new TilePickerWindow(s, [0], altIndex: 1, palRow: 2, preferAlt: false, global: true);
        g.Show(); Dispatcher.UIThread.RunJobs();
        Assert.Contains("file 61", (string)g.GetControl<ComboBox>("Source").SelectedItem!);
        g.Close();

        var l = new TilePickerWindow(s, [0], altIndex: 0, palRow: 2, preferAlt: false, global: false);
        l.Show(); Dispatcher.UIThread.RunJobs();
        var picked = (string)l.GetControl<ComboBox>("Source").SelectedItem!;
        Assert.True(picked.Contains("AN2") || picked.Contains("file 60"), picked);   // AN2 when set, else the 60-63 file
        Assert.DoesNotContain("AN1", picked);
        l.Close();
    }
}
