using PipeDream.Services;
using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// Painting the two background layers.
///
/// The load-bearing rule is that an edit is PER LEVEL. Vanilla shares one background stream
/// across a dozen levels and one layer-3 tilemap across every level of a mode, so an edit that
/// wrote back through the shared thing would repaint levels nobody touched. Layer 3 solves it by
/// growing a tilemap of its own on the first stroke — the same move LM makes; layer 2 cannot,
/// because a background's page byte comes from its address (§10a), so the sharing is kept and
/// the build says so.
///
/// The second rule is that undo is per stroke, not per cell: a drag across twenty cells is one
/// entry, and a drag over cells that already hold the brush is no entry at all.
/// </summary>
public class BackgroundEditTests(ITestOutputHelper log)
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private EditorSession? Open(int level)
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return null; }
        var s = new EditorSession();
        if (!s.OpenRom(Vanilla)) { log.WriteLine(s.Status); return null; }
        s.ShowLevel(level);
        return s;
    }

    // ---- the shared edit model ----

    [Fact]
    public void a_stroke_is_one_undo_entry_and_a_no_op_drag_is_none()
    {
        if (Open(0x105) is not { BgMap: { } map } ) return;
        int before = map.At(3, 4);
        int brush = before == 0x25 ? 0x30 : 0x25;

        for (int x = 3; x < 9; x++) Assert.True(map.Stamp(x, 4, brush));
        Assert.True(map.InStroke);
        Assert.True(map.EndStroke());
        Assert.Equal(1, map.UndoDepth);
        Assert.Equal(brush, map.At(8, 4));

        // Painting the same value again changes nothing, so it must not become an entry —
        // otherwise every pass over finished work costs an undo press to get back through.
        for (int x = 3; x < 9; x++) Assert.False(map.Stamp(x, 4, brush));
        Assert.False(map.EndStroke());
        Assert.Equal(1, map.UndoDepth);

        Assert.True(map.Undo());
        Assert.Equal(before, map.At(3, 4));
        Assert.True(map.Redo());
        Assert.Equal(brush, map.At(3, 4));
    }

    [Fact]
    public void the_two_layers_index_their_own_screens()
    {
        if (Open(0x009) is not { } s) return;

        // Layer 3: four 32x32 screens, so (32,0) is the SECOND screen's first word, not word 32.
        Assert.Equal(0x400, Layer3.CellIndex(32, 0));
        Assert.Equal(0x800, Layer3.CellIndex(0, 32));
        Assert.Equal(0xC21, Layer3.CellIndex(33, 33));
        // ...and it round-trips with the renderer's own word → position mapping.
        for (int i = 0; i < Layer3.MapWords; i++)
        {
            var (x, y) = Layer3.At(i);
            Assert.Equal(i, Layer3.CellIndex(x, y));
        }

        // Layer 2: two 16x27 screens 0x1B0 apart, which is why column 16 is not index 16.
        if (Open(0x105) is not { BgMap: { } bg }) return;
        Assert.NotNull(bg);
        Assert.Equal(EditorSession.BgCols, bg.Cols);
        log.WriteLine($"layer 2 grid {bg.Cols}x{bg.Rows}, layer 3 {s.Layer3Map!.Cols}x{s.Layer3Map.Rows}");
    }

    // ---- the canvas grammar ----

    /// <summary>
    /// Left lassos, right stamps, and the lasso OUTRANKS the drawer's tile — the same precedence
    /// the Map16 canvas runs, where a selection beats the 8x8 brush. A plain left click settles a
    /// one-cell lasso straight away, which is what makes it double as the eyedropper: on layer 3
    /// a cell is a whole BG3 word, so it carries the palette group an existing map uses.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void a_lasso_settles_on_press_and_covers_the_dragged_rectangle()
    {
        var view = new TilemapView { Cols = 32, Rows = 27, CellPx = 16, Zoom = 2 };
        var seen = 0;
        view.SelectionChanged += (_, _) => seen++;

        // A press at (2,3) settles one cell immediately — no drag threshold to find.
        view.BeginSelection(2, 3);
        Assert.Equal((2, 3, 1, 1), view.Selection);
        Assert.Equal((1, 1), view.Brush);

        // Dragging up and left of the anchor still gives a positive rectangle.
        view.ExtendSelection(0, 1);
        Assert.Equal((0, 1, 3, 3), view.Selection);
        Assert.Equal((3, 3), view.Brush);
        Assert.True(seen >= 2);

        view.ClearSelection();
        Assert.Null(view.Selection);
        Assert.Equal((1, 1), view.Brush);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void the_cell_under_a_point_follows_the_zoom()
    {
        var view = new TilemapView { Cols = 32, Rows = 27, CellPx = 16, Zoom = 2 };
        Assert.Equal((0, 0), view.At(new Avalonia.Point(0, 0)));
        Assert.Equal((1, 0), view.At(new Avalonia.Point(32, 0)));     // 16px cell at 2x
        Assert.Equal((3, 2), view.At(new Avalonia.Point(3 * 32 + 5, 2 * 32 + 5)));
        Assert.Null(view.At(new Avalonia.Point(32 * 32, 0)));         // past the last column
        view.Zoom = 1;
        Assert.Equal((2, 0), view.At(new Avalonia.Point(32, 0)));
    }

    // ---- layer 3: the first stroke makes the level's own tilemap ----

    [Fact]
    public void editing_a_shared_layer_3_gives_this_level_a_tilemap_of_its_own()
    {
        if (Open(0x009) is not { Layer3Map: { } map } s) return;
        Assert.False(s.Layer3TilemapImported);          // still on vanilla's (mode, option) pick

        // Somewhere the vanilla map never wrote, so the change is unambiguous.
        Assert.Equal(-1, map.At(60, 60));
        Assert.True(map.Stamp(60, 60, (2 << 10) | 0x41));
        Assert.True(map.EndStroke());

        Assert.True(s.Layer3TilemapImported);
        var raw = s.Rom!.Layer3Tilemaps[0x009];
        int at = Layer3.CellIndex(60, 60) * 2;
        Assert.Equal((2 << 10) | 0x41, raw[at] | (raw[at + 1] << 8));

        // Level 01A is mode 2 option 3 as well, so vanilla hands both levels the SAME tilemap.
        // It must be untouched — that is the whole reason the edit forks a copy.
        Assert.False(s.Rom.Layer3Tilemaps.ContainsKey(0x01A));
        Assert.Equal(-1, Layer3.LevelTilemap(s.Rom, 0x01A, 2, 3)![Layer3.CellIndex(60, 60)]);
    }

    /// <summary>Words the vanilla script never wrote stay unwritten through a save: a flat file
    /// has no way to say "untouched", so they go out as <see cref="Layer3.BlankWord"/> — the
    /// transparent tile SMW's own status bar pads with — rather than as tile 0, which in GFX28 is
    /// a font glyph.
    ///
    /// This used to be 0xFFFF on the reasoning that tile 0x3FF is past the 512 the window holds
    /// and so draws nothing anywhere. That is true of this editor and false of the console, which
    /// fetched the tile out of the tilemap region and drew it in FRONT of layer 1 (0xFFFF sets the
    /// priority bit) — a built map is stamped full-window, so it covered the level in garbage.</summary>
    [Fact]
    public void unwritten_words_survive_the_round_trip_as_nothing()
    {
        if (Open(0x009) is not { Layer3Map: { } map } s) return;
        map.Stamp(1, 1, (2 << 10) | 5);
        map.EndStroke();

        var raw = s.Rom!.Layer3Tilemaps[0x009];
        int at = Layer3.CellIndex(60, 60) * 2;
        Assert.Equal(Layer3.BlankWord, raw[at] | (raw[at + 1] << 8));

        // ...and it draws nothing on the way back in — a real tile, every pixel on colour 0, so
        // the editor and the console agree instead of only the editor being blank.
        var px = s.Layer3CellPixels(Layer3.FromBytes(raw)[Layer3.CellIndex(60, 60)]);
        Assert.NotNull(px);
        Assert.All(px!, p => Assert.Equal(0u, p));
    }

    // ---- the level-canvas preview ----

    /// <summary>
    /// Layer 3 is drawn on the LEVEL canvas by default, composed into the picture rather than
    /// painted over the finished one — the console puts it under the level's own tiles, and
    /// nothing painted on top can go underneath.
    ///
    /// $009 is a ghost house: no Layer 3 Priority in its header, so every one of its layer-3
    /// cells stays behind layer 1, and a cell layer 1 filled has to read identically with the
    /// preview on and off. That is the assertion that fails if this ever becomes an overlay.
    /// </summary>
    [Fact]
    public void the_level_canvas_draws_layer_3_under_the_level()
    {
        if (Open(0x009) is not { } s) return;
        Assert.True(s.PreviewLayer3);                      // on by default: the level draws one
        var previewed = (uint[])s.Phases[0]!.Clone();

        Assert.True(s.SetPreviewLayer3(false));
        Assert.False(s.SetPreviewLayer3(false));           // idempotent: no needless recompose
        var plain = s.Phases[0]!;
        Assert.Equal(plain.Length, previewed.Length);
        Assert.NotEqual(plain, previewed);

        // UNDER: compose layer 1 by itself, and every pixel it actually paints — not every cell
        // it fills, since a Map16 tile is mostly transparent — has to read the same either way.
        var scene = s.Scene!;
        uint backdrop = scene.Backdrop[0];
        var (l1, _, _) = Map16.ComposeLevel(scene.TileCaches[0], backdrop, scene.Grid,
                                            visibleRows: scene.VisibleRows);
        int touched = 0;
        for (int i = 0; i < l1.Length; i++)
            if (l1[i] != backdrop) { Assert.Equal(plain[i], previewed[i]); touched++; }
        Assert.True(touched > 1000, $"layer 1 painted only {touched} pixels — nothing was proved");

        Assert.True(s.SetPreviewLayer3(true));
        Assert.Equal(previewed, s.Phases[0]);
    }

    [Fact]
    public void a_level_with_no_layer_3_previews_nothing()
    {
        if (Open(0x105) is not { } s) return;                // no layer 3 at all
        var previewed = (uint[])s.Phases[0]!.Clone();
        Assert.True(s.SetPreviewLayer3(false));
        Assert.Equal(previewed, s.Phases[0]);
    }

    // ---- layer 2: the edit reaches the level canvas and the built ROM ----

    [Fact]
    public void a_background_edit_is_what_the_whole_editor_reads_and_survives_a_reopen()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdbg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x105);
            var map = s.BgMap!;
            int before = map.At(2, 2);
            int brush = before == 0x25 ? 0x30 : 0x25;

            map.Stamp(2, 2, brush);
            Assert.True(map.EndStroke());
            Assert.True(s.BgTilemapEdited);

            // DecodeBgImage prefers the edit, so the level canvas and the Background tab agree
            // without either of them knowing an edit happened.
            Assert.Equal(brush, LevelParser.DecodeBgImage(s.Rom!, 0x105)![2 * 16 + 2] & 0xFF);

            s.Save();
            var reopened = new EditorSession();
            Assert.True(reopened.OpenProject(s.Project!.FilePath), reopened.Status);
            reopened.ShowLevel(0x105);
            Assert.True(reopened.BgTilemapEdited);
            Assert.Equal(brush, reopened.BgMap!.At(2, 2));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// The build writes the edit back OVER the original stream, because the page byte comes from
    /// the address and moving it would recolour every tile (§10a). It fits when the re-encode is
    /// no longer than what it replaces, and says so when it is not — a background that silently
    /// did not ship would be indistinguishable from one that did.
    /// </summary>
    [Fact]
    public void building_writes_the_background_in_place_or_says_why_not()
    {
        if (!File.Exists(Vanilla)) { log.WriteLine("SKIP: no ROM"); return; }
        string dir = Path.Combine(Path.GetTempPath(), "pdbgb-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x105);
            var map = s.BgMap!;
            // One cell to the blank tile: an edit that can only make the stream shorter.
            int before = map.At(1, 1);
            Assert.NotEqual(BgImage.Blank, before);
            map.Stamp(1, 1, BgImage.Blank);
            Assert.True(map.EndStroke());
            s.Save();

            string status = s.Build();
            log.WriteLine(status);
            string built = Path.Combine(s.Project!.Folder, "build", s.Project.Name + ".smc");
            Assert.True(File.Exists(built), status);

            var rom = Rom.Load(built);
            Assert.Equal(BgImage.Blank, LevelParser.DecodeBgImage(rom, 0x105)![1 * 16 + 1] & 0xFF);
            Assert.DoesNotContain("background edit skipped", status);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// The whole promise behind painting a layer 3: the strokes are saved with the project like
    /// any other level edit, and a build puts them in the ROM. No Save button of its own, no
    /// export step in between — which is exactly why it needed pinning, because "it won't let me
    /// save this" is what the absence of a button looks like from the outside.
    ///
    /// Four hops, because each one has failed somewhere in this codebase before: the stroke
    /// reaches project.pdp, a reopened project has it, the build inserts the file, and the bytes
    /// in that file are the ones that were painted.
    /// </summary>
    [RealRomFact]
    public void a_painted_layer_3_is_saved_with_the_project_and_built_into_the_rom()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdl3-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(Path.Combine(dir, "proj"), Vanilla), s.Status);
            s.ShowLevel(0x009);
            int word = 3 << 10 | 0x1A5;                       // palette 3, a tile from LG4
            Assert.True(s.Layer3Map!.Stamp(9, 9, word));
            Assert.True(s.Layer3Map.EndStroke());
            s.Save();

            string pdp = Path.Combine(dir, "proj", "project.pdp");
            var s2 = new EditorSession();
            Assert.True(s2.OpenProject(pdp), s2.Status);
            s2.ShowLevel(0x009);
            Assert.Equal(word, s2.Layer3Map!.At(9, 9));

            string status = s2.Build();
            var rom = Rom.Load(Path.Combine(dir, "proj", "build", "proj.smc"));
            var bypass = rom.LmLayer3Tilemap(0x009);
            Assert.True(bypass is not null, "the build named no LT3 file: " + status);
            var raw = Gfx.Cached(rom, bypass!.Value.File)!;
            int at = Layer3.CellIndex(9, 9) * 2;
            Assert.Equal(word, raw[at] | (raw[at + 1] << 8));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Export is the mirror of import, so the file it writes has to be one import takes back —
    /// a "save" that only this editor can read is not a save. It exports what the level DRAWS,
    /// so a level still on its mode's shared tilemap exports that rather than refusing.
    /// </summary>
    [RealRomFact]
    public void an_exported_tilemap_is_the_file_import_reads_back()
    {
        if (Open(0x009) is not { Layer3Map: { } map } s) return;
        string path = Path.Combine(Path.GetTempPath(), "pdl3-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        try
        {
            int word = 6 << 10 | 0x0C3;
            map.Stamp(1, 2, word);
            map.EndStroke();
            Assert.True(s.ExportLayer3Tilemap(path), s.Status);
            Assert.Equal(Layer3.MapWords * 2, new FileInfo(path).Length);

            // Round trip through the importer, onto a level that has never been painted.
            s.ShowLevel(0x01A);
            Assert.False(s.Layer3TilemapImported);
            Assert.True(s.ImportLayer3Tilemap(path), s.Status);
            Assert.Equal(word, s.Layer3Map!.At(1, 2));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
