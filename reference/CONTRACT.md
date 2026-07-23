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
OBJECTGFXLIST): FG1→page0 (8x8 tiles 0x000-07F), FG2→page1 (0x080-0FF), BG1→page2
(0x100-17F), FG3→page3 (0x180-1FF). I.e. renderer FG files = [w7, w6, w5, w4] =
[FG1, FG2, BG1, FG3], replacing OBJECTGFXLIST[tileset*4+0..3].
**BG2/BG3 = pages 4-5** (0x200-0x27F / 0x280-0x2FF), words [w3, w2]. These are the two
EXTRA background slots LM's VRAM patch adds (option_vram.htm: "anything in slots BG2 and
BG3 will not be loaded" without it) — no vanilla OBJECTGFXLIST entry, so they come ONLY
from the bypass (0x7F/absent → blank). FgTiles now loads 6 background pages; Fetch indexes
`tile>>7` over 8 pages (6-7 = the animated region). Verified: ShaoBase 105 (BG2=ExGFX 0x310,
BG3=0x311) renders its block graphics correctly; before, tiles 0x200+ wrapped to pages 0-3.
AN1/AN2 (the animated ExGFX source) still not applied to the overlay (separate gap).

**File# → data resolver** (`$0FF903`, slot word AND #$0FFF):
- 0x00-0x33: vanilla ptr tables $00B992/B9C4/B9F6 (unchanged).
- 0x7F: slot skipped (keep tileset default / don't load).
- 0x80-0xFF: 3-byte ptr at **$0FF600 + (n&0x7F)*3** (fixed address, all 4 hacks).
- 0x100-0xFFF: 3-byte ptr at **exgfxBase + (n-0x100)*3**, exgfxBase baked per-ROM
  ($128008 DogsOfWar, $108008 ShaoBase/BigEye/gfx_after, $118000 juz). Signature scan:
  `38 E9 00 01 85 8A 0A 18 65 8A AA BF <base:3>`.
- All pointers → LC_LZ2-compressed GFX (loader tail JMLs into vanilla decompressor $00BA47
  with $8A-8C = pointer). 0x000000/0xFFFFFF entry = file not inserted.
- GFX file = 0x80 tiles. **Bit depth is ROM-WIDE, not per-file** (CORRECTED): the earlier
  "pick bpp from decompressed length" heuristic is WRONG for PARTIAL ExGFX — a 0x800-byte
  file is 128×2bpp, 85×3bpp, OR 64×4bpp, indistinguishable by size. SMW stores every GFX/
  ExGFX at one depth: vanilla = 3bpp (full file 0xC00), and Lunar Magic re-normalizes ALL
  graphics to 4bpp on save (full file 0x1000) — but NOT every LM-touched ROM (a ROM edited
  only for e.g. the GFX bypass keeps 3bpp base GFX). So `Gfx.RomBpp(rom)` probes a full base
  file (GFX00) once and every slot decodes at that depth; full files were always right, only
  partial ExGFX were garbled. (AN2 is special: up to 0xD0 tiles / 0x1A00 bytes — not needed
  for FG rendering.)

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

## 10. Layer 2  [CONFIRMED from bank 05 loader]

Layer-2 pointer (§3): bank != $FF → **object mode**: same stream format as layer 1 with its
OWN 5-byte header copy, which the game skips ($0583FB "address +5"); screen counter resets
to 0. Only loaded when the header's level mode is a layer-2 mode (not in
{0,0A,0C,0D,0E,11,1E}) — the editor just renders it whenever present.

Bank == $FF → **background image** ($05803B): data at `$0C:(ptr&FFFF)` (all vanilla BGs in
bank $0C), decoded by the RLE at $058126 into a tile map of two 16-wide × **27-tall**
screens (0x1B0 tiles/screen — same screen stride as the layer-1 tilemap planes; a 0x200
stride shows the 2nd screen shifted up 5 tiles), repeating horizontally:
```
cmd  = byte;  cmd==FF && next==FF → end
bit7 set: run  — next byte repeated (cmd&0x7F)+1 times
bit7 clr: copy — next cmd+1 bytes literal
```
Buffer initialized to tile 0x25. High/page byte for ALL tiles = 0, or 1 when
`(ptr&FFFF) >= 0xE8FF` ($058046). **BG Map16 defs are at fixed `$0D9100 + idx*8`**
(idx = page<<8|low, 0x000-0x1FF) — the $0FBE refill at $05819B. Same word format as §5.
Readers: Level.ParseLayer2 / Level.DecodeBgImage / Map16.ComposeAllBg; ComposeLevel draws
backdrop → layer 2 → layer 1. (Layer-2 vertical positioning/scroll modes not modeled; BG
drawn from y=0.)

## 11. Sprites  [CONFIRMED from disassembly + LM ASM trace]

Pointer table §3 ($05EC00, 2 B/level, bank fixed $07). Stream ($05D8F9 + $02A82C):
```
header    1 byte   bits5-0 = sprite memory ($1692), bits7-6 = buoyancy ($190E)
entry     3 bytes  b1 = YYYYEEsy  (Y = (y<<4)|YYYY, EE = extra bits, s = screen bit4)
                   b2 = XXXXSSSS  (X nibble within screen, screen = (s<<4)|SSSS)
                   b3 = sprite number (>= 0xE7 → scroll command, $02A866)
0xFF      terminator (lead byte)
```
**LM/PIXI extra bytes**: LM hijacks the sprite-advance (vanilla `INY INY INX` at $02A846 →
JML into LM's relocatable code bank). Entry size = byte table at
**sizeBase + (EE<<8 | sprite#)** (0x400 bytes, per-ROM; includes the 3 base bytes; vanilla
entries = 3, customs 4-13). Locate per-ROM via signature
`4A 4A 29 03 EB C8 C8 B7 CE 88 88 08 C2 10 DA AA 98 18 7F <base:3>` (Rom.LmSpriteSizeBase;
absent in clean/juz ROMs → fixed 3). Sizes MUST be honored or the stream desyncs (DoW).
Readers: SpriteData.Parse/Encode (byte-identical round-trip incl. extra bytes);
SpriteData.DrawOverlay renders badge markers (green = sprite, orange = scroll command).
Sprite GFX rendering (real tiles per sprite) is out of scope for now.

## 12. Tile animations — the header-dependent Map16 appearance  [DECODED, overlay not yet implemented]

Why some Map16 tiles look different per header (LM: "The FG/BG index also alters which
animated tiles are used"): 8x8 VRAM tiles in the ranges below are re-uploaded EVERY FRAME
by NMI DMA; their static content in the GFX files is placeholder (e.g. the 'COIN' text seen
in DoW renders). LM displays animation frame 0. Decoded from `CODE_05BB39` (bank 05):

- Per frame: group = frame&7, phase = (frame&0x18)>>3 (0-3). Each group drives 3 DMA slots.
- **VRAM dests**: word table `$05B93B + group*6` (3 words) → each slot uploads **4
  consecutive 8x8 tiles** (0x80 bytes 4bpp) at word/16. Groups 0-6 cover 8x8 tiles
  0x40-0x83, 0xDA-0xDD, 0xEA-0xED (dest 0 = slot unused).
- **Animation id** = group*3+slot, modified by behavior byte `$05B96B[group*3+slot]`:
  0 = as-is; 1 = POW-dependent (`$05B97D`, +0x26 when POW active — editor: inactive);
  else **+= `$05B98B[tileset]`** ← the header-dependent part
  (offsets per tileset 0-D: 0,5,10,15,20,20,25,20,10,20,0,5,0,20).
- **Frame source**: `AnimatedTileData` ($05B999)[id*8 + phase*2] = 16-bit RAM address in
  bank $7E of the 0x80-byte 4bpp source block. Frame 0 (= LM's static view) = first word.
  Observed source range $6D80-$AC20.
- Map16 DEFS also animate for non-0/7 tilesets: CODE_0581FB leaves tiles 0x1C4-7/0x1EC-F
  def-animated via source list $05E55E ($05E5C8 + static defs $0D8A70 for tilesets 0/7 —
  already ported); anim def frames written per-frame elsewhere (targets cached $1430/$1431).

**REMAINING (next session)**: the $7E source-buffer layout. GFX32/33 are NOT in the vanilla
pointer tables ($00B992 stride is 0x32; no `LDY #$32/33 : JSL $00BA28` exists) — find their
loader/pointers + the 3bpp→4bpp-to-RAM conversion that fills $7E:6D80-$AC20, OR calibrate
empirically: decompress GFX33, locate coin/turn-block frames visually (`--gfxsheet`), solve
the base from id0 srcs (9500/9700/9900/9B00) and id3 (9D80..A380). Then overlay frame-0
tiles into FgTiles at compose time. LM bypass AN2 slot (§7d w0) replaces GFX33 for customs.

### 12a. RESOLVED: the animated-GFX buffer  [CONFIRMED + IMPLEMENTED]

`CODE_00B888`: GFX32+33 are NOT in the pointer tables — they decompress from a fixed
pointer whose operands live at **$00B88B (16-bit) / $00B890 (bank)** (vanilla `$08BFC0`;
read per-ROM — LM repoints it when Mario/animated GFX are edited). Decompressed 3bpp
(0x2400 bytes) to $7E2000, then expanded 3bpp→4bpp backwards to occupy
**$7E7D00-$7EACFF**. The expansion zero-fills plane 3, so an AnimatedTileData address A
maps to 3bpp source tile `(A - 0x7D00)/0x20` (24 bytes/tile) — no 4bpp step needed.
NMI consumer $00A3A4: 0x80 bytes (4 tiles) per slot; dest word $0800 is special-cased
into two 0x40 transfers at $0800 and $0900 (tiles 0x80,0x81,0x90,0x91).
Sources below $7D00 (e.g. id 5, berries) come from other RAM — skipped (rare).
Implemented: Gfx.FgTiles.OverlayAnimatedTiles — frame-0 tiles overlaid at load.

### 12b. Second animated blob — berries etc.  [CONFIRMED + IMPLEMENTED]

$00B8D7 (fall-through after the GFX32 conversion): a second blob decompresses from
bank:(operand at $00B8D8) — vanilla $088000 — to $7E2000 — the decompressor writes via [$00],Y
without advancing $00, so blob2 lands over blob1's spent 3bpp source. It is stored RAW
4bpp (0x5D00 bytes): DMA-visible RAM = $2000-$7CFF blob2 (berries at id 5's $6D80 =
offset 0x4D80), $7D00-$ACFF converted blob1. Overlay resolves >= $7D00 from blob1 as
3bpp, else >= $2000 from blob2 as 4bpp.

## 13. Object expansion via emulation  [IMPLEMENTED — vanilla-layout ROMs]

Instead of hand-porting every bank-0D handler, the editor EXECUTES the ROM's own
`LoadLevelData` ($0585FF) in a small 65816 interpreter (src/Rom/Cpu65816.cs): RAM banks
$7E/$7F emulated, low-RAM mirror for banks 00-3F/80-BF, ROM via LoROM map, PPU writes
ignored. Setup: planes filled 0x25/0x00, $65-$67 = layer data (+5), $1925/$1931/$1930/
$192B from the header, $5B from VerticalTable, $1933 = layer. Result read back through
the loader's own plane tables ($00BEA8/$00BEAC → per-mode screen tables, 3-byte base per
screen; screen layout = rows 0-15 at +0x000, rows 16-26 at +0x100).
Tiles are exact for every object/tileset by construction (slopes, mud edges, context
blends — all previous approximations retired on this path).

**TODO (LM-saved ROMs)**: LM patches the loader and rebuilds plane pointers at runtime
(static tables dead; observed screen-0 low plane at $7E:0000 when entered without LM's
init). Enter via the full chain ($05801E: BG clear + decode + LoadLevel, RTL exit,
GameMode >= 0x22 to skip sprite init) and capture $6B/$6E per screen with a CPU write
hook, then extraction needs no table knowledge at all. Until then LM ROMs use the ported
engine (hand-written handlers, kept as fallback).

### 12c. Global palette exanimation  [CONFIRMED + IMPLEMENTED]

The animation NMI tail ($00A418) rewrites **CGRAM color 0x64** (palette row 6, color 4)
every 4 frames from `MorePalettes` ($00B60C): 8 BGR555 words, byte offset
`(frame & 0x1C) >> 1` — a gold→white glint cycle (02DF 035F 27FF 5FFF 73FF 5FFF 27FF 035F).
Applied in Palette.Load per display phase (offsets 0/4/8/12), including on top of LM
custom palettes (the NMI write happens regardless).

### 12d. LM ExAnimation / AN1-AN2 slots  [INVESTIGATED — NOT the vanilla path]

The bypass AN1 (record w1) / AN2 (w0) slots are the source GFX for LM's rewritten
ExAnimation engine, NOT the vanilla animated-tile system in §12/§12a. Traced from the LM
loader (fixed $0FF780 region, hand-decoded — the disassembler needs Python which the build
box lacks):
- The bypass record reader is at $0FF7F0 (`LDA.l <bypassBase>,X`, X = level*0x20); each slot
  word is masked `AND #$0FFF` → file number, then decompressed via $0FF900.
- $0FF900 resolves file→source EXACTLY as our `Gfx.SourceSnes` (vanilla $00B992/C4/F6,
  ExGFX80 $0FF600, ExGFX100+ per-ROM `LmExGfxBase`) then JMLs the vanilla decompressor
  $00BA47 — confirms SourceSnes is correct, including for AN files.
- The AN/extended-animated loader (~$0FF8B8) sets the decompress destination to **$7E:AD00**
  (`LDA #$AD:STA $01 / LDA #$7E:STA $02`) — matches level_extend_ani.htm ("$AD00 = start of
  the extended animated tile area").
- BUT the vanilla frame table `AnimatedTileData` ($05B999) that §12a's overlay reads tops out
  at **$AC20** — NOTHING in it points at $AD00. So loading AN1/AN2 into the §12 overlay is a
  NO-OP: the vanilla path never samples the extended area. AN tiles are consumed only by LM's
  ExAnimation slot engine, whose per-level/global frame references (+ triggers, rates, line/
  palette types — changes.htm) live in an as-yet-undecoded LM data area. Displaying AN
  animation therefore requires decoding that slot format (a controlled before/after diff),
  not wiring AN into the vanilla overlay. (The OLD "Extend Animated Tile GFX" feature did use
  $AD00 via manually-edited $05B999 entries — a ROM using it WOULD show $05B999 >= $AD00.)

**ENGINE LOCATED (foothold for the decode).** Diffing clean SMW vs ShaoBase (which has global
ExAnimation) in the NMI animation code: LM overwrites the vanilla DMA setup at **$00A390**
(was `REP #$20 : LDY #$80 : STY $2115`) with **`JSL $138170`** (expanded bank $13). $138170 is
the ExAnimation engine — it performs the animated-tile VRAM DMA itself (sets DP=$4300, writes
$2116/$22/$420B from the `$0D7x` params) and layers its own slots on top; the vanilla dispatch
$05BB39 is UNTOUCHED.

ENGINE MAP (traced with --disasm on ShaoBase, global ExAnimation):
- **$138170** (NMI DMA): blits the vanilla animated tiles (from the `$0D7x` params) then the
  ExAnimation slots. Reads **8 per-slot DMA-param records in RAM at `$7FC0C0`, stride 7**
  (ctrl word @+0, VRAM dest word @+2, 3-byte source ptr @+4). These are already RESOLVED for
  the current frame — the NMI half only blits.
- Color-0x64 glint lives here too ($138487: CGRAM 0x64 ← `$00B60C`+((frame&1C)>>1), same as
  §12c) — gated by a per-level "disable color-64 anim" flag at **`$7FC00A`** (bit7).
  ExAnimation frame counter **`$7FC004` = `$7FC019` >> 3**.
- The `$7FC0C0` param records are filled by a per-tick PROCESSOR (bank $13/$10, RATS blocks;
  init/clear routine at **$10F2F9** zeroes all 8 slots) that reads the ROM slot DEFINITIONS
  and dispatches per slot TYPE via a handler-offset table at **~$10F32D** (~12 entries — the
  "new line/palette types" of changes.htm). The processor writes params via indexed stores.
- STILL UNDECODED: the ROM slot-definition table (base + per-type record: dest tile, frame
  source list, rate, trigger) and where per-level vs global slots live. Because there are ~12
  type variants, decode this with a CONTROLLED per-level before/after (one slot, distinctive
  values) rather than reading ~12 handlers blind — same method that cracked the GFX bypass.

### 12e. LM ExAnimation slot record  [DECODED via controlled diff]

Decoded from exanim_0..3 in .resources (one 8x8-tile animation on level 0x105; dest and
frame-count varied one at a time). Each ExAnimation slot is a variable-length record inside
a small RATS block (LM relocates it on every save):

  +0x00 word   type/config       (0x0001 for the plain 8x8-tile line — TENTATIVE)
  +0x02 word   trigger           (0xFFFF = none/periodic — TENTATIVE)
  +0x04..+0x0B                    runtime state, zeroed on save (current frame/timer — TENTATIVE)
  +0x0C word   frameCount - 1     (CONFIRMED: 3 frames→0x0002, 4 frames→0x0003)
  +0x0E byte   destTile / 0x10    (CONFIRMED: dest 0xA0→0x0A, 0x40→0x04; dialog range 0x00-0xAA)
  +0x0F...     frame list         (CONFIRMED: one 16-bit $7E RAM source addr per frame)

Frame source addr = **$7D00 + (srcTile - 0x600) * 0x20** (CONFIRMED on 4 values: 0x601→7D20,
0x655→87A0, 0x6AA→9240, 0x633→8360). So source tile 0x600 = $7E:7D00 (the animated-GFX source
area, §12a), 0x20 bytes/tile 4bpp — the ExGFX 60-63 uncompressed source data loaded there.
**PER-LEVEL TABLE (CONFIRMED):** a table at **$109278**, 3 bytes/level (24-bit pointer),
sentinel `FF 00 00` = no ExAnimation. `table[level]` → the level's slot record. Verified:
only level 0x105 populated across exanim_1/2/3, entry = $10A92E / $10AAD3 (tracks the record
as it relocates). $109587 (= $109278 + 0x105*3) was the level-0x105 entry. So the full read
chain is: `ReadValue($109278 + level*3, 3)` → record → §12e fields → frame src `$7D00 +
(tile-0x600)*0x20`.

IMPLEMENTED: ExAnimation.ReadLevel / ParseSlots (src/Rom/ExAnimation.cs) + `--exanim` dump;
record header confirmed by the $108700 level-setup reader (LDA $109278,X): +0 = slot count,
+2/+4 = AND/OR masks into $7FC0FC, +6 = 16-bit selector filling $7FC070, and it sets
$7FC000 = record+8 = the slot array. Per-slot (slot-relative): +0/+2 unknown words, +4 =
frameCount-1, +6 = dest byte, +7.. = frame src addrs. Verified against exanim_1/2/3.

DEST PINNED (exanim_4, dest 0x2A -> word $02A0): +0C is a BYTE (frameCount-1) and +0D a WORD
(dest VRAM word = dialog*0x10); FG tile = word/16 = dialog value. Same word/16 convention as
vanilla animation. OVERLAY IMPLEMENTED in FgTiles.OverlayAnimatedTiles: for each slot,
Overlay(DestTile, FrameSrcAddrs[phase % FrameCount]). Verified live on exanim_1 (tile 0xA0
cycles across phases) and gated (a level without ExAnimation is untouched).

REMAINING: (1) $109278 fixed vs per-ROM (verify on other hacks; signature-scan if it moves);
(2) multiple slots per level + GLOBAL list (ShaoBase) untested; (3) custom ExGFX 60-63 loaded
into the $7E:7D00 source model — today only standard animated GFX resolves, so custom-source
ExAnimation shows the GFX32/33 tile at that offset as a placeholder; (4) slot header words
+0/+2 (0x0002/0x0001 — likely tile-count/type) for multi-tile ExAnimation types.
Tooling: --disasm, --diff, --exanim.

## 14. Sprite graphics via OAM capture  [IMPLEMENTED v2]

No unified sprite→tile table exists; each sprite's look comes from its graphics routine.
The editor emulates the ROM's own load flow per sprite (slot 0 seeded: $9E num, $E4/$14E0
+ $D8/$14D4 position, $15EA=$30 OAM index, $64 priority, $187B extra bits, screen boundary
$1A/$1C near the sprite):

1. `InitSpriteTables` ($07F7D2, RTL via CallLong) — loads tweaker/palette RAM ($1656-$1686,
   $15F6) from ROM; without it every sprite draws with palette 0.
2. Status $14C8 seeded **1** (init) — or **9** (stationary) for shells, see below.
3. Up to 16 frames of `HandleSprite` ($018127) — the per-slot STATUS dispatcher
   (0→erase, 1→CallSpriteInit $018172, 8→CallSpriteMain $0185C3, 9/A/B→stunned/kicked/
   carried at $01953C/$019913/$019F71). Calling CallSpriteMain directly misses every
   carryable: POW's number-dispatch main ($01E75B) only handles the message-box timer —
   the visible POW draws from the status-9 handler. Each frame: OAM Y-fill $F0, $13/$14++,
   position re-pinned; first frame that yields OAM tiles wins. A frame that overruns the
   instruction budget (wait loops, e.g. castle fireball init) is skipped, not fatal.

**DBR**: the sprite engine runs with **data bank = 1** in-game (PHK/PLB in the bank-1
sprite loop). Absolute table reads — `SprTilemap` $019B83, `SprTilemapOffset` $019C7F,
every bank-1 tile/anim table — silently read bank-0 garbage with DBR=0, producing
"close but wrong" tiles (koopas one-tiled, shells drawing GFX00's P-switch). Preset
DBR=1 before HandleSprite frames.

**Ground seeding**: the sprite block probe ($019441) treats any position whose screen
number >= **$5D (screens in level)** as "no blocks" — unseeded, nothing is ever solid,
so walkers run their in-air path forever: 2px/frame gravity sag, stay-on-ledge direction
flip ($018B98), walk animation frozen on the standing pose. Seed $5D and write solid
tiles (Map16 $130: $7EC800=$30/$7FC800=$01) a few rows below the sprite. The $C800
layout matches the probe's ROM pointer tables DATA_00BA60/BA9C (horizontal: screen*$1B0,
rows 16-26 at +$100) and DATA_00BA80/BABC (vertical: band*$200, right 16 columns at
+$100). Horizontal bottom bound is a hardcoded $01B0 ($0194D6), not $13D7.

**Mario seeding**: `SubHorizPos`/`SubVertPos` ($01AD30/$01AD42) read Mario from the
**$D1-$D4 mirrors**, not $94-$97 — seed both. Mario is parked at sprite X **-0x140**:
truly LEFT (Banzai Bill's init self-erases when Mario is to its right, $01838B; matches
LM's face-left convention) yet aliasing to -0x40 in the LOW byte, because proximity gates
like Monty Mole's ($01E2E3) compare only the low byte of the 16-bit distance.

**Sprite-list number classes** ($02A866-$02A8D8): 00-C8 normal; C9-CA shooters ($1783
system, badge); CB-D9 generators ($18B9, badge); **DA-DD/DF koopa shells** — loaded as
sprite (num-$DA)+4 with initial status 9 ($02A97E; $1EEB can color-swap 04↔07/05↔06);
DE = 5 Eeries, E0 = 3 chain platforms, E1-E6 cluster specials (badge); E7+ scroll
commands (badge).

Tiles resolve through SP1-4 (SPRITEGFXLIST $00A8C3 / bypass words 11-8), palettes = CGRAM
rows 8-F, 16x16 assembly T/T+1/T+16/T+17 with flip-quadrant swap; entries draw in reverse
OAM order. Remaining badges are sprites that are genuinely invisible in-game at rest
(invisible mushroom C7, warp blocks 8E, static net door 54) — plus hidden Monty Moles,
which draw their in-ground dirt pose.

### 14a. Sprite OAM specifics  [verified against bank_01]

- Sprite GFX routines write tiles to the **$0300 half** of the $0200-$03FF OAM buffer
  (per 4-byte entry: X, Y, tile, YXPPCCCT props). The size table **$0420-$049F holds ONE
  byte per OAM entry** — `FinishOAMWriteRt` ($01B7BB) LSRs the $0300-relative byte offset
  twice and indexes $0460 = $0420 + entry index (consecutive per-tile stores at $01BC08
  confirm). bit1 = 16x16; **bit0 = 9th X bit, meaning the tile hangs off the LEFT edge**
  (subtract 0x100, don't add — set via DEX/offscreen check at $01B7F5-$01B811). Unwritten
  entries must default to 0 (8x8) or garbage neighbour tiles appear.
- **Priority**: earlier-drawn OAM tiles sit in FRONT of later ones regardless of the PP
  priority bits (PP only orders sprites vs BG layers). We draw the captured list in reverse
  so slot order wins — matches.
- **Palette**: OAM CCC addresses only the SECOND half of CGRAM → sprite palettes are rows
  8-F (colour base 0x80 + CCC*16). The CCC value itself comes from $15F6,x, loaded by
  InitSpriteTables — hence step 1 above.
