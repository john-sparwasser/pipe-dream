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
