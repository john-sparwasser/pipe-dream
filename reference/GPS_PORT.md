# Custom blocks: bringing GPS into Pipe Dream

Plan for doing Gopher Popcorn Stew's job (GPS V1.4.5, custom block insertion) inside Pipe Dream,
without shelling out to `gps.exe`. Started 2026-10-10. Background on GPS's on-ROM format is in
`ROM_TOOLING.md` § "GPS — what to look at"; this file is the plan, not the reference.

## Decisions (2026-10-10)

| question | answer | consequence |
|---|---|---|
| How are blocks assembled? | **Link Asar as a library** (its C ABI through upstream's `asar.cs`), not the CLI | Full Asar compatibility from the start; our code is GPS's logic and the UI. A native library per platform, and a licence to settle before it ships. |
| Where do blocks come from? | **Both**: import an existing GPS folder, and create/edit blocks in the app | Existing hacks move over unchanged; app-made blocks use the same files and format. |
| First milestone? | **Read what's already there** | No assembler: detect a GPS-inserted ROM and show which Map16 tiles are custom blocks. |

## What GPS does, which is what we have to do

1. Read `list.txt`: Map16 tile (or range) → block `.asm`, optional acts-like.
2. Assemble each block with Asar. A block is `db $42` (or `db $37` for wall-run) followed by
   three-byte `JMP`s, one per contact: Mario below/above/side, sprite V/H, cape, fireball, top
   corner, body inside, head inside (+ wall feet/body for `$37`).
3. Put each routine from `routines/` that a block calls (`%name()`) into freespace once, and
   give the blocks a macro that `JSL`s it.
4. Write the per-tile tables (`block_bank_byte`, `block_pointers_1`/`_2`) and the twelve
   contact stubs at `$06F690`-`$06F7E0` that `JSL block_execute`; `main.asm` is that driver.
5. Set each tile's acts-like.
6. On re-insertion, find the previous run (the `"GPS_VeRsIoN"` marker) and free what it claimed
   (RATS / `autoclean`) before claiming again.

## What Pipe Dream already has

- `RatsWriter`: RATS enumeration and allocation in the expanded area. Asar's `freespace` speaks
  the same protocol, so the two can share a ROM — but the order they run in at build matters.
- Acts-like: `RomPrep` owns a table at `$11:8000` and detects Lunar Magic's acts-like hook;
  `RomBuilder.ReplayMap16` writes the project's acts-as edits into LM's table at build.
- Projects apply their edits at **build** (`RomBuilder`), onto a pinned base ROM. Blocks fit that
  model: imported into the project, inserted at build, never patched into the base.
- `Asm`: a clean-room 65816 emitter for RomPrep's own code. Not a substitute for Asar here.

## Phases

Each phase ends shippable (or, for the Asar ones, dev-only until the licence is settled).

### 1. Read GPS blocks off a ROM — no assembler
- `rom/GpsBlocks.cs` (storage): find GPS in a ROM — the `"GPS_VeRsIoN"` marker, cross-checked by
  following the `$06F690` stub's `JSL` into `block_execute` and reading its table operands, the
  way `LunarMagic.cs` reads LM's hooks. Per Map16 tile `$200`-`$7FFF`: inserted or not, the
  block's SNES address, its header (`$42`/`$37`), and which contacts do something (a `JMP` whose
  target is a bare `RTL` handles nothing).
- Session gateway + UI: the Map16 editor's tile readout says "custom block" with its address,
  header and handled contacts, beside the acts-like it already shows; a badge on custom tiles in
  the Map16 sheet.
- Exit: on a GPS-patched ROM, every tile `gps.exe` inserted is reported with the right address
  (checked against a list.txt we inserted ourselves with `gps.exe`), and nothing else is.
- **Status (2026-10-10):** `rom/GpsBlocks.cs` reads the marker the way `gps.exe`'s `clean_hack`
  does (asm version 1 without the pages-40+ byte, 2+ with it, both pointer tables), but only
  behind the `$06F690` stub's JSL target. The Map16 editor shows a corner tag on custom tiles,
  "GPS $addr" in the gutter, the block in the hover card and on the selected tile's label.
  `GpsBlocksTests` covers it on a synthetic ROM.
- **First real hack (What The Shell 2!!, BPS on vanilla):** asm version 3, pages-40+ layout,
  965 blocks over 229 code addresses, headers and JMP tables disassemble as read. But 606 of
  the 965 sit on LM's FG pages 40+ (0x4000-0x7FFF), which this editor numbers as BG tiles, and
  35 more are past `Map16TileCount` (0xFB0) — so the editor shows 324. Reaching pages 40+ is a
  Map16 numbering change (CONTRACT §10), not a GPS one; it decides how much of a real GPS hack
  phase 5 can show. Still open: an oracle check against a `list.txt` we inserted ourselves.

### 2. Asar in the build
- Build Asar's shared library from the pinned source (`~/asar`, `5fd539c`, API 3.3.0) for
  `win-x64` and `linux-x64` in CI; a local `libasar.dylib` keeps the Mac dev loop working
  (Mac stays unshipped). Vendor `asar.cs`; wrap it in one storage-layer class.
- Asar's state is process-global: one patch at a time, behind a lock, like `SpriteRender.Capture`.
- Exit: a test patches a small `.asm` onto an in-memory ROM and checks the bytes and the RATS tag;
  ROM-less CI still passes (the test skips without the native library, as ROM tests skip now).

### 3. Insert blocks from a GPS folder
- Import copies `list.txt`, `blocks/`, `routines/` and the user's `main.asm`/`defines.asm` into
  the project folder. Running *their* driver through Asar keeps exact compatibility, and means we
  ship none of GPS's assembly.
- C# does what `gps.exe` does around it: parse `list.txt`, generate the routine macros, assemble
  each block, fill the tables, write the marker, clean up the last insertion.
- Build order with Pipe Dream's own allocations is decided here, once.
- Exit: the same inputs built by Pipe Dream and by `gps.exe` give equivalent tables and block code
  (an oracle run of `gps.exe` on the Windows PC; compare extracted tables, not whole ROMs).

### 4. Acts-like has one owner
- `list.txt` acts-like values import into the project's Map16 acts-as, so the Map16 editor and
  the block list can never disagree; the build writes them once, as it does today.

### 5. Blocks in the app
- A Blocks list (tile, file, acts-like, handled contacts), assign a block to a tile, new block
  from `template.asm`, open the `.asm` in the user's editor, and Asar's errors shown as
  `file:line: message` after a build.

### 6. ROMs that already carry GPS blocks
- A base ROM inserted with `gps.exe` before it came to Pipe Dream: phase 1 reads it; phase 3
  must free the old insertion (or adopt it) before claiming its own. Decide which, with real hacks.

## Open questions

1. ~~GPS source on this Mac.~~ GPS V1.4.5 is at `~/Downloads/GPS(V1.4.5)` (`main.asm`, and
   `src.zip` = `main.cpp` + Asar's dll loader). The marker layout above is from them.
2. **Asar's licence** (`ROM_TOOLING.md`: WTFPL, LGPL and GPL all ship, none assigned to a target).
   Ask upstream before linking it into a release; until then phases 2-5 stay behind `--dev`.
3. **GPS's own licence.** `src.zip` carries no licence text, so nothing of it is copied:
   `gps.exe`'s behaviour is ported from its documented formats and observed output, as the repo
   treats Lunar Magic.
4. **Asar version.** GPS 1.4.5 ships Asar 1.91's dll; the pinned `5fd539c` is the same 1.9x line.
   Confirm real blocks assemble identically before phase 3's exit.
5. **SA-1.** GPS resolves it at assembly time (`defines.asm`); it comes free with Asar, but needs
   an SA-1 ROM in the oracle set to be claimed.
