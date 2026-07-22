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
- [x] **4. Introduce `EditTool`** (`src/EditTools.cs`) — `TileTool`/`SpriteTool`/`ObjectTool`,
  each owning its whole per-frame interaction (highlights, hover, rubber-band, move-drag,
  place/duplicate, delete). `EditorApp.ActiveTool` picks by mode; the 336-line `DrawLevelView`
  interaction became a ~15-line `ActiveTool.Frame(ctx)` dispatch with zero mode conditionals.
  Tools are nested `partial class` types so they reach EditorApp's edit state directly
  (the domain ops — PaintCell/PlaceSprite/MoveSelected*/etc. — stay on EditorApp). Shared
  band math in the `EditTool` base. EditorApp 1645 → 1373. Verified: selfcheck + 31 tests +
  render. (Live 3-mode interaction: verbatim block move, needs an eyeball in-app.)
- [~] **5. Peel off remaining panels/state** (partial):
  - [x] `Dm16Saver` (`src/Dm16Saver.cs`) — the DM16 overlay→ROM save, pure Rom/Level
    orchestration returning (status, committed).
  - [x] `DebugPanels` (`src/DebugPanels.cs`) — ROM info + GFX viewer + Level GFX inspectors,
    owning their own preview textures + Show* toggles + `InvalidateLevel()`.
  - [ ] `PaletteEditorState`, `SpriteCatalog`/`ObjectCatalog` — DEFERRED: these are tightly
    coupled to the render path (`EditedPalette` feeds the canvas scene, tile caches, and both
    catalogs; catalogs need tileCaches + GraphicsDevice). Extracting adds delegation churn
    across ~5 call sites for modest gain and real regression risk on rendering. Revisit only
    if the palette/catalog code grows. EditorApp is now 1201 (from 1761).
- [ ] **6. (Optional, not done) Core/App project split** — move `Rom/` into a `PipeDream.Core`
  library the exe + tests reference, compiler-enforcing the UI/domain boundary. NOT done: the
  boundary is already clean by convention (Rom/ has zero Foster refs, verified) and tests
  already run against the exe without loading Foster, so the practical gain today is marginal
  while the cost is real — moving ~16 files breaks the graphify index paths (graphify-out/,
  refresh-graph.ps1) and the memory notes that reference `src/Rom/`. Do it as its own
  deliberate task if/when a second consumer of Core appears; don't bundle it with feature work.

## Result
God file broken up: `EditorApp.cs` 1761 → 1201. `ObjectEngine.cs` 750 → 137. New focused
units: `Map16Grid`, `ObjectEnginePorted`, `LevelCanvas`, `EditHistory`, `EditTools`
(Tile/Sprite/Object), `Dm16Saver`, `DebugPanels`. 31 unit tests + `--selfcheck` guard every
step. Remaining EditorApp is the app shell + level orchestration + the render-coupled
palette/catalog builders (deliberately kept — see step 5).

## Notes
- Inheritance is *not* the problem here (only `EditorApp : App`, `ImGuiLayer : IDisposable`).
  The God object + mode-switching conditionals are; step 4 is where composition pays off.
- The three edit modes are structurally duplicated today — that duplication is the signal
  that the `EditTool` abstraction is earned (and a 4th mode, background/layer-3, is coming).
