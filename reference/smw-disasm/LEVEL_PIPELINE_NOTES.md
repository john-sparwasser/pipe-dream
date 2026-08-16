# SMW Level Pipeline — level number → tiles on screen

The full level-loading data path traced through banks 05 and 0D (2026-07-24).
Directly relevant to `CONTRACT.md`'s level-decode contracts. Companion docs:
`SYSTEM_NOTES.md`, `SPRITE_ENGINE_NOTES.md` (the sprite side of spawning).

## A. Orchestration

- Entry: `CODE_05801E` $05801E — clears BG tile buffers $7EB900/$7EBB00 to `#$25`
  (blank tile), handles the layer-2-background case (§G), clears the Map16 buffer,
  then `JSR LoadLevel` $0583AC.
- `LoadLevel` runs per layer (`$1933` = 0 for L1, 1 for L2): decode header (§B) →
  GFX/tileset setup (`CODE_0581FB`) → object loop (§E). Boss modes 09/0B/10 skip
  object data. For L2-object levels it repoints $65–$67 ← $68–$6A + 5 and reruns.

## B. Primary header — 5 bytes (`CODE_0584E3`)

Read via `[$65],Y`; $65 then advances by 5 to the object stream.

| Byte | Bits | → |
|---|---|---|
| 1 | 0–4 (+1) | screen count `$5D` |
| 1 | 5–7 | BG palette `$1930` |
| 2 | 0–4 | **level mode `$1925`** (indexes every §C table) |
| 2 | 5–7 | BG color `$192F` |
| 3 | 0–3 | **sprite GFX set `$192B`** |
| 3 | 4–6 | music, via `LevelMusicTable` $0584DB → `$0DDA` |
| 3 | 7 | layer-3 priority `$3E` |
| 4 | 0–2 / 3–5 | FG palette `$192D` / sprite palette `$192E` |
| 4 | 6–7 | timer, via `TimerTable` $0584D7 (0/200/300/400) → `$0F31+` |
| 5 | 0–3 | **FG/BG GFX set (tileset) `$1931`/`$1932`** |
| 5 | 4–5 | H/V scroll `$1412` (3 = no scroll) |
| 5 | 6–7 | item memory `$13BE` |

**Sprite memory `$1692` is NOT in this header** — it's bits 0–5 of the *sprite data's*
first byte (bits 6–7 = buoyancy `$190E`), read at `CODE_05D8F9`.

## C. Level-mode tables (X = `$1925`, one byte per mode, $058417–$0584DB)

- `VerticalTable` $058417 — bit 0 = vertical level → `$5B`. Vertical modes: 02–08, 1F.
- `LevMainScrnTbl`/`LevSubScrnTbl` $058437/$058457 → TM/TS mirrors $0D9D/$0D9E.
- `LevCGADSUBtable` $058477 → color-math mirror `$40`.
- `SpecialLevTable` $058497 → `$0D9B`: 00 normal, 80 Iggy/Larry, C0 Morton/Ludwig/Roy,
  C1 Bowser (this flag is what flips NMI onto the Mode-7 path).
- `LevXYPPCCCTtbl` $0584B7 → `$64` (sprite OAM property XOR mask).
- L2-object modes (get a second object pass): everything except 0A,0C,0D,0E,11,1E.

## D. Level number → data pointers (`CODE_05D8B7`)

- Level# `$0E` × 3 (`ASL : ADC`) indexes the 3-byte × 512 tables:
  - `Layer1Ptrs` $05E000 → `$65–$67`. Targets live in **bank 06** (143 streams; a few
    overflow into bank 07). Unused slots point at the shared default $068000.
  - `Layer2Ptrs` $05E600 → `$68–$6A`. **Bank byte $FF = "L2 is a prebuilt background"**
    (§G), real bank forced to $0C.
- Level# × 2 indexes `Ptrs05EC00` (2-byte) → sprite pointer `$CE–$CF`, bank forced
  to **$07** (`$D0 = #$07`) — all sprite lists live in bank 07.
- Entrance ("secondary header") tables, 1 byte × 512 each: `DATA_05F000` (entrance Y/
  screen + midway bits), `DATA_05F200` (entrance X + fade FX `$1BE3`), `DATA_05F400`,
  `DATA_05F600` (vertical flags `$5B`, L2 scroll `$141F`), `DATA_05FC00`, `DATA_05FE00`.
  Nibble→pixel mapping via the small `DATA_05D7xx` tables.

## E. Object stream — 3 bytes per object (`LoadLevelData` $0585FF)

Bytes → `$0A,$0B,$59`; loop until the next byte is `$FF`.

```
byte1 $0A: N BB Y YYYY   N=new screen (INC $1928), BB=obj# bits 5-6... 
           bit7=new screen, bit4=high-coord bit (right/lower half),
           bits0-3 = Y, bits5-6 = object# high bits
byte2 $0B: XXXX NNNN     bits4-7 = object# low nibble, bits0-3 = X
byte3 $59: settings/size (standard) or extended-object number (when obj# == 0)
```

- Object number `$5A` = ($0B>>4) | (($0A & $60)>>1), range $00–$3F.
- Position packed as `$57` = Y<<4 | X.
- Dispatch: `$5A == 0` → `LevLoadExtObj` → `JSL CODE_0DA100` (extended);
  else `LevLoadNrmObj` → `JSL CODE_0DA40F` (standard).
- Destination pointers (from `LoadBlkPtrs` $00BEA8, indexed by layer `$1933`):
  L1 → **$7EC800 (tile low byte) + $7FC800 (page)**; L2 → $7EB900 + $7EBD00.

## F. Object → Map16 tiles (bank 0D)

- **Extended objects** (`CODE_0DA100`): `$59` → `ExecutePtrLong` via `PtrsLong0DA10F`.
  Entry 0 = **screen exit** (`CODE_0DA512`: dest → `$19B8,X`, secondary-exit flag →
  `$1B93`, water bit → `$19D8,X`; X = screen#). Entry 1 = **screen jump**
  (`CODE_0DA53D`: `$0A & $1F` → `$1928`). Rest: single-tile objects via `CODE_0DA57B`
  + lookup `DATA_0DA548`; invisible/item-memory blocks special-cased with bit tables
  `DATA_0DA8A6+` against flags `$1F3C/$1FEE`.
- **Standard objects** (`CODE_0DA40F`): tileset `$1931` → `ExecutePtrLong` via
  `PtrsLong0DA41E` (15 tileset handlers: Normal $0DA44B, Castle $0DC190, Rope $0DCD90,
  Underground $0DD990, Ghost/Switch $0DE890 …), then object# `$5A` → the per-object
  draw-routine table (e.g. `PtrsLong0DA455` for Normal: $01–$0E singles, $0F/$10
  pipes, $11 shooter, $12 slopes, ledges/water/nets…).
- **Tile-writing primitives** (shared by all object routines):
  - `STA [$6B],Y` = tile low byte; `Sta1To6ePointer`/`StzTo6ePointer` $0DAA08/$0DAA0D
    = page 1/0 into the high buffer. Y = `$57`.
  - `CODE_0DA95B` step right (+1 column; screen-cross adds **+$1B0** and `INC $1BA1`);
    `CODE_0DA97D` step down (+$10); $0DA9D6/$0DA9EF = ±one screen (±$1B0).
  - So the buffer geometry is confirmed: **16×27 tiles = $1B0 bytes per screen**,
    column-within-row +1, row +$10, screen +$1B0.
- **Map16 16×16 definitions** live at $0D8000–$0DA098: **8 bytes per Map16 tile =
  4 × (8×8 chr, YXPCCCTT prop)** in TL,TR,BL,BR order. Tileset-specific pages at
  $0D8B70/$0DBC00/$0DC800/$0DD400 (pointer table `TilesetMAP16Loc` in bank 04).
  The in-RAM Map16 pointer table is seeded at `$0FBE` (`MAP16AppTable` $058776).
- Acts-like/behavior is *not* stored here — block behavior comes from the Map16 number
  indexing the interaction code in banks 00–03.

## G. Layer 2

- **Prebuilt background** (Layer2Ptrs bank byte == $FF, `CODE_058039`): data is an
  RLE-compressed Map16 stream in **bank 0C** (bank forced), decompressed by
  `CODE_058126` (bit-7 run flag, double-$FF terminator) into $7EB900 with page
  $10/$11 chosen by pointer (`CPX #$E8FE`). Tileset regs zeroed.
- **L2-object levels**: second pass of the §E loop with `$1933 = 1`.
- **Scroll/motion commands**: `$143E` (scroll sprite number) dispatched through init
  table `Ptrs05BC87` + main table `Ptrs05BCF0` — 00/01 auto-scroll, 02 L2 smash,
  03/08 L2 scroll, 06 L2 falls, 0B on/off-controlled, 0D fast BG, 0E sink/rise.
- Per-mode L2 build dispatch `CODE_0588EC` → `PtrsLong0588F5`; initial L2 Y from
  `DATA_05D70C` (.db $60,$90,$C0,$00).

## H. Layer 3

- Per-mode dispatch `CODE_058955` → `PtrsLong05895E`; `Layer3Ptr` (~$058FFD) is a
  mode-indexed 3-byte pointer table into the L3 tilemap blocks at $059087–$05A221
  (most modes → the default $059549; specific modes pick clouds/water/etc.).
- `CODE_058D7A` expands a Map16-coded stream into the actual tilemap: reads the 8-byte
  definitions (`LDA [$0A],Y`, base $9100 bank $0D) and writes 4 chr entries per tile.
- L3 priority comes from header byte 3 bit 7 (`$3E`).

## I. Sprite list (bank 07 data, parsed in bank 02)

- Pointer set up here ($CE–$D0, §D); first byte = sprite memory + buoyancy (§B note).
- The per-sprite 3-byte records (Y/screen, X, number), the `$1938` load-status gating,
  and the **$FF list terminator** are handled by the spawn streamer `LoadSprFromLevel`
  $02A7FB — documented in `SPRITE_ENGINE_NOTES.md` §1 (architecture) and §5.
- Editor-relevant: sprite Y precedes X in the record (opposite of objects' packing),
  and the list is sorted by screen column — the streamer early-outs past the boundary.

## J. Freespace in these banks

$FF filler: `DATA_0581BB`, `DATA_058E19`–$058FFC (bank 05); `Empty0DA0A0`–$0DA0FF
(96 bytes, right before the object engine); bank 06 tail beyond ~$06F5xx.
