namespace PipeDream.Ui;

/// <summary>
/// Compose a level straight from a ROM, with no editor state involved.
///
/// The ImGui editor assembles the same inputs inside LevelSession, which needs an EditorApp
/// — that entanglement is what makes the current UI untestable, and untangling it is Phase 1
/// of the migration. This is the shape the session state should collapse to: a ROM, a level
/// number, and four phases of composed pixels.
/// </summary>
public sealed record LevelScene(
    uint[]?[] Phases, int Width, int Height, Map16Grid Grid, Level Level, uint[][][] TileCaches)
{
    /// <summary>Parse, render objects, compose all four animation phases. The per-phase work
    /// is independent, so it runs in parallel exactly as the editor does it.</summary>
    public static LevelScene Build(Rom rom, int levelNum)
    {
        var level = LevelParser.Parse(rom, levelNum);
        var grid = ObjectEngine.Render(rom, level);
        int visRows = rom.IsVerticalMode(level.Header.LevelMode) ? grid.Height : 27;

        var phases = new uint[4][];
        var caches = new uint[4][][];
        var backdrop = new uint[4];
        int w = 0, h = 0;
        Parallel.For(0, 4, p =>
        {
            caches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p);
            var pal = Palette.Load(rom, level.Header, levelNum, p);
            backdrop[p] = pal.Rgba[0];
            var (img, pw, ph) = Map16.ComposeLevel(caches[p], pal.Rgba[0], grid, null, null, null, visRows);
            phases[p] = img;
            w = pw; h = ph;
        });
        return new LevelScene(phases, w, h, grid, level, caches) { Backdrop = backdrop };
    }

    /// <summary>The Map16 tile sheet for the palette drawer, 16 tiles per row — the same
    /// composition the level uses, so a tile looks identical in both places.</summary>
    public (uint[] Px, int W, int H) Sheet(int phase = 0) => Map16.ComposeSheet(TileCaches[phase & 3]);

    /// <summary>Backdrop colour behind the level, per phase — the palette's colour 0.</summary>
    public uint[] Backdrop { get; init; } = new uint[4];

    /// <summary>
    /// Recompose ONE cell in every animation phase, after its grid value changed. Painting a
    /// whole level image per mouse-move would be ~13MB of work for one 16x16 tile; this is
    /// the incremental path the ImGui canvas also uses, and the reason a drag stays smooth.
    /// </summary>
    public void RecomposeCell(int cx, int cy)
    {
        int px = cx * 16, py = cy * 16;
        if (px < 0 || py < 0 || px + 16 > Width || py + 16 > Height) return;
        int tile = Grid.Get(cx, cy);

        for (int p = 0; p < 4; p++)
        {
            if (Phases[p] is not { } img) continue;
            uint bg = Backdrop[p];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++) img[(py + y) * Width + px + x] = bg;
            if (tile == Map16Grid.Empty) continue;

            var cache = TileCaches[p];
            // Out-of-range or marker tiles draw as magenta, exactly as the ImGui canvas does,
            // so a bad tile number is visible rather than silently transparent.
            uint[]? t = (tile & ObjectEngine.Marker) != 0 || tile >= cache.Length ? null : cache[tile];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    uint c = t is null ? 0xFFFF00FFu : t[y * 16 + x];
                    if (c != 0) img[(py + y) * Width + px + x] = c;
                }
        }
    }
}
