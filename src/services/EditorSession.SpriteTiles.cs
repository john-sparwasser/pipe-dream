namespace PipeDream.Services;

/// <summary>One display tile of a sprite, as Debug ▸ Sprite tiles edits it: SpriteDisplay.json's
/// own fields, decoded (the UI layer does not name SpriteRender.Oam).</summary>
public sealed record SpriteTile(int X, int Y, int Tile, int Pal, bool Big, bool XFlip = false, bool YFlip = false);

// EditorSession — Debug ▸ Sprite tiles: reading and rewriting the sprite display table
// (src/data/SpriteDisplay.json), which is what the level overlay and the sprite catalog draw
// vanilla sprites from. The overlay itself lives in EditorSession.Sprites.cs.
public sealed partial class EditorSession
{
    /// <summary>Every sprite in the display table, ascending.</summary>
    public IReadOnlyList<(int Number, string Name)> SpriteTableEntries()
        => SpriteDisplay.All.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Name)).ToList();

    public List<SpriteTile> SpriteTilesOf(int number)
        => SpriteDisplay.All.TryGetValue(number, out var e)
            ? e.Oam.Select(o => new SpriteTile(o.X, o.Y, o.Tile, (o.Attr >> 1) & 7, o.Big,
                                               (o.Attr & 0x40) != 0, (o.Attr & 0x80) != 0)).ToList()
            : [];

    /// <summary>What the tool can show a sprite against: its own files, this level's, or any of
    /// the sixteen sprite GFX sets (SPRITEGFXLIST). Index into this is SpriteTileFiles' choice.</summary>
    public IReadOnlyList<string> SpriteTileSetChoices()
        => ["This sprite's files", "This level's files", .. GfxHeaderChoices().Sprites.Select(s => $"Sprite set {s}")];

    /// <summary>The SP1-SP4 files for a choice from <see cref="SpriteTileSetChoices"/>. The sprite's
    /// own: per slot, the first file its gfx needs, else the level's — so a sprite whose GFX this
    /// level does not carry still opens on the right sheet.</summary>
    public int[] SpriteTileFiles(int number, int choice = 0)
    {
        if (Rom is not { } r || Scene is not { } s) return new int[4];
        if (choice >= 2) return Enumerable.Range(0, 4).Select(slot => (int)r.ReadByte(Gfx.SpriteGfxList + (choice - 2) * 4 + slot)).ToArray();
        var files = SpriteRender.ResolveSpFiles(r, s.Level.Header, LevelNum);
        if (choice == 0 && SpriteDisplay.All.TryGetValue(number, out var e))
            for (int slot = 0; slot < 4; slot++)
                if (e.Req[slot].Length > 0) files[slot] = e.Req[slot][0];
        return files;
    }

    /// <summary>Per SP slot, the GFX files that power the sprite (empty = any) — its JSON "gfx".</summary>
    public int[][] SpriteGfxOf(int number)
        => SpriteDisplay.All.TryGetValue(number, out var e) ? e.Req.Select(f => f.ToArray()).ToArray()
         : [[], [], [], []];

    /// <summary>Four SP files as one 16-wide sheet (tiles 000-1FF) in sprite palette row 8+pal.</summary>
    public (uint[] Px, int W, int H) SpFilesSheet(int[] files, int pal)
    {
        if (Rom is not { } r || Scene?.Palettes[0] is not { } p) return ([], 0, 0);
        return GfxSheets.Tiles(SpriteRender.LoadSpTiles(r, files), p, 8 + pal);
    }

    /// <summary>A sprite's tiles drawn on a clear w×h canvas with the placement cell's top-left at
    /// (ox, oy) — the preview the tool edits on, over the desk pattern.</summary>
    public uint[] DrawSpriteTiles(IEnumerable<SpriteTile> tiles, int[] files, int w, int h, int ox, int oy)
    {
        var img = new uint[w * h];
        if (Rom is not { } r || Scene?.Palettes[0] is not { } p) return img;
        var oam = tiles.Select(t => SpriteDisplay.MakeOam(t.X + ox, t.Y + oy, t.Tile, t.Pal, t.Big, t.XFlip, t.YFlip)).ToList();
        SpriteRender.Draw(img, w, h, oam, SpriteRender.LoadSpTiles(r, files), p);
        return img;
    }

    /// <summary>
    /// Write edited sprites back to src/data/SpriteDisplay.json in the source tree and swap the
    /// table in live, so the level and the catalog redraw with them now. Name and hitbox stay as
    /// they were. Returns the problem, or null. Dev-only: a shipped build has no source tree to
    /// write to.
    /// </summary>
    public string? SaveSpriteTiles(IReadOnlyDictionary<int, (List<SpriteTile> Tiles, int[][] Gfx)> edits)
    {
        if (SpriteDisplaySource() is not { } path) return "src/data/SpriteDisplay.json was not found above the app folder.";
        var table = new Dictionary<int, SpriteDisplay.Entry>(SpriteDisplay.All);
        foreach (var (num, (tiles, gfx)) in edits)
            if (table.TryGetValue(num, out var e))
                table[num] = e with
                {
                    Oam = tiles.Select(t => SpriteDisplay.MakeOam(t.X, t.Y, t.Tile, t.Pal, t.Big, t.XFlip, t.YFlip)).ToArray(),
                    Req = gfx,
                };
        try { File.WriteAllText(path, SpriteDisplay.Serialize(table)); }
        catch (Exception ex) when (FileProblem.IsFile(ex)) { return ex.Message; }
        SpriteDisplay.Replace(table);
        spriteCatalog = null;
        RefreshSprites();
        return null;
    }

    private static string? SpriteDisplaySource()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Path.Combine(d.FullName, "src", "data", "SpriteDisplay.json") is var p && File.Exists(p)) return p;
        return null;
    }
}
