# Pipe Dream

[![build](https://github.com/john-sparwasser/pipe-dream/actions/workflows/build.yml/badge.svg)](https://github.com/john-sparwasser/pipe-dream/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A Super Mario World level editor that operates on the ROM directly — the same pointer tables,
level headers, object streams and Map16 pages the game itself reads. Not an emulator
front-end, not a patch format: you open a ROM, move a pipe, and the bytes that come out are
the bytes SMW loads.

ROMs already edited by **Lunar Magic** stay readable and stay writable. Pipe Dream honours
LM's RATS allocation tags, its Direct Map16 ASM, its secondary-exit and ExAnimation
structures, so a hack in progress does not have to pick a side.

C# on .NET 10, [Avalonia](https://avaloniaui.net) 11.3 for the UI, MIT licensed.
Project site: **[pipedream.nexus](https://pipedream.nexus)**

> **Status: pre-release, under active development.** The core loop — open a ROM, edit a level,
> save something the game runs — works. Expect sharp edges and keep backups of any ROM you
> point it at. Overworld, title screen and credits editing are not built yet — see the
> [roadmap](#roadmap).

You supply your own copy of Super Mario World. No game data is distributed here.

---

## Build and run

Requires the **.NET 10 SDK**. Nothing else — Avalonia and its natives come from NuGet.

```bash
git clone https://github.com/john-sparwasser/pipe-dream
cd pipe-dream

dotnet build src/PipeDream.csproj                  # compile
dotnet run   --project src/PipeDream.csproj        # launch the editor
dotnet test  src/tests/PipeDream.Tests.csproj      # 63 test files, headless
```

Pass a ROM or project straight through, plus an optional level in hex:

```bash
dotnet run --project src/PipeDream.csproj -- rom.smc 105
```

### Self-contained publish

```bash
dotnet publish src/PipeDream.csproj -c Release -r win-x64 --self-contained true -o bin/publish
```

Swap the RID for `linux-x64`, `osx-arm64`, `win-arm64`, `linux-arm64` or `osx-x64` — Avalonia
ships natives for all six. Each payload is ~85 MB because it carries its own runtime, so the
target machine needs nothing preinstalled. `dotnet publish -r <rid>` cross-builds from any
host (there is no AOT step), but macOS signing/notarization and Linux AppImage tooling need
their own OS — see [`reference/PORTABILITY.md`](reference/PORTABILITY.md).

### Windows install script

```powershell
.\install\install.ps1          # -SkipPublish reuses the last bin\publish
.\install\uninstall.ps1
```

Per-user, no admin: publishes, copies to `%LOCALAPPDATA%\Programs\PipeDream`, adds a Start
Menu shortcut, and registers `.pdp` under `HKCU\Software\Classes` (via `OpenWithProgids` as
well as the default, so it does not silently steal the extension from another tool). Re-run to
upgrade in place. Uninstall leaves `%APPDATA%\PipeDream` and your projects alone. Details in
[`install/README.md`](install/README.md).

Linux and macOS build and run from the same source today; the one-click packaging for them is
not written yet.

### Native macOS builds — in progress

There is nothing Mac-specific left in the app, so a `.app` bundle is mostly packaging work:
`Contents/MacOS`, an `Info.plist`, an `.icns`, and `CFBundleDocumentTypes` to declare `.pdp`
(no install step at all on macOS), shipped as a `.dmg`.

The hold-up is **Gatekeeper, not the code.** An unsigned bundle gets quarantined — "damaged,
move to Trash" — and needs a right-click→Open or `xattr -dr com.apple.quarantine` to launch,
which is not something to ask of someone downloading a level editor. Apple Silicon goes
further and refuses to run a binary with no signature at all, so ad-hoc signing
(`codesign -s -`) is the floor. A clean double-click install needs an **Apple Developer ID
(~$99/yr)** plus `codesign` and notarization, and `notarytool` only runs on real macOS — so it
also means adding a `macos-latest` runner to CI, not just another RID.

Until that's sorted, build it yourself: `dotnet publish src/PipeDream.csproj -c Release -r
osx-arm64 --self-contained true -o bin/publish` (or `osx-x64` for Intel).

## Automated builds

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs on every push to `main`,
every pull request, and on demand. Two-runner matrix:

| Runner           | RID         | Artifact                          |
|------------------|-------------|-----------------------------------|
| `windows-latest` | `win-x64`   | `PipeDream-Setup` (installer)     |
| `ubuntu-latest`  | `linux-x64` | `PipeDream-linux-x64` (one file)  |

Each job runs the full test suite in Release, then publishes a self-contained single-file
build. Windows wraps it in an installer — per-user, with a Start Menu entry, the `.pdp`
association and an uninstaller — and CI proves that installer works by installing it,
running `--selfcheck` from the installed binary, and uninstalling it again. Linux uploads
the executable as-is. So **every green commit leaves an installable Windows build and a
runnable Linux one behind** — grab either from the
[Actions tab](https://github.com/john-sparwasser/pipe-dream/actions/workflows/build.yml)
instead of building locally (GitHub requires a login to download artifacts, and they expire).
The same builds are linked from [pipedream.nexus](https://pipedream.nexus).

ROM-dependent tests skip themselves when no SMW ROM is present, which is always the case on
CI, so the suite is green without game data.

## Architecture

One assembly (`src/PipeDream.csproj`, assembly name `PipeDream`), organised by layer:

```
src/            Program, App — what actually starts, plus the command-line tools
src/ui/         windows, views, canvases: draws and takes input, owns nothing
src/services/   what the editor DOES — composition, editing, catalogs, the save cycle
src/rom/        the SNES/SMW formats: levels, Map16, GFX, sprites, prep, Lunar Magic
src/data/       the project file, config, the ROM builder — the storage layer
src/tests/      its own project, excluded from the app
```

The rule: **the UI talks to the services layer and nothing else.** It goes through
`EditorSession` rather than touching a `Rom` or a `Project`, so it cannot reach past the
services to the project file or the config. Storage is the editor's database — not called from
the presentation layer, and it knows nothing about it.

The concrete payoff is that the whole open → edit → save → build cycle stays runnable with no
window at all, which is what makes both the test suite and the command line possible.

Because it is a single assembly, the compiler no longer enforces any of that — internals are
visible everywhere. `src/tests/ArchitectureTests.cs` reads these folders and fails the test
run when the boundary slips. **That test is the whole enforcement; treat a failure as a build
break, not a style note.**

`src/ui/` gets `PipeDream.Services` as a global `<Using>` since every file needs it; the `rom`
and `data` layers sit in namespace `PipeDream` and need nothing.

### One executable, two halves

`PipeDream.exe` opens the editor. With `--headless` — or any command flag — it runs the ROM
tooling instead; `--headless` alone lists the commands. On Windows it is a GUI-subsystem
binary, so it has no console of its own and borrows the terminal's via a six-line
`Program.AttachParentConsole` guarded by `OperatingSystem.IsWindows()` (the only
Windows-specific code in the app). Launched with no terminal at all, redirect output to a file.

```bash
PipeDream.exe --headless                    # list the ROM commands
PipeDream.exe --selfcheck  rom.smc          # parse + round-trip check
PipeDream.exe --render     rom.smc 105 out.png
PipeDream.exe --diff       a.smc b.smc      # changed runs + new RATS blocks
PipeDream.exe --newproject ...              # create a .pdp
PipeDream.exe --buildproject ...            # build the output ROM
```

| Area        | Commands |
|-------------|----------|
| Projects    | `--newproject` `--buildproject` `--bps` |
| Inspection  | `--selfcheck` `--diff` `--markers` `--disasm` `--dumpcell` `--pixitrace` |
| Levels      | `--render` `--exits` `--entrances` `--mainentrance` `--sprites` |
| Graphics    | `--gfxsheet` `--blobsheet` `--tilepng` `--map16def` `--writedm16` |
| Animation   | `--exanim` `--globalexanim` |

Implemented in `src/DebugCommands.cs`, dispatched from `src/Program.cs`.

### The `.pdp` project file

`src/data/ProjectFile.cs`. Plain JSON: a **semantic snapshot of every edit relative to a
pinned base ROM**, containing no ROM data at all. Which means it is shareable — a collaborator
with a byte-identical base opens it and sees the same project — and it diffs in git like any
other text file. The base ROM is read, never written; builds are outputs.

Explicit DTOs rather than the domain structs, because `LevelObject`/`Sprite` are readonly
structs whose computed properties would serialize as noise and cannot round-trip by
reflection. Key conventions worth knowing before you touch it:

- Map16 **vanilla** def slots are keyed by the def slot's SNES **address** (hex) — canonical
  across tilesets, since tiles below `0x200` alias per-tileset regions, and stable across ROM
  expansion.
- **Extended** tiles (`0x200`+) are keyed by **tile number** (hex), because their region
  relocates on page allocation.
- Level keys are 3-digit hex. `SchemaVersion` is 1; fields are added beside existing ones
  rather than folded in, so older `.pdp` files keep loading.

## ROM format work

The data contract between Lunar Magic and the ROM — what to read to open a level, what to
write to save it — is documented in [`reference/CONTRACT.md`](reference/CONTRACT.md), with
every claim tagged by how well it is known:

- **[CONFIRMED]** — verified byte-for-byte against real ROMs in-repo
- **[LM-DOC]** — stated in FuSoYa's Lunar Magic help
- **[COMMUNITY]** — well-known SMW lore, not yet byte-verified here

Behaviour is traced against the actual SMW disassembly rather than guessed from a wiki: the
level loader decode at `$058601`, RATS validation (`(word4 XOR word6) == 0xFFFF`, required —
random ROM data really does contain the bytes `53 54 41 52`), LoROM mapping, copier-header
detection by `size mod 0x8000 == 0x200`.

**Round-trip compatibility with Lunar Magic is a requirement**, not a nice-to-have: a ROM must
survive being edited in either tool and reopened in the other. `CONTRACT.md` §0 states it,
tracks where it currently fails, and lists the known divergences; `LUNAR_MAGIC.md` is how to
check it, by driving the real Lunar Magic binary over a prepped ROM.

Other docs in [`reference/`](reference/): `ROM_TOOLING.md`, `LUNAR_MAGIC.md`, `PORTABILITY.md`,
`AVALONIA.md`, `SPRITE_DISPLAY.md`, `MESEN.md`, `REFACTORING.md`.

Bulk third-party material is deliberately not committed — the split SMW disassembly and the
decompiled Lunar Magic help are regenerable locally and not redistributed. `*.smc`/`*.sfc` are
gitignored so game data cannot land in a commit by accident.

## Roadmap

**Feature parity with Lunar Magic** is the headline item — the point where Pipe Dream can be
the only editor open. That means the things listed as out of scope above (overworld, title
screen, credits) plus the smaller LM tools you would otherwise switch back for. Reading and
writing LM's own structures already works, so parity is a matter of covering what it edits,
not of interoperating with it.

Then five managers, built into the editor, eventually replacing the separate insertion tools a
hack currently has to chain together by hand:

| Manager         | Folds in |
|-----------------|----------|
| **Blocks**      | Custom block behaviour and Map16 wiring — the GPS step |
| **Sprites**     | Custom sprites, their tables and per-level assignment — the PIXI step |
| **UberASM**     | Per-level, per-gamemode and global custom ASM — the UberASMTool step |
| **Music**       | Custom songs, sample groups and track slots — the AddmusicK step |
| **Backgrounds** | The ExGFX-and-Map16 shuffle a custom background takes today |

Each with the same shape: browse and install from the community, record in the `.pdp` what the
project uses, and re-apply on build — so nothing needs re-inserting by hand after a level
changes, and there is no tool ordering to get right.

Plus:

- **A built-in updater** — check for a newer release, download it, and replace the running
  install in place. No fetching a fresh build by hand, no re-running `install.ps1`, and
  nothing that touches `%APPDATA%\PipeDream` or your projects. Wants a tagged release feed to
  point at, so it follows on from publishing real releases rather than CI artifacts.
- **Custom base ROMs you can create and share** — package a prepared base (patches, ASM,
  resources) for another creator to build against. A `.pdp` already pins its base, so sharing
  the base is what makes sharing a project complete.

No timelines, and these land one at a time.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with Nintendo; Super Mario World is a trademark
of Nintendo.
