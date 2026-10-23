# SA-1 support

SA-1 is a **build switch**, not a kind of project. The base ROM stays LoROM and the editor never
opens an SA-1 image; when a project has SA-1 on, the build applies the SA-1 Pack to its fresh copy
of the base first and assembles everything after it as SA-1. Off, the build is what it is today.

What the user gets, and nothing short of it is done:

- **A.** ASM ▸ SA-1, a checkbox. It installs the pack on first use.
- **B.** Before the switch flips, and on every build, a report of every patch, sprite, block,
  UberASM file and song that breaks under SA-1 (or, switching off, needs it).
- **C.** Untick it. Nothing to undo: the next build simply doesn't apply the pack.

## Decisions

| question | answer | why |
|---|---|---|
| Where does SA-1 live? | Only in the build output | Every build starts from a fresh copy of the base (`PatchInsert`), so on/off is the presence of one build step. The session ROM, rendering and every reader stay LoROM. |
| Where is the switch saved? | In `project.pdp` | The .pdp is the whole project. Absent = off, so older projects load unchanged. |
| Where does the pack come from? | **Downloaded** from its GitHub release when first switched on | The pack (VitorVilela7/SMW-SA1-Pack) declares no licence — GitHub reports `license: null`, the README has credits and no terms — so it cannot be embedded and redistributed. Downloading leaves the user's copy as theirs. If upstream adds a licence, the same catalogue entry turns embedded and nothing else changes. |
| Which version? | **The latest release at install, then pinned** | The project records tag + SHA-256. Builds use that tag, never "latest", so a new release cannot silently change a ROM. A newer release is offered (ASM menu), and taking it runs the same compatibility check as switching on. |
| Cached where? | `%APPDATA%\PipeDream` (the platform equivalent elsewhere) | Builds work offline once installed; another machine downloads the recorded tag and must match the hash, else the build stops and says so. |
| ROM sizes | 4MB (`sa1rom`) first | The pack's `6mb.asm`/`8mb.asm` (`fullsa1rom`) are a later option. |

### Bundled patches, generally

SA-1 is the first of several patches Pipe Dream will ship, so it goes in through a **catalogue**,
not a special case: an id, a source (embedded, or a release URL + asset name), a licence, and a
build position (SA-1: first; others: with the user's patches). The project stores a reference,
not the source:

```csharp
/// <summary>Patches Pipe Dream ships (SA-1 Pack and the like), by id and pinned version.</summary>
public List<BuiltinPatchRef> BuiltinPatches { get; set; } = new();
public sealed class BuiltinPatchRef { public string Id { get; set; } = ""; public string Version { get; set; } = "";
                                      public string Sha256 { get; set; } = ""; public bool Enabled { get; set; } = true; }
```

The SA-1 checkbox is a shortcut to the `sa1` entry; the rest show in the Patches window as a
"Built-in" group. A downloaded entry is named in THIRD-PARTY-NOTICES as a download, not as
something shipped. Each further patch needs its own licence check before it can be embedded.

## The pack

- Release **v1.40** ("MaxTile", 2021-01-10), the latest. Asset `SA1-Pack-140.zip`, 192,824 bytes,
  SHA-256 `ffd31d1ef68a35af5b2f91d754f4b792cad05ac1e6445b8191bb7a3ddec905a2`, 48 files; entry
  `sa1.asm`. The release's `.bps` patches a clean ROM only and is of no use on a prepped base.
- Assembles with our Asar (Quasar) as is: deprecation warnings only (`@asar`, `warnpc`, `rep`).
- On a clean ROM: 13,093 written blocks, 49,547 bytes, banks `$00-$05 $07 $0C $0D $0F $10`; the
  ROM grows to 1MB and its header reads SA-1 (`$FFD5 = $23`).
- `docs/` in the zip (memory-map-summary, I-RAM, BW-RAM, W-RAM, Sprite-Remap, programming) is the
  RAM-move reference for everything below. Headline moves: direct page → `$3000`, `$0100-$1FFF` →
  `$6100-$7FFF` (BW-RAM), the big `$7E/$7F` buffers → `$40/$41`, sprite tables → `$3200…`,
  22 sprite slots, load table `$418A00`, multiplication through `$2250`.

Measure any pack version with `--asarpatch <rom> <sa1.asm>` (every written block) against
`--stampranges` and the drivers' `org` lines.

## Phase 0 findings (2026-10-23)

| ROM | game mode at frame 2400, Start pulsed |
|---|---|
| clean | `$0E`, overworld |
| clean + pack (our Asar) | `$0E`, overworld |
| prepped v34 | `$0E`, overworld |
| **prepped v34 + pack** | **`$E2`, crashed** |

**The pack corrupts the prep's hooks.** It remaps RAM by rewriting operands at fixed vanilla
offsets, and where the prep has already replaced that instruction with a jump, it rewrites the
jump's target instead:

| hook | prepped | prepped + pack |
|---|---|---|
| `$00C17A` Map16 lookup | `JSL $06F5D0 : NOP` | `JSL $6FBEF5`… |
| `$0583B8` | `JSL $0FF890 : NOP` | `JSL $0F7925 : NOP` |
| `$00AD32` OW palette | `JSL $13C808 : RTS` | `JSL $7931C8`… |
| `$00A5A2` | `JML $1FA28D` | `JML $1F743A` |

63 of the 427 prep stamp ranges are written by the pack: 57 by an operand remap (1-2 bytes), 5 by
a hijack of the pack's own at the same spot (`$00A2AF`, `$00AB0B`, `$01C08C`, `$01E762`,
`$05DBC2`). The 63rd (`$108000`) is a clean-ROM artefact: on the prepped base Asar honours our
RATS tag and puts the pack's code at `$108148` instead.

Driver hooks the pack writes (by `org` address and estimated hook length):

| driver | hooks hit | note |
|---|---|---|
| sprites | 28 of 36 | every load-table hook (`$1938` moves to `$418A00` with the pack's own code), the status dispatch, the extended/cluster/minor runners |
| UberASM | 5 of 10 | status bar `$008E1A`, game modes `$0C`/`$12`/`$14`, `$05808F` |
| music (SNES side) | 3 of 6 | engine upload `$0080E8`, level music `$008148`, NMI `$008179` |
| blocks | 0 of 2 | |

Other things the spike turned up:

- A `Rom` parses its header once, at load. After the pack runs, `MapMode` is stale until the image
  is reloaded; the build must re-read it before anything addresses the ROM.
- The pack's code lands in PC `0x80000-0x87FF7`, which RomPrep leaves zero as first-fit territory
  for RomBuilder. It is RATS-tagged; the allocator must be shown to respect that.
- `tools/mesen/prelude.lua` reads the game mode at `$7E0100`. On SA-1 it is `$6100` (and the
  frame counter `$3013`); the Mesen smoke tests need an SA-1 switch.

## Plan

**1. The switch and the build step.** `BuiltinPatchRef` + the catalogue + download/cache/verify;
ASM ▸ SA-1; a first build step that applies the pack and records its blocks as an `Owned` range.
Addressing follows the mapper: once the header says SA-1, PC ≥ 2MB is `$80-$BF`, never LoROM's
`$40+` (BW-RAM on SA-1); `Rom.SnesToPc`/`PcToSnes` and the hand-rolled bank math
(`LunarMagic.ScanCodeAddr`, `LunarMagic.cs:913`) go through it. Done when SA-1 off builds
byte-identical to today (golden hashes) and a project with no custom ASM builds with SA-1 on.

**2. The prep under SA-1.** After the pack: restamp every prep hook the pack wrote (the jump wins),
and stamp SA-1 forms of the routines behind them — the instruction a hook displaced is the pack's
remapped one (`LDA $7925`, not `LDA $1925`), and our own code's RAM moves the same way. Where the
pack hijacks the same spot, our routine calls the pack's in place of the displaced code. Routines
that are Lunar Magic's own bytes (palette engine, Map16 ladder) take LM's SA-1 forms, from
`D:\SMW\Projects\Secret\workspace\sgdq2024.smc` (4MB SA-1, LM-saved), so the output still opens in
LM. The base stays byte-frozen; the SA-1 forms are applied at build. Done when prepped + pack boots
and plays in Mesen and opens in Lunar Magic 3.40.

**3. The drivers**, least work first: blocks (already SA-1 aware in `defines.asm`) → user patches
(Asar sees the header) → music SNES side → UberASM → sprites (22 slots, moved tables, hooks on the
pack's code, the SA-1 multiplier, `invoke_sa1`/`invoke_snes`). Every hook asserts the bytes it
expects, so a pack release that moves code fails the build with a message, not the game.

**4. The compatibility report (B).**
1. *Static*, on import and as a badge per resource: **ready** (uses `!sa1`/`!SA1`/`!addr`/`!dp`,
   `sa1rom`, or `read1($00FFD5)`), **breaks** (hard-coded `$7E/$7F` longs, literal sprite tables
   like `$14C8,x`/`$9E,x`, `$4202` multiply, `LDX #$0B` slot loops, `lorom`, an `org` into code
   the pack rewrites), **unknown**. Run both ways: SA-1-only code (`invoke_sa1`) is flagged when
   the switch is off.
2. *Build*: Asar errors and failed asserts, and any write over the pack's blocks — the existing
   `Owned` overlap check, with the pack as the first owner — each named by resource.
3. *On the switch*: 1 and 2 run first and the report is the confirmation dialog.

Layer 1 needs nothing from phases 2-3 and can ship with phase 1.

**Out of scope for now:** opening other people's SA-1 ROMs, 6/8MB, and the overworld unless the
pack turns out to break our overworld writes.
