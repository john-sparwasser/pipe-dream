# Refactoring plan

Goal: break up the one God file, give classes single responsibility, prefer composition.
The `Rom/` layer is already UI-free (zero Foster/ImGui refs) and mostly static — the work is
concentrated in `src/EditorApp.cs` (1761 lines, ~60 methods, ~10 responsibilities).

Each step is behavior-preserving and must pass `--selfcheck` + `dotnet test` before commit.

## Testing split
- **Unit tests** (`tests/PipeDream.Tests`, xUnit): pure logic, no ROM file, no window —
  LZ2, tile decode, palette math, object-stream round-trip, `NormalizeStream`, JSON specs,
  bit decoders. Run anywhere via `dotnet test`.
- **`--selfcheck`** stays the integration harness: needs the test ROMs on disk, exercises the
  full read/render/save pipeline. Not a unit test; keep it.

## Steps

- [x] **0. Test project** — `tests/PipeDream.Tests` (xUnit) references the app exe; 26
  pure-logic tests (palette math, header decode, object/NormalizeStream, LZ2, tile decode,
  Map16.Word, ObjectNames, SpriteDisplay.Parse). `dotnet test` runs without loading Foster.
- [x] **1. Split `Map16Grid` and the ported engine out of `ObjectEngine.cs`** (750 → 137).
  - `Map16Grid.cs` (36) — plain grid data structure.
  - `ObjectEnginePorted.cs` (584) — hand-ported fallback handlers, `partial` of ObjectEngine.
  - `ObjectEngine.cs` (137) — emulated path + `Handler`. Verified: selfcheck + tests green.
- [x] **2. Extract `LevelCanvas`** (`src/LevelCanvas.cs`) — the incremental compositor owns
  the 4 phase images/textures, dirty-cell set, and px size; `Rebuild`/`ApplyDirty`/
  `RefreshPhase`/`Drop`/`MarkDirty`/`TexFor`. Inputs arrive as a `CanvasScene` record (grid,
  caches, backdrop, bg/layer2 + an overlay delegate). EditorApp keeps thin `Scene()` +
  `BuildLevelCanvas`/`ApplyDirtyCells` wrappers. Verified: selfcheck + 26 tests + render.
- [x] **3. Extract `EditHistory`** (`src/EditHistory.cs`) — a generic command stack of
  (undo, redo) closure pairs; the `EditAction` record hierarchy + two type-switches are gone.
  Each `Push*` in EditorApp captures its own restore closures, so the history knows nothing
  about tiles/sprites/objects/palettes. 5 dedicated unit tests (31 total). Verified green.
- [ ] **4. Introduce `EditTool` (composition, the main win)** — `TileTool`/`SpriteTool`/
  `ObjectTool`, each owning its selection state + interaction + operations + overlay draw.
  `EditorApp` holds the active tool and swaps it on tab change. Collapses the 336-line
  `DrawLevelView` and the duplicated move/duplicate/delete/lasso trios. Shared rubber-band +
  drag-ghost machinery becomes one `DragSelection` helper.
- [ ] **5. Peel off remaining panels/state** — `PaletteEditorState`, `SpriteCatalog`/
  `ObjectCatalog`, `GfxViewerPanel`/`DrawRomInfo`, `Dm16Saver`.
- [ ] **6. (Later) Core/App project split** — move `Rom/` into a `PipeDream.Core` library the
  exe and tests both reference, so the UI framework never enters the domain/test build. Low
  risk (Rom/ is already dependency-free) but high file-move churn; do it deliberately, not
  bundled with a behavior change. Tests reference the exe project until then.

## Notes
- Inheritance is *not* the problem here (only `EditorApp : App`, `ImGuiLayer : IDisposable`).
  The God object + mode-switching conditionals are; step 4 is where composition pays off.
- The three edit modes are structurally duplicated today — that duplication is the signal
  that the `EditTool` abstraction is earned (and a 4th mode, background/layer-3, is coming).
