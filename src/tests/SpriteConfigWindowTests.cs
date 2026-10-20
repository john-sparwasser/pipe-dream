using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Services;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// Custom sprites, phase 4 (reference/PIXI_PORT.md): the config editor writes what its fields say
/// into the config, a sheet click gives a display or an own tile its tile, and Save hands the
/// config to the project as JSON PIXI reads.
/// </summary>
public class SpriteConfigWindowTests
{
    /// <summary>A project with one library sprite, "thing", holding <paramref name="c"/>, in level 105.</summary>
    private static EditorSession Project(PixiConfig c)
    {
        string dir = Directory.CreateTempSubdirectory("pipedream-proj-").FullName;
        var s = new EditorSession();
        Assert.True(s.NewProject(Path.Combine(dir, "proj"), ReferenceRoms.Vanilla), s.Status);
        s.NewCustomSprite("thing", "sprite", out _);
        s.SetCustomSpriteConfig("thing", c.ToJson());
        s.ShowLevel(0x105);
        return s;
    }

    private static SpriteConfigWindow Open(EditorSession s, int tab)
    {
        var w = new SpriteConfigWindow(s, "thing", s.CustomSpriteModel("thing")!) { Width = 1180, Height = 800 };
        w.Show();
        w.GetControl<TabControl>("Tabs").SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static void Click(Window w, TileSheetView sheet, int col, int row)
    {
        Dispatcher.UIThread.RunJobs();                                       // lay out a sheet just redrawn
        double cell = sheet.CellPx * sheet.Scale;
        var at = sheet.TranslatePoint(new Point(col * cell + cell / 2, row * cell + cell / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void the_sprite_tab_writes_acts_like_tweakers_and_list_entries()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var c = new PixiConfig { Type = 1, ExtraBytes = 2 };
        c.Collection.Add(new PixiConfig.CollectionEntry { Name = "Thing" });
        var s = Project(c);
        var w = Open(s, 0);

        Assert.False(w.GetControl<Button>("SaveButton").IsEnabled);
        w.GetControl<TextBox>("ActLike").Text = "0F";
        var tweakers = w.GetControl<WrapPanel>("Tweakers");
        var boxes = tweakers.Children.Cast<Border>().Select(b => (StackPanel)b.Child!).ToList();
        boxes[0].Children.OfType<CheckBox>().Single(x => (string)x.Content! == "Can be jumped on").IsChecked = true;
        var palette = boxes[2].Children.OfType<StackPanel>().Single(p => ((TextBlock)p.Children[1]).Text == "Palette");
        ((NumericUpDown)palette.Children[0]).Value = 5;

        w.GetControl<TabControl>("Tabs").SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("00 00", w.GetControl<TextBox>("EntryBytes").Text);      // two extra bytes, extra bit clear
        w.GetControl<TextBox>("EntryName").Text = "Thing, renamed";
        w.GetControl<TextBox>("EntryBytes").Text = "12 3G";                     // not hex: ignored
        w.GetControl<TextBox>("EntryBytes").Text = "12 34";
        Dispatcher.UIThread.RunJobs();                                          // TextChanged comes posted

        Assert.True(w.GetControl<Button>("SaveButton").IsEnabled);
        w.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(w.GetControl<Button>("SaveButton").IsEnabled);
        var saved = s.CustomSpriteModel("thing")!;
        Assert.Equal(0x0F, saved.ActLike);
        Assert.Equal(0x10, saved.Tweak[0]);
        Assert.Equal(5 << 1, saved.Tweak[2]);
        Assert.Equal("Thing, renamed", saved.Collection[0].Name);
        Assert.Equal([0x12, 0x34, 0], saved.Collection[0].Bytes[..3]);
        w.Close();
    }

    [AvaloniaFact]
    public void a_map16_sheet_click_adds_a_display_tile_and_a_gfx_click_fills_an_own_tile()
    {
        if (!File.Exists(ReferenceRoms.Vanilla)) return;
        var s = Project(new PixiConfig { Type = 1, Map16 = new byte[8] });
        var w = Open(s, 1);

        Click(w, w.GetControl<TileSheetView>("Map16Sheet"), 3, 2);              // tile 023, into a new display
        var tiles = w.GetControl<ListBox>("DisplayTiles");
        Assert.Equal(1, tiles.ItemCount);
        Assert.Equal("023", w.GetControl<TextBox>("TileNumber").Text);
        Click(w, w.GetControl<TileSheetView>("Map16Sheet"), 4, 2);              // the picked tile becomes 024
        Assert.Equal(1, tiles.ItemCount);
        Assert.Equal("024", w.GetControl<TextBox>("TileNumber").Text);

        w.GetControl<TabControl>("Tabs").SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, w.GetControl<ListBox>("OwnTiles").ItemCount);
        Assert.Equal(0, w.GetControl<ListBox>("Quarters").SelectedIndex);
        w.GetControl<NumericUpDown>("QuarterPal").Value = 3;
        Click(w, w.GetControl<TileSheetView>("GfxSheet"), 1, 2);                  // 8x8 tile 021 to the top left
        Assert.Equal(1, w.GetControl<ListBox>("Quarters").SelectedIndex);          // on to the bottom left
        w.GetControl<CheckBox>("QuarterXFlip").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        w.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var saved = s.CustomSpriteModel("thing")!;
        Assert.Equal(0x024, saved.Displays.Single().Tiles.Single().Number);
        Assert.Equal([0x21, 3 << 2, 0x00, 0x40], saved.Map16[..4]);
        w.Close();
    }
}
