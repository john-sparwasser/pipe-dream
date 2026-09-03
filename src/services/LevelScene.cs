namespace PipeDream.Services;

/// <summary>
/// A level composed for display: four animation phases of RGBA pixels, plus everything needed
/// to recompose part of it after an edit.
///
/// The ImGui editor assembles the same inputs inside LevelSession, which needs an EditorApp —
/// that entanglement is what makes the current UI untestable, and untangling it is Phase 1 of
/// the migration. This is the shape the session state collapses to: a ROM, a level number,
/// and the pixels.
/// </summary>
public sealed class LevelScene
{
    public required uint[]?[] Phases { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Level Level { get; init; }
    public required uint[][][] TileCaches { get; init; }

    /// <summary>Layer-1 Map16 grid. Replaced wholesale when the object list is re-rendered,
    /// which is what an edit actually does — the grid is a projection of the objects.</summary>
    public Map16Grid Grid { get; set; } = null!;

    public uint[] Backdrop { get; init; } = new uint[4];
    public Palette?[] Palettes { get; init; } = new Palette?[4];
    public ushort[]? BgImage { get; init; }
    public uint[][]?[] BgCaches { get; init; } = new uint[4][][];

    /// <summary>Layer 2's Map16 grid, or null when layer 2 is a background image instead — the
    /// two are exclusive, because the pointer's bank byte IS the mode (CONTRACT §10). Settable
    /// because layer 2 is editable: its grid is a projection of an object stream, exactly as
    /// layer 1's is.</summary>
    public Map16Grid? Layer2 { get; set; }
    public SpriteData? Sprites { get; init; }

    /// <summary>The sprite overlay drawn over layer 1. Settable because an EDITED sprite list
    /// replaces the ROM's parse: the scene is composed without sprites and the edited overlay
    /// put in here, so later per-cell recomposes still redraw the right sprites.</summary>
    public SpriteOverlay? Overlay { get; set; }
    public int VisibleRows { get; init; } = 27;

    /// <summary>
    /// Parse, render objects, and compose all four animation phases at full fidelity: the
    /// background image (or layer-2 object stream) behind layer 1, and the sprite overlay in
    /// front. The per-phase work is independent, so it runs in parallel as the editor does.
    ///
    /// A level's layer 2 is a background image OR an object stream, never both — the pointer's
    /// bank IS the mode (CONTRACT §10), which is why these are an either/or below.
    /// </summary>
    /// <summary>
    /// What to do about sprites when composing. Three states rather than a bool because the
    /// expensive part (capturing each sprite's OAM by interpreting its GFX routine) is worth
    /// paying for even when the overlay is HIDDEN — selection hit-tests against it — but not
    /// worth paying at all when the caller has an edited sprite list to draw instead.
    /// </summary>
    public enum SpriteDraw { Compose, ParseOnly, Skip }

    /// <param name="previewLayer3">Compose the level's layer 3 into the canvas as well — behind
    /// layer 2 and layer 1, or in front when the header gives it priority. Off by default: it is
    /// a view setting, and the level canvas is otherwise exactly what the level's own data
    /// draws.</param>
    public static LevelScene Build(Rom rom, int levelNum, SpriteDraw sprites = SpriteDraw.Compose,
                                   IReadOnlyDictionary<int, ushort>? paletteEdits = null,
                                   bool previewLayer3 = false)
    {
        var level = LevelParser.Parse(rom, levelNum);
        var grid = ObjectEngine.Render(rom, level);
        int visRows = grid.Height;          // the engine sizes the grid to the level: LM height, or 27

        var bgImage = LevelParser.DecodeBgImage(rom, levelNum);
        var layer2 = bgImage is null ? ObjectEngine.RenderLayer2(rom, level.Header, levelNum) : null;

        // The OAM capture is expensive and phase-independent, so it happens once.
        SpriteData? spriteData = null;
        SpriteOverlay? overlay = null;
        if (sprites != SpriteDraw.Skip)
        {
            try
            {
                spriteData = SpriteData.Parse(rom, levelNum);
                overlay = SpriteOverlay.Build(rom, spriteData, level.Header, levelNum);
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
            // The palette comes FIRST and is handed to the tile composers: an edited colour has
            // to reach the tile caches, not just the backdrop, or the level keeps showing the
            // ROM's colours while the swatch shows the new one.
            var pal = Palette.Load(rom, level.Header, levelNum, p);
            if (paletteEdits is not null)
                foreach (var (i, c) in paletteEdits)
                {
                    if (i is < 0 or > 255) continue;
                    pal.Bgr[i] = c;
                    pal.Rgba[i] = Palette.ToRgba(c);
                }
            caches[p] = Map16.ComposeAll(rom, level.Header, levelNum, p, pal);
            palettes[p] = pal;
            backdrop[p] = pal.Rgba[0];
            // Always, not only when layer 2 is a background image: these 0x200 defs are the Map16
            // editor's bank 2 and the drawer's BG sheet, which a level whose layer 2 is an object
            // stream still edits — and until now saw as a black bank.
            bgCaches[p] = Map16.ComposeAllBg(rom, level.Header, levelNum, p, pal);

            // Layer 3 does not animate, so one surface serves every phase — but it is rendered
            // per phase anyway because the palette can differ, and it is cheap next to the level.
            Map16.Layer3Draw? l3 = null;
            if (previewLayer3
                && Layer3.LevelTilemap(rom, levelNum, level.Header.LevelMode,
                                       Layer3.Option(rom, levelNum)) is { } l3map)
            {
                // The console's own rules, as far as a still picture can carry them. The header's
                // Layer 3 Priority is mode 1's BG3-priority bit ($3E = $09 rather than $01 at
                // $05:8570), which does NOT lift the whole layer: it lifts the cells whose own
                // priority bit is set, and leaves the rest at the very back. Colour math (the
                // level's CGADSUB / Subscreen settings) is what lets it show THROUGH the level
                // at all, which is why a level with neither is meant to look hidden.
                var tiles = Layer3.Tiles(rom, levelNum);
                var adv = rom.LmLayer3Advanced(levelNum);
                var screens = Layer3.ScreenSetup(rom, level.Header.LevelMode, adv);
                // Where the level parks layer 3 to begin with. With a scroll rate of None that is
                // where it stays, so the canvas is exact; with any other rate it is the frame the
                // level opens on, which is the one frame a still picture can be right about.
                var (ox, oy) = adv is { } a
                    ? (Layer3.XPositions[a.XPos & 3] * 16, a.Y * 16) : (0, 0);
                bool bg3Priority = level.Header.Layer3Priority != 0;
                // Backdrop 0: the gaps have to stay transparent, or the preview would paint the
                // level's own back-area colour over layer 2.
                var (lp, lw, lh) = Layer3.Render(l3map, tiles, pal, 0, bg3Priority ? 0 : null);
                var front = bg3Priority ? Layer3.Render(l3map, tiles, pal, 0, 1).Px : null;
                l3 = new Map16.Layer3Draw(lp, front, lw, lh, screens, ox, oy);
            }

            var (img, pw, ph) = Map16.ComposeLevel(caches[p], pal.Rgba[0], grid,
                                                   bgImage, bgCaches[p], layer2, visRows, l3);
            if (sprites == SpriteDraw.Compose) overlay?.Draw(img, pw, ph, pal);
            phases[p] = img;
            w = pw; h = ph;
        });

        return new LevelScene
        {
            Phases = phases, Width = w, Height = h, Level = level, TileCaches = caches,
            Grid = grid, Backdrop = backdrop, Palettes = palettes,
            BgImage = bgImage, BgCaches = bgCaches, Layer2 = layer2,
            Sprites = spriteData, Overlay = overlay, VisibleRows = visRows,
        };
    }

    /// <summary>The Map16 tile sheet for the palette drawer, 16 tiles per row — the same
    /// composition the level uses, so a tile looks identical in both places.</summary>
    public (uint[] Px, int W, int H) Sheet(int phase = 0) => Map16.ComposeSheet(TileCaches[phase & 3]);

    /// <summary>The layer-2 background drawn as pixels — <paramref name="cols"/> columns wide
    /// (two screens side by side is how it repeats) by <paramref name="rows"/> rows. Empty when
    /// layer 2 is an object stream rather than a background image.</summary>
    public (uint[] Px, int W, int H) BgSurface(int cols, int rows, int phase)
    {
        if (BgImage is not { } bg || BgCaches[phase & 3] is not { } cache) return ([], 0, 0);
        int w = cols * 16, h = rows * 16;
        var img = new uint[w * h];
        // Colour 0 is transparent in a BG tile, and what shows through it in game is the back
        // area colour — the same backdrop ComposeLevel lays down before layer 2.
        if (Palettes[phase & 3] is { } pal) Array.Fill(img, pal.Rgba[0]);
        Map16.DrawBgImage(img, w, h, cols, bg, cache);
        return (img, w, h);
    }

    /// <summary>The BG Map16 defs as a picker sheet — the fixed 0x200 at $0D9100.</summary>
    public (uint[] Px, int W, int H) BgSheet(int phase = 0)
        => BgCaches[phase & 3] is { } cache ? Map16.ComposeSheet(cache) : ([], 0, 0);

    /// <summary>LM's default-empty definition (4 × word 0x1004, the bytes GrowRange fills a new
    /// page with) drawn in this level's graphics: what every FG page that has no defs yet looks
    /// like, so the picker shows the same thing before and after allocation.</summary>
    public uint[]? Placeholder(Rom rom, int levelNum, int phase)
        => Palettes[phase & 3] is { } pal
            ? Map16.Compose(Map16.LmExtendedDef(rom, -1), Fg(rom, levelNum, phase & 3).Fetch, pal) : null;

    /// <summary>GFX per phase, kept across recolours. Loading it is the expensive half of a
    /// compose and a colour change cannot move it.</summary>
    private readonly Gfx.FgTiles?[] fg = new Gfx.FgTiles?[4];

    /// <summary>
    /// Recompose every phase for a changed PALETTE, in place. A colour is an input to
    /// composition — it is baked into each tile's pixels — so there is no way to tint the
    /// result afterwards; the tiles genuinely have to be rebuilt.
    ///
    /// What this skips is everything a colour cannot have changed: parsing the level, rendering
    /// the object streams, and capturing the sprite OAM. Rebuilding the whole scene costs ~75ms
    /// a colour, which is a slideshow to drag against; this is roughly a quarter of that, and
    /// less again after the first call once the GFX is cached.
    ///
    /// <paramref name="onlyPhase"/> narrows it to the one phase on screen. Composing a phase is
    /// bandwidth-bound — 13.5MB in and out for a full-width level — so doing all four costs
    /// nearly four times as much even in parallel. Tile animation is off unless the user turns
    /// it on, which makes the other three phases invisible work during a colour drag; they are
    /// brought back up to date when the drag ends.
    /// </summary>
    public void Repalette(Rom rom, int levelNum, IReadOnlyDictionary<int, ushort>? edits,
                          int? onlyPhase = null)
    {
        void Phase(int p)
        {
            var pal = PaletteFor(rom, levelNum, p, edits);
            var tiles = Fg(rom, levelNum, p);

            Palettes[p] = pal;
            Backdrop[p] = pal.Rgba[0];
            TileCaches[p] = Map16.ComposeAll(rom, Level.Header, tiles, pal);
            BgCaches[p] = Map16.ComposeAllBg(rom, tiles, pal);

            Phases[p] = Map16.ComposeLevelInto(Phases[p] ?? new uint[Width * Height],
                                               TileCaches[p], pal.Rgba[0], Grid,
                                               BgImage, BgCaches[p], Layer2, VisibleRows);
            DrawOverlay(p);
        }

        if (onlyPhase is { } q) Phase(q & 3);
        else Parallel.For(0, 4, Phase);
    }

    private Palette PaletteFor(Rom rom, int levelNum, int p, IReadOnlyDictionary<int, ushort>? edits)
    {
        var pal = Palette.Load(rom, Level.Header, levelNum, p);
        if (edits is not null)
            foreach (var (i, c) in edits)
            {
                if (i is < 0 or > 255) continue;
                pal.Bgr[i] = c;
                pal.Rgba[i] = Palette.ToRgba(c);
            }
        return pal;
    }

    /// <summary>This level's 8x8 graphics for one animation phase, cached. Public because the
    /// 8x8 picker draws the same tiles the level does — loading its own copy per phase would
    /// re-decode every GFX slot four times for a sheet that is already sitting here.</summary>
    public Gfx.FgTiles Fg(Rom rom, int levelNum, int p)
        => fg[p] ??= Gfx.FgTiles.Load(rom, Level.Header.Tileset, levelNum, p);

    /// <summary>Drop the cached GFX, for the one thing a colour or a definition cannot change:
    /// the graphics themselves being edited or repointed.</summary>
    public void InvalidateGfx() => Array.Clear(fg);

    /// <summary>
    /// Recompose only the named Map16 tiles and only the cells that use them. Editing one
    /// definition rebuilt the entire scene — parse, objects, sprite OAM, all 512 tiles and every
    /// pixel of a 13.5MB image, four times over — for a change that touches 256 pixels of art.
    ///
    /// An empty set is a real answer, not a no-op to be defended against: changing a tile's
    /// acts-like byte commits Map16 bytes and changes nothing you can see.
    /// </summary>
    public void RecomposeTiles(Rom rom, int levelNum, IReadOnlyCollection<int> tiles,
                               IReadOnlyDictionary<int, ushort>? edits)
    {
        if (tiles.Count == 0) return;
        var set = tiles as HashSet<int> ?? [.. tiles];

        // Painting an empty page allocated it: the caches still have the old tile count, and
        // ComposeInto ignores tiles past its end — which is how a freshly painted page stayed
        // black. Grow them to the ROM's count first; the new tail is composed like any edit.
        int count = rom.Map16TileCount;
        for (int p = 0; p < 4; p++)
            if (count > TileCaches[p].Length)
            {
                set.UnionWith(Enumerable.Range(TileCaches[p].Length, count - TileCaches[p].Length));
                Array.Resize(ref TileCaches[p], count);
            }

        // A BG definition (0x4000+) lives in its own caches; ComposeInto ignores it, so the bank-2
        // sheet kept showing the old tile until something rebuilt the scene.
        bool bgEdited = set.Any(t => t >= Map16.BgTileBase);
        Parallel.For(0, 4, p =>
        {
            var pal = PaletteFor(rom, levelNum, p, edits);
            Palettes[p] = pal;
            var tiles = Fg(rom, levelNum, p);
            Map16.ComposeInto(TileCaches[p], rom, Level.Header, tiles, pal, set);
            if (bgEdited) BgCaches[p] = Map16.ComposeAllBg(rom, tiles, pal);
        });
        // A background image is drawn from those caches, so the level shows the edit too.
        if (bgEdited && BgImage is not null)
            for (int p = 0; p < 4; p++)
                Phases[p] = Map16.ComposeLevelInto(Phases[p] ?? new uint[Width * Height], TileCaches[p],
                                                   Palettes[p].Rgba[0], Grid, BgImage, BgCaches[p], Layer2, VisibleRows);

        // The grid is small (a full-width level is under 14k cells) and scanning it beats
        // tracking a tile→cells index that every object edit would have to maintain.
        for (int y = 0; y < Grid.Height; y++)
            for (int x = 0; x < Grid.Width; x++)
                if (set.Contains(Grid.Get(x, y)) || (Layer2 is { } l2 && set.Contains(l2.Get(x, y))))
                    RecomposeCell(x, y);
        RedrawOverlay();
    }

    /// <summary>
    /// Recompose ONE cell in every animation phase. Composing the whole level per edit would
    /// be ~13MB of work for one 16x16 tile; this is the incremental path the ImGui canvas
    /// also uses.
    ///
    /// The layering must match a full compose exactly — backdrop, then the background image
    /// (or the layer-2 stream), then layer 1 — or a repainted cell punches a hole through to
    /// the backdrop wherever the background used to show.
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
    /// straddle cell boundaries, so a per-cell recompose clips them — the ImGui canvas
    /// re-blits after applying dirty cells for the same reason.</summary>
    public void RedrawOverlay()
    {
        for (int p = 0; p < 4; p++) DrawOverlay(p);
    }

    private void DrawOverlay(int p)
    {
        if (Overlay is { } o && Phases[p] is { } img && Palettes[p] is { } pal)
            o.Draw(img, Width, Height, pal);
    }

    /// <summary>Swap in a re-rendered grid and repaint only the cells that actually changed.
    /// Returns how many did — an edit that touches six cells should cost six cells of work,
    /// not a full compose, however many objects the re-render walked.</summary>
    public int ReplaceGrid(Map16Grid next) => Replace(next, layer2: false);

    /// <summary>The same for layer 2. Separate because a layer-2 edit changes what shows THROUGH
    /// layer 1's transparent tiles, so the cells to repaint are chosen by diffing layer 2 while
    /// the recompose still draws both.</summary>
    public int ReplaceLayer2(Map16Grid next) => Replace(next, layer2: true);

    private int Replace(Map16Grid next, bool layer2)
    {
        var old = layer2 ? Layer2 : Grid;
        if (layer2) Layer2 = next; else Grid = next;
        int changed = 0;
        int rows = Math.Min(VisibleRows, next.Height);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < next.Width; x++)
                if (old is null || x >= old.Width || y >= old.Height || old.Get(x, y) != next.Get(x, y))
                { RecomposeCell(x, y); changed++; }
        if (changed > 0) RedrawOverlay();
        return changed;
    }

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
