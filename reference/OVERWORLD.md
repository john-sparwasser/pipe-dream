# Overworld

How SMW stores the overworld, how Lunar Magic edits it, and what pipe-dream's Overworld mode
does with both. Addresses are vanilla and traced in `smw-disasm/bank_04.asm` (uncommented, so
labels below are grep-able but unnamed). Lunar Magic facts are from `lm-help/html/ov_*.htm`.

Reader: `src/rom/Overworld.cs` and its partials, one per concern (Tables, Layers, Graphics,
Paths, Levels, Transitions, Events — the class doc in `Overworld.cs` maps them). Session:
`src/services/EditorSession.Overworld.cs`. Window: `src/ui/MainWindow.Overworld.cs` dispatches
each gesture to `.Layer2.cs` (Tiles tab) or `.Layer1.cs` (Paths & Levels tab); `.Badges.cs` is
LM's View menu drawn over the map. Eyeball the reader with `--owpng <rom> <out.png>`.

## 1. Geometry

Two maps, each 32x32 16x16 tiles (512x512 px). The **main map** is index 0x000-0x3FF. The
**six submaps share one second map**, index 0x400-0x7FF, two columns by three rows; a "submap"
(`$1F11`: 1 Yoshi's Island, 2 Vanilla Dome, 3 Forest, 4 Valley, 5 Special, 6 Star) is a fixed
camera plus a palette, not separate storage. Cameras `$049A0C`: 1 (-17,-40), 2 (-17,128),
3 (-17,296), 4 (240,-40), 5 (240,128), 6 (240,296) px. Lunar Magic draws it exactly so: the
main map at tile Y 0-0x1F, the submap map at Y 0x20-0x3F, one canvas — **with the submap map
rotated 16px right and 8px down** (two 8x8 cells and one row): its tilemap's last two columns
and last row wrap round to the lower map's left edge and top, as a hardware scroll would show
them, and layer 1 rides the same shift. Measured 2026-09-06 by aligning LM's window and a Mesen
frame of the game against our raw render: the game shows both layers on the plain grid; the
rotation is LM's canvas layout, and pipe-dream copies it (`EditorSession.OwSubDx/OwSubDy`,
`OwMapCell`, `OwHasLayer1`) so the two editors agree on what is where.

Layer 1 index (`$049885`): `(X&0xF) | (X&0x10)<<4 | (Y&0xF)<<4 | (Y&0x10 ? 0x200 : 0)` — four
16x16-tile screens TL TR BL BR of 0x100 each, +0x400 on the submap map.

## 2. Layer 1 — level tiles, clouds, Mario's paths (16x16)

- Tilemap `$0CF7DF`, 0x800 bytes, one tile number per cell; copied to `$7EC800` (`$04DC5A`).
- Map16 definitions `$05D000`, 8 bytes per tile = four words **TL, BL, TR, BR** (the same order
  `Map16.Compose` takes); **0xC1 tiles** — `$05D608` is the next table. Words are standard
  `vhopppcc tttttttt tt`. LM expands this to two pages (0x200).
- Tile meaning (`$049140`, `$0492F2`): 0 empty; **< 0x56 path tiles** (walk/climb/swim/exit,
  0x4E-0x50 fish, 0x49-0x4B Koopa-kid triggers); **0x56-0x86 level tiles**; 0x5F star, 0x5B pipe,
  0x82 pipe ignoring directions, 0x81 destroyed castle. Tiles >= 0x81 are not enterable.
- **Vanilla stores no per-tile level number.** `$04D7F2` scans `$7EC800[0..0x7FF]` in index order
  and numbers every 0x56-0x80 tile 1, 2, 3… (translevel). Level = translevel below 0x25, else
  translevel - 0x24 | 0x100 (`$05D88B`). LM's "level number to use for this tile" therefore
  needs a hijack there — **inferred, not yet probed**.

## 3. Layer 2 — the land (8x8)

Two RLE streams: tile numbers `$04A533`, property bytes `$04C02B` (`$04DABA`): header n, bit 7
clear = copy n+1 literal bytes, set = repeat next byte (n&0x7F)+1 times; each stream fills one
byte of every word until 0x2000 words. Result: two 64x64 8x8 tilemaps in SNES screen order
(four 32x32 screens of 0x400 words, TL TR BL BR), main map then submap map, DMA'd to VRAM
$3000. Layer 2 scrolls with layer 1 (no parallax); layer 3 is the static border.

**Layer-2 event tiles** (LM's 6x6 and 2x2 "pieces"): tile bytes `$0C8000` (0x900 bytes of
6x6 pieces, then 2x2), property bytes RLE at `$0C8D00` — the same encoding as layer 2's streams,
so the two bytes join into an 8x8 word (`Overworld.EventPieces`). 0xD00 cells in all: 64 pieces of
6x6, then 256 of 2x2. A step writes piece `src` at `dst` (`$04E4D0` 2x2 if src >= 0x900,
`$04E520` 6x6).

## 4. Graphics and palettes

- Tileset row `$1931 = 0x11 + submap` → `ObjectGfxList $00A92B` rows 0x11-0x17 = **GFX1C 1D 08
  1E** on every submap in vanilla (rows exist so LM can bypass per submap). Sprite set 0x11 →
  `SpriteGfxList $00A8C3` = GFX10 0F 1C 1D. Layer 3 = GFX28-2B. `Gfx.FgTiles.Load(rom, 0x11+n)`
  yields the FG pages.
- **Per-submap GFX** (LM's *Overworld ▸ Submap GFX*, `ov_overworld_super_bypass.htm`) —
  **SETTLED 2026-09-09**, by installing the hack through that dialog on four copies of a v17 base
  (one with nothing changed as the control, one changing submap 1's FG1, one submap 4's FG1, one
  submap 1's SP1 and AN2) and diffing. Prep v18 carries it; `Rom.OwGfxBypass(submap)` reads it.
  - **The table is the per-level Super-GFX-Bypass table, seven entries longer**: submap N is
    entry **0x200 + N**, N = 0 the main map and 1-6 LM's dialog order (Yoshi's Island, Vanilla
    Dome, Forest of Illusion, Bowser's Valley, Special World, Star Road). LM reallocates the
    whole table to fit them (ours moved from `$129000` to `$18B284` on its save); prep v18
    stamps it 0x207 entries long in the first place.
  - **A submap's record is byte-for-byte a level's** (CONTRACT §7d, §12b): w0 AN2, w1 AN1,
    w2/w3 = FG6/FG5, w4-w7 = FG4-FG1, w8-w11 = SP4-SP1, w12-w15 = the layer-3 four. So the
    dialog's FG1-FG6 are the level record's FG1/FG2/BG1/FG3/BG2/BG3 — the same six pages
    `Gfx.FgTiles` loads, in the same word order — and FG5/FG6 are BG2/BG3, which the dialog
    greys unless the *merge FG1/FG2 into SP3/SP4* option is on. FG7/FG8 exist as dialog controls
    but are not laid out. `0x7F` is *skip this slot*, the same convention as a level's.
  - **There is no enable bit.** LM fills all seven records with the values the vanilla lists
    imply — `14 00 7F 00 7F 00 7F 00 1E 00 08 00 1D 00 1C 00 1D 00 1C 00 0F 00 10 00 2B 00 2A 00
    29 00 28 00` — and leaves w0 bit 15 clear; the loader reads a submap's record
    unconditionally. Read back both ways: our writer's bytes appear in LM's dialog when they are
    in a table LM installed (a hand-patched LM save showed FG1 = 0F and SP1 = 0C on the Forest
    of Illusion), and LM's dialog writes exactly these words.
  - **The hooks.** Vanilla's `STA $20 : SEP #$20` at `$00A140` — the last thing before the
    `JSR UploadSpriteGFX` ($00A9DA) whose tail at `$00AA50` carries the bypass loader — becomes
    `JSL $0FFAB0`, and the `LDY #$14 : JSL $00BA28` at `$00A147` keeps the LDY but has the JSL
    NOPped, because the record's AN2 slot now supplies that file. LM's stub at `$0FFAB0` is
    `STA $20 : TXA : ASL×4 : CLC : ADC #<record low16> : STA $7FC006 : SEP #$20 :
    LDA #<bank> : STA $7FC008 : LDA #$42 : STA $7FC009 : LDA #$00 : STA $7FC00B : RTL` — the
    submap comes in as X = submap*2 from vanilla's own `LDX $0DB3 : LDA $1F11,X : ASL : TAX` at
    `$00A12A`, the same place LM's overworld ExAnimation setup reads it (EXANIMATION.md §10), and
    `$7FC009 = #$42` is what tells LM's loader this is the overworld rather than a level (#$41).
  - **Ours differs in the stub and the loader, and that costs the read-back**: prep v18's stub
    arms `$FE` = index + 1, which is the same fact in the form the loader prep v2 carries already
    reads, and v18's loader treats an index past the levels as needing no enable bit. LM finds a
    submap's record through the immediate baked into *its own* stub, so LM's dialog does not read
    ours — see reference/LM_PARITY.md §2. The table, the index and the words are LM's; only the
    code around them is not.
  - **Not done: per-submap LAYER 3 GFX and tilemap bypass** (`ov_overworld_layer3_gfx.htm`) is a
    SECOND, separate hack with its own dialog, its own per-submap enable checkbox, and a bypass
    that `level_layer3_gfx.htm` says turns on per level *and* per submap once any level or submap
    is saved with it. Opening that dialog installs it immediately too — it was opened by accident
    during this probe, which is how we know it is a different install. Untouched here.
- **Animated tiles**: VRAM tiles 0x75-0x7F are rebuilt every frame from GFX14 (the file
  decompressed last, still in `$7EAD00`) and uploaded by `$00A4E3` (0x160 bytes to VRAM word
  $0750). Three water tiles, GFX14 0x50-0x52 (`$048000`), scrolled in RAM every eight frames
  (`$0480E0`: 0x75 rows 0-3 a pixel left and 4-7 right, 0x76 a row down, 0x77 both) — LM shows
  them unscrolled (matched against its pixels 2026-09-06). Eight cycling, eight frames each:
  GFX14 tile 0x40 + 8k + frame (`$048006`, one table of 64; `$048123` takes the frame from
  counter bits 3-5, or 4-6 for the two waterfall slots). Prep v13 rescales that table for 4bpp
  (CONTRACT §6a-13). **Lunar Magic shows counter 8-15**: slots 2-7 on frame 1, the waterfall
  on frame 0 — measured against LM's pixels 2026-09-06; `Overworld.WithAnimatedTiles` mirrors it.
- **Plane 3 from the uploader**: vanilla's expand-upload (`$00AA80`) synthesizes a fourth plane
  as the OR of the other three for GFX1E always and GFX08 on tilesets 0x11+ (`$00AA8C-$00AA94`,
  the `$0A = $FF00` filter), so every drawn pixel of those files lands in colours 8-F of its
  row — the castles, the level stars and the SNES logo are painted for those colours, and the
  3bpp files hold indices 1-7 that never show. `Gfx.FgTiles.Load` applies the same rule
  (`UploaderAddsPlane3`), reading the two file numbers off the ROM; LM's 4bpp mode bakes the
  plane into its files and points both compares at GFX32.
- Palette: the overworld load (`$00A5BC`) runs the ordinary level palette loader `LoadPalette`
  first, with whatever header bytes the last level left (a new game arrives with the intro level
  0xC7's), then `$00AD25` lays the overworld's colours over it: palettes 4-7 colours 1-7 from
  `$00B3D8 + $00ABDF[$00AD1E[submap]]` (`$00AD1E = {1,0,3,4,3,5,2}`, sets of 0x38 bytes;
  special-world-passed set at `$00B732`); palettes 2-7 colours 9-F `$00B528`; sprite palettes
  8-F colours 1-7 `$00B57C`; palettes 0-1 colours 8-F `$00B5EC`. Both layers draw in rows
  0-7; what a layer 1 tile from GFX1E or GFX08 shows is colours 8-F of its row (plane 3 above),
  so rows 0-3 colours 1-7 — the level loader's leftovers — hardly matter, and LM's palette
  editor shows them black. Path fading borrows rows 0-3 and C-F colours 1-7 during a reveal.
  `Palette.LoadOverworld`; LM's Overworld Palette Editor agreed with it row for row on every
  submap (2026-09-06).

## 5. Level tiles

Per translevel (0-0x5F): exit directions `$04D678` (bits 7-6 normal, 5-4/3-2/1-0 secret 1-3;
0 up 1 down 2 left 3 right); base event `$05D608` ($FF none; secret exit N fires base+N);
level name `$04A0FC` (word → three string offsets into `$049AC5`). Flags RAM `$1EA2`: bit 7
passed, 6 midway, 3-0 directions enabled (LM adds 5 "no entry if passed", 4 "save prompt");
new-game pairs at `$009EE0`; Mario/Luigi start `$009EF0` = submap 1, tile (6,7). Reveal list
`$04DA1D → $04DA33` (22 pairs); event N reveals the layer-1 cell at `$04D85D[N]`. No-auto-move
levels `$04906C` (6). Music per submap `$048D8A`.

## 6. Paths

Walkability is the layer-1 tile at the next cell (path < 0x56 or level tile) and the direction
bit at the level you stand on. **What a path tile does** is its pose byte at `$049FEB` (read at
`$0495EF`): bit 3 swims (tiles 28-3E, 50), bit 4 climbs (3F-41, the ladders; also sets `$1B80`
for the ladder speed table `$049414`), anything else walks — bit 2 only picks Mario's
front-facing walk frames. **Exit tiles** are the ten in `$049426` (25, 40, 42-48, 4D). Fish jump
on 4E-50, Koopa Kids trigger on 49-4B. Level tiles 56-82 enter; 83-86 stop Mario without
entering. CONFIRMED 2026-09-06 against Lunar Magic's "Layer 1 Mario Paths" view on a ROM whose
layer 1 was a grid of every tile 01-86: LM draws exactly those sets green / blue / rungs / red /
X (`Overworld.KindOf`, `path_tiles_are_classified_from_the_engines_tables`). LM's pictures for
tiles 01-55 and 84-86 were lifted pixel for pixel from that view the same day into
`src/data/OwPathGlyphs.bin` (`Overworld.PathGlyph`; two captures over different land, keeping
the pixels both agreed on — the tiles LM only tints, 4D and 52-54, and the level-tile octagons
were left out and fall back to the kind's fill). LM stores no such bitmap: its exe resources
hold the level editor's slope outlines (type 2/500 BMPs) and the message font, not these. Step vectors `$049058`; corner tiles `$04A03C`; per-tile speed
and animation `$049EA7`. Five hardcoded paths (`$049078`, `$04910E`, `$0490CA`, `$049086`) that
LM cannot display, only disable. Layer-1 paths are static; "a path revealed by an event" is a
layer-2 change.

## 7. Events

0x78 events (bitfield `$1F02-$1F10`; vanilla tables sized 0x70). Layer-2 **standard steps**:
cumulative index `$04E35B` (event N = steps E35B[N-1]..E35B[N]-1), 4 bytes per step at
`$04DD8D` `[src word][dst word]`, animated one per frame with a sound. **Silent steps** (layer 1
and layer 2 on other maps): events `$04E8E4`, flags `$04E910` (bit 0 = layer-2 piece), dst
`$04E93C`, tile/piece `$04E994` (0x2C each); applied all at once on load. **Destroy** (castle,
switch palace): events `$04E5D6` (0x18), offsets `$04E5B6`, source tiles `$04E5A7`,
replacements `$04E5AC`/`$04E5B1`.

## 8. Transitions

- **Star/pipe warps**: 27 entries. Source `$048431[i]` (word `submap<<8 | tileX`) and
  `$048467[i]` (tileY); destination `$04849D[i]` (bits 0-8 X px, 9-12 dest submap) and
  `$0484D3[i]` (Y px). LM's "destination index" is derived by matching a destination to another
  entry's source; LM grows the table to 0x100 and reuses it for level "Location teleports".
  Every destination vanilla stores is the tile's CENTRE (`tile*16 + 8`), and every source sits on
  a star or pipe tile — 0x5A, 0x5B, 0x5F or 0x82 (`Overworld.IsWarpTile`, measured against all 27
  on 2026-09-08). The four arrays are butted together ($048431 + 0x36 IS $048467), so pipe-dream
  writes them in place and reuses slots but never adds one: vanilla fills all 27, and a link that
  needs a fresh slot is refused until something is unlinked. A source of $FFFF is a free slot —
  the shape the game's own search walks past.
- **Exit tiles** (walk off one map onto another): source `$049964/66/68` (Y px, X px, submap;
  5-byte stride, 14 entries) → destination `$0499AA/AC/AE`, arrival tile `$0499F0/F1` (tile Y,
  tile X). The walk at `$049A3F` compares the source against Mario's own position `$1F19/$1F17`
  and his submap `$13C3`, then writes the destination into all three plus his tile `$1F21/$1F1F`.
  The three tables are contiguous ($049964 + 0x46 IS $0499AA, and + 0x46 again is $0499F0), so
  pipe-dream keeps them as one block and writes them in place.

  **The position carries the direction.** Mario is centred on the axis he is not moving along and
  a tile short on the one he is, so an entry is a tile AND a way of travelling through it
  (`Overworld.StepInto`): moving down `(x*16+8, y*16)`, up `(x*16+8, y*16+16)`, right
  `(x*16, y*16+8)`, left `(x*16+16, y*16+8)`. The same bytes therefore read as "tile 20 walking
  down" or "tile 21 walking up"; the one of the two that is an exit tile settles it
  (`Overworld.Decode`). This is the "side to enter from" LM's link dialog asks for, and the
  destination is the partner tile written the same way, since the player keeps travelling as he
  was. Derived and confirmed against all 14 vanilla entries 2026-09-08; a round trip through
  pipe-dream's unlink and link reproduces the ROM's own bytes.

  **The map already knows the side**: an exit tile is the end of a path, so the player leaves by
  the side the path does not use, and `Overworld.TravelDirOf` reads it off the four neighbours.
  All 13 vanilla entries that sit on an exit tile agree with it (the fourteenth, entry 04, sits
  on a plain walk tile). Exactly one walkable neighbour or it refuses rather than guess.
- **Koopa teleports**: three positions `$048E49` (X) / `$048E4F` (Y), triggered by tiles 0x49-0x4B.

## 9. Sprites

13 slots x 5 bytes at `$04F625`: type, X lo/hi, Y lo/hi. Types in LM's list order (Lakitu,
bird, fish x3, piranha, cloud, Koopa kid x3, smoke x2, sign, Bowser, ghost x2). Mario's and
Luigi's start positions are edited as sprites in LM.

## 10. Lunar Magic's editor, for parity

One canvas; **left click selects, right click pastes**, Ctrl+Right always pastes the selector's
tile, left-drag lassos, drag a selection moves it, Ctrl+Left on a selection edge resizes it as
a repeating pattern. Five edit modes and where they land in pipe-dream's bar:

| Lunar Magic mode | pipe-dream |
|---|---|
| Layer 2 8x8 Editor (default) — and the only mode that shows the two areas beside the maps: the event pieces to the right of the main map, and layer 1's Map16 tiles below the submaps | Tiles (the same three areas on one canvas — see §10a) |
| Layer 1 16x16 Editor — level tiles AND the invisible path tiles are dragged like any tile; Alt+Right = Modify Level Tile Settings; Alt+Left on two star/pipe/exit tiles links them | Paths & Levels (places and moves the tiles, snapped to 16x16 over the 8x8 canvas; a level tile under the pointer grows an **Edit** button that opens LM's settings dialog — see below. Linking is the Transitions tab's Link button rather than Alt+Left here, and it covers the star and pipe half of LM's list) |
| Layer 2 Event Editor — Page Up/Down event, Home/End step; Shift+Right pastes a silent step | Events |
| Layer 1 Event Editor — silent only, for other submaps | Events |
| Sprite Editor — 8px steps, Mario/Luigi start | not yet placed |

View toggles worth matching: Layer 1, Layer 2, Sprites, Show Level Numbers, Show Event
Numbers, Show Star/Pipe/Location Numbers, Show Exit Path Numbers, Layer 1 Mario Paths (green
walk, black climb, blue swim, red exit), Future Layer 1 Tiles, Tile Grid, Special World Passed
Palette. "Modify Level Tile Settings" holds: level number, base event, direction per exit,
reveal-on-events, and the eight initial flags. Gotchas LM warns about: test on a fresh save
slot (directions live in SRAM); never reuse a level number on two tiles; paths must not
dead-end, gap or cross; one destroy and one reveal per event; star/pipe at X=0 on the submap
map do not work.

### 10a. The Tiles canvas, area by area  [MEASURED 2026-09-08 off LM's own status bar]

Lunar Magic's Layer 2 8x8 mode is wider than the two maps, and so is pipe-dream's Tiles tab. The
cells below are the one 8x8 grid both editors count in; pipe-dream's **Areas** view outlines and
names them, which LM does not.

| area | cols | rows | what a cell is |
|---|---|---|---|
| main map | 0-63 | 0-63 | a layer 2 word |
| submap map | 0-63 | 64-127 | a layer 2 word (rotated 2 right, 1 down — §1) |
| event pieces, 6x6 | 64-111 | 0-47 | a byte of `$0C8000`: 8 pieces across, 36 bytes each, row-major |
| event pieces, 2x2 | 112-159 | 0-21 | a byte from `$0C8900`: 24 pieces across, 4 bytes each |
| layer 1 tiles | 0-31 | 128+ | a word of the Map16 defs `$05D000`: 16 tiles across, TL BL TR BR |

Measured by hovering LM and reading its status bar: canvas (0x41,1) is the 6x6 pieces' byte 7
(0x11 in a vanilla ROM, which is what LM shows there), the 2x2 index steps by 1 across and 0x18
down (so 24 across), and (2,0x82) is "Map16 11" whose top-left word is 8x8 tile 0x1D9.

**The filler.** A cell no table reaches gets LM's filler tile rather than a hole: 8x8 tile 0x7A,
a black cross on blue, in palette row 5 (`Overworld.FillerWord`; LM's status bar reports "7A" out
there, and 0x7A's pixels are colours 1 and 4, which row 5 paints black on the sea's blue). It is
drawn from the FILE's graphics, not the map's: 0x7A is one of the cycling animated slots
(0x78-0x7F), so the map's copy is whichever GFX14 frame is up, and a cross that flickers is worse
than no marker (`Overworld.FillerPixels`).

LM paints that same X over piece slots a vanilla ROM fills with tile 0xBA — a dither texture —
so its event area looks emptier than ours. pipe-dream draws the bytes that are there, which is
what the game would draw if a step used the piece; LM is showing its own normalised copy, which
it writes on its first overworld save.

`EditorSession.OwAreaAt` maps a cell to its area and index; the three tables ride in ONE
`TilemapEdit`, so a brush, a lasso, a float and an undo work the same everywhere on the canvas,
and a commit takes each part back to its own table (`Overworld.WriteEventPieces`, which re-packs
the property RLE and refuses to grow it, and `WriteLayer1Defs`, a fixed table in place).

## 11. Status and caveats

**Modify Level Tile Settings** (LM's Alt-right click; here the hovered level tile grows an Edit
button, `MainWindow.Overworld.Layer1.cs`). Of its five groups, two are ours to write and three
are Lunar Magic's — measured 2026-09-08 by changing each in LM's own dialog and diffing:

| field | where it lives | on a base of ours |
|---|---|---|
| base event | `$05D608` + translevel, in place on every ROM | **editable** |
| direction per exit (normal + 3 secret) | `$04D678` + translevel, 2 bits each | **editable** — but LM's overworld save jumps its copy loop and reads a packed block of its own instead, so this is live exactly while `HasLevelTable` is false |
| level number | LM's per-tile table (the LZ2 blob behind the scan's operands) | **editable exactly while `HasLevelTable` is true** — a base of ours numbers by the game's scan order, so there is nothing to write and the number follows the tile instead. Set through the layer 1 map, which carries the number above the tile, so undo and the next brush stroke agree with it; the event and directions then land on the level now in the box, as they do in LM |
| eight initial flags | `$05DDA0` + level, a table LM ADDS when it saves | shown, not offered |
| reveal on event | LM's layer 1 event data | shown, not offered — the Events tab's job |

`EditorSession.OwLevelTileAt` / `SetOwLevelTile`; both tables are the ROM's edited copies, kept
in the project (`Overworld.BaseEvents`, `Overworld.ExitDirs`, and `Overworld.Translevels` for the
level number) and replayed at build.

Done (v0.4.x): render of both layers with animated tiles at rest, per-submap palettes by region;
the Overworld mode with five drawer tabs; the overworld's eight GFX files in the Graphics
drawer. **Tiles** edits layer 2 in 8x8s: right-click paints the drawer's tile in the bar's
palette row and flips, lasso/move/grow as the background tilemap does, undo per stroke; the map
is kept as 0x2000 words in the project (`Overworld.Layer2`) and written back into the ROM's own
stream space at build time when it packs small enough (`Overworld.WriteLayer2`; refused with a
reason otherwise — no relocation yet). Beside the maps it edits the other two areas of LM's 8x8
canvas (§10a): the event pieces (project `Overworld.EventPieces`) and layer 1's Map16 definitions
(`Overworld.Layer1Defs`), with the drawer offering all 0x200 8x8 tiles rather than the land's
first 0x100. **Paths & Levels** edits layer 1 in 16x16s
(place, move, grow, delete) and opens a level tile's settings. **Transitions** links and unlinks
both kinds of transition: the star and pipe warps, and the exit tiles the player walks between
maps on. The tab draws the stars and pipes RED rather than the green SMW paints them, since they
are its whole subject and green is what every path and level tile around them also is, and it
shows the exit tiles' own red without waiting for the Paths view — a tile you cannot see is one
you cannot link. Nothing on it edits a tile, so the map picks rather than paints: no 8x8 reticle,
no lasso. The tile under the pointer grows a **Link** button (or **Unlink**, with a confirm, on a
tile that has one), pressing Link lights every tile of the SAME kind the other end could be — the
two tables are different shapes, so a pipe cannot come out of an exit tile — and the next click
answers; Esc, or a click anywhere else, calls it off. Both ends are written, as a pipe pair is,
into the ROM's edited tables and the project (`Overworld.Warps`, `Overworld.Exits`), replayed at
build by `WriteWarps`/`WriteExits`. Not undoable, as the level tile's tables are not. An exit link
needs the map to say which way the player walks through each tile (see §8) and refuses when it
cannot. Koopa teleports are still read-only, and so is the Events tab.

### Lunar Magic's overworld hooks  [CONFIRMED 2026-09-06, BigEye + DogsOfWar + ShaoBase vs vanilla]

LM saves an overworld by moving a few tables and leaving the vanilla bytes in place — a reader
that trusts the vanilla addresses shows vanilla land under the hack's level tiles (what
pipe-dream did on DogsOfWar until `Overworld.Tables`). What moves, and where the loader says so
(`tools/dis65816.py <rom> <addr>` to look):

| table | vanilla | LM | read from |
|---|---|---|---|
| layer 2 tile stream | `$04A533` | anywhere (BigEye `$12B1D3`, Dogs `$139F8E`) | `LDA #$addr` at `$04DC71`; bank `LDA #$bb` at `$04DC78`, shared by both streams |
| layer 2 property stream | `$04C02B` | packed right after the tile stream (BigEye `$12B259`, Dogs `$13B2D6`) | `LDA #$addr` at `$04DC8C` |
| layer 1 Map16 | `$05D000`, 0xC1 tiles | **0x200 tiles**, LM's `STAR` marker right after (BigEye `$15A42C`; Dogs kept vanilla) | `LDX #$addr` at `$04DCBB`, `LDA #$bb` at `$04DCBF`; also at `$04DC3B` |
| layer 1 low bytes | `$0CF7DF` | in place | — |
| layer 1 high bytes | none | LZ2 blob → `$7FC800` at load (all zero in both hacks) | second `LDX #$addr : STX $8A : LDA #$bb : STA $8C` in LM's rewrite of the `$04D7F2` block |
| translevel per tile | the scan at `$04D7F2` | LZ2 blob → `$7ED000` (0x1000 bytes): **this is "level number to use for this tile"**; Dogs numbers 81 tiles differently from the scan | first `LDX/STX $8A/LDA/STA $8C` in the same block; the decompressor is vanilla `$00B8DE`, called by `PHK : PER : PEA : JML` |
| standard event steps | `$04DD8D` | BigEye `$138C38`, Dogs `$10F144` (vanilla bytes left behind) | long operands at `$04E49E`/`$04E4A3` and `$04E708`/`$04E70F` |
| step index `$04E35B` | | in place | — |
| event piece tiles | `$0C8000` | BigEye `$148000`, Dogs `$10E1E3` | bank `LDA #$bb` at `$04E4AF`, address `LDA #$addr` at `$04E4BA` |
| event piece properties | `$0C8D00` | BigEye `$148B4C`, Dogs `$138080` | `LDY #$addr` at `$04DD44`, bank `LDA #$bb` at `$04DD49` |
| silent events | `$04E8E4`/`$04E910`/`$04E93C`/`$04E994` | LM's own block, **16-bit tiles**; vanilla tables stale | `JSL` at `$04E9F1` → hook (BigEye `$129353`). Its four long operands in order: event→step index (words, 0x79), flags (byte per step, bit 0 = layer 2 piece), tile (word: layer 1 tile, or piece index with ≥0x900 = 2x2), destination (word: layer 1 index or layer 2 tilemap offset). Layer 2 pieces still go through vanilla `$04E4A9` |
| destroy events | `$04E5D6`, `$04E5B6`… | BigEye `$12A0B5`/`$12A045`, Dogs `$10F880`/`$10F810` | long operands at `$04E67C`, `$04E69C` |
| reveal list | `$04D85D` | BigEye `$129C37`, Dogs `$10EF1D` | long operands at `$04DA73`, `$04EC8B`, `$04ED96` |
| `$04E587` table (level-tile flags) | | BigEye `$12A07D`, Dogs `$10F848` | operand at `$04EEC8`, behind a `JSL` at `$04EEC3` that indexes it by LM's translevel |
| star/pipe warps | 27 entries at `$048431`… | in place; **entry count** is the `LDX #$n*2` at hook+$11 behind the `JSL` at `$048509` (BigEye 0, Dogs 26) | hook operand |
| base events `$05D608`, exit dirs `$04D678`, exit tiles `$049964`…, Koopa teleports `$048E49`, sprites `$04F625`, music `$048D8A`, no-auto-move `$04906C`, OW palettes `$00B3D8`… | | **edited in place** (Dogs changed music and 4 palette bytes there) | vanilla addresses hold |
| level names `$049AC5` | | Dogs only: `JMP $04FFB1` at `$049882`/`$049AC2` — LM's names rewrite, not traced | |

Also hooked and not yet traced: `$04DBB9` and `$04DCA5` (the latter replaces `INC $0F : LDA $0F`
in the load loop and replays passed events' silent steps from LM's tables), `$04DCFA → $06F5E4`,
`$04E5F1 → $05DCB0`, `$04E6C5`, `$04EDDD`, `$04EEF1`, `$05D7B9`, `$05D979`, `$05D9A1`, `$05D9E3`
(the level-number / base-event code of bank 05), `$0C9436`. SA-1 ROMs (sgdq2024) move all of
this to bank `$40` and `$6D`-mirrored RAM; out of scope.

`Overworld.Tables.Of` follows the first five rows, so layer 2, the Map16 table and 16-bit layer 1
read as LM wrote them; the rest of the table is what the Levels, Events and Transitions tabs
must follow when they land. `OverworldLmTests` pins the addresses on the two hacks.
