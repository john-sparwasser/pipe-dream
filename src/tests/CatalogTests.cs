using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The Sprites and Objects drawer tabs: the catalogs you place FROM.
///
/// Two behaviours here are easy to get subtly wrong and invisible when they are:
/// the right-click PRECEDENCE (duplicate a selection, else place an armed catalog object, else
/// stamp the tile brush — read off the ImGui ObjectTool before it was deleted), and the fact that
/// the drawer tab and the canvas edit mode are ONE piece of state, so picking the Sprites tab
/// must switch the canvas into sprite editing exactly as Esc does.
/// </summary>
public class CatalogTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    /// <summary>The shared prepped ROM (see PreppedRom): prep is expensive and a private copy
    /// per class raced once the tests became one assembly.</summary>
    private static string? Prepped => PreppedRom.Path;

    private static (MainWindow W, LevelView C)? Open()
    {
        if (Prepped is not { } path) return null;
        Program.RomPath = path;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (w, w.GetControl<LevelView>("Canvas"));
    }

    private static Point At(LevelView v, Window w, int x, int y)
        => v.TranslatePoint(new Point(x * 16 * v.Zoom + 8 - v.Origin.X,
                                      y * 16 * v.Zoom + 8 - v.Origin.Y), w)!.Value;

    private static LevelEdit EditOf(MainWindow w) => (LevelEdit)typeof(MainWindow)
        .GetField("edit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(w)!;

    private static void SelectTab(MainWindow w, int index)
    {
        w.GetControl<TabStrip>("PaletteTabs").SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    // ---- the catalogs themselves ----

    [Fact]
    public void the_sprite_catalog_is_drawn_with_this_levels_own_graphics()
    {
        if (Prepped is not { } path) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(path);
        var scene = LevelScene.Build(rom, 0x105);

        var items = Catalog.Sprites(rom, scene, 0x105, out var spFiles);
        Assert.NotEmpty(items);
        Assert.Equal(4, spFiles.Length);
        // Names come from the sprite table, so every entry is identifiable in the list.
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Label)));
        // And most of them really render — a catalog of empty boxes would pass a count check.
        int drawn = items.Count(i => i.Thumb is not null);
        log.WriteLine($"{drawn}/{items.Count} sprites rendered, SP {string.Join(" ", spFiles)}");
        Assert.True(drawn > items.Count / 2, $"only {drawn} of {items.Count} sprites rendered");
    }

    /// <summary>
    /// An object's thumbnail comes from the cells it actually writes, diffed against an empty
    /// level — not from its declared rectangle. So every listed object has a real footprint,
    /// and objects that draw nothing in this tileset are not listed at all.
    /// </summary>
    [Fact]
    public void the_object_catalog_lists_only_objects_that_draw_something_here()
    {
        if (Prepped is not { } path) { log.WriteLine("SKIP: no ROM"); return; }
        var rom = Rom.Load(path);
        var scene = LevelScene.Build(rom, 0x105);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = Catalog.Objects(rom, scene);
        log.WriteLine($"{items.Count} objects in {sw.Elapsed.TotalMilliseconds:F0}ms");

        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.NotNull(i.Thumb);
            Assert.True(i.W >= 1 && i.H >= 1, $"object {i.Number:X2} has an empty footprint");
            Assert.InRange(i.Number, 1, 0x3F);
        });

        // On a Direct Map16 ROM these numbers dispatch to the DM16 handlers, which read tile
        // bytes a bare 3-byte record does not carry — the handler runs away. Tiles are placed
        // from the Map16 tab instead, so they must not be offered here.
        if (rom.HasDm16Hijack)
            foreach (int n in new[] { 0x22, 0x23, 0x26, 0x27, 0x28, 0x29 })
                Assert.DoesNotContain(n, items.Select(i => i.Number));
    }

    // ---- through the window ----

    /// <summary>The tab and the edit mode are one state: the Sprites tab IS sprite editing.</summary>
    [AvaloniaFact]
    public void picking_the_sprites_tab_switches_the_canvas_into_sprite_mode()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        Assert.Equal(LevelView.EditMode.Objects, c.Mode);

        SelectTab(w, 1);
        Assert.Equal(LevelView.EditMode.Sprites, c.Mode);
        Assert.True(w.GetControl<DockPanel>("SpritePanel").IsVisible);

        // Map16 and Objects are both layer-1 tabs.
        SelectTab(w, 2);
        Assert.Equal(LevelView.EditMode.Objects, c.Mode);
        Assert.True(w.GetControl<DockPanel>("ObjectPanel").IsVisible);
    }

    /// <summary>Esc still toggles the mode, and has to drag the tab along with it — otherwise
    /// the drawer shows a sprite catalog while the canvas edits objects.</summary>
    [AvaloniaFact]
    public void esc_moves_the_drawer_tab_with_the_edit_mode()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        var tabs = w.GetControl<TabStrip>("PaletteTabs");

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Sprites, c.Mode);
        Assert.Equal(1, tabs.SelectedIndex);

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Objects, c.Mode);
        Assert.Equal(0, tabs.SelectedIndex);
    }

    /// <summary>The Sprites tab stays populated across a level change. The rows are re-made for
    /// the new level (thumbnails are drawn with its GFX), and the list is never left empty: the
    /// adopt cleared the list box but the window's cached rows made the refill think it was done.</summary>
    [AvaloniaFact]
    public void the_sprite_list_refills_after_a_level_change()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, _) = o;
        SelectTab(w, 1);
        var list = w.GetControl<ListBox>("SpriteList");
        var before = list.ItemsSource!.Cast<CatalogRow>().ToList();
        Assert.NotEmpty(before);

        var box = w.GetControl<ComboBox>("LevelBox");
        int first = box.SelectedIndex, other = first == 0x105 ? 0x009 : 0x105;
        box.SelectedIndex = other;
        Dispatcher.UIThread.RunJobs();
        var after = list.ItemsSource?.Cast<CatalogRow>().ToList();
        Assert.NotNull(after);
        Assert.NotEmpty(after);
        Assert.NotSame(before[0], after[0]);          // this level's own rows, not the old ones

        // ...and a level change while another tab is up: the list is intact when the tab returns.
        SelectTab(w, 0);
        box.SelectedIndex = first;
        Dispatcher.UIThread.RunJobs();
        SelectTab(w, 1);
        Assert.NotEmpty(list.ItemsSource!.Cast<CatalogRow>());
    }

    /// <summary>"Loaded only" is LM's "sprites available with the current sprite GFX" filter.</summary>
    [AvaloniaFact]
    public void the_loaded_only_filter_hides_sprites_this_level_cannot_draw()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, _) = o;
        SelectTab(w, 1);
        var list = w.GetControl<ListBox>("SpriteList");
        var box = w.GetControl<CheckBox>("LoadedOnly");

        var shown = list.ItemsSource!.Cast<CatalogRow>().ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, i => Assert.True(i.Loaded));      // filtered: only what will draw

        box.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        var all = list.ItemsSource!.Cast<CatalogRow>().ToList();
        log.WriteLine($"{shown.Count} loaded of {all.Count} total");
        Assert.True(all.Count >= shown.Count);
    }

    /// <summary>
    /// Arming a catalog object changes what RIGHT-click means: it places that object rather
    /// than stamping the tile brush. Getting this precedence wrong is invisible — the click
    /// still "works", it just paints a tile instead of placing the object you picked.
    /// </summary>
    [AvaloniaFact]
    public void arming_a_catalog_object_makes_right_click_place_it_instead_of_stamping()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        SelectTab(w, 2);
        var list = w.GetControl<ListBox>("ObjectList");
        var items = list.ItemsSource!.Cast<CatalogRow>().ToList();
        Assert.NotEmpty(items);

        // Pick an object, then right-click an empty patch of sky with nothing selected.
        var pick = items[0];
        list.SelectedItem = pick;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(pick.Number, c.CatalogObject);

        var edit = EditOf(w);
        edit.Selection.Clear();
        int before = edit.Objects.Count;
        w.MouseDown(At(c, w, 12, 4), MouseButton.Right);
        w.MouseUp(At(c, w, 12, 4), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(before + 1, edit.Objects.Count);
        var placed = edit.Objects[^1];
        Assert.Equal(pick.Number, placed.Number);
        Assert.False(placed.IsDm16, "an armed catalog object must not stamp a Direct Map16 tile");
        Assert.Equal((12, 4), (placed.AbsoluteX, placed.Y));
    }

    /// <summary>Right-click means one thing at a time: picking a Map16 tile re-arms the brush,
    /// which has to disarm the catalog object or the tile pick silently does nothing.</summary>
    [AvaloniaFact]
    public void picking_a_map16_tile_disarms_the_catalog_object()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, c) = o;
        SelectTab(w, 2);
        var list = w.GetControl<ListBox>("ObjectList");
        list.SelectedItem = list.ItemsSource!.Cast<CatalogRow>().First();
        Dispatcher.UIThread.RunJobs();
        Assert.True(c.CatalogObject >= 0);

        // Pick a tile in the drawer, the way the user would.
        SelectTab(w, 0);
        var palette = w.GetControl<Map16PaletteView>("Palette");
        w.MouseDown(palette.TranslatePoint(new Point(8, 8), w)!.Value, MouseButton.Left);
        w.MouseUp(palette.TranslatePoint(new Point(8, 8), w)!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(-1, c.CatalogObject);
        Assert.Null(list.SelectedItem);
    }

    /// <summary>The Map16 canvas mode always feeds from the 8x8 GFX picker, whatever tab was
    /// last chosen — a sprite catalog cannot stamp Map16 quadrants.</summary>
    [AvaloniaFact]
    public void map16_canvas_mode_overrides_the_drawer_tab()
    {
        if (Open() is not { } o) { log.WriteLine("SKIP: no ROM"); return; }
        var (w, _) = o;
        SelectTab(w, 1);
        Assert.True(w.GetControl<DockPanel>("SpritePanel").IsVisible);

        w.GetControl<ToggleButton>("ModeMap16").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(w.GetControl<DockPanel>("ChrPanel").IsVisible);
        Assert.False(w.GetControl<DockPanel>("SpritePanel").IsVisible);
    }
}
