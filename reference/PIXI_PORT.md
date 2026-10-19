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
| Routines? | **Ours are not shipped either** (PIXI's are GPL). A sprite calling `%SubOffScreen()` etc. assembles once the routine is in the project: imported from a PIXI folder (the user's own copy, stored in the project as user data) or written in the app | The same position GPS blocks were in. Writing MIT versions of the handful every sprite uses (`GetDrawInfo`, `SubOffScreen`, `Aiming`…) from SMW's own behaviour is a candidate phase, not a prerequisite. |

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

### 3. Sprites window and the level editor
- ASM ▸ Custom sprites…: rows by slot (number, name, type, acts-like), New…, Import (file or
  PIXI folder), Edit config, Edit ASM, Open in editor, Remove, assign slot.
- The Sprites catalog lists the project's custom sprites by their `Collection` names; placing
  one sets the extra bit and the collection's extra bytes; the overlay draws its `Displays`
  instead of the red X.

### 4. The sprite editor
- Extract `SpriteTilesWindow`'s preview, tile list and sheet into a shared control.
- The sprite window: type, acts-like, the six tweaker bytes as named checkboxes and fields,
  extra property bytes, extra-byte counts; Displays (per extra bit and position, or per
  extension byte) edited as Map16 tiles at pixel offsets; the sprite's own Map16 tiles with a
  four-quadrant editor (tile, palette, flips); the Collection list.
- Tests: editing each field writes the JSON PIXI's parser reads back the same.

### 5. Parity and the rest
- Status entry points, the 255-sprites-per-level remap, Lunar Magic's aux files on build.
- Cluster, extended, minor extended, bounce, smoke, spinning coin, score.
- MIT versions of the commonest routines, if importing PIXI's proves a hurdle.

## Open questions

1. **Lunar Magic's own sprite Map16.** Display tiles below `0x300` are LM's built-in sprite
   Map16 (PIXI's example uses tile `459`); the sprite's own `Map16` only covers `0x300-0x3FF`.
   To draw those we need LM's default `.s16` contents — capture from LM, or derive from our
   `SpriteDisplay.json`? Must be settled before phase 3 draws Displays.
2. **Clean-room boundary.** The driver is written from this file's format facts. Anyone
   writing it should not have PIXI's `main.asm` open; the facts above are what to work from,
   and gaps go to Mesen or the SMW disassembly, not to PIXI's source.
3. **Prep compatibility.** PIXI's own pre-checks want LM's VRAM patch (`$00F6E4 = $5C`) and a
   level saved in LM (`$06F624`). Ours does not need them, but does a prepped base satisfy
   what PIXI-built sprites assume at runtime (e.g. `$010B` holding the full level number)?
4. **Version byte.** What should our `STSD` version byte say — PIXI's 143, so sprites with
   `VERG` requirements accept it, or our own? PIXI refuses a ROM patched by a newer version.
