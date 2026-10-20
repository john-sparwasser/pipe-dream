# Custom sprites: bringing PIXI into Pipe Dream

Plan for doing PIXI's job (custom sprite insertion, JackTheSpades/SpriteToolSuperDelux 1.43)
inside Pipe Dream, with a library window like Custom blocks and Patches and a sprite editor for
each sprite's JSON. Started 2026-10-19. Modelled on custom blocks (`GPS_PORT.md`) and patches
(`PATCHES.md`). Background on PIXI's files is in `ROM_TOOLING.md` § "PIXI — what to look at";
this file is the plan and its decision log.

## Decisions (2026-10-19)

| question | answer | consequence |
|---|---|---|
| Whose driver? | **Our own**, written from PIXI's documented on-ROM format, as the block driver was | PIXI is **GPL-3.0** (root `LICENSE`, covering `asm/` and `routines/`); Pipe Dream is MIT, so none of PIXI's ASM ships or is copied. The driver is written from the format and behaviour described below, not from `main.asm`'s text. Works with no PIXI folder. It keeps PIXI's on-ROM layout (the `STSD` header, the 0x10-byte table), so PIXI sprites run unchanged and anything that reads a PIXI ROM finds ours. |
| Where do sprites live? | **In project.pdp**: a sprite library (name → config JSON, ASM, included files) and, separately, which slot runs which (global `00-DF`, per-level `B0-BF`) | Sharing the .pdp shares everything, as with blocks and patches. A sprite can sit in the library in no slot and is then not inserted. Asar reads it all as in-memory files. |
| Which kinds first? | **The SPRITE: section**: normal `00-BF`, per-level `B0-BF`, shooters `C0-CF`, generators `D0-DF` | These are what a level places and what the config describes. Cluster, extended and the misc kinds are ASM-only with MAIN-only pointer tables, and come in a later phase. |
| CFG or JSON? | **JSON only.** A `.cfg` converts at import into the JSON PIXI's CFG Editor would write | One format, one editor. The CFG's six lines map onto JSON fields one to one (below); CFG export can come later if anyone needs it. |
| How is a sprite's look edited? | **As Map16 tiles, natively**: 16×16 tiles at pixel offsets, as PIXI's JSON and Lunar Magic hold it, with a quadrant editor for the sprite's own Map16 tiles | The JSON round-trips exactly. The window shares its preview, sheet and tile list with Debug ▸ Sprite tiles by extracting them into one control both windows use. |
| Routines? | **PIXI's are not shipped** (they are GPL). **Ours are (2026-10-20):** MIT versions of all 27 of PIXI 1.2.x's, clean room, used for any routine the project has not imported; one imported from a PIXI folder (the user's own copy, stored in the project) still wins | Until then this was the position GPS blocks are in. How ours were written and checked against PIXI's is under phase 5 and in `PIXI_ROUTINES.md`. |

## What PIXI does, which is what we have to do

Facts from the research of 2026-10-19 against PIXI master `734b8ed` (paths relative to that repo).
Format facts only; promote to `CONTRACT.md` as **[CONFIRMED]** once measured in Mesen.

1. **Read the list** (`list.txt`): `NN file`, per-level `LLL:NN file` (B0-BF), section headers.
   In `SPRITE:`, slots below C0 read from `sprites/`, C0-CF from `shooters/`, D0+ from
   `generators/`; only C0-DF run as custom shooters/generators.
2. **Read each config.** CFG lines: type (0 tweak, 1 custom, 2 shooter/generator), acts-like,
   the six tweaker bytes `$1656 $1662 $166E $167A $1686 $190F`, extra property bytes 1-2, ASM
   file, `a:b` extra-byte counts (extra bit clear:set, hex, ≤ 0C). JSON: the same, with each
   tweaker byte an object of named fields (`src/json_const.h` has the exact names, typos
   included: "Don't disable cliping when starkilled"), byte counts in **decimal**, plus
   `Map16` (base64, 8 bytes per 16×16 tile: TL, BL, TR, BR tile words), `DisplayType`
   (`XY`/`ExByte`), `Displays` (description, extra bit, position nibble X/Y 0-F or extension
   byte Index/Value, then text or `Tiles`: signed pixel offsets + Map16 tile number), and
   `Collection` (name, extra bit, up to 12 extra-byte values: Lunar Magic's sprite list).
3. **Assemble each sprite** with Asar: a wrapper giving it SA-1/base defines, the shared
   routines as `%Name()` macros, its folder's `_header.asm`, then `freecode` and the sprite.
   INIT and MAIN come back as Asar prints (`print "INIT ",pc`); status entry points
   (`CARRIABLE`, `KICKED`, `CARRIED`, `MOUTH`, `GOAL`) likewise.
4. **Insert routines once**: each routine used is put in freespace once and its address kept
   in a table, so every sprite's macro `JSL`s the same copy.
5. **Write the tables**: the header at `$02FFE2` (`"STSD"`, version, flags, `dl` table,
   per-level pointers, status pointers); the global table, 0x100 × 0x10 bytes (type, acts-like,
   six tweakers, INIT 24-bit, MAIN 24-bit, two extra property bytes; empty = `$018021`, an RTL);
   per-level tables keyed by `$010B`; Lunar Magic's sprite size table (`$0EF30C: dl Size : db
   $42`, 0x400 bytes, `3 + count` per (extra bit, number)).
6. **Hook the game**: init `$018172`, main `$0185C3`, the extra-bits keepers (`$02A963`,
   `$02A94B`, `$02A9C9`), the table setter `$0187A7`, shooter/generator load and run, the
   status handler `$018127`, and the sprite-data offset `$02A846` that reads the size table.
   A sprite is custom when its level-data extra bit `$08` is set.
7. **Fix the levels** whose sprite records changed length (PIXI's MeiMei), since a level's
   sprite list is walked with the size table.
8. **Write Lunar Magic's aux files** (`.ssc`, `.mwt`, `.mw2`, `.s16`) so LM shows the sprites.

## What Pipe Dream already has

- **Asar as a library**, in-memory files, `Result.Blocks` for overlap warnings; the build's
  order (blocks, then patches, then the editor's own data) and its overlap bookkeeping
  (`RomBuilder.PrepOwned`, `PatchInsert.Overlaps`).
- **Extra bytes and LM's size table**: `SpriteData` parses per-(extra bits, number) record
  lengths; `RomBuilder` writes the size table from placed sprites; Sprite data edits a
  placement's bytes (`SpriteDataWindow`).
- **Detection**: `LunarMagic.HasPixiSpriteHook` (`$0185C3` is a JSL) and `PixiCustomTable`.
- **Drawing**: `SpriteRender`/`SpriteOverlay` draw vanilla sprites from `SpriteDisplay.json`;
  custom ones are a red-X box today (`SpriteData.cs` "PIXI custom sprite marker").
- **The library pattern**: `EditorSession.Gps.cs`/`.Patches.cs`, `BlocksWindow`/`PatchesWindow`,
  `BlockEditWindow` (ASM editor with Asar colouring and lint), the external-editor round trip.
- **`SpriteTilesWindow`** (Debug ▸ Sprite tiles): preview with drag/lasso, tile list, SP sheet
  with slot pins — the parts the sprite editor reuses.

## Phases

Each phase ends shippable.

### 1. Model, import, session API — no insertion
- `ProjectFile.Sprites` (library: name → `{ Config, Source, Files }`, `Config` the JSON) and
  `ProjectFile.SpriteSlots` (global `NN` and per-level `LLL:NN` → library name).
- `data/PixiConfig.cs`: parse and write the JSON (every field, named tweaker bits), CFG → JSON.
- Import a PIXI folder: `list.txt` → slots, configs → JSON, each sprite's ASM and the files it
  includes, `routines/` and `ExtraDefines/` into the project as user data.
- `services/EditorSession.Sprites…` (name chosen to sit beside the existing sprite partials):
  list, new, import, set config, set source, delete, assign slot — each returning a status.
- Tests: CFG → JSON for every field; JSON round trip byte-identical on PIXI's own examples;
  folder import (list.txt with per-level lines, shooters, generators, includes).
- **Status (2026-10-19): done.** `data/PixiConfig.cs` (parse, write, `FromCfg`; the tweaker
  fields are a table of PIXI's names and bits), `data/PixiImport.cs` (slots, one sprite, a
  folder), `ProjectFile.Sprites`/`SpriteSlots`/`PixiFiles`, `services/EditorSession.CustomSprites.cs`,
  `tests/PixiTests.cs`. The round trip is ours, not PIXI's: written JSON reads back and writes
  again byte for byte, laid out as PIXI's example (two-space indent, every key); a file from
  elsewhere is normalised to that layout on import, keys we do not model dropped. A sprite keeps
  its file's name ("sub/thing" from a subfolder); a shooter sharing a sprite's name becomes
  "name (shooter)". A .cfg gets the two "(extra bit clear/set)" list entries PIXI shows it under.
  Cluster, extended and the misc sections of list.txt are skipped with a count.

### 2. Our driver and the build step
- `data/spritedriver/`: hooks, tables and the `STSD` header for normal sprites, per-level
  B0-BF, shooters and generators; size table from the configs' counts; routines inserted once.
- `RomBuilder`: insert after custom blocks, before patches (a patch may hook a sprite). Errors
  fail the build naming the sprite; overlaps with prep's hooks are checked like patches'.
  **Prep's hooks near PIXI's must be checked first**: prep owns `$05D8F5` (sprite bank) and
  LM's level-word mirror at `$05D8E2`; PIXI writes `$010B` from `$05D8B9`.
- Levels: every level whose sprites use a number whose record length changed is re-encoded
  (MeiMei's job), not only the levels the project edits.
- Tests (Asar): a sprite's INIT/MAIN land in the table; tweakers land; a disabled/unslotted
  sprite does not; per-level overrides global; size table matches counts. **Mesen**: a custom
  sprite placed in level 105 runs (its MAIN writes a marker byte), 3/3.
- **Status (2026-10-19): done** — global sprites, per-level B0-BF, shooters and generators.
  Per-level sprites use a layout of our own, not PIXI's (the header says none): the table is
  the 0x100 global entries then 16 per level that has its own, and `LevelBlocks` (a word per
  level, keyed by `$010B`) gives a level's block less B0's offset, so every lookup is base +
  number × 16 (`EntryOf`). With any per-level sprite, B0-BF records are 4 extra bytes in every
  level, as PIXI has them: the size table is per number, not per level. Custom shooters and
  generators branch off at `$02A866`, SMW's first test of the number, before it would read
  D0-D9 as its own generators or DA-DF as shells: a shooter keeps its number (C0-CF) in `$1783`
  where SMW's are 1-2, and runs from `$02B395` with its timer counted down as SMW's are (PIXI's
  shooters test and set it, never count it); a generator keeps D0-DF in `$18B9` where SMW's are
  1-F, and runs from `$02AFFE` unless sprites are frozen. `$02B3AB` could not be the shooter
  hook: the `JSL $0086DF` after it reads its own return address as the table.
  Measured in Mesen, 3/3 each, on level 105: level 105's own B0 ran where a global B0 also
  exists; a custom shooter C0 and a generator D0 each ran MAIN 60 times in 60 frames.
- Global sprites, first pass:
  `data/spritedriver/` (`main.asm`, `defines.asm`), `data/SpriteInsert.cs`,
  `RomBuilder.InsertSprites`, `tests/SpriteInsertTests.cs`. What the vanilla code at each hook
  turned out to be, read with `--disasm` on a v34 base:
  - `$02A846` is Lunar Magic's record step on a prepped base (`JML $1E9185`: `INX : INY : INY`,
    then a BPL that rebases `$CE` past `$80`), fixed at 3 bytes — nothing in the game read the
    size table before the driver. Ours steps by the table and chains to LM's code past those
    three bytes (or to `$02A82E` on SMW's own loader).
  - `$02A94B`/`$02A963` store `byte0 AND #$0D` into a position-high byte (`$14E0` vertical,
    `$14D4` from LM's horizontal path, whose caller then ANDs A with 1): the extra bits would
    land in the position. Ours keeps bit 0 there and the extra bits in `!extra_bits`.
  - `$02A9C9` is the spawn's `JSL $07F7D2` (clear the slot, then SMW's tweakers by `$9E`);
    `$07F785` is that clear's tail, where the custom state is cleared with it.
  - `$0187A7` is sprite 9B's INIT stub (via `$0182B3`); it moves to a copy at `$01FFF0`, and
    `$0187A7` becomes the "give this slot its custom tables" call spawning sprites make.
  - Custom INIT/MAIN are entered through `JML [$00]` from a JSL, and come back to bank 01 through
    the RTS at `$0185C2`.
  Measured in Mesen on level 105 (walk left, enter): INIT stored `$11` and the placement's
  first extra byte `$AB`, MAIN counted exactly 60 in 60 frames — 3/3; the level's own sprites
  still load and live walking right. Testing this found an existing bug, fixed separately: the
  build wrote a level's sprites unsorted, and the loader never reaches a record placed left of
  an earlier one. Not yet: the `CARRIABLE`/`KICKED`… status entry points, `VERG` checks, the
  silver-POW coin path reading SMW's table by the custom number.

### 3. Sprites window and the level editor
- ASM ▸ Custom sprites…: rows by slot (number, name, type, acts-like), New…, Import (file or
  PIXI folder), Edit config, Edit ASM, Open in editor, Remove, assign slot.
- The Sprites catalog lists the project's custom sprites by their `Collection` names; placing
  one sets the extra bit and the collection's extra bytes; the overlay draws its `Displays`
  instead of the red X.
- **Status (2026-10-19): done.** `ui/CustomSpritesWindow` (ASM ▸ Custom
  sprites…): New (sprite, shooter or generator), Import of .json/.cfg or a PIXI folder, Slots…
  as a list ("1A, 105:B0"), Edit ASM, Edit config (the JSON, in the block editor, until phase
  4 below), Open in the text editor, Remove. The Sprites catalog adds one item per Collection entry of
  each sprite in a global slot or the level's own B0-BF, labelled "· custom", carrying extra
  bits 2 (3 with the extra bit) and the entry's bytes; placing it keeps them. A custom sprite
  shares its number with SMW's, so the drawer re-arms by label and the spawn overlay's
  thumbnails keep to SMW's. The level overlay draws a custom placement as the custom-sprite box
  whenever the project has custom sprites (the base never has our driver, so `HasPixiSpriteHook`
  alone said "vanilla sprite" and emulated the wrong one).
- **Drawing (2026-10-19): done.** `rom/SpriteMap16`: a placement shows its config's display
  for its extra bit and position nibbles (or, with `ExByte`, the display whose `Index` extra
  byte holds `Value`), else the first for its extra bit, else the first; a text display, or a
  sprite with no config (a base's own PIXI sprites), keeps the box. Each Map16 tile is four
  8x8 tiles drawn by `SpriteRender.Draw` from `LoadMap16Tiles` (SP1-SP4, then GFX33). The
  catalog's custom entries get the same as thumbnails, and an edit to a config or slot redraws
  the level's sprites. Not yet: `GFXInfo` (a display's own GFX files; the level's are used)
  and translucency.

### 4. The sprite editor
- Extract `SpriteTilesWindow`'s preview, tile list and sheet into a shared control.
- The sprite window: type, acts-like, the six tweaker bytes as named checkboxes and fields,
  extra property bytes, extra-byte counts; Displays (per extra bit and position, or per
  extension byte) edited as Map16 tiles at pixel offsets; the sprite's own Map16 tiles with a
  four-quadrant editor (tile, palette, flips); the Collection list.
- Tests: editing each field writes the JSON PIXI's parser reads back the same.
- **Status (2026-10-19): done.** `ui/SpriteConfigWindow` (Custom sprites ▸ Edit config) edits
  a working copy of the config; Save writes it as JSON, JSON… shows that text with the edits
  in it and takes back a hand edit PIXI can read, and a config PIXI cannot read still opens as
  text. Tabs: Sprite (type, acts-like, property bytes, extra-byte counts, the six tweaker
  bytes as `TweakFields`' check boxes and numbers), Look (the displays and how one is picked;
  each display's tiles arranged on `TileArrangeView`, the preview `SpriteTilesWindow` now
  shares, and picked off the whole sprite Map16 on a `TileSheetView`), Own tiles (from 0x300:
  each quarter's 8x8 tile, palette and flips, picked off SP1-SP4 and GFX33; four clicks build
  a tile), List (the `Collection` entries). Save is on while the config's JSON differs from the
  saved one. Not yet: `GFXInfo`.

### 5. Parity and the rest
- Status entry points, the 255-sprites-per-level remap, Lunar Magic's aux files on build (all
  done; see below).
- Cluster, extended, minor extended, bounce, smoke, spinning coin, score.
- MIT versions of the routines (done; see below).
- **Status entry points (2026-10-19): done.** A sprite may print `CARRIABLE` (status 09),
  `KICKED` (0A), `CARRIED` (0B), `MOUTH` (07) and `GOAL` (0C), as PIXI's README has them; the
  printed code then runs in that status in place of SMW's, entered with the data bank set to its
  own, and a status it does not print runs SMW's code as before. The second extra property
  byte still overrides: bit 7 runs MAIN instead, bit 6 runs MAIN first (then the sprite's entry
  or SMW's). The pointers are a `StatusTable` of our own, laid out as the sprite table (five
  longs and a pad byte per entry, per-level blocks included) so `EntryOf` finds both; the
  `STSD` header's status pointer stays `$FFFFFF`, since a tool reading it would expect PIXI's
  layout. Measured in Mesen on level 105, 3/3: a sprite whose INIT sets status 09 ran
  `CARRIABLE` 120 times in 120 frames with the data bank at its code's ($90), and not MAIN.
- **Lunar Magic's aux files (2026-10-19): done.** `data/SpriteAuxFiles`: every build writes
  `<name>.ssc`, `.mwt`, `.mw2` and `.s16` beside `build/<name>.smc` from the global slots'
  configs (a build without any removes the last build's). Formats from Lunar Magic 3.40's help
  (`Lunar Magic.chm`: "Custom Tooltips for Sprites", "Custom Collections of Sprites", "Custom
  Map16 for Sprites"), then compared with PIXI 1.43's own output for two real sets on this
  machine: Tempest's two JSON sprites came out **byte-identical** in all four files; QLDC2023's
  31 JSON sprites identical in `.mwt`, `.s16` and every tile line, with two differences that are
  PIXI bugs we do not copy: PIXI writes a description's line break raw into the `.ssc` (ending
  the line mid-text), where we write LM's `\n` escape, and it fills extra bytes past a
  Collection entry's four from stale memory (`.mw2` held the bytes of "tra", "By", "Ext"), where
  we write the entry's. Facts the help leaves out, as PIXI writes them:
  - `.ssc` fields are **space**-separated (the help says tab; LM reads both), lines end CRLF;
    `.mwt` is tab-separated, CRLF, the number only on a sprite's first entry.
  - TYPE is Y nibble, X nibble, then `20`/`30` (extra bit clear/set) + 0 description, 2 tiles,
    8 GFX files; by extension byte, the byte's number counting the three record bytes
    (`Index` + 3), two hex digits of value, then the same. Tile numbers and GFX files in hex.
  - `.mw2` records are `70|extra<<2|1, 70, number, bytes…` — every one flags "next group", so
    each Collection entry is its own list item — after a `00` header, ending `FF`.
  - `.s16` starts at sprite tile 0x400 and is 0x10 pages (PIXI's size; LM saves 0x38). Each
    sprite's own tiles go in slot order and its display tiles from 0x300 are renumbered there.
  - PIXI writes nothing for a `.cfg` sprite; ours are JSON by then, so a converted one is listed
    under its two "(extra bit clear/set)" entries.
  Not done: appending a base's own aux files (a project copies only the ROM), the overworld's
  `.sscov`/`.s16ov`, extmod. Not yet opened in Lunar Magic itself. Found on the way: a config
  whose `Map16` is not base64 (QLDC2023's CapeShell says "Cape Shell") was refused at import;
  it now reads as no tiles, as PIXI does.
- **Cluster and extended sprites (2026-10-19): done.** ASM alone (no config), slotted in lists of
  their own, `cluster NN` / `extended NN` (00-7F); a PIXI folder's `CLUSTER:` and `EXTENDED:`
  sections import into them, with `cluster/` and `extended/` `_header.asm`. Each must print
  MAIN; an extended one may print `CAPE`. The interface, from PIXI's README and real sprites
  (Ladida's cluster package, UberASM's defines): a custom number is the list number plus
  `!ClusterOffset` = `$09` / `!ExtendedOffset` = `$13`, the sprite or its spawner storing it in
  `$1892` / `$170B` itself (bit 7 of a cluster number is its extra bit). Those offsets are SMW's
  table sizes, read off its dispatchers: the cluster jump table at `$02F825` has entries 00-08,
  the extended one at `$029B2B` 00-12. Hooks: `$02F815` (the cluster loop's `LDA $1892,X : BEQ`)
  and `$029B16` (the extended loop's `LDA $170B,X : BEQ`) send a custom number to its MAIN from
  `ClusterTable` / `ExtendedTable` (0x80 longs each); an extended one's timer `$176F` counts down
  first, as SMW's do. Entered with X = Y = the slot and the data bank set to the code's: a real
  cluster sprite (`lava.asm`) indexes with Y and reads its tables without setting the bank.
  SMW reads the number as an index in one other place, found by scanning for every
  `$170B`/`$1892` read: the cape's check of extended sprites at `$029636`, whose clipping
  (`$02A519`) is a table by number. That is hooked too: a custom one runs its `CAPE`, or nothing
  (PIXI's README: without one "cape interaction ... do nothing"). The rest compare against fixed
  numbers or look for a free slot. Measured in Mesen on level 105, 3/3: a sprite's INIT put
  cluster 02 in cluster slot 3 and extended 00 in extended slot 5; the cluster MAIN ran 121
  times and the extended 119 in 120 frames (the extended loop runs before the spawn that frame),
  with the data bank `$90` and the slot in Y (cluster) and X (extended). The cape path is in the
  table test only. (The minor kinds came next; see below.)
  Defines checked against PIXI's own defines file (`asm/sa1def.asm`, read for names and values
  only): `!ExtendedSize` is `$08` (`!ExSprSize`+1 — spawning keeps off slots 8-9, Mario's
  fireballs), cluster speed tables are `!cluster_misc_1e52`… by address, `!Bank8` is `$80`.
  The same file gives the minor kinds' offsets for later: minor extended `$0C`, bounce `$08`,
  smoke `$06`, spinning coin `$02`, score `$16`; its spawn routines (`routines/Spawn/*Generic`)
  store A as the number, so the caller adds the offset. Since PIXI 1.41 its `!1938` is the
  remapped `$7FAF00` and disabling the remap is an error; ours is too (the remap, below).
- **Minor extended, bounce, smoke, spinning coin and score sprites (2026-10-19): done.** As
  cluster and extended: ASM alone, MAIN only, slotted as `minorextended NN`, `bounce NN`,
  `smoke NN`, `spinningcoin NN`, `score NN` (the kind is its list.txt section's name, lower
  case), imported from those sections out of `misc_sprites/<kind>/` with its `_header.asm`.
  PIXI's README names the sections and folders and nothing more: not the entry points, the
  registers or how the number is stored, so the interface is the cluster one, and the offsets
  are SMW's counts (above). Every place SMW reads the five number tables (`$17F0`, `$1699`,
  `$17C0`, `$17D0`, `$16E1`) was listed by scanning for each read; all but one per kind look
  for a free slot or compare with a fixed number, and the one is each kind's dispatch, hooked
  at its `LDA number,X : BEQ`:
  - minor extended `$028B69` (12 entries in SMW's jump table, matching `$0C`);
  - bounce `$02904D`, the top of the dispatch the bounce/smoke loop JSRs; a custom one's
    timer `$16C5` counts down first, as the dispatch does for SMW's;
  - smoke `$0296C0`, whose next instruction drops bit 7 (a flag), so a custom smoke number
    is masked the same way and its list stops at `7F - $06` (cluster's, for its extra bit,
    at `7F - $09`; the rest have 0x80);
  - spinning coin `$0299D7` and score `$02ADBD`, loops that JSR to SMW's one routine each
    (score's reads its tables by number).
  Each runs MAIN through `CallLong`, every frame, frozen or not (SMW's check `$9D`
  themselves), with X = Y = the slot and the data bank its code's; minor extended also sets
  `$1698` to the slot as SMW's loop does. The table loads are a `%call_listed()` macro now,
  cluster and extended included (same bytes). Measured in Mesen, 3/3 each, in the level the
  probe's walk left reaches: a number written into a slot of each kind ran its MAIN 60 times
  in 60 frames with X the slot and the data bank `$90`; SMW's own smoke 01, score 01 and
  spinning coin 01 written the same way ran their own code and emptied their slots. The probe
  must wait for the level's message box (`$1426`) to close: while it is open no sprite runs.
- **255 sprites a level (2026-10-20): done.** SMW's load table `$1938` (a byte per level sprite
  list record, by its index `$161A,x`: spawned, so not again) is 128 bytes, so a 129th record
  wrote into the screen exits at `$19B8`. As PIXI has it since 1.2.15 (its changelog and
  `asm/sa1def.asm`, names only): the table is `$7FAF00`, 256 bytes, `!1938` means it, and index FF
  stays "not from the level". Every instruction addressing `$1938` was found by scanning a built
  ROM for each absolute opcode with that operand (no long ones exist; the three hits in bank 0D
  are tile data; Lunar Magic's bank-1E loader never touches it and jumps back only to
  `$02A846`/`$02A84B`/`$02A856`). Nine sites, each now calling the driver ("The load table" in
  `main.asm`): the loader's test-and-mark `$02A856`; the level-load clear `$02ABF2` (64 bytes in
  SMW, 128 after Lunar Magic's `$02ABF3 = $7F`, 256 now); a generator's record left unmarked
  `$02A8BB` (and the driver's custom-generator branch); no free slot `$02A936`; `$02FAE6` by
  `$0FBC,y`; and the six `LDA #$00 : STA $1938,y` that free a record (`$01AC9C` the off-screen
  erase, `$02AB99` a shooter's, `$02D088`, `$02FF15`, `$038712`, `$03B8BA`). The editor never
  capped a level at 128. Lunar Magic is told the limit is 255 by clearing bit 0 of `$0FFFE0`:
  its help ("Custom Sprite List Sizes") says "0x801E0 PC", and Lunar Magic's PC offsets count the
  copier header (its `0x7750C` is the `$0EF30C` size-table pointer); every PIXI ROM on this
  machine has `$FE` there, an LM-saved hack without PIXI `$FF`. Measured in Mesen, 3/3 each, on
  level 105: the probe sprite spawned with its entry marked, both as record 00 and as record 8C
  (140, after 140 generator records); nothing wrote `$7E1938-$7E19B7` after boot; and a level
  sprite pushed two screens off was erased by its own off-screen check and its entry went from
  1 to 0, so it can come back. Asar here assembles `BNE $02A846` (an absolute operand) as an
  offset of `$46`; branch to a label, or JML.
- **Shared routines of our own (2026-10-20): done.** All 27 of PIXI 1.2.x's routines, MIT, in
  `data/spritedriver/routines/`, and its `sa1def.asm` macros (`BES`, `BEC`, `Between`, `debugmsg`,
  the two `define_*`) in `defines.asm`; not `invoke_snes`/`invoke_sa1`, which are SA-1 only. Clean
  room, as the driver: a separate pass read PIXI's routines and wrote their observable behaviour
  into `reference/PIXI_ROUTINES.md` (no code); ours were written from that and SMW's own code,
  tables from SMW's ROM at assembly time (`read1()`) or from a formula (`Aiming`'s 1/√k). A build
  uses ours for any routine the project has not imported; an imported one still wins.
  `PixiRoutineTests` runs each of ours against PIXI's (read from a project of the user's at test
  time, never shipped) from 2000 random states, on the same vanilla ROM in `Cpu65816`, and compares
  every register, flag, the stack and all of RAM; it was run over 12 seeds. Where it disagreed with
  the contracts, PIXI was measured black-box and ours matched to it, except one PIXI bug kept out on
  purpose (`ExtendedHurt` leaves DB 0 for SMW's `$02A469`): the list is at the end of
  `PIXI_ROUTINES.md`. In Mesen, 3/3 each, a real sprite (TestRom's FlyingQuestionBlock: GetDrawInfo,
  SubOffScreen, SubHorzPos, SpawnSprite, `%BEC`) built once with ours and once with PIXI's
  routines: alive, at the same position after 120 frames, the same 4 tiles in OAM.
  Found on the way: PIXI's routines use macro labels (`?+`), which Asar takes only inside a macro,
  so an imported PIXI 1.2.x routine set had never assembled; each routine is now included inside a
  macro of its own. `Cpu65816` gained the CPU's multiplier/divider, the PPU's signed multiply and
  the stack-relative ALU forms, all of which the routines use.
  Not done: the newer PIXI's routines in subfolders (`Spawn/`, `Bounce/`, `Score/`… that the minor
  kinds' `_header.asm` call), SA-1.

## Open questions

1. **Lunar Magic's own sprite Map16 — settled (2026-10-19).** Display tiles are numbered in
   LM's sprite Map16, `0x000-0x3FF`, which the sprite's own `Map16` replaces from `0x300`. Real
   configs mostly use LM's (`0x030` green shell, `0x006`/`0x007`, ...). The table sits
   uncompressed in `Lunar Magic.exe`, 0x400 tiles × 8 bytes, found by its first tiles;
   `--lmspritemap16 <Lunar Magic.exe> <out>` wrote `src/data/SpriteMap16.bin` from LM 3.40. (LM
   3.21's, as PIXI's CFG Editor++ embeds it in an `LM16` export, differs in two tiles, 16F and
   21B, and stops at 0x2FF.) Tile words are FG Map16's layout, YXPCCCTT TTTTTTTT; 8x8 tiles
   `0x000-0x1FF` are SP1-SP4 and `0x200-0x2FF` GFX33 (Yoshi is `0x080-0x0A3`).
   `--spritemap16png <rom> <level> <out>` draws the whole table.
2. **Clean-room boundary.** The driver is written from this file's format facts. Anyone
   writing it should not have PIXI's `main.asm` open; the facts above are what to work from,
   and gaps go to Mesen or the SMW disassembly, not to PIXI's source.
3. **Prep compatibility.** PIXI's own pre-checks want LM's VRAM patch (`$00F6E4 = $5C`) and a
   level saved in LM (`$06F624`). Ours does not need them, but does a prepped base satisfy
   what PIXI-built sprites assume at runtime (e.g. `$010B` holding the full level number)?
4. **Version byte.** What should our `STSD` version byte say — PIXI's 143, so sprites with
   `VERG` requirements accept it, or our own? PIXI refuses a ROM patched by a newer version.
