# Migrating the UI to Avalonia — scope

Status: **Phase 0 done and green — the approach is viable.** Written 2026-08-18.

## Phase 0 results (measured, not estimated)

Avalonia 11.3.11, `ui/PipeDream.Ui` (spike shell) + `ui/PipeDream.Ui.Tests` (headless).

**(a) Canvas cost — the thing that could have killed it.** Level `$105` is 8192x432px,
**13.5 MB per animation phase**, the widest SMW can express:

| | |
|---|---|
| compose 4 phases | **81.7 ms** — core work, unchanged by the migration |
| first upload (allocates the bitmap) | 6.1 ms |
| **steady-state repaint** | **2.28 ms** |

A full 13.5 MB push costs 2.3 ms, comfortably inside a 16.6 ms frame — and that is the *worst*
case, since the editor's dirty-cell path repaints a few cells rather than the whole level.
The canvas is not a problem.

**(b) Headless UI tests — the reason to migrate.** `Avalonia.Headless.XUnit` boots the real
application with a real visual tree, layout and pointer input, no window and no GPU: 7 tests
in ~2 s, including clicks landing on the right cell at different zooms and scroll offsets,
clicks outside the level selecting nothing, and rendering not throwing. This is the loop the
ImGui editor never had.

**(c) Packaging.** Single-file self-contained win-x64 publish works: one 101 MB exe, 10.5 s.
For comparison the current ImGui/Foster app is 76 MB the same way — **+25 MB** for Skia and
the Avalonia stack. `Tmds.DBus.Protocol` comes in transitively with a high-severity advisory
(GHSA-xrw6-gwf8-vvr9) and is pinned to 0.21.3 to clear it.

**What the spike also confirmed about the port itself:** `LevelCanvas` is already CPU
composition plus ~6 lines of GPU upload, so `LevelBitmap` (its Avalonia twin) is a
like-for-like swap of `Texture` for `WriteableBitmap`. Composition packs 0xAABBGGRR, which is
`PixelFormat.Rgba8888` — no per-pixel swizzle on the way in.

## Why this is tractable

The core does not know the UI exists. Measured:

| | files | lines | touches Foster/ImGui |
|---|---|---|---|
| `src/Rom` (ROM, level, GFX, prep, LM) | 34 | 6,593 | **0** |
| UI layer | 16 | 3,227 | all |
| everything in `src` | 72 | 13,085 | 25 reference ImGui, 10 Foster |

So a migration rewrites ~3,200 lines and leaves the 6,593-line core — and the 259 tests
over it — untouched. The expensive, subtle work (object engine, Map16 composition, LZ2,
prep) is already framework-agnostic and stays.

The UI files, largest first:

```
Map16Editor 707   ImGuiLayer 519   GfxEditor 332   EditorApp 316   LevelGfxPanel 204
LevelViewport 192 GfxBrowser 184   ProjectWizard 145 ShellLayout 128 LevelCanvas 112
MenuBar 87        SpriteOverlay 74 BackgroundPicker 73 ImGuiCompat 62 GfxViewerPanel 58
RomInfoPanel 34
```

`ImGuiLayer` (519) and `ImGuiCompat` (62) are pure adapter — they are **deleted**, not ported.

## What Avalonia actually buys

1. **Headless UI tests.** `Avalonia.Headless.XUnit` runs a real visual tree with no window:
   click a point, assert state, in the same `dotnet test` run. Every UI bug this project has
   hit — "click to allocate does nothing", "bank 1 renders nothing" — is that shape. This is
   the reason to do it.
2. **Drops Foster + SDL3.** One less native dependency to ship per RID; see PORTABILITY.md.
   Avalonia still has native bits (SkiaSharp) but desktop packaging for it is well trodden.
3. **Real native UI**: file pickers, menus, dialogs, text input/IME, per-monitor DPI,
   clipboard, keyboard navigation, accessibility. ImGui reimplements these, badly, and we
   have already written per-OS font-picking and modal-centering helpers to compensate.
4. **Retained rendering.** ImGui redraws the whole UI every frame; Avalonia invalidates only
   what changed. An editor that is mostly static chrome around one busy canvas is the good
   case for that.

**Precedent worth noting: Mesen 2's own UI is Avalonia** (its binary references
`Avalonia.Headless`). A cross-platform emulator with a custom-rendered viewport, memory
viewers and debug tools is a close analogue of this app.

## The canvas — the part that decides the whole thing

Today: compose tiles into `uint[]` RGBA CPU-side (`Map16.ComposeAll`, `ComposeSheet`,
`LevelCanvas`) → upload to a Foster `Texture` → `ImGui.Image`.

In Avalonia the composition **carries over unchanged**: write the same `uint[]` into a
`WriteableBitmap` (BGRA8888) and blit it from a custom `Control.Render(DrawingContext)`. The
incremental dirty-cell path (`canvasFull`) ports as-is. Overlays are close to 1:1:

| ImGui | Avalonia |
|---|---|
| `dl.AddRectFilled` | `DrawingContext.FillRectangle` |
| `dl.AddRect` / `AddLine` | `DrawRectangle(pen)` / `DrawLine` |
| `dl.AddText` | `DrawText(FormattedText)` |
| `ImGui.Image(tex, size, uv0, uv1)` | `DrawImage(bitmap, srcRect, dstRect)` — the Map16 bank window is already expressed as a UV rect (`Map16Editor.SheetWindow`) |

Per-frame hit testing becomes `PointerPressed/Moved/Released` on the canvas control, which is
also where the paint/select/lasso intent logic wants to live anyway.

## Phases

**All done.** Kept for the record — the plan, and where it turned out to be wrong.

The strategy held: the Avalonia app ran **in parallel** in its own project against the same
core, the ImGui app stayed buildable the whole way, and the entry point only moved at parity.
There was never a broken editor.

- ~~**Phase 0 — spikes.**~~ **Done**, see the results above. All three green.
- ~~**Phase 1 — separate the layers.**~~ **Done**, though not the way it was planned. Rather
  than moving `src/Rom` out into a library, a **services layer** went in above it:

  | layer | project | what it is |
  |---|---|---|
  | presentation | `ui/PipeDream.Ui` | draws, takes input, owns nothing |
  | services | `services/PipeDream.Services` | composition, editing, catalogs, the open/edit/save/build cycle |
  | storage | `PipeDream.csproj` (`src/`) | ROM bytes, `.pdp` files, the patch builder — the editor's database |

  Same effect, smaller move: `src/Rom` never needed relocating, only a layer that stops the UI
  reaching it. The UI's single project reference is the services layer, storage internals are
  visible to the services and *not* to the UI, and `ArchitectureTests` fails on a storage call
  appearing in a UI source file. `EditorSession` is the service the window talks to; the ROM,
  the project and the config stay internal to it.

- ~~**Phase 2 — shell.**~~ **Done.** Window, menu bar, left palette drawer, canvas region,
  status bar, canvas-mode switching from the header. The UI paradigm held: canvas centre,
  palette drawer left, new editors as canvas modes rather than drawer panels.
- ~~**Phase 3 — level canvas.**~~ **Done.** Render, pan/zoom, selection, paint, objects,
  sprites, resize handles, layer 2. `ControlParityTests` pins the bindings against
  `ObjectTool` — including the one that was guessed wrong first time, that RIGHT drag paints
  and LEFT selects.
- ~~**Phase 4 — palette drawer.**~~ **Done.** Map16 picker, sprite and object catalogs,
  palette, GFX bins. The drawer tab and the canvas edit mode are one piece of state, as they
  were in `ShellLayout`.
- ~~**Phase 5 — other canvas modes.**~~ **Done.** Map16 (canvas, 8x8 picker, properties
  inspector) and GFX (pixel editing, bins, browser).
- ~~**Phase 6 — dialogs and panels.**~~ **Done.** Level properties, screen exits, secondary
  entrances, background picker, GFX browser, ROM info, first run, base-ROM recovery — all
  ordinary windows, and all smaller than what they replaced.
- ~~**Phase 7 — delete.**~~ **Done.** Foster, ImGui.NET and the 28 UI files are gone.
  `PipeDream.csproj` and the services layer are libraries; the single executable is the editor,
  which runs the ROM tools instead when given `--headless` or a command flag. `install/`
  publishes `ui/PipeDream.Ui`.

One deliberate omission: the old GFX Viewer could inspect a file at an arbitrary bit depth
(2/3/4bpp). The browser and the GFX canvas mode both work at the ROM's depth, which covers the
ordinary uses; arbitrary-depth inspection is a diagnostic, and diagnostics belong in the CLI
rather than in a UI that can only look.

## Sizing — what it actually took

The estimate was 13–16 focused sessions. It came in under that, and the reason is worth
recording: **the expensive half was already portable**. Composition, the object engine, Map16
and the sprite OAM capture all produce `uint[]` pixels and know nothing about any UI, so the
canvas port was swapping a Foster texture for a WriteableBitmap. What actually consumed the
time was the part the estimate treated as incidental — the ~65 fields of shared mutable state
on `EditorApp`, which had to become a service before anything could be tested.

## Risks

1. ~~**Canvas performance.**~~ **Retired by Phase 0**: 2.28 ms for a full 13.5 MB repaint.
2. **Shared mutable state.** `EditorApp` carries ~65 fields that every component reaches into.
   Retained UI wants view models with change notification. This is the boring, large,
   unavoidable middle of the work — and the reason to do it phase by phase rather than at once.
3. **Feature drift during migration**, per the sizing note.
4. **Two UIs in the tree** for the duration: some duplicated wiring, and a period where a
   core change means touching both. Bounded by keeping the phases short.

## The alternative, for the record

Headless testing does **not** require Avalonia. Measured this session
(`HeadlessImGuiProbe`): an ImGui context lays out, hit-tests and renders draw data in a plain
xunit process with no GPU, and a synthetic click reaches a button — in 19 ms. What blocks a
headless *editor* today is not ImGui, it is that `EditorApp` derives from Foster's `App` and
the draw paths construct Foster `Texture`s.

So the cheap version of the feedback loop is: make the UI components depend on something
smaller than `EditorApp`, and let textures be null in tests (the draw paths already tolerate
that — `BankSheet` returns no texture and the callers skip the blit). That buys click-level
UI tests for a fraction of a migration.

Choose Avalonia for the other four reasons, not for testability alone.
