namespace PipeDream;

// The debug CLI toolbox: flag dispatch + every handler, so Main knows nothing about
// debug commands. One-shot inspection / reverse-engineering commands; no GUI.
static class DebugCommands
{
    // Flag → handler; every handler gets (args, index-of-its-flag) and returns an exit
    // code. Handlers with friendlier signatures are adapted inline.
    private static readonly (string Flag, Func<string[], int, int> Run)[] Commands =
    {
        ("--selfcheck",         (_, _) => SelfCheck()),
        ("--newproject",        NewProject),
        ("--buildproject",      BuildProject),
        ("--render",            RenderLevel),
        ("--writedm16",         WriteDm16),
        ("--dumpcell",          DumpCell),
        ("--markers",           DumpMarkers),
        ("--exits",             DumpExits),
        ("--entrances",         DumpEntrances),
        ("--mainentrance",      DumpMainEntrance),
        ("--gfxsheet",          GfxSheet),
        ("--blobsheet",         BlobSheet),
        ("--diff",              (a, i) => DiffRoms(a[i + 1], a[i + 2])),
        ("--globalexanim",      (a, i) => DumpGlobalExAnim(a[i + 1])),
        ("--pixitrace",         PixiTrace),
        ("--sprites",           DumpSprites),
        ("--tilepng",           TilePng),
        ("--map16def",          Map16Def),
        ("--exanim",            (a, i) => DumpExAnim(a[i + 1], Convert.ToInt32(a[i + 2], 16))),
        ("--disasm",            Disassemble),
        ("--gen-spritedisplay", GenSpriteDisplay),
    };

    /// <summary>Run the debug command named in <paramref name="args"/>, if any.
    /// Returns its exit code, or null when no debug flag is present (launch the editor).</summary>
    public static int? TryDispatch(string[] args)
    {
        foreach (var (flag, run) in Commands)
        {
            int i = Array.IndexOf(args, flag);
            if (i >= 0) return run(args, i);
        }
        return null;
    }

    // --selfcheck : run the ROM self-check suite (exit code = failures).
    public static int SelfCheck() => RomSelfCheck.Run();

    // --newproject <folder> <baseRom> : headless project creation (scripting/tests) —
    // same pipeline as the New Project wizard, including automatic vanilla-base prep.
    public static int NewProject(string[] args, int ni)
    {
        var p = Project.Create(args[ni + 1], args[ni + 2]);
        Console.WriteLine($"created {p.FilePath}");
        Console.WriteLine($"base: {p.Data.BaseRom.Title}, prepVersion {p.Data.BaseRom.PrepVersion}, " +
                          $"sha256 {p.Data.BaseRom.Sha256}");
        return 0;
    }

    // --buildproject <project.pdp> [--bps] : headless project build (CI/scripting) —
    // same pipeline as File → Build ROM, plus optional BPS export.
    public static int BuildProject(string[] args, int bi)
    {
        var project = Project.Open(args[bi + 1]);
        if (project.ValidateBase() is { } problem) { Console.WriteLine("ERROR: " + problem); return 1; }
        if (args.Contains("--bps"))
        {
            var (bpsStatus, bpsPath) = RomBuilder.ExportBps(project, Config.Load().VanillaRomPath);
            Console.WriteLine(bpsStatus);
            return bpsPath is null ? 1 : 0;
        }
        var (status, outPath) = RomBuilder.Build(project);
        Console.WriteLine(status);
        return outPath is null ? 1 : 0;
    }

    // --render <rom> <levelHex> <out.png> [cropTilesW] : compose a level to PNG for inspection.
    public static int RenderLevel(string[] args, int ri)
    {
        string romPath = args.ElementAtOrDefault(ri + 1) ?? ReferenceRoms.Vanilla;
        int level = Convert.ToInt32(args.ElementAtOrDefault(ri + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(ri + 3) ?? "level.png";
        int cropW = int.TryParse(args.ElementAtOrDefault(ri + 4), out var cw) ? cw : 0;

        var rom = Rom.Load(romPath);
        var lv = LevelParser.Parse(rom, level);
        var grid = ObjectEngine.Render(rom, lv);
        int phase = int.TryParse(args.ElementAtOrDefault(ri + 5), out var ph) ? ph & 3 : 0;
        var (px, w, h) = Map16.ComposeLevel(rom, lv.Header, grid, level, phase);
        SpriteData.Parse(rom, level).DrawOverlay(px, w, h, rom, lv.Header, level);

        if (cropW > 0 && cropW * 16 < w)
        {
            int cw16 = cropW * 16;
            var crop = new uint[cw16 * h];
            for (int y = 0; y < h; y++)
                Array.Copy(px, y * w, crop, y * cw16, cw16);
            px = crop; w = cw16;
        }
        Png.Write(outPath, px, w, h);
        Console.WriteLine($"wrote {outPath}: {w}x{h}, level 0x{level:X3}, tileset {lv.Header.Tileset}, " +
                          $"header {Convert.ToHexString(lv.Header.ToBytes())}");
        return 0;
    }

    // --exits <rom> [levelHex] : dump a level's screen exits, or every level's when the
    // level is omitted. Prints the raw object fields next to the decoded meaning, so the
    // "Y field is the screen" contract stays checkable against a real ROM.
    public static int DumpExits(string[] args, int ei)
    {
        var rom = Rom.Load(args.ElementAtOrDefault(ei + 1) ?? ReferenceRoms.Vanilla);
        string? one = args.ElementAtOrDefault(ei + 2);
        IEnumerable<int> levels = one is not null ? [Convert.ToInt32(one, 16)] : Enumerable.Range(0, Rom.LevelCount);
        foreach (int level in levels)
        {
            List<LevelObject> exits;
            try { exits = LevelParser.Parse(rom, level).Objects.Where(o => o.IsScreenExit || o.IsLmSecondaryExit).ToList(); }
            catch { continue; }
            if (exits.Count == 0) continue;
            Console.WriteLine($"level 0x{level:X3}: {exits.Count} exit(s)");
            foreach (var o in exits)
                Console.WriteLine($"  {(o.IsLmSecondaryExit ? "lm-secondary" : "vanilla     ")} " +
                                  $"streamScreen {o.Screen:X2}  Y(screen) {o.ExitScreen:X2}  xNib {o.XNibble:X}" +
                                  $"  dest {o.ExitDestination:X2}" +
                                  (o.IsLmSecondaryExit ? "" : $"  water {o.ExitIsWater}  secondary {o.ExitUsesSecondary}"));
        }
        return 0;
    }

    // --entrances <rom> [indexHex] : dump secondary entrance records (non-empty ones when
    // no index is given), decoded out of the four $05F800/FA00/FC00/FE00 tables.
    public static int DumpEntrances(string[] args, int ei)
    {
        var rom = Rom.Load(args.ElementAtOrDefault(ei + 1) ?? ReferenceRoms.Vanilla);
        string? one = args.ElementAtOrDefault(ei + 2);
        IEnumerable<int> idx = one is not null
            ? [Convert.ToInt32(one, 16)] : Enumerable.Range(0, Rom.SecondaryEntranceCount);
        int shown = 0;
        foreach (int i in idx)
        {
            var e = rom.ReadSecondaryEntrance(i);
            if (one is null && e.ToBytes().All(b => b == 0)) continue;
            shown++;
            Console.WriteLine($"entrance {i:X3}: bytes {Convert.ToHexString(e.ToBytes())}  " +
                              $"dest level {e.DestinationLevel:X2}  marioX {e.MarioX}  marioY {e.MarioY:X}  " +
                              $"bndryY {e.ScreenBoundaryY}  vscroll {e.VerticalScroll}  action {e.EntranceAction}");
        }
        if (one is null) Console.WriteLine($"{shown} non-empty entrance(s)");
        return 0;
    }

    // --mainentrance <rom> [levelHex] : dump a level's main entrance / entry settings.
    public static int DumpMainEntrance(string[] args, int mi)
    {
        var rom = Rom.Load(args.ElementAtOrDefault(mi + 1) ?? ReferenceRoms.Vanilla);
        string? one = args.ElementAtOrDefault(mi + 2);
        IEnumerable<int> levels = one is not null
            ? [Convert.ToInt32(one, 16)] : Enumerable.Range(0, Rom.LevelCount);
        foreach (int lvl in levels)
        {
            var e = rom.ReadMainEntrance(lvl);
            if (one is null && e.ToBytes().All(b => b == 0)) continue;
            Console.WriteLine($"level {lvl:X3}: bytes {Convert.ToHexString(e.ToBytes())}  " +
                              $"marioX {e.MarioX} marioY {e.MarioY:X}  action {e.EntranceAction}  " +
                              $"bndryY {e.ScreenBoundaryY} vscroll {e.VerticalScroll}  " +
                              $"l2scroll {e.Layer2Scroll:X} l2set {e.Layer2Setting}  " +
                              $"vert {e.VerticalLevel} skipwalk {e.SkipEntranceWalk}");
        }
        return 0;
    }

    // --writedm16 <rom> <levelHex> <out> : inject known Direct-Map16 test tiles and save,
    // so the result can be opened in Lunar Magic to verify the encoding round-trips.
    public static int WriteDm16(string[] args, int wi)
    {
        string romPath = args.ElementAtOrDefault(wi + 1) ?? ReferenceRoms.LmAfter;
        int level = Convert.ToInt32(args.ElementAtOrDefault(wi + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(wi + 3) ?? ReferenceRoms.Resource("test_dm16.smc");

        var rom = Rom.Load(romPath);
        if (!rom.HasDm16Hijack)
        {
            Console.WriteLine("ERROR: ROM lacks the LM Direct Map16 ASM — open/save it in LM once first.");
            return 1;
        }
        var lv = LevelParser.Parse(rom, level);

        // Two known test placements in empty sky (screen 0): Form A (0x105) and Form B (0x205).
        var added = new[]
        {
            LevelObject.MakeDm16(0x105, screen: 0, xNib: 2, y: 8),
            LevelObject.MakeDm16(0x205, screen: 0, xNib: 5, y: 8),
        };
        var newObjs = added.Concat(lv.Objects).ToList();

        byte[] data = LevelEncoder.Encode(lv, newObjs);
        if (rom.ActualRomSize < 0x180000) rom.ExpandTo(0x200000);
        int addr = RatsWriter.Allocate(rom, data);
        rom.SetLayer1Pointer(level, addr);
        RatsWriter.SaveAs(rom, outPath);
        Console.WriteLine($"wrote {outPath}: level 0x{level:X3}, added DM16 tiles " +
                          "0x105 @ (2,8) [Form A] and 0x205 @ (5,8) [Form B]; pointer -> $" + addr.ToString("X6"));

        // verify by reloading + re-parsing
        var re = Rom.Load(outPath);
        var rl = LevelParser.Parse(re, level);
        var dm = rl.Objects.Where(o => o.IsDm16 && (o.Dm16Tile == 0x105 || o.Dm16Tile == 0x205)).ToList();
        Console.WriteLine("reload check: " + string.Join(" ", dm.Select(o => $"0x{o.Dm16Tile:X3}@({o.AbsoluteX},{o.Y})")));
        return 0;
    }

    // --dumpcell <rom> <levelHex> <cx> <cy> : debug — grid value + Map16 def words for a cell.
    public static int DumpCell(string[] args, int di)
    {
        var rom = Rom.Load(args[di + 1]);
        int level = Convert.ToInt32(args[di + 2], 16);
        int cx = int.Parse(args[di + 3]), cy = int.Parse(args[di + 4]);
        var lv = LevelParser.Parse(rom, level);
        var grid = ObjectEngine.Render(rom, lv);
        int t = grid.Get(cx, cy);
        Console.WriteLine($"cell ({cx},{cy}) = 0x{t:X4}" +
                          ((t & ObjectEngine.Marker) != 0 ? " (MARKER)" : ""));
        if (t == Map16Grid.Empty || (t & ObjectEngine.Marker) != 0) return 0;
        var words = t >= Map16.FgTiles
            ? Map16.LmExtendedDef(rom, t)
            : Map16.Definition(rom, Map16.BuildDefPointers(rom, lv.Header.Tileset), t);
        foreach (var (w, q) in words.Zip(new[] { "TL", "BL", "TR", "BR" }))
            Console.WriteLine($"  {q}: raw {w.Raw:X4}  8x8 tile {w.Tile:X3} pal {w.Palette} " +
                              $"prio {(w.Priority ? 1 : 0)} flip {(w.FlipX ? "X" : "")}{(w.FlipY ? "Y" : "")}");
        return 0;
    }

    // --markers <rom> <levelHex> : debug — which object numbers still render as markers.
    public static int DumpMarkers(string[] args, int mi)
    {
        var rom = Rom.Load(args[mi + 1]);
        int level = Convert.ToInt32(args[mi + 2], 16);
        var lv = LevelParser.Parse(rom, level);
        try { ObjectEngine.RenderEmulated(rom, lv.Header, lv.DataPointer, 0); Console.WriteLine("engine: emulated"); }
        catch (Exception e) { Console.WriteLine($"engine: ported fallback ({e.Message})"); }
        var grid = ObjectEngine.Render(rom, lv);
        var markers = new Dictionary<int, int>();
        for (int i = 0; i < grid.Tiles.Length; i++)
            if (grid.Tiles[i] != Map16Grid.Empty && (grid.Tiles[i] & ObjectEngine.Marker) != 0)
                markers[grid.Tiles[i] & 0xFF] = markers.GetValueOrDefault(grid.Tiles[i] & 0xFF) + 1;
        var objCounts = lv.Objects.Where(o => !o.IsScreenExit && !o.IsDm16)
            .GroupBy(o => (o.Extended, Num: o.Extended ? o.ExtendedNumber : o.Number))
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"level 0x{level:X3}: {lv.Objects.Count} objects, tileset {lv.Header.Tileset}");
        foreach (var (num, cells) in markers.OrderByDescending(kv => kv.Value))
        {
            int std = objCounts.GetValueOrDefault((false, num));
            int ext = objCounts.GetValueOrDefault((true, num));
            string h = std > 0 ? $"  handler ${ObjectEngine.Handler(rom, lv.Header.Tileset, num):X6}" : "";
            Console.WriteLine($"  marker obj 0x{num:X2}: {cells} cells  (std x{std}, ext x{ext}){h}");
        }
        return 0;
    }

    // --gfxsheet <rom> <fileHex> <out.png> : debug — render a GFX file as a tile sheet.
    public static int GfxSheet(string[] args, int gi)
    {
        var rom = Rom.Load(args[gi + 1]);
        int file = Convert.ToInt32(args[gi + 2], 16);
        var data = Gfx.DecompressFile(rom, file);
        int bpp = data.Length >= 0x1000 ? 4 : 3;
        var pal = Palette.Load(rom, LevelParser.Parse(rom, 0x105).Header);
        var (px, w, h) = Gfx.TileSheet(data, bpp, pal, 0x0A);
        Png.Write(args[gi + 3], px, w, h);
        Console.WriteLine($"wrote {args[gi + 3]}: GFX{file:X2}, {data.Length / Gfx.TileBytes(bpp)} tiles {bpp}bpp");
        return 0;
    }

    // --blobsheet <rom> <snesHex> <bpp> <out.png> : debug — decompress a blob and sheet it.
    public static int BlobSheet(string[] args, int bi)
    {
        var rom = Rom.Load(args[bi + 1]);
        var data = Gfx.Lz2Decompress(rom.Data, rom.FileOffset(Convert.ToInt32(args[bi + 2], 16)));
        int bpp = int.Parse(args[bi + 3]);
        var pal = Palette.Load(rom, LevelParser.Parse(rom, 0x106).Header);
        var (px, w, h) = Gfx.TileSheet(data, bpp, pal, 0x02);
        int scale = int.TryParse(args.ElementAtOrDefault(bi + 5), out var sc) ? sc : 1;
        if (scale > 1)
        {
            var big = new uint[w * scale * h * scale];
            for (int y = 0; y < h * scale; y++)
                for (int x = 0; x < w * scale; x++)
                    big[y * w * scale + x] = px[(y / scale) * w + (x / scale)];
            px = big; w *= scale; h *= scale;
        }
        Png.Write(args[bi + 4], px, w, h);
        Console.WriteLine($"wrote {args[bi + 4]}: {data.Length / Gfx.TileBytes(bpp)} tiles {bpp}bpp");
        return 0;
    }

    // --diff <a.smc> <b.smc> : byte-diff two ROMs for reverse-engineering. Coalesces changed
    // runs (gap <= 16), skips the SNES checksum, and flags RATS blocks new in B — the usual
    // home of LM-inserted data. Prints SNES/PC address + length + a hex preview of both sides.
    public static int DiffRoms(string aPath, string bPath)
    {
        var a = Rom.Load(aPath);
        var b = Rom.Load(bPath);
        Console.WriteLine($"A {System.IO.Path.GetFileName(aPath)}: {a.ActualRomSize / 1024}KB   " +
                          $"B {System.IO.Path.GetFileName(bPath)}: {b.ActualRomSize / 1024}KB");

        int n = Math.Min(a.ActualRomSize, b.ActualRomSize);
        int ah = a.HeaderOffset, bh = b.HeaderOffset;
        bool Diff(int pc) => pc is >= 0x7FDC and <= 0x7FDF ? false : a.Data[ah + pc] != b.Data[bh + pc];

        var runs = new List<(int pc, int len)>();
        for (int pc = 0; pc < n;)
        {
            if (!Diff(pc)) { pc++; continue; }
            int start = pc, gap = 0, last = pc;
            while (pc < n && gap <= 16) { if (Diff(pc)) { last = pc; gap = 0; } else gap++; pc++; }
            runs.Add((start, last - start + 1));
        }

        string Hex(Rom r, int pc, int len) =>
            string.Join(" ", Enumerable.Range(0, Math.Min(len, 24)).Select(i => $"{r.Data[r.HeaderOffset + pc + i]:X2}"));

        Console.WriteLine($"{runs.Count} changed run(s) (checksum skipped):");
        foreach (var (pc, len) in runs.OrderByDescending(r => r.len).Take(60).OrderBy(r => r.pc))
        {
            Console.WriteLine($"  SNES ${Rom.PcToSnes(pc):X6}  PC {pc:X6}  len {len}");
            Console.WriteLine($"    A: {Hex(a, pc, len)}");
            Console.WriteLine($"    B: {Hex(b, pc, len)}");
        }

        // RATS blocks present in B but not A (by PC offset) — likely the newly-inserted data.
        var aRats = RatsWriter.EnumerateRats(a).Select(r => r.PcOffset).ToHashSet();
        var newRats = RatsWriter.EnumerateRats(b).Where(r => !aRats.Contains(r.PcOffset)).ToList();
        if (newRats.Count > 0)
        {
            Console.WriteLine($"RATS blocks new in B ({newRats.Count}):");
            foreach (var r in newRats.Take(20))
                Console.WriteLine($"  SNES ${Rom.PcToSnes(r.PcOffset + 8):X6}  PC {r.PcOffset:X6}  size {r.Size}");
        }
        return 0;
    }

    // --globalexanim <rom> : dump LM's global ExAnimation slots' raw bytes (CONTRACT §12f).
    public static int DumpGlobalExAnim(string romPath)
    {
        var rom = Rom.Load(romPath);
        int ptr = rom.LmGlobalExAnimPtr;
        if (ptr < 0) { Console.WriteLine("No global ExAnimation list."); return 0; }
        Console.WriteLine($"Engine: setup ${rom.LmExAnimSetupEntry:X6}  processor ${rom.LmExAnimProcEntry:X6}");
        var frames = ExAnimation.ResolveGlobal(rom, 32);
        Console.WriteLine($"  [emulated 32 frames] {frames.Count(f => f.Ctrl != 0)} tile update(s):");
        foreach (var gf in frames.Where(f => f.Ctrl != 0).Take(24))
            Console.WriteLine($"    f{gf.Frame,2} slot{gf.Slot}: dest tile {gf.DestTile:X3}  " +
                              $"<- src ${gf.SrcSnes:X6}  (ctrl ${gf.Ctrl:X4})");
        var slots = ExAnimation.ReadGlobalRaw(rom);
        Console.WriteLine($"Global ExAnimation record @ ${ptr:X6}: {slots.Count} used slot(s)");
        Console.WriteLine("  (header fields are type-dependent/undecoded; trailing words in the");
        Console.WriteLine("   0x600+ tile range resolve to a $7Dxx/$ADxx source, §12e convention)");
        foreach (var s in slots)
        {
            var hdr = Convert.ToHexString(s.Raw, 0, Math.Min(ExAnimation.GlobalSlot.HeaderLen, s.Raw.Length));
            var words = string.Join(" ", Enumerable.Range(0, s.FrameCount).Select(f =>
            {
                int t = s.FrameTile(f);
                return t >= 0x600 ? $"{t:X3}(->${s.FrameSrcAddr(f):X4})" : $"{t:X3}";
            }));
            Console.WriteLine($"  slot {s.Index,2}: hdr {hdr}  {s.FrameCount} word(s): {words}");
        }
        return 0;
    }

    // --pixitrace <rom> <levelHex> <spriteNumHex> : trace a custom sprite's capture execution.
    public static int PixiTrace(string[] args, int pti)
    {
        var rom = Rom.Load(args[pti + 1]);
        int lvl = Convert.ToInt32(args[pti + 2], 16), wantNum = Convert.ToInt32(args[pti + 3], 16);
        var sd = SpriteData.Parse(rom, lvl);
        var s = sd.Sprites.First(x => x.Number == wantNum);
        SpriteRender.Trace = true;
        var oam = SpriteRender.Capture(rom, s);
        var banks = string.Join(",", (SpriteRender.LastBanks ?? new()).Order().Select(b => $"{b:X2}"));
        Console.WriteLine($"#{wantNum:X2} extra{s.Extra} custom={s.Extra >= 2} tablePtr=" +
            $"${rom.ReadValue(rom.PixiCustomTable + wantNum * 0x10, 3):X6}");
        Console.WriteLine($"  result: {(oam is null ? "NULL" : oam.Count + " tiles")}, " +
                          $"OAM writes={SpriteRender.LastOamWrites}, banks visited=[{banks}]");
        var hot = SpriteRender.LastPcHot ?? new();
        Console.WriteLine($"  slots after spawn: " + (SpriteRender.LastSlots is { } ls
            ? Convert.ToHexString(ls) : "n/a"));
        Console.WriteLine($"  $14C8 status writes: " + string.Join(" ",
            (SpriteRender.LastStatusLog ?? new()).Where(w => w.Addr < 0x14D4)
                .Select(w => $"{w.Pc:X6}:{w.Addr:X4}={w.V:X2}")));
        Console.WriteLine($"  inserted-bank addrs executed ({hot.Count}): " +
                          string.Join(" ", hot.Select(a => $"{a:X6}")));
        Console.WriteLine($"  ordered bank-01/02 steps: " + string.Join(" ",
            (SpriteRender.LastStepLog ?? new()).Select(t => $"{t.Pc & 0xFFFF:X4}[X={t.X:X2}]")));
        Console.WriteLine($"  final status $14C8={SpriteRender.LastStatus:X2} (0=erased)");
        return 0;
    }

    // --sprites <rom> <levelHex> : dump a level's sprite list + whether OAM capture yields tiles.
    public static int DumpSprites(string[] args, int spd)
    {
        var rom = Rom.Load(args[spd + 1]);
        int lvl = Convert.ToInt32(args[spd + 2], 16);
        var sd = SpriteData.Parse(rom, lvl);
        var h = LevelParser.Parse(rom, lvl).Header;
        string spStatus;
        try { var sp = SpriteRender.LoadSpTiles(rom, h, lvl); spStatus = sp is null ? "null" : $"{sp.Length} slots"; }
        catch (Exception e) { spStatus = "THREW: " + e.Message; }
        int hptr = LevelParser.Parse(rom, lvl).DataPointer, hfo = rom.FileOffset(hptr);
        Console.WriteLine($"  layer1 header @ ${hptr:X6}: {Convert.ToHexString(rom.Data.AsSpan(hfo, 5))} " +
                          $"(mode={h.LevelMode:X2} vert={rom.IsVerticalMode(h.LevelMode)})");
        int sptr = rom.SpritePointer(lvl), sfo = rom.FileOffset(sptr);
        Console.WriteLine($"Level {lvl:X3}: {sd.Sprites.Count} sprites, header.Screens={h.Screens}, " +
                          $"sizeBase ${rom.LmSpriteSizeBase:X6}, hijacked={rom.HasPixiSpriteHook}, SP tiles={spStatus}");
        Console.WriteLine($"  sprite data @ ${sptr:X6}: {Convert.ToHexString(rom.Data.AsSpan(sfo, 24))}");
        foreach (var s in sd.Sprites)
        {
            string how;
            bool pixiCustom = rom.HasPixiSpriteHook && s.Extra >= 2;
            if (s.IsScrollCommand) how = "scroll-cmd";
            else if (!pixiCustom && s.Extra < 2 && SpriteDisplay.TryGet(s.Number, out var rel)) how = $"static-table ({rel.Length} tiles)";
            else { var oam = SpriteRender.Capture(rom, s); how = oam is null ? "CAPTURE NULL (badge)" : $"captured {oam.Count}" + (pixiCustom ? " [PIXI]" : ""); }
            string eb = s.ExtraBytes is null ? "" : " eb=" + Convert.ToHexString(s.ExtraBytes);
            Console.WriteLine($"  #{s.Number:X2} extra{s.Extra} scr{s.Screen:X2} x{s.XNibble} y{s.Y:X2}{eb}  -> {how}");
        }
        return 0;
    }

    // --tilepng <rom> <levelHex> <tileHex> <out.png> : render one Map16 tile across the 4
    // animation phases (debug — for eyeballing animated-tile decode, e.g. munchers).
    public static int TilePng(string[] args, int tpng)
    {
        var rom = Rom.Load(args[tpng + 1]);
        int lvl = Convert.ToInt32(args[tpng + 2], 16);
        int tile = Convert.ToInt32(args[tpng + 3], 16);
        string outp = args[tpng + 4];
        var h = LevelParser.Parse(rom, lvl).Header;
        int scale = 8;
        var img = new uint[64 * scale * 16 * scale];   // 4 phases across, 16px tall, scaled
        for (int ph = 0; ph < 4; ph++)
        {
            var cache = Map16.ComposeAll(rom, h, lvl, ph);
            var t = cache[tile];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                            img[(y * scale + sy) * 64 * scale + (ph * 16 + x) * scale + sx] = t[y * 16 + x];
        }
        Png.Write(outp, img, 64 * scale, 16 * scale);
        Console.WriteLine($"wrote {outp}: Map16 {tile:X3} across 4 phases");
        return 0;
    }

    // --map16def <rom> <levelHex> <tileHex> : print a Map16 tile's 4 8x8 tiles (flip/palette).
    public static int Map16Def(string[] args, int m16)
    {
        var rom = Rom.Load(args[m16 + 1]);
        int lvl = Convert.ToInt32(args[m16 + 2], 16);
        int tile = Convert.ToInt32(args[m16 + 3], 16);
        int tileset = LevelParser.Parse(rom, lvl).Header.Tileset;
        var defPtr = Map16.BuildDefPointers(rom, tileset);
        var d = tile < 0x200 ? Map16.Definition(rom, defPtr, tile) : Map16.LmExtendedDef(rom, tile);
        Console.WriteLine($"Map16 {tile:X3} (tileset {tileset}, RomBpp {Gfx.RomBpp(rom)}): 8x8 tiles [" +
            string.Join(" ", d.Select(w => $"{w.Tile:X3}{(w.FlipX ? "h" : "")}{(w.FlipY ? "v" : "")} p{w.Palette}")) + "]");
        return 0;
    }

    // --exanim <rom> <levelHex> : dump a level's LM ExAnimation slots (CONTRACT §12e).
    public static int DumpExAnim(string romPath, int level)
    {
        var rom = Rom.Load(romPath);
        var slots = ExAnimation.ReadLevel(rom, level);
        Console.WriteLine($"Level {level:X3}: {slots.Count} ExAnimation slot(s)");
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            var frames = string.Join(" ", Enumerable.Range(0, s.FrameCount)
                .Select(f => $"{s.SrcTile(f):X3}(${s.FrameSrcAddrs[f]:X4})"));
            Console.WriteLine($"  slot {i}: destTile {s.DestTile:X3} (word ${s.DestWord:X4})  " +
                              $"{s.FrameCount} frames: {frames}   [u0={s.Unknown0:X4} u2={s.Unknown2:X4}]");
        }
        return 0;
    }

    // --disasm <rom> <snesHex> [count] [--m8] [--x8] : disassemble from a SNES address.
    public static int Disassemble(string[] args, int dsi)
    {
        var rom = Rom.Load(args[dsi + 1]);
        int snes = Convert.ToInt32(args[dsi + 2], 16);
        int count = int.TryParse(args.ElementAtOrDefault(dsi + 3), out var c) ? c : 40;
        Console.Write(Disasm.Dis(rom, snes, count, args.Contains("--m8"), args.Contains("--x8")));
        return 0;
    }

    // --gen-spritedisplay [rom] [out.json] : regenerate the static sprite display table.
    public static int GenSpriteDisplay(string[] args, int si)
    {
        var rom = Rom.Load(args.ElementAtOrDefault(si + 1) ?? ReferenceRoms.Vanilla);
        string outp = args.ElementAtOrDefault(si + 2) ?? @"src\Data\SpriteDisplay.json";
        File.WriteAllText(outp, SpriteDisplay.Generate(rom));
        var parsed = SpriteDisplay.Parse(File.ReadAllText(outp));
        Console.WriteLine($"wrote {outp}: {parsed.Count} sprite entries, " +
                          $"{parsed.Values.Sum(v => v.Oam.Length)} OAM tiles, " +
                          $"{parsed.Values.Count(v => v.Req.Any(r => r.Length > 0))} with GFX requirements");
        return 0;
    }
}
