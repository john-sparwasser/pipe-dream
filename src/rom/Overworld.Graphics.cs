namespace PipeDream;

// Overworld — how the map is drawn: each submap's tiles and palette, the animated water and
// sparkles at a game frame, and the pixels of a Map16 tile, a layer 2 word or a whole cell.

public sealed partial class Overworld
{
    private readonly Gfx.FgTiles?[] fg = new Gfx.FgTiles?[Submaps];

    private readonly Palette?[] pal = new Palette?[Submaps];

    private readonly Dictionary<(int Tile, int Submap), uint[]> map16Px = [];

    public Palette PaletteOf(int submap) => pal[submap] ??= Palette.LoadOverworld(Rom, submap);

    private Gfx.FgTiles TilesOf(int submap) => fg[submap] ??= WithAnimatedTiles(
        Gfx.FgTiles.Load(Rom, Tileset + submap, levelAnimation: false, bypass: Rom.OwGfxBypass(submap)));

    /// <summary>Drop a submap's loaded tiles (and everything composed from them), so the next
    /// draw resolves its files again — what a repointed slot changes.</summary>
    public void InvalidateGfx(int submap)
    {
        fg[submap] = null;
        Drop(tile8Px, k => k.Submap == submap);
        Drop(map16Px, k => k.Submap == submap);
        Drop(layer1Art, k => k.Submap == submap);
        Drop(quarters, k => k.Submap == submap);
    }

    private static void Drop<K, V>(Dictionary<K, V> cache, Func<K, bool> gone) where K : notnull
    {
        foreach (var key in cache.Keys.Where(gone).ToList()) cache.Remove(key);
    }

    /// <summary>The game's frame counter the animated tiles are drawn for; starts on Lunar
    /// Magic's frame. <see cref="Animate"/> moves it.</summary>
    public int AnimationCounter { get; private set; } = LunarMagicCounter;

    public const int LunarMagicCounter = 8;

    /// <summary>Show the animated tiles as the game has them at <paramref name="counter"/>:
    /// every loaded tileset takes the frames' pictures, and the layer 1 pictures composed
    /// from them are dropped to be composed again. A tick of the editor's animation is eight
    /// game frames, the step at which slots 2-7 change.</summary>
    public void Animate(int counter)
    {
        AnimationCounter = counter & 0x7F;
        foreach (var tiles in fg) if (tiles is not null) WithAnimatedTiles(tiles);
        // Only the pictures an animated 8x8 reaches are stale: the eleven slots themselves, the
        // layer 1 tiles built from one, and a layer 1 picture whose GHOST is — the map is a few
        // hundred distinct pictures, and a tick used to throw every one of them away.
        Drop(tile8Px, k => AnimatedTile8(k.Word & 0x3FF));
        Drop(map16Px, k => AnimatedMap16(k.Tile));
        Drop(layer1Art, k => AnimatedLayer1(k.Tile));
        Drop(quarters, k => AnimatedLayer1(k.Tile));
    }

    /// <summary>VRAM tiles 0x75-0x7F: the ones <see cref="WithAnimatedTiles"/> rebuilds.</summary>
    public static bool AnimatedTile8(int tile) => tile is >= 0x75 and <= 0x7F;

    /// <summary>A layer 1 tile whose four 8x8s include an animated one.</summary>
    public bool AnimatedMap16(int tile)
    {
        if (tile < 0 || tile >= Map16Count) return false;
        animatedDef ??= [.. defs.Select(d => d.Any(w => AnimatedTile8(w.Tile)))];
        return animatedDef[tile];
    }
    private bool[]? animatedDef;

    /// <summary>A layer 1 tile whose picture moves each tick: its own art, or the ghost of the
    /// tile an event reveals it as (Layer1Art draws both).</summary>
    public bool AnimatedLayer1(int tile) => AnimatedMap16(tile) || AnimatedMap16(RevealedTile(tile));

    /// <summary>Whether the 8x8 map cell's picture changes with the animation counter — the land
    /// word under it, or the layer 1 tile over it. What lets a tick redraw those cells alone.</summary>
    public bool CellAnimated(int cx, int cy, bool submapMap)
        => AnimatedTile8(Layer2[Layer2Index(cx, cy, submapMap)] & 0x3FF)
        || AnimatedLayer1(Layer1At(cx >> 1, cy >> 1, submapMap));

    /// <summary>The frame a cycling slot shows at a counter ($048123): slots 2-7 take counter
    /// bits 3-5, the two waterfall slots bits 4-6.</summary>
    private static int AnimationFrame(int counter, int slot) => slot < 2 ? (counter >> 4) & 7 : (counter >> 3) & 7;

    /// <summary>
    /// The overworld's eleven animated tiles, as Lunar Magic shows them. Every frame the game
    /// builds VRAM tiles 0x75-0x7F out of GFX14 — the file decompressed last, so still in the
    /// buffer — and uploads them ($00A4E3: 0x160 bytes to VRAM word $0750). Three are water,
    /// GFX14 tiles 0x50-0x52 ($048000) SCROLLED in RAM every eight frames ($0480E0): 0x75's rows
    /// 0-3 a pixel left and 4-7 a pixel right, 0x76 a row down, 0x77 a pixel left and a row
    /// down; Lunar Magic shows them unscrolled. Eight cycle through eight frames each, GFX14 tile
    /// 0x40 + 8k + frame ($048006, one table of 64), the frame read off the frame counter at
    /// $048123: bits 3-5 for slots 2-7, bits 4-6 for the two waterfall slots. Lunar Magic draws
    /// the map as the game has it at counter 8-15 — slots 2-7 on their second frame, the
    /// waterfall on its first (read off LM's pixels for the Special World's letters and level
    /// sparkles on 2026-09-06; vanilla places no waterfall tile, so those two follow the game).
    /// The tiles are those of <see cref="AnimationCounter"/>, so a running animation shows the
    /// game's cycle.
    /// </summary>
    private Gfx.FgTiles WithAnimatedTiles(Gfx.FgTiles tiles)
    {
        if (Gfx.Cached(Rom, AnimatedGfxFile) is not { } gfx14) return tiles;
        int bpp = Gfx.FileBpp(Rom, AnimatedGfxFile), tb = Gfx.TileBytes(bpp);
        byte[] Tile(int n) => (n + 1) * tb <= gfx14.Length ? Gfx.DecodeTile(gfx14, n * tb, bpp) : new byte[64];
        int scroll = ((AnimationCounter >> 3) + 7) & 7;            // no scroll at Lunar Magic's counter
        tiles.Set(0x75, Scrolled(Tile(0x50), row => row < 4 ? scroll : -scroll, 0));
        tiles.Set(0x76, Scrolled(Tile(0x51), _ => 0, scroll));
        tiles.Set(0x77, Scrolled(Tile(0x52), _ => scroll, scroll));
        for (int k = 0; k < 8; k++) tiles.Set(0x78 + k, Tile(0x40 + 8 * k + AnimationFrame(AnimationCounter, k)));
        return tiles;
    }

    /// <summary>A tile with each row moved <paramref name="left"/>(row) pixels left and the whole
    /// tile <paramref name="down"/> rows down, wrapping — what the game's ROL/ROR of a bitplane
    /// row and its row rotation do to the picture.</summary>
    private static byte[] Scrolled(byte[] px, Func<int, int> left, int down)
    {
        var img = new byte[64];
        for (int y = 0; y < 8; y++)
        {
            int sy = (y - down) & 7, dx = left(sy);
            for (int x = 0; x < 8; x++) img[y * 8 + x] = px[sy * 8 + ((x + dx) & 7)];
        }
        return img;
    }

    /// <summary>A layer 1 tile as a 16x16 image, transparent where its art is. Cached: layer 1
    /// does not change under the layer 2 editor, and every 8x8 cell asks for a quarter of one.</summary>
    public uint[] Map16Pixels(int tile, int submap)
    {
        if (tile >= Map16Count) return new uint[256];
        if (map16Px.TryGetValue((tile, submap), out var done)) return done;
        return map16Px[(tile, submap)] = Map16.Compose(defs[tile], TilesOf(submap).Fetch, PaletteOf(submap));
    }

    /// <summary>One 8x8 tilemap word drawn in a submap's colours, opaque: layer 2 is the bottom
    /// layer, so its colour 0 shows the backdrop (CGRAM 0).</summary>
    public uint[] TilePixels(int word, int submap)
    {
        // Cached by word: the canvas is seventeen thousand cells drawn from a few hundred
        // distinct words, and it used to build every one of them from the tile again per compose.
        if (tile8Px.TryGetValue((word, submap), out var done)) return done;
        var w = new Map16.Word((ushort)word);
        var src = TilesOf(submap).Fetch(w.Tile);
        var p = PaletteOf(submap);
        var img = new uint[64];
        for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
            {
                int idx = src[(w.FlipY ? 7 - py : py) * 8 + (w.FlipX ? 7 - px : px)];
                img[py * 8 + px] = idx == 0 ? p.Rgba[0] | 0xFF000000 : p.Rgba[w.Palette * 16 + idx];
            }
        return tile8Px[(word, submap)] = img;
    }
    private readonly Dictionary<(int Word, int Submap), uint[]> tile8Px = [];

    /// <summary>An 8x8 cell as it shows on the map: <paramref name="word"/> (the layer 2 word
    /// there — passed in so a stroke in progress draws before it is committed) under the
    /// quarter of the layer 1 tile that covers it.</summary>
    public uint[] Cell8Pixels(int word, int cx, int cy, bool submapMap)
    {
        int x = cx >> 1, y = cy >> 1;
        var img = TilePixels(word, SubmapAt(x, y, submapMap));
        var over = Map16Pixels(Layer1At(x, y, submapMap), SubmapAt(x, y, submapMap));
        int ox = (cx & 1) * 8, oy = (cy & 1) * 8;
        for (int py = 0; py < 8; py++)
            for (int px = 0; px < 8; px++)
                if (over[(oy + py) * 16 + ox + px] is var o && o != 0) img[py * 8 + px] = o;
        return img;
    }

    /// <summary>
    /// A layer 1 tile as the editor shows it: its own art, and under a hidden tile the tile an
    /// event will reveal it as, at half opacity — Lunar Magic's "Future Layer 1 Tiles", so a
    /// hidden level reads as the level it is. The ghost's alpha shows the land through it where
    /// the tile has no art of its own; where it has, the two are mixed and stay opaque. Pixels
    /// are premultiplied, as the bitmaps they land in are (LevelBitmap): a half-alpha pixel
    /// carries half its colour.
    /// </summary>
    public uint[] Layer1Art(int tile, int submap)
    {
        if (layer1Art.TryGetValue((tile, submap), out var done)) return done;
        var art = Map16Pixels(tile, submap);
        int future = RevealedTile(tile);
        if (future < 0 || future >= Map16Count) return layer1Art[(tile, submap)] = art;
        var ghost = Map16Pixels(future, submap);
        var img = (uint[])art.Clone();
        for (int i = 0; i < img.Length; i++)
        {
            if (ghost[i] == 0) continue;
            img[i] = img[i] == 0 ? ((ghost[i] >> 1) & 0x007F7F7F) | 0x80000000 : Mix(img[i], ghost[i]);
        }
        return layer1Art[(tile, submap)] = img;
    }
    private readonly Dictionary<(int Tile, int Submap), uint[]> layer1Art = [];

    /// <summary>Half of each, opaque: 0xAABBGGRR channel by channel.</summary>
    private static uint Mix(uint a, uint b)
        => 0xFF000000 | ((((a & 0xFF) + (b & 0xFF)) >> 1) & 0xFF)
         | (((((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) >> 1) << 8)
         | (((((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) >> 1) << 16);

    /// <summary>The quarter of the layer 1 tile over an 8x8 cell, transparent where it has no
    /// art — the layer drawn OVER the land, kept apart so a layer 2 edit never carries it.</summary>
    public uint[] Layer1QuarterPixels(int cx, int cy, bool submapMap)
        => OverlayQuarter(cx, cy, submapMap, layer1: true, paths: false)!;

    /// <summary>
    /// What the overlay shows over an 8x8 map cell: the quarter of the layer 1 tile's picture,
    /// the quarter of Lunar Magic's path picture for it, or the second over the first — whichever
    /// the view has on. Null when neither has anything there. Cached per (tile, quarter, submap,
    /// what is on), because the same few pictures cover the whole map.
    /// </summary>
    public uint[]? OverlayQuarter(int cx, int cy, bool submapMap, bool layer1, bool paths)
    {
        int x = cx >> 1, y = cy >> 1;
        return OverlayQuarterOf(Layer1At(x, y, submapMap), SubmapAt(x, y, submapMap), (cx & 1) | (cy & 1) << 1, layer1, paths);
    }

    /// <summary>The same, for a tile named rather than read off the map — a block in flight,
    /// drawn where it is going.</summary>
    public uint[]? OverlayQuarterOf(int tile, int submap, int q, bool layer1, bool paths)
    {
        var key = (tile, submap, q, layer1, paths);
        if (quarters.TryGetValue(key, out var done)) return done;

        uint[]? img = null;
        if (layer1) img = Quarter(Layer1Art(tile, submap), q);
        if (paths && PathGlyph(tile) is { } g)
        {
            var glyph = Quarter(g, q);
            if (img is null) img = glyph;
            else for (int i = 0; i < 64; i++) if (glyph[i] != 0) img[i] = glyph[i];
        }
        return quarters[key] = img;
    }
    private readonly Dictionary<(int Tile, int Submap, int Q, bool Layer1, bool Paths), uint[]?> quarters = [];

    /// <summary>The 8x8 quarter <paramref name="q"/> (bit 0 right, bit 1 lower) of a 16x16 picture.</summary>
    private static uint[] Quarter(uint[] tile16, int q)
    {
        var img = new uint[64];
        int ox = (q & 1) * 8, oy = (q >> 1) * 8;
        for (int py = 0; py < 8; py++) Array.Copy(tile16, (oy + py) * 16 + ox, img, py * 8, 8);
        return img;
    }

    /// <summary>A 16x16 cell as it shows on the map: its four 8x8s.</summary>
    public uint[] CellPixels(int x, int y, bool submapMap)
    {
        var img = new uint[256];
        for (int q = 0; q < 4; q++)
        {
            int cx = x * 2 + (q & 1), cy = y * 2 + (q >> 1);
            var quad = Cell8Pixels(Layer2[Layer2Index(cx, cy, submapMap)], cx, cy, submapMap);
            for (int py = 0; py < 8; py++) Array.Copy(quad, py * 8, img, ((q >> 1) * 8 + py) * 16 + (q & 1) * 8, 8);
        }
        return img;
    }

    /// <summary>GFX14, the file the overworld decompresses last so its animated tiles can be
    /// built out of what is left in the buffer ($00A147) — the AN2 slot of a submap's record.</summary>
    public const int AnimatedGfxFile = 0x14;

    /// <summary>
    /// The GFX record the ROM's own lists imply for a submap: what every submap loads in vanilla,
    /// and what Lunar Magic writes into all seven entries when it installs the per-submap hack.
    /// A level's record layout exactly (LunarMagic.LmGfxBypass): the overworld's FG1-FG6 are the
    /// level's FG1/FG2/BG1/FG3/BG2/BG3 words 7-2 — the same six pages Gfx.FgTiles loads — and its
    /// SP1-SP4 the level's, words 11-8. The slots with no vanilla file (AN1, FG5, FG6) are 0x7F.
    /// </summary>
    public static ushort[] VanillaGfxRecord(Rom rom)
    {
        int fgList = rom.FileOffset(Gfx.ObjectGfxList) + Tileset * 4;
        int spList = rom.FileOffset(Gfx.SpriteGfxList) + SpriteSet * 4;
        var w = new ushort[16];
        Array.Fill(w, (ushort)0x7F);
        for (int i = 0; i < 4; i++) w[7 - i] = rom.Data[fgList + i];      // FG1-FG4
        for (int i = 0; i < 4; i++) w[11 - i] = rom.Data[spList + i];     // SP1-SP4
        w[0] = AnimatedGfxFile;                                           // AN2
        for (int i = 0; i < 4; i++) w[15 - i] = (ushort)Layer3.VanillaGfx[i];
        return w;
    }

    /// <summary>The overworld slots the Graphics drawer shows, in dialog order — Lunar Magic's
    /// Submap GFX has FG1-FG6, SP1-SP4 and AN2. Word = the record word each one is, and
    /// 0x70 + word is the drawer's key for it (clear of a level's own words 0-15).</summary>
    public static readonly (string Name, int Word, int PalRow)[] GfxSlotOrder =
        [.. Enumerable.Range(0, 6).Select(i => ($"FG{i + 1}", 7 - i, 4)),
         .. Enumerable.Range(0, 4).Select(i => ($"SP{i + 1}", 11 - i, 8)),
         ("AN2", 0, 4)];

    /// <summary>
    /// One submap's graphics files, as bins for the Graphics drawer. Def is the file the vanilla
    /// lists give every submap; File is what this submap actually loads, which differs once its
    /// record repoints a slot — so the drawer badges it "bypass" exactly as a level's bins do.
    /// </summary>
    public static (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] GfxSlots(Rom rom, int submap)
    {
        var def = VanillaGfxRecord(rom);
        var rec = rom.OwGfxBypass(submap) ?? def;
        return [.. GfxSlotOrder.Select(s => (s.Name, s.PalRow, 0x70 + s.Word, def[s.Word] & 0xFFF, rec[s.Word] & 0xFFF, 0, 0))];
    }
}
