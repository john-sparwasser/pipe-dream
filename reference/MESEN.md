# Headless Mesen testing

Mesen has a **test runner** that loads a ROM plus a Lua script with no GUI, no video and no
audio. This replaces driving the Mesen window with synthetic clicks and keystrokes, which
needed the foreground, stole focus, and took minutes per check.

**It DOES have input** — this file said for months that it did not, and that was a bug in
`prelude.lua`, not a property of the runner. See "Input" below before believing anything here
about what cannot be reached.

```
Mesen.exe /testrunner <rom> <script.lua> /timeout=<wall-clock seconds>
```

Tooling lives in `tools/mesen/`:

| file | what it is |
|---|---|
| `Invoke-MesenTest.ps1` | runs a ROM + script, returns `{ExitCode, TimedOut, Seconds}` |
| `New-MesenProbe.ps1` | pastes `prelude.lua` in front of a Lua body, writes it, runs it |
| `prelude.lua` | `T.rb/rw/vram`, `T.pass/fail/report`, `T.hold`, `T.each`, `T.mode`, `T.onOverworld`, `T.OverworldFrame` |
| `Test-RomBoots.ps1` | smoke: ROM reaches the gameplay loop and keeps ticking |
| `Test-RomOverworld.ps1` | smoke: ROM reaches the MAP and keeps ticking; `-Reference` diffs its tile graphics against another ROM |
| `Dump-OwPalette.ps1` | the overworld's real CGRAM, row by row, to diff against `--owrows` |

Set `PIPEDREAM_MESEN` to point at the emulator; otherwise PATH, then `~/Mesen.exe`.

## Measured facts

- **~210 fps** headless (~3.5x realtime), ~0.6 s process startup. A typical probe that boots
  and samples state costs **4-5 s** wall clock.
- `/timeout=` is **wall clock seconds**, and a run that never calls `emu.stop` exits **-1**.
- Mesen.exe is a **GUI-subsystem binary**: PowerShell's call operator does NOT wait for it
  and silently reports an empty exit code while the run orphans in the background. Use
  `Start-Process -Wait` (or pipe the output, which also forces a wait).
- A **second instance swallows the launch** — an already-running Mesen takes the command line
  and the new process returns immediately. Check for a running instance before trusting a
  result.

## The exit code is the only output channel

Verified absent in `/testrunner` mode: `io` is **nil** (so no file writes), `os` likewise,
`emu.log` goes nowhere (stdout and stderr are both empty), no `.srm` is flushed on stop, and
`emu.takeScreenshot()` succeeds but writes no file. There is also no settings file on this
machine (`%APPDATA%\Mesen2` does not exist), so the sandbox runs at its defaults.

So a script answers with one byte: `emu.stop(code)`. Anything richer is **more runs** —
probes are generated per question by `New-MesenProbe.ps1`, so asking a different question
costs nothing. Convention: `0` pass, `1..99` a failure reason, or the raw observed byte for
probe scripts (the caller knows which it asked for).

## Lua API notes

- `emu.setInput(input, port)` — **(input, port)**, not (port, input). The reversed form
  throws, and a throwing frame callback is invisible: the run just never reaches `emu.stop`
  and dies on the timeout.
- `emu.write(addr, value, memType)` takes **THREE** arguments. The 4-arg form that mirrors
  `emu.read`'s `disableSideEffects` flag **throws** — and per the note above that is invisible,
  so it presents as a bare timeout with no clue. Wrap a suspect call in `pcall` and report a
  sentinel to tell "threw" apart from "never satisfied the exit condition".

## Entering a level — the recipe that works

Boot settles at mode `$07` and **never enters a level on its own**: measured `$7E:0100 == 0x07`
at frame 1800 both with and without Start pulses, and mode `$14` is never seen by frame 3000.
Pulsing Start changes nothing (confirming the older note). What works:

```lua
if f == 1200 then emu.write(0x7E0100, 0x0B, emu.memType.snesMemory) end
```

At frame 1500 the mode reads `$14` (the gameplay loop) and `$7E:010B` reads `$0C7` — the title
demo's level, matching the "Selecting a level — UNSOLVED" note below. So *reaching* a level is
solved; *choosing* which one still is not.

## VRAM sampling is NOT reproducible — do not compare checksums across ROMs

A sum over VRAM at a fixed frame **varies run to run for the same ROM**. Measured on vanilla,
bytes `$0000-$5FFF` (pages 0-5, deliberately excluding the animated region), three runs each:

| anchor | vanilla | prep v15 |
|---|---|---|
| absolute frame 1500 | 98, 235, 94 | 35, 102, 137 |
| 120 frames after mode `$14` first seen | 149, 106, 216 | 156, 145, 9 |

Anchoring to the mode transition instead of an absolute frame does **not** fix it, so this is not
level-entry jitter — the run itself is not deterministic (randomized power-on state is the likely
cause; there is no settings file to turn it off, see above).

**Consequence:** a "ROM A differs from ROM B in VRAM" result from this harness is worthless, and
an earlier investigation drew a wrong root-cause conclusion from exactly that. Any VRAM claim
needs a comparison made **inside one run** (sample twice, report the delta), or a different
channel entirely. Before trusting a new numeric probe, run it 3x on one ROM and assert the
values agree.
- `emu.read(addr, emu.memType.snesMemory, false)` takes CPU-bus addresses, so `$7E:0100` is
  written `0x7E0100`. `emu.memType.snesWorkRam` takes WRAM-relative offsets (`0x0100`).
  Both agree; writes via `emu.write` land.
- Available and working: `emu.setInput`, `emu.getState`, `snesSaveRam`, `snesVideoRam`.

## SMW game modes ($7E:0100)

Swept by poking each value and sampling where it settles:

| poked | settles at | |
|---|---|---|
| `$07` | `$07` | title screen / overworld (also where boot lands on its own) |
| `$0B` `$0C` `$0F` `$10` `$11` | **`$14`** | these enter a level |
| `$0D` `$0E` | `$0E` | level intro |
| `$12` `$13` `$14` | `$17` | |

**`$14` is the gameplay loop.** `$7E:0013` is a frame counter that advances 1 per frame while
the game is live — a hung game keeps its mode but stops ticking, so assert on both.

The layer-1 Map16 map is built at `$7E:C800` (tile low byte) and `$7F:C800` (page byte),
`0x3800` bytes; confirmed populated in a running level, with page bytes `$00`/`$01` for
vanilla tiles.

## Selecting a level — UNSOLVED

Forcing game mode `$0B` from the title screen enters **the title demo's level**, and there is
currently no known way to choose which level that is:

- Poking `$7E:010B` (level number) has **no effect** — measured with `$0C5`, `$101`, `$105`
  and `$024`: every one still reported `$010B = 0xC7` afterwards and produced a
  **byte-identical** Map16 map.
- Poking `$7E:13BF` (translevel) has **no effect** either — same result for `$00`, `$05`, `$25`.
- A ROM edited with `--writedm16` (tile placed and verified by re-parsing the level data)
  produces a Map16 map identical to the unedited base in game, confirming the edited level is
  simply never loaded.

**Do not "fix" this by holding `$010B` across the load window.** That makes the RAM read back
as the requested level while the game runs a different one — an assertion on it then passes
green for the wrong reason. An earlier version of `Test-RomBoots.ps1` did exactly that.

Still unsolved for a CHOSEN level, but no longer for the reason recorded here: input works
(below), so the route is now to drive the overworld — walk to a tile and press A — rather than
to poke a level number. Unwritten.

## Input — `emu.setInput` MUST be called from `inputPolled`  [MEASURED 2026-09-11]

This is the single fact that unblocked the overworld, and getting it wrong is silent:

- `emu.setInput(buttons, 0)` from a **`startFrame`** callback does nothing at all. The
  controller is read after the frame starts, so the poll overwrites whatever the script set.
  `$7E:0015`/`0016` stay `0x00` with Start held for 240 frames.
- The same call from an **`inputPolled`** callback works: `$7E:0015` reads `0x10` for Start and
  `0x91` for Start+B+Right, and SMW's menus advance.

`prelude.lua` had it at frame start, so every input experiment came back dead, and this file
concluded — in three places — that the runner has no input device and that menus, the
overworld and level selection were all unreachable. **None of that was true.** The prelude now
registers one `inputPolled` callback and `T.hold` just sets the held set.

## The OVERWORLD — reachable by playing  [MEASURED 2026-09-11, vanilla + prep v28]

Pulsing Start from boot walks the whole way: title screen, file select, new game, the intro
level (mode `$14` by frame ~600) and then **the map** — mode `$0E`, submap `$1F11` = 1 (Yoshi's
Island), frame counter ticking — by **frame 2400** (~7 s wall clock). Identical on vanilla and
on a prepped base, so vanilla is a valid control. `Test-RomOverworld.ps1` is this, with the
hang check `Test-RomBoots` uses.

- **Mode `$0E` alone is not the test.** The boot sequence passes through `$0E` around frame 608
  on its way into the intro level, so a probe that stops at the first `$0E` reports the wrong
  thing. Wait out the level; `T.OverworldFrame` is that wait.
- `$7FC009 = #$42` (prep v19's arming stub) reads back on the map, so the overworld's GFX load
  really ran — the thing poking mode `$0E` could never make happen.
- The earlier account of this — 3000 frames of pulsing leaving the game on the title screen at
  mode `$07` — was the `startFrame` input bug above, not the game.

**Comparing the map's graphics between ROMs** (`-Reference`) needs two windows left out:

| VRAM | why |
|---|---|
| `$0800-$0FFF` | the animated tiles, which cycle — vanilla's own checksum there changes between frame 2400 and 2408, so a fixed-frame comparison catches two ROMs at different phases |
| `$4000`+ | the tilemaps, i.e. the hack's CONTENT. TestRom (an LM-saved vanilla with an edited map) differs from both vanilla and a prepped base there, while every graphics window matches |

What that comparison then shows, and it is the first in-game check of the 4bpp work: **a prep
v28 base's overworld tile graphics are byte-identical to Lunar Magic's** (TestRom and
ShaoBasePrepatch both `True`) and deliberately NOT identical to stock vanilla's (`False`) —
`$2000-$2FFF` is where they part, which is LM's GFX08 compromise: vanilla's uploader ORs plane 3
into every GFX08 tile on an overworld tileset, LM's baked file carries it on 24 (LM_PARITY §2,
prep v25). Prep v18/v19's per-submap path is still verified under `Cpu65816` as well
(`OverworldGfxTests.the_loader_uploads_the_submaps_own_files`), which remains the finer-grained
check; this one covers the whole pipeline at once.

Not covered: the other submaps. A new game lands on Yoshi's Island and this harness stays
there. Reaching Donut Plains means walking the map, which input now makes possible.

## What the boot smoke does and does not cover

`Test-RomBoots.ps1` asserts the ROM reaches gameplay and keeps ticking. Measured: vanilla and
a prep-v3 base both pass in ~5 s.

It does **not** cover the extended Map16 ranges. A ROM with the range dispatcher at `$06F538`
overwritten with `STP` still passes, because the demo level only uses tiles below `$200`,
which return through the vanilla `$0FBE` path before the dispatcher is ever reached. Covering
the ladder in game needs a level that actually uses an extended tile — which needs level
selection, above.
