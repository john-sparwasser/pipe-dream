using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The Map16 properties inspector: what a tile acts as, its palette row, priority and flips.
///
/// These apply to a SELECTION, and the rule that matters is that the controls reflect the first
/// tile and write to all of them — the only sane behaviour when a lasso can cover tiles that
/// disagree — with the whole set landing in one undo entry.
/// </summary>
public class Map16PropsTests(ITestOutputHelper log)
{
    private static string RomPath => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects",
        ".resources", "SMW.smc");

    private static readonly Lazy<string?> Prepped = new(() =>
    {
        if (!File.Exists(RomPath)) return null;
        string tmp = Path.Combine(Path.GetTempPath(), "pdui-m16props.smc");
        if (!File.Exists(tmp))
        {
            File.Copy(RomPath, tmp, overwrite: true);
            if (RomPrep.PrepInPlace(tmp) is not null) return null;
        }
        return tmp;
    });

    /// <summary>A fresh ROM copy per test: these write definitions and acts-like entries, and a
    /// shared one would make the order of the tests matter.</summary>
    private static Map16Edit? Edit()
    {
        if (Prepped.Value is not { } p) return null;
        string mine = Path.Combine(Path.GetTempPath(), $"pdui-m16-{Guid.NewGuid():N}.smc");
        File.Copy(p, mine, overwrite: true);
        return new Map16Edit(Rom.Load(mine), tileset: 1, project: null);
    }

    [Fact]
    public void a_palette_change_applies_to_every_selected_tile_as_one_undo_step()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        int[] tiles = [0x100, 0x101, 0x110, 0x111];
        foreach (int t in tiles) Assert.Null(m16.EnsurePage(t));
        m16.EndStroke();
        int depth = m16.UndoDepth;

        m16.Transform(tiles, w => (ushort)((w.Raw & ~0x1C00) | (5 << 10)));
        Assert.Equal(depth + 1, m16.UndoDepth);            // one entry for the whole block
        foreach (int t in tiles)
            Assert.All(m16.ReadDef(t)!, w => Assert.Equal(5, w.Palette));

        Assert.True(m16.Undo());
        foreach (int t in tiles)
            Assert.All(m16.ReadDef(t)!, w => Assert.NotEqual(5, w.Palette));
    }

    /// <summary>Priority is a per-quadrant bit, so setting it on a tile means setting it on all
    /// four — a checkbox that only touched the top-left would be silently half-applied.</summary>
    [Fact]
    public void priority_applies_to_all_four_quadrants()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x130;
        Assert.Null(m16.EnsurePage(tile));
        m16.EndStroke();

        m16.Transform([tile], w => (ushort)(w.Raw | 0x2000));
        Assert.All(m16.ReadDef(tile)!, w => Assert.True(w.Priority));

        m16.Transform([tile], w => (ushort)(w.Raw & ~0x2000));
        Assert.All(m16.ReadDef(tile)!, w => Assert.False(w.Priority));
    }

    /// <summary>A flip has to swap the quadrant PAIRS and toggle the flip flag. Doing only one
    /// mirrors the arrangement but not the art, or the art but not the arrangement — and either
    /// looks almost right, which is the worst kind of wrong.</summary>
    [Fact]
    public void flipping_swaps_the_quadrants_and_the_flags_together()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x120;
        Assert.Null(m16.EnsurePage(tile));
        // Four distinguishable quadrants, all flags clear.
        for (int q = 0; q < 4; q++) m16.StampQuad(tile, q, (ushort)(0x10 + q));
        m16.EndStroke();

        m16.Flip([tile], vertical: false);
        var def = m16.ReadDef(tile)!;
        // Visual order is TL, TR, BL, BR: a horizontal flip swaps left and right within each row.
        Assert.Equal(0x11, def[0].Raw & 0x3FF);
        Assert.Equal(0x10, def[1].Raw & 0x3FF);
        Assert.Equal(0x13, def[2].Raw & 0x3FF);
        Assert.Equal(0x12, def[3].Raw & 0x3FF);
        Assert.All(def, w => Assert.True((w.Raw & 0x4000) != 0, "the X-flip flag was not toggled"));

        m16.Flip([tile], vertical: false);                 // and it is its own inverse
        var back = m16.ReadDef(tile)!;
        Assert.Equal(0x10, back[0].Raw & 0x3FF);
        Assert.All(back, w => Assert.True((w.Raw & 0x4000) == 0));
    }

    [Fact]
    public void a_vertical_flip_swaps_the_rows_and_toggles_the_y_flag()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        const int tile = 0x121;
        Assert.Null(m16.EnsurePage(tile));
        for (int q = 0; q < 4; q++) m16.StampQuad(tile, q, (ushort)(0x20 + q));
        m16.EndStroke();

        m16.Flip([tile], vertical: true);
        var def = m16.ReadDef(tile)!;
        Assert.Equal(0x22, def[0].Raw & 0x3FF);            // bottom row moves to the top
        Assert.Equal(0x23, def[1].Raw & 0x3FF);
        Assert.Equal(0x20, def[2].Raw & 0x3FF);
        Assert.Equal(0x21, def[3].Raw & 0x3FF);
        Assert.All(def, w => Assert.True((w.Raw & 0x8000) != 0, "the Y-flip flag was not toggled"));
    }

    /// <summary>Acts-like is an FG concept and needs Lunar Magic's table: BG tiles have no entry
    /// at all, and writing one would run past the end of the table.</summary>
    [Fact]
    public void acts_like_is_refused_for_bg_tiles()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        log.WriteLine($"acts-like table present: {m16.HasActsAs}");
        Assert.Null(m16.ActsAs(0x4000));
        Assert.False(m16.SetActsAs([0x4000], 0x130));
    }

    [Fact]
    public void acts_like_round_trips_for_fg_tiles()
    {
        if (Edit() is not { } m16) { log.WriteLine("SKIP: no ROM"); return; }
        if (!m16.HasActsAs) { log.WriteLine("SKIP: base has no LM acts-like table"); return; }

        Assert.True(m16.SetActsAs([0x100], 0x130));
        Assert.Equal(0x130, m16.ActsAs(0x100));
        Assert.False(m16.SetActsAs([0x100], 0x130));       // already says that

        // The value is masked to the table's 14 bits rather than overflowing into the next entry.
        Assert.True(m16.SetActsAs([0x100], 0xFFFF));
        Assert.Equal(0x3FFF, m16.ActsAs(0x100));
    }
}
