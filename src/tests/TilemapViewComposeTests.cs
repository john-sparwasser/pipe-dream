using Avalonia.Headless.XUnit;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Ui.Tests;

/// <summary>
/// The partial recompose paints exactly what a full one would. It exists so a block in flight
/// and the animation tick can redraw a few hundred cells instead of seventeen thousand, and the
/// one thing that must hold is that nobody can tell which path drew a pixel.
/// </summary>
public class TilemapViewComposeTests
{
    private const int Cols = 12, Rows = 9, Px = 4;

    /// <summary>A grid whose cell values are a mutable array, each value a flat colour with a
    /// transparent corner, and an overlay that marks every cell whose value is odd.</summary>
    private static (TilemapView View, int[] Cells) Grid(uint backdrop = 0xFF101010)
    {
        var cells = new int[Cols * Rows];
        for (int i = 0; i < cells.Length; i++) cells[i] = i % 7;
        var view = new TilemapView { Cols = Cols, Rows = Rows, CellPx = Px, Backdrop = backdrop };
        view.CellAt = (c, r) => cells[r * Cols + c];
        view.CellPixels = v =>
        {
            var px = new uint[Px * Px];
            Array.Fill(px, 0xFF000000u | (uint)(v * 0x203040));
            px[0] = 0;                                        // colour 0: the backdrop shows through
            return px;
        };
        view.OverlayPixels = (c, r) =>
        {
            if (cells[r * Cols + c] is var v && (v < 0 || v % 2 == 0)) return null;
            var px = new uint[Px * Px];
            px[Px * Px - 1] = 0xFFFFFFFFu;
            return px;
        };
        return (view, cells);
    }

    [AvaloniaFact]
    public void a_region_recompose_matches_a_full_one()
    {
        var (view, cells) = Grid();
        view.ComposeForTests();
        var before = (uint[])view.SurfaceForTests!.Clone();

        // Change a block of cells, and redraw only that block — plus one cell it did not touch,
        // which must come out unchanged.
        for (int r = 2; r < 5; r++) for (int c = 3; c < 7; c++) cells[r * Cols + c] += 3;
        view.InvalidateRegion((3, 2, 4, 3));
        view.InvalidateRegion((0, 0, 1, 1));
        view.ComposeForTests();
        var partial = (uint[])view.SurfaceForTests!.Clone();
        var partialOver = (uint[])view.OverlaySurfaceForTests!.Clone();

        // The same cells, drawn from scratch by a fresh view.
        var (fresh, freshCells) = Grid();
        Array.Copy(cells, freshCells, cells.Length);
        fresh.ComposeForTests();

        Assert.Equal(fresh.SurfaceForTests, partial);
        Assert.Equal(fresh.OverlaySurfaceForTests, partialOver);
        Assert.NotEqual(before, partial);                     // and something did change
    }

    [AvaloniaFact]
    public void a_predicate_recompose_matches_a_full_one()
    {
        var (view, cells) = Grid();
        view.ComposeForTests();

        // Every cell whose value is 4 becomes 5 — scattered across the grid, the way the sea is.
        for (int i = 0; i < cells.Length; i++) if (cells[i] == 4) cells[i] = 5;
        view.InvalidateWhere((c, r) => cells[r * Cols + c] == 5);
        view.ComposeForTests();

        var (fresh, freshCells) = Grid();
        Array.Copy(cells, freshCells, cells.Length);
        fresh.ComposeForTests();
        Assert.Equal(fresh.SurfaceForTests, view.SurfaceForTests);
        Assert.Equal(fresh.OverlaySurfaceForTests, view.OverlaySurfaceForTests);
    }

    /// <summary>A cell whose new value draws LESS than its old one — a tile with art replaced by
    /// one without, or an overlay mark that goes away — must lose the old pixels, not keep them
    /// under the new ones.</summary>
    [AvaloniaFact]
    public void a_repainted_cell_forgets_what_it_held()
    {
        var (view, cells) = Grid();
        cells[0] = 1;                                         // odd: has an overlay mark
        view.ComposeForTests();
        Assert.NotEqual(0u, view.OverlaySurfaceForTests![(Px - 1) * (Cols * Px) + Px - 1]);

        cells[0] = -1;                                        // nothing here at all now
        view.InvalidateRegion((0, 0, 1, 1));
        view.ComposeForTests();
        // Backdrop everywhere in the cell, and the overlay mark gone.
        for (int y = 0; y < Px; y++)
            for (int x = 0; x < Px; x++)
            {
                Assert.Equal(0xFF101010u, view.SurfaceForTests![y * (Cols * Px) + x]);
                Assert.Equal(0u, view.OverlaySurfaceForTests![y * (Cols * Px) + x]);
            }
    }
}
