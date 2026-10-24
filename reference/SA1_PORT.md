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

## Phase 2: the prep's SA-1 form

`RomPrep.ApplySa1` writes, after the pack, every byte of the prep's SA-1 form that differs from the
LoROM form or that the pack wrote. Where the SA-1 bytes come from depends on whose code it is:

| code | SA-1 form | checked by |
|---|---|---|
| ours, built with `Asm` (17 routines) | `Asm.Sa1` set: every absolute/long RAM operand emitted through `Sa1Remap` | golden hashes unchanged with it off; Mesen |
| in-place rewrites of vanilla loops | `Sa1Remap.Code` (a sweep), listed in `RomPrep.Sa1RawCode` with their entry M/X | where the stamp kept a vanilla instruction, our byte must be the pack's |
| **Lunar Magic's, carried byte for byte** | **LM's own SA-1 bytes**, from an LM-saved SA-1 ROM, as the LoROM ones came from `after.smc` | that ROM |

`Sa1Remap` holds the pack's rules (absolute `$0000-$00FF` → `$3000`, `$0100-$1FFF` → `$6100`,
Map16 → `$40/$41`, load table → `$418A00`, SRAM → `$41C000`) and the sprite tables, parsed from the
installed pack's `more_sprites/sprite_tables.asm`.

Why not the sweep for LM's code too — measured against `lm-sa1.smc` (clean + pack, one level saved
by Lunar Magic 3.40 through `tools/lm/Invoke-LmGui.ps1 -Action SaveLevel -Dirty`), which puts LM's
13 KB bank-`$1F` engine at `$1F8008`, the prep's own address:

- the sweep reproduces 673 of the 709 bytes LM changed in it, and most of the small vanilla-bank
  pieces exactly — so the rules are right;
- what it cannot do is the rest: LM moves RAM made of immediates (`LDA #$7E : STA $6D` → `#$40`,
  a `#$1BE6` pointer → `#$7BE6`, `LDA #$0000 : TCD` → `#$3000`), and a sweep reads LM's tables and
  `4C 4D` markers as code and loses M/X after branches;
- direct page is context: `$9E-$EF` are sprite tables in sprite code and scratch elsewhere. The pack
  remaps them in sprite code only, and LM's VRAM code keeps `LDA $AB`. `Sa1Remap.Code` leaves direct
  page alone; `SpriteDp` is for sprite code.

sgdq2024 (a different LM version, LM's code moved to bank `$80`) confirms the small pieces but is
no oracle for the big ones.

**Oracles still needed.** A plain level save installs the bank-`$1F` engine, the vanilla-bank
pieces and the `LmLevelEntry` blocks (at `$10Dxxx`; the prep moves them to bank `$1E`). It does not
install what other LM actions do — midway (`$13BC00`), overworld palette (`$13C508`/`$13C800`),
4bpp mode, ExGFX/Super GFX Bypass, layer 3, ExAnimation. Each needs the same action done in LM on
the SA-1 base, as `after.smc`'s siblings were made on vanilla.

**Not yet checked:** which CPU runs each hook. The pack runs level mode and Map16 loading on the
SA-1 CPU, which cannot reach W-RAM (`$7E/$7F`) or the PPU/DMA registers; code of ours that does,
from there, needs the pack's `invoke_snes` path rather than a remap. The layer-3 code reads LM's
RAM at `$7F:C01A`, which the pack does not move.

## Phase 2 findings (2026-10-24): the first boot

`RomPrep.ApplySa1` as committed (fd2552e) crashes on the title screen's level load, and so does
every variant short of the three rules below. With all three, **prepped v34 + pack boots to the
overworld** (Mesen, mode `$0E` at frame 2400, frame counter ticking) — with the ExAnimation engines
unhooked. The booting image is `D:\SMW\Projects\.resources\sa1\prepped-v34-sa1-boots.smc`, its
hooked sibling (`-exanim-hangs.smc`) stops at game mode `$05`. Both were made by the throwaway
`TmpSa1Measure.patch2` from `sa1.smc` (`--sa1prep` output), `pack.smc` (prepped + pack, before
`ApplySa1`), clean + pack, after.smc and `lm-sa1.smc`.

**Rule 1 — never write a LoROM byte over the pack's.** `ApplySa1` restamps every byte of ours the
pack wrote. Where our stamp merely *carries* a vanilla or LM LoROM byte, the pack's remap was of
that very instruction and must stand. Six bytes in v34; one is fatal: `$02ABF3`, LM's `LDX #$7F`
over vanilla's `#$3F` inside the sprite-table init. On SA-1 LM does not write it — the pack's
three `JML`s at `$02ABEE-$02ABFB` own the spot — and our restamp turned the pack's relocated
`JML $10B7CE` into `JML $10B77F`. Measured by the jump ring below: `$05D9A1 → $1EDB81 → BRK`
after the merge's own bug, then this. The rule needs vanilla's byte, which the build no longer
has: the SA-1 form of a stamp must therefore be able to say "the pack's" per byte, not just "same
as LoROM". The other five: `$00A8A1` (LM's 4bpp loop, `$0BF6` → `$6BF6`) and the checksum.

**Rule 2 — LM's pieces take LM's SA-1 bytes, relocation-aware.** 1009 bytes of lm-sa1 differ from
after.smc across the bank-`$1F` engine, blocks A-F, the fixed blocks, the in-place edits and the
hooks; all are RAM moves (`$0Bxx-$1Fxx` → `+$6000`, `$7E/$7F` → `$40/$41`, `$0005` → `$3005`),
except: every hook's JSL/JML target (LM's copies sit at `$10Dxxx`/`$10C9xx` in lm-sa1 — ours stay
in bank `$1E`; a merge that takes LM's low bytes with our bank is the `$1EDB81` crash); and
`$0DE1EC`, where LM's SA-1 form of the third little bank-`$0D` routine is a longer
`$7FC060,X → $410000,X` mirror loop (23 bytes, same address, inside our 88-byte stamp) followed by
its marker at `LM 01 01` instead of `LM 00 01`. The merge found no byte where ours, LM's LoROM and
LM's SA-1 byte all differ.

**Rule 3 — a stamp LM drops on SA-1 is dropped.** `$02ABF3` again: the oracle shows LM leaving the
pack's bytes, so the SA-1 stamp list must omit it (rule 1's "the pack's" byte).

**Still LoROM in the booting image, to be fixed before it is called done:**
- LM's 4bpp-mode install (`RomPrep.Stamps.cs` V?? "what LM writes": `$00A897` `STA $0BF6,X`,
  `$00AB0B` `$1BB2,X`, `$00B895` `LDA #$7E : STA $02`) — needs the 4bpp oracle, not a sweep.
- The ExAnimation engines (level and overworld) — LM installs them on use; needs that oracle.
- The midway routine (`$13BC00`), OW palette engine (`$13C508`/`$13C800`), layer 3 — as planned.
- Our own overworld code keeps `LDA $7Fxxxx,Y` where the pack moved vanilla's `$7E` to `$41`
  (64 bytes at `$04F2B8-$04F3D0`): the overworld runs on the SA-1 CPU under the pack, which
  cannot read `$7F`. Boots, but the map is not yet trusted.

**How the crash was found (Mesen, headless, one byte per run):** game mode and frame counter at
`$6100`/`$3013`; exec callbacks over the whole bus with `emu.cpuType.snes`/`sa1`; the *last ROM
address before a RAM execution* is deterministic across runs (power-on RAM is random, the garbage
after it is not), but the pack legitimately runs code it copies to WRAM `$1E80-$1E93` and `$1D00`
(`engine/snes_init.asm`), and vanilla's `JSL $7F8000` is kept, so "RAM execution" must exclude
those. The decisive probe is a ring of the last 12 distinct non-sequential jumps before the first
executed `BRK` opcode (SMW has none; the native BRK vector is `$FFFF`), read out 3 bytes per entry
in parallel runs (24 cores, ~90 s a batch). Reset executes before the script attaches, so an exec
count of `$008000` is always 0.

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
`lm-sa1.smc` and its siblings (`D:\SMW\Projects\.resources\sa1\`), so the output still opens in
LM. The base stays byte-frozen; the SA-1 forms are applied at build. The three rules of the
findings above are the specification; the SA-1 stamp list carries LM's forms as per-stamp byte
diffs generated from the oracle ROMs and checked against them by a `RealRomFact`. Done when
prepped + pack boots and plays in Mesen (measured 2026-10-24 for the plain-save pieces) and opens
in Lunar Magic 3.40.

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
