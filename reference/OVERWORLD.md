# Overworld

How SMW stores the overworld, how Lunar Magic edits it, and what pipe-dream's Overworld mode
does with both. Addresses are vanilla and traced in `smw-disasm/bank_04.asm` (uncommented, so
labels below are grep-able but unnamed). Lunar Magic facts are from `lm-help/html/ov_*.htm`.

Reader: `src/rom/Overworld.cs`. Session: `src/services/EditorSession.Overworld.cs`. Window:
`src/ui/MainWindow.Overworld.cs`. Eyeball the reader with `--owpng <rom> <out.png>`.

## 1. Geometry

Two maps, each 32x32 16x16 tiles (512x512 px). The **main map** is index 0x000-0x3FF. The
**six submaps share one second map**, index 0x400-0x7FF, two columns by three rows; a "submap"
(`$1F11`: 1 Yoshi's Island, 2 Vanilla Dome, 3 Forest, 4 Valley, 5 Special, 6 Star) is a fixed
camera plus a palette, not separate storage. Cameras `$049A0C`: 1 (-17,-40), 2 (-17,128),
3 (-17,296), 4 (240,-40), 5 (240,128), 6 (240,296) px. Lunar Magic draws it exactly so: the
main map at tile Y 0-0x1F, the submap map at Y 0x20-0x3F, one canvas.

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
6x6 pieces, then 2x2), property bytes RLE at `$0C8D00`. A step writes piece `src` at `dst`
(`$04E4D0` 2x2 if src >= 0x900, `$04E520` 6x6).

## 4. Graphics and palettes

- Tileset row `$1931 = 0x11 + submap` → `ObjectGfxList $00A92B` rows 0x11-0x17 = **GFX1C 1D 08
  1E** on every submap in vanilla (rows exist so LM can bypass per submap). Sprite set 0x11 →
  `SpriteGfxList $00A8C3` = GFX10 0F 1C 1D. Layer 3 = GFX28-2B. `Gfx.FgTiles.Load(rom, 0x11+n)`
  yields the FG pages.
- **Animated tiles**: VRAM tiles 0x75-0x7F are rebuilt every frame from GFX14 (the file
  decompressed last, still in `$7EAD00`) and uploaded by `$00A4E3` (0x160 bytes to VRAM word
  $0750). Three fixed: GFX14 tiles 0x50-0x52 (`$048000`). Eight cycling, four frames each:
  frame 0 = GFX14 tile 0x40 + 8k (`$048006`, stepped at `$048123`). Prep v13 rescales that
  table for 4bpp (CONTRACT §6a-13). `Overworld.WithAnimatedTiles` mirrors frame 0.
- Palette: the overworld load (`$00A5BC`) runs the ordinary level palette loader `LoadPalette`
  first, with whatever header bytes the last level left (a new game arrives with the intro level
  0xC7's), then `$00AD25` lays the overworld's colours over it: palettes 4-7 colours 1-7 from
  `$00B3D8 + $00ABDF[$00AD1E[submap]]` (`$00AD1E = {1,0,3,4,3,5,2}`, sets of 0x38 bytes;
  special-world-passed set at `$00B732`); palettes 2-7 colours 9-F `$00B528`; sprite palettes
  8-F colours 1-7 `$00B57C`; palettes 0-1 colours 8-F `$00B5EC`. **Layer 2 draws only in rows
  4-7; layer 1 draws mostly in rows 0-3**, whose colour 1 is the loader's white and colours 8-F
  the overrides. Path fading borrows rows 0-3 and C-F colours 1-7 during a reveal.
  `Palette.LoadOverworld`.

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
bit at the level you stand on. Step vectors `$049058`; corner tiles `$04A03C`; per-tile speed
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
- **Exit tiles** (walk off one map onto another): source `$049964/66/68` (Y, X, submap; 5-byte
  stride, ~14 entries) → destination `$0499AA/AC/AE`, arrival tile `$0499F0/F1`.
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
| Layer 2 8x8 Editor (default) | Tiles |
| Layer 1 16x16 Editor — level tiles AND the invisible path tiles are dragged like any tile; Alt+Right = Modify Level Tile Settings; Alt+Left on two star/pipe/exit tiles links them | Paths, Levels, Transitions |
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

## 11. Status and caveats

Done (v0.4.x): render of both layers with animated tiles at rest, per-submap palettes by region;
the Overworld mode with five drawer tabs; the overworld's eight GFX files in the Graphics
drawer. **Tiles** edits layer 2 in 8x8s: right-click paints the drawer's tile in the bar's
palette row and flips, lasso/move/grow as the background tilemap does, undo per stroke; the map
is kept as 0x2000 words in the project (`Overworld.Layer2`) and written back into the ROM's own
stream space at build time when it packs small enough (`Overworld.WriteLayer2`; refused with a
reason otherwise — no relocation yet). Paths, Levels, Events, Transitions are read-only views.

The reader is **vanilla-format**. No ROM on this machine carries a Lunar Magic overworld edit,
so LM's relocations are unverified: before trusting the reader on an edited ROM, probe
`$04DC5A`, `$04DC71/8C`, `$04DD44`, `$04D7F2/$04D83C`, `$04DCBB`, `$048509/3B`, `$05D9C9`,
`$00AD25/27`, `$04E470`, `$04E677`, `$04E9F1` for `JSL`/re-pointed operands on a ShaoBase-style
ROM, and make the addresses in `Overworld` follow them.
