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
    /// <summary>
    /// Parse, render objects, and compose all four animation phases at full fidelity: the
    /// background image (or layer-2 object stream) behind layer 1, and the sprite overlay in
    /// front. The per-phase work is independent, so it runs in parallel as the editor does.
    ///
    /// A level's layer 2 is a background image OR an object stream, never both — the pointer's
    /// bank IS the mode (CONTRACT §10), which is why these are an either/or below rather than
    /// two independent layers.
    /// </summary>
    public static LevelScene Build(Rom rom, int levelNum, bool showSprites = true)
    {
        var level = LevelParser.Parse(rom, levelNum);
        var grid = ObjectEngine.Render(rom, level);
        int visRows = rom.IsVerticalMode(level.Header.LevelMode) ? grid.Height : 27;

        var bgImage = LevelParser.DecodeBgImage(rom, levelNum);
        var layer2 = bgImage is null ? ObjectEngine.RenderLayer2(rom, level.Header, levelNum) : null;

        // The OAM capture is expensive and phase-independent, so it happens once.
        SpriteData? sprites = null;
        SpriteOverlay? overlay = null;
        if (showSprites)
        {
            try
            {
                sprites = SpriteData.Parse(rom, levelNum);
                overlay = SpriteOverlay.Build(rom, sprites, level.Header, levelNum);
            }
            catch { /* a level with unreadable sprite data still renders its terrain */ }
        }

        var phases = new uint[4][];
        var caches = new uint[4][][];
        var bgCaches = new uint[4][][];
        var backdrop = new uint[4];
        var palettes = new Palette?[4];
        int w = 0, h = 0;
        Parallel.For(0, 4, p =>
        {
            caches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p);
            var pal = Palette.Load(rom, level.Header, levelNum, p);
            palettes[p] = pal;
            backdrop[p] = pal.Rgba[0];
            if (bgImage is not null) bgCaches[p] = Map16.ComposeAllBg(rom, level.Header, levelNum, p);

            var (img, pw, ph) = Map16.ComposeLevel(caches[p], pal.Rgba[0], grid,
                                                   bgImage, bgCaches[p], layer2, visRows);
            overlay?.Draw(img, pw, ph, pal);
            phases[p] = img;
            w = pw; h = ph;
        });

        return new LevelScene(phases, w, h, grid, level, caches)
        {
            Backdrop = backdrop,
            BgImage = bgImage,
            BgCaches = bgCaches,
            Layer2 = layer2,
            Sprites = sprites,
            Overlay = overlay,
            VisibleRows = visRows,
            Palettes = palettes,
        };
    }

    public ushort[]? BgImage { get; init; }
    public uint[][]?[] BgCaches { get; init; } = new uint[4][][];
    public Map16Grid? Layer2 { get; init; }
    public SpriteData? Sprites { get; init; }
    public SpriteOverlay? Overlay { get; init; }
    public int VisibleRows { get; init; } = 27;

    /// <summary>The Map16 tile sheet for the palette drawer, 16 tiles per row — the same
    /// composition the level uses, so a tile looks identical in both places.</summary>
    public (uint[] Px, int W, int H) Sheet(int phase = 0) => Map16.ComposeSheet(TileCaches[phase & 3]);

    /// <summary>Backdrop colour behind the level, per phase — the palette's colour 0.</summary>
    public uint[] Backdrop { get; init; } = new uint[4];

    /// <summary>
    /// Recompose ONE cell in every animation phase, after its grid value changed. Painting a
    /// whole level image per mouse-move would be ~13MB of work for one 16x16 tile; this is
    /// the incremental path the ImGui canvas also uses, and the reason a drag stays smooth.
    ///
    /// The layering must match a full compose exactly — backdrop, then the background image
    /// (or the layer-2 object stream), then layer 1 — or a painted cell punches a hole
    /// through to the backdrop wherever the background used to show.
    /// </summary>
    public void RecomposeCell(int cx, int cy)
    {
        int px = cx * 16, py = cy * 16;
        if (px < 0 || py < 0 || px + 16 > Width || py + 16 > Height) return;

        for (int p = 0; p < 4; p++)
        {
            if (Phases[p] is not { } img) continue;
            uint bg = Backdrop[p];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++) img[(py + y) * Width + px + x] = bg;

            if (BgImage is { } bgi && BgCaches[p] is { } bgc)
            {
                // 2-screen horizontal repeat, 27-row vertical repeat (CONTRACT §10).
                int within = cx & 0x1F;
                int idx = bgi[(within / 16) * 0x1B0 + (cy % 27) * 16 + (within & 0x0F)];
                Blit(img, bgc[idx & 0x1FF], px, py);
            }
            else if (Layer2 is { } l2) DrawTile(img, l2.Get(cx, cy), TileCaches[p], px, py);

            DrawTile(img, Grid.Get(cx, cy), TileCaches[p], px, py);
        }
    }

    /// <summary>Re-blit the sprite overlay over the whole image. Sprites are drawn last and
    /// can straddle cell boundaries, so a per-cell recompose erases parts of them — the ImGui
    /// canvas re-blits for the same reason after applying dirty cells.</summary>
    public void RedrawOverlay()
    {
        if (Overlay is null) return;
        for (int p = 0; p < 4; p++)
            if (Phases[p] is { } img && Palettes[p] is { } pal) Overlay.Draw(img, Width, Height, pal);
    }

    public Palette?[] Palettes { get; init; } = new Palette?[4];

    private void DrawTile(uint[] img, int tile, uint[][] cache, int px, int py)
    {
        if (tile == Map16Grid.Empty) return;
        // Out-of-range or marker tiles draw magenta, as the ImGui canvas does, so a bad tile
        // number is visible rather than silently transparent.
        uint[]? t = (tile & ObjectEngine.Marker) != 0 || tile >= cache.Length ? null : cache[tile];
        if (t is null)
        {
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++) img[(py + y) * Width + px + x] = 0xFFFF00FFu;
            return;
        }
        Blit(img, t, px, py);
    }

    private void Blit(uint[] img, uint[] tile, int px, int py)
    {
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                uint c = tile[y * 16 + x];
                if (c != 0) img[(py + y) * Width + px + x] = c;
            }
    }
}
