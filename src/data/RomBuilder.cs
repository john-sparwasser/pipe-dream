namespace PipeDream;

/// <summary>
/// Applies a project's snapshot to a ROM image. Two consumers, one implementation, so
/// the session you edit and the ROM you build can't drift: ReplayMap16 runs on the live
/// session ROM at project open, and Build runs the full pipeline on a FRESH copy of the
/// base (never the session instance — the session ROM stays "base + replay" forever).
/// Build is deterministic: fixed op order (map16 → levels ascending: objects, sprites,
/// palette) over a first-fit allocator, so the same project yields a byte-identical ROM.
/// </summary>
internal static class RomBuilder
{
    /// <summary>Replay the project's Map16/acts-as snapshot into a ROM image.
    /// Returns a user-facing error, or null on success.</summary>
    internal static string? ReplayMap16(Rom rom, ProjectFile data)
    {
        var m = data.Map16;
        if (m.TileCount > 0x200 && rom.EnsureMap16Tiles(m.TileCount) is { } err)
            return "project needs extended Map16 pages: " + err;
        foreach (var (addr, hex) in m.Slots)
        {
            if (hex.Length != 16) continue;               // unfilled placeholder
            int fo = rom.FileOffset(Convert.ToInt32(addr, 16));
            Convert.FromHexString(hex).CopyTo(rom.Data, fo);
        }
        foreach (var (tileHex, hex) in m.Ext)
        {
            if (hex.Length != 16) continue;
            // Extended defs are tileset-independent; offsets resolve fresh post-allocation.
            int fo = Map16.DefFileOffset(rom, 0, Convert.ToInt32(tileHex, 16));
            if (fo >= 0) Convert.FromHexString(hex).CopyTo(rom.Data, fo);
        }
        if (m.ActsAs.Count > 0)
        {
            if (rom.LmActsAsBase <= 0)
                return "project has acts-as edits but the base ROM lacks LM's acts-like table.";
            foreach (var (tileHex, val) in m.ActsAs)
            {
                int fo = rom.FileOffset(rom.LmActsAsBase + Convert.ToInt32(tileHex, 16) * 2);
                rom.Data[fo] = (byte)val; rom.Data[fo + 1] = (byte)(val >> 8);
            }
        }
        return null;
    }

    /// <summary>Whether a project's level key names a real level.</summary>
    private static bool IsLevelKey(string key, out int level)
    {
        level = -1;
        return int.TryParse(key, System.Globalization.NumberStyles.HexNumber, null, out level)
               && level >= 0 && level < Rom.LevelCount;
    }

    /// <summary>Replay the project's entrance-table edits into a ROM — secondary entrance
    /// records and per-level main entrances. Same code for the session ROM and a fresh build
    /// copy, so the editor can't drift from the built game.</summary>
    /// <summary>
    /// Write the project's ExAnimation into the ROM: source files 60-63 (from <c>Gfx</c>, raw,
    /// uncompressed — the engine DMAs them straight from ROM), then the per-level records and
    /// the global list. Shared by the build and the session hydrate, so the in-app overlay and
    /// the built ROM cannot disagree. Silent on a base without the engine only when there is
    /// nothing to write.
    /// </summary>
    internal static void ReplayExAnimation(Rom rom, ProjectFile data, List<string>? warnings)
    {
        var alt = data.Gfx.Where(kv => Convert.ToInt32(kv.Key, 16) is >= 0x60 and <= 0x63).ToList();
        bool any = alt.Count > 0 || data.ExAnimation.Levels.Count > 0 || data.ExAnimation.Global is not null;
        if (!any) return;
        if (rom.LmExAnimBase < 0)
        {
            warnings?.Add($"ExAnimation skipped (base lacks LM's ExAnimation engine — File → Upgrade base to prep v{RomPrep.Version})");
            return;
        }
        foreach (var (idHex, b64) in alt.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            rom.SetLmAltExGfx(Convert.ToInt32(idHex, 16) - 0x60, Convert.FromBase64String(b64));
        foreach (var (key, hex) in data.ExAnimation.Levels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!IsLevelKey(key, out int level)) { warnings?.Add($"ignored ExAnimation entry '{key}' — not a level number"); continue; }
            var rec = Convert.FromHexString(hex);
            if (rom.WriteLevelExAnim(level, ExAnimation.ParseSlots(rec), rec.Length > 1 ? rec[1] : 0) is { } err) warnings?.Add($"level {key}: {err}");
        }
        if (data.ExAnimation.Global is { } g)
        {
            var rec = Convert.FromHexString(g);
            if (rom.WriteGlobalExAnim(ExAnimation.ParseSlots(rec), rec.Length > 1 ? rec[1] : 0) is { } err) warnings?.Add($"global ExAnimation: {err}");
        }
    }

    internal static void ReplayEntrances(Rom rom, ProjectFile data)
    {
        foreach (var (idxHex, hex) in data.Entrances)
        {
            if (hex.Length is not (8 or 10 or 12)) continue;   // unfilled placeholder
            int index = Convert.ToInt32(idxHex, 16);
            if (index is < 0 or >= Rom.SecondaryEntranceCount) continue;
            rom.WriteSecondaryEntrance(index, new SecondaryEntrance(Convert.FromHexString(hex)));
        }
        foreach (var (levelHex, state) in data.Levels)
        {
            int level = Convert.ToInt32(levelHex, 16);
            if (state.MainEntrance is not { Length: 8 or 12 or 20 or 22 or 24 } hex) continue;
            rom.WriteMainEntrance(level, new MainEntrance(Convert.FromHexString(hex)));
        }
    }

    /// <summary>Build build\&lt;name&gt;.smc from the project. Returns a status line
    /// (including per-level warnings for LM-gated features on a vanilla base).</summary>
    internal static (string status, string? outPath) Build(Project project)
    {
        try
        {
            var rom = Rom.Load(project.BaseRomPath);
            var warnings = new List<string>();
            if (ReplayMap16(rom, project.Data) is { } err) return (err, null);
            ReplayEntrances(rom, project.Data);
            WriteGfx(rom, project.Data, warnings);
            ReplayExAnimation(rom, project.Data, warnings);

            // Skip level entries whose key is not a level number. A project should never contain
            // one, but an editor bug wrote entries keyed -1 for a while, and refusing to build a
            // project someone already has is worse than saying what was ignored.
            var levels = project.Data.Levels
                .Where(kv => IsLevelKey(kv.Key, out _))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            foreach (string bad in project.Data.Levels.Keys.Where(k => !IsLevelKey(k, out _)))
                warnings.Add($"ignored level entry '{bad}' — not a level number");

            // Header edits are applied through the parse path (same as the session), so
            // seed them before any level is parsed below.
            foreach (var (key, state) in levels)
                if (state.Header is { } hx)
                    rom.LevelHeaderOverrides[Convert.ToInt32(key, 16)] = Convert.FromHexString(hx);

            foreach (var (key, state) in levels)
            {
                int level = Convert.ToInt32(key, 16);
                var lv = LevelParser.Parse(rom, level);

                // Layer-1 objects (DM16 placements need LM's ASM in the base to render in-game).
                var objs = state.Objects.Select(o => o.ToLevelObject()).ToList();
                if (!rom.HasDm16Hijack && objs.Any(o => o.IsDm16))
                {
                    warnings.Add($"level {key}: DM16 tile objects skipped (base lacks LM's Direct Map16 ASM)");
                    objs = objs.Where(o => !o.IsDm16).ToList();
                }
                byte[] encoded = LevelEncoder.Encode(lv, LevelEncoder.NormalizeStream(objs));
                rom.SetLayer1Pointer(level, AllocateAutoExpand(rom, encoded));

                // Layer 2 as a background image: bank $FF IS the mode, and the low 16 bits
                // pick the stream. Checked first — a level's layer 2 is a background or an
                // object stream, never both.
                if (state.Layer2Background is { } bgLo16)
                    rom.SetLayer2Pointer(level, 0xFF0000 | (bgLo16 & 0xFFFF));
                // Layer 2, when the project carries a stream for it. Same format as layer 1
                // (its own header copy, which the game skips), and writing a real bank into
                // the pointer is what puts the level in object mode at all.
                else if (state.Layer2Objects is { } l2dto)
                {
                    var l2 = l2dto.Select(o => o.ToLevelObject()).ToList();
                    byte[] enc2 = LevelEncoder.Encode(lv, LevelEncoder.NormalizeStream(l2));
                    rom.SetLayer2Pointer(level, AllocateAutoExpand(rom, enc2));
                    if (!Rom.LoadsLayer2Objects(lv.Header.LevelMode))
                        warnings.Add($"level {key}: layer-2 objects written but level mode " +
                                     $"{lv.Header.LevelMode:X2} never loads them");
                }

                // Sprites: relocatable only when LM's per-level bank table exists; a vanilla
                // base reads bank $07 fixed, so there it's overwrite-in-place-if-it-fits.
                var sd = new SpriteData { SpriteMemory = state.SpriteMemory, Buoyancy = state.Buoyancy };
                sd.Sprites.AddRange(state.Sprites.Select(s => s.ToSprite()));
                // LM's size table (CONTRACT §11): a record's length is per (extra bits, number), and
                // the game reads the table, so every sprite carrying extra bytes registers its size.
                // Two placements of one sprite with different lengths cannot both be right.
                foreach (var g in sd.Sprites.Where(s => s.ExtraBytes is not null).GroupBy(s => (s.Extra, s.Number)))
                {
                    int size = 3 + g.Max(s => s.ExtraBytes!.Length);
                    if (g.Select(s => s.ExtraBytes!.Length).Distinct().Count() > 1)
                        warnings.Add($"level {key}: sprite {g.Key.Number:X2} (extra bits {g.Key.Extra}) placed with differing extra-byte counts; the game reads {size} bytes for all of them");
                    rom.SetSpriteEntrySize(g.Key.Extra, g.Key.Number, size);
                }
                byte[] sprites = sd.Encode();
                if (rom.LmSpriteBankTable >= 0)
                {
                    int snes = AllocateAutoExpand(rom, sprites);
                    rom.SetSpritePointerWord(level, snes & 0xFFFF);
                    rom.Data[rom.FileOffset(rom.LmSpriteBankTable + level)] = (byte)(snes >> 16);
                }
                else
                {
                    int cur = rom.SpritePointer(level);
                    int curLen = SpriteData.Parse(rom, level).Encode().Length;   // Encode is Parse's exact inverse
                    if (sprites.Length <= curLen)
                        sprites.CopyTo(rom.Data, rom.FileOffset(cur));
                    else
                        warnings.Add($"level {key}: sprite list grew and the vanilla base can't relocate it (needs an LM-saved base)");
                }

                // Palette: base level palette + the project's CGRAM edits as an LM custom palette.
                if (state.Palette.Count > 0)
                {
                    if (!rom.HasLmPaletteHook)
                        warnings.Add($"level {key}: palette edits skipped (base lacks LM's palette ASM)");
                    else
                    {
                        var pal = Palette.Load(rom, lv.Header, level);
                        foreach (var (idx, bgr) in state.Palette)
                            if (idx is >= 0 and < 256) pal.Bgr[idx] = (ushort)bgr;
                        rom.WriteLmCustomPalette(level, pal.Bgr[0], pal.Bgr);
                    }
                }

                WriteBackground(rom, level, key, state, warnings);
                int lt3 = WriteLayer3Tilemap(rom, project.Data, level, key, state, warnings);
                WriteGfxRecord(rom, level, key, state, warnings, lt3);
            }

            Directory.CreateDirectory(Path.Combine(project.Folder, "build"));
            string outPath = Path.Combine(project.Folder, "build", project.Name + ".smc");
            RatsWriter.SaveAs(rom, outPath);   // fixes the checksum before writing
            string status = $"built {Path.GetFileName(outPath)} ({project.Data.Levels.Count} level(s))"
                          + (warnings.Count > 0 ? "  —  " + string.Join("; ", warnings) : "");
            return (status, outPath);
        }
        // A file that cannot be read or written is the caller's to explain — it knows what the
        // user was doing and owns the dialog. Everything else is still a build failure here.
        catch (Exception e) when (!FileProblem.IsFile(e)) { return ("build failed: " + e.Message, null); }
    }

    /// <summary>Build, then export export\&lt;name&gt;.bps. For a prepped project the patch
    /// SOURCE is the user's stock vanilla ROM (prep is deterministic, so vanilla + patch
    /// reproduces the built ROM exactly) — the standard hack-distribution form anyone can
    /// apply with a generic BPS patcher. Falls back to diffing against the project's own
    /// base copy when no verified vanilla ROM is available (or the base isn't prepped).
    /// Both sides are diffed with copier headers stripped, per patching convention.</summary>
    internal static (string status, string? bpsPath) ExportBps(Project project, string? vanillaRomPath)
    {
        var (status, outPath) = Build(project);
        if (outPath is null) return (status, null);

        byte[] source;
        string sourceNote;
        if (project.Data.BaseRom.PrepVersion > 0 && vanillaRomPath is not null &&
            File.Exists(vanillaRomPath) &&
            RomHash.HeaderlessSha256File(vanillaRomPath) == RomHash.VanillaUsSha256)
        {
            source = RomHash.HeaderlessSpan(File.ReadAllBytes(vanillaRomPath)).ToArray();
            sourceNote = "applies to a stock vanilla SMW (U) ROM";
        }
        else
        {
            source = RomHash.HeaderlessSpan(File.ReadAllBytes(project.BaseRomPath)).ToArray();
            sourceNote = "applies to this project's base ROM";
        }
        byte[] target = RomHash.HeaderlessSpan(File.ReadAllBytes(outPath)).ToArray();

        string dir = Path.Combine(project.Folder, "export");
        Directory.CreateDirectory(dir);
        string bps = Path.Combine(dir, project.Name + ".bps");
        File.WriteAllBytes(bps, BpsWriter.Create(source, target));
        return ($"exported {Path.GetFileName(bps)} ({new FileInfo(bps).Length} bytes, {sourceNote})", bps);
    }

    /// <summary>The in-game GFX stage needs the GFX-bypass loader + a locatable record
    /// table — present on V2-prepped bases and on LM-saved bases (their own layout).</summary>
    private static bool GfxCapable(Rom rom) => rom.HasLmGfxLoader && rom.LmGfxBypassBase > 0;

    /// <summary>
    /// Write the project's imported GFX files into the ROM: zero-pad each blob to a full
    /// 128-tile file at the depth its CONSUMER uploads at — the ROM's bit depth for the slots
    /// the vanilla expander serves, 2bpp for a layer-3 slot, whose pass copies a fixed 0x800
    /// bytes and never expands — LC_LZ2-compress, allocate (GFX pointers are 24-bit — bank-crossing is fine,
    /// the decompressor's reads wrap LoROM banks), and point the id's pointer at it:
    /// vanilla ids (&lt;0x34, copy-on-write forks of stock files) through the vanilla three
    /// tables — works on ANY base; 0x80-0xFF through the fixed $0FF600 table; 0x100+
    /// through the per-ROM ExGFX table. Deterministic: ids ascending.
    /// </summary>
    private static void WriteGfx(Rom rom, ProjectFile data, List<string> warnings)
    {
        // A file some level points a LAYER-3 slot at (record words 12-15) is not uploaded by the
        // vanilla expander — the layer-3 pass copies a FIXED 0x400 words straight through and
        // never expands (RomPrep's `l3copy`). So it is 128 tiles at 2bpp = 0x800 bytes whatever
        // the ROM's depth is, and padding it to the ROM's 4bpp 0x1000 makes the decompressor
        // write 0x800 bytes MORE than the slot can ever use into the shared $7E:AD00 buffer,
        // for no gain. The upload takes the first 0x800 either way, which is why the slot still
        // looks right while something further up the buffer does not.
        var layer3Files = data.Levels.Values
            .SelectMany(l => l.GfxOverrides.Where(kv => kv.Key is >= 12 and <= 15))
            .Select(kv => kv.Value)
            .ToHashSet();

        foreach (var (idHex, b64) in data.Gfx.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            int full = 128 * Gfx.TileBytes(layer3Files.Contains(Convert.ToInt32(idHex, 16))
                                           ? Layer3.Bpp : Gfx.RomBpp(rom));
            int id = Convert.ToInt32(idHex, 16);
            if (id >= 0x34 && !GfxCapable(rom))
            {
                warnings.Add($"GFX{id:X3} skipped (base lacks the in-game GFX loader — File → Upgrade base to prep v{RomPrep.Version})");
                continue;
            }
            if (id is >= 0x60 and <= 0x63) continue;         // ExAnimation source files: ReplayExAnimation, raw
            if (id is >= 0x34 and < 0x80)
            {
                warnings.Add($"GFX{id:X2} skipped (0x34-0x7F are not loadable ids)");
                continue;
            }
            // The three vanilla pointer tables are 0x32 bytes each and ADJACENT, so writing
            // "entry 0x32" or "0x33" lands on the next table's entry for GFX00/GFX01 and
            // silently repoints those instead. Those two ids are the animation blobs and are
            // not table-addressed at all (fixed operands at $00B88B), so there is nothing to
            // write even if the arithmetic were safe.
            if (id is 0x32 or 0x33)
            {
                warnings.Add($"GFX{id:X2} skipped (animation source — not reachable through the GFX pointer tables)");
                continue;
            }
            if (id >= 0x100 && rom.LmExGfxBase <= 0)
            {
                warnings.Add($"GFX{id:X3} skipped (base lacks the ExGFX 0x100+ pointer table)");
                continue;
            }
            byte[] raw = Convert.FromBase64String(b64);
            if (raw.Length < full) { var p = new byte[full]; raw.CopyTo(p, 0); raw = p; }
            int snes = AllocateAutoExpand(rom, Gfx.Lz2Compress(raw), avoidBankCross: false);
            int fo = id switch
            {
                < 0x34 => -1,                                                  // three parallel tables
                < 0x100 => rom.FileOffset(Gfx.ExGfx80Table + (id - 0x80) * 3),
                _ => rom.FileOffset(rom.LmExGfxBase + (id - 0x100) * 3),
            };
            if (fo < 0)
            {
                rom.Data[rom.FileOffset(Gfx.PtrLow) + id] = (byte)snes;
                rom.Data[rom.FileOffset(Gfx.PtrHigh) + id] = (byte)(snes >> 8);
                rom.Data[rom.FileOffset(Gfx.PtrBank) + id] = (byte)(snes >> 16);
            }
            else
            {
                rom.Data[fo] = (byte)snes; rom.Data[fo + 1] = (byte)(snes >> 8); rom.Data[fo + 2] = (byte)(snes >> 16);
            }
        }
    }

    /// <summary>
    /// Write an edited layer-2 background.
    ///
    /// Two routes, and which one is available is a property of the BASE (CONTRACT §10a/§10b):
    ///
    /// * CUSTOM — what LM does, and what this does whenever the base has the `$05803B` hook (any
    ///   LM-saved base, ours from prep v10). The background becomes an ordinary relocatable RATS
    ///   block that a real 24-bit pointer names, the per-level settings byte at `$0EF310` says
    ///   "custom background, do not fill the map", and the stream carries its own page plane. No
    ///   size limit, and it FORKS: the level gets its own copy, so editing a background that four
    ///   other levels share no longer edits theirs. LM forks on any modification for the same
    ///   reason.
    /// * IN PLACE — the fallback on a base without the hook. Vanilla derives the page byte from
    ///   the stream's ADDRESS and reads bank `$0C` only, so the stream cannot move or grow: the
    ///   edit ships only when it re-encodes no larger than what it replaces, and says so when it
    ///   does not. A shared stream stays shared, which is the vanilla arrangement.
    /// </summary>
    private static void WriteBackground(Rom rom, int level, string key, ProjectFile.LevelState state,
                                        List<string> warnings)
    {
        if (state.BgTilemap is not { } b64) return;
        if (!rom.Layer2IsBackground(level))
        {
            warnings.Add($"level {key}: background edit skipped — its layer 2 is an object stream now");
            return;
        }
        int lo16 = rom.Layer2Pointer(level) & 0xFFFF;
        var low = Convert.FromBase64String(b64);
        var pages = state.BgTilemapPages is { } pg ? Convert.FromBase64String(pg) : BgImage.PagePlane(rom, level);

        if (rom.HasLmLayer2Custom)
        {
            // Each cell's own page rides across — a tile painted from the other page builds as
            // that tile (§10b's plane); the re-layout is vanilla's 0x1B0 stride to LM's 0x200.
            var planes = BgImage.ToCustomPlanes(low, pages);
            int snes = AllocateAutoExpand(rom, BgImage.EncodeCustom(planes));
            rom.SetLayer2Pointer(level, snes);
            int fo = rom.FileOffset(RomPrep.Layer23Settings + level);
            rom.Data[fo] = (byte)(rom.Data[fo] | RomPrep.Layer23CustomBg);
            return;
        }

        // A vanilla stream has ONE page, from its address. Cells painted from the other page
        // have nowhere to say so and build as this page's tile of the same number — what LM
        // does too ("the equivalent tile number of the current bank", §10c) — so say it.
        int fixedPage = BgImage.PageFor(lo16), strays = pages.Count(p => p != fixedPage);
        if (strays > 0)
            warnings.Add($"level {key}: {strays} background tile(s) painted from page {1 - fixedPage} build as "
                       + $"page {fixedPage}'s — this base has no custom-background hook (File → Upgrade base)");
        BgImage.Decode(rom, lo16, out int consumed);
        var encoded = BgImage.Encode(low);
        if (encoded.Length > consumed)
        {
            warnings.Add($"level {key}: background edit skipped — it re-encodes to 0x{encoded.Length:X} bytes "
                       + $"and its stream is 0x{consumed:X}, and this base cannot hold a relocatable "
                       + $"custom background (File → Upgrade base to prep v{RomPrep.Version})");
            return;
        }
        encoded.CopyTo(rom.Data, rom.FileOffset(BgImage.Bank | lo16));
    }

    /// <summary>
    /// Insert a level's layer-3 tilemap as an ExGFX file and return the id it took, or -1.
    ///
    /// The tilemap rides the ordinary graphics path — LM's LT3 slot names a GFX file number and
    /// the same resolver fetches it (§7d) — so this is compress, allocate, point, exactly as
    /// <see cref="WriteGfx"/> does for a real graphics file. The id comes from the 0x80-0xFF
    /// range, which is the one LM's own dialog offers for LT3, and is chosen as the lowest that
    /// neither the project nor the base already uses so a rebuild picks the same one.
    /// </summary>
    private static int WriteLayer3Tilemap(Rom rom, ProjectFile data, int level, string key,
                                          ProjectFile.LevelState state, List<string> warnings)
    {
        if (state.Layer3Tilemap is not { } b64) return -1;
        if (!rom.HasLmLayer3Tilemap)
        {
            warnings.Add($"level {key}: the layer-3 tilemap stays editor-only (base lacks LM's "
                       + "layer-3 tilemap loader — it draws the level mode's own tilemap instead)");
            return -1;
        }
        var raw = Convert.FromBase64String(b64);
        if (Array.IndexOf(Layer3.TilemapSizes, raw.Length) is < 0 or 3)
        {
            warnings.Add($"level {key}: layer-3 tilemap skipped — 0x{raw.Length:X} bytes is not one "
                       + "of LM's sizes (0x800 / 0x1000 / 0x2000)");
            return -1;
        }
        int id = 0x80;
        while (id <= 0xFF && (data.Gfx.ContainsKey($"{id:X}") || Gfx.SourceSnes(rom, id) >= 0)) id++;
        if (id > 0xFF)
        {
            warnings.Add($"level {key}: layer-3 tilemap skipped — no free ExGFX id in 80-FF");
            return -1;
        }
        int snes = AllocateAutoExpand(rom, Gfx.Lz2Compress(raw), avoidBankCross: false);
        int fo = rom.FileOffset(Gfx.ExGfx80Table + (id - 0x80) * 3);
        rom.Data[fo] = (byte)snes; rom.Data[fo + 1] = (byte)(snes >> 8); rom.Data[fo + 2] = (byte)(snes >> 16);
        return id;
    }

    /// <summary>
    /// Write a level's Super-GFX-Bypass record (16 words at LmGfxBypassBase + level*0x20):
    /// the base ROM's record (or an all-default one) with the project's slot overrides
    /// applied and the w0 enable bit set — byte-parity with the session overlay
    /// (LunarMagic.LmGfxBypass) by construction.
    /// </summary>
    private static void WriteGfxRecord(Rom rom, int level, string key, ProjectFile.LevelState state,
                                       List<string> warnings, int lt3File = -1)
    {
        bool advEdit = state.Layer3Advanced is not null || state.Layer3AdvancedOff;
        if (state.GfxOverrides.Count == 0 && lt3File < 0 && !advEdit) return;
        if (!GfxCapable(rom))
        {
            warnings.Add($"level {key}: GFX slot overrides skipped (base lacks the in-game GFX loader — File → Upgrade base to prep v{RomPrep.Version})");
            return;
        }
        // The record is two halves with two enable bits (§12b): words 0-11 behind w0 bit 15, and
        // the layer-3 slots 12-15 behind bit 14. The layer-3 half is carried across on its own,
        // because LmGfxBypass hands back nothing at all for a base that bypasses layer 3 and
        // nothing else — and rebuilding from all-defaults would drop its LG slots.
        var w = rom.LmGfxBypass(level);
        if (w is null) { w = new ushort[16]; Array.Fill(w, (ushort)0x7F); w[0] = 0x007F; }
        if (rom.LmGfxRecord(level) is { } real && (real[0] & 0x4000) != 0)
        {
            Array.Copy(real, 12, w, 12, 4);
            w[0] |= 0x4000;
        }
        foreach (var (word, file) in state.GfxOverrides)
            if (word is >= 0 and < 16) w[word] = (ushort)((w[word] & ~0xFFF) | (file & 0xFFF));
        // The advanced layer-3 group rides the spare high nibbles of nine of these words, so it
        // is rewritten unconditionally — `w` was rebuilt from all-defaults above and would
        // otherwise drop settings the base ROM already had. No enable bit in w0: its own is the
        // low bit of w12's nibble (§12b).
        var adv = state.Layer3AdvancedOff ? null
                : state.Layer3Advanced ?? Layer3.ReadAdvanced(rom.LmGfxRecord(level) ?? w);
        Layer3.WriteAdvanced(w, adv);
        // Light only the bit for the half that was actually used. Setting bit 15 for a
        // layer-3-only edit would switch on an FG/BG/SP bypass the project never asked for.
        if (state.GfxOverrides.Keys.Any(k => k is >= 0 and <= 11)) w[0] |= 0x8000;
        if (state.GfxOverrides.Keys.Any(k => k is >= 12 and <= 15)) w[0] |= 0x4000;
        // ...and the third enable, bit 13, with everything it needs packed into word 1: the file
        // in the low 12 bits, the size in 12-13, the destination in 14-15 (§12b).
        if (lt3File >= 0)
        {
            int size = Array.IndexOf(Layer3.TilemapSizes, Convert.FromBase64String(state.Layer3Tilemap!).Length);
            w[0] |= 0x2000;
            w[1] = (ushort)((lt3File & 0xFFF) | (size & 3) << 12 | Layer3.BuiltTilemapDestination << 14);
        }
        int fo = rom.FileOffset(rom.LmGfxBypassBase + level * 0x20);
        for (int i = 0; i < 16; i++) { rom.Data[fo + i * 2] = (byte)w[i]; rom.Data[fo + i * 2 + 1] = (byte)(w[i] >> 8); }

        if (!rom.HasLmVramPatch && state.GfxOverrides.Keys.Any(k => k is 2 or 3))
            warnings.Add($"level {key}: BG2/BG3 slot overrides stay editor-only (base lacks LM's VRAM patch)");
        if (state.GfxOverrides.Keys.Any(k => k is 0 or 1))
            warnings.Add($"level {key}: AN1/AN2 slot overrides stay editor-only (ExAnimation sources aren't inserted)");
        if (!rom.HasLmLayer3Advanced && advEdit)
            warnings.Add($"level {key}: advanced layer-3 settings stay editor-only (base lacks LM's advanced layer-3 reader)");
        if (!rom.HasLmLayer3Gfx && state.GfxOverrides.Keys.Any(k => k is >= 12 and <= 15))
            warnings.Add($"level {key}: LG1-LG4 slot overrides stay editor-only (base lacks LM's layer-3 GFX loader — it streams GFX 28-2B regardless)");
    }

    // Level/sprite streams ride 16-bit runtime pointers — those stay bank-cross-safe;
    // GFX blobs are 24-bit-addressed and may cross banks (avoidBankCross: false).
    private static int AllocateAutoExpand(Rom rom, byte[] data, bool avoidBankCross = true)
    {
        try { return RatsWriter.Allocate(rom, data, avoidBankCross); }
        catch (InvalidOperationException)
        {
            rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, rom.ActualRomSize * 2)));
            return RatsWriter.Allocate(rom, data, avoidBankCross);
        }
    }
}
