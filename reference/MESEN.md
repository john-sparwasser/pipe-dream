# Headless Mesen testing

Mesen has a **test runner** that loads a ROM plus a Lua script with no GUI, no video, no
audio and no input device. This replaces driving the Mesen window with synthetic clicks and
keystrokes, which needed the foreground, stole focus, and took minutes per check.

```
Mesen.exe /testrunner <rom> <script.lua> /timeout=<wall-clock seconds>
```

Tooling lives in `tools/mesen/`:

| file | what it is |
|---|---|
| `Invoke-MesenTest.ps1` | runs a ROM + script, returns `{ExitCode, TimedOut, Seconds}` |
| `New-MesenProbe.ps1` | pastes `prelude.lua` in front of a Lua body, writes it, runs it |
| `prelude.lua` | `T.rb/rw/vram`, `T.pass/fail/report`, `T.hold`, `T.each` |
| `Test-RomBoots.ps1` | smoke: ROM reaches the gameplay loop and keeps ticking |

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

Next thing to try: verify controller input actually reaches the game (pulsing Start was never
shown to change anything — boot reaches `$07` on its own, so that was not evidence), then
drive file-select → overworld → level normally. Failing that, prepare save data so the game
starts on the wanted overworld tile.

## What the boot smoke does and does not cover

`Test-RomBoots.ps1` asserts the ROM reaches gameplay and keeps ticking. Measured: vanilla and
a prep-v3 base both pass in ~5 s.

It does **not** cover the extended Map16 ranges. A ROM with the range dispatcher at `$06F538`
overwritten with `STP` still passes, because the demo level only uses tiles below `$200`,
which return through the vanilla `$0FBE` path before the dispatcher is ever reached. Covering
the ladder in game needs a level that actually uses an extended tile — which needs level
selection, above.
