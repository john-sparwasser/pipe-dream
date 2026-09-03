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
/// Sprite editing. The rule that matters and is easy to get wrong: sprites are positioned by
/// CELL but selected by PIXEL. A sprite's spawn cell is one 16x16 square while what it draws
/// can be much larger and offset, so a cell-based lasso would miss most of what is on screen.
/// </summary>
public class SpriteModeTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static bool HaveRom => File.Exists(RomPath);

    private static SpriteEdit? Edit(int level = 0x105)
    {
        if (!HaveRom) return null;
        var rom = Rom.Load(RomPath);
        var scene = LevelScene.Build(rom, level);
        return scene.Sprites is null ? null : new SpriteEdit(scene.Sprites, scene.Overlay, vertical: false);
    }

    [Fact]
    public void placing_a_sprite_puts_it_at_the_clicked_cell()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        int before = sp.Sprites.Sprites.Count;

        Assert.True(sp.Place(number: 0x0B, cx: 20, cy: 10));
        Assert.Equal(before + 1, sp.Sprites.Sprites.Count);
        Assert.Equal((20, 10), sp.Sprites.Sprites[^1].Cell(false));
        Assert.Equal(0x0B, sp.Sprites.Sprites[^1].Number);
    }

    /// <summary>Vertical levels swap the axes — the "screen" runs down the level, so a cell's
    /// Y is the absolute coordinate. Getting this backwards scatters sprites sideways.</summary>
    [Fact]
    public void a_vertical_level_swaps_the_placement_axes()
    {
        var s = SpriteEdit.At(number: 1, extra: 0, cx: 3, cy: 40, vert: true);
        Assert.Equal((3, 40), s.Cell(vertical: true));

        var h = SpriteEdit.At(number: 1, extra: 0, cx: 40, cy: 3, vert: false);
        Assert.Equal((40, 3), h.Cell(vertical: false));
    }

    /// <summary>Reordering moves a selection as a block, stops at the ends, and keeps the
    /// selection on the same sprites. Pure list work, so no ROM is needed.</summary>
    [Fact]
    public void reordering_moves_the_selection_as_a_block_and_stops_at_the_ends()
    {
        var data = new SpriteData();
        for (int n = 0; n < 4; n++) data.Sprites.Add(SpriteEdit.At(n, 0, n, 5, vert: false));
        var sp = new SpriteEdit(data, null, vertical: false);
        int[] Numbers() => data.Sprites.Select(s => s.Number).ToArray();

        sp.Selection.Add(1); sp.Selection.Add(2);
        Assert.True(sp.ReorderSelected(+1));
        Assert.Equal([0, 3, 1, 2], Numbers());
        Assert.Equal([2, 3], sp.Selection.Order());

        Assert.False(sp.ReorderSelected(+1));                 // against the end: nothing moved, no undo
        Assert.Equal(1, sp.UndoDepth);

        Assert.True(sp.ReorderSelected(-1));
        Assert.Equal([0, 1, 2, 3], Numbers());
        Assert.True(sp.Undo());
        Assert.Equal([0, 3, 1, 2], Numbers());
    }

    /// <summary>The core rule: selection is by drawn pixels, so a band that touches a sprite's
    /// graphics selects it even when it never touches its spawn cell.</summary>
    [Fact]
    public void selection_is_by_drawn_pixels_not_the_spawn_cell()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        if (sp.Sprites.Sprites.Count == 0) { log.WriteLine("SKIP: level has no sprites"); return; }

        // Band exactly over the first sprite's drawn rectangle.
        var (x0, y0, x1, y1) = sp.PixelRect(0);
        sp.SelectInPixelRect(x0, y0, x1 - x0, y1 - y0);
        Assert.Contains(0, sp.Selection);

        // A band far away selects nothing.
        sp.SelectInPixelRect(x1 + 500, y0, 8, 8);
        Assert.DoesNotContain(0, sp.Selection);
    }

    [Fact]
    public void moving_a_selection_shifts_every_sprite_in_it()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        sp.Place(0x0B, 20, 10);
        sp.Place(0x0B, 22, 10);
        int a = sp.Sprites.Sprites.Count - 2, b = sp.Sprites.Sprites.Count - 1;
        sp.Selection.Add(a); sp.Selection.Add(b);

        Assert.True(sp.MoveSelected(3, 2));
        Assert.Equal((23, 12), sp.Sprites.Sprites[a].Cell(false));
        Assert.Equal((25, 12), sp.Sprites.Sprites[b].Cell(false));
    }

    [Fact]
    public void duplicating_keeps_the_selections_relative_layout()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        sp.Place(0x0B, 10, 5);
        sp.Place(0x0C, 13, 7);
        int a = sp.Sprites.Sprites.Count - 2, b = sp.Sprites.Sprites.Count - 1;
        sp.Selection.Add(a); sp.Selection.Add(b);
        int before = sp.Sprites.Sprites.Count;

        Assert.True(sp.DuplicateSelected(20, 10));
        Assert.Equal(before + 2, sp.Sprites.Sprites.Count);
        // The copies keep the same 3-across, 2-down offset between them.
        var c1 = sp.Sprites.Sprites[before].Cell(false);
        var c2 = sp.Sprites.Sprites[before + 1].Cell(false);
        Assert.Equal((3, 2), (c2.X - c1.X, c2.Y - c1.Y));
        Assert.Equal(2, sp.Selection.Count);              // and the copies are what is selected
    }

    [Fact]
    public void deleting_removes_the_selection_and_undo_brings_it_back()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        sp.Place(0x0B, 20, 10);
        int count = sp.Sprites.Sprites.Count;
        sp.Selection.Add(count - 1);

        Assert.True(sp.DeleteSelected());
        Assert.Equal(count - 1, sp.Sprites.Sprites.Count);

        Assert.True(sp.Undo());
        Assert.Equal(count, sp.Sprites.Sprites.Count);
    }

    /// <summary>Undo can change the list length, so a selection held across it would point at
    /// whatever moved into those slots.</summary>
    [Fact]
    public void undo_drops_the_selection_rather_than_leaving_it_dangling()
    {
        if (Edit() is not { } sp) { log.WriteLine("SKIP: no ROM"); return; }
        sp.Place(0x0B, 20, 10);
        sp.Selection.Add(sp.Sprites.Sprites.Count - 1);
        sp.DeleteSelected();
        sp.Undo();
        Assert.Empty(sp.Selection);
    }

    // ---- through the window ----

    /// <summary>
    /// Dragging a selected sprite moves it, and the press is hit-tested where the sprite is
    /// DRAWN — the spawn cell is usually somewhere else entirely, so testing that instead sent
    /// every drag down the rubber-band path and the selection never moved.
    /// </summary>
    [AvaloniaFact]
    public void dragging_a_selected_sprite_by_its_graphics_moves_it()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = w.GetControl<LevelView>("Canvas");
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);      // sprite mode
        Dispatcher.UIThread.RunJobs();
        if (canvas.Sprites is not { } sp) { log.WriteLine("SKIP: level has no sprites"); return; }
        canvas.Zoom = 1;                 // keep the whole gesture inside the headless viewport
        Dispatcher.UIThread.RunJobs();

        // Grab a sprite by a pixel it DRAWS that is not in its spawn cell — the case that used
        // to fall through to the rubber band. Most sprites draw wider than their one cell.
        int? pick = null, gx = null, gy = null;
        for (int i = 0; i < sp.Sprites.Sprites.Count && pick is null; i++)
        {
            var (x0, y0, x1, y1) = sp.PixelRect(i);
            var c = sp.Sprites.Sprites[i].Cell(false);
            for (int y = y0; y < y1 && pick is null; y += 4)
                for (int x = x0; x < x1; x += 4)
                {
                    if (x > 900 || y > 500 || (x / 16, y / 16) == c) continue;
                    pick = i; gx = x; gy = y; break;
                }
        }
        if (pick is not { } idx) { log.WriteLine("SKIP: no sprite drawn off its spawn cell"); return; }
        int px = gx!.Value, py = gy!.Value;
        var cell = sp.Sprites.Sprites[idx].Cell(false);
        log.WriteLine($"sprite {idx}: cell {cell}, grabbing pixel ({px},{py}) in cell ({px / 16},{py / 16})");

        sp.Selection.Clear();
        sp.Selection.Add(idx);

        // Drag two cells right, one down.
        Point At(int x, int y) => canvas.TranslatePoint(
            new Point(x * canvas.Zoom - canvas.Origin.X, y * canvas.Zoom - canvas.Origin.Y), w)!.Value;
        w.MouseDown(At(px, py), MouseButton.Left);
        w.MouseMove(At(px + 32, py + 16));
        w.MouseUp(At(px + 32, py + 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(idx, sp.Selection);
        Assert.Equal((cell.X + 2, cell.Y + 1), sp.Sprites.Sprites[idx].Cell(false));
        Assert.Equal(1, sp.UndoDepth);        // one drag, one undo entry
    }

    [AvaloniaFact]
    public void esc_toggles_between_object_and_sprite_editing()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = w.GetControl<LevelView>("Canvas");

        Assert.Equal(LevelView.EditMode.Objects, canvas.Mode);
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Sprites, canvas.Mode);

        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(LevelView.EditMode.Objects, canvas.Mode);
    }

    /// <summary>Clicking bare canvas in sprite mode drops the selection — a stale selection is
    /// what turns the next Ctrl+right into a surprise duplicate.</summary>
    [AvaloniaFact]
    public void left_clicking_empty_space_clears_the_sprite_selection()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = w.GetControl<LevelView>("Canvas");
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);      // sprite mode
        Dispatcher.UIThread.RunJobs();
        if (canvas.Sprites is not { } sp || sp.Sprites.Sprites.Count == 0)
        { log.WriteLine("SKIP: level has no sprites"); return; }
        canvas.Zoom = 1;
        Dispatcher.UIThread.RunJobs();

        // A viewport pixel no sprite draws on (with a margin, so rounding cannot land on one).
        int? ex = null, ey = null;
        for (int y = 8; y < 400 && ex is null; y += 16)
            for (int x = 8; x < 900; x += 16)
            {
                bool hit = false;
                for (int i = 0; i < sp.Sprites.Sprites.Count && !hit; i++)
                {
                    var (x0, y0, x1, y1) = sp.PixelRect(i);
                    hit = x >= x0 - 8 && x < x1 + 8 && y >= y0 - 8 && y < y1 + 8;
                }
                if (!hit) { ex = x; ey = y; break; }
            }
        if (ex is not { } px) { log.WriteLine("SKIP: no empty pixel in view"); return; }
        log.WriteLine($"clicking empty pixel ({px},{ey})");

        sp.Selection.Clear();
        sp.Selection.Add(0);

        Point At(int x, int y) => canvas.TranslatePoint(
            new Point(x * canvas.Zoom - canvas.Origin.X, y * canvas.Zoom - canvas.Origin.Y), w)!.Value;
        w.MouseDown(At(px, ey!.Value), MouseButton.Left);
        w.MouseUp(At(px, ey!.Value), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(sp.Selection);
    }

    /// <summary>Ctrl+left picks a selection one sprite at a time instead of lassoing, and toggles
    /// back off. It must not clear what is already selected — that is the plain click's job.</summary>
    [AvaloniaFact]
    public void ctrl_left_click_toggles_one_sprite_in_and_out_of_the_selection()
    {
        if (!HaveRom) { log.WriteLine("SKIP: no ROM"); return; }
        Program.RomPath = RomPath;
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = w.GetControl<LevelView>("Canvas");
        w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);      // sprite mode
        Dispatcher.UIThread.RunJobs();
        if (canvas.Sprites is not { } sp || sp.Sprites.Sprites.Count == 0)
        { log.WriteLine("SKIP: level has no sprites"); return; }
        canvas.Zoom = 1;
        Dispatcher.UIThread.RunJobs();

        // A viewport pixel that hit-tests to exactly one known sprite.
        int? tx = null, ty = null, target = null;
        for (int i = 0; i < sp.Sprites.Sprites.Count && target is null; i++)
        {
            var (x0, y0, x1, y1) = sp.PixelRect(i);
            for (int y = y0; y < y1 && target is null; y += 4)
                for (int x = x0; x < x1; x += 4)
                {
                    if (x > 900 || y > 400 || sp.SpriteAt(x, y) != i) continue;
                    target = i; tx = x; ty = y; break;
                }
        }
        if (target is not { } idx) { log.WriteLine("SKIP: no sprite pixel in view"); return; }
        log.WriteLine($"ctrl-clicking sprite {idx} at ({tx},{ty})");

        // Something else already selected, to prove the toggle adds rather than replaces.
        int other = idx == 0 ? sp.Sprites.Sprites.Count - 1 : 0;
        sp.Selection.Clear();
        if (other != idx) sp.Selection.Add(other);

        Point At(int x, int y) => canvas.TranslatePoint(
            new Point(x * canvas.Zoom - canvas.Origin.X, y * canvas.Zoom - canvas.Origin.Y), w)!.Value;
        void CtrlClick()
        {
            w.MouseDown(At(tx!.Value, ty!.Value), MouseButton.Left, RawInputModifiers.Control);
            w.MouseUp(At(tx!.Value, ty!.Value), MouseButton.Left, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
        }

        CtrlClick();
        Assert.Contains(idx, sp.Selection);
        if (other != idx) Assert.Contains(other, sp.Selection);   // the existing pick survives

        CtrlClick();
        Assert.DoesNotContain(idx, sp.Selection);
        if (other != idx) Assert.Contains(other, sp.Selection);
    }
}
