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

## 0. Round-trip with Lunar Magic  [REQUIREMENT — currently ONE-WAY]

**A ROM must survive being edited in either tool and reopened in the other.** Edit in
pipe-dream, open in LM, edit there, come back — seamless, both directions. This is a
product requirement, not an aspiration: LM is the incumbent and nobody adopts an editor
that strands their hack. It holds until pipe-dream grows features LM has no representation
for, which is far enough off that nothing should be traded away for it now. When that day
comes the break must be a deliberate, announced, opt-in divergence — never a silent one.

**Use LM's rails. Do not route around them.** When a feature exists in Lunar Magic, ours goes
on the same hook sites, with the same flag layout, reading and writing the same structures —
even when a private mechanism would be quicker to build and easier to test. The failure mode is
seductive and has recurred: find LM in the way (a site it overwrites, a table at a per-ROM
address, a decode half-finished), and route around it. Every time, the right move was to finish
reading what LM does. Prep v7's exit flags and v8's 4bpp upload are what agreement looks like —
byte-identical, asserted against LM's own ROMs. `reference/LM_PARITY.md` is the standing
inventory of where we have not managed it yet. v10's entrance positions WERE the standing debt —
private hook sites and a private table — until LM's GUI was driven over a prepped base and wiped
them (below); v10 now stamps LM's own method-2 routines byte for byte (§9d-3).

Where it stands today: **LM can now READ a prepped base (v5+); it still cannot WRITE to one.**
Tested against the real `Lunar Magic.exe` via its command line (`info_command_line.htm`),
which is scriptable — that is the harness for all of this.

**`$0DF100` is LM's level-access flag. [SOLVED in prep v5]** Undocumented anywhere in LM's
help; found by bisecting a prepped ROM against LM itself, one byte at a time. Any value but
`$FF` and every LM operation dies with *"Lunar Magic : Access Denied! The author of this hack
has chosen to restrict level access."* It sits inside the vanilla `$FF` gap in bank `$0D`
that our Direct-Map16 handlers occupy, and v1–v4 wrote code straight over it — so **every
base this editor ever produced was unopenable in LM, from v1 onward.** LM's own codegen
respects it: in ShaoBase the surrounding block ends at `$0DF0F8` and leaves the byte `$FF`.
V5 branches around it; `-ExportAllMap16` then succeeds (exit 0) where v4 fails (exit 1).

**LM CAN write to a prepped base, and the result does not boot. [OPEN — the round trip's real
state, 2026-08-27]** Driven through the GUI rather than the CLI: open a v10 base, change the
main/midway entrance, Ctrl+S. Two things happen.

First, LM asks **"Restore System Issue — the restore system cannot locate a copy of the original
unmodified ROM with header, which is required for an operation about to be performed. Would you
like to browse to a copy of this file now?"** That is almost certainly the dialog behind the
headless `-ImportLevel` hang: the "Open" picker captured below is what it puts up. Cancel and
the save proceeds.

Second, it proceeds: **375 changed runs** — LM installs its whole hack suite on save — and the
resulting ROM **black-screens on boot in Mesen**. Not attributed to a specific stamp yet;
restoring v10's hijacked `JMP` alone does not fix it, so the cause is elsewhere in the overlap.
That is the round trip's real state: not "LM cannot write to ours" but "LM writes, and the ROM
dies". Everything downstream of §0 should be read in that light.

What survived is as informative as what did not. `$05DC50` — prep v7's exit routine — came back
**byte-identical**, because we match LM there and LM simply rewrote the same bytes over it, and
`$05D7CE` still points at it. That is parity working. `$05DC90` — the PREVIOUS v10's entrance stubs,
which matched nothing of LM's — was wiped to `$FF` while `$05D9FE` still jumped to it. A private
mechanism does not merely get ignored by LM; it gets left as a jump into cleared space. v10 was
redone on LM's rails the same day (§9d-3); the black screen is not yet re-tested against it.

**The write path blocks headlessly — but NOT because the base is ours. [SUPERSEDED in part by
the above]** The dialog was captured: it is not a warning at all but LM's **"Open" file
picker, filtered to SNES ROM images** — LM declining the target and asking for another. It
appears identically on:

| target | `-ImportLevel` (its own exported level) |
|---|---|
| vanilla, 512KB | exit 0, ROM written |
| vanilla after LM itself expanded + imported | exit 0, ROM written |
| `after.smc` — **LM's own save** | dialog, times out |
| ShaoBase — **a real LM hack** | dialog, times out |
| ours, v9 | dialog, times out |

`-ImportAllMap16` behaves the same way on our base. So the CLI write path fails on LM's own
ROMs too, and blaming the prep for it was wrong. Whatever the discriminator is, it is not
"pipe-dream touched this file". Reading is unaffected throughout — the same ROMs export levels,
Map16 and GFX with exit 0.

That leaves the round trip genuinely untested rather than known-broken, and the next step is a
GUI round trip (open a v9 base in LM, edit, save, reopen here and diff) — not more CLI
archaeology.

**The checksum warning. [SOLVED in prep v9]** LM runs TWO checks and words them differently —
that is the whole clue:

- *"The ROM's checksum is **incorrect**"* — stored ≠ computed.
- *"The ROM's checksum has been **tampered with**, which means the file has either been
  previously modified by another program"* — stored **=** computed, but is not the value LM
  knows Super Mario World has. Every prepped base got this one: adding data and recomputing
  honestly is exactly what "another program" looks like.

LM skips both for a ROM it considers one of its own hacks — ShaoBase opens silently even with
its checksum deliberately set to `0000`, and after.smc keeps vanilla's `$A0DA` because LM's
expansion is zero-filled and does not change the sum.

So v9 stops *writing* a new checksum and starts *steering* the old one: a RATS-tagged block of
`0x140` bytes at pc `0x80000` whose only meaning is its sum. `RatsWriter.FixChecksum` zeroes it,
totals the ROM and writes back whatever lands on `$A0DA` — so the ROM is checksum-VALID by the
hardware's rules and unremarkable by LM's. Every write path goes through FixChecksum, so a
built ROM is balanced too. Confirmed: a v9 base opens in LM with no dialog at all.

Remaining divergences, in the order they'd need settling:

- ~~**The v4 4bpp GFX upload is not LM's mechanism.**~~ **[SOLVED in prep v8]** V8 installs LM's
  shape instead: `$00AACD` becomes `LDX #$10` and the planes-0/1 loop, the tile loop and the
  routine's tail collapse into a verbatim 32-byte-per-tile copy returning at `$00AAE1` — which
  is the only thing that makes LM read a ROM's files as 4bpp. `-ExportGFX` on a v8 base now
  yields 4096, LM renders levels and the 8x8 grid correctly, and the game is unchanged (VRAM
  parity against v3 for every file, plus a Mesen boot). Vanilla's plane-3 swap moved into the
  DATA, since the copy no longer synthesizes it: `$00AA9B-$00AAC6` applies it only to files
  `$01`/`$17` when Y is `$6E/$6F/$7E/$7F`, and Y counts DOWN from `$7F` while the pointer walks
  forward — so the tiles are `$00`, `$01`, `$10`, `$11`, not the Y values. Files `$08`
  (tileset >= `$11`) and `$1E` never reach that path; `$00AA96` sends them to the filter path,
  which keeps v4's rewrite. The original finding, for the record:
- **The v4 4bpp GFX upload is not LM's mechanism.** Byte evidence from the reference ROMs:
  on an LM 4bpp hack (ShaoBase) `$00AA50` is `22 80 F7 0F` (JSL $0FF780 — LM's loader, the
  same address our prep uses since v2) and `$00AAE1` is `60`, i.e. LM **stubs vanilla's
  expand-upload with an RTS** and uploads from its own routine. Prep v4 instead *rewrites*
  vanilla's inner loops in place, because our loader delegates to that routine rather than
  replacing it. Both work; they are not the same ROM. A plain LM save (`after.smc`) has
  neither — vanilla loops intact, no loader at `$00AA50`, but the VRAM patch at `$0081E2`.
  **Measured symptom [CONFIRMED 2026-08-27]:** since v6 stores the FILES 4bpp, LM now renders
  every level and the 8x8 editor as noise, because `$00AAE1` is how LM decides a ROM's files
  are 4bpp at all. `-ExportGFX` is the headless proof (exit 0, no dialog):

  | ROM | `$00AA50` | `$00AAE1` | LM's `GFX00.bin` |
  |---|---|---|---|
  | vanilla | vanilla | `A2` | 4096 (3bpp, widened on export) |
  | ours v5 | `22 80 F7 0F` | `A2` | 4096 ✓ |
  | ours v6/v7 | `22 80 F7 0F` | `A2` | **5456 ✗** — 4bpp data read as 3bpp |
  | ShaoBase (LM 4bpp) | `22 80 F7 0F` | `60` | 4096 ✓ |

  5456 = 170 tiles × 32 + 16, i.e. LM read our 4096-byte 4bpp file as 3bpp (4096/24 tiles) and
  widened it. The GAME is unaffected — a v7 build boots and renders correctly in Mesen — so
  this is LM's preview only, and the fix is to install LM's mechanism (stub the expand-upload
  with `RTS` and upload from our own loader) rather than rewriting vanilla's loops in place.
- **The decompression buffer address is ours alone.** V4 moves it to `$7F:A000`, chosen from
  a free-space audit of *vanilla*. Where LM puts its own 4bpp buffer is unknown; if a ROM
  ever carried both they would have to agree, or one would corrupt the other.
- **`HasLmVramPatch` (`$0081E2` = JML) is present on LM saves and absent on ours.** LM's
  BG2/BG3 bypass slots depend on it, which is why those stay editor-only here.
- ~~**`IsPrepped` must stay true for LM-saved ROMs**~~ **[SOLVED alongside v8]** V4's clause
  tested our instruction at `$00AAE5` (`29` on ShaoBase), so an LM 4bpp hack read as unprepped
  — a licence to stamp over it. It now accepts either mechanism
  (`HasGfx4bppUpload || HasLmGfx4bppHack`), and `v8_wears_the_4bpp_hack_lunar_magic_looks_for`
  asserts ShaoBase reads as prepped.

The gate stays empirical, not analytical: prep a base here, drive real LM over it, and diff.
**`reference/LUNAR_MAGIC.md` is the harness** — the invocation, the message-box hazard, the
measured facts, and the bisect method that found `$0DF100`. `--prep <rom> [version]` exists for
exactly that bisect: it takes a version so a failure can be pinned to the stamp list that
introduced it.

Two things LM's help establishes that constrain us (agent sweep of `lm-help/html/`):

- **Hijacks install on save, and some on merely opening a dialog** — the Super GFX Bypass,
  Layer 3 bypass and ExAnimation hacks install "immediately" when their dialog opens
  (`level_super_bypass.htm`, `level_layer3_gfx.htm`, `level_extend_ani.htm`); the VRAM patch
  and FastROM install on the next level save (`option_vram.htm`). So an LM user touching a
  prepped base WILL have LM write its own versions of things we already stamped.
- **LM honours any valid RATS tag it finds, including ones it did not write**
  (`info_rats_format.htm`), and warns on nested ones (`option_restore.htm`). Our expansion
  blocks are RATS-tagged, which is why LM tolerates them — but "Nested RATs are not allowed!!"
  is a hard rule we must not break.
- **`option_vram.htm`**: "If Lunar Magic does not recognize the version of the patch currently
  installed, all options may be disabled." A stamped-but-unrecognized hack does not fail
  loudly; it silently greys out LM's UI. Expect that class of failure, not exceptions.

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
  4bpp expansion — ours is prep v4 and reaches the same result by a different mechanism, see
  §0. 2bpp for `28-2B`,`2F`; Mode7 for `27`; `32` is 4bpp 23.2KB.

**Prep v6 converts the files** (`RomPrep.ConvertGfxTo4bpp`), which is the half of v4 that was
missing: v4 taught the upload to read four bit planes, but until v6 nothing converted the FILES,
so a v4/v5 base read 32 bytes per tile out of 24-byte tiles and uploaded garbage. The editor
looked fine either way — it decodes files directly rather than through the loader — so only an
exported ROM or BPS showed it. **v4/v5 bases stay broken until they upgrade** (File → Upgrade
base to prep v6, which re-preps from vanilla and re-pins the hash); v1–v3 are unaffected (3bpp
loader, 3bpp files).

The pass: for each vanilla id where `Gfx.IsTilePlanar3Bpp` holds and the file resolves,
decompress → `NormalizeBpp(3→4)` → `Lz2Compress` → allocate → repoint the three vanilla pointer
tables. Ascending ids into first-fit space from `RomPrep.GfxConvertBase` (pc `0xA0000`, past the
prep's own tables) keeps it byte-reproducible AND keeps the run at `0x80000` free for
`RomBuilder`'s allocations. `RatsWriter.Allocate` throws when the ROM is full and `PrepInPlace`
turns that into a message — prep has no auto-expand. ~44 files convert; the tail has room to
spare.

Converting the BASE rather than the build or the project's imports is what makes everything
else follow for free: `Gfx.RomBpp` reads 4 off the ROM, imports normalise to it, copy-on-write
forks come out 4bpp, `MaxColor` becomes 15, and the greyed top half of the palette row lights
up — with no `.pdp` schema change and no per-file depth to track. `IsPrepped` tests the depth
itself (v6 leaves no stamp to look for), which also satisfies §0's rule that it stay true for
LM-saved 4bpp ROMs.

`RomBuilder.WriteGfx` rejects ids `0x32`/`0x33` with a warning: `Gfx.Count` is `0x32` and the
three pointer tables are 0x32 bytes and adjacent, so writing `PtrLow + id` for those landed on
GFX00/GFX01's entries. They are the animation blobs, not table-addressed at all (fixed operands
at `$00B88B`), so there was never anything to write.

Proof: `v6_converts_every_tile_planar_file_and_leaves_the_rest_alone` (per file: converted ==
`NormalizeBpp(original)`, excluded ids byte-identical and never repointed) and
`v6_uploads_its_own_converted_files_byte_identically_to_v3` (the v4 reader on a v6 base sends
VRAM exactly what a v3 base sent). Still outstanding: a whole-level eight-slot parity run, and
Mesen, which nothing else substitutes for.
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

### 6a-13. The decompression buffer and the overworld's reader — prep v13  [CONFIRMED in Mesen, 2026-08-29]

The buffer is at vanilla's and LM's `$7EAD00` again (v4-v12 had `$7FA000`). The overworld
reads its animated tiles (water, clouds) out of that buffer with its own bank-04 reader —
offset table `$048000` (67 words), bank `$7E` hard-coded, 3bpp expander `$0480B9` — every
frame, from whatever file was decompressed last. V13 stamps LM's 4bpp-mode bytes for exactly
that: table rescaled to 32 B/tile, expander `$0480BD=$10` / `$0480D0=RTS`, and the OW sprite
tables moved `$7EB9xx/$7EBAxx → $7FC5xx/$7FC6xx` (21 operands, `$04F2B8-$04F3D0`) so the
4bpp file's overrun to `$7EBCFF` hits nothing. Byte-compared against ShaoBase. What of LM's
4bpp mode we still do NOT carry is listed in `LM_PARITY.md` §2 "4bpp graphics".

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

**LADDER = LM'S BYTES (prep v12, 2026-08-29) and the range-2/3 carry.** Mesen showed every prep
v10/v11 level as one repeated filler tile (the intro level, bisected by v10 stamp group: only the
LmLevelRender group reproduces it; v9 fine). Cause: LM's render engine (the bank-$1F block) JSLs
LM's ladder ENTRY `$06F540` ~150 times and reads the bank from `$0B`; our v3 ladder had the same
SLOT addresses but its own dispatcher (mid-code at `$06F540`) and `STY $05`. v12 restamps
`$06F538-$06F5E3` with after.smc's bytes (entry `CMP #$0400`, `$06F5D0` wrapper `LDY $0B : STY $05`
for the vanilla hook, high-range dispatcher), keeping only our slot immediates; LM's second
wrapper at `$06F5E4` (its per-site acts path, not taken) is excluded — our acts stub is at
`$06F5F0`. IsPrepped v12 = `$06F540 == C9 00 04`. Golden V12 pinned.
CARRY: LM's ladder reaches the `ADC #imm` of ranges 2/3 (and 6/7) with C=1 — the second `ASL`
shifts tile bit 13 out and nothing clears it — so those slots store **imm − 1** (LM's defaults
`FFFF`/`7FFF`), and the pointer is imm + tile*8 + 1. `LmMap16DefAddr` and `GrowRange` now do this
(`SlotCarry`); before, tiles ≥ 0x2000 read/wrote one byte off. Emulated-ladder test covers all
ranges; the intro level renders like vanilla in Mesen on v12 and on a rebuilt project.

**The §7a formula below ($02C2E1 → RATS block, linear (tile-0x200)*8) is a coincidence that
holds only for map16_after.smc** — in ShaoBase the $02C2E1 block is a stale FF-filled
allocation while the game reads defs from $158274. The real contract, from the in-game
consumer (LM's Map16-lookup hijack, identical code in every LM ROM):

- `$00C17A` = `JSL $06F5D0` (detector: byte $00C17A == 0x22, operand $06F5D0).
- `$06F5D0` → piecewise def-pointer math at **fixed $06F540**. Entry A = tile*2:
  - tile < 0x200 → vanilla RAM table $0FBE path (our BuildDefPointers equivalent);
    def bank = $0D, or a per-ROM bank for LM custom tilesets ($1930 >= 0x1000).
  - tile 0x200+ → **def = bank:((imm + tile*8) & 0xFFFF)** where `imm` is the 16-bit ADC
    operand and `bank` the high byte of the LDY operand of the SLOT covering that tile's
    range (`69 imm16 A0 bank<<8`). bank == 0 → that range has no defs installed.

**The ladder — one slot per 0x1000 tiles.** `imm + tile*8` is 16-bit addressing into a 32KB
LoROM window, so a slot can address at most `$8000/8` = 0x1000 defs. That is the whole reason
LM has a ladder rather than one slot, and it is a hard hardware limit, not an LM policy:

| range | tiles | slot | | range | tiles | slot |
|---|---|---|---|---|---|---|
| 0 | 0x200-0x0FFF | `$06F552` | | 4 | 0x4000-0x4FFF | `$06F593` |
| 1 | 0x1000-0x1FFF | `$06F55B` | | 5 | 0x5000-0x5FFF | `$06F59C` |
| 2 | 0x2000-0x2FFF | `$06F566` | | 6 | 0x6000-0x6FFF | `$06F5A7` |
| 3 | 0x3000-0x3FFF | `$06F56F` | | 7 | 0x7000-0x7FFF | `$06F5B0` |

Ranges 4-7 are a second chain further along. A slot's opcode bytes (`69`/`A0`) are present
even when unused, with junk imms (`$FFFF`, `$7FFF`, `$8000`) and bank 0 — so **only the bank
distinguishes populated from empty**, never the imm.

Observed slot 0: map16_after $10:7008 (≡ the old $108008+(t-0x200)*8), DoW-backup $14:7000,
ShaoBase/BigEye $15:8274/$15:CB42, juz $10:DE94, after/gfx_after $00:F000 (= none).
Populated high ranges: **dogs_of_war.smc** r0 $1D:AEAE + **r1 $1D:4E5E**;
dogs_of_war-backup3 r1 $1B:2C3A; **sgdq2024.smc r4 $89:C770 + r5 $89:5778** (the only
sampled use of the second chain). Everything else leaves ranges 1+ at bank 0.

**Holes are legal.** DogsOfWar's range 0 stops well short of 0xFFF while range 1 is
populated. `Map16TileCount` is a flat ceiling for the editor, so it stops AT the hole rather
than aliasing across it; the high pages appear once ordinary allocation fills the gap.

- Unedited tiles in the region read FF → def FFFF×4 (renders as t3FF pal7 flips); levels
  don't reference them. LM's .s16 export stores such tiles as zeros.
- Reader: `Rom.LmMap16Slot(range)`, `Rom.LmMap16DefAddr(tile)`, `Rom.HasMap16Range(range)`,
  `Rom.Map16TileCount`, `Map16.LmExtendedDef`. `Rom.LmMap16Defs` is range 0 only.
- Writer: `Rom.EnsureMap16Tiles(minCount)` allocates one fresh bank per range and repatches
  that range's slot. A FULL range needs all 0x8000 window bytes, which leaves no room for a
  RATS tag at the bank start — so a full range takes two banks and parks its tag in the
  first one's tail. Our own prep emits ranges 0-3 (the editor's ceiling is 0x3FFF, which is
  also all LM's Direct-Map16 objects can address: the page byte is masked `& 0x3F`).

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

**True record layout** (starts 8 bytes AFTER the §7c-observed offset): 16 words. The
"constant 2B 2A 29 28" is NOT a previous record's tail and not constant — it is the vanilla
layer-3 GFX set, decoded in §12b:
```
w0 = AN2 (bit15 = BYPASS ENABLED flag)   w1 = AN1
w2 = BG3   w3 = BG2   w4 = FG3   w5 = BG1   w6 = FG2   w7 = FG1
w8 = SP4   w9 = SP3   w10 = SP2   w11 = SP1
w12 = LG4  w13 = LG3   w14 = LG2   w15 = LG1  <- layer-3 GFX, gated by w0 bit 14 (§12b)
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

**There is a SECOND hook site** (found the hard way by RomPrep's real-game smoke): the
fade-in mode step at `$00A5B9` re-runs `JSR UploadSpriteGFX : JSR LoadPalette`,
rebuilding the vanilla staging and wiping the custom palette. Every palette-engine LM
ROM (ShaoBase/DoW/BigEye) repoints the vanilla `JSL $05BE8A` at **$00A5BF** (immediately
after that second LoadPalette) to an LM stub that re-applies the blob. An installer that
only hooks $0095E9 shows the custom palette in stills but loses it on the real fade-in.

Back-area color runtime path: the staging word at **$0701** feeds COLDATA `$2132`
per-frame (`$00AE47: LDA $0701 … STA $2132`); the fade system snapshots
`$0701/$0703+ → $0903/$0905+` (`$00A5D8`). The back color is NOT CGRAM color 0.

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
  byte if bits7+6. Width switches to (size & 0x7F) only when bits7+6 are BOTH set
  (bit7 alone keeps the nibble sizes — corrected via RomPrep parity probing). Obj 0x29
  writes the page|0x40 words through the same plane writes as 0x27.
- The bit7 "run byte" is NOT inert: it is a **stamp descriptor** — low nibble =
  stamp width-1, high nibble = stamp height-1; the fill cycles
  `tileLow + (col % W) + 0x10*(row % H)` (decoded by footprint probes vs after.smc,
  parity-verified incl. screen-crossing fills).
- 0x28 → $0DF160: no-tile directive (entrance/position, $0F31-33).
- LM ext obj 0x02 ($0DE1B0): secondary exit — consumes 2 EXTRA stream bytes (exit word).
Parse lengths must match exactly or the whole object stream desyncs (DoW builds levels
almost entirely from extended 0x27 forms).

Calling-convention trap for installers (RomPrep bug, found in-game): code hooked at the
sprite-pointer site ($05D8F5) and the acts-like call sites (the four vanilla
`JSL $00F545`s) MUST preserve the accumulator's HIGH byte (B). The entrance decode after
$05D909 does `TAX` with 16-bit X, so a leaked B shifts every entrance-table index by
+0x100 for levels >= 0x100 (symptom: garbage spawn/scroll state, e.g. instant TIME UP).
Vanilla $00F545 is pure 8-bit and B-transparent; replacements must be too.

### 9d-2. A screen exit's destination bit 8  [CONFIRMED from disassembly + emulated parity]

Vanilla has **no field** for the destination level's ninth bit. $05D7BD reads the destination
byte into $0E, then takes the high byte from where the PLAYER is:

```
$05D7BD  LDA $19B8,X : STA $0E          ; destination low byte
$05D7C5  LDA $0DD6 : LSR : LSR : TAY
$05D7CB  LDA $1F11,Y                    ; the player's submap flag
$05D7CE  BEQ +2 : LDA #$01 : STA $0F    ; high byte = "am I on a submap?"
```

So the same exit lands in $005 from the main map and $105 from a submap, and "exit to $105"
is inexpressible in the data. Same for the secondary route: $05F800,Y is a low byte too.

$19D8,X (the exit flags the handler writes) has **no reader anywhere in the vanilla image** —
only the store at $0DA533 — so its upper bits are free. Both Lunar Magic and RomPrep §V7 use
that, at the same two sites and with the same layout:

| site | vanilla | patched |
|---|---|---|
| $0DA531 | `AND #$01` (keep the water bit) | `AND #$0F` (keep the whole X nibble) |
| $05D7CE | `BEQ +2 : LDA #$01` | `JSL $05DC50` (4 bytes, exact fit) |

$05DC50 (vanilla $FF run from $05DC46, 954 bytes) reads $19D8,X:

- **bit 2** — extended exit; clear falls back, so every untouched exit keeps its behaviour
- **bit 0** — destination bit 8 &nbsp;&nbsp; **bit 1** — use the secondary table ($1B93)
- **bit 3** — entrance action → $192A bit 6 &nbsp;&nbsp; **bits 4-7** — further high bits, only
  reachable through LM's word form (ext obj 0x02), since the nibble form masks to 4 bits
- high byte = `(flags >> 4) << 1 | bit0`; fallback = `$13BF >= $25` (is this a main level),
  which replaces the submap test and no longer depends on how the player arrived

Ext obj 0x02 ($0DE1B0) is the same two tables in one object: `LDA [$65] : STA $19B8,X : XBA :
STA $19D8,X` — the "exit word" is `destination | flags << 8`, NOT a 16-bit destination.

RomPrep stamps its own routine at that address; `RomPrepTests.v7_decides_the_high_byte_exactly_
as_lunar_magic_does` runs both ROMs' routines under Cpu65816 over every flag combination and
asserts they agree, so a base prepped here and one saved by LM are interchangeable.

### 9d-3. Where an entrance puts Mario  [CONFIRMED from the decode]

No entrance record stores a position. It stores a SCREEN and two indices into small bank-05
tables, and the three kinds keep the screen in three different places:

| kind | screen | X index | Y index |
|---|---|---|---|
| main | `$05F600` bits 0-4 (`$05D9EC`: `LDA $01 : AND #$1F`) | `$05F200` bits 0-2 | `$05F000` bits 0-3 |
| midway | `$05F400` bits 4-7 (`$05D9E1`: `LDA $02 : LSR x4`) | — shares the main's | — shares the main's |
| secondary | `$05FC00` bits 0-4 (stashed to `$01`, same tail) | `$05FC00` bits 5-7 | `$05FA00` bits 0-3 |

Position = `screen * 0x100 + DATA_05D750[xIndex]`, `DATA_05D730/40[yIndex]`. `DATA_05D758`
supplies an X high byte too, but the horizontal tail overwrites it with the screen — which is
why indices 0-3 and 4-7 collapse to the same five distinct offsets on a horizontal level, and
why a screen field exists at all. Vertical levels take the other branch at `$05D9A7` and keep
the table's high byte.

Two consequences, **for vanilla data only**. A marker can only sit at one of **8 x 16 spots per
screen**, so a drag snaps; and a **midway entrance carries only a screen** — its position inside
that screen is the main entrance's — so it moves a screen at a time and not vertically at all.
Both are in `EntrancePlacement` and pinned by `EntrancePlacementTests`.

**Lunar Magic lifts the first**, and its help says so in as many words: the tables are "method 1",
while "Method 2 does not use table-based coordinates, and is an enhancement inserted by Lunar
Magic" (`level_main_entrance.htm`). Method 2 is not a second table: it reinterprets the SAME two
index nibbles as 16px steps and adds a flags byte and a Y-high byte — `$05DE00`/`$06FC00` per
level for the main entrance (routine `$05DD30`, hooked at `$05D97D`), the spare bits of `$05FE00`
plus a fifth table for secondary ones (routine `$03BCE0`, hooked at `$05D833`). Every LM save
installs both, byte-identical across after.smc and every reference hack.

**Prep v10 stamps exactly those bytes** (`RomPrep.AppendV10Stamps`), so a ROM saved by either
tool reads the same here: `X = screen << 8 | xHigh << 7 | xIndex << 4`, `Y = yHigh << 8 |
yIndex << 4` (`EntrancePlacement.Method2X/Y`), main and secondary markers land on the 16px step
they were dropped on, and `Rom.HasFreeEntrancePositions`/`HasFreeSecondaryPositions` are LM's own
hooks. Two things come with the routines and are stamped too: LM's `$1A` in `$06FE00` (it lands in
`$13CD`, which the midway tape at `$00F2D8` tests for zero — vanilla kept the midway screen there,
so `$05D9C3` becomes a load), and the migration of every secondary record `$100-$1FF` to carry
destination bit 8 in `$05FE00` bit 3, because `$03BCE0` takes the ninth bit from the record where
vanilla took it from the submap. Pinned byte-for-byte against after.smc in
`EntrancePlacementTests`, and run as code under `Cpu65816` there. LM_PARITY has the full decode.

**The midway has a position of its own too**, via LM's "separate midway settings": a 0xC4-byte
blob hooked at `$05D9E3` (and at `$05D979`, for exits that target the midway) reading four
per-level tables — flags, position, FG/BG, Y high. LM installs it on demand (juz, ShaoBase,
DogsOfWar have it; a plain save does not); prep v10 installs it always, byte-identical apart from
the table operands. The midway's X is a whole nibble (`screen << 8 | nibble << 4`) and it gains a
fifth screen bit; until a level opts in, the blob hands back the screen and the midway borrows the
main's spot as before. `Rom.HasFreeMidwayPosition` follows the hook, so on a base without it the
editor still reports `ScreenOnly` and says so when a drag has nowhere to go.

**The camera follows, via LM's level-entry engine.** A free position is only half of it: vanilla
starts the camera at one of four fixed offsets, and an entrance far from them puts Mario off
screen. LM's "set FG/BG relative to player" (`$06FE00` bit 7 for the main entrance, the FG/BG byte
for midway and secondary ones) starts the FG at Mario's Y plus the entrance's offset nibble x16;
it lives in a two-block engine every LM save installs, which v10 transplants verbatim with only
bank bytes changed (`LmLevelEntry`, LM_PARITY "Level entry"). At vanilla height the engine's RAM
comes out equal to vanilla's, asserted in
`RomPrepTests.level_pointer_chain_leaves_identical_ram_except_the_level_word`.

**Variable level height, LM's way** (LM_PARITY "Level entry"). A horizontal level trades width for
height: the header's screen count is the width, and the entrance record's `HeightIndex` — LM's
per-level height byte, block B of the engine — picks one of 32 LUT heights with `columns × height
≤ 0x3800`. Objects reach rows past 31 through LM's 32-row BANDS: ext 01 carries a band in its X
nibble and ext 03 a band in Y, the loader adds `band × 0x200` to every plane pointer, and the parser
tracks it (`LevelObject.Band`, `AbsoluteY`) while the encoder re-derives the jumps like screen
jumps. The whole of LM's level engine is on the base — entry (blocks A/B), height (block C and the
loader/sprite/object edits) and render (LM's bank-$1F block with its VRAM patch, `LmLevelRender`) —
so a tall level built here loads, scrolls and draws as it does under LM. Byte-parity with after.smc
is asserted for every block and edit; the in-game run is the check that remains.

### 9e. Level connections  [CONFIRMED in-game, two-room hack in Mesen]

Screen exit ($0DA512) → secondary entrance ($05F800/FA00/FC00/FE00) → destination level
was played end to end: a built two-room hack where a door in level $0C5 warps to level
$0C6, with the destination's GFX slots overridden to an imported ExGFX. Mario arrives
standing, alive, with the custom graphics applied. That exercises the level rebuild,
header edit, main entrance, secondary exit, entrance record and GFX bypass together.

Three things that make a connection *look* broken while the data is correct:

- **Enterable vs decorative pipes.** Object $0F's settings LOW nibble picks the pipe kind
  and only kind 1 is enterable. In vanilla, pipes standing on a screen that has an exit
  are overwhelmingly `$x1` ($21×29, $11×11, $51×10); decorative pipes elsewhere are `$x0`
  ($20×50, $10×29). A kind-0 pipe produces byte-identical Map16 tiles to a kind-1 one
  (mouth $137/$138, shaft $135/$136), so the level render cannot tell them apart.
  Pipes also need Mario pixel-centred on the mouth; ext obj $10 (a door, entered with Up)
  is the reliable choice for a test.
- **The entrance's camera bits are not optional.** $05FA00 bits 4-5 (screen boundary Y)
  and 6-7 (vertical scroll) at 0 spawn Mario outside the visible area and he falls out of
  the level — the warp fires but looks like a crash. Copy the values the source level uses.
- **Time index 0.** Level $0C5's vanilla header has Time = 0. Nothing notices in vanilla
  because the opening message box holds the timer, but a rebuilt level without one dies to
  TIME UP within seconds.

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

### 10a. Background images: codec + why they cannot grow  [MEASURED on vanilla]

`BgImage` is the RLE codec (decode + encode). Encode is a lossless inverse and never worse
than vanilla's own output: all **17** distinct vanilla backgrounds re-encode 0-50 bytes
SMALLER than the stream they came from.

But that slack is the entire budget, because a background cannot move:

- The page byte is not in the data — the loader derives it from the address (`>= $E8FE` =
  page 1), so relocating a stream across that boundary recolours every tile in it.
- The stream must live in **bank $0C**; the loader hardcodes that bank.

Free space in bank $0C, by page side (runs >= 32 bytes of $00/$FF fill):

| page | free runs | largest |
|------|-----------|---------|
| 0 (`< $E8FE`) | `$B66E` 402 B, `$D86F` 145 B | 402 B |
| 1 (`>= $E8FE`) | ten fragments, 32-58 B each | 58 B |

Backgrounds are 200-900 bytes, so a page-1 background has nowhere to go at all and page-0
only fits the small ones. **Editing a background's bitmap therefore needs a prep-style
hijack** (relocate the pointer read to a bank we control, as RomPrep V2 did for GFX), not
just an encoder. Until then only SELECTION is offered: pointing a level at one of the 17
addresses already in use, which needs no allocation (`ProjectFile.LevelState
.Layer2Background`, and bank $FF in the pointer IS the mode).

**Editing** (LevelSession.SetEditLayer / RomBuilder): object mode is edited with the same
code as layer 1 — same stream format, same encoder, only ObjectEngine's `layer` argument
differs. The MODE is the pointer's bank, so writing a real bank converts a background-image
level to object mode and there is no separate flag; the project stores only
`LevelState.Layer2Objects` (null = keep the base ROM's layer 2). The reverse conversion is
not offered: it needs a background-image id to point at, which nothing stores.

Verified against vanilla: 26 levels are in object mode and **all 26** have a level mode that
loads layer-2 objects, so the ignore-set above is consistent from both directions. Note
`$0C4`, `$0EB`, `$0F0`, `$0FB`, `$1DA`, `$1E6`, `$1E7`, `$1F9` are object mode with an EMPTY
stream — editable from blank. Conversely, background-image levels usually sit on a mode that
never loads layer-2 objects (that is *why* they use a background), so converting one also
needs a level-mode change or the objects are written and never read (the build warns).

### 10b. LM custom backgrounds  [CONFIRMED — controlled save, .resources/backgrounds]

§10a's "a background cannot move" is true of *vanilla's* loader only. LM does not rewrite a
shared background in place; per `editor_back.htm` it "will save the current level's background
as a custom background as soon as you modify it", and that custom background is an ordinary
relocatable block. Decoded from `bg_0.smc` → `bg_a.smc` (LM 3.33: same ROM saved twice, level
`$105`, the second with exactly one BG tile deleted in the Background Layer 2 Editor):

  layer-2 ptr    `$05E600 + level*3` holds a REAL 24-bit address, bank != $FF
                 ($105: `00 D9 FF` → `CC 94 10` = `$10:94CC`)
  block          RATS: `STAR` + (len-1) word + its inverse, immediately before the stream
                 (`53 54 41 52 B1 01 4E FE` → 0x1B2 payload bytes). Allocatable anywhere.
  codec          UNCHANGED from vanilla `$058126` (§10): bit7 set = run of the next byte
                 ((cmd&0x7F)+1 times); bit7 clear = literal copy of cmd+1 bytes; `FF FF` ends.
                 Our BgImage.Decode reads LM's stream with no changes.
  geometry       2 screens x 16 wide x **32 tall, stride 0x200** — LM's 512px backgrounds
                 (`level_map16_bg.htm`: 432px -> 512px, "5 extra rows"), NOT vanilla's 27-row
                 0x1B0. Verified: LM's custom screen 1 at stride 0x200 equals the vanilla
                 background's screen 1 at stride 0x1B0 for all 432 tiles, 0 mismatches; the
                 one edited tile lands at idx 0x065 = screen 0 row 6 col 5, which is the
                 position LM's own status bar reported.
  mode flag      `$0EF310 + level` (the per-level layer-2/3 settings byte LM's `$05803B` hook
                 reads into `$7FC00B`) goes `00` -> `06`. Bit 1 set = do not fall through to
                 `$058074`; bit 2 set = skip the layer-2 map fill. Without it a non-$FF bank
                 would be read as an object stream, so this byte IS what separates "custom
                 background" from "layer-2 objects".
  BG Map16 bank  SAME byte, **bits 4-6** = the background's Map16 bank 0-7. Bank N covers BG
                 pages `0x80 + N*0x10` (LM's "Change BG Map16 Bank" dialog lists exactly
                 Bank 00 = pages 80-8F ... Bank 07 = pages F0-FF; "1 bank = 0x10 pages or
                 0x1000 tiles", one bank per background, `editor_back.htm`). Confirmed by two
                 further saves: bank 01 -> byte `16`, bank 07 -> byte `76`.
  page byte      No longer derived from the stream address (§10a). The stream stores the low
                 byte; the page comes from the bank field above plus the high nibble of the
                 tile within the bank. LM numbers BG tiles 0x8000+i (pages 0x80-0xFF) = our
                 virtual 0x4000+i; its editor's status bar reported the edited tile as `8002`.
  relocation     LM allocates a FRESH RATS block on every save (the pointer walked
                 `$10:94CC` -> `$10:980E` -> `$10:9B50` across three saves); it does not
                 rewrite the previous block in place.

**We already carry the runtime half**: `LmLevelRender` stamps both the `$05803B` hook
(`$0EF510`) and the `$0EF310` table (all zeros today, read/written by nothing). So writing a
custom background needs no new ASM — allocate a RATS block, `BgImage.Encode` the 0x400-entry
map, point `$05E600` at it, and set `$0EF310[level] |= 0x06`.

Evidence: `.resources/backgrounds/bg_0.smc` (LM save, no edits), `bg_a.smc` (+1 BG tile
deleted), `bg_b.smc` (+BG Map16 bank 01), `bg_c.smc` (+bank 07). All LM 3.33 on level `$105`.

STILL OPEN (not yet saved/diffed): whether a 32-row edit changes anything beyond the stream
contents; what LM does when two levels share a vanilla background and only one is edited
(expected: the edited level gets its own block and the other keeps the `$FF` pointer, since
the pointer and the settings byte are both per level, but unverified).

## 11. Sprites  [CONFIRMED from disassembly + LM ASM trace]

Pointer table §3 ($05EC00, 2 B/level, bank fixed $07 — **vanilla only**). LM relocates
sprite data and replaces the bank setup at $05D8F5 (vanilla `LDA #$07 : STA $D0`) with a
JSL to `PHB:PHK:PLB : LDY $0E : LDA $xxxx,Y : STA $D0` — the operand is a per-level BANK
table (ShaoBase $0EF100; 105 → bank $13). Reading bank $07 there returns STALE pre-move
data that parses plausibly (sorted screens!) but is wrong — Rom.LmSpriteBankTable detects
the hijack; Rom.SpritePointer composes bank<<16 | $05EC00 word. The same LM stub also
stores the level number to $010B (PIXI per-level customs read it). Stream ($05D8F9 + $02A82C):
```
header    1 byte   bits5-0 = sprite memory ($1692), bits7-6 = buoyancy ($190E)
entry     3 bytes  b1 = YYYYEEsy  (Y = (y<<4)|YYYY, EE = extra bits, s = screen bit4)
                   b2 = XXXXSSSS  (X nibble within screen, screen = (s<<4)|SSSS)
                   b3 = sprite number (>= 0xE7 → scroll command, $02A866)
0xFF      terminator (lead byte)
```
**Extended list (Lunar Magic)  [CONFIRMED, DogsOfWar $109/$10F/$11F + block C decode].** Header
bit 5 — LM narrows the memory field to bits 4-0 (`$05D8FC: AND #$3F → #$1F`) to free it — marks
the list as extended, and LM's loader (block C of the level engine, `$0BF5` bit 5) then reads
`FF nn` as "the 32-row BAND for the entries that follow" (`nn` < $FE; Y high byte = band×2 |
Y bit 4, so row = band × 32 + Y, exactly the object stream's ext 01/03 bands) and `FF FE` (or
`FF FF`) as the end. A vanilla list is the degenerate case: no bit 5, a lone `FF` ends it. LM
writes the list as groups per band with a marker before each change, first group unmarked when
it is band 0 — `$109` is `20 | FF 18 | 3 entries | FF 19 | … | FF FE`. `Sprite.Band`/`AbsoluteY`
carry it; `SpriteData.Parse/Encode` are the exact inverse of those bytes (`LevelHeightTests`), and
the OAM capture opens its synthetic list with the marker (and `$0A` = band×2, which the loader's
skipped head would have set) so a sprite past row 31 draws at its row.

**LM/PIXI extra bytes**: LM hijacks the sprite-advance (vanilla `INY INY INX` at $02A846 →
JML into LM's relocatable code bank). Entry size = byte table at
**sizeBase + (EE<<8 | sprite#)** (0x400 bytes, per-ROM; includes the 3 base bytes; vanilla
entries = 3, customs 4-13). Locate per-ROM via signature
`4A 4A 29 03 EB C8 C8 B7 CE 88 88 08 C2 10 DA AA 98 18 7F <base:3>` (Rom.LmSpriteSizeBase;
absent in clean/juz ROMs → fixed 3). Sizes MUST be honored or the stream desyncs (DoW).
**Registration [CONFIRMED DoW/ShaoBase]**: LM's help ("Custom Sprite List Sizes") puts the
table's SNES address at PC 0x7750C and 0x42 at 0x7750F — headered offsets, i.e. **$0EF30C /
$0EF30F**, which is exactly what block C of LM's level engine reads (`LDA $0EF30C..0E → $0C-$0E`,
`$0EF30F + $BE → $0F`, zero = enabled). DoW registers `$909F52`, ShaoBase `$928F4B`; after.smc
none (`FF`). `LmSpriteSizeBase` reads the registration first, the signature second. We AUTHOR the
table the same way (`Rom.SetSpriteEntrySize`): 0x400 bytes of 3 in a RATS block, pointer + 0x42,
one entry per (extra bits, number) — written by `RomBuilder` for every sprite carrying extra
bytes; `SpriteEdit.Place` zero-fills a placed sprite to the table's size so a record never
comes up short. Editor entry: Level ▸ Sprite data… (`SpriteDataWindow`).
Readers: SpriteData.Parse/Encode (byte-identical round-trip incl. extra bytes);
SpriteData.DrawOverlay renders badge markers (green = sprite, orange = scroll command).
Sprite GFX rendering (real tiles per sprite) is out of scope for now.

**Entrance flags (LM dialog) [CONFIRMED]**: `$192A` bit 7 = slippery (`$86=$80`), bit 6 = water (`$85=1`), consumed and cleared by LM's `$05DD00` (hooked from `$00A6CC`); main record: `$05DE00` bits 6-7 → those bits (`TSB $192A` at `$05DD48`), secondary: `$05FE00` bit 7 → bit 7 and sixth-table bit 5 → bit 6. **Face left** = `$06FE00` bit 6 (→ `$13CD`; block A `$1083C1`: `BIT $13CD : BVC : STZ $76`) — previously mis-read here as "BG relative to FG only". Vanilla actions 5/7 still set the flags by themselves (`$00A6D5`). Editor: the entrance marker's edit badge → `EntranceWindow` (main/midway), `SecondaryEntranceWindow`.

**11a. PIXI custom sprites + spawn emulation  [CONFIRMED on ShaoBase/DogsOfWar]**
- Custom-ness is per PLACEMENT, not per number: the spawn hijack stores `b1 & $0C`
  (extra bits << 2) to `$7FAB10,X` ($94AC26-AC2C in ShaoBase) and the raw number to
  `$7FAB9E,X`; both hooks ($018172/$0185C3 → PIXI bank) gate on `$7FAB10,X & $08` and
  dispatch on `$7FAB9E,X`. The config-table pointer canNOT detect customs: PIXI shares
  one routine across numbers and fills unreplaced entries with `$018021` (an RTL).
- PIXI config table: base `$B166` + num*16 in the hijack bank (pointer-dispatch scan
  finds `$B171` = +0x0B): +0 type (0 = tweak-only), +1 acts-like, +2..+7 tweakers,
  +8 init ptr (3B), +0x0B main ptr (3B), +0x0E extra props. Hook trick: `PLA:PLA` pops
  2 of the JSL frame's 3 bytes, `PEA $85C1`, custom routine ends RTL — the leftover
  JSL bank byte completes the PEA into a long return to $0185C2 (RTS). Balanced.
- OAM capture spawns through the ROM's OWN loader instead of hand-seeding: synthetic
  1-record stream (header+record+FF) in $7F0100, `$CE-$D0` → it, enter the record-spawn
  body at **$02A84E** (Y=2 = b2 offset, X=0 = record index, DBR=**2** — the slot-range
  tables $02A773+ are DBR-relative!, `$00`/`$01` = load column/screen — CreateSprite
  takes position high bytes from `$01`). Skips LM 3.x's rewritten walker ($10FA9F),
  which needs level-load state ($0BF4 flags, per-screen stream index $0CF6/$0D36).
  Status value flows via `$04` → `STA $14C8,X` at $02A975; slot search = $02A918
  (start/end per sprite-memory `$1692` from tables $02A773/$A7AC; $1692=0 is safe).
  After spawn: seed `$15E9` = slot, clear `$15A0,X` (spawn marks sprites offscreen)
  each frame, run frames with X = slot.
- Cpu65816 gotcha: hijack chains JML into $80+ mirror banks, so a top-level RTS can
  arrive at the sentinel with PBR = bank|$80 — CallNear's sentinel compares `& 0x7F`.

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
BPP FIX: the "24 bytes/tile, decode 3bpp" above holds only for 3bpp ROMs. On a 4bpp LM ROM
(RomBpp 4) blob1 is packed 4bpp (32 bytes/tile), so Overlay decodes it at RomBpp with a
TileBytes(RomBpp) stride — otherwise animated tiles garble (e.g. ShaoBase munchers, Map16
0x12F, anim slot 9 / $7D00-region source). Pre-existing; unrelated to the §12f global work.
Debug: `--tilepng <rom> <lvl> <tileHex> <out>` renders a Map16 tile across the 4 phases.

### 12b. Second animated blob — berries etc.  [CONFIRMED + IMPLEMENTED]

$00B8D7 (fall-through after the GFX32 conversion): a second blob decompresses from
bank:(operand at $00B8D8) — vanilla $088000 — to $7E2000 — the decompressor writes via [$00],Y
without advancing $00, so blob2 lands over blob1's spent 3bpp source. It is stored RAW
4bpp (0x5D00 bytes): DMA-visible RAM = $2000-$7CFF blob2 (berries at id 5's $6D80 =
offset 0x4D80), $7D00-$ACFF converted blob1. Overlay resolves >= $7D00 from blob1 as
3bpp, else >= $2000 from blob2 as 4bpp.

## 12b. Layer 3  [vanilla path CONFIRMED + IMPLEMENTED; LM's own records PARTIAL]

**The geometry is fixed by the PPU setup, not by the level**  [CONFIRMED — `SetUpScreen`
`$008A7F`]. `$2109` (BG3SC) = `$53` → the layer-3 tilemap is **64×64 tiles at VRAM word
`$5000`**, four 32×32 screens in the order left-top, right-top, left-bottom, right-bottom.
`$210C` (BG34NBA) = `$04` → its character data starts at **word `$4000`**. `$00A993` uploads
GFX **28, 29, 2A, 2B** back to back from there, `$400` words each, so the four slots LM calls
LG1-LG4 fill exactly the **512 2bpp tiles** a tilemap word can name. A layer 3 is therefore
**512×512 pixels** — which is also where the ExAnimation destination range `1C00-1DFF` comes
from (§12e) and why LM's tilemap-bypass file size defaults to `0x2000` = 64×64 words.

**The tilemap is picked by (level mode, option), and it is a stripe image**  [CONFIRMED].
`CODE_009FB8` computes `index = $1931*3 + ($1BE3-1)` — level mode by three, plus the Layer 3
Options value below, which is why option 0 means no layer 3 at all. That index reads the
3-byte `Layer3Ptr` at **`$059000`** (not `$058FFD`), and `$00A02F` hands the block to the
**stripe-image uploader `$00871E`**. The table is exactly **45 entries** — it ends where the
first tilemap block `DATA_059087` begins, so it covers level modes 0-14 only. Vanilla has six
distinct blocks in `$059087-$05A221`, default `$059549`.

Stripe-image entry, 4 bytes then data — the format `$00871E` consumes:

  +0,+1  VRAM word address, **big-endian**
  +2     bit 7 = step DOWN a column (+32 words, one row of a screen) instead of across;
         bit 6 = RLE; bits 5-0 = high bits of the length
  +3     low bits of the length. Length is in **BYTES, minus one**.
  data   the tilemap words, low byte first — or, for RLE, ONE word repeated to fill the length.
  end    a first header byte with bit 7 set (`$FF`).

Words the script never writes are **not** tile 0: the console still has whatever the last level
left in that VRAM plus the status bar it redraws every frame, and tile 0 of GFX28 is a font
glyph, so filling the gaps with it draws a screen of noise the game never shows.
`Layer3.Tilemap` returns `-1` for those and the renderer leaves them as the back-area colour.

**Not the same thing as the layer-3 SCROLL strips.** `CODE_058955` (`$1925` → `PtrsLong05895E`
→ `CODE_058D7A` / `CODE_058B8D` / `CODE_058C71` / `Return058C70`) does NOT read `Layer3Ptr`. It
builds the per-column/row strips in `$1CE8`/`$1D68` by expanding the **layer-2 RAM map**
(`$7E:B900`/`$7E:BD00`) through the **same BG Map16 defs at `$0D9100`** (§10) — which is why LM
warns you cannot use tides and layer-2 data in the same level (`view_layer_3.htm`). L3 priority
= header byte 2 bit 7 (`LevelHeader.Layer3Priority`, already decoded).

**Implemented** in `src/rom/Layer3.cs` (option, tiles, tilemap, render), surfaced as
`EditorSession.Layer3Image` / `Layer3Sheet`, drawn by the Background tab's Layer 3 mode, and
used for the ExAnimation destination picker's layer-3 range (which drew flat grey before).
`--layer3 <rom> [level] [out.png]` renders one; with no level it lists all 26 vanilla levels
that carry one. Tests: `Layer3Tests`, `BackgroundModeTests`.

**LM's two dialogs** (field sets confirmed by opening them; Level menu):

*Change Layer 3 Settings* — Layer 3 Options, exactly four: `Blank Layer 3`, `Water, high and
low tides`, `Water, low tide only (smasher if tileset is 1)`, `Tileset Specific (current tileset
is N:<name>)`. Plus "Make tides act as", a "Force Layer 3 tiles with priority" flag, an "Enable
advanced bypass settings" flag, and the advanced group: CGADSUB, Move Layer 3 to Subscreen, Fix
scroll sync, Vertical Scroll, Horizontal Scroll, Initial Y Position/Offset, Initial X
Position/Offset. Selecting a tides option raises a **destructive** confirm ("will cause Lunar
Magic to delete objects and sprites beyond the new lower max screens limit") — tides halve the
screen count.

*Layer 3 GFX/Tilemap Bypass* — "Enable bypass of standard Layer 3 GFX for this level" + four
slots **LG1-LG4 defaulting to GFX 28/29/2A/2B** (shown with their vanilla source addresses
`05C26C`/`05C8A3`/`05CD7B`/`05D2F0`); and "Enable bypass of standard Layer 3 tilemap" + GFX
Tilemap File **LT3** (default `7F Skip File`), Destination for File (default `Under Status
Bar`), File Size (default `0x2000 bytes 512x512`). Dialog note: standard GFX 0-31, ExGFX 80-FF,
Super ExGFX 100-FFF.

**The install gate  [CONFIRMED — this is why the first three saves wrote nothing].** LM will
not install its layer-3 hack until its restore system can find *the original unmodified ROM
with header*; it prompts "Restore System Issue ... browse to a copy of this file now?" and
creates an Auto Full Restore Point first. Before that prompt is answered, opening *Change Layer
3 Settings*, changing the option and saving the level produces a **byte-identical ROM** —
verified three times (`bg_0` ≡ `l3_0` ≡ `l3_a`), even though the editor re-renders with the new
tilemap. Any future controlled save for layer 3 must satisfy the restore system first.

Once satisfied, enabling the GFX bypass and saving installs the hack: **~24 KB across 7871
runs**. Observed hook sites (PC, headered): `0x001671` → `JSL $0FF9C0`, `0x002340` →
`JSL $0FFAB0`, `0x002C06`/`0x002C47` → `EA EA`, `0x002C50` → `JSL $0FF780`, `0x0285B8` →
`JSL $0FF7F0`, `0x021FFE` operand → `$0FFAF0`; new code in bank `$0D` free space
(`0x06F2E0`, `0x06F2F0`) and bank `$0F` (`0x07F1C3`, `0x07F35C` — begins `4C 4D 03 01` = "LM"
+ version, `0x07F3B5`); plus repeated 2-byte `E3 B3` → `E0 F0`/`F0 F0` patches at `0x06A6BE`,
`0x06C403`, `0x06D003`, `0x06DC03`, `0x06EB03`.

**The GFX bypass: NOT a new table — the tail of §7d's record  [CONFIRMED + IMPLEMENTED]**

The earlier reading of these saves put the record boundary 0x18 bytes too early and concluded
a second per-level table existed at `$12:AD20` whose reader could not be found. Both halves
were wrong. Re-aligned to `LmGfxBypassBase + level*0x20`, the LG slots fall exactly on **words
12-15 of the Super GFX Bypass record** — the four words §7d recorded as "w12-15 = tail (TBD,
constant)". `2B 2A 29 28` was never a constant; it is the vanilla LG4/LG3/LG2/LG1 default set,
and `gfx_after.smc` (which predates the layer-3 hack entirely) already carries it.

  words        w12 = LG4, w13 = LG3, w14 = LG2, **w15 = LG1** — reverse slot order, the same
               convention as the rest of the record. `& 0xFFF` = GFX/ExGFX file, `0x7F` = keep
               the vanilla file. w13 bits 13-14 additionally carry the Layer 3 Option below.
  enable       **w0 bit 14**, distinct from the FG/BG/SP bypass on w0 bit 15. LM's loader at
               `$0FF9E0` is literally `LDA [record] : ASL : BPL default`, and its "default" is
               a fixed record at `$0FFA6F` whose tail is `2B 2A 29 28`. So `w0 = $407F` means
               layer-3 bypass only (`l3_e` level 105) and `$8008` means the other one only
               (`gfx_after` level 105) — reading either bit as "the record is in use" breaks
               the opposite ROM, which is what `Layer3Tests` pins.
  location     no new scan: `Rom.LmGfxBypassBase` already finds it. The reader IS a plain
               `LDA.l base,X` at `$0FF7F0` (`A5 FE F0 ?? 3A 0A×5 AA BF <base>`, §7d) — the
               earlier `BF nn AD 12` search failed only because it was run against the wrong
               base. `l3_0` has no such reader at all: LM allocates the table on the save that
               first needs it, which here was the layer-3 hack's install.
  VRAM         LM's own destination table at `$0FFA7F` is `$4C00 $4800 $4400 $4000` for slot
               index 0-3, i.e. **LG1 → word $4000, LG2 → $4400, LG3 → $4800, LG4 → $4C00**,
               0x400 words each — independently confirming the 128-tile-per-slot layout above.
**The TILEMAP bypass  [CONFIRMED — all four fields, `l3_g.smc`]** — six controlled saves off
`l3_e`, each moving one control. It is not in w9-w11 as guessed: the whole thing is **word 1**,
gated by **w0 bit 13**.

  enable       **w0 bit 13** (`0x2000`). Ticking "Enable bypass of standard Layer 3 tilemap"
               alone moved exactly that bit: `407F` → `607F`, nothing else in the record. So w0
               carries THREE independent enables — bit 15 FG/BG/SP, bit 14 layer-3 GFX, bit 13
               layer-3 tilemap.
  w1 bits 0-11 the LT3 file (`0x7F` = Skip File). `l3_g` ends at `w1 = 8080`, LT3 = ExGFX 80.
  w1 bits 12-13 file size: **0 = 0x2000 (512x512), 1 = 0x1000 (512x256), 2 = 0x800 (256x256),
               3 = "Do not use"**. Read off four saves: `807F 907F A07F B07F`.
  w1 bits 14-15 destination: **0 = Under Status Bar, 1 = Start of Layer 3, 2 = Last Line of
               Status Bar, 3 = Bottom Half of Layer 3**. Four saves gave `007F 507F 907F D07F`,
               whose high nibbles are `dest<<2 | size` — the bit 12 that first looked like a
               stray "not default" flag was the size field, which LM's dialog had quietly left
               on 0x1000 from an earlier step.

  LM VALIDATES on OK, which is worth knowing before driving it: an un-inserted LT3 file is
  refused ("Graphics file not found in ROM"), and a file larger than the declared size is
  refused too ("larger than it should be for the slot it's in") — including against "Do not
  use", which permits 0 bytes.

  destinations **[CONFIRMED — LM's own table]** `$0FFEBC` holds the four VRAM words:
  **`$50A0 $5000 $5080 $5800`** for destination 0-3, i.e. Under Status Bar = +`$A0` (five rows
  of 32, clearing the status bar), Start of Layer 3 = the window base, Last Line of Status Bar =
  +`$80` (four rows), Bottom Half = +`$800`. Alongside it `$0FFEB4` holds the sizes
  (`$2000 $1000 $0800 $0000`) and `$0FFEC4` the status bar tilemap's own size, `$0140` = 320
  bytes = 32x5 words. LM's help names the last two by file offset — "0x800BC holds the 2 byte
  layer 3 VRAM destination for that setting, 0x800C4 the 2 byte size of the layer 3 status bar
  tilemap" — which is the same bytes, independently. `Layer3.TilemapDestinationWords`.

**w1 IS ALSO AN1 — an unresolved collision.** §7d and §12d both read w1's low bits as the
ExAnimation AN1 slot, traced from LM's loader at `$0FF7F0`; this decode reads the same bits as
the LT3 file. Both cannot hold a file at once. Either one of the readers masks differently, or
LM treats the two features as mutually exclusive per level. NOT established — and until it is,
`Gfx.LevelSlots` will report a bypassed LT3 file as that level's AN1. `Rom.LmLayer3Tilemap`
reads the layer-3 side; `LmGfxBypass`'s w1 still reads the AN1 side.

Read by `Rom.LmLayer3Gfx(level)` (null = not bypassed) via `Rom.LmGfxRecord`, which returns
the raw 16 words so each feature applies its own gate. `--layer3` prints the resolved LG1-4 and
dumps LG1's sheet. WRITTEN as session overrides: LG1-LG4 are ordinary bins in `Gfx.LevelSlots`
(words 15-12, below SP4 in the GFX drawer under a "Layer 3" heading), so `SetGfxSlot` repoints
them and `ProjectFile` round-trips them exactly like the FG/BG/SP slots. Setting one turns the
layer-3 bypass on and leaves the other three at 0x7F = their vanilla file; it does NOT turn on
the bit-15 bypass, which is why `LmGfxBypass` now ignores words 12-15 and `LmLayer3Gfx` owns
them. Nothing writes the record back into an LM ROM's own table yet — the override lives in the
project and is applied on build, the same as every other slot.

**Tilemaps can be EDITED, IMPORTED and BUILT.** Painting layer 3 forks the level a tilemap of its
own on the first stroke, leaving the shared (mode, option) block alone;
`EditorSession.ImportLayer3Tilemap` takes LM's LT3 file shape — a flat little-endian 16-bit map
of 0x800/0x1000/0x2000 bytes. Either way it is stored per level (`Rom.Layer3Tilemaps`,
`ProjectFile.LevelState.Layer3Tilemap`) and drawn in place of vanilla's pick.

The BUILD inserts it as an ordinary graphics file — LZ2-compressed, RATS-allocated, taking the
lowest free ExGFX id in 80-FF — and names it from the record's LT3 slot with bit 13 lit. That
needs a base carrying LM's layer-3 tilemap loader (`Rom.HasLmLayer3Tilemap`: a `JSL` replacing
`LDA $1BE3` at `$00A01F`). Our prep does NOT install it, so on a prepped base the tilemap stays
editor-only and the build says so.

**Confirmed on the console  [Mesen, level `$0C5`]**. `Layer3SmokeSetup` (gated on
`PIPEDREAM_L3_SMOKE`) builds a ROM whose layer-3 map fills every row with the FONT GLYPH for its
own row number mod 10, gives the level option 3 and layer-3 priority, and drops it on `$0C5` —
the level a new game enters. Booted in Mesen (Start, Start, B) the pattern is on screen, and the
rows read `5 6 7 8 9 0 1 2 3 4` down the edge of the intro message box: consecutive, wrapping,
exactly as authored. A custom tilemap built here reaches the SNES.

The build stamps destination 1, "Start of Layer 3" = word `$5000`, which is exactly where
`Layer3.FromBytes` draws an imported map — so the editor's picture and the console's agree.
That was a guess when the smoke test ran and is now measured, off LM's table above; a screenshot
could never have settled it, because a full 0x2000 file fills the whole window and all four
destinations look the same.

**The Layer 3 Options field  [CONFIRMED for 0 and 3; 1/2 by dropdown order]** — isolated by
`l3_c` (Blank Layer 3) → `l3_e` (Tileset Specific), with the hack installed on both. Changing
only that dropdown moved exactly TWO semantic bytes, and LM writes the value to both:

  `$05F200 + level` **bits 6-7** — the VANILLA per-level byte, read into `$1BE3` at `$05D928`
  and indexed straight into `Layer3Ptr` above. This repo parses and round-trips it as
  `MainEntrance.Layer3Option` (`MainEntrance.cs:48`) — renamed from `Layer2Setting`, which was
  wrong: LM's *Change Layer 3 Settings* dialog is what writes it, and it selects the layer-3
  tilemap, nothing on layer 2. `LevelPropertiesWindow` labels it "Layer 3 option".
  bypass record **+3, bits 5-6** — the same value mirrored into LM's own 0x20-byte record.

  value  0 = Blank Layer 3 [CONFIRMED]   1 = Water, high and low tides   2 = Water, low tide
  only (smasher if tileset is 1)   3 = Tileset Specific [CONFIRMED]
  Readback: Blank → `$05F200+105` = `00`, rec+3 = `00`; Tileset Specific → `C0` / `60`.
  1 and 2 are INFERRED from the dropdown's order. Both are tides options, and selecting either
  raises LM's destructive "Max Screens Mode Change Alert" — tides halve the screen count and
  LM deletes objects and sprites past the new limit — so pinning them wants a throwaway level.

**The ADVANCED BYPASS  [CONFIRMED + IMPLEMENTED]** — the group that answers "I don't want the
tileset's default". LM's help is explicit about why it exists: *"the actual tilemap displayed
can be bypassed from the Layer 3 GFX/Tilemap bypass dialog. But the behavior and scrolling of
the original setting will remain unless you enable the advanced bypass settings."* So a custom
tilemap on a Tileset Specific level still scrolls like the beta cage until this is on, and there
is no other per-level override of it — LM has no "use tileset N's layer 3" control at all.

It costs no new table: it rides the **spare high nibble of nine of the record's sixteen words**,
which were free because a slot's file id is only 12 bits. LM's reader at **`$0FFD9F`** (gated on
`$7FC009` being `$41`/`$42`, i.e. the record is in use) glues them into four variables through a
helper at `$0FFE82` that reads a nibble pair (`LDA [$8A],Y : AND #$F0 : DEY DEY : LDA [$8A],Y :
LSR x4 : ORA`), leaving Y two lower each call:

  `$7FC01A` = nib(w11)                bits 0-1 initial X index, 2 CGADSUB, 3 layer 3 to subscreen
  `$7FC01B` = nib(w3)<<4 | nib(w2)    never non-zero in nine controlled saves — bits 0-5 unknown,
                                      most likely "Make tides act as", which is greyed out on
                                      every level without tides
  `$7FC01C` = nib(w10)<<4 | nib(w9)   bits 0-5 = the Y offset's high bits, **bit 6 = the vertical
                                      scroll's bit 4, bit 7 = the horizontal's**
  `$145E`   = nib(w15)<<12 | nib(w14)<<8 | nib(w13)<<4 | nib(w12)
                                      bit 0 **enable**, bit 1 scroll-sync fix, bit 2 unused,
                                      bits 3-7 the Y offset's low bits, bits 8-11 horizontal
                                      scroll bits 0-3, bits 12-15 vertical scroll bits 0-3

  **The advanced group has no enable bit in w0.** Its enable is `$145E` bit 0 = nib(w12) bit 0 —
  the one thing the first controlled save moved, `$12CDC1` `00` → `10`, nothing else in the ROM
  but relocated level data.

  Y is stored **times 8** in a 14-bit signed field (`$7FC01C` bits 0-5 : `$145E` bits 3-7), which
  is exactly the -0x400..0x3FF the dialog accepts. `$109964` turns it into `$146C`, layer 3's Y
  position in pixels = Y*16. X is a 2-bit INDEX into `00 04 08 10` tiles, not a value: the game
  computes index*`$40` pixels and special-cases index 3 to `$100`, which is why the list skips
  `0C`. `$146A` gets the result.

  scroll codes  5 bits, and LM's dropdown order is NOT the code order. Measured one save per
  entry (21 of them), the list index → code map is
  `00 01 02 03 18 19 04 1A 05 | 06 07 08 09 10 11 | 0A 0B 0C 0D 0E 0F`
  for `None, Constant, Medium, Medium 2, Medium 3, Medium 4, Slow, Slow 2, Fast |
  Auto-Scroll Up/Left Slow..Fast 4 | Auto-Scroll Down/Right Slow..Fast 4`. The code space
  explains the order: 0-5 are six rate handlers, 6-0x11 the twelve auto-scroll speeds sharing
  one handler (`$109D3B` holds them: ±`$40 $80 $100 $200 $300 $400` per frame), and 0x18-0x1A
  three later rate handlers appended to the codes rather than to the list.

Read by `Rom.LmLayer3Advanced(level)` via `Layer3.ReadAdvanced`, written by
`Layer3.WriteAdvanced` (which zeroes all nine nibbles first, so turning the group off leaves no
half-read setting behind) and stamped by the build. Probe `Rom.HasLmLayer3Advanced` chases the
reader's own opening (`LDY #$17 : LDA [$8A],Y : LSR x4 : STA $7FC01A`) rather than an address.
Surfaced as the "Override the tileset's scroll and blend settings" group in the Layer 3 settings
dialog. Evidence: `.resources/layer3/l3_i.smc` (Fast/Slow, subscreen, sync fix, X=10, Y=123) and
`l3_j.smc` (Auto-Scroll Up Fast 3 / None, CGADSUB only) — both pinned in `Layer3Tests`.

Still not located: "Make tides act as", and LM's mirror of the priority flag.

Evidence: `.resources/layer3/l3_0.smc` (pre-hack baseline), `l3_b.smc` (hack + GFX bypass,
LG1=29), `l3_c.smc` (LG1=30), `l3_e.smc` (+ Tileset Specific).

## 13. Object expansion via emulation  [IMPLEMENTED — vanilla-layout ROMs]

Instead of hand-porting every bank-0D handler, the editor EXECUTES the ROM's own
`LoadLevelData` ($0585FF) in a small 65816 interpreter (src/rom/Cpu65816.cs): RAM banks
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

**SLOT FORMAT PINNED (exanim_a..n controlled saves, 2026-08-29; supersedes the TENTATIVE fields
below and the "7-byte opaque header" of §12f — per-level and global records share ONE format):**
  record +0 word   low = slot-entry count, high = alt-ExGFX file index (0-3 → files 60-63 via $03BCC0)
  record +2/+4     AND / OR masks ($7FC0FC);  +6 selector word, one trailing byte per set bit
  section          `count` offset words (slot-number order, relative to section start, 0 = unused),
                   then the slot blocks
  slot +0 byte     TYPE: 01-08 = 1-8 8x8s line, 09-0E = 12/16/20/24/28/32 8x8s line (engine oracle,
                   --exanimtypes; o = "20 8x8s" saved as 0E); 0F = 1 8x8 2bpp (DMA 0x10 bytes); 10 = 2 stacked;
                   11 = 4 as 16x16; 12 = 8 as 32x16 (p); 13 = Palette (l); 18 = Palette Rotate Right (q) —
                   LM's list order gives 14 +Working, 15 +Working stop, 16 back area, 17 back area stop,
                   19 rotate R reverse, 1A rotate L, 1B rotate L reverse [by order, unsaved]
  slot +1 byte     TRIGGER: 00 none, 01 POW, 02 Silver POW (s), 03 ON/OFF, 04 Have Star (t), 10+n Manual n,
                   20+n Custom n (r), 30+n One-Shot n; 05-0F = Timer/Yoshi variants by list order [unsaved]
  slot +2 byte     frames − 1
  slot +3 word     DEST: raw VRAM word, passed to VMADD unchanged for every type (engine oracle).
                   LM's dialog numbering: layer 1/2 tile = word/16 (BG12NBA=0); sprite 400-5FF =
                   $6000 + (t−400)×10 (OBSEL=3); layer-3 2bpp 1C00-1DFF = $4000 + (t−1C00)×8
                   (BG34NBA=4). ExAnimation.WordToLmTile/LmTileToWord. Palette types: low byte = first colour, high byte =
                   Colors−1 (q: 0385 = 4 colours at 85); BIT 15 = "use alternate ExGFX file" — frame words
                   then are BYTE OFFSETS into that file, else $7E RAM addresses
  slot +5 ..       frame words: `frames` of them, ×2 for a stateful trigger (01-0F and Custom 20-2F; POW,
                   Silver POW, Star, Custom confirmed; Manual/One-Shot do not) — second half = triggered
                   animation, LM zero-fills the unset half. Palette ROTATE types (18-1B) store NO frame
                   words: frames is the delay.
  slot placement   k: count 6, offsets 0000×5 then 000C → slot 5's block at section+0x0C (verified)
Alt file: h/n → dest word $8A00, frames 0020/00A0/0140 = (C01/C05/C0A − C00)×20; $03BCC0 = 00 80 20 →
ExGFX60 at $20:8000. Palette (l): type 13, dest word 0085, frames = raw SNES colours 7FFF 001F 06AA.
16 frames (j) = 16 words; 1 frame (i) = 1 word. Implemented: ExAnimation.ParseSlots (shared),
ReadLevel/ReadGlobal, Slot.Describe, Rom.LmAltExGfx; per-level overlay draws N tiles in the block
shape and alt-file sources from ROM (Gfx.cs). Not saved (inferred from LM's list order only): types 14-17, 19-1B; triggers 05-0F.


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

IMPLEMENTED: ExAnimation.ReadLevel / ParseSlots (src/rom/ExAnimation.cs) + `--exanim` dump;
record header confirmed by the $108700 level-setup reader (LDA $109278,X): +0 = slot count,
+2/+4 = AND/OR masks into $7FC0FC, +6 = 16-bit selector filling $7FC070, and it sets
$7FC000 = record+8 = the slot array. Per-slot (slot-relative): +0/+2 unknown words, +4 =
frameCount-1, +6 = dest byte, +7.. = frame src addrs. Verified against exanim_1/2/3.

DEST PINNED (exanim_4, dest 0x2A -> word $02A0): +0C is a BYTE (frameCount-1) and +0D a WORD
(dest VRAM word = dialog*0x10); FG tile = word/16 = dialog value. Same word/16 convention as
vanilla animation. OVERLAY IMPLEMENTED in FgTiles.OverlayAnimatedTiles: for each slot,
Overlay(DestTile, FrameSrcAddrs[phase % FrameCount]). Verified live on exanim_1 (tile 0xA0
cycles across phases) and gated (a level without ExAnimation is untouched).

TABLE BASE PER-ROM (DONE): rom.LmExAnimBase signature-scans the $108700 reader
(`A5 FE F0 ?? 3A 0A 18 65 FE 3A AA BF <base+1>`, subtract 1); -1 = no ExAnimation ASM.
Verified: exanim_1 $109278, vanilla none, ShaoBase/BigEye $12A312, DogsOfWar $14A4E2.
ReadLevel clamps the record read to the ROM so a stray pointer can't overrun (the overlay
runs on every ROM). This fixed a real crash: the old hardcoded $109278 read garbage on any
non-exanim ROM.

KEY FINDING: ShaoBase/DogsOfWar/BigEye have ZERO per-level slots — real hacks use LM's GLOBAL
ExAnimation list (runs in all levels), a SEPARATE structure not yet decoded. So the custom
animated source (AN1/AN2 -> $AD00, source tiles >= 0x780) can't be verified via the per-level
path on the hacks we have; the data lives in the global list.

### 12f. LM GLOBAL ExAnimation list  [DONE — resolved via engine emulation, rendered + animating]

Real hacks (ShaoBase/DoW/BigEye) drive animation through a GLOBAL list, not the per-level
table (they have 0 per-level slots). LOCATED + outer format read straight from the engine
setup routine (ShaoBase $13805A, disassembled with `--disasm`), so it's deterministic, not
diff-guessed. `Rom.LmGlobalExAnimPtr` / `ExAnimation.ReadGlobalRaw` / `--globalexanim` implement it.
- POINTER baked as two immediates: `LDA #bankword : STA $01 : STA $C017 : LDA #low16 : STA $00`,
  so record = bankword.hi<<16 | low16. Zero bankword -> `BEQ` skips (no global list). Byte-stable
  anchor: `A9 <bankword> F0 ?? 85 01 8D 17 C0 A9 <low16>` (bank = byte 3 back from the anchor;
  low16 = the two bytes after it). Engine address is per-ROM (exanim $10869A bank $10; ShaoBase
  $13805A bank $13). Records: ShaoBase/BigEye $10F331, DogsOfWar $12FFCB, juz none.
- RECORD outer form, exactly as the engine consumes it (DP/DBR = $7F, the $7FCxxx scratch page):
  +0 word = low byte slot COUNT (ShaoBase 0x19=25), high byte = index into a stride-3 table at
  $03BCC0 -> $7FC013/14; +2 word AND-mask and +4 word OR-mask -> $7FC0FC (DMA-enable); +6 word =
  16-bit SELECTOR, and for each set bit one trailing byte is consumed (-> $7FC070,X). So the slot
  section starts at +8 + popcount(+6) (for ShaoBase +6 = 0, section at +8 — the earlier "+8 fixed"
  was only right because the selector happened to be zero). Section = COUNT 16-bit offsets indexed
  by global slot# (relative to the section start, 0x0000 = unused), then the packed slot blocks.
- PER-SLOT block = 7-byte header + frameCount 16-bit words; length 9 (1 word) or 13 (3 words),
  CONFIRMED on ShaoBase/BigEye (13 used slots) and DoW (1 slot). Reader RATS-bounds the last block
  so it can't bleed into the next STAR block.
- FRAME WORDS: for the MULTI-frame tile-animation type the trailing words ARE 0x600-based source
  tiles (§12e convention, src = $7D00 + (tile-0x600)*0x20): ShaoBase slot6 & DoW slot0 =
  0x680/0x700/0x780, and 0x780 -> $AD00 CONFIRMS ShaoBase uses the custom source region. But the
  1-frame slots' trailing word is small/irregular (0x080, 0x420, 0x100...) and does NOT resolve as
  a tile source — its meaning is TYPE-DEPENDENT. Header byte 0 is the slot TYPE (0x04 common, 0x01
  variant) — but see the ENGINE TRACE below: the low byte is only an on/off, the real behavior
  split is on word0's HIGH byte, and the layout is a timer state machine, NOT safe to decode blind.

ENGINE TRACE (ShaoBase, --disasm — maps the processor that fills the $7FC0C0 DMA records):
- $10F2F9 = init/clear (zeroes the 8 stride-7 slots $7FC0C0/C7/CE/D5/DC/E3/EA/F1, then RTL).
  CORRECTION to §12d: its "dispatch table ~$10F32D" was a MISREAD — that region is the record's
  RATS tag ("STAR" at $10F329, size $00B6+1) + the record itself ($10F331). No table there.
- $138524 (global loop) / $138560 (per-level loop): walk the offset table via the section pointer
  the setup stashed ($7FC016 global / $7FC000 per-level), and for each used slot call the per-slot
  handler $1385AF with X = runtime slot#, [$05] = slot data ptr, ($08) = per-slot state $7FC0A0+slot.
- $1385AF per-slot handler: reads word0 = slot+0. LOW byte → JMP ($138B4A,X): entry 0 = RTS (off),
  entries 1-11 ALL → $1385CF (so the low byte is just an active flag, not a real type selector).
  $1385CF: XBA to word0's HIGH byte; 0 = inline path ($1385ED); else JSR ($138B82,X) — the REAL
  sub-dispatch, 7 sub-handlers ($88E6/EB/F0/F5/FA, $8974, $8903). The high byte drives a frame-timer
  compare (state ($08) vs a slot rate byte [$05]) that advances the current frame.
- Then it fills the stride-7 $7FC0C0 record: +0 ctrl = $138B24[X] (0x20-scaled), +2 = VRAM dest word
  (from a slot byte, offset path-dependent), +5 = $7E source bank. So dest/frame/rate ARE all in the
  slot bytes, but their offsets differ per sub-handler and are gated by the timer — a static parse of
  all 7 sub-handlers (through the SEP/REP width flips) is error-prone.

EMULATION RESOLVED (done — the per-slot format never had to be decoded). `ExAnimation.ResolveGlobal`
runs LM's own engine under Cpu65816 and reads back the resolved DMA records, exactly as §13 emulates
the object loader:
- Two engine routines, located per-ROM by their shared DBR prologue `8B A2 7F DA AB` (PHB/LDX #$7F/
  PHX/PLB). SETUP (`Rom.LmExAnimSetupEntry`, ShaoBase $138002) is the prologue followed by
  `A9 FF 8D 19 C0`; PROCESSOR (`Rom.LmExAnimProcEntry`, $1384B0) the one followed by `A4 14 CC 03 C0`.
- Seed $FE=1 (nonzero → setup takes the global path; $FE=0 sets the skip-global bit $7FC00A.4 and
  emits nothing), $14 = frame counter. CallLong(setup) once, then CallLong(proc) per frame.
- Read the eight stride-7 records at $7FC0C0: +0 ctrl (0 = tile not rewritten this frame), +2 VRAM
  dest word (tile = word/0x10), +4 3-byte source. KEY: the source is a **ROM GFX address** (ShaoBase
  $20:9Exx-A4xx, advancing per frame), NOT the $7E:AD00 buffer — so LM's global ExAnimation DMAs
  animation frames straight from ROM. This SUPERSEDES the $7D00/0x600-based reading of §12e/the raw
  slot words for the global list. Verified: ShaoBase resolves 52 tile updates over 32 frames, dest
  tiles {28,38,B0-F4}, all sources in ROM. Self-check + `--globalexanim` (now shows the emulated
  timeline).

RENDERED (done). `ExAnimation.GlobalStates(rom)` builds 4 display-phase snapshots (dest tile -> ROM
source), cached per ROM: it emulates 8 frames/phase and carries each tile's last source forward
(the engine spreads its VRAM writes over frames). `FgTiles.OverlayAnimatedTiles` applies the phase's
snapshot after the vanilla/per-level overlays, decoding the raw ROM source at RomBpp (ctrl = DMA byte
count -> ctrl/0x20 consecutive tiles). Gated on `LmGlobalExAnimPtr >= 0`, so vanilla/per-level ROMs are
untouched. Flows through the normal FgTiles.Load path, so both --render and the in-app canvas animate.
Verified: ShaoBase levels 105/106/10A visibly cycle across phases (decorative cave tiles), self-check
asserts the overlay changes a tile between phases. (Magenta squares in those renders are unrelated
object-engine markers, not ExAnimation.)

ALT-EXGFX FILES 60-63 [CONFIRMED, 2026-08-29]: the "stride-3 table at $03BCC0" indexed by the record's
word0 HIGH byte is the pointer table of LM's uncompressed ExAnimation source files 60-63 (4 x 24-bit,
FF FF FF = absent; vanilla all FF, zeroed once LM installs the ExAnimation ASM). ShaoBase: $03BCC0 =
E4 9E 20 -> file 60 at $20:9EE4 inside a STAR block, bytes identical to ExGraphics/ExGFX60.bin
(0x1000); global record word0 = 0x0019 -> 25 slots, file index 0 = 60. That is why the emulated
global sources are ROM addresses: they are alt-file DMAs. Per-slot alt flag vs RAM source (slot 6 =
0x680/700/780 RAM) coexist in one list. Full model + editor plan: reference/EXANIMATION.md.

PER-SLOT FORMAT NOW DECODED — see §12e "SLOT FORMAT PINNED". ShaoBase's 13 global slots all read alt file
60 (dest bit 15; frames = file offsets, e.g. slot 6 = 4-line, offsets 600/680/700/780 → $209EE4+600 =
$20A4E4, exactly the source the emulation reported for dest 038). The emulation stays as the timing
oracle. REMAINING (minor): (1) confirm ctrl's exact DMA-size/bit-depth semantics on a 2bpp-source hack.
(2) multiple per-level slots via the same emulation (per-level path already resolves; not yet run
through GlobalStates). (3) animation timing is approximated as 8 frames/phase; real per-slot periods
vary (some > 8) so fast/slow anims may look slightly off-cadence. Tooling: --disasm, --diff, --exanim,
--globalexanim.

### 12g. ExAnimation WRITE side  [IMPLEMENTED — prep v11]

PREP V11 transplants LM's ExAnimation engine (LmExAnimEngine; RomPrep.AppendV11Stamps): the 0xC30
engine block from exanim_1 ($108640) relocated to $1E:9400 — all 269 bytes that differ from
ShaoBase's copy ($138000) are relocations: 12 in-bank 16-bit operands, the 108-word handler table at
+B4A, 6 in-block long operands, the per-level table longs (+DF = table+1, +EA = table), the global
immediates (+5B bankword, +65 low16). Helpers $1E:A040 (MVN, hook $00A5E1) and $1E:A068 ($7FC0C0
clear, hook $008A4E); empty per-level table (0x600, FF 00 00) at $1E:A0A0; hooks $0583AD → setup,
$00A390 → NMI DMA (+RTS), $0095B6/$00A2A6/$00A5FE JSL $05BB39 → processor (+4B0); $03BCC0-CF zeroed.
LM's own footprint (exanim_0 saved-in-LM vs exanim_1) is exactly these plus relocations of its other
bank-$10 blocks. IsPrepped v11 = LmExAnimBase >= 0. Golden V11 pinned. The emulated engine resolves a
slot written by our writer (RomPrepTests.v11_exanimation_engine_runs_a_written_slot).
WRITERS: Rom.WriteLevelExAnim / WriteGlobalExAnim (ExAnimation.Encode = ParseSlots⁻¹, RATS
allocate/release, table entry / setup immediates); Rom.SetLmAltExGfx for files 60-63 ($03BCC0).
PROJECT: ProjectFile.ExAnimation (record hex per level + global), files 60-63 in Gfx["060".."063"];
RomBuilder.ReplayExAnimation shared by build and hydrate. UI: Animations mode (slot lists +
ExAnimSlotWindow). NOT transplanted: LM's $0FEFDB metadata bytes (LM bookkeeping, unknown meaning).

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
