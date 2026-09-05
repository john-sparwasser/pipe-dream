namespace PipeDream.Services;

// EditorSession — graphics files: the GFX pixel editor, the level's VRAM bins and their
// bypass overrides, and importing / saving / naming custom ExGFX. ROM-wide, so unlike the
// level state it survives a level switch. The rest of the class: EditorSession.cs and the other
// EditorSession.*.cs files.
public sealed partial class EditorSession
{
    // ---- graphics ----

    /// <summary>Pixel editing for one GFX file. ROM-wide rather than per level, so it survives
    /// level switches — unlike the object, sprite and palette state above it.</summary>
    public GfxEdit? GfxPixels { get; private set; }

    /// <summary>The level's VRAM GFX bins in drawer order: the ten FG/BG/SP ones, then LG1-LG4
    /// (the layer-3 window), then the animation slots.</summary>
    public (string Name, int PalRow, int BypWord, int Def, int File, int ColorOffset, int Bpp)[] GfxBins
        => Rom is { } r && Scene is { } s ? Gfx.LevelSlots(r, s.Level.Header, LevelNum) : [];

    /// <summary>The 16 tileset / sprite-set choices for the graphics-header dialog: the setting
    /// number plus the GFX files it loads, straight from the ROM's own lists — the lists have
    /// no prose names, and the files are what actually distinguishes the settings.</summary>
    public (IReadOnlyList<string> Layer1, IReadOnlyList<string> Sprites) GfxHeaderChoices()
    {
        if (Rom is not { } rom) return ([], []);
        List<string> Items(int listBase) => [.. Enumerable.Range(0, 16).Select(i =>
            $"{i:X} — GFX " + string.Join(" ", Enumerable.Range(0, 4)
                .Select(s => $"{rom.Data[rom.FileOffset(listBase) + i * 4 + s]:X2}")))];
        return (Items(Gfx.ObjectGfxList), Items(Gfx.SpriteGfxList));
    }

    /// <summary>How a bin's current file got there, for the drawer's badge. A base file — fork or
    /// not — says nothing: it is the normal case, and a badge on all ten bins is not a badge.</summary>
    public string GfxBinNote(int bypWord, int file, int def)
        => Rom is not { } r ? ""
         : Gfx.SourceSnes(r, file) < 0 && r.ImportedGfx.ContainsKey(file) ? "custom"
         : r.GfxSlotOverrides.ContainsKey((LevelNum, bypWord)) ? "override"
         : file != def ? "bypass" : "";

    public string? GfxName(int file)
        => Rom?.GfxName(file) is { Length: > 0 } n ? n : null;

    /// <summary>One GFX file decoded as a tile sheet, for a preview. Empty when the id resolves
    /// nowhere or will not decode — a bin pointing at nothing is normal (0x7F means "unused").</summary>
    public (uint[] Px, int W, int H) GfxFileSheet(int file, int palRow, int colorOffset = 0, int bpp = 0)
    {
        if (Rom is null || file == 0x7F || Scene?.Palettes[0] is not { } pal) return ([], 0, 0);
        if (Gfx.Cached(Rom, file) is not { } data) return ([], 0, 0);
        try { return Gfx.TileSheet(data, bpp > 0 ? bpp : Gfx.FileBpp(Rom, file), pal, palRow, colorOffset: colorOffset); }
        catch { return ([], 0, 0); }
    }

    /// <summary>
    /// Point one VRAM bin at a different GFX file. This is a SESSION override recorded in the
    /// project (CONTRACT §7d's Super GFX Bypass), so it re-resolves everything that reads the
    /// bin: the level's tiles, the sprite graphics and the Map16 sheet alike.
    /// </summary>
    public string SetGfxSlot(int bypWord, int file)
    {
        if (Rom is null) return "no ROM open";
        if (file is < 0 or > 0xFFF) return "GFX ids run 000-FFF";
        Rom.GfxSlotOverrides[(LevelNum, bypWord)] = file;
        Project?.MarkDirty();
        touched.Add(LevelNum);
        RecomposeScene();
        return $"bin ← GFX{file:X3}" + (GfxName(file) is { } n ? $" \"{n}\"" : "");
    }

    /// <summary>
    /// Import a raw planar .bin as a custom ExGFX file: detect its depth from the size, normalise
    /// to the ROM's depth, and store it under the next FREE id ≥ 0x100. Returns that id, or -1 with
    /// the reason in the status.
    ///
    /// The id must be fresh — skipping both prior imports and ids the ROM itself resolves — or the
    /// import would shadow a real ExGFX file other levels use. Pointing a bin at the result is a
    /// separate step (<see cref="SetGfxSlot"/>): importing and assigning are different decisions.
    /// </summary>
    public (int Id, string Status) ImportGfx(string path)
    {
        if (Rom is null) return (-1, "no ROM open");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (FileProblem.IsFile(e)) { Fail(FileProblem.From(e, "import the graphics file", path)); return (-1, Status); }
        catch (Exception e) { return (-1, $"import failed: {e.Message}"); }

        int bpp = Gfx.DetectBpp(bytes);
        if (bpp == 0)
            return (-1, $"import rejected: {Path.GetFileName(path)} is 0x{bytes.Length:X} bytes — "
                      + "not whole 3bpp (x24) or 4bpp (x32) planar tiles");
        int romBpp = Gfx.RomBpp(Rom);
        bytes = Gfx.NormalizeBpp(bytes, bpp, romBpp, out bool plane3Dropped);

        // A file named by the ExGFX### convention carries its own id — honour it when it is a
        // usable custom id (0x100+) that nothing here resolves yet. Anything else auto-assigns.
        string stem = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(stem, "^ExGFX([0-9A-Fa-f]{3})$",
                                                           System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int id = m.Success
              && int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) is >= 0x100 and <= 0xFFF and var wanted
              && !Rom.ImportedGfx.ContainsKey(wanted) && Gfx.SourceSnes(Rom, wanted) < 0
            ? wanted : 0x100;
        while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
        if (id > 0xFFF) return (-1, "import failed: no free ExGFX id (0x100-0xFFF all in use)");

        Rom.ImportedGfx[id] = bytes;
        // The filename is the only human-meaningful label an import has; keeping it beats
        // leaving the user with a bare hex id.
        Rom.ImportedGfxNames[id] = stem;
        Gfx.InvalidateCache(Rom);
        Project?.MarkDirty();

        return (id, $"imported {Path.GetFileName(path)} as GFX{id:X3} ({bpp}bpp → {romBpp}bpp)"
                  + (plane3Dropped ? " — nonzero plane 3 data discarded" : ""));
    }

    /// <summary>The sheet for the file currently open in the pixel editor.</summary>
    public (uint[] Px, int W, int H) GfxSheet()
        => GfxPixels is { } g && Scene?.Palettes[0] is { } pal ? g.Sheet(pal) : ([], 0, 0);

    /// <summary>
    /// Files a picker should offer — the project's custom ExGFX or the ROM's base files, filtered.
    /// <paramref name="filter"/> matches names anywhere and hex ids by prefix, so "grass" finds it
    /// by name and "10" finds $100-$10F.
    /// </summary>
    public List<GfxFileInfo> GfxFiles(bool custom, string filter)
    {
        if (Rom is null) return [];
        return Gfx.Candidates(Rom, custom, filter).Select(id => new GfxFileInfo
        {
            Id = id,
            Custom = Gfx.SourceSnes(Rom, id) < 0,
            Name = GfxName(id),
            Description = Gfx.Describe(Rom, id),
            // Palette row 2 (the FG row) is the least misleading single choice for a preview; the
            // real row depends on which bin the file ends up in.
            Sheet = GfxFileSheet(id, 2),
        }).ToList();
    }

    /// <summary>Whether the open GFX file is one of the ROM's own — including a copy-on-write fork
    /// of one, which has no ExGFX id of its own yet. False means a custom ExGFX file. This is what
    /// makes a save ask for a name, and what the mode's badge shows.</summary>
    public bool GfxIsStock
        => Rom is { } r && GfxPixels is { } g && Gfx.SourceSnes(r, g.File) >= 0;

    /// <summary>Committed GFX pixel edits that are not in project.pdp yet.</summary>
    public bool GfxDirty => GfxPixels?.Dirty == true;

    /// <summary>The name a new custom file derived from <paramref name="from"/> gets when the
    /// user offers none: the source's own label plus "copy". Custom files go by name in the UI,
    /// so leaving one nameless would strand it behind a bare hex id.</summary>
    public string DefaultGfxName(int from)
        => (GfxName(from) is { Length: > 0 } n ? n : $"GFX{from:X3}") + " copy";

    /// <summary>
    /// Save the open GFX file into the project as a custom ExGFX.
    ///
    /// An already-custom file is just written under its own id. A STOCK file MOVES to the next
    /// free id ≥ 0x100 under <paramref name="name"/>: the stock file is restored for everyone
    /// else, and this level's bins that pointed at it are repointed to the new file — the same
    /// shape <see cref="ImportGfx"/> gives an imported .bin, so the edit travels with the level
    /// instead of shadowing stock graphics ROM-wide.
    /// </summary>
    public string SaveGfx(string name)
    {
        if (Rom is null || GfxPixels is not { } g) return "no GFX open";
        if (Project is null) return "no project open — File ▸ New Project first";
        g.EndStroke();
        if (Gfx.EditableBytes(Rom, g.File, out _) is not { } bytes) return $"GFX{g.File:X3} is empty";

        if (Gfx.SourceSnes(Rom, g.File) >= 0)
        {
            int id = 0x100;
            while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
            if (id > 0xFFF) return "save failed: no free ExGFX id (0x100-0xFFF all in use)";
            int from = g.File;
            Rom.ImportedGfx[id] = bytes;
            Rom.ImportedGfx.Remove(from);        // the stock file comes back for every other user
            Rom.ImportedGfxNames[id] = name.Trim().Length > 0 ? name.Trim() : DefaultGfxName(from);
            Gfx.InvalidateCache(Rom);
            g.Retarget(from, id);
            foreach (var bin in GfxBins)
                if (bin.File == from) SetGfxSlot(bin.BypWord, id);
        }
        else if (name.Trim().Length > 0) Rom.ImportedGfxNames[g.File] = name.Trim();

        // An ExAnimation source file lives in the ROM uncompressed: push the edited bytes into
        // its block too, so the animation overlay draws what was just painted.
        if (g.File is >= 0x60 and <= 0x63 && Rom.LmExAnimBase >= 0)
        {
            Rom.SetLmAltExGfx(g.File - 0x60, bytes);
            Gfx.InvalidateCache(Rom);
            Scene?.InvalidateGfx();
            RecomposeScene();
        }

        Save();
        return GfxName(g.File) is { Length: > 0 } n ? $"saved \"{n}\"" : $"saved GFX{g.File:X3}";
    }

    /// <summary>
    /// Save a COPY of the open GFX file as a new custom ExGFX under <paramref name="name"/>.
    ///
    /// The source keeps its bytes: a custom source stays as it is, and a stock source drops its
    /// copy-on-write fork so the stock file is restored for everyone else. The editor and this
    /// level's bins that pointed at the source move to the copy.
    /// </summary>
    public string SaveGfxAs(string name)
    {
        if (Rom is null || GfxPixels is not { } g) return "no GFX open";
        if (Project is null) return "no project open — File ▸ New Project first";
        g.EndStroke();
        if (Gfx.EditableBytes(Rom, g.File, out _) is not { } bytes) return $"GFX{g.File:X3} is empty";

        int id = 0x100;
        while (id <= 0xFFF && (Rom.ImportedGfx.ContainsKey(id) || Gfx.SourceSnes(Rom, id) >= 0)) id++;
        if (id > 0xFFF) return "save failed: no free ExGFX id (0x100-0xFFF all in use)";
        int from = g.File;
        Rom.ImportedGfx[id] = (byte[])bytes.Clone();   // its own array: edits must not alias the source
        if (Gfx.SourceSnes(Rom, from) >= 0)
            Rom.ImportedGfx.Remove(from);              // the stock file comes back for every other user
        Rom.ImportedGfxNames[id] = name.Trim().Length > 0 ? name.Trim() : DefaultGfxName(from);
        Gfx.InvalidateCache(Rom);
        g.Retarget(from, id);
        foreach (var bin in GfxBins)
            if (bin.File == from) SetGfxSlot(bin.BypWord, id);

        Save();
        return GfxName(id) is { Length: > 0 } n ? $"saved \"{n}\"" : $"saved GFX{id:X3}";
    }

    /// <summary>Rename an imported file. Stock files have no name to change — vanilla ships no
    /// label table, and inventing one would be guesswork.</summary>
    public bool RenameGfx(int id, string name)
    {
        if (Rom is null || !Rom.ImportedGfx.ContainsKey(id)) return false;
        Rom.ImportedGfxNames[id] = name.Trim();
        Project?.MarkDirty();
        return true;
    }

    /// <summary>One GFX pixel editor per ROM — the bytes are ROM-wide, so unlike Map16 defs it
    /// outlives a level switch. A committed stroke changes what every level draws with, hence the
    /// full recompose.</summary>
    private void NewGfxEdit()
    {
        if (Rom is null) return;
        GfxPixels = new GfxEdit(Rom);
        GfxPixels.Committed += (_, _) =>
        {
            Project?.MarkDirty();
            RecomposeScene();
        };
    }
}
