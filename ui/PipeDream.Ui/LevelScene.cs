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
        int w = 0, h = 0;
        Parallel.For(0, 4, p =>
        {
            caches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p);
            var pal = Palette.Load(rom, level.Header, levelNum, p);
            var (img, pw, ph) = Map16.ComposeLevel(caches[p], pal.Rgba[0], grid, null, null, null, visRows);
            phases[p] = img;
            w = pw; h = ph;
        });
        return new LevelScene(phases, w, h, grid, level, caches);
    }

    /// <summary>The Map16 tile sheet for the palette drawer, 16 tiles per row — the same
    /// composition the level uses, so a tile looks identical in both places.</summary>
    public (uint[] Px, int W, int H) Sheet(int phase = 0) => Map16.ComposeSheet(TileCaches[phase & 3]);
}
