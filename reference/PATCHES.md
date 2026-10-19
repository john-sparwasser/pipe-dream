# Patches: user Asar patches in Pipe Dream

Plan for a project-level list of Asar patches the user adds, edits, deletes, enables and disables,
applied at build. Started 2026-10-19. Modelled on custom blocks (`GPS_PORT.md`): the same storage
rule, the same editor, the same build model. This file is the plan and its decision log.

## Decisions (2026-10-19)

| question | answer | consequence |
|---|---|---|
| Where do patches live? | **In project.pdp**, as blocks do: an ordered list `ProjectFile.Patches`, each entry `{ Name, Enabled, Source, Files }` — the patch's ASM and every file it includes, by path relative to it | Sharing the .pdp shares the patches. Nothing is written to disk to build: Asar reads them as in-memory files. Older projects load with an empty list, no migration. One entry holds everything about a patch, so order, state and files cannot drift apart. |
| What about included files? | **Stored in the project, binary too.** Importing a patch follows its `incsrc` and `incbin` and keeps every file it reaches | Self-contained, as blocks are. `incbin` data (tables, graphics) is `byte[]`, which System.Text.Json writes as base64, and `Asar.Patch` already takes `byte[]` files. |
| Whose files are they? | **Each patch's own** (`PatchEntry.Files`); a patch assembles seeing only its own files, at their relative paths | Two patches each shipping a `defines.asm` is common; one shared namespace would make the second import overwrite the first. Deleting a patch deletes its files. |
| Does order matter? | **Yes.** Patches apply in list order; the window has Move up / Move down | Patches that build on each other (a hijack, then one that extends it) can be ordered. |
| When are they applied? | **At build, onto a fresh copy of base.smc**, never into the base | Disabling or deleting a patch removes it at the next build with no cleanup pass: there is nothing to `autoclean` from a previous run. |
| Where in the build? | **After custom blocks, before everything else** `RomBuilder` writes (Map16 acts-as, levels, GFX, overworld) | A patch's `freecode`/`freedata` is RATS-tagged before the build allocates, so `RatsWriter` steps around it. The editor's own data is written last, so a patch cannot silently undo an edit made in the editor. |
| What if a patch writes over bytes someone else owns? | **Warn and build anyway.** Overlaps with prep's hooks (`RomPrep.StampedRanges`), the block driver's hooks, or an earlier patch are listed in the build status | Some patches mean to replace a hook; the warning names the patch, the address and the owner so an accidental one is findable. |
| Undo? | **No**, as the block library: library operations mark the project dirty and autosave | Consistent with Custom blocks. |

## What already exists to build on

- `data/Asar.cs`: `Asar.Patch(rom, asmPath, files)` with in-memory files; `Result.Blocks` is the
  written ranges, which is what the overlap check reads. Errors leave the ROM untouched.
- `data/GpsInsert.cs`: the include-follower from 2b6e7ff (`BlockIncludes`) and the in-memory
  file map under a virtual temp directory — the pattern `PatchInsert` copies.
- `services/EditorSession.Gps.cs`: the library API shape (names, source, new, import, set, delete,
  external editor round-trip through `Tick`), every operation returning a status string.
- `ui/BlocksWindow`, `ui/BlockEditWindow`: the list window and the ASM editor (Asar.xshd
  colouring, `LintSquiggles`, problem count).
- `data/RomBuilder.cs`: `Build` runs `InsertGps` first; the patch step slots in after it.
- `rom/RomPrep.cs`: `StampedRanges(version)` — every byte prep owns.

## Phases

Each phase ends shippable.

### 1. Model, session API, build step
- `ProjectFile.Patches` (`PatchEntry`: `Name`, `Enabled`, `Source`, `Files`).
- `services/EditorSession.Patches.cs`: `Patches` (name, enabled, description — the leading
  comment — and file count per row), `PatchSource`, `NewPatch`, `ImportPatchFile` (with includes), `SetPatchSource`,
  `SetPatchEnabled`, `MovePatch`, `DeletePatch`.
- `data/PatchInsert.cs`: one `Asar.Patch` call per enabled patch, in order, so an error names its
  patch. Without the Asar library: patches skipped with a warning, as blocks are.
- `RomBuilder.Build`: the step after `InsertGps`. An Asar error fails the build
  (`build failed: patch "x.asm": file:line: message`) and writes no ROM; warnings and overlaps go
  into the status.
- Tests: project file round trip (binary file included); the session operations; a disabled patch
  does not land; order is honoured; an error fails the build naming the patch; an overlap with a
  prep hook is reported and the build still succeeds; a patch's freespace is not reused.
- **Status (2026-10-19): done.** `data/PatchInsert.cs`, `services/EditorSession.Patches.cs`,
  `RomBuilder.ApplyPatches` (with `PrepOwned` and the block driver's ranges, which
  `GpsInsert.Result.Written` now carries), `tests/PatchTests.cs`. Asar writes message paths
  lowercased with forward slashes on Windows, so `PatchInsert` strips the in-memory folder
  case-insensitively. An import renames characters a patch name cannot have to `_`
  ("retry v1.2.asm" → `retry_v1_2.asm`).

### 2. Window and shared editor
- ASM ▸ Patches… beside Custom blocks → `PatchesWindow`: rows with an enable checkbox, name and
  description; New…, Import…, Edit, Open in editor, Remove (confirmed), Move up, Move down;
  double-click edits.
- `BlockEditWindow` becomes a general ASM editor taking the text and a save callback, so blocks
  and patches share one editor. Internal to `ui/`.
- Tests: list, toggle, reorder, remove; the editor counts problems; the architecture test
  (UI reaches the project only through `EditorSession`).
- **Status (2026-10-19): done**, with phase 3's external editor brought forward so the window's
  Open button works. `ui/PatchesWindow`, ASM ▸ Patches… (`OnPatches`). `BlockEditWindow` kept its
  name and gained a `(title, text, save)` constructor; the block one delegates to it. The external
  editor round trip (`OpenExternally`) is shared, keyed `block:`/`patch:` so a block and a patch of
  the same name do not collide.

### 3. Includes and external editing
- Edit / add / remove a patch's included files from the window.
- ~~Open in external editor, edits pulled back on `Tick`, as blocks.~~ Done in phase 2.

## Open questions

1. Should a patch be able to see the block driver's labels or defines? Not in phase 1.
