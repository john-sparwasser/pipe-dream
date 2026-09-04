using Avalonia.Media;

namespace PipeDream.Ui;

/// <summary>
/// Colours for the CODE-DRAWN overlays — selection rings, rubber bands, brush outlines.
///
/// The canvases render with a DrawingContext rather than styled controls, so they cannot pick
/// up Theme.axaml's brushes through styling. Keeping the values here, mirroring that file,
/// means the artwork's chrome and the window's chrome stay one palette instead of drifting
/// apart every time one of them is adjusted.
/// </summary>
internal static class UiColors
{
    /// <summary>A composed pixel (0xAABBGGRR, little-endian RGBA) as a named Avalonia colour.
    /// Opaque: every path that hands us one is drawing a palette entry, where alpha means
    /// "index 0" rather than a blend.</summary>
    public static Color FromRgba(uint v)
        => Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    /// <summary>"What is active" — matches AccentColor in Theme.axaml.</summary>
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2E7FD4"));

    /// <summary>Selection rings on the canvas: a brighter cyan so they read against artwork.</summary>
    public static readonly IBrush Selection = new SolidColorBrush(Color.Parse("#4FC1E9"));

    /// <summary>A live rubber band, before it settles into a selection.</summary>
    public static readonly IBrush Band = new SolidColorBrush(Color.Parse("#7FD4F5"));

    /// <summary>Grabbing tiles rather than selecting — deliberately a different hue, because
    /// the two gestures look identical otherwise and do very different things.</summary>
    public static readonly IBrush Grab = new SolidColorBrush(Color.Parse("#5FD08A"));

    /// <summary>Where a stamp would land.</summary>
    public static readonly IBrush Brush = new SolidColorBrush(Color.Parse("#8FD0FF"));

    /// <summary>Translucent fill under a selection ring.</summary>
    public static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x50, 0x4F, 0xC1, 0xE9));

    /// <summary>Outline around the colour picker's square and hue strip — matches BorderColor
    /// in Theme.axaml, which the picker cannot reach because it draws itself.</summary>
    public static readonly IBrush PickerEdge = new SolidColorBrush(Color.Parse("#333944"));

    /// <summary>Sprites highlight in their own hue: they overlap objects constantly, and one
    /// colour for both would make a sprite selection unreadable over a selected object.</summary>
    public static readonly IBrush Sprite = new SolidColorBrush(Color.Parse("#6FE0C0"));
    public static readonly IBrush SpriteFill = new SolidColorBrush(Color.FromArgb(0x30, 0x6F, 0xE0, 0xC0));

    /// <summary>The desk behind the level — dark grey with lighter diamonds, the ImGui
    /// editor's DrawDeskBackdrop (0xFF101010 under 0xFF1B1B1B diamonds, half-diagonal 16px,
    /// centres every 32px). A tiled brush from one 32x32 bitmap rather than per-diamond
    /// geometry: the pattern is fixed in SCREEN pixels, so one tile covers any zoom.</summary>
    public static IBrush DeskPattern => desk ??= MakeDesk();
    private static IBrush? desk;

    /// <summary>The tile's pixels, separate from the bitmap so the geometry is testable —
    /// a headless WriteableBitmap cannot be read back.</summary>
    internal static uint[] DeskTile()
    {
        const uint baseC = 0xFF101010u, diamond = 0xFF1B1B1Bu;   // packed RGBA, as composition uses
        var px = new uint[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                px[y * 32 + x] = Math.Abs(x - 16) + Math.Abs(y - 16) <= 16 ? diamond : baseC;
        return px;
    }

    private static IBrush MakeDesk()
        => new ImageBrush(LevelBitmap.FromPixels(DeskTile(), 32, 32))
        {
            TileMode = TileMode.Tile,
            DestinationRect = new Avalonia.RelativeRect(0, 0, 32, 32, Avalonia.RelativeUnit.Absolute),
        };
}
