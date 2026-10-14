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
  965 blocks over 229 code addresses, headers and JMP tables disassemble as read. 606 of them
  sit on LM's FG pages 40-7F and 35 more past the old flat tile count (0xFB0), so at first the
  editor showed 324. Fixed by the Map16 work of 2026-10-12 (CONTRACT §7a-rev, §9d): tiles are
  numbered as LM numbers them (BG 0x8000+), exist per range, object 0x29 places pages 40-7F
  on layer 1, and their acts-like is LM's second table. All 965 now show, with their acts-like.
- **Oracle check (2026-10-13): passed.** GPS built from its own `src.zip` on the Mac (see
  "The local oracle" below) re-inserted a copy of What The Shell 2!! with this list:

  ```
  200:0130       above.asm   ; $42, Mario above only, calls %glitter()
  2FF            wall.asm    ; $37, wall feet + wall body only
  R203-214:0025  none.asm    ; template.asm: every contact a bare RTL
  4005:0130      above.asm   ; pages 40+
  ```

  `GpsBlocks.Read` then reported exactly 7 blocks (200, 203, 204, 213, 214, 2FF, 4005) and
  none of the 965 GPS had just cleaned out: headers, handled contacts and acts-like as listed,
  one address for the two uses of `above.asm` and one for the four of `none.asm`, and 2FF kept
  the hack's own acts-like (04C) since its line sets none. Not a checked-in test: the ROM is
  derived from SMW and a third-party hack, and the oracle needs a native build CI doesn't have.

### 2. Asar in the build
- Build Asar's shared library from the pinned source (`~/asar`, `5fd539c`, API 3.3.0) for
  `win-x64` and `linux-x64` in CI; a local `libasar.dylib` keeps the Mac dev loop working
  (Mac stays unshipped). Vendor `asar.cs`; wrap it in one storage-layer class.
- Asar's state is process-global: one patch at a time, behind a lock, like `SpriteRender.Capture`.
- Exit: a test patches a small `.asm` onto an in-memory ROM and checks the bytes and the RATS tag;
  ROM-less CI still passes (the test skips without the native library, as ROM tests skip now).
- **Status (2026-10-13):** `data/Asar.cs` binds the seven calls a patch needs (`apiversion`,
  `init`, `patch_ex`, errors, warnings, prints, written blocks) straight from `asardll.h`'s
  documented structs, instead of vendoring `asar.cs` (that file is LGPL; only the C interface
  files carry the WTFPL exception). One lock; `PIPEDREAM_ASAR` points it at a dev build, else the
  library is looked for beside the app. `AsarTests` pass against the local `libasar.dylib` and
  skip without it. Not done: building the library for `win-x64`/`linux-x64` in CI, so CI only
  ever skips these.
- **Speed (2026-10-13):** a patch on a 4MB ROM took 13.7 ms, 77% of it a CRC Asar takes over the
  whole ROM after every patch for the symbols file alone. Taking that CRC only when a symbols
  file is asked for: 2.3 ms.
- **Quasar (2026-10-14):** our fork of Asar, [john-sparwasser/quasar](https://github.com/john-sparwasser/quasar),
  branch `quasar`, where the speed work lives (the lazy CRC is its first commit). It keeps
  Asar's API and library name, so it drops in where Asar would and stays replaceable. Under the
  LGPL it is a modified Asar: its README says so, Alcaro stays credited, and the public fork is
  the source we offer, so it must stay up and its pinned commits reachable while a release ships
  them. `tools/asar/build.sh` builds the pinned commit; `QUASAR_SRC=<checkout>` builds a working
  tree. Not offered upstream.

### 3. Insert blocks from a GPS folder
- Import copies `list.txt`, `blocks/`, `routines/` and the user's `main.asm`/`defines.asm` into
  the project folder. Running *their* driver through Asar keeps exact compatibility, and means we
  ship none of GPS's assembly.
- C# does what `gps.exe` does around it: parse `list.txt`, generate the routine macros, assemble
  each block, fill the tables, write the marker, clean up the last insertion.
- Build order with Pipe Dream's own allocations is decided here, once.
- Exit: the same inputs built by Pipe Dream and by GPS give equivalent tables and block code
  (the local oracle below; compare extracted tables, not whole ROMs).
- **Status (2026-10-13): the inserter, not yet the import or the build.** `data/GpsInsert.cs`
  does gps.exe's job in **one** Asar patch where gps.exe runs one per block file: clean the
  last insertion, the user's `main.asm` (tables as in-memory placeholders, filled in after the
  patch from the blocks' labels), routine macros, then each distinct block in freecode and its
  own namespace. Asar frees autocleaned space in pass 0, so the new insertion reuses the old.
  On What The Shell 2!! with the oracle list: every tile's header, handled contacts and
  acts-like, and every block's code (JMPs taken relative, JSLs followed), match gps.exe's
  output; gps.exe re-inserts cleanly over ours; ours over ours twice claims the same RATS
  bytes. 27 ms warm, including cleaning out the hack's 965 old blocks; per block, as gps.exe
  does it, that hack's 229 files would be ~3 s. `GpsInsertTests` run it through a minimal
  driver of our own (GPS's files have no licence).
  Differences from gps.exe, all from sharing one patch: two block files defining the same
  macro collide, and a define set by one block is visible to later ones. Not done: no `.dsc`,
  no `-O1`/`-O2`, no BTSD removal; import into a project and running it at build.
- **Import (2026-10-14):** `GpsInsert.Import` copies list.txt, main.asm, defines.asm, blocks/
  and routines/ into `<project>/gps/` (Debug ▸ Import GPS folder…). It checks list.txt parses
  and every listed block exists, and stages the copy before swapping it in, so a bad folder
  keeps the last import. The files sit beside project.pdp, not in it, so blocks stay editable
  in the user's editor; sharing a project with blocks now means sharing gps/ too.
- **Build (2026-10-14):** `RomBuilder.Build` inserts `gps/` first, before `ReplayMap16`, so
  the old insertion's space is freed before Pipe Dream allocates and the project's acts-like
  edits land over list.txt's. An Asar error fails the build with `file:line: message`; no
  library skips the blocks with a warning. ~17 ms on What The Shell 2!! with the oracle list.
  **A prepped base is refused.** Prep stamps LM's acts-like core (`LM 10 01`), so GPS's own
  check passes, but not LM's custom-block dispatch (`LM 11 01`, `$06F65C`), which is what calls
  the stubs GPS writes at `$06F690`; and prep's acts-like remap reads `$11:8000` directly, while
  GPS's `main.asm` repoints `$06F624` to a table of its own. Built anyway, the blocks insert
  and never run. Custom blocks on a vanilla project need prep to carry that dispatch and to
  read acts-like through `$06F624`: a prep version of its own.

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

## The local oracle: GPS and Asar built on the Mac

No Windows PC needed. `main.cpp` is portable (its Asar loader `dlopen`s `./libasar.dylib`), it
expects Asar API 303, and the pinned `~/asar` (`5fd539c`, v1.91+16) is API 3.3. Built in a
scratch dir, never in the repo (GPS has no licence):

```sh
cmake ~/asar/src -DCMAKE_BUILD_TYPE=Release -DASAR_GEN_EXE=OFF -DASAR_GEN_DLL=ON \
      -DASAR_GEN_LIB=OFF -DASAR_GEN_EXE_TEST=OFF -DASAR_GEN_DLL_TEST=OFF && cmake --build . -j8
clang -c asar/asardll.c && clang++ -std=c++17 main.cpp asardll.o -o gps     # from src.zip
```

GPS works in the current directory: copy `main.asm`, `defines.asm`, `routines/`, a `blocks/`
and `list.txt` beside `gps` and `libasar.dylib`, then `./gps rom.sfc`. It refuses a ROM Lunar
Magic has not saved (it reads LM's acts-like pointer at `$06F624`), so vanilla will not do, and
pages 40+ need LM to have created them (`$06F63C` != FF). `main.cpp` is dated the day before
the shipped `gps.exe`; one byte-compare against `gps.exe` on Windows would confirm the build
matches, but nothing waits on it.

## Open questions

1. ~~GPS source on this Mac.~~ GPS V1.4.5 is at `~/Downloads/GPS(V1.4.5)` (`main.asm`, and
   `src.zip` = `main.cpp` + Asar's dll loader). The marker layout above is from them.
2. **Asar's licence.** The repo's `LICENSE` (read 2026-10-13) settles most of it: Asar is LGPL
   v3+, the DLL interface files are WTFPL, and a dynamic library shipped as a separate,
   replaceable file, with Alcaro credited and source offered, meets the LGPL. Still the owner's
   call before a release ships it; until then phases 2-5 stay behind `--dev`.
3. **GPS's own licence.** `src.zip` carries no licence text, so nothing of it is copied:
   `gps.exe`'s behaviour is ported from its documented formats and observed output, as the repo
   treats Lunar Magic.
4. **Asar version.** GPS 1.4.5 ships Asar 1.91's dll; the pinned `5fd539c` is the same 1.9x line.
   Confirm real blocks assemble identically before phase 3's exit.
5. **SA-1.** GPS resolves it at assembly time (`defines.asm`); it comes free with Asar, but needs
   an SA-1 ROM in the oracle set to be claimed.
