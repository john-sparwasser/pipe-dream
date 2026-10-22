# UberASM: bringing UberASM Tool into Pipe Dream

Plan for doing UberASM Tool's job (level, game mode, overworld, global and status bar code) inside Pipe Dream, with a window
like Custom blocks and Custom sprites and each level's resources in Level Properties. Started 2026-10-22. Modelled on custom
sprites (`PIXI_PORT.md`) and blocks (`GPS_PORT.md`). Background in `ROM_TOOLING.md` § "UberASMTool — what to look at"; this
file is the plan and its decision log.

## Decisions (2026-10-22)

| question | answer | consequence |
|---|---|---|
| Which UberASM Tool? | **2.x behaviour (2.1, Fernap's)**, reading 1.x lists too | Several resources per level/game mode/map, `*` for all and `-name` to skip one, extra bytes (`;>bytes n`/`any`, `()`/`[]` words and longs), `end:`, shared routines (`%UberRoutine(name)`), `;>dbr off`, `;>sa1 labels`. A 1.x list's `sprite:` is read as `freeram:`; 1.x global-file `main:`/`nmi:`/`load:` code is moved the way 2.x's readme says (Common Issues). |
| Whose driver? | **Our own**, written from the readmes and SMW's disassembly | UberASM Tool is **GPL-3.0** (both lines); Pipe Dream is MIT, so none of its ASM (`asm/base/`, `routines/`, `other/macro_library.asm`) ships or is copied, as with PIXI. Resources run unchanged on ours because they see the same labels, calling conventions and defines. The macro library and defines resources include (`!sa1`, `!addr`, `%require_uber_ver`, `%prot_file`, `%invoke_sa1`…) are ours, written from what the readme says they do. |
| Where does it live? | **In project.pdp**: resources (name → ASM), the library (name → ASM or binary), shared routines, the global, status bar and macro library files, and the list (level/game mode/map → resources with extra bytes) | Sharing the .pdp shares everything. A resource in no list entry is not inserted. Asar reads it all as in-memory files. list.txt is read at import and converted, as GPS's is. |
| Where are resources assigned? | **Both**: an UberASM window (library + the list for levels, game modes, maps and `*`) and the level's own resources in Level Properties | One model behind both; Level Properties edits that level's entry. |
| First milestone? | **Full insertion**: import a folder, edit, insert at build | Checked in the 65816 interpreter, then Mesen. |

## What UberASM Tool does, which is what we have to do

From the 2.1 readme (`assets/readme.html`), 1.5's `readme.txt`, and `ROM_TOOLING.md`'s hook table. Behaviour only.

1. **The list**: `level:` (000-1FF), `gamemode:` (00-FF), `overworld:` (0-6) sections; `number|* file [: bytes] [, file …]`;
   `-file` skips a `*` resource; comments `;` and `#`; `rom:`, `global:`, `statusbar:`, `macrolib:`, `freeram:` (2 bytes of
   free RAM for the previous game mode), `verbose:`/`deprecations:` (ignored).
2. **Labels per resource**: `load:` (levels: early in loading, before `init:`, for LM's conditional Map16 and level-table
   writes), `init:` (once, after loading, before fade-in), `main:` (each frame in game mode $14; maps $0E; game modes at the
   start of the loop on every frame after the first), `end:` (end of the frame, before `$0420`→`$0400`; game modes after the
   whole loop), `nmi:` (early in NMI; levels in modes $13-14, and $05-07 for the title screen; maps $0D-0E; game mode's
   first). `*` resources run first, then the number's, in list order. Lower-case labels only.
3. **The DBR** is set to the resource's bank for every label but `nmi:`, unless `;>dbr off`. **Extra bytes**: `$00-01` holds a
   16-bit pointer to them (in NMI on SA-1: on the stack, `($04,s)`); with `;>bytes any` the first byte is the count.
4. **Library**: every file in `library/` goes in its own freecode/freedata; a binary is one label (its name); an .asm's labels
   are exported as `file_label` (subfolders and spaces become `_`). Libraries cannot call libraries.
5. **Shared routines**: `routines/name.asm`, inserted on first `%UberRoutine(name)`, macro-local labels, may call others.
6. **Global code file**: `init:` only (2.x), RTS, early in SMW's initialisation. **Status bar file**: `main:`, RTS, right before
   the status bar is drawn.
7. **Each resource** in its own freecode (RATS), ends in RTL. A ROM that a newer UAT patched is refused; old insertions are
   cleaned (ours: the build starts from the base, so only detection matters).

## Hooks

Our own sites, chosen from the disassembly; UberASM's (`ROM_TOOLING.md`) are the guide where nothing else hooks there.
**Conflict found:** UberASM's NMI hook `$008176` (a 4-byte JML) would overlap our music driver's `$008179`
(`data/musicdriver/snes.asm`). Ours goes at `$008174` (`SEP #$30 : LDA $4210`, 5 bytes) instead. Every site is checked against
prep's, the music driver's, the sprite driver's and the block driver's before it is used.

## Phases

Each phase ends shippable.

### 1. Model, import, session API — no insertion
- `ProjectFile.Uber`: resources, library, routines, global/status bar/macro library text, and the list (per kind: number or `*`
  → resources with extra bytes and skips), `freeram`.
- `data/UberImport.cs`: list.txt (2.x and 1.x), a folder (level/, gamemode/, overworld/, library/, routines/, the three files),
  the `;>bytes`/`;>dbr`/`;>sa1` lines.
- `services/EditorSession.Uber.cs`: list, import, set source, delete, assign. Tests on 2.1's and 1.5's example lists.
- **Status (2026-10-22): done.** `data/UberState.cs` (`ProjectFile.Uber`: every user file by UberASM Tool's own path, the
  three sections as entries of calls with extra bytes and skips, the three file settings, `FreeRam`), `data/UberImport.cs`
  (folder, list.txt both versions, `FormatList` for writing back), `services/EditorSession.Uber.cs`, `tests/UberTests.cs`
  (2.1's readme example list word for word, every extra-byte form the readme names, 1.5's list, a folder through the
  session). A file's name in the list resolves against its section's folder, `../` included. Not here: the `;>bytes` /
  `;>dbr` / `;>sa1` lines, which the build reads.

### 2. Our driver and the build step
- `data/uberdriver/`: the hooks above, the dispatch tables (per kind: number → a list of resource calls with their extra-byte
  pointers), DBR set/restore, SA-1 forms, the defines and macro library resources include, `%UberRoutine`.
- `RomBuilder`: after sprites, before patches. Errors name the resource. A base ROM with UberASM Tool's markers is refused.
- Tests: in the 65816 interpreter, a level resource's `init:`/`main:`/`end:` run in their order; `*` before the number; a
  skip; extra bytes reach `$00`; a library label and a shared routine resolve. **Mesen**: level 105 `main:` counts frames.

- **Status (2026-10-22): done; Mesen check outstanding.** `data/uberdriver/` (`main.asm` the hooks and the code around them,
  `defines.asm`, `macros.asm`), `data/UberInsert.cs`, `RomBuilder.InsertUber`, `tests/UberInsertTests.cs`. Order: shared routines
  (one assembly, each body in a macro of its own so its `?` labels work), library files (an .asm under `namespace file`, so its
  labels come out `file_label`; a binary as one label), each resource with its calls' extra bytes after it (so they are in its
  bank), then the driver with the global and status bar files (`namespace UberGlobal`/`UberStatus`) and the dispatch: per kind
  and label a table of long pointers to a routine per distinct call list (a call: `$00` ← its extra bytes, DBR ← its bank unless
  nmi or `;>dbr off`, JSL), entered by `JML [$0000]`. Every hook site is checked for SMW's own bytes, and all eleven hold them
  on the dev base (prepped v34). Defines are the sprite driver's (SMW's tables under UberASM Tool's names) plus ours; our
  macros are left out where the user's macro library or the sprite defines have one of the name. The 1.x global file's
  `main:`, `nmi:` and `load:` are called where 2.x's readme moves them. Tested in the interpreter: `*` before the level's own,
  `-file` skips, extra bytes through `$00`, the DBR, a library label and a shared routine, game mode init/main/end, global init
  and the game mode word, the hooks' JMLs, and the errors; and a full project build beside the sprite driver with no warnings.
  Not yet checked in Mesen: the frame, level main (incl. paused frames), NMI, the map. UberASM Tool's own routines (GPL) are not
  shipped; one a resource calls must be in the project (an imported folder brings the user's copies).

### 3. The window and Level Properties
- ASM ▸ UberASM…: resources and library with Edit/Import/Remove, the list per kind, extra bytes per entry.
- Level Properties: the level's resources (and which `*` ones it skips).

### 4. Parity and the rest
- `;>sa1`, `%invoke_sa1`, `%prot_file` with start/size, deprecations, the title-screen NMI switch, `.extmod`.
