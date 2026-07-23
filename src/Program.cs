namespace PipeDream;

class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--selfcheck"))
            return RomSelfCheck.Run();

        int ri = Array.IndexOf(args, "--render");
        if (ri >= 0)
            return RenderLevel(args, ri);

        int wi = Array.IndexOf(args, "--writedm16");
        if (wi >= 0)
            return WriteDm16(args, wi);

        int di = Array.IndexOf(args, "--dumpcell");
        if (di >= 0)
            return DumpCell(args, di);

        int mi = Array.IndexOf(args, "--markers");
        if (mi >= 0)
            return DumpMarkers(args, mi);

        int gi = Array.IndexOf(args, "--gfxsheet");
        if (gi >= 0)
            return GfxSheet(args, gi);

        int bi = Array.IndexOf(args, "--blobsheet");
        if (bi >= 0)
            return BlobSheet(args, bi);

        int dfi = Array.IndexOf(args, "--diff");
        if (dfi >= 0)
            return DiffRoms(args[dfi + 1], args[dfi + 2]);

        int gxi = Array.IndexOf(args, "--globalexanim");
        if (gxi >= 0) return DumpGlobalExAnim(args[gxi + 1]);

        // --tilepng <rom> <levelHex> <tileHex> <out.png> : render one Map16 tile across the 4
        // animation phases (debug — for eyeballing animated-tile decode, e.g. munchers).
        int tpng = Array.IndexOf(args, "--tilepng");
        if (tpng >= 0)
        {
            var rom = Rom.Load(args[tpng + 1]);
            int lvl = Convert.ToInt32(args[tpng + 2], 16);
            int tile = Convert.ToInt32(args[tpng + 3], 16);
            string outp = args[tpng + 4];
            var h = Level.Parse(rom, lvl).Header;
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
        int m16 = Array.IndexOf(args, "--map16def");
        if (m16 >= 0)
        {
            var rom = Rom.Load(args[m16 + 1]);
            int lvl = Convert.ToInt32(args[m16 + 2], 16);
            int tile = Convert.ToInt32(args[m16 + 3], 16);
            int tileset = Level.Parse(rom, lvl).Header.Tileset;
            var defPtr = Map16.BuildDefPointers(rom, tileset);
            var d = tile < 0x200 ? Map16.Definition(rom, defPtr, tile) : Map16.LmExtendedDef(rom, tile);
            Console.WriteLine($"Map16 {tile:X3} (tileset {tileset}, RomBpp {Gfx.RomBpp(rom)}): 8x8 tiles [" +
                string.Join(" ", d.Select(w => $"{w.Tile:X3}{(w.FlipX ? "h" : "")}{(w.FlipY ? "v" : "")} p{w.Palette}")) + "]");
            return 0;
        }

        int exi = Array.IndexOf(args, "--exanim");
        if (exi >= 0)
            return DumpExAnim(args[exi + 1], Convert.ToInt32(args[exi + 2], 16));

        int dsi = Array.IndexOf(args, "--disasm");
        if (dsi >= 0)
        {
            var rom = Rom.Load(args[dsi + 1]);
            int snes = Convert.ToInt32(args[dsi + 2], 16);
            int count = int.TryParse(args.ElementAtOrDefault(dsi + 3), out var c) ? c : 40;
            Console.Write(Disasm.Dis(rom, snes, count, args.Contains("--m8"), args.Contains("--x8")));
            return 0;
        }

        int si = Array.IndexOf(args, "--gen-spritedisplay");
        if (si >= 0)
        {
            var rom = Rom.Load(args.ElementAtOrDefault(si + 1) ?? @"C:\SMW\Projects\.resources\SMW.smc");
            string outp = args.ElementAtOrDefault(si + 2) ?? @"src\Data\SpriteDisplay.json";
            File.WriteAllText(outp, SpriteDisplay.Generate(rom));
            var parsed = SpriteDisplay.Parse(File.ReadAllText(outp));
            Console.WriteLine($"wrote {outp}: {parsed.Count} sprite entries, " +
                              $"{parsed.Values.Sum(v => v.Oam.Length)} OAM tiles, " +
                              $"{parsed.Values.Count(v => v.Req.Any(r => r.Length > 0))} with GFX requirements");
            return 0;
        }

        using var app = new EditorApp();
        app.Run();
        return 0;
    }

    // --gfxsheet <rom> <fileHex> <out.png> : debug — render a GFX file as a tile sheet.
    private static int GfxSheet(string[] args, int gi)
    {
        var rom = Rom.Load(args[gi + 1]);
        int file = Convert.ToInt32(args[gi + 2], 16);
        var data = Gfx.DecompressFile(rom, file);
        int bpp = data.Length >= 0x1000 ? 4 : 3;
        var pal = Palette.Load(rom, Level.Parse(rom, 0x105).Header);
        var (px, w, h) = Gfx.TileSheet(data, bpp, pal, 0x0A);
        Png.Write(args[gi + 3], px, w, h);
        Console.WriteLine($"wrote {args[gi + 3]}: GFX{file:X2}, {data.Length / Gfx.TileBytes(bpp)} tiles {bpp}bpp");
        return 0;
    }

    // --blobsheet <rom> <snesHex> <bpp> <out.png> : debug — decompress a blob and sheet it.
    private static int BlobSheet(string[] args, int bi)
    {
        var rom = Rom.Load(args[bi + 1]);
        var data = Gfx.Lz2Decompress(rom.Data, rom.FileOffset(Convert.ToInt32(args[bi + 2], 16)));
        int bpp = int.Parse(args[bi + 3]);
        var pal = Palette.Load(rom, Level.Parse(rom, 0x106).Header);
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
    private static int DiffRoms(string aPath, string bPath)
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
        var aRats = a.EnumerateRats().Select(r => r.PcOffset).ToHashSet();
        var newRats = b.EnumerateRats().Where(r => !aRats.Contains(r.PcOffset)).ToList();
        if (newRats.Count > 0)
        {
            Console.WriteLine($"RATS blocks new in B ({newRats.Count}):");
            foreach (var r in newRats.Take(20))
                Console.WriteLine($"  SNES ${Rom.PcToSnes(r.PcOffset + 8):X6}  PC {r.PcOffset:X6}  size {r.Size}");
        }
        return 0;
    }

    // --exanim <rom> <levelHex> : dump a level's LM ExAnimation slots (CONTRACT §12e).
    private static int DumpExAnim(string romPath, int level)
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

    // --globalexanim <rom> : dump LM's global ExAnimation slots' raw bytes (CONTRACT §12f).
    private static int DumpGlobalExAnim(string romPath)
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

    // --markers <rom> <levelHex> : debug — which object numbers still render as markers.
    private static int DumpMarkers(string[] args, int mi)
    {
        var rom = Rom.Load(args[mi + 1]);
        int level = Convert.ToInt32(args[mi + 2], 16);
        var lv = Level.Parse(rom, level);
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

    // --dumpcell <rom> <levelHex> <cx> <cy> : debug — grid value + Map16 def words for a cell.
    private static int DumpCell(string[] args, int di)
    {
        var rom = Rom.Load(args[di + 1]);
        int level = Convert.ToInt32(args[di + 2], 16);
        int cx = int.Parse(args[di + 3]), cy = int.Parse(args[di + 4]);
        var lv = Level.Parse(rom, level);
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

    // --render <rom> <levelHex> <out.png> [cropTilesW] : compose a level to PNG for inspection.
    private static int RenderLevel(string[] args, int ri)
    {
        string romPath = args.ElementAtOrDefault(ri + 1) ?? @"C:\SMW\Projects\.resources\SMW.smc";
        int level = Convert.ToInt32(args.ElementAtOrDefault(ri + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(ri + 3) ?? "level.png";
        int cropW = int.TryParse(args.ElementAtOrDefault(ri + 4), out var cw) ? cw : 0;

        var rom = Rom.Load(romPath);
        var lv = Level.Parse(rom, level);
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
        Console.WriteLine($"wrote {outPath}: {w}x{h}, level 0x{level:X3}, tileset {lv.Header.Tileset}");
        return 0;
    }

    // --writedm16 <rom> <levelHex> <out> : inject known Direct-Map16 test tiles and save,
    // so the result can be opened in Lunar Magic to verify the encoding round-trips.
    private static int WriteDm16(string[] args, int wi)
    {
        string romPath = args.ElementAtOrDefault(wi + 1) ?? @"C:\SMW\Projects\.resources\after.smc";
        int level = Convert.ToInt32(args.ElementAtOrDefault(wi + 2) ?? "105", 16);
        string outPath = args.ElementAtOrDefault(wi + 3) ?? @"C:\SMW\Projects\.resources\test_dm16.smc";

        var rom = Rom.Load(romPath);
        if (!rom.HasDm16Hijack)
        {
            Console.WriteLine("ERROR: ROM lacks the LM Direct Map16 ASM — open/save it in LM once first.");
            return 1;
        }
        var lv = Level.Parse(rom, level);

        // Two known test placements in empty sky (screen 0): Form A (0x105) and Form B (0x205).
        var added = new[]
        {
            LevelObject.MakeDm16(0x105, screen: 0, xNib: 2, y: 8),
            LevelObject.MakeDm16(0x205, screen: 0, xNib: 5, y: 8),
        };
        var newObjs = added.Concat(lv.Objects).ToList();

        byte[] data = lv.Encode(rom, newObjs);
        if (rom.ActualRomSize < 0x180000) rom.ExpandTo(0x200000);
        int addr = rom.AllocateRats(data);
        rom.SetLayer1Pointer(level, addr);
        rom.SaveAs(outPath);
        Console.WriteLine($"wrote {outPath}: level 0x{level:X3}, added DM16 tiles " +
                          "0x105 @ (2,8) [Form A] and 0x205 @ (5,8) [Form B]; pointer -> $" + addr.ToString("X6"));

        // verify by reloading + re-parsing
        var re = Rom.Load(outPath);
        var rl = Level.Parse(re, level);
        var dm = rl.Objects.Where(o => o.IsDm16 && (o.Dm16Tile == 0x105 || o.Dm16Tile == 0x205)).ToList();
        Console.WriteLine("reload check: " + string.Join(" ", dm.Select(o => $"0x{o.Dm16Tile:X3}@({o.AbsoluteX},{o.Y})")));
        return 0;
    }
}
