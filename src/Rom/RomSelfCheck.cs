namespace PipeDream;

/// <summary>
/// Runnable check for the ROM I/O layer (ponytail self-check). Asserts the LoROM
/// addressing math, then validates against the ROMs on disk when present. Run with:
///   dotnet run -- --selfcheck
/// </summary>
public static class RomSelfCheck
{
    const string CleanRom = @"C:\SMW\Projects\.resources\SMW.smc";
    const string EditedRom = @"C:\SMW\Projects\ShaoBase\base.smc";

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
            Check("clean ROM has no RATS (unexpanded)", !r.EnumerateRats().Any());
        }
        else Console.WriteLine($"(skip) clean ROM not found: {CleanRom}");

        if (File.Exists(EditedRom))
        {
            Console.WriteLine($"Edited ROM: {EditedRom}");
            var r = Rom.Load(EditedRom);
            var rats = r.EnumerateRats().ToList();
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
            var lv = Level.Parse(r, 0x105);
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
            var lv = Level.Parse(r, 0x105);
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
            var lv = Level.Parse(r, 0x105);
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
                var l = Level.Parse(r, ln);
                if (l.Empty) continue;
                tested++;
                byte[] enc = l.Encode(r);
                int fo = r.FileOffset(l.DataPointer);
                var orig = r.Data.AsSpan(fo, enc.Length).ToArray();
                if (!enc.AsSpan().SequenceEqual(orig)) { mism++; Console.WriteLine($"    level 0x{ln:X3}: MISMATCH ({enc.Length} bytes)"); }
            }
            Console.WriteLine($"    round-tripped {tested} levels, {mism} mismatches");
            Check("object stream re-encodes byte-identical", mism == 0 && tested > 0);

            Console.WriteLine("Save path (expand + RATS + repoint + reload):");
            var wr = Rom.Load(CleanRom);             // fresh copy to mutate
            var yi2 = Level.Parse(wr, 0x105);
            int origCount = yi2.Objects.Count;
            wr.ExpandTo(0x200000);                   // expand to 2MB
            int newAddr = wr.AllocateRats(yi2.Encode(wr));
            wr.SetLayer1Pointer(0x105, newAddr);
            string tmp = Path.Combine(Path.GetTempPath(), "pd_save_test.smc");
            wr.SaveAs(tmp);
            var re = Rom.Load(tmp);
            Check("saved pointer relocated to expanded space", re.Layer1Pointer(0x105) >= 0x080000);
            var yi2b = Level.Parse(re, 0x105);
            Console.WriteLine($"    reloaded: ptr ${re.Layer1Pointer(0x105):X6}, {yi2b.Objects.Count} objects (was {origCount})");
            Check("reloaded level has same object count", yi2b.Objects.Count == origCount);
            Check("reloaded RATS tag is valid", re.EnumerateRats().Any());
            // checksum: SaveAs fixed it; verify chk + complement == 0xFFFF and chk matches a resum
            long resum = 0; int rh = re.HeaderOffset, rsz = re.ActualRomSize;
            for (int i = 0; i < rsz; i++) resum += re.Data[rh + i];
            Check("saved ROM checksum + complement == 0xFFFF", (re.Checksum ^ re.ChecksumComplement) == 0xFFFF);
            Check("saved ROM checksum matches byte sum", re.Checksum == (int)(resum & 0xFFFF));
            File.Delete(tmp);
        }

        string afterRom = @"C:\SMW\Projects\.resources\after.smc";
        if (File.Exists(afterRom))
        {
            Console.WriteLine("Direct Map16 parse + round-trip (after.smc, level 0x105):");
            var ar = Rom.Load(afterRom);
            Check("DM16 hijack detected", ar.HasDm16Hijack);
            var al = Level.Parse(ar, 0x105);
            var dm = al.Objects.Where(o => o.IsDm16).ToList();
            Console.WriteLine("    DM16 objects: " + string.Join(" ",
                dm.Select(o => $"0x{o.Dm16Tile:X3}@({o.AbsoluteX},{o.Y})")));
            Check("found the placed DM16 tiles", dm.Count >= 5);
            var tiles = dm.Select(o => o.Dm16Tile).ToHashSet();
            Check("decoded tiles include 0x100/0x101/0x200/0x201/0x202",
                  new[] { 0x100, 0x101, 0x200, 0x201, 0x202 }.All(tiles.Contains));
            byte[] enc = al.Encode(ar);
            int afo = ar.FileOffset(al.DataPointer);
            Check("DM16 level re-encodes byte-identical",
                  enc.AsSpan().SequenceEqual(ar.Data.AsSpan(afo, enc.Length)));

            Console.WriteLine("In-app save (merge DM16 edit on a mid screen + reload):");
            var sr = Rom.Load(afterRom);
            var sl = Level.Parse(sr, 0x105);
            int targetScreen = sl.Objects[sl.Objects.Count / 2].Screen;   // a screen with objects
            var newObj = LevelObject.MakeDm16(0x110, targetScreen, 4, 6);
            var merged = new List<LevelObject>();
            for (int i = 0; i < sl.Objects.Count; i++)
            {
                merged.Add(sl.Objects[i]);
                int next = i + 1 < sl.Objects.Count ? sl.Objects[i + 1].Screen : -1;
                if (sl.Objects[i].Screen == targetScreen && sl.Objects[i].Screen != next) merged.Add(newObj);
            }
            var sd = sl.Encode(sr, merged);
            sr.ExpandTo(0x200000);
            sr.SetLayer1Pointer(0x105, sr.AllocateRats(sd));
            string stmp = Path.Combine(Path.GetTempPath(), "pd_inapp_save.smc");
            sr.SaveAs(stmp);
            var sre = Rom.Load(stmp);
            var srl = Level.Parse(sre, 0x105);
            var newPlaced = srl.Objects.Where(o => o.IsDm16 && o.Dm16Tile == 0x110).ToList();
            Console.WriteLine($"    target screen {targetScreen}; placed tile 0x110 -> " +
                string.Join(" ", newPlaced.Select(o => $"scr{o.Screen}@({o.AbsoluteX},{o.Y})")));
            Check("new DM16 tile landed on the target screen",
                  newPlaced.Count == 1 && newPlaced[0].Screen == targetScreen);
            Check("all original objects preserved",
                  srl.Objects.Count == sl.Objects.Count + 1);
            File.Delete(stmp);
        }

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
