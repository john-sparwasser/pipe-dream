namespace PipeDream.Services;

/// <summary>
/// One entry in a drawer catalog: what it is, what it looks like in THIS level, and whether it
/// can actually be drawn here.
///
/// The thumbnail is raw RGBA, not a UI bitmap — the layer that draws decides what to wrap it
/// in, and a service that returned an Avalonia image would be a service that cannot be used
/// without a window.
/// </summary>
public sealed class CatalogItem
{
    public required int Number { get; init; }
    public required string Name { get; init; }

    /// <summary>Square RGBA thumbnail, <see cref="Size"/> px on a side, or null when the thing
    /// could not be rendered.</summary>
    public uint[]? Thumb { get; init; }
    public int Size { get; init; }

    /// <summary>False when the level's GFX slots do not hold what this sprite needs — LM's
    /// "sprites available with the current sprite GFX". Such a sprite still places, it just
    /// draws as garbage, so it is shown greyed rather than hidden.</summary>
    public bool Loaded { get; init; } = true;

    /// <summary>Footprint in cells (objects only) so the canvas can outline where a placement
    /// will land, the way it outlines a tile brush.</summary>
    public int W { get; init; } = 1;
    public int H { get; init; } = 1;

    public string Label => $"{Number:X2}  {Name}";
}

/// <summary>
/// Thumbnail catalogs for the Sprites and Objects drawer tabs, rendered with the CURRENT
/// level's graphics and palette — the same rule as the Map16 picker: a thing looks in the
/// drawer exactly as it will look once placed.
///
/// Both are expensive for the same reason: there is no table of "what a sprite/object looks
/// like". A sprite thumbnail comes from interpreting its GFX routine, an object thumbnail from
/// running the object engine on a one-record stream and diffing against an empty level. So
/// both are built on demand (first view of the tab) and cached by the caller.
/// </summary>
public static class Catalog
{
    /// <summary>Every insertable sprite, drawn with this level's SP GFX. <paramref name="spFiles"/>
    /// reports the four resolved SP slots, which is what the "loaded" filter is judged against
    /// and worth showing in the drawer.</summary>
    public static List<CatalogItem> Sprites(Rom rom, LevelScene scene, int levelNum, out int[] spFiles)
    {
        spFiles = [];
        var items = new List<CatalogItem>();
        if (scene.Palettes[0] is not { } pal) return items;
        try
        {
            spFiles = SpriteRender.ResolveSpFiles(rom, scene.Level.Header, levelNum);
            var sp = SpriteRender.LoadSpTiles(rom, scene.Level.Header, levelNum);
            const int cell = 32;
            foreach (int num in SpriteDisplay.Numbers)
            {
                uint[]? thumb = null;
                if (SpriteDisplay.TryGet(num, out var rel))
                {
                    // Relative OAM is centred on the sprite's origin, so it needs shifting into
                    // the cell — the same +8/+16 the ImGui catalog uses.
                    thumb = new uint[cell * cell];
                    SpriteRender.Draw(thumb, cell, cell,
                                      rel.Select(o => o with { X = o.X + 8, Y = o.Y + 16 }).ToList(), sp, pal);
                }
                items.Add(new CatalogItem
                {
                    Number = num,
                    Name = SpriteDisplay.NameOf(num),
                    Thumb = thumb,
                    Size = cell,
                    Loaded = SpriteDisplay.IsLoaded(num, spFiles),
                });
            }
        }
        catch { /* a level whose sprite GFX will not decode still gets a usable name list */ }
        return items;
    }

    /// <summary>
    /// Every standard object that draws something in this tileset, with a thumbnail composed
    /// from the cells it actually writes.
    ///
    /// The footprint comes from a diff against an EMPTY level rather than from the object's
    /// declared size: irregular objects (slopes, pipes, ledges) write nothing like their
    /// rectangle, and a thumbnail of the rectangle would be mostly sky.
    /// </summary>
    public static List<CatalogItem> Objects(Rom rom, LevelScene scene)
    {
        var items = new List<CatalogItem>();
        var level = scene.Level;
        var cache = scene.TileCaches[0];
        Map16Grid baseG;
        try { baseG = ObjectEngine.RenderEmulatedStream(rom, level.Header, LevelEncoder.Encode(level, []), 0, ObjectEngine.SoloBudget); }
        catch { return items; }

        // On LM ROMs 0x22/0x23/0x27/0x29 dispatch to the Direct Map16 handlers, which read
        // extra tile bytes a bare 3-byte record does not carry (the handler runs away), and
        // 0x26/0x28 are LM no-tile directives. Tiles are placed from the Map16 tab instead.
        bool dm16 = rom.HasDm16Hijack;
        const int size = 0x22;      // default placed size: 3 wide x 3 tall, as LM uses
        const int cell = 48;

        for (int num = 1; num <= 0x3F; num++)
        {
            if (dm16 && num is 0x22 or 0x23 or 0x26 or 0x27 or 0x28 or 0x29) continue;
            var one = new List<LevelObject> { new(false, num, 0, 4, 10, size, -1) };
            Map16Grid g;
            try { g = ObjectEngine.RenderEmulatedStream(rom, level.Header, LevelEncoder.Encode(level, one), 0, ObjectEngine.SoloBudget); }
            catch { continue; }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var cells = new List<(int X, int Y, int T)>();
            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                {
                    int t = g.Get(x, y);
                    if (t == baseG.Get(x, y) || t == Map16Grid.Empty) continue;
                    cells.Add((x, y, t));
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            if (cells.Count == 0) continue;                     // draws nothing here: not placeable

            int srcW = (maxX - minX + 1) * 16, srcH = (maxY - minY + 1) * 16;
            double scale = Math.Min(1.0, (double)cell / Math.Max(srcW, srcH));
            int ox = (cell - Math.Max(1, (int)(srcW * scale))) / 2;
            int oy = (cell - Math.Max(1, (int)(srcH * scale))) / 2;
            var img = new uint[cell * cell];
            foreach (var (cx, cy, t) in cells)
            {
                uint[]? tile = (t & ObjectEngine.Marker) != 0 || t >= cache.Length ? null : cache[t];
                if (tile is null) continue;
                for (int py = 0; py < 16; py++)
                    for (int px = 0; px < 16; px++)
                    {
                        uint c = tile[py * 16 + px];
                        if (c == 0) continue;
                        int dx = ox + (int)(((cx - minX) * 16 + px) * scale);
                        int dy = oy + (int)(((cy - minY) * 16 + py) * scale);
                        if (dx >= 0 && dx < cell && dy >= 0 && dy < cell) img[dy * cell + dx] = c;
                    }
            }
            items.Add(new CatalogItem
            {
                Number = num,
                Name = ObjectNames.Standard(num),
                Thumb = img,
                Size = cell,
                W = maxX - minX + 1,
                H = maxY - minY + 1,
            });
        }
        return items;
    }
}
