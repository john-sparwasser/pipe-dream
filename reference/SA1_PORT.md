# SA-1: Pipe Dream on SA-1 ROMs, every ASM feature included

Spec for making Pipe Dream fully SA-1 compatible. Written 2026-10-21; nothing here is built.
Modelled on `PIXI_PORT.md`, `GPS_PORT.md` and `PATCHES.md`: decisions first, then phases that
each end shippable. Background on how SA-1 ROMs differ is in `ROM_TOOLING.md` (the
`read1($00FFD5) == $23` autodetect, SA-1 Pack's relocated tables).

## Definition of done

"Fully SA-1 compatible" means **every feature that reads, writes or assembles ASM works on an
SA-1 base ROM exactly as on a LoROM one**, not only that an SA-1 ROM opens:

1. Open an SA-1 ROM (`sa1rom` and `fullsa1rom`), edit levels, overworld, GFX, palettes, Map16.
2. **Prep** stamps its hooks on it.
3. **Patches** (ASM ▸ Patches) apply to it.
4. **Custom blocks** (ASM ▸ Custom blocks) run on it.
5. **Custom sprites** (ASM ▸ Custom sprites), every kind (normal, per-level, shooter, generator,
   cluster, extended, minor extended, bounce, smoke, spinning coin, score), their shared routines
   and Lunar Magic's aux files work on it.
6. Build, Mesen run, release: same flow, same buttons.

A feature that has no SA-1 form says so in its window and in the build status, never builds a
ROM that crashes. An SA-1 base is detected from the ROM, never chosen by the user.

## Decisions (2026-10-21)

| question | answer | consequence |
|---|---|---|
| Which SA-1 bases? | **SA-1 Pack's** (`$23` at `$00FFD5`), `sa1rom` and `fullsa1rom` (size byte `$00FFD7 == $0D`) | SA-1 Pack is what Lunar Magic's own SA-1 support and every SA-1 tool targets. Other SA-1 hacks that relocate differently are out. |
| Detected how? | **From the ROM**: `Rom` gets `IsSa1` and `IsFullSa1` set at load; assembly-time detection stays in the ASM as the tools do it | One source of truth in C#, the same check the shipped drivers already make in asm, so the two cannot disagree. |
| Mapper | **`Rom.SnesToPc`/`PcToSnes` become mapper-aware** (LoROM, SA-1 `sa1rom`, `fullsa1rom` with bank switching of `$C0-$FF`) | Every caller already goes through them; this is the single change that lets the rest work, and the first thing built. Callers that bake in `& 0x7FFF` or `0x8000` get found by test, not by hand. |
| One driver or two? | **One driver source per feature, `!SA1`-switched** as the blocks driver already is (`%sprite_table`, `!dp`, `!addr`, `!bank`) | LoROM output must stay **byte-identical** to today's. SA-1 is an `if !SA1` branch, never a fork. |
| Whose code runs on the SA-1 CPU? | **Sprite and block code runs on the SA-1 CPU**, as PIXI and GPS do; the driver's SNES-side hooks call it through `invoke_sa1` and wait | Matches what existing SA-1 sprites assume (`$3000` direct page, `$6000`/`$7000` RAM). Sprites written for PIXI on SA-1 then run unchanged. |
| Shared routines | **Each of ours gets an SA-1 form**, MIT and clean room as before, checked against PIXI's SA-1 routines by behaviour only | Same licence rule as `PIXI_PORT.md` open question 2: PIXI's `routines/` text stays closed. The SA-1-only routines (`invoke_snes`, `invoke_sa1`, the multiply/divide/`SA-1 math` ones) are added. |
| The CPU interpreter | **Gets the SA-1's memory map**, not a coprocessor | Object expansion needs only SA-1 Pack's relocated RAM and ROM windows. Pipe Dream does not emulate the SA-1 CPU; sprite behaviour is checked in Mesen. |
| Testing | **Mesen 2 with an SA-1 Pack ROM John supplies**, plus CPU-interpreter checks for what can be | Mesen is the only SA-1 emulation we have (`MESEN.md`). Tests that need the ROM **skip without it**, as the PIXI session tests do (6039952), because CI has none. |
| Order | **Mapper, editor, prep, patches, blocks, sprites** | Each needs the one before it; each ends shippable as "this feature works on SA-1". |

## What is LoROM-only today (audit starting point)

Found 2026-10-21 by reading the code; phase 0 turns it into a complete list.

- `rom/Rom.cs`: `SnesToPc`/`PcToSnes` are LoROM; the header table names `$23` "SA-1" but nothing
  branches on it.
- `data/spritedriver/defines.asm`: `!SA1 = 0`, "a LoROM, non-SA-1 ROM, which is all Pipe Dream
  builds". The driver's tables, the `$7FAF00` load table and every hook assume vanilla RAM.
- `data/blockdriver/defines.asm`: SA-1-aware on paper (`!sa1`, `!fullsa1`), **never built or run**
  on an SA-1 ROM, so unproven.
- `rom/Cpu65816.cs` (object expansion, `CONTRACT.md §13`): marked "vanilla-layout ROMs".
- `rom/RatsWriter`, level parser and encoder, `Gfx`, `Map16`, `Palette`, overworld readers: assume
  LoROM addresses and vanilla table positions; SA-1 Pack moves several (sprite tables, Map16 at
  `$40/$41:C800`).
- `RomPrep` hook stamps (prep v34) and `RomPrep.StampedRanges`: written for vanilla code.
- `SpriteAuxFiles`, `PixiImport`: SA-1 only matters where an imported file names SA-1 symbols.

## Phases

Each phase ends shippable.

### 0. Fixtures and the audit
- John supplies an SA-1 Pack base ROM (and its Lunar Magic aux files if any). It goes where
  `ReferenceRoms` looks, never into git.
- `Rom` knows `IsSa1`/`IsFullSa1`. Opening an SA-1 ROM reports it instead of mis-reading it.
- Run the existing readers over the SA-1 ROM, list every one that breaks (level list, GFX,
  Map16, palettes, overworld, sprite tables), sorted by subsystem. That list replaces the
  audit above and sets phase 1's scope.
- Mesen launches the SA-1 ROM unchanged (a baseline: it boots, the first level plays).
- Tests: detection of `sa1rom` vs `fullsa1rom` vs LoROM; skipped without the fixture.

### 1. Mapper and the editor
- Mapper-aware `SnesToPc`/`PcToSnes`; `fullsa1rom` bank switching; the LoROM results untouched
  (a test pins them over every bank).
- Every reader on the phase 0 list fixed; `RatsWriter` allocates in SA-1's banks and respects
  the `0x7FF8`-per-bank rule where it applies.
- Levels, overworld, GFX, Map16, palettes read and **round-trip byte-identically** on the SA-1
  ROM, as they do on LoROM.
- Object expansion: the interpreter uses SA-1 Pack's relocated RAM and code; expanded levels
  match Lunar Magic's.

### 2. Prep and build
- Prep stamps its hooks on SA-1 Pack's code (the vanilla addresses differ); `StampedRanges`
  lists the SA-1 ones so overlap warnings keep working.
- `RomBuilder.Build` produces an SA-1 ROM from an SA-1 base: same steps, same order, correct
  size byte and bank layout, checksum.
- Open question 1 below is settled here.

### 3. Patches
- Patches assemble with Asar in `sa1rom`/`fullsa1rom` mode, picked from the ROM, so a patch
  that says `sa1rom` or reads `!sa1` works. A patch written for LoROM on an SA-1 ROM builds and
  warns that it may not be SA-1 safe (it cannot be proven either way).
- The overlap check reads SA-1 ranges.
- Tests: an SA-1 patch writes where it says, in the right bank, and the ROM boots.

### 4. Custom blocks
- The block driver built and run on SA-1 Pack: `!sa1` tables, `!dp = $3000`, `!addr`, `!bank`,
  dispatch through `invoke_sa1`.
- GPS blocks that use SA-1 (PIXI-style `!dp` code) run; blocks written for LoROM build with the
  same warning as patches.
- Acts-as and the Map16 level-table writes target the SA-1 window.
- Tests: the existing block insert tests re-run against the SA-1 ROM, same expectations.

### 5. Custom sprites
The biggest phase; each item mirrors a phase-5 item of `PIXI_PORT.md`.
- **Driver**: SA-1 sprite RAM (`$3200`, `$6xxx`, `$7xxx` tables, 22 slots), the load table
  (SA-1 Pack's place for it, not `$7FAF00`), every hook re-found in SA-1 Pack's code, SNES-side
  calls through `invoke_sa1`. `!SA1` flipped by the ROM, not hard-coded to 0.
- **The sprite kinds**: normal, per-level, shooter, generator, then cluster, extended, minor
  extended, bounce, smoke, spinning coin, score, each in SA-1's tables and slot counts.
- **Status entry points** (`CARRIABLE`, `KICKED`, `CARRIED`, `MOUTH`, `GOAL`) and the second
  extra-property byte's MAIN override on the SA-1 CPU.
- **Routines**: SA-1 forms of all 50 (27 of 1.2.x, 23 of 1.40), plus the SA-1-only ones.
  Behaviour checked in the interpreter where the SA-1 map allows, in Mesen otherwise.
- **255 sprites a level** on SA-1: its own load table placement and every reader/writer of it.
- **Lunar Magic aux files**: unchanged in format; tests confirm LM opens an SA-1 ROM's `.ssc`,
  `.mwt`, `.mw2`, `.s16` the same way.
- **Imported PIXI folders**: an SA-1 PIXI sprite set imports and runs; `list.txt` and config
  are the same format.
- Tests: the existing sprite insert and session tests re-run on SA-1, skipped without the ROM.

### 6. Parity and release
- `LM_PARITY.md`/`ROM_TOOLING.md` updated: what SA-1 covers, what it does not.
- Each ASM window shows the base's kind, and a feature's SA-1 limits where it has any.
- Release notes say SA-1 is supported and name the base it was tested on.

## Open questions

1. **Prep hooks on SA-1.** Does prep v34 need a separate stamp set, or does SA-1 Pack's remap of
   the hooked routines allow the same patches with `!sa1` branches? Phase 2 settles it with the
   fixture; if separate, `RomPrep` gets a version per base kind.
2. **Which SA-1 Pack version?** Hooks move between releases. Does the base ROM John supplies
   pin the supported version, or do we detect and refuse unknown ones?
3. **Clean room for SA-1 routines.** PIXI's SA-1 routines are GPL like the rest. Our versions
   are written from behaviour and SA-1 documentation; confirm none were read while writing
   (as `PIXI_PORT.md` question 2 requires for the driver).
4. **Extmod and the overworld's aux files** (`.sscov`, `.s16ov`): not done on LoROM either; do
   they come with this or stay out on both?
5. **Mesen as the only emulator.** Anything wrong only on hardware cannot be caught. Is that an
   accepted limit, or is a second emulator wanted for SA-1 sign-off?
6. **Size.** Phases 1 and 5 are the bulk. If SA-1 sprites slip, is a release with phases 0-4
   ("everything but custom sprites on SA-1") acceptable, given the definition of done?
