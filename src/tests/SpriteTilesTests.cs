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

/// <summary>Debug ▸ Sprite tiles: a click on the SP sheet gives the selected sprite tile that
/// sheet tile, and the sprite counts as edited. Never saves — that writes the source tree.</summary>
public class SpriteTilesTests(ITestOutputHelper log)
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects", ".resources", "SMW.smc");

    [AvaloniaFact]
    public void clicking_the_sheet_retiles_the_selected_sprite_tile()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla));
        s.ShowLevel(0x105);
        var w = new SpriteTilesWindow(s) { Width = 1100, Height = 760 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var tiles = w.GetControl<ListBox>("Tiles");
        Assert.Equal(0, tiles.SelectedIndex);                       // sprite 00 opens with its tile selected
        var sheet = w.GetControl<PixelImage>("Sheet");
        double cell = sheet.Bounds.Width / 16;
        var at = sheet.TranslatePoint(new Point(3 * cell + cell / 2, 2 * cell + cell / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("023", (string)tiles.SelectedItem!);      // row 2, column 3
        Assert.Contains("1 sprite(s) edited", w.GetControl<TextBlock>("Status").Text);
        Assert.True(w.GetControl<Button>("SaveButton").IsEnabled);
        Assert.True(w.GetControl<CheckBox>("Need0").IsChecked);                 // the tile came from SP1
        Assert.StartsWith("pinned to ", w.GetControl<TextBlock>("Note0").Text);
        Assert.EndsWith("no tiles here", w.GetControl<TextBlock>("Note1").Text);   // its only tile left SP2: a stale pin

    }

    [AvaloniaFact]
    public void showing_another_sprite_set_swaps_the_sheet_without_editing()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla));
        s.ShowLevel(0x105);
        var w = new SpriteTilesWindow(s) { Width = 1100, Height = 760 };
        w.Show();
        Dispatcher.UIThread.RunJobs();

        var set = w.GetControl<ComboBox>("SetBox");
        Assert.Equal(18, set.ItemCount);                            // own, level, sets 0-F
        set.SelectedIndex = 2 + 4;                                  // sprite set 4
        Dispatcher.UIThread.RunJobs();

        var sp = Enumerable.Range(0, 4).Select(i => w.GetControl<TextBox>($"File{i}").Text).ToArray();
        Assert.Equal(s.SpriteTileFiles(0, 6).Select(f => f.ToString("X2")), sp);
        Assert.False(w.GetControl<Button>("SaveButton").IsEnabled);
    }

    [AvaloniaFact]
    public void a_lasso_picks_tiles_and_dragging_one_moves_them_all()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla));
        s.ShowLevel(0x105);
        var w = new SpriteTilesWindow(s) { Width = 1100, Height = 760 };
        w.Show();
        w.GetControl<ListBox>("Sprites").SelectedIndex = 4;          // 04 Green Koopa: head + body
        Dispatcher.UIThread.RunJobs();
        var before = s.SpriteTilesOf(4);
        Assert.Equal(2, before.Count);

        var host = w.GetControl<Panel>("PreviewHost");
        Point At(double x, double y) => host.TranslatePoint(new Point(x, y), w)!.Value;
        w.MouseDown(At(4, 4), MouseButton.Left);                     // empty corner: lasso
        w.MouseMove(At(380, 380));
        w.MouseUp(At(380, 380), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var tiles = w.GetControl<ListBox>("Tiles");
        Assert.Equal(2, tiles.Selection.SelectedIndexes.Count());

        // Grab the body (cell-relative 0..15, 1..16 → preview (48+x)*3) and drag 5px right, 2px up.
        var grab = At((48 + 8) * 3, (64 + 8) * 3);
        w.MouseDown(grab, MouseButton.Left);
        w.MouseMove(new Point(grab.X + 15, grab.Y - 6));
        w.MouseUp(new Point(grab.X + 15, grab.Y - 6), MouseButton.Left);
        w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);  // and one nudge
        Dispatcher.UIThread.RunJobs();

        var rows = ((IEnumerable<string>)tiles.ItemsSource!).ToList();
        for (int i = 0; i < 2; i++)
            Assert.Contains($"x{before[i].X + 6,4} y{before[i].Y - 2,4}", rows[i]);
        Assert.Equal(2, tiles.Selection.SelectedIndexes.Count());     // still both picked
    }

    /// <summary>Picking a sprite's second tile, then a sprite with only one, threw: the list still
    /// held index 1 while the sheet redrew against the new sprite's tiles.</summary>
    [AvaloniaFact]
    public void switching_to_a_sprite_with_fewer_tiles_keeps_working()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        var s = new EditorSession();
        Assert.True(s.OpenRom(Vanilla));
        s.ShowLevel(0x105);
        var w = new SpriteTilesWindow(s) { Width = 1100, Height = 760 };
        w.Show();
        var sprites = w.GetControl<ListBox>("Sprites");
        sprites.SelectedIndex = 4;                                  // 04 Green Koopa: two tiles
        Dispatcher.UIThread.RunJobs();
        w.GetControl<ListBox>("Tiles").SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        sprites.SelectedIndex = 0;                                  // 00: one tile
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, w.GetControl<ListBox>("Tiles").SelectedIndex);
    }
}
