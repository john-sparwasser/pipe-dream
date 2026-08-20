namespace PipeDream;

/// <summary>
/// Runnable check for the ROM I/O layer (ponytail self-check). Asserts the LoROM
/// addressing math, then validates against the ROMs on disk when present. Run with:
///   dotnet run -- --selfcheck
/// </summary>
public static class RomSelfCheck
{
    static string CleanRom => ReferenceRoms.Vanilla;
    static string EditedRom => ReferenceRoms.ShaoBase;

    public static int Run()
    {
        int fails = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) fails++;
        }

        Console.WriteLine("LoROM addressing:");
        Check("SnesToPc($008000) == 0x000000", Rom.SnesToPc(0x008000) == 0x000000);
        Check("SnesToPc($018000) == 0x008000", Rom.SnesToPc(0x018000) == 0x008000);
        Check("SnesToPc($058000) == 0x028000", Rom.SnesToPc(0x058000) == 0x028000);
        Check("SnesToPc($05E000) == 0x02E000", Rom.SnesToPc(0x05E000) == 0x02E000);
        Check("PcToSnes round-trips 0x2E000", Rom.SnesToPc(Rom.PcToSnes(0x2E000)) == 0x2E000);

        // Everything below reads the reference ROMs, which are never redistributed. Most
        // sections guard for that themselves, but several load the clean ROM unguarded and
        // took the whole run down with an unhandled exception on a machine that has none —
        // which is every CI runner and every fresh clone. One gate, before any of them.
        if (!File.Exists(CleanRom))
        {
            Console.WriteLine($"(skip) reference ROMs not present under {ReferenceRoms.Root}");
            return fails;
        }

        if (File.Exists(CleanRom))
        {
            Console.WriteLine($"Clean ROM: {CleanRom}");
            var r = Rom.Load(CleanRom);
            Check("copier header detected (0x200)", r.HeaderOffset == 0x200);
            Check("title == 'SUPER MARIOWORLD'", r.Title == "SUPER MARIOWORLD");
            Check("map mode == LoROM (0x20)", r.MapMode == 0x20);
            Check("Layer1Pointer(0) == $068654", r.Layer1Pointer(0) == 0x068654);
            Check("Layer2 level 0 is background (bank $FF)", r.Layer2IsBackground(0));
            Check("SpritePointer(0) == $07C407", r.SpritePointer(0) == 0x07C407);
            Check("clean ROM has no RATS (unexpanded)", !RatsWriter.EnumerateRats(r).Any());
        }
        else Console.WriteLine($"(skip) clean ROM not found: {CleanRom}");

        if (File.Exists(EditedRom))
        {
            Console.WriteLine($"Edited ROM: {EditedRom}");
            var r = Rom.Load(EditedRom);
            var rats = RatsWriter.EnumerateRats(r).ToList();
            Check("map mode == LoROM (0x20)", r.MapMode == 0x20);
            Check("expanded to >= 2MB", r.ActualRomSize >= 0x200000);
            Check("has valid RATS", rats.Count > 0);
            Check("first RAT at pc 0x80000", rats.Count > 0 && rats[0].PcOffset == 0x80000);
            Console.WriteLine($"    ({rats.Count} valid RATS, first protects {rats.FirstOrDefault().Size} bytes)");
        }
        else Console.WriteLine($"(skip) edited ROM not found: {EditedRom}");

        if (File.Exists(CleanRom))
        {
            Console.WriteLine("Level parse (clean ROM, level 0x105 = Yoshi's Island 2):");
            var r = Rom.Load(CleanRom);
            var lv = LevelParser.Parse(r, 0x105);
            var h = lv.Header;
            Console.WriteLine($"    header@${lv.DataPointer:X6}  tileset={h.Tileset} mode={h.LevelMode} " +
                              $"screens={h.Screens} fgPal={h.FgPalette} bgPal={h.BgPalette} music={h.Music}");
            int maxScreen = lv.Objects.Count > 0 ? lv.Objects.Max(o => o.Screen) : 0;
            Console.WriteLine($"    objects parsed: {lv.Objects.Count}  max screen: 0x{maxScreen:X2}");
            Check("level not empty", !lv.Empty);
            Check("parsed at least a few objects", lv.Objects.Count >= 3);
            Check("all object numbers in 0x00-0x3F", lv.Objects.All(o => o.Number is >= 0 and <= 0x3F));
            Check("max screen < 0x20 (no run-past-terminator)", maxScreen < 0x20);
            Console.WriteLine("    first 8 objects (num,screen,x,y,b3):");
            foreach (var o in lv.Objects.Take(8))
                Console.WriteLine($"      {(o.NewScreen ? "*" : " ")} #{o.Number:X2} scr{o.Screen} x{o.XNibble} y{o.Y:X2} b3={o.Byte3:X2}" +
                                  (o.Extended ? $"  [ext {o.ExtendedNumber:X2}]" : $"  {o.Width}x{o.Height}"));
        }

        if (File.Exists(CleanRom))
        {
            Console.WriteLine("Object engine (YI2 → Map16 grid):");
            var r = Rom.Load(CleanRom);
            var lv = LevelParser.Parse(r, 0x105);
            var grid = ObjectEngine.Render(r, lv);
            int placed = grid.PlacedCount();
            int markers = grid.Tiles.Count(t => t != Map16Grid.Empty && (t & ObjectEngine.Marker) != 0);
            int real = placed - markers;
            Console.WriteLine($"    grid {grid.Width}x{grid.Height}  placed {placed} cells  " +
                              $"(real {real}, unimplemented-markers {markers})");
            Check("grid has tiles placed", placed > 0);
            Check("rectangle/single families produced real tiles", real > 0);
            // Which object numbers dominate YI2 → prioritize the next handlers to port.
            var hist = lv.Objects.Where(o => !o.Extended && !o.IsScreenExit)
                                 .GroupBy(o => o.Number)
                                 .OrderByDescending(gr => gr.Count());
            var impl = new HashSet<int>(Enumerable.Range(1, 0x0E))
                { 0x0F, 0x12, 0x13, 0x14, 0x15, 0x17, 0x1F, 0x21, 0x39, 0x3A, 0x3F };
            var stillMarker = lv.Objects.Where(o => !o.Extended && !o.IsScreenExit && !impl.Contains(o.Number))
                                        .Select(o => o.Number).Distinct().OrderBy(x => x);
            var extMarkers = lv.Objects.Where(o => o.Extended && !o.IsScreenExit &&
                                                   !(o.ExtendedNumber is >= 0x10 and < 0x43))
                                       .Select(o => o.ExtendedNumber).Distinct().OrderBy(x => x);
            Console.WriteLine($"    still-marker standard objs: [{string.Join(",", stillMarker.Select(x => $"{x:X2}"))}]  " +
                              $"ext objs: [{string.Join(",", extMarkers.Select(x => $"{x:X2}"))}]");
            Console.WriteLine("    standard-object histogram (num × count):");
            foreach (var gr in hist)
                Console.WriteLine($"      obj {gr.Key:X2} × {gr.Count()}" +
                                  (impl.Contains(gr.Key) ? "  [impl]" : "  [TODO handler]"));
        }

        if (File.Exists(CleanRom))
        {
            Console.WriteLine("Owner attribution + resize probe (tracked render):");
            var r = Rom.Load(CleanRom);
            var lv = LevelParser.Parse(r, 0x105);
            // Find a plain rect-fill object in this tileset via the probe itself:
            // 1x1 at byte3=0x00 and exactly 2 wide x 3 tall at byte3=0x21.
            int rectNum = 0;
            for (int n = 1; n <= 0x3F && rectNum == 0; n++)
                if (ObjectEngine.ProbeResize(r, lv, n) is { W: ObjectEngine.SizeSrc.Lo, H: ObjectEngine.SizeSrc.Hi } &&
                    ObjectEngine.SoloBBox(r, lv, n, 0x00) == (1, 1) &&
                    ObjectEngine.SoloBBox(r, lv, n, 0x21) == (2, 3))
                    rectNum = n;
            Check("probe finds a rect-family object", rectNum != 0);
            if (rectNum != 0)
            {
                Console.WriteLine($"    using obj {rectNum:X2}: two objects on different screens (jump inserted)");
                var objs = new List<LevelObject>
                {
                    new(false, rectNum, 0, 4, 10, 0x21, -1),   // 2 wide x 3 tall at (4,10)
                    new(false, rectNum, 1, 2, 5, 0x13, -1),    // 4 wide x 2 tall at (18,5)
                    new(false, rectNum, 0, 4, 10, 0x11, -1),   // 2x2 at (4,10): covers obj 1's top
                };
                var prov = new List<int>();
                var norm = LevelEncoder.NormalizeStream(objs, prov);
                var offs = new List<int>();
                byte[] enc = LevelEncoder.Encode(lv, norm, offs);
                var so = new ushort[enc.Length];
                for (int i = 0; i < norm.Count; i++)
                {
                    if (prov[i] < 0) continue;
                    int end = i + 1 < norm.Count ? offs[i + 1] : enc.Length - 1;
                    for (int b = offs[i]; b < end; b++) so[b] = (ushort)(prov[i] + 1);
                }
                ObjectEngine.RenderEmulatedStream(r, lv.Header, enc, 0, so, out var owners, out var stacks);
                (int x0, int y0, int x1, int y1)? BBox(int id)
                {
                    (int x0, int y0, int x1, int y1)? bb = null;
                    for (int y = 0; y < owners!.Height; y++)
                        for (int x = 0; x < owners.Width; x++)
                            if (owners.Get(x, y) == id)
                                bb = bb is { } e ? (Math.Min(e.x0, x), Math.Min(e.y0, y), Math.Max(e.x1, x), Math.Max(e.y1, y))
                                                 : (x, y, x, y);
                    return bb;
                }
                Check("owner grid produced", owners is not null);
                // Obj 3 (later in stream) covers obj 1's top 2x2 — z-order is stream order.
                Check("obj 1 visibly owns only its uncovered row (4,12)-(5,12)", BBox(1) == (4, 12, 5, 12));
                Check("obj 2 owns exactly its 4x2 rect at (18,5)", BBox(2) == (18, 5, 21, 6));
                Check("obj 3 owns the 2x2 it covers at (4,10)", BBox(3) == (4, 10, 5, 11));
                Check("no stray owner ids", owners!.Tiles.All(t => t is 0 or 1 or 2 or 3 || t == Map16Grid.Empty));
                // Full writer stacks: covered cells remember every writer, bottom→top.
                Check("stacks produced", stacks is not null);
                Check("covered cell (4,10) stack is [1,3]",
                      stacks!.TryGetValue(10 * owners.Width + 4, out var s1) && s1.SequenceEqual(new ushort[] { 1, 3 }));
                Check("uncovered cell (4,12) stack is [1]",
                      stacks.TryGetValue(12 * owners.Width + 4, out var s2) && s2.SequenceEqual(new ushort[] { 1 }));
                // Full extent of buried obj 1 from stacks (what selection/handles use).
                (int x0, int y0, int x1, int y1)? full = null;
                foreach (var (cell, ids) in stacks)
                    if (ids.Contains((ushort)1))
                    {
                        int x = cell % owners.Width, y = cell / owners.Width;
                        full = full is { } e ? (Math.Min(e.x0, x), Math.Min(e.y0, y), Math.Max(e.x1, x), Math.Max(e.y1, y))
                                             : (x, y, x, y);
                    }
                Check("obj 1 full extent from stacks is still 2x3 at (4,10)", full == (4, 10, 5, 12));
            }
            Console.WriteLine("DM16 brush -> objects (FromBrush):");
            const ushort E = Map16Grid.Empty;
            var fa = Dm16Saver.FromBrush(new ushort[] { 5, 5, 5, 5, 5, 5 }, 3, 2, 4, 10, false);
            Check("uniform 3x2 -> one 3x2 object",
                  fa.Count == 1 && fa[0].Width == 3 && fa[0].Height == 2 &&
                  fa[0].Dm16Tile == 5 && fa[0].AbsoluteX == 4 && fa[0].Y == 10);
            var fb = Dm16Saver.FromBrush(new ushort[] { 1, 1, 2 }, 3, 1, 0, 0, false);
            Check("mixed row -> two runs (2-wide + 1-wide)",
                  fb.Count == 2 && fb.Sum(o => o.Width) == 3);
            var fc = Dm16Saver.FromBrush(new ushort[] { 1, E, 1 }, 3, 1, 0, 0, false);
            Check("empty cells skipped (no erase)", fc.Count == 2 && fc.All(o => o.Width == 1));
            var fd = Dm16Saver.FromBrush(Enumerable.Repeat((ushort)7, 20).ToArray(), 20, 1, 0, 0, false);
            Check("20-wide run -> one extended Form B object (128-wide cap)",
                  fd.Count == 1 && fd[0].Dm16Size() == (20, 1));
            var fe = Dm16Saver.FromBrush(new ushort[] { 9 }, 1, 1, 17, 20, true);
            Check("vertical mapping: (17,20) -> screen 1, Y bit4 = right half",
                  fe.Count == 1 && fe[0].Screen == 1 && fe[0].Y == 0x14 && fe[0].XNibble == 1);

            // Informational: how the probe classifies every object in this tileset.
            var byKind = Enumerable.Range(1, 0x3F)
                .Select(n => (n, rz: ObjectEngine.ProbeResize(r, lv, n)))
                .GroupBy(t => t.rz).OrderByDescending(g => g.Count());
            foreach (var g in byKind)
                Console.WriteLine($"    W={g.Key.W} H={g.Key.H}: " +
                    string.Join(",", g.Select(t => $"{t.n:X2}")));
        }

        if (File.Exists(EditedRom))
        {
            var r = Rom.Load(EditedRom);
            var lv = LevelParser.Parse(r, 0x105);
            Console.WriteLine("LM ROM: emulated stream render (captured plane tables) + DM16 size:");
            (int x0, int y0, int x1, int y1)? SoloBBoxDm16(int w, int h)
            {
                var one = new List<LevelObject> { LevelObject.MakeDm16(0x105, 0, 4, 10, w, h) };
                var offs = new List<int>();
                byte[] enc = LevelEncoder.Encode(lv, one, offs);
                var so = new ushort[enc.Length];
                for (int b = offs[0]; b < enc.Length - 1; b++) so[b] = 1;
                ObjectEngine.RenderEmulatedStream(r, lv.Header, enc, 0, so, out var owners, out _);
                (int x0, int y0, int x1, int y1)? bb = null;
                for (int y = 0; y < owners!.Height; y++)
                    for (int x = 0; x < owners.Width; x++)
                        if (owners.Get(x, y) == 1)
                            bb = bb is { } e ? (Math.Min(e.x0, x), Math.Min(e.y0, y), Math.Max(e.x1, x), Math.Max(e.y1, y))
                                             : (x, y, x, y);
                return bb;
            }
            try
            {
                Check("DM16 1x1 renders 1x1 at (4,10)", SoloBBoxDm16(1, 1) == (4, 10, 4, 10));
                Check("DM16 3x2 renders 3x2 (resize via byte3 nibbles works)", SoloBBoxDm16(3, 2) == (4, 10, 6, 11));

                // Screen-boundary crossing needs LM's $13D7 stride seeded (LM patches the
                // step primitives to read it); regression for the wrap bug.
                List<(int x, int y)> Cells(LevelObject o)
                {
                    var offs = new List<int>();
                    byte[] enc = LevelEncoder.Encode(lv, new List<LevelObject> { o }, offs);
                    var so = new ushort[enc.Length];
                    for (int b = offs[0]; b < enc.Length - 1; b++) so[b] = 1;
                    ObjectEngine.RenderEmulatedStream(r, lv.Header, enc, 0, so, out var owners, out _);
                    var cells = new List<(int, int)>();
                    for (int y = 0; y < owners!.Height; y++)
                        for (int x = 0; x < owners.Width; x++)
                            if (owners.Get(x, y) == 1) cells.Add((x, y));
                    return cells;
                }
                var expect = new List<(int x, int y)> { (14, 10), (15, 10), (16, 10), (17, 10) };
                Check("DM16 4w at x=14 crosses the screen boundary (no wrap)",
                      Cells(LevelObject.MakeDm16(0x105, 0, 14, 10, 4, 1)).SequenceEqual(expect));
                Check("std rect 4w at x=14 crosses the screen boundary (no wrap)",
                      Cells(new LevelObject(false, 1, 0, 14, 10, 0x03, -1)).SequenceEqual(expect));

                // Extended DM16 Form B (page bits 6+7): width = (byte3 & 0x7F)+1 up to 128,
                // height = ExtH+1 — the size a "tile object" can really reach (probed).
                (int w, int h, int n) BB(LevelObject o)
                {
                    var cl = Cells(o);
                    if (cl.Count == 0) return (0, 0, 0);
                    return (cl.Max(t => t.x) - cl.Min(t => t.x) + 1,
                            cl.Max(t => t.y) - cl.Min(t => t.y) + 1, cl.Count);
                }
                Check("Form B C0: 20x8 renders 20x8",
                      BB(new LevelObject(false, 0x27, 0, 2, 5, 0x13, -1, 0x105, 0xC1, 0x00, 0x07)) == (20, 8, 160));
                Check("Form B C0: 128-wide renders 128x1",
                      BB(new LevelObject(false, 0x27, 0, 2, 5, 0x7F, -1, 0x105, 0xC1, 0x00, 0x00)) == (128, 1, 128));
                Check("MakeDm16 40x5 renders 40x5 (auto extended form)",
                      BB(LevelObject.MakeDm16(0x105, 0, 2, 5, 40, 5)) == (40, 5, 200));
                // Resize round-trip: 3x2 nibble form -> 40x5 extended -> encode -> parse.
                var grown = LevelObject.MakeDm16(0x105, 0, 2, 5, 3, 2).Dm16Resized(40, 5);
                var ps = LevelParser.ParseEncoded(r, LevelEncoder.Encode(lv, new List<LevelObject> { grown }));
                Check("Dm16Resized 40x5 round-trips through encode/parse",
                      ps.Count == 1 && ps[0].IsDm16 && ps[0].Dm16Tile == 0x105 && ps[0].Dm16Size() == (40, 5));
                Check("Dm16Resized back to 4x3 returns to nibble form",
                      grown.Dm16Resized(4, 3) is { Byte3: 0x23, Dm16ExtH: -1 } sm && sm.Dm16Size() == (4, 3));
            }
            catch (Exception e) { Check("emulated render on LM ROM (" + e.Message + ")", false); }
        }
        else Console.WriteLine($"(skip) edited ROM not found: {EditedRom}");

        if (File.Exists(CleanRom))
        {
            Console.WriteLine("GFX LC_LZ2 decompression (clean ROM):");
            var r = Rom.Load(CleanRom);
            int ok = 0, bad = 0; long totalBytes = 0;
            var sizes = new List<int>();
            for (int f = 0; f < Gfx.Count; f++)
            {
                try { var d = Gfx.DecompressFile(r, f); sizes.Add(d.Length); totalBytes += d.Length; ok++; }
                catch { bad++; }
            }
            Console.WriteLine($"    {ok}/{Gfx.Count} files decompressed, {bad} failed; " +
                              $"total {totalBytes} bytes; sizes seen: {string.Join(",", sizes.Distinct().OrderBy(x => x))}");
            // GFX00 sits at $08D9F9; SNES-3bpp GFX decompress to 0x600 (3bpp) or 0x1000 bytes.
            Check("all GFX files decompress without error", bad == 0);
            Check("GFX00 decompresses to a sane size (0x200-0x2000)",
                  sizes.Count > 0 && sizes[0] is >= 0x200 and <= 0x2000);
            // Depth is ROM-wide, probed from a full base file — vanilla stores 3bpp.
            Check("clean ROM GFX depth is 3bpp", Gfx.RomBpp(r) == 3);
        }

        if (File.Exists(CleanRom))
        {
            var r = Rom.Load(CleanRom);
            Console.WriteLine("Tile decode (GFX00, 3bpp):");
            var g0 = Gfx.DecompressFile(r, 0);
            var tile = Gfx.DecodeTile(g0, 0, 3);
            Check("tile is 64 px", tile.Length == 64);
            Check("3bpp indices in 0-7", tile.All(v => v <= 7));
            Check("tile has >1 distinct color (not blank)", tile.Distinct().Count() > 1);

            Console.WriteLine("Palette (YI2):");
            var lv = LevelParser.Parse(r, 0x105);
            var pal = Palette.Load(r, lv.Header);
            int nonzero = pal.Bgr.Count(c => c != 0);
            Console.WriteLine($"    backdrop=0x{pal.Bgr[0]:X4} rgba=0x{pal.Rgba[0]:X8}; {nonzero}/256 colors set");
            Console.WriteLine("    FG palette 2 (colors 0-7): " +
                string.Join(" ", Enumerable.Range(0x20, 8).Select(i => $"{pal.Bgr[i]:X4}")));
            Check("palette has many colors set", nonzero > 40);
            Check("all RGBA fully opaque", pal.Rgba.All(c => (c >> 24) == 0xFF));

            Console.WriteLine("Tile sheet render (GFX00, 3bpp, pal row 2):");
            var (px, w, h) = Gfx.TileSheet(g0, 3, pal, 2);
            int lit = px.Count(p => p != 0xFF303030u);
            Console.WriteLine($"    sheet {w}x{h}, {lit} non-background pixels");
            Check("tile sheet has expected size (128 wide)", w == 128 && h > 0);
            Check("tile sheet has colored pixels", lit > 100);

            Console.WriteLine("Map16 definitions (YI2 tileset):");
            var defPtr = Map16.BuildDefPointers(r, lv.Header.Tileset);
            var allWords = new List<int>();
            for (int t = 0; t < Map16.FgTiles; t++)
                foreach (var wd in Map16.Definition(r, defPtr, t)) allWords.Add(wd.Tile);
            int distinct = allWords.Distinct().Count();
            var sample = Map16.Definition(r, defPtr, 0x100);
            Console.WriteLine($"    tile 0x100 words: " +
                string.Join(" ", sample.Select(x => $"{x.Raw:X4}(t{x.Tile:X3}p{x.Palette})")));
            Console.WriteLine($"    {distinct} distinct 8x8 tile numbers referenced across 512 Map16 tiles");
            Check("Map16 words reference sane 8x8 tiles (<0x400)", allWords.All(t => t < 0x400));
            Check("Map16 defs have variety (not blank)", distinct > 50);

            Console.WriteLine("Map16 composition (real pixels):");
            var (sheet, sw, sh) = Map16.ComposeSheet(r, lv.Header);
            int colored = sheet.Count(p => p != 0xFF303030u);
            int distinctColors = sheet.Where(p => p != 0xFF303030u).Distinct().Count();
            Console.WriteLine($"    sheet {sw}x{sh}, {colored} colored px, {distinctColors} distinct colors");
            Check("composed sheet has real pixels", colored > 5000);
            Check("composed sheet uses many palette colors", distinctColors > 8);

            Console.WriteLine("Object-stream re-encode (round-trip):");
            int mism = 0, tested = 0;
            foreach (int ln in new[] { 0x105, 0x106, 0x101, 0x102, 0x104, 0x1C0 })
            {
                var l = LevelParser.Parse(r, ln);
                if (l.Empty) continue;
                tested++;
                byte[] enc = LevelEncoder.Encode(l);
                int fo = r.FileOffset(l.DataPointer);
                var orig = r.Data.AsSpan(fo, enc.Length).ToArray();
                if (!enc.AsSpan().SequenceEqual(orig)) { mism++; Console.WriteLine($"    level 0x{ln:X3}: MISMATCH ({enc.Length} bytes)"); }
            }
            Console.WriteLine($"    round-tripped {tested} levels, {mism} mismatches");
            Check("object stream re-encodes byte-identical", mism == 0 && tested > 0);

            Console.WriteLine("Save path (expand + RATS + repoint + reload):");
            var wr = Rom.Load(CleanRom);             // fresh copy to mutate
            var yi2 = LevelParser.Parse(wr, 0x105);
            int origCount = yi2.Objects.Count;
            wr.ExpandTo(0x200000);                   // expand to 2MB
            int newAddr = RatsWriter.Allocate(wr, LevelEncoder.Encode(yi2));
            wr.SetLayer1Pointer(0x105, newAddr);
            string tmp = Path.Combine(Path.GetTempPath(), "pd_save_test.smc");
            RatsWriter.SaveAs(wr, tmp);
            var re = Rom.Load(tmp);
            Check("saved pointer relocated to expanded space", re.Layer1Pointer(0x105) >= 0x080000);
            var yi2b = LevelParser.Parse(re, 0x105);
            Console.WriteLine($"    reloaded: ptr ${re.Layer1Pointer(0x105):X6}, {yi2b.Objects.Count} objects (was {origCount})");
            Check("reloaded level has same object count", yi2b.Objects.Count == origCount);
            Check("reloaded RATS tag is valid", RatsWriter.EnumerateRats(re).Any());
            // checksum: SaveAs fixed it; verify chk + complement == 0xFFFF and chk matches a resum
            long resum = 0; int rh = re.HeaderOffset, rsz = re.ActualRomSize;
            for (int i = 0; i < rsz; i++) resum += re.Data[rh + i];
            Check("saved ROM checksum + complement == 0xFFFF", (re.Checksum ^ re.ChecksumComplement) == 0xFFFF);
            Check("saved ROM checksum matches byte sum", re.Checksum == (int)(resum & 0xFFFF));
            File.Delete(tmp);
        }

        {
            Console.WriteLine("Layer 2 (CONTRACT §10):");
            var r2 = Rom.Load(CleanRom);
            Check("YI2 layer 2 is a background image", r2.Layer2IsBackground(0x105));
            var bg = LevelParser.DecodeBgImage(r2, 0x105);
            Check("BG image decodes (0x400 tiles, variety)",
                  bg is not null && bg.Distinct().Count() > 8);
            var l2 = LevelParser.ParseLayer2(r2, 0x105);
            Check("BG-image level has no layer-2 objects", l2 is null);
        }

        {
            Console.WriteLine("Sprites (CONTRACT §11):");
            var rs = Rom.Load(CleanRom);
            var sd = SpriteData.Parse(rs, 0x105);
            Console.WriteLine($"    YI2: {sd.Sprites.Count} sprites, memory {sd.SpriteMemory}, buoyancy {sd.Buoyancy}");
            Check("YI2 has a sane sprite count", sd.Sprites.Count is > 10 and < 120);
            Check("all sprite screens/Y in range",
                  sd.Sprites.All(s => s.Screen < 0x20 && s.Y < 0x20));
            int sfo = rs.FileOffset(rs.SpritePointer(0x105));
            byte[] senc = sd.Encode();
            Check("sprite data re-encodes byte-identical",
                  senc.AsSpan().SequenceEqual(rs.Data.AsSpan(sfo, senc.Length)));

            string dow = ReferenceRoms.InProject("DogsOfWar", "dogs_of_war-backup.smc");
            if (File.Exists(dow))
            {
                var dr = Rom.Load(dow);
                Check("DoW sprite size table located", dr.LmSpriteSizeBase > 0);
                var ds = SpriteData.Parse(dr, 0x101);
                Console.WriteLine($"    DoW 101: {ds.Sprites.Count} sprites, " +
                                  $"{ds.Sprites.Count(s => s.ExtraBytes is not null)} with extra bytes");
                Check("DoW 101 sprites parse in range (extra-byte sizes honored)",
                      ds.Sprites.Count is > 0 and < 400 &&
                      ds.Sprites.Count(s => s.IsScrollCommand) < ds.Sprites.Count / 4);
                int dfo = dr.FileOffset(dr.SpritePointer(0x101));
                byte[] denc = ds.Encode();
                Check("DoW sprite data re-encodes byte-identical",
                      denc.AsSpan().SequenceEqual(dr.Data.AsSpan(dfo, denc.Length)));
            }
        }

        string map16After = ReferenceRoms.Resource("map16_after.smc");
        if (File.Exists(map16After))
        {
            Console.WriteLine("LM extended Map16 def read (map16_after.smc, tile 0x300):");
            var mr = Rom.Load(map16After);
            Console.WriteLine($"    LmMap16Base = ${mr.LmMap16Base:X6}");
            Check("LM extended Map16 table detected", mr.LmMap16Base > 0);
            var d = Map16.LmExtendedDef(mr, 0x300);
            Console.WriteLine($"    tile 0x300 = TL {d[0].Raw:X4} BL {d[1].Raw:X4} TR {d[2].Raw:X4} BR {d[3].Raw:X4}");
            Check("tile 0x300 def matches the edit (DA/DB/DC/DD, pal 0/1/2/3)",
                  d[0].Raw == 0x00DA && d[1].Raw == 0x08DC && d[2].Raw == 0x04DB && d[3].Raw == 0x0CDD);
            Check("corner tiles/palettes decode right",
                  d[0].Tile == 0xDA && d[0].Palette == 0 && d[2].Tile == 0xDB && d[2].Palette == 1 &&
                  d[1].Tile == 0xDC && d[1].Palette == 2 && d[3].Tile == 0xDD && d[3].Palette == 3);

            // Page-1 tileset-specific tile 0x166 (same edit, tileset 7) via the vanilla reader.
            var defPtr = Map16.BuildDefPointers(mr, 7);
            var d166 = Map16.Definition(mr, defPtr, 0x166);
            Console.WriteLine($"    tile 0x166 (vanilla path) = TL {d166[0].Raw:X4} BL {d166[1].Raw:X4} " +
                              $"TR {d166[2].Raw:X4} BR {d166[3].Raw:X4};  acts-as = 0x{mr.ActsAs(0x166):X3}");
            Check("page-1 tile 0x166 def matches the edit (in vanilla bank-0D table)",
                  d166[0].Raw == 0x00DA && d166[1].Raw == 0x08DC && d166[2].Raw == 0x04DB && d166[3].Raw == 0x0CDD);
            Check("acts-as table: 0x166 acts as 0x130", mr.ActsAs(0x166) == 0x130);
        }

        string gfxAfter = ReferenceRoms.Resource("gfx_after.smc");
        if (File.Exists(gfxAfter))
        {
            Console.WriteLine("LM Super GFX Bypass read (gfx_after.smc, level 0x105, CONTRACT §7d):");
            var gr = Rom.Load(gfxAfter);
            Console.WriteLine($"    bypassBase = ${gr.LmGfxBypassBase:X6}  exGfxBase = ${gr.LmExGfxBase:X6}  actsAsBase = ${gr.LmActsAsBase:X6}");
            Check("bypass table located by signature", gr.LmGfxBypassBase == 0x10AD08);
            Check("ExGFX 0x100+ table located by signature", gr.LmExGfxBase == 0x108008);
            Check("acts-like table located by signature", gr.LmActsAsBase == 0x118000);
            var rec = gr.LmGfxBypass(0x105);
            Check("level 0x105 bypass record enabled", rec is not null);
            if (rec is not null)
            {
                Console.WriteLine($"    FG1={rec[7] & 0xFFF:X2} FG2={rec[6] & 0xFFF:X2} BG1={rec[5] & 0xFFF:X2} FG3={rec[4] & 0xFFF:X2} " +
                                  $"BG2={rec[3] & 0xFFF:X2} BG3={rec[2] & 0xFFF:X2} SP1={rec[11] & 0xFFF:X2} AN2={rec[0] & 0xFFF:X2}");
                Check("record slots match the LM edit (FG1=12 FG2=1A BG1=33 FG3=05)",
                      (rec[7] & 0xFFF) == 0x12 && (rec[6] & 0xFFF) == 0x1A &&
                      (rec[5] & 0xFFF) == 0x33 && (rec[4] & 0xFFF) == 0x05);
                Check("sprite/AN2 slots match (SP1=30 SP2=1F SP3=0C SP4=25 AN2=08 BG2=21 BG3=08)",
                      (rec[11] & 0xFFF) == 0x30 && (rec[10] & 0xFFF) == 0x1F && (rec[9] & 0xFFF) == 0x0C &&
                      (rec[8] & 0xFFF) == 0x25 && (rec[0] & 0xFFF) == 0x08 &&
                      (rec[3] & 0xFFF) == 0x21 && (rec[2] & 0xFFF) == 0x08);
            }
            // Renderer honors the bypass: FG tiles for level 0x105 must differ from tileset default.
            var lvh = LevelParser.Parse(gr, 0x105).Header;
            var defTiles = Gfx.FgTiles.Load(gr, lvh.Tileset);
            var bypTiles = Gfx.FgTiles.Load(gr, lvh.Tileset, 0x105);
            bool differs = Enumerable.Range(0, 0x200).Any(t => !defTiles.Fetch(t).SequenceEqual(bypTiles.Fetch(t)));
            Check("FgTiles.Load(level) applies the bypass (tiles differ from default)", differs);
        }

        string shaoRom = ReferenceRoms.ShaoBase;
        if (File.Exists(shaoRom))
        {
            Console.WriteLine("LM extended defs via $06F540 constants (ShaoBase, CONTRACT §7a-rev):");
            var sh = Rom.Load(shaoRom);
            var (imm, bank) = sh.LmMap16Defs;
            Console.WriteLine($"    defs = ${bank:X2}:{imm:X4} + tile*8, tileCount = 0x{sh.Map16TileCount:X}");
            Check("ShaoBase extended def region found ($158274)", bank == 0x15 && imm == 0x8274);
            var d279 = Map16.LmExtendedDef(sh, 0x279);
            Check("tile 0x279 def is the real ground block (not FF filler)",
                  d279[0].Raw == 0x1206 && d279[1].Raw == 0x1216 && d279[2].Raw == 0x1207 && d279[3].Raw == 0x1217);
            // The def region is a RATS block; the count must stop at its end (the next
            // block's STAR tag sits at tile 0x436's slot in this ROM) — not the bank end.
            Check("Map16TileCount bounded by the defs RATS block (0x436)", sh.Map16TileCount == 0x436);

            {   // Object-catalog solo sweep (Objects tab): DM16 numbers are skipped by the
                // editor (a bare 3-byte record makes the DM16 handlers run away — they
                // expect tile bytes) and everything else must finish inside the solo
                // budget, fast. Regression for the 17-second tab freeze.
                var lv105 = LevelParser.Parse(sh, 0x105);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int oFails = 0;
                for (int num = 1; num <= 0x3F; num++)
                {
                    if (num is 0x22 or 0x23 or 0x26 or 0x27 or 0x28 or 0x29) continue;
                    var one = new List<LevelObject> { new(false, num, 0, 4, 10, 0x22, -1) };
                    try { ObjectEngine.RenderEmulatedStream(sh, lv105.Header, LevelEncoder.Encode(lv105, one), 0, ObjectEngine.SoloBudget); }
                    catch { oFails++; }
                }
                Console.WriteLine($"    catalog sweep: {sw.ElapsedMilliseconds}ms, {oFails} failing handler(s)");
                Check("catalog sweep: at most the known-bad handler fails (0x2D)", oFails <= 1);
                Check("catalog sweep completes fast (< 3s)", sw.ElapsedMilliseconds < 3000);
            }

            Console.WriteLine("LM-free Map16 page allocation (in-memory only):");
            int szBefore = sh.ActualRomSize;
            Check("allocation through page 5 succeeds", sh.EnsureMap16Tiles(0x600) is null);
            Check("count grew to 0x600", sh.Map16TileCount == 0x600);
            var (imm2, bank2) = sh.LmMap16Defs;
            Check("lookup slot repatched to the fresh bank (imm $7008)",
                  imm2 == 0x7008 && ((bank2 << 16) | 0x8000) == Rom.PcToSnes(szBefore) + 0);
            var d279b = Map16.LmExtendedDef(sh, 0x279);
            Check("existing defs copied (tile 0x279 still the ground block)",
                  d279b[0].Raw == 0x1206 && d279b[1].Raw == 0x1216 && d279b[2].Raw == 0x1207 && d279b[3].Raw == 0x1217);
            Check("new tiles are LM's default-empty def (0x1004 x4)",
                  Map16.LmExtendedDef(sh, 0x5FF).All(w => w.Raw == 0x1004));
            Check("re-allocating a covered page is a no-op",
                  sh.EnsureMap16Tiles(0x500) is null && sh.Map16TileCount == 0x600 && sh.ActualRomSize == szBefore + 0x8000);
            // Tile-def editing: DefFileOffset is the write target (raw word order TL,BL,TR,BR).
            int efo = Map16.DefFileOffset(sh, 0, 0x5FF);
            Check("DefFileOffset lands in the new region", efo > 0);
            sh.Data[efo + 2 * 2] = 0xAB; sh.Data[efo + 2 * 2 + 1] = 0x12;   // TR word = 0x12AB
            Check("edited TR quadrant reads back (0x12AB)", Map16.LmExtendedDef(sh, 0x5FF)[2].Raw == 0x12AB);
            Check("BG def write target = fixed $0D9100 table",
                  Map16.DefFileOffset(sh, 0, 0x4025) == sh.FileOffset(0x0D9100 + 0x25 * 8));

            Console.WriteLine("LM global ExAnimation list (ShaoBase, CONTRACT §12f):");
            Check("global list located by engine signature", sh.LmGlobalExAnimPtr == 0x10F331);
            var gslots = ExAnimation.ReadGlobalRaw(sh);
            Console.WriteLine($"    {gslots.Count} used slots; sizes [{string.Join(",", gslots.Select(s => s.Raw.Length))}]");
            Check("13 used slots (RATS-bounded, no bleed into next block)", gslots.Count == 13);
            Check("all slots are 9 or 13 bytes (7B header + 1 or 3 frame words)",
                  gslots.All(s => s.Raw.Length is 9 or 13));
            var s6 = gslots.FirstOrDefault(s => s.Index == 6);
            Check("slot 6 = 3 frames 0x680/0x700/0x780 (last → $AD00 custom source)",
                  s6.FrameCount == 3 && s6.FrameTile(0) == 0x680 && s6.FrameTile(1) == 0x700 &&
                  s6.FrameTile(2) == 0x780 && s6.FrameSrcAddr(2) == 0xAD00);
            Check("clean ROM has no global ExAnimation list", Rom.Load(CleanRom).LmGlobalExAnimPtr == -1);

            Console.WriteLine("LM global ExAnimation resolved by emulation (CONTRACT §12f):");
            Check("setup + processor entries located", sh.LmExAnimSetupEntry == 0x138002 && sh.LmExAnimProcEntry == 0x1384B0);
            var gf = ExAnimation.ResolveGlobal(sh, 32);
            var updates = gf.Where(f => f.Ctrl != 0).ToList();
            Console.WriteLine($"    {updates.Count} tile updates over 32 frames; " +
                              $"dest tiles [{string.Join(",", updates.Select(u => u.DestTile).Distinct().Order().Select(t => $"{t:X2}"))}]");
            Check("engine emits tile updates (non-empty timeline)", updates.Count > 0);
            Check("resolved sources are ROM GFX addresses (bank >= $10)",
                  updates.All(u => (u.SrcSnes >> 16) >= 0x10) && updates.Any(u => u.SrcSnes != 0));
            Check("dest tiles are valid FG 8x8 indices (< 0x200)",
                  updates.All(u => u.DestTile is >= 0 and < 0x200));

            // Overlay applies + animates: some resolved dest tile's decoded pixels differ across
            // phases (not every tile moves each phase, so scan all of them).
            var states = ExAnimation.GlobalStates(sh);
            Check("4 phase snapshots, each covering the animated tiles", states.Length == 4 && states.All(s => s.Count > 0));
            int ts = LevelParser.Parse(sh, 0x106).Header.Tileset;
            var fgA = Gfx.FgTiles.Load(sh, ts, 0x106, 0);
            var fgB = Gfx.FgTiles.Load(sh, ts, 0x106, 2);
            var animTiles = states[0].Keys.Union(states[2].Keys).Distinct();
            Check("some animated tile changes between phase 0 and 2 (overlay applied + animating)",
                  animTiles.Any(t => !fgA.Fetch(t).AsSpan().SequenceEqual(fgB.Fetch(t))));
        }

        string dowRom = ReferenceRoms.InProject("DogsOfWar", "dogs_of_war-backup.smc");
        if (File.Exists(dowRom))
        {
            Console.WriteLine("LM custom palettes (DogsOfWar, CONTRACT §7e):");
            var dr = Rom.Load(dowRom);
            // This hack re-normalized its graphics to 4bpp; partial ExGFX rely on the
            // ROM-wide depth probe (not a per-file size guess) to decode at the right depth.
            Check("LM 4bpp ROM: GFX depth probes as 4bpp", Gfx.RomBpp(dr) == 4);
            Check("palette hook detected (JML at $0095E9)", dr.HasLmPaletteHook);
            var cp = dr.LmCustomPalette(0x107);
            Check("level 0x107 has a custom palette", cp is not null);
            Check("level 0x105 has none (vanilla path)", dr.LmCustomPalette(0x105) is null);
            if (cp is (var back, var colors))
            {
                Console.WriteLine($"    back=${back:X4} c1=${colors[1]:X4} c0x21=${colors[0x21]:X4}");
                Check("row color-0 slots stored as 0", Enumerable.Range(0, 16).All(r => colors[r * 16] == 0));
                Check("palette has real colors", colors.Count(c => c != 0) > 64);
                var lp = Palette.Load(dr, LevelParser.Parse(dr, 0x107).Header, 0x107);
                Check("Palette.Load(level) uses the custom palette", lp.Bgr[0] == back && lp.Bgr[1] == colors[1]);
            }
            // vanilla ROM guard: $0EF600 holds unrelated data there, hook check must gate it
            var vr = Rom.Load(CleanRom);
            Check("clean ROM: no palette hook, no custom palettes",
                  !vr.HasLmPaletteHook && vr.LmCustomPalette(0x107) is null);

            // Write round-trip: new blob (0x105 had none) + in-place overwrite (0x107 had one).
            var wc = new ushort[256];
            for (int i = 0; i < 256; i++) wc[i] = (ushort)i;
            int ptr107Before = dr.ReadValue(LunarMagic.LmPaletteTable + 0x107 * 3, 3);
            dr.WriteLmCustomPalette(0x105, 0x1234, wc);
            dr.WriteLmCustomPalette(0x107, 0x4321, wc);
            var w5 = dr.LmCustomPalette(0x105);
            var w7 = dr.LmCustomPalette(0x107);
            Check("written palette reads back (new RATS blob)",
                  w5 is (0x1234, var c5) && c5[1] == 1 && c5[0x11] == 0x11 && c5[0x10] == 0);
            Check("written palette reads back (in-place overwrite)",
                  w7 is (0x4321, var c7) && c7[0xFF] == 0xFF &&
                  dr.ReadValue(LunarMagic.LmPaletteTable + 0x107 * 3, 3) == ptr107Before);
        }

        string juzRom = ReferenceRoms.InProject("juz", "SMW.smc");
        if (File.Exists(juzRom))
        {
            // Regression: these tables move per-ROM; juz's ExGFX table sits where other ROMs
            // keep acts-like ($118000), which the old hardcoded reader misread.
            var jr = Rom.Load(juzRom);
            Console.WriteLine($"LM table bases in juz: bypass=${jr.LmGfxBypassBase:X6} exGfx=${jr.LmExGfxBase:X6} actsAs=${jr.LmActsAsBase:X6}");
            Check("juz acts-like base found per-ROM ($128000)", jr.LmActsAsBase == 0x128000);
            Check("juz ExGFX base found per-ROM ($118000)", jr.LmExGfxBase == 0x118000);
        }

        string afterRom = ReferenceRoms.LmAfter;
        if (File.Exists(afterRom))
        {
            Console.WriteLine("Direct Map16 parse + round-trip (after.smc, level 0x105):");
            var ar = Rom.Load(afterRom);
            Check("DM16 hijack detected", ar.HasDm16Hijack);
            var al = LevelParser.Parse(ar, 0x105);
            var dm = al.Objects.Where(o => o.IsDm16).ToList();
            Console.WriteLine("    DM16 objects: " + string.Join(" ",
                dm.Select(o => $"0x{o.Dm16Tile:X3}@({o.AbsoluteX},{o.Y})")));
            Check("found the placed DM16 tiles", dm.Count >= 5);
            var tiles = dm.Select(o => o.Dm16Tile).ToHashSet();
            Check("decoded tiles include 0x100/0x101/0x200/0x201/0x202",
                  new[] { 0x100, 0x101, 0x200, 0x201, 0x202 }.All(tiles.Contains));
            var agrid = ObjectEngine.Render(ar, al);
            Check("DM16 tiles land in the render grid (not markers)",
                  agrid.Get(2, 5) == 0x100 && agrid.Get(9, 5) == 0x200);
            byte[] enc = LevelEncoder.Encode(al);
            int afo = ar.FileOffset(al.DataPointer);
            Check("DM16 level re-encodes byte-identical",
                  enc.AsSpan().SequenceEqual(ar.Data.AsSpan(afo, enc.Length)));

            Console.WriteLine("In-app save (merge DM16 edit on a mid screen + reload):");
            var sr = Rom.Load(afterRom);
            var sl = LevelParser.Parse(sr, 0x105);
            int targetScreen = sl.Objects[sl.Objects.Count / 2].Screen;   // a screen with objects
            var newObj = LevelObject.MakeDm16(0x110, targetScreen, 4, 6);
            var merged = new List<LevelObject>();
            bool inserted = false;                              // screens can repeat (jumps)
            for (int i = 0; i < sl.Objects.Count; i++)
            {
                merged.Add(sl.Objects[i]);
                int next = i + 1 < sl.Objects.Count ? sl.Objects[i + 1].Screen : -1;
                if (!inserted && sl.Objects[i].Screen == targetScreen && sl.Objects[i].Screen != next)
                { merged.Add(newObj); inserted = true; }
            }
            var sd = LevelEncoder.Encode(sl, merged);
            sr.ExpandTo(0x200000);
            sr.SetLayer1Pointer(0x105, RatsWriter.Allocate(sr, sd));
            string stmp = Path.Combine(Path.GetTempPath(), "pd_inapp_save.smc");
            RatsWriter.SaveAs(sr, stmp);
            var sre = Rom.Load(stmp);
            var srl = LevelParser.Parse(sre, 0x105);
            var newPlaced = srl.Objects.Where(o => o.IsDm16 && o.Dm16Tile == 0x110).ToList();
            Console.WriteLine($"    target screen {targetScreen}; placed tile 0x110 -> " +
                string.Join(" ", newPlaced.Select(o => $"scr{o.Screen}@({o.AbsoluteX},{o.Y})")));
            Check("new DM16 tile landed on the target screen",
                  newPlaced.Count == 1 && newPlaced[0].Screen == targetScreen);
            Check("all original objects preserved",
                  srl.Objects.Count == sl.Objects.Count + 1);
            File.Delete(stmp);

            Console.WriteLine("Erase-on-save (blank sky tile 0x025 overwrites an original cell):");
            var er = Rom.Load(afterRom);
            var el = LevelParser.Parse(er, 0x105);
            var eg = ObjectEngine.Render(er, el);
            int ex = -1, ey = -1;                       // first real tile on screen 0
            for (int y = 0; y < eg.Height && ex < 0; y++)
                for (int x = 0; x < 16 && ex < 0; x++)
                    if (eg.Get(x, y) != Map16Grid.Empty && (eg.Get(x, y) & ObjectEngine.Marker) == 0)
                    { ex = x; ey = y; }
            var emerged = new List<LevelObject>();
            for (int i = 0; i < el.Objects.Count; i++)
            {
                emerged.Add(el.Objects[i]);
                int next = i + 1 < el.Objects.Count ? el.Objects[i + 1].Screen : -1;
                if (el.Objects[i].Screen == 0 && next != 0)
                    emerged.Add(LevelObject.MakeDm16(0x025, 0, ex, ey));
            }
            er.ExpandTo(0x200000);
            er.SetLayer1Pointer(0x105, RatsWriter.Allocate(er, LevelEncoder.Encode(el, emerged)));
            string etmp = Path.Combine(Path.GetTempPath(), "pd_erase_save.smc");
            RatsWriter.SaveAs(er, etmp);
            var ere = Rom.Load(etmp);
            var egrid = ObjectEngine.Render(ere, LevelParser.Parse(ere, 0x105));
            Console.WriteLine($"    erased cell ({ex},{ey}): was 0x{eg.Get(ex, ey):X3}, now 0x{egrid.Get(ex, ey):X3}");
            Check("erased cell reads back as blank sky 0x025", egrid.Get(ex, ey) == 0x025);
            File.Delete(etmp);
        }

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
