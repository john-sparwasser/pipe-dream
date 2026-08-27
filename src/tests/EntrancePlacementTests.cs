using Xunit;
using Xunit.Abstractions;

namespace PipeDream.Tests;

/// <summary>Where an entrance puts Mario. The record stores a screen and two indices into the
/// bank-05 tables, never a position, so this is the decode ($05D7D9 / $05D909-$05DA05) read
/// back out.</summary>
public class EntrancePlacementTests(ITestOutputHelper log)
{
    [RealRomFact]
    public void the_tables_are_the_eight_by_sixteen_grid_the_decode_indexes()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        var xs = Enumerable.Range(0, EntrancePlacement.XCount).Select(i => EntrancePlacement.X(rom, 0, i)).ToList();
        var ys = Enumerable.Range(0, EntrancePlacement.YCount).Select(i => EntrancePlacement.Y(rom, i)).ToList();
        log.WriteLine($"X: {string.Join(" ", xs.Select(v => $"{v:X3}"))}");
        log.WriteLine($"Y: {string.Join(" ", ys.Select(v => $"{v:X3}"))}");

        Assert.All(xs, x => Assert.InRange(x, 0, 0xFF));       // X low byte; the screen is separate
        Assert.All(ys, y => Assert.InRange(y, 0, 0x1FF));      // Y is a full 16-bit level position

        // Only FIVE of the eight X offsets are distinct on a horizontal level: indices 4-7 differ
        // from 0-3 only in the table's HIGH byte ($05D758), and the tail overwrites that with
        // the screen. The pairs collapse — a property of the ROM, not a bug here.
        Assert.Equal(5, xs.Distinct().Count());

        // The screen is a whole 256px step on top of the X offset.
        Assert.Equal(EntrancePlacement.X(rom, 0, 3) + 0x300, EntrancePlacement.X(rom, 3, 3));
    }

    /// <summary>A drag lands on the nearest spot the ROM can express, which is the whole reason
    /// the snap exists: there are 8 x 16 of them per screen and nothing in between.</summary>
    [RealRomFact]
    public void a_dropped_position_snaps_to_the_nearest_expressible_one()
    {
        var rom = Rom.Load(TestRom.RealRomPath);
        foreach (int screen in new[] { 0, 1, 7, 0x1F })
            foreach (int xi in new[] { 0, 3, 7 })
            {
                int exact = EntrancePlacement.X(rom, screen, xi);
                var (s, i) = EntrancePlacement.NearestX(rom, exact);
                Assert.Equal(exact, EntrancePlacement.X(rom, s, i));    // an exact spot is kept
                var (s2, i2) = EntrancePlacement.NearestX(rom, exact + 3);
                Assert.Equal(exact, EntrancePlacement.X(rom, s2, i2));  // ...and 3px off returns to it
            }
        foreach (int yi in new[] { 0, 5, 15 })
        {
            int exact = EntrancePlacement.Y(rom, yi);
            Assert.Equal(exact, EntrancePlacement.Y(rom, EntrancePlacement.NearestY(rom, exact)));
        }
    }
}
