using Avalonia;
using Avalonia.Input;

namespace PipeDream.Ui;

/// <summary>
/// The open and closed hand, drawn here rather than asked of the platform: neither Windows nor
/// Avalonia has a stock grab pair (StandardCursorType stops at Hand and DragMove), and a probe
/// you pick up and put down wants to SHOW that it is held. Pixel art keeps them crisp at the one
/// size a cursor is, and keeps them identical on every platform.
///
/// Both share a hot spot, so pressing does not make the pointer jump — only the fingers close.
/// </summary>
internal static class HandCursors
{
    // '#' outline, 'o' fill, '.' transparent. The outline is what makes them readable over
    // pixel art, which is as likely to be white as dark.
    private static readonly string[] Open =
    [
        "...#.#.#.#......",
        "..#o#o#o#o#.....",
        "..#o#o#o#o#.....",
        "..#o#o#o#o#.....",
        "..#o#o#o#o#.....",
        "..#o#o#o#o#.....",
        "..#ooooooooo#...",
        "#.#ooooooooo#...",
        "#o#ooooooooo#...",
        "#ooooooooooo#...",
        ".#oooooooooo#...",
        "..#ooooooooo#...",
        "..#ooooooooo#...",
        "...#ooooooo#....",
        "....#######.....",
        "................",
    ];

    private static readonly string[] Closed =
    [
        "................",
        "................",
        "................",
        "....#.#.#.#.....",
        "...#o#o#o#o#....",
        "..##ooooooooo#..",
        ".#oo#oooooooo#..",
        ".#ooooooooooo#..",
        "..#ooooooooooo#.",
        "..#ooooooooooo#.",
        "...#ooooooooo#..",
        "...#ooooooooo#..",
        "....#ooooooo#...",
        ".....#######....",
        "................",
        "................",
    ];

    /// <summary>The middle of the palm, which both shapes have in the same place.</summary>
    private static readonly PixelPoint HotSpot = new(7, 8);

    private static Cursor? open, closed;
    public static Cursor OpenHand => open ??= Build(Open);
    public static Cursor ClosedHand => closed ??= Build(Closed);

    private static Cursor Build(string[] art)
    {
        int h = art.Length, w = art[0].Length;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                // Rgba8888, premultiplied: opaque black or opaque white, or nothing at all.
                px[y * w + x] = art[y][x] switch { '#' => 0xFF000000u, 'o' => 0xFFFFFFFFu, _ => 0u };
        return new Cursor(LevelBitmap.FromPixels(px, w, h), HotSpot);
    }
}
