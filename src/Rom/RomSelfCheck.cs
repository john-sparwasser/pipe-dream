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

        string map16After = @"C:\SMW\Projects\.resources\map16_after.smc";
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

        string gfxAfter = @"C:\SMW\Projects\.resources\gfx_after.smc";
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
            var lvh = Level.Parse(gr, 0x105).Header;
            var defTiles = Gfx.FgTiles.Load(gr, lvh.Tileset);
            var bypTiles = Gfx.FgTiles.Load(gr, lvh.Tileset, 0x105);
            bool differs = Enumerable.Range(0, 0x200).Any(t => !defTiles.Fetch(t).SequenceEqual(bypTiles.Fetch(t)));
            Check("FgTiles.Load(level) applies the bypass (tiles differ from default)", differs);
        }

        string shaoRom = @"C:\SMW\Projects\ShaoBase\base.smc";
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
        }

        string dowRom = @"C:\SMW\Projects\DogsOfWar\dogs_of_war-backup.smc";
        if (File.Exists(dowRom))
        {
            Console.WriteLine("LM custom palettes (DogsOfWar, CONTRACT §7e):");
            var dr = Rom.Load(dowRom);
            Check("palette hook detected (JML at $0095E9)", dr.HasLmPaletteHook);
            var cp = dr.LmCustomPalette(0x107);
            Check("level 0x107 has a custom palette", cp is not null);
            Check("level 0x105 has none (vanilla path)", dr.LmCustomPalette(0x105) is null);
            if (cp is (var back, var colors))
            {
                Console.WriteLine($"    back=${back:X4} c1=${colors[1]:X4} c0x21=${colors[0x21]:X4}");
                Check("row color-0 slots stored as 0", Enumerable.Range(0, 16).All(r => colors[r * 16] == 0));
                Check("palette has real colors", colors.Count(c => c != 0) > 64);
                var lp = Palette.Load(dr, Level.Parse(dr, 0x107).Header, 0x107);
                Check("Palette.Load(level) uses the custom palette", lp.Bgr[0] == back && lp.Bgr[1] == colors[1]);
            }
            // vanilla ROM guard: $0EF600 holds unrelated data there, hook check must gate it
            var vr = Rom.Load(CleanRom);
            Check("clean ROM: no palette hook, no custom palettes",
                  !vr.HasLmPaletteHook && vr.LmCustomPalette(0x107) is null);
        }

        string juzRom = @"C:\SMW\Projects\juz\SMW.smc";
        if (File.Exists(juzRom))
        {
            // Regression: these tables move per-ROM; juz's ExGFX table sits where other ROMs
            // keep acts-like ($118000), which the old hardcoded reader misread.
            var jr = Rom.Load(juzRom);
            Console.WriteLine($"LM table bases in juz: bypass=${jr.LmGfxBypassBase:X6} exGfx=${jr.LmExGfxBase:X6} actsAs=${jr.LmActsAsBase:X6}");
            Check("juz acts-like base found per-ROM ($128000)", jr.LmActsAsBase == 0x128000);
            Check("juz ExGFX base found per-ROM ($118000)", jr.LmExGfxBase == 0x118000);
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
            var agrid = ObjectEngine.Render(ar, al);
            Check("DM16 tiles land in the render grid (not markers)",
                  agrid.Get(2, 5) == 0x100 && agrid.Get(9, 5) == 0x200);
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
