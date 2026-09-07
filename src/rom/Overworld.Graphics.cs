namespace PipeDream;

// Overworld — how the map is drawn: each submap's tiles and palette, the animated water and
// sparkles at a game frame, and the pixels of a Map16 tile, a layer 2 word or a whole cell.

public sealed partial class Overworld
{
    private readonly Gfx.FgTiles?[] fg = new Gfx.FgTiles?[Submaps];

    private readonly Palette?[] pal = new Palette?[Submaps];

    private readonly Dictionary<(int Tile, int Submap), uint[]> map16Px = [];

    public Palette PaletteOf(int submap) => pal[submap] ??= Palette.LoadOverworld(Rom, submap);

    private Gfx.FgTiles TilesOf(int submap) => fg[submap] ??= WithAnimatedTiles(Gfx.FgTiles.Load(Rom, Tileset + submap, levelAnimation: false));

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
        map16Px.Clear();    // ponytail: drops every composed layer 1 tile per tick; keep only the ones that use slots 0x75-0x7F if the map ever stutters
    }

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
        if (Gfx.Cached(Rom, 0x14) is not { } gfx14) return tiles;
        int bpp = Gfx.FileBpp(Rom, 0x14), tb = Gfx.TileBytes(bpp);
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
        return img;
    }

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

    /// <summary>The quarter of the layer 1 tile over an 8x8 cell, transparent where it has no
    /// art — the layer drawn OVER the land, kept apart so a layer 2 edit never carries it.</summary>
    public uint[] Layer1QuarterPixels(int cx, int cy, bool submapMap)
    {
        int x = cx >> 1, y = cy >> 1;
        var over = Map16Pixels(Layer1At(x, y, submapMap), SubmapAt(x, y, submapMap));
        var img = new uint[64];
        int ox = (cx & 1) * 8, oy = (cy & 1) * 8;
        for (int py = 0; py < 8; py++) Array.Copy(over, (oy + py) * 16 + ox, img, py * 8, 8);
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

    /// <summary>The graphics files the overworld loads, as bins for the Graphics drawer: the
    /// four FG files under the main map's tileset row and the four sprite files under its sprite
    /// set. Bypass words 0x70+ keep them clear of a level's real record words.</summary>
    public static (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] GfxSlots(Rom rom)
    {
        int fgList = rom.FileOffset(Gfx.ObjectGfxList) + Tileset * 4;
        int spList = rom.FileOffset(Gfx.SpriteGfxList) + SpriteSet * 4;
        return [.. Enumerable.Range(0, 4).Select(i => ($"FG{i + 1}", 4, 0x70 + i, (int)rom.Data[fgList + i], (int)rom.Data[fgList + i], 0, 0)),
                .. Enumerable.Range(0, 4).Select(i => ($"SP{i + 1}", 8, 0x74 + i, (int)rom.Data[spList + i], (int)rom.Data[spList + i], 0, 0))];
    }
}
