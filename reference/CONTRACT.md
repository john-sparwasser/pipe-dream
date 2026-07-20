# Lunar Magic ↔ SMW ROM — Format Contract

Goal: reimplement a SMW level editor. This doc is the **data contract** between
Lunar Magic (LM) and the ROM — what to read to open a level, render it, and what
to write to save it. Focus is the core loop; overworld/title/credits are out of scope for v1.

Legend: **[CONFIRMED]** = verified in this repo's ROMs this session. **[LM-DOC]** =
stated in FuSoYa's help (`reference/lm-help/html/`). **[COMMUNITY]** = well-known SMW
lore, not yet byte-verified here — trace in Ghidra or check SMW Central before trusting.

Reference source docs preserved at `reference/lm-help/html/` (decompiled from `Lunar Magic.chm`).
Test ROMs: clean `C:\SMW\Projects\.resources\SMW.smc` (512KB headered); LM-edited
2MB ROMs under `C:\SMW\Projects\{ShaoBase,DogsOfWar,BigEye}`.

---

## 1. ROM container

- **Copier header** [CONFIRMED]: file size mod `0x8000` == `0x200` → a 512-byte header is
  present; subtract `0x200` from every file offset to get the PC/ROM address. Clean SMW =
  `524800 = 0x200 + 0x80000`. Detect, don't assume.
- **Mapping**: SMW is **LoROM** [CONFIRMED via SNES header map-mode `0x20`].
  SNES→PC: `pc = ((bank & 0x7F) << 15) | (addr & 0x7FFF)` for `addr >= 0x8000`.
  file offset = `pc + (headered ? 0x200 : 0)`.
- **SNES internal header** at SNES `$00FFC0` (PC `0x7FC0`, file `0x81C0`) [CONFIRMED]:
  21-byte title `"SUPER MARIOWORLD     "`, `$FFD5` map mode, `$FFD6` cart type,
  `$FFD7` ROM size (`log2(KB)`; `0x0B`=2MB, `0x09`=512KB), `$FFD8` SRAM size,
  `$FFDC/DD` checksum complement, `$FFDE/DF` checksum.
- **Expansion**: LM expands to 1/2/4 MB (test ROMs are 2MB = `0x200000`). Original data
  lives in the first `0x80000`; everything LM adds goes past it, RATS-protected.

## 2. RATS — ROM Allocation Tag System  [CONFIRMED live: 232 tags in ShaoBase]

Every block LM inserts past the original `0x80000` is prefixed with an 8-byte tag:
```
"STAR"            4 bytes  (ASCII 53 54 41 52 — "RATS" reversed)
size-1            2 bytes  little-endian, size of protected data (NOT incl. tag)
~(size-1)         2 bytes  = (size-1) XOR 0xFFFF   ← validity check
```
Validation: `(word4 XOR word6) == 0xFFFF`, else it's a false positive — **required**, because
random data contains the bytes `53 54 41 52` (saw overlapping fake "STAR" runs in-ROM).
Protects 1..0x10000 bytes. Free-space scan = look for runs of `0x00`, but skip over any
valid RAT. Data inside the original 512KB is **not** RATS-tagged and can be moved by LM/other
tools without warning. First real tag: file `0x80200` (PC `0x80000`), size `0x6DFF`.

## 3. Level data — pointer tables  [CONFIRMED in clean ROM]

512 level slots (`0x000`–`0x1FF`). Three parallel tables in bank `$05`:

| Data     | SNES table   | file (clean) | stride | entry 0    |
|----------|--------------|--------------|--------|------------|
| Layer 1  | `$05E000`    | `0x2E200`    | 3 B    | `54 86 06` → `$06:8654` |
| Layer 2  | `$05E600`    | `0x2E800`    | 3 B    | `74 E6 FF` → bank `$FF` |
| Sprites  | `$05EC00`    | `0x2EE00`    | 2 B    | `07 C4` → `$07:C407` (bank fixed `$07`) |

- Layer-1 pointer = 24-bit; points at the level's header+object stream.
- Layer-2 pointer: **bank `$FF`** [CONFIRMED pattern] = layer 2 is a *background image*
  (uses a BG pointer table / Map16 BG), not an object layer. Any other bank = layer-2 object data.
- Sprite pointer = 16-bit within fixed bank `$07`.
- LM relocation: for edited/new levels these entries are repointed into expanded banks
  (`$08`+), where the target data carries a RATS tag. Table location itself stays put.

## 4. Level header + object stream  [CONFIRMED via disassembly — bank 05 `LoadLevelData` $058601]

Layer-1 data begins with a **5-byte level header** (screens count, FG/BG palette, bg color,
level mode, music, sprite GFX set, FG/BG GFX set, timer, …), consumed by the loader before
the object loop. Then a stream of 3-byte objects, terminated by `0xFF`.

Standard object = 3 bytes. Verified against the actual decode at `$058601` (bytes → `$0A`,
`$0B`, `$59`; pointer `$65/$66` += 3):
```
byte1: N B B y YYYY   bit7 = new-screen flag; bits6-5 = obj# high 2 bits;
                      bits4-0 = Y position 0..0x1F (bit4 is the Y-high/page bit)
byte2: O O O O X X X X bits7-4 = obj# LOW 4 bits;  bits3-0 = X position 0..0xF (col in screen)
byte3: settings       size/extra, passed to the object handler; = ext-object id when obj#==0
```
Exact math from the ROM:
```
obj# ($5A) = ((byte1 & 0x60) >> 1) | (byte2 >> 4)          ; range 0x00-0x3F
pos  ($57) = ((byte1 & 0x0F) << 4) | (byte2 & 0x0F)        ; Y in high nibble, X in low
new screen  = byte1 & 0x80
Y high/page = byte1 & 0x10   (bumps the Map16 dest pointer down one screen)
```
Note: byte2's **high** nibble is the object-number low bits and its **low** nibble is X —
the opposite of an earlier guess. `obj# == 0` → **extended object** (`LevLoadExtObj` →
`JSL $0DA100`); otherwise **normal object** (`LevLoadNrmObj` → `JSL $0DA40F`). Loop repeats
`LoadLevelData` until the next byte is `0xFF`.

Objects decode to **Map16 tiles** written into the level's 16×16 tilemap by the bank-0D
handlers — `$0DA40F` (normal) and `$0DA100` (extended) are the next routines to trace for the
object→tile expansion (the per-object drawing logic). Sprite stream (bank `$07`) is a
separate list: 1 header byte + 3-byte sprite entries, terminated by `0xFF` (bit layout
still [verify]).

### 4a. Object → Map16 expansion  [CONFIRMED via disassembly — bank 0D]

Each object is expanded to Map16 tiles by a **two-level dispatch**:
1. Normal object handler `$0DA40F` → `$0DA415`: reads **FG tileset** `$1931` (0–E) and
   jumps through the tileset table at `$0DA41E` to a per-tileset routine (Normal, Castle,
   Rope, Underground, Switch Palace, Ghost House, Cloud/Forest, …). **The same object
   number produces different tiles per tileset** — the editor must key object rendering on
   `(tileset, object#)`, not object# alone.
2. The per-tileset routine (e.g. Normal = `$0DA44B`) does `LDX $5A` (object#), `DEX`, then
   jumps through a per-object table (Normal's is at `$0DA455`) to the object's own handler:
   coins, blocks, pipes, slopes, ledges, water, nets, bushes, etc.

Each handler writes into the level's Map16 tilemap with this primitive (`$0DA648`):
```
LDA $0C          ; the Map16 tile's low byte
STA [$6B],Y      ; Y = $57 (packed position); [$6B..$6D] = low-byte plane base
```
The tilemap is stored as **two parallel byte-planes**: low bytes via `[$6B]` and the tile's
high byte / page via `[$6E]` (`StzTo6ePointer`=page 0, `Sta1To6ePointer`=page 1). A cell's
full Map16 index = `high_plane[pos]<<8 | low_plane[pos]`. (`[$6B]`/`[$6E]` are the two planes
of one layer, set up in `LoadLevelData` from the `LoadBlkPtrs`/`LoadBlkTable2` tables — this
is distinct from the Layer-1/Layer-2 *data* pointers in §3.) Simple single-tile objects just
look the tile up: `tile = DATA_0DA548[object# - 0x10]`, plus item-memory logic that swaps a
`?`-block to the used block `$32` when already hit.

**Reimplementation reality check:** this dispatch is the bulk of the remaining work — a
finite but sizable set of per-object handlers (~0x3F objects, several tilesets, though many
share code like `$0DA8C3`). Porting them is how you turn an object stream into a tile grid;
budget for it as its own phase. The `bank_0D.asm` handlers are the reference.

### 4b. Rectangular objects — byte3 = size  [CONFIRMED via disassembly — `$0DA8C3`]

The shared handler `$0DA8C3` covers most "rectangle of one repeated tile" objects (water,
coins, cement/turn/note/throw blocks, walk-through dirt, …). It decodes **byte3 (`$59`)** as:
```
width  = byte3 & 0x0F      ; low nibble
height = byte3 >> 4        ; high nibble
```
and fills a **(width+1) × (height+1)** tile rectangle (loops are inclusive) with a single
Map16 tile taken from `DATA_0DA8B4[object# - 1]` (low byte; high byte/page = 0 if the object
index < 7, else 1). So byte3 is the object's drag-size in Lunar Magic: low nibble = tiles
wide − 1, high nibble = tiles tall − 1.

Fill order: inner loop lays tiles left→right (`INY`, with a screen-boundary fixup that adds
`0xB0` to the map pointer and bumps the page/screen counter every 16 columns); the outer loop
drops one row by adding `0x10` to the packed position `$57`. Single-tile objects (§4a) are the
degenerate `width=height=0` case with a per-object tile table (`DATA_0DA548`) instead of a
fill. Other object families (pipes, slopes, ledges, nets, diagonals) have their own handlers
with their own byte3 interpretations — those are the per-object logic still to port.

## 5. Map16 — the tile layer  [LM-DOC + file format CONFIRMED]

A level's geometry is a grid of **16×16 tiles**; each 16×16 = **4 SNES 8×8 tiles**.
`.map16` file layout (`reference/lm-help/html/info_map16_file_format.htm`):
- Header `0x40` bytes: `"LM16"`, format ver (`0x100`), game ID (1=SMW), prog ID (1=LM),
  offset+size table pointer, size X (max `0x10`), size Y, base coords, flags.
- Data block 0 = Map16: **8 bytes per 16×16 tile** = four 16-bit SNES tilemap words,
  order **TL, BL, TR, BR**.
- Data block 1 = "Act As" = 2 bytes/tile: which vanilla tile this one behaves as (`<0x200`).

Each 8×8 tilemap word (standard SNES): `vhopppcc cccccccc` →
tile number (10 bits, `cc…`), palette (3 bits, `ppp`), priority (1 bit `o`), H-flip `h`, V-flip `v`.
**This word + the GFX + the palette is everything the renderer needs per 8×8.**

## 6. Graphics (GFX)  [LM-DOC]

- Files `GFX00.bin`–`GFX33.bin`. Original game 3bpp for most (4KB); LM installs an optional
  4bpp expansion. 2bpp for `28-2B`,`2F`; Mode7 for `27`; `32` is 4bpp 23.2KB.
- ExGFX `80`–`FFF` (user-supplied), ExGFX `60-63` stored uncompressed (≤32KB, ExAnimation).
- In-ROM GFX is **compressed with LC_LZ2** (LM default) or **LC_LZ3** — this is the
  "Lunar Compress" format. LM exposes a decompressor at **`JSL $0FF900`** (A=file#,
  `$00`=24-bit dest) but for an external editor you reimplement LC_LZ2/LZ3 in your own code.
   Port from the open-source **Lunar Compress DLL** / SMW Central docs. Can't decompress
  files 32/33 via that JSL.
- Which GFX file loads into which VRAM slot per level is set by the level header's FG/BG/SP
  GFX-set bytes → a GFX-list lookup. LM's per-level GFX override (ExGFX) repoints these.

## 7. Palettes  [LM-DOC]

- SNES 15-bit BGR555 colors, little-endian words (`0bbbbbgg gggrrrrr`).
- Per-level palette derives from header palette indices into shared palette data + optional
  custom per-level palette LM stores separately. `.mw3` = raw 16-bit colors; `.pal` = YY-CHR.

## 8. Other exposed LM entry points (only relevant if hijacking, not for a clean reimpl)

- `JSL $0FF900` — decompress GFX/ExGFX to RAM. [LM-DOC]
- `JSL $03BCDC` — screen-exit "which screen is Mario on" calc. [LM-DOC]
- Map16 gameplay JSL hooks at PC `0x37890`–`0x379C0` (block-interaction custom behavior). [LM-DOC]

---

## Reimplementation plan (phased)

The whole editor is not one project; ship the read path first, it's most of the value.

1. **ROM I/O layer** — header detection, LoROM addressing, SNES-header parse, RATS
   scan/alloc. Small, fully spec'd above, testable against the 4 ROMs. *Start here.*
2. **LC_LZ2/LZ3 decompressor** — pure function, port + unit-test against `GFX*.bin`
   round-trips. Blocks all rendering; no ROM state, easiest to test in isolation.
3. **Level model read** — follow pointer tables → parse header + object stream + sprites
   into an in-memory tilemap of Map16 indices. **Verify the object bit-layout via Ghidra**
   (the one [COMMUNITY] gap) before trusting the parser.
4. **Renderer** — Map16 index → 4× 8×8 words → GFX tiles + palette → pixels. Read-only
   proof that the whole chain is correct: render a known level, compare to LM's screenshot.
5. **Edit + save** — mutate the tilemap, re-encode object stream, RATS-alloc in expanded
   space, repoint the level pointer. Save is the risky half; nail read+render first.

**Ongoing RE tool:** diff a clean ROM vs the same ROM after one known LM edit (save a single
tile change) — isolates exactly which bytes LM touches, far cheaper than reading disassembly.
Ghidra is the tie-breaker for the few undocumented encodings (object parser, LC_LZ internals).

---

## 6. Rendering assets — ROM locations  [located via disassembly; vanilla ROM]

Scoping map for the three subsystems still needed to turn a Map16 grid into pixels. All
addresses are vanilla SMW; Lunar Magic relocates/expands most of this into RATS-protected
expanded ROM for edited ROMs (see per-item notes), but the vanilla layout is the baseline
and what a clean ROM uses.

### 6a. GFX files + decompression
- **Pointer tables** (indexed by GFX file number, 0x00–0x33), three parallel byte tables in
  bank $00: low `$00B992`, high `$00B9C4`, bank `$00B9F6`. File N's source address =
  `B9F6[N]<<16 | B9C4[N]<<8 | B992[N]`. (`CODE_00BA28`.)
- **Decompressor**: wrapper `$00BA28` (looks up the pointer, sets destination) → core
  `$00B8DE`. Output goes to the GFX buffer at `$7EAD00`, then DMA'd to VRAM.
- **Format**: SMW's native LZ = **LC_LZ2** (the community format is named after it). To port,
  transcribe `$00B8DE` — that's the exact command set, no guessing. Effort: small, pure
  function, unit-testable by decompressing `GFX*.bin` and checking sizes from §6 of the CHM.
- **LM note**: LM re-inserts GFX (LC_LZ2/LZ3) and adds ExGFX (files 0x80–0xFFF); its own
  decompressor is `JSL $0FF900`. For a clean ROM, the vanilla tables above are authoritative.

### 6b. Map16 (16×16 → 8×8) definitions
- **Runtime tilemap** (object-engine output): `$7EC800` low-byte plane, `$7FC800` high-byte
  plane; full index = `high<<8 | low`. Current tile scratch = `$1693`.
- **FG tile definitions**: 8 bytes per 16×16 (four 8×8 SNES tilemap words, §5). Assembled per
  level by `CODE_0581FB` from **`TilesetMAP16Loc` at `$058000`** — a per-tileset table of
  addresses (`$8B70,$BC00,$C800,$D400,…` in bank 05) — plus shared regions `$05E55E` and
  `$05E5C8`. A per-page bitmap at `$0581BB` decides, for each Map16 page, whether it comes
  from the tileset-specific block or the shared block.
- **Effort/scoping**: this is the fiddliest asset. Reconstructing the full 0x000–0x1FF FG
  Map16 table means porting `CODE_0581FB`'s per-page source selection, not just reading one
  contiguous table. Budget real time here.
- **LM note**: LM stores an expanded Map16 table (0x000–0x3FFF) in its own RATS region and
  repoints the game to it; the `.map16` file format (§5) is that data serialized. For edited
  ROMs, read LM's Map16 table rather than reconstructing the vanilla tileset assembly.

### 6c. Palettes
- **Load routine**: `$00ABED` (`LoadPalette`) → `LoadColors`; writes BGR555 words into the
  CGRAM buffer at `$0703+`.
- **Vanilla palette data**, bank $00: back-area color table `$00B0A0`; row bases — BG
  `$00B0B0`, FG/object `$00B190`, sprite `$00B318`; layer-3 `$00B170`; shared colors 9–F
  `$00B674`; the row-offset table `DATA_00ABD3` at `$00ABD3`.
- **Header selects rows**: `$1930` BG palette, `$192D` FG palette, `$192E` sprite palette,
  `$192F` back-area color (all parsed already in `LevelHeader`). Format: 15-bit BGR555
  little-endian words.
- **Effort**: small — direct table reads + row assembly per `LoadPalette`.
- **LM note**: LM adds optional per-level custom palettes stored separately (its palette
  export is `.mw3`/`.pal`); vanilla base is the above.

**Bottom line on remaining effort:** palettes = easy, GFX decompression = small self-contained
port of `$00B8DE`, Map16 assembly = the real work (`CODE_0581FB` per-page logic) — and the
object engine (§4a/§4b, ~60 handlers) remains the largest piece. For LM-edited ROMs, the
Map16/GFX/palette all shift to LM's expanded tables, which is a separate (often simpler,
contiguous) read path worth handling explicitly.

---

## 7. Lunar Magic read path (edited 2MB ROMs)  [PARTIAL — entry points located]

The vanilla read path (§1–6) works on clean ROMs. LM-edited ROMs relocate/expand
Map16 + GFX, so rendering them needs LM's tables. Entry points found by diffing clean
SMW vs DogsOfWar/ShaoBase:

- **Map16 lookup hijack**: LM patches the Map16→8×8 consumer at `$00C17A` with
  `JSL $06F5D0` (vanilla was `REP #$20`). `TilesetMAP16Loc` ($058000) is left unchanged
  (vestigial).
- `$06F5D0`: remaps the tile through a table at **`$118000`** (2 bytes/tile, identity by
  default = tile→tile), then `JML $00F545`.
- `$00F545`: the gameplay **acts-like** handler (operates on `$1693`), NOT the graphics defs.
- **Graphics defs**: LM stores the actual 16×16 definitions (8 bytes = 4 words TL/BL/TR/BR,
  §5) in RATS-protected blocks — `STAR` tags seen at `$100000` and `$120000`. The precise
  structure (offset tables → the 8-byte-per-tile block, per LM's `.map16` layout in
  `reference/lm-help/html/info_map16_file_format.htm`) is not yet decoded.

**Remaining to render LM hacks:** decode LM's in-ROM Map16 structure (fully trace `$06F5D0`,
or do a controlled diff: change one Map16 tile's 8×8 in LM, save before/after, diff) to find
the 8-byte-def table + its index; then read GFX via LM's per-level GFX/ExGFX list (LM also
relocates/adds those). The vanilla-path renderer already works for clean ROMs.

### 7a-rev. LM extended Map16 defs — CORRECTED CONTRACT  [CONFIRMED on 7 ROMs]

**The §7a formula below ($02C2E1 → RATS block, linear (tile-0x200)*8) is a coincidence that
holds only for map16_after.smc** — in ShaoBase the $02C2E1 block is a stale FF-filled
allocation while the game reads defs from $158274. The real contract, from the in-game
consumer (LM's Map16-lookup hijack, identical code in every LM ROM):

- `$00C17A` = `JSL $06F5D0` (detector: byte $00C17A == 0x22, operand $06F5D0).
- `$06F5D0` → piecewise def-pointer math at **fixed $06F540**. Entry A = tile*2:
  - tile < 0x200 → vanilla RAM table $0FBE path (our BuildDefPointers equivalent);
    def bank = $0D, or a per-ROM bank for LM custom tilesets ($1930 >= 0x1000).
  - tile 0x200-0xFFF → **def = bank:(imm + tile*8)** where `imm` = 16-bit ADC operand at
    fixed **$06F553** and `bank` = high byte of LDY operand at **$06F556**
    (`69 imm16 A0 bank<<8` at $06F552). bank == 0 → no extended defs installed.
    Observed: map16_after $10:7008 (≡ the old $108008+(t-0x200)*8), DoW $14:7000,
    ShaoBase/BigEye $15:8274/$15:CB42, juz $10:DE94, after/gfx_after $00:F000 (= none).
  - tile >= 0x1000 → fallback blank regions (ADC #$8000/#$FFFF/#$7FFF, bank 0) +
    a tileset-specific path at $06F578 gated by a per-ROM CMP constant (0 = disabled in
    all sampled ROMs). Not implemented; cap tileCount at min(0x1000, (0x10000-imm)/8).
- Unedited tiles in the region read FF → def FFFF×4 (renders as t3FF pal7 flips); levels
  don't reference them. LM's .s16 export stores such tiles as zeros.
- Reader: `Rom.LmMap16Defs` (imm, bank), `Rom.Map16TileCount`, `Map16.LmExtendedDef`.

### 7a. LM extended Map16 table  [SUPERSEDED by 7a-rev — kept for the diff history]

Decoded by editing one Map16 tile (0x300) in LM and diffing before/after:
- LM stores the **extended Map16 definitions (tiles >= 0x200)** in a RATS block; its address
  is held at SNES **`$02C2E1`** (and `$049371`) — `$108000` in the test ROMs (data at
  `$108008`, after the 8-byte tag). Read it per-ROM (don't hardcode): `Rom.LmMap16Base`.
- Index: `def(tile) = LmMap16Base + (tile - 0x200) * 8`, 8 bytes = 4 words **TL, BL, TR, BR**.
- Word layout confirmed = §5: tile low-10 bits, palette bits 10-12, priority 13, Xflip 14,
  Yflip 15. (Edit 0x300 = `00DA 08DC 04DB 0CDD` → tiles DA/DC/DB/DD, palettes 0/2/1/3.)
- Unused extended tiles are a blank fill (`1004`×4). Tiles **< 0x200** still use the
  vanilla/tileset path (LM's handler splits on `CMP #$0200`); the tileset-specific page-1
  (0x100-0x1FF) custom table is not yet located (needs a second controlled edit on a 0x1xx tile).
- The 2-byte-per-tile table at `$118000` is the acts-like/remap (identity by default).

**Still needed to render LM hacks fully:** (1) feed extended defs into the tile cache/render;
(2) LM per-level GFX + ExGFX so the 8x8 tiles those defs reference decode correctly;
(3) the page-1 tileset-specific custom table.

### 7b. Page-1 (tileset-specific) Map16 + acts-like  [CONFIRMED via 2nd diff]

Editing tile 0x166 (copy of 0x300) with acts-as 0x130:
- Page-1 tiles (0x100-0x1FF) are **tileset-specific and written back into the vanilla bank-$0D
  table in place** (tile 0x166 -> $0D90C0 for tileset 7). So the vanilla reader
  (`BuildDefPointers` + `Definition`) reads LM's page-1 edits with NO new code.
- **Acts-like** (behavior/hitbox) is a separate 2-byte-per-tile table at **$118000**:
  `actsAs(tile) = word[$118000 + tile*2]` (identity for unedited; 0x166 -> 0x130). `Rom.ActsAs`.

Map16 READ side is now complete for LM hacks: <0x200 vanilla bank-$0D (reads edits in place),
>=0x200 LM extended table ($02C2E1->base), acts-like $118000. Remaining for rendering LM hacks:
LM per-level GFX + ExGFX (so referenced 8x8 tiles decode), then feed >=0x200 into the cache.

### 7c. LM per-level GFX bypass  [PARTIAL — located, layout not confirmed]

Controlled diff (level 0x105 Super GFX Bypass set to FG1-3=28/29/2A, BG1-3=2B/2C/2D,
SP1-4=2E/2F/30/31, AN2=01):
- LM stores per-level GFX assignments as fixed **0x20-byte records (16 x 16-bit words)** in its
  expanded data region (edited record for level 0x105 at SNES **$10CDA0** in gfx_after.smc).
  Entries are 16-bit (ExGFX-capable). Sentinels: **0x7F = slot off/use tileset default**,
  0xFFFF = end/unused; some words carry high-bit flags (AN2 had 0x8000, SP4 0xE000).
- NOT yet confirmed: exact word->slot order, table base + per-level index. The ascending test
  values (a) collided with the $118000 identity table (false positive) and (b) can't
  disambiguate slot order. REDO with DISTINCT non-sequential per-slot values, e.g.
  FG1=0x12 FG2=0x1A FG3=0x05 BG1=0x33 BG2=0x21 BG3=0x08 SP1=0x30 SP2=0x1F SP3=0x0C SP4=0x25.
- Then: locate LM ExGFX pointer table + data (set one slot to an ExGFX file like 0x100, diff),
  and integrate into the renderer (FG slots -> VRAM -> tile decode).

### 7c (confirmed). GFX bypass record layout

2nd diff with distinct per-slot values decoded the 0x20-byte (16-word) record:
```
w0-3  : constant (2B 2A 29 28 default) - not the bypass slots (separate field, TBD)
w4    : AN2 (animated GFX 2), bit15 = enabled     w10 : FG2
w5    : AN1 (animated GFX 1)                       w11 : FG1
w6    : BG3      w7 : BG2      w8 : FG3            w12 : SP4 (bits15-13 flags)
w9    : BG1                                        w13 : SP3   w14 : SP2   w15 : SP1
```
Each word 16-bit (ExGFX-capable); low byte = GFX/ExGFX file #; 0x7F = slot uses tileset default.
Level 0x105 record at SNES $10CDA0 (gfx_after.smc); records 0x20/level; sub-table base ~$10AD00
(tentative: rec = base + level*0x20). Still to do: map named slots -> the 4 FG VRAM regions the
renderer uses (tiles 0x000-0x1FF), locate ExGFX pointer+data (diff a slot set to ExGFX 0x100),
then wire GFX into the LM render path.

### 7d. GFX bypass + ExGFX — COMPLETE READ CONTRACT  [CONFIRMED via ASM trace, validated on 4 hacks]

Traced LM's installed loader in gfx_after.smc (no further LM round-trips needed). LM hooks the
vanilla level-GFX loader: `$00AA50` = `JSL $0FF780` (22 80 F7 0F — identical in DogsOfWar,
ShaoBase, BigEye, juz → **bypass-installed detector**); a decompress call at $00AA6C is
redirected $00BA28 → $0FF160.

**Record fetch** (`$0FF7F0`): `LDA $FE(=level+1) : BEQ off : DEC : ASL x5 : TAX :
LDA $10AD08,X` → **record = tableBase + level\*0x20**, tableBase baked into the BF operand
(per-ROM: $12AD08 DogsOfWar, $10AD08 ShaoBase/BigEye, $11AD00 juz). Locate per-ROM by
signature scan: `A5 FE F0 ?? 3A 0A 0A 0A 0A 0A AA BF <base:3>`.
Record long-ptr cached at $7FC006/8; enabled-flag byte at $7FC009 (0x42 = enabled).

**True record layout** (starts 8 bytes AFTER the §7c-observed offset; the "constant 2B 2A 29 28"
words are the previous record's tail): 16 words:
```
w0 = AN2 (bit15 = BYPASS ENABLED flag)   w1 = AN1
w2 = BG3   w3 = BG2   w4 = FG3   w5 = BG1   w6 = FG2   w7 = FG1
w8 = SP4   w9 = SP3   w10 = SP2   w11 = SP1   w12-15 = tail (TBD, constant)
```
So level-0x105's record in gfx_after.smc = $10CDA8, not $10CDA0.

**Named slot → VRAM/renderer mapping** (from LM help level_change_graphics.htm "FG1=14" +
OBJECTGFXLIST): FG1→slot0 ($0000, 8x8 tiles 0x000-07F), FG2→slot1 ($0800, 0x080-0FF),
BG1→slot2 ($1000, 0x100-17F), FG3→slot3 ($1800, 0x180-1FF). I.e. renderer FG files =
[w7, w6, w5, w4] = [FG1, FG2, BG1, FG3], replacing OBJECTGFXLIST[tileset*4+0..3].

**File# → data resolver** (`$0FF903`, slot word AND #$0FFF):
- 0x00-0x33: vanilla ptr tables $00B992/B9C4/B9F6 (unchanged).
- 0x7F: slot skipped (keep tileset default / don't load).
- 0x80-0xFF: 3-byte ptr at **$0FF600 + (n&0x7F)*3** (fixed address, all 4 hacks).
- 0x100-0xFFF: 3-byte ptr at **exgfxBase + (n-0x100)*3**, exgfxBase baked per-ROM
  ($128008 DogsOfWar, $108008 ShaoBase/BigEye/gfx_after, $118000 juz). Signature scan:
  `38 E9 00 01 85 8A 0A 18 65 8A AA BF <base:3>`.
- All pointers → LC_LZ2-compressed GFX (loader tail JMLs into vanilla decompressor $00BA47
  with $8A-8C = pointer). 0x000000/0xFFFFFF entry = file not inserted.
- GFX file = 0x80 tiles; ExGFX may be 3bpp (0x80*24 = 0xC00 decompressed) or 4bpp
  (0x80*32 = 0x1000). Pick bpp from decompressed length, as we already do for vanilla files.
  (AN2 is special: up to 0xD0 tiles / 0x1A00 bytes 4bpp — not needed for FG rendering.)

Tooling: `tools/dis65816.py <rom> <snesHex> [count]` — minimal 65816 disassembler used for
the trace.

### 7e. LM per-level custom palettes  [CONFIRMED via .pal cross-check, 3 hacks]

LM leaves `LoadPalette` ($00ABED) and all vanilla color tables untouched. Its palette engine
hooks the caller: vanilla `$0095E9: JSR UploadSpriteGFX : JSR LoadPalette` becomes
`JML $0EFC50 : JSR LoadPalette : JML $0EFC80` — **byte $0095E9 == 0x5C is the
palette-engine-installed detector** (present in DogsOfWar/ShaoBase/BigEye; absent in
juz/gfx_after/vanilla). The hooks save/restore $7E2000 (LM's relocated CGRAM staging buffer)
around the vanilla loads; the custom palette itself is applied from ROM data:

- **Pointer table at fixed `$0EF600`** (bank-$0E twin of the ExGFX table's $0FF600):
  3 bytes/level, 0x200 entries. `0x000000` or `0xFFFFFF` = level has no custom palette
  (vanilla assembly applies). Every non-zero entry in all 3 hacks points at a valid RATS blob.
- Entry → RATS `STAR` tag + 0x202 bytes of data: **word 0 = back-area color**, then
  **256 BGR555 words = a complete CGRAM image** (replaces the whole vanilla §6c assembly).
  Each row's color 0 is stored as 0 (transparent in-engine).
- Cross-checked against LM's own `.pal` exports (RGB888, 0x300 bytes): ROM words ==
  `(b>>3)<<10|(g>>3)<<5|(r>>3)` of the .pal triplets for all colors except the 16 row-0
  slots (zeroed in ROM) — DogsOfWar levels 0x107/0x115.
- Reader: `Rom.LmCustomPalette(level)`, applied by `Palette.Load(rom, header, level)`.

NOTE while investigating: DoW level 0x105 has NO custom palette and no bypass record — its
washed-out render is expected (mostly-empty level slot), not a palette bug.

## 9. Object engine — full dispatch contract  [CONFIRMED from disassembly + LM ASM traces]

### 9a. Tileset dispatch
`$0DA41E + tileset*3` → per-tileset dispatcher (5 distinct: Normal $0DA44B for tilesets
0/7/C, Castle $0DC190, Rope $0DCD90 for 2/6/8, Underground $0DD990 for 3/9/A/B/E,
Ghost/SP $0DE890 for 4/5/D). Per-object handler table = dispatcher + 0xA, entry (obj-1)*3.
Handlers are SHARED across tilesets (theming comes from per-tileset Map16 defs), so the
editor dispatches on handler ADDRESS (ObjectEngine.Handler). ~30 vanilla handlers ported;
remaining exotic per-tileset ones fall back to magenta markers.

### 9b. Extended-object dispatch
ONE global table `$0DA10F + ext#*3` (0x00-0xFF; dispatcher $0DA106). Ported by address:
$0DA57B/$0DA64D single-tile (DATA_0DA548[ext-0x10], page 1 when idx >= 0x13), $0DA68E
midway bar (035 at x-1 + 038), $0DCE94 line-guides 0x51-54, $0DCEA6/$0DCEC0/$0DDA80
vertical pairs, $0DDA68 deco 0x75-7B.

### 9c. Screen jumps (parse-critical)
Vanilla ext 0x01 ($0DA53D): screen := b1 & 0x1F (the Y bits). LM ext 0x03 ($0DE1E0):
screen := b2. Screen sequences are therefore NON-MONOTONIC; anything that walks "screen
boundaries" (save-merge) must handle repeated screens.

### 9d. LM reserved objects 0x22/0x23/0x26/0x27/0x28/0x29 (all 5 tileset tables repointed
identically; vanilla had placeholder $0DB3E3)
- 0x22 → $0DF08A: Direct Map16 page-0 form — 4 bytes (b1,b2,size,tileLow), tile = 0x000|low.
- 0x23 → $0DF08E: DM16 page-1 Form A (4 bytes) — as §8.
- 0x26 → $0DF130: no-tile directive (pokes $0DDA; music-related).
- 0x27 → $0DF150 / 0x29 → $0DFF50 (BG pages, +0x40): VARIABLE LENGTH —
  b1,b2,size,pageByte,tileLow (5 bytes) + 1 run byte if pageByte bit7 + 1 height-override
  byte if bits7+6. bit7 also switches width to (size & 0x7F). Obj 0x29 writes the LAYER-2
  plane (no layer-1 tiles).
- 0x28 → $0DF160: no-tile directive (entrance/position, $0F31-33).
- LM ext obj 0x02 ($0DE1B0): secondary exit — consumes 2 EXTRA stream bytes (exit word).
Parse lengths must match exactly or the whole object stream desyncs (DoW builds levels
almost entirely from extended 0x27 forms).
