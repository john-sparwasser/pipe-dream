namespace PipeDream.Services;

// EditorSession — ExAnimation (reference/EXANIMATION.md): the per-level and global slot
// lists, their source files, and the pixels the timeline editor shows. The rest of the class:
// EditorSession.cs and the other EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- ExAnimation (reference/EXANIMATION.md) ----

    /// <summary>The current level's slots, or the global list's, as the ROM has them.</summary>
    public IReadOnlyList<ExAnimation.Slot> ExAnimSlots(bool global)
        => Rom is null ? [] : global ? ExAnimation.ReadGlobal(Rom) : ExAnimation.ReadLevel(Rom, LevelNum);

    /// <summary>
    /// One frame of a tile slot as pixels, in the slot's shape (a line of N tiles, or the
    /// stacked / 16x16 / 32x16 block), coloured with the level's palette row. The source is
    /// whatever the frame word names: a byte offset into the list's alternate file, or a $7E
    /// address in AN1 ($7D00, GFX33), AN2 ($AD00, the level's bypass file) or Mario's sheet
    /// ($2000, GFX32). Empty when the source is not loaded (no AN2 file, no alt file yet).
    /// </summary>
    public (uint[] Px, int W, int H) ExAnimFramePixels(ExAnimation.Slot s, int frame, int palRow)
    {
        if (Rom is null || Scene?.Palettes[0] is not { } pal || s.IsPalette || s.TileCount == 0 || frame >= s.Frames.Length)
            return ([], 0, 0);
        int word = s.Frames[frame];
        byte[]? src; int off, bpp;
        if (s.AltFile)
        {
            src = Gfx.Cached(Rom, 0x60 + s.AltFileIndex); off = word; bpp = s.Type == ExAnimation.Type2bpp ? 2 : 4;
        }
        else if (word >= 0xAD00)
        {
            int an2 = GfxBins.FirstOrDefault(b => b.Name == "AN2").File;
            src = an2 is 0 or 0x7F ? null : Gfx.Cached(Rom, an2); bpp = an2 is 0 or 0x7F ? 4 : Gfx.FileBpp(Rom, an2);
            off = (word - 0xAD00) / 0x20 * Gfx.TileBytes(bpp);
        }
        else if (word >= 0x7D00)
        {
            src = Gfx.Cached(Rom, 0x33); bpp = Gfx.FileBpp(Rom, 0x33); off = (word - 0x7D00) / 0x20 * Gfx.TileBytes(bpp);
        }
        else
        {
            src = Gfx.Cached(Rom, 0x32); bpp = 4; off = (word - 0x2000) / 0x20 * 0x20;
        }
        if (src is null) return ([], 0, 0);

        int cols = s.Type switch { ExAnimation.TypeStacked => 1, ExAnimation.Type16x16 => 2, ExAnimation.Type32x16 => 4, _ => s.TileCount };
        int rows = (s.TileCount + cols - 1) / cols;
        int w = cols * 8, h = rows * 8, tb = Gfx.TileBytes(bpp), baseColor = (palRow & 0x0F) * 16;
        var px = new uint[w * h];
        for (int k = 0; k < s.TileCount; k++)
        {
            if (off + (k + 1) * tb > src.Length) break;
            var tile = Gfx.DecodeTile(src, off + k * tb, bpp);
            int ox = (k % cols) * 8, oy = (k / cols) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = tile[y * 8 + x];
                    px[(oy + y) * w + ox + x] = idx == 0 ? 0xFF303030u : pal.Rgba[baseColor + idx];
                }
        }
        return (px, w, h);
    }

    /// <summary>Add a working slot with nothing decided yet — the next free slot number, one 8x8,
    /// no trigger, one frame of AN1 tile 600, destination tile 000 — so the decisions can be made
    /// on the timeline afterwards. Null when the list is full or the base cannot hold it.</summary>
    public ExAnimation.Slot? AddExAnimSlot(bool global)
    {
        var have = ExAnimSlots(global);
        int index = Enumerable.Range(0, 0x20).FirstOrDefault(i => have.All(s => s.Index != i), -1);
        if (index < 0) { Report("all 32 slots of this list are used"); return null; }
        var slot = new ExAnimation.Slot(index, 1, ExAnimation.TriggerNone, 1, 0x0000, [0x7D00], ExAnimAltFile(global));
        return SetExAnimSlot(global, slot) ? slot : null;
    }

    /// <summary>Move a slot to a free slot number, keeping everything else about it.</summary>
    public bool ReassignExAnimSlot(bool global, int from, int to)
    {
        var list = ExAnimSlots(global).ToList();
        int i = list.FindIndex(s => s.Index == from);
        if (i < 0 || from == to || to is < 0 or >= 0x20) return false;
        if (list.Any(s => s.Index == to)) { Report($"slot {to:X2} is already used"); return false; }
        list[i] = list[i] with { Index = to };
        return SetExAnim(global, list, ExAnimAltFile(global));
    }

    /// <summary>Replace (or add) one slot in a list, keeping the list's source file.</summary>
    public bool SetExAnimSlot(bool global, ExAnimation.Slot slot)
    {
        var list = ExAnimSlots(global).Where(x => x.Index != slot.Index).ToList();
        list.Add(slot);
        return SetExAnim(global, list, ExAnimAltFile(global));
    }

    /// <summary>What currently sits at a tile slot's destination in the level's VRAM, in the slot's
    /// shape — the thing the animation will overwrite. Empty for palette slots.</summary>
    public (uint[] Px, int W, int H) ExAnimDestPixels(ExAnimation.Slot s, int palRow)
    {
        if (Rom is null || Scene?.Palettes[0] is not { } pal || s.IsPalette || s.TileCount == 0) return ([], 0, 0);
        var fg = Scene.Fg(Rom, LevelNum, 0);
        int cols = s.Type switch { ExAnimation.TypeStacked => 1, ExAnimation.Type16x16 => 2, ExAnimation.Type32x16 => 4, _ => s.TileCount };
        int rows = (s.TileCount + cols - 1) / cols, w = cols * 8, h = rows * 8, baseColor = (palRow & 0x0F) * 16;
        var px = new uint[w * h];
        byte[][]? sp = null;        // loaded on first sprite-range dest tile
        byte[]?[]? l3 = null;       // and likewise the layer-3 window
        for (int k = 0; k < s.TileCount; k++)
        {
            int tile = s.DestTileAt(k);
            byte[]? t;
            if (tile is >= 0x400 and < 0x600)
                t = (sp ??= SpriteRender.LoadSpTiles(Rom, Scene.Level.Header, LevelNum))[tile - 0x400];
            else if (tile is >= 0x1C00 and < 0x1C00 + Layer3.TileCount)
                t = (l3 ??= Layer3.Tiles(Rom, LevelNum))[tile - 0x1C00];
            else if (tile is < 0 or >= 0x400) continue;
            else t = fg.Fetch(tile);
            if (t is null) continue;
            int ox = (k % cols) * 8, oy = (k / cols) * 8;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int idx = t[y * 8 + x];
                    px[(oy + y) * w + ox + x] = idx == 0 ? 0xFF303030u : pal.Rgba[baseColor + idx];
                }
        }
        return (px, w, h);
    }

    /// <summary>Which of files 60-63 a list reads (its record header), 0 when it has no record.</summary>
    public int ExAnimAltFile(bool global)
    {
        if (Rom is null) return 0;
        int ptr = global ? Rom.LmGlobalExAnimPtr : Rom.LmExAnimBase < 0 ? -1 : Rom.ReadValue(Rom.LmExAnimBase + LevelNum * 3, 3);
        return ptr > 0xFFFF ? Rom.ReadByte(ptr + 1) & 3 : 0;
    }

    /// <summary>Replace the level's (or the global) slot list: written to the session ROM so the
    /// canvas animates it, recorded in the project as the encoded record, and the graphics
    /// recomposed. False with a report when the base cannot hold it.</summary>
    public bool SetExAnim(bool global, IReadOnlyList<ExAnimation.Slot> slots, int altFileIndex)
    {
        if (Rom is null) return false;
        string? err = global ? Rom.WriteGlobalExAnim(slots, altFileIndex) : Rom.WriteLevelExAnim(LevelNum, slots, altFileIndex);
        if (err is not null) { Report(err); return false; }
        if (Project is not null)
        {
            string? hex = slots.Count == 0 ? null : Convert.ToHexString(ExAnimation.Encode(slots, altFileIndex));
            if (global) Project.Data.ExAnimation.Global = hex;
            else if (hex is null) Project.Data.ExAnimation.Levels.Remove(LevelNum.ToString("X3"));
            else Project.Data.ExAnimation.Levels[LevelNum.ToString("X3")] = hex;
            Project.MarkDirty();
        }
        Scene?.InvalidateGfx();
        Rebuild("exanimation");
        return true;
    }

    /// <summary>Install raw 4bpp tile data as ExAnimation source file 60+<paramref name="index"/>
    /// (≤ 32KB): into the session ROM for the overlay, and into the project under its id.</summary>
    public bool SetExAnimSource(int index, byte[] data)
    {
        if (Rom is null || Rom.LmExAnimBase < 0) { Report("this base has no ExAnimation engine — File → Upgrade base"); return false; }
        if (data.Length is 0 or > 0x8000) { Report("an ExAnimation source file is 1..32768 bytes"); return false; }
        Rom.SetLmAltExGfx(index, data);
        Rom.ImportedGfx[0x60 + index] = data;
        Project?.MarkDirty();
        Scene?.InvalidateGfx();
        Rebuild("exanimation source");
        return true;
    }

    /// <summary>The same from a file on disk (a raw 4bpp .bin, as LM's ExGraphics/ExGFX6x.bin).</summary>
    public bool ImportExAnimSource(int index, string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (FileProblem.IsFile(e)) { Fail(FileProblem.From(e, "import the animation graphics", path)); return false; }
        return SetExAnimSource(index, data);
    }

    // ---- destination picker ----
    /// <summary>The level's composed sprite 8x8s (SP1-SP4, bypass honored) as one sheet,
    /// for the destination picker's sprite range (LM dest tiles 400-5FF).</summary>
    public (uint[] Px, int W, int H) SpriteSheet(int palRow)
    {
        if (Rom is not { } r || Scene is not { } s || s.Palettes[0] is not { } pal) return ([], 0, 0);
        return GfxSheets.Tiles(SpriteRender.LoadSpTiles(r, s.Level.Header, LevelNum), pal, palRow);
    }
}
