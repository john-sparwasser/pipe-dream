using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// The Map16 sheet's bank windowing. Every FG tile lives in ONE texture, so a bank is a slice
/// of it rather than a texture of its own — and getting that slice wrong is invisible in the
/// type system: the editor happily allocated tiles past 0x1FFF that no bank ever drew, so
/// pages could be created and never seen. These are the assertions that defect fails.
/// </summary>
public class Map16SheetTests
{
    private const int BankTiles = 0x2000, BankRows = 0x200;

    /// <summary>Sheet height for a given tile count: 16 tiles per row, 16px per row.</summary>
    private static int SheetH(int tiles) => (tiles + 15) / 16 * 16;

    [Fact]
    public void bank_0_shows_the_front_of_the_sheet()
    {
        var (v0, v1, rows, count) = Map16Layout.SheetWindow(0, SheetH(0x300), 0x300);
        Assert.Equal(0f, v0);
        Assert.Equal(1f, v1);                    // the whole sheet: nothing above bank 0 yet
        Assert.Equal(0x300 / 16, rows);
        Assert.Equal(0x300, count);
    }

    /// <summary>The regression: with tiles allocated past 0x1FFF, bank 1 must show the rows
    /// past the first 512 — not nothing, and not bank 0's rows again.</summary>
    [Fact]
    public void bank_1_is_a_window_onto_the_rows_past_bank_0()
    {
        int tiles = 0x2100, h = SheetH(tiles);
        var (v0, v1, rows, count) = Map16Layout.SheetWindow(1, h, tiles);

        Assert.Equal(0x100 / 16, rows);                       // only the 0x100 tiles past 0x2000
        Assert.Equal(0x100, count);
        Assert.True(v0 > 0, "bank 1 must start past the top of the sheet");
        Assert.Equal(BankRows * 16f / h, v0, 5);
        Assert.Equal(1f, v1, 5);                              // ...through to the end of it

        // And it must not overlap bank 0's window, or the two banks show the same tiles.
        var (b0v0, b0v1, _, _) = Map16Layout.SheetWindow(0, h, tiles);
        Assert.Equal(0f, b0v0);
        Assert.True(b0v1 <= v0 + 1e-6f, $"bank 0 ends at {b0v1} but bank 1 starts at {v0}");
    }

    [Fact]
    public void a_bank_with_nothing_allocated_in_it_shows_nothing()
    {
        // 0x300 tiles is entirely inside bank 0, so bank 1 has no rows at all.
        Assert.Equal(0, Map16Layout.SheetWindow(1, SheetH(0x300), 0x300).Rows);
        // Exactly full bank 0 is still nothing for bank 1 — the boundary, not one row of it.
        Assert.Equal(0, Map16Layout.SheetWindow(1, SheetH(BankTiles), BankTiles).Rows);
        // One tile past it is one row.
        Assert.Equal(1, Map16Layout.SheetWindow(1, SheetH(BankTiles + 1), BankTiles + 1).Rows);
    }

    [Fact]
    public void a_bank_never_reports_more_than_it_holds()
    {
        // A full two banks: each reports its own 0x2000, never the sheet total.
        int tiles = BankTiles * 2, h = SheetH(tiles);
        Assert.Equal(BankTiles, Map16Layout.SheetWindow(0, h, tiles).Count);
        Assert.Equal(BankTiles, Map16Layout.SheetWindow(1, h, tiles).Count);
        Assert.Equal(BankRows, Map16Layout.SheetWindow(0, h, tiles).Rows);
        Assert.Equal(BankRows, Map16Layout.SheetWindow(1, h, tiles).Rows);
    }

    [Fact]
    public void banks_without_fg_defs_and_degenerate_input_are_empty_not_wrong()
    {
        Assert.Equal(0, Map16Layout.SheetWindow(2, SheetH(0x2100), 0x2100).Rows);   // BG: own texture
        Assert.Equal(0, Map16Layout.SheetWindow(3, SheetH(0x2100), 0x2100).Rows);
        Assert.Equal(0, Map16Layout.SheetWindow(-1, SheetH(0x300), 0x300).Rows);
        Assert.Equal(0, Map16Layout.SheetWindow(0, 0, 0).Rows);                     // no sheet yet
    }

    /// <summary>Both FG banks are paintable end to end even where nothing is allocated —
    /// that is what makes an empty page ordinary empty tiles instead of a locked region.</summary>
    [Fact]
    public void fg_banks_are_paintable_beyond_what_is_allocated()
    {
        Assert.Equal(BankTiles, Map16Layout.PaintableIn(0, 0x300));
        Assert.Equal(BankTiles, Map16Layout.PaintableIn(1, 0));
        // The BG bank is a fixed table: only its real tiles are paintable.
        Assert.Equal(0x200, Map16Layout.PaintableIn(2, 0x200));
        Assert.Equal(0, Map16Layout.PaintableIn(3, 0));
    }
}
