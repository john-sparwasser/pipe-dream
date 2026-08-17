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

    /// <summary>Build build\&lt;name&gt;.smc from the project. Returns a status line
    /// (including per-level warnings for LM-gated features on a vanilla base).</summary>
    internal static (string status, string? outPath) Build(Project project)
    {
        try
        {
            var rom = Rom.Load(project.BaseRomPath);
            var warnings = new List<string>();
            if (ReplayMap16(rom, project.Data) is { } err) return (err, null);

            foreach (var (key, state) in project.Data.Levels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
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
                byte[] encoded = LevelEncoder.Encode(lv, rom, LevelEncoder.NormalizeStream(objs));
                rom.SetLayer1Pointer(level, AllocateAutoExpand(rom, encoded));

                // Sprites: relocatable only when LM's per-level bank table exists; a vanilla
                // base reads bank $07 fixed, so there it's overwrite-in-place-if-it-fits.
                var sd = new SpriteData { SpriteMemory = state.SpriteMemory, Buoyancy = state.Buoyancy };
                sd.Sprites.AddRange(state.Sprites.Select(s => s.ToSprite()));
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

                if (state.GfxOverrides.Count > 0)
                    warnings.Add($"level {key}: GFX slot overrides are editor-preview only (not written to the ROM yet)");
            }

            Directory.CreateDirectory(Path.Combine(project.Folder, "build"));
            string outPath = Path.Combine(project.Folder, "build", project.Name + ".smc");
            RatsWriter.SaveAs(rom, outPath);   // fixes the checksum before writing
            string status = $"built {Path.GetFileName(outPath)} ({project.Data.Levels.Count} level(s))"
                          + (warnings.Count > 0 ? "  —  " + string.Join("; ", warnings) : "");
            return (status, outPath);
        }
        catch (Exception e) { return ("build failed: " + e.Message, null); }
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

    // Level/sprite streams ride 16-bit runtime pointers — always bank-cross-safe.
    private static int AllocateAutoExpand(Rom rom, byte[] data)
    {
        try { return RatsWriter.Allocate(rom, data, avoidBankCross: true); }
        catch (InvalidOperationException)
        {
            rom.ExpandTo(Math.Min(0x400000, Math.Max(0x200000, rom.ActualRomSize * 2)));
            return RatsWriter.Allocate(rom, data, avoidBankCross: true);
        }
    }
}
