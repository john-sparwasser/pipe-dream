# SA-1 support

SA-1 is a **build switch**, not a kind of project. The base ROM stays LoROM and the editor never
opens an SA-1 image; when a project has SA-1 on, the build applies the SA-1 Pack to its fresh copy
of the base first and assembles everything after it as SA-1. Off, the build is what it is today.

What the user gets, and nothing short of it is done:

- **A.** ASM ▸ SA-1, a checkbox that toggles easily on and off. Either way an "are you sure"
  confirmation comes first (on, it carries B's report). It installs the pack on first use.
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
| Cached where? | `%LOCALAPPDATA%\PipeDream\builtin` (the platform equivalent elsewhere) | Builds work offline once installed; another machine downloads the recorded tag and must match the hash, else the build stops and says so. |
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
| ours, built with `Asm` (17 routines) | built for an `Sa1Target`: every absolute/long RAM operand emitted through its `Sa1Remap` | golden hashes unchanged for `RomTarget.LoRom`; Mesen |
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
the SA-1 base, as `after.smc`'s siblings were made on vanilla (ExAnimation, level and overworld:
done, see the later findings; `Invoke-LmGui.ps1 -Action SaveExAnim`/`SaveOwExAnim` are the model
for scripting the others).

**Not yet checked:** which CPU runs each hook (layer 3's: checked, all the SNES CPU — see the
layer-3 findings below). The pack runs level mode and Map16 loading on the
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
- ~~LM's 4bpp-mode install (`$00A897` `STA $0BF6,X`, `$00AB0B` `$1BB2,X`, `$00B895` `LDA #$7E :
  STA $02`) — needs the 4bpp oracle, not a sweep.~~ Done (below).
- ~~The ExAnimation engines (level and overworld) — LM installs them on use; needs that oracle.~~
  Both done (below).
- ~~The midway routine~~ — done (below). ~~The OW palette engine (its hook block at `$13C800`, table
  at `$13C900`)~~ — done (below). ~~Layer 3~~ — done (below). (This line used to say midway `$13BC00` and palette
  `$13C508`: `$13BC00` is v10's unreferenced copy of the midway blob and `$13C508` V22's tag for it.)
- ~~Our own overworld code keeps `LDA $7Fxxxx,Y` where the pack moved vanilla's `$7E` to `$41`
  (64 bytes at `$04F2B8-$04F3D0`): the overworld runs on the SA-1 CPU under the pack, which
  cannot read `$7F`.~~ Done: these were LM's 4bpp-mode operands, and on SA-1 LM leaves the pack's
  `$4188xx` standing (below).

**Implemented (2026-10-24):** `RomPrep.ApplySa1` is overlay-based and follows the three rules:
`LmSa1Forms` carries LM's SA-1 forms as a diff from the LoROM stamps (`SNES=HEX` sets bytes,
`SNES=...` drops them — the pack's stand), generated from `.resources/sa1/lm-sa1.smc` with the
extracted pack beside it (`--sa1lmforms`, `ReferenceRoms.Sa1LevelSave`/`Sa1Pack`) and pinned by
`Sa1FormsTests`, which also checks the invariant "wherever the pack wrote inside a stamp, the SA-1
form is ours or agrees with LM's SA-1 save" and the first boot's bytes. Both ExAnimation engines
were unhooked by `ApplySa1` until their oracles below existed; both are hooked now. `ApplySa1` refuses a base not at
`LmSa1Forms.PrepVersion`: a new prep version regenerates and re-pins the diffs. `--sa1prep` output
boots: `tools/mesen/Test-RomOverworld.ps1 -Rom x -Sa1` and `Test-RomLevel.ps1 -Rom x -Sa1 -Walk
left` (prelude.lua `M.sa1()` moves the mode, counter, submap and level-number reads). Two loose
ends for phase 1's build step: LM's SA-1 routine at `$0DE1F0-$0DE20F` and the pack's blocks must
be `Owned`, since neither is RATS-tagged; and `Sa1RawCode` is empty — nothing of ours is raw code
yet.

## Phase 2 findings (2026-10-24, later): Lunar Magic on the SA-1 image, and the ExAnimation oracle

**LM accepts the image as its own.** LM 3.40 opens `--sa1prep`'s output with no dialog, and a
dirty level save on it has the same footprint as the same save on the LoROM base — 17 runs, 590
bytes: the two level pointers, LM's per-level flag, its version string and bank-`$0F` table, the
new level data (placed after the pack's code, `$10C712`, where on LoROM it follows our RATS block
at `$108148`), and its own two bank-0 bytes that every save writes, in their SA-1 form (`STY $7693`
at `$00F4A0` where LoROM gets `STY $1693`). Nothing of the prep's is rewritten: LM found its markers
where it expects them, SA-1 or not.

**LM uses our ExAnimation install.** `Invoke-LmGui.ps1 -Action SaveExAnim` fills the "Edit Level
ExAnimated Frames" dialog by control id (exanim_1's slot: type "1 8x8", no trigger, three frames
601/655/6AA at A0) and saves the level. On the LoROM base LM installs no engine and writes level
105's entry into OUR per-level table at `$1E:A3AF`: it recognises the relocated engine through the
hooks and reads the table's address from the engine's operand. On the SA-1 image, where the engine
was unhooked, it installs its SA-1 engine fresh at `$10D2E4` with all seven hooks — that save is
`.resources/sa1/lm-sa1-exanim.smc`, the oracle (`ReferenceRoms.Sa1ExAnimSave`, generated against
`exanim_1.smc` by `--sa1lmforms`, pinned as `LmSa1Forms.ExAnimSave`). Once hooked in LM's SA-1 form,
LM's save on the SA-1 image behaves as on LoROM again: no engine, our table's entry written.

**What LM's SA-1 engine changes** (42 bytes of 0xC30): RAM operands (`$0Dxx → $6Dxx`, `$14 → $74`,
`$19 → $79`; `$7FC0xx` stays, it is WRAM the SNES CPU reaches), `LDA #$0000 : TCD → #$3000` (the
pack's direct page), one branch shortened, and the MVN stub: the engine builds `MVN : RTL` in the
direct page and LoROM calls it with `JSL $000003`, which SA-1 cannot (the direct page is I-RAM), so
LM appends a 16-byte trampoline to its block (`LDA $03 : STA $C020 : LDA $05 : STA $C022 : LDA $08 :
JML $7FC020`, data bank `$7F`), the first call becomes `JSL trampoline` and the second `JSL $7FC020`.
Our copy has 8 bytes behind it, so **prep v35** stamps the trampoline as its own RATS block at
`$1EA6A0` (inert on LoROM; the layouts stay one), and the engine's SA-1 form names it
(`LmExAnimEngine.Engine(sa1: true)`) — the one byte of LM's that is ours to re-point. The generator
learned two things from this oracle: a block's candidate at our own address is never the answer (an
oracle saved on our base carries our copy, which matches better than LM's), and a byte our SA-1
stamps already re-point is ours, not LM's to set.

**Measured in Mesen:** the hooked v35 image boots to the overworld and into level 105; with LM's
slot saved onto it, the destination tile's 32 VRAM bytes at frames 3500/3516/3532 checksum 137/99/178
on the SA-1 image and on the LoROM control alike — the same three frames, in the same phase.

**The overworld engine, the same way.** `Invoke-LmGui.ps1 -Action SaveOwExAnim` saves the same slot
onto the main map from the overworld editor. On vanilla that is `.resources/ow_exanim_1.smc` (LM's
LoROM install, blob at `$10CE3A`); on the SA-1 image, with the overworld engine unhooked, it is
`.resources/sa1/lm-sa1-ow-exanim.smc` (LM's SA-1 install at `$10ED51`, `ReferenceRoms.Sa1OwExAnimSave`).
The SA-1 blob is the LoROM one with RAM operands moved (`$0DB3 → $6DB3`, `$1F11 → $7F11`, `$0100 →
$6100`, and others) and the same 16-byte trampoline appended, which its one `JSL $000003` (`+7D0`)
calls. The two trampolines are byte for byte the same, so the overworld blob's SA-1 form calls the
level engine's at `$1EA6A8` (`LmOwExAnimEngine.Engine(sa1: true)`): no new prep version. The
generated diff is 13 bytes, pinned as `LmSa1Forms.OwExAnimSave`.

Measured: once hooked, LM's overworld save on the SA-1 image installs no engine and writes the main
map's entry into our table at `$1EB440`, as on LoROM. In Mesen, with our writer's slot on submap 1
(where a new game lands), the destination tile's VRAM alternates between the same two images on
the SA-1 build and the LoROM control (checksums 32/236 every 8 frames), one step out of phase at
the same frame numbers; LM's own SA-1 install animates out of step with LoROM too. Before the diff
was applied, our engine's `$7FC00A` read `$30` (no list found) where LM's read `$10`.

**What an overworld save also does on SA-1.** The overworld editor saves the whole overworld, and
on a ROM without them installs LM's overworld-save hacks, which the prep does not carry. On SA-1
they come in LM's SA-1 form: `$03BA10` reads `$40D000` where LoROM reads `$7ED000`, `$04D7F9`
decompresses into bank `$40`, `$05DCD0` reads `$6109`, and a `JSL $0FFB20` at `$04DBB9` exists only
on SA-1. Nothing of ours depends on them. A hack LM saved on LoROM carries them in LoROM form,
which matters for phase 1's switch: turning SA-1 on under an existing hack needs them in SA-1 form,
from the same oracle.

## Phase 2 findings (2026-10-24, later): LM's 4bpp mode on SA-1

LM installs its 4bpp mode on an import, not a save: on its command line `-ExportGFX`, `-ImportGFX`,
then GFX00 back as `ExGraphics/ExGFX100.bin` with `-ImportExGFX`. Run on vanilla that is
`.resources/gfx4bpp_1.smc`; run on clean + pack it is `.resources/sa1/lm-sa1-4bpp.smc`, the oracle.
Both travel as fixtures. LM writes the same pieces at the same addresses on both, with one
exception, and `--sa1lmforms` generates `LmSa1Forms.FourBppSave` from the pair (pieces: our stamps
clipped to the bytes the import changed below `$088000`), pinned by `Sa1FormsTests`:

- **The overworld sprite tables stay where the pack put them.** On LoROM, LM moves bank 04's
  21 operands from `$7EB9xx`/`$7EBAxx` to `$7FC5xx`/`$7FC6xx`, out of the 4bpp file's overrun of
  the buffer (V13 carries this). The pack has already moved them to `$4188xx`/`$4189xx`, and on SA-1
  LM leaves the pack's operands standing, so the diff drops all 21 (rule 3). These were the "`$7F`
  reads" in the overworld code above: the overworld runs on the SA-1 CPU, which cannot reach `$7F`.
  The tables belong to the switch-palace cutscene (`$04F290`, triggered by `$13D2`: the eight "!"
  blocks fly out of the palace). Measured in Mesen by writing `$13D2` on the map at frame 2400 and
  counting on-screen OAM entries 10/30/60 frames later: LoROM 33/41/45, the fixed SA-1 build
  33/40/45, the SA-1 build before the fix 25/25/25, which is no blocks at all.
- **Two RAM moves** in LM's filter body: `$00AB17`, `$00AB2D` read `$7Bxx` where LoROM reads `$1Bxx`.
- **No other change.** `$00B895` keeps `LDA #$7E` on SA-1 as well, `$00A897` is vanilla's on both
  (its `$00A8A1` operand was already dropped by the level save), and the import's NMI-wait hook at
  `$0093F7` is ours. LM allocates its routine at a different address on each base (`$108008`,
  `$10C5D2`); the bytes are identical in both, and ours is at `$13C608`. Because the LoROM
  address's low byte matches ours, a hook run is taken as `Kind.Hook`, so the operand is never
  compared.

## Phase 2 findings (2026-10-25): the midway routine

LM installs its separate-midway blob on a level save with *Use separate settings for Midway
Entrance* ticked. `Invoke-LmGui.ps1 -Action SaveMidway` ticks it in *Modify Main and Midway
Entrance* (control 480), sets the midway's X/Y (105/107; default `3`/`13`) and FG/BG (484/485,
`C0`/`C0` as the main entrance has it), and saves level 105. On vanilla that is
`.resources/midway_1.smc`, on clean + pack `.resources/sa1/lm-sa1-midway.smc`, the oracle. Both
travel as fixtures. LM's SA-1 blob differs from its LoROM one in seven RAM operands, `$192A` →
`$792A` (3), `$13CD` → `$73CD` (2), `$141A` → `$741A` (2), plus its table/self pointers (ours). The
hooks at `$05D9E3`/`$05D979` and the edits at `$05D9C3`/`$05D9E7` are the same on both bases, give or
take the JSL targets. `LmSa1Forms.MidwaySave` carries the seven bytes into V22's blob at `$13C510`,
pinned by `Sa1FormsTests`.

Measured in Mesen. Each build had LM's save made on it, then booted into level `$105` with
bit 6 of every `$1EA2` translevel flag set on the map:

| Build | Start tile (x, y) | 200 frames later |
|---|---|---|
| Our LoROM, our SA-1, LM's LoROM, LM's SA-1 | (147, 20) | (147, 19), alive |
| Our SA-1 before the fix | (3, 20) | the midway's screen lost |

In the build before the fix, LM's LoROM `STA $13CD`/`$192A` miss the RAM the pack moved.

Two traps in that measurement:
- A midway row of `19` puts the player in the pit, where they die on every base.
- The dialog's default FG for a separate midway is `00`. The camera then starts at the top and the
  player falls off-screen.

Before both were fixed, every build, LoROM included, showed a death, which looked like an SA-1 bug.

## Phase 2 findings (2026-10-25, later): the overworld palette hook

No new oracle was needed: `lm-sa1-ow-exanim.smc` already holds LM's SA-1 install of the palette
hook, because LM's first overworld save on clean + pack installs it (on vanilla, `ow_exanim_1.smc`
has none). LM put it at the same address as V32's, `JSL $13C808 : RTS` at `$00AD32`.

**LM's SA-1 form is broken.** Its hook body is byte for byte the LoROM one: `LDA $1931` and
`LDY #$0703` (the `MVN` destination). The pack moved both, to `$7931` and to the palette buffer
`$6703` (the pack rewrites all five of vanilla bank 0's `$0703` operands). So the hook reads a
stale submap number and fills a buffer nothing uploads.

Measured in Mesen: a hash of each CGRAM row on the overworld, 120 frames after mode `$0E`, Yoshi's
Island.

| build | rows 0-5, 8-15 |
|---|---|
| vanilla, clean + pack | the same (rows 0-3 and 9-11 are vanilla's, not LM's conversion) |
| our LoROM build | LM's converted table |
| LM's SA-1 save, our SA-1 build before the fix | **every row wrong**, the same in both |
| either with `$7931`/`$6703` | our LoROM build's, row for row |

Rows 6-7 are the overworld's animated colours, and differ with timing (vanilla against clean +
pack too).

The fix is ours, not LM's: `AppendV32Stamps` says the two operands through its target, so the SA-1
stamp list carries them and the LoROM one is unchanged (no prep version bump). LM keeps them: an
overworld save by LM 3.40 over the fixed SA-1 build leaves `$7931`/`$6703` and the hook in place,
and its overworld colours measure the same as before the save.
Pinned by `Sa1FormsTests.apply_sa1_leaves_the_image_the_first_boot_needed`.

## Phase 2 findings (2026-10-25, later): layer 3

Layer 3 is not LM's code but ours (V14-V16, V34: `GfxCode`'s layer-3 pass, `L3Opt`, `L3Map`, the
advanced engine and the per-frame dispatcher, the overworld's `OwL3Map`), written clean-room in
LM's places so LM's probes recognise it. So its SA-1 form is the target's (`Sa1Target`), not an LM
diff, and the question was where it runs: it reads `$7F:C01A` and the `$7FA000` buffer, writes the
PPU and DMA registers and uses the SNES divider, none of which the SA-1 CPU has.

**All of it runs on the SNES CPU under the pack.** Measured with Mesen exec callbacks per CPU:
level load (`$0583B8`, `$00A01F`, `$00A153` and the routines behind them), the per-frame scroll
(`$05C40C` → `$0FFC40`, with `$13D5` cleared so it runs) and the overworld's `$13C708`, all bit
"SNES" only. As a control, the SA-1 CPU does execute bank `$01` sprite code in the same level, and
never anything in `$05BC00-$05C4FF` or `$0FF770-$0FFFFF`. So the RAM operands moved by the target
are the whole of the SA-1 form, plus one thing that is not RAM:

**The overworld's tilemap DMA.** `OwL3Map` replays the `LDA #$02 : STA $420B` its hook at
`$04D76A` displaces. The pack moves that transfer to channel 2 (`$4320`, `LDA #$04`;
`remap/dma.asm`: channel 1 is its window HDMA, "NMI-DMA goes to channel 2"), so on SA-1 our replay
started a channel nobody had set up and the overworld's own layer-3 tilemap never reached VRAM —
with the bypass OFF, i.e. on every SA-1 build. Measured: VRAM at word `$3000` hashed 160 on vanilla,
clean + pack and our LoROM build, 114 on our SA-1 build, with the `$7F:6000` source identical in
all four. Fixed by `RomTarget.DmaChannel` (SA-1: 1 → 2), and 160 again. A sweep of every short
prep stamp the pack also rewrites found no other non-RAM change behind a hook of ours (`$02A826`'s
is LM's, from its oracle).

**Measured in Mesen** on a project build of level `$105` with all three bypasses
(`Sa1Layer3Setup`, `PIPEDREAM_SA1_L3`), each checked inside the run and twice per ROM:

| check | LoROM | SA-1 |
|---|---|---|
| LG2 window (`$8800`) against the imported file's pattern | 0 bytes differ | 0 bytes differ |
| row-number glyph words in the layer-3 tilemap | 3936 | 3936 |
| "Fast" scroll: X − (offset + camera + camera/5), camera ≈ `$270` | 0 | 0 |
| overworld tilemap bypass (GFX 28 at word `$3000`, hash) | 248 | 248 |

An LM 3.40 save over the SA-1 build (`SaveMidway`) leaves every byte of ours alone: it writes its
own data at `$0FF023-$0FF13F` and `$0FFFE7-$0FFFFF`, outside our stamps, and at `$00F4A0` its own
SA-1 form (`STY $7693`, `$1693` on LoROM). After it the graphics and tilemap checks still read
0 / 3936. LM's save also stops level `$105` scrolling horizontally — on LoROM too — so the scroll
check has nothing to measure there.

**How the crash was found (Mesen, headless, one byte per run):** game mode and frame counter at
`$6100`/`$3013`; exec callbacks over the whole bus with `emu.cpuType.snes`/`sa1`; the *last ROM
address before a RAM execution* is deterministic across runs (power-on RAM is random, the garbage
after it is not), but the pack legitimately runs code it copies to WRAM `$1E80-$1E93` and `$1D00`
(`engine/snes_init.asm`), and vanilla's `JSL $7F8000` is kept, so "RAM execution" must exclude
those. The decisive probe is a ring of the last 12 distinct non-sequential jumps before the first
executed `BRK` opcode (SMW has none; the native BRK vector is `$FFFF`), read out 3 bytes per entry
in parallel runs (24 cores, ~90 s a batch). Reset executes before the script attaches, so an exec
count of `$008000` is always 0.

## Phase 3 findings (2026-10-25): the drivers

`Sa1DriversSetup` (gated by `PIPEDREAM_SA1_DRIVERS`) builds one project per driver LoROM and again
with ASM ▸ SA-1 on; a Mesen probe reads marks the project leaves in RAM on level `$105` (walk left
from a new game, enter). Every driver gives the same marks on both ROMs, and every driver's LoROM
build is byte-identical to before but music's (below).

| driver | what changed for SA-1 | measured, LoROM = SA-1 |
|---|---|---|
| blocks | nothing: `defines.asm` already follows the header | a column of a block setting lives to 99 stops Mario and sets 99 |
| patches | Asar assumes LoROM until a patch names a mapper and never reads the header, so an SA-1 build includes the patch from a wrapper that says `sa1rom` first; the patch's own mapper wins, errors keep its file and line | `org $808000` lands at PC `$200000` on a 4MB SA-1 image (`PatchTests`) |
| music | `$1DF9-$1DFE` through `!addr`; the upload's direct page is work RAM `$0000` on SA-1 (I-RAM `$3000` is the SA-1 CPU's while an upload runs for frames; the pack keeps WRAM `$0000-$000F` as SNES scratch); the build's sound-code check takes the pack's `LDA $7DFB`. The pack's NMI hijack at `$816A` returns to `$8172`, before our hook | the level's song is in ARAM and echoed |
| UberASM | `lorom` only on LoROM; the game mode, level and map read through `!BankA`; displaced code through `!addr`, the map's call replayed through whatever the ROM holds (`!OverworldMain`: the pack sends it to its own routine); the hook check takes the pack's forms (`UberInsert.Sa1Sites`); `invoke_sa1`/`invoke_snes` per the pack's guide; and the dispatch's `JML [$0000]` → `[$0000\|!dp]` | level init/main/nmi, game mode 14, the map, the status bar, `invoke_sa1` |
| sprites | below | INIT, its extra byte, MAIN; `SubHorzPos`, `Random`, `Aiming`; a shooter, a generator, a sprite with six extra bytes read through the pointer |

**Two bugs older than SA-1**, found on the way, both now fixed for LoROM too:

- Music's NMI hook said `BRA $0081AA`, which Asar (upstream 1.91 as well) assembles as `80 AA`: a
  number after a branch is the offset itself. Every build with music crashed on its first frame
  with sound; the interpreter tests never ran the hook's own instructions. Music had never been
  checked in Mesen. `MusicSnesTests` pins the hook now.
- UberASM's dispatch jumped with `JML [$0000]`: an absolute operand, which on SA-1 is work RAM
  rather than the direct page `$3000` the dispatch had just written. The block driver had it right
  (`[$0000|!dp]`); the sprite driver's three are fixed the same way.

**Sprites.** The pack rewrites SMW's level sprite loader to run with 16-bit X and Y (it indexes past
255 bytes of sprite data) and takes over the load table (`$418A00`, 255 records) at the same eleven
places our driver hooks for it. So on SA-1:

- the load-table hooks and routines step aside (`!1938` = the pack's `!SprLoadStatus`);
- `StepRecord` keeps Y's high byte (`%index16()`/`%index_back()` around the size lookup), adds the
  record length to all 16 bits, and hands Lunar Magic's step (unchanged on SA-1, and fine with a
  16-bit Y since its rebase only runs on `BMI`) the sign of the whole Y;
- the spawn calls what the hook displaced (`!SpawnSetup`: SMW's `$07F7D2`, or the pack's wrapper of
  it for the loader's widths), and the long extra-bytes pointer is built from all of Y;
- the shooter-number hook moves to the pack's own JML at `$02AB9E` and does its `REP #$10 : PLY`;
- everything else is addresses: a one-off rewrite took 401 operands in the driver and the 50
  built-in routines to the defines (`!14C8`, `!E4`, `$xxxx|!addr`, `$00xx|!dp`),
  and the sprite defines take the pack's 22-slot tables, PIXI's SA-1 per-slot tables (`$6040`…) and
  `|!Base2` on the rest. By hand: the Map16 tables through `!BankA` (`$40/$41C800` on SA-1); the
  8 x 8 multiplier through `%mul_a()`/`%mul_b()`/`!MulLo` (the SA-1's `$2250`, as sprite code runs on
  the SA-1 CPU); CircleX/CircleY's PPU signed multiply as the SA-1's signed 16 x 16.

Not exercised in Mesen: cluster, extended and the minor kinds (never run in Mesen on LoROM either),
custom status entry points, per-level sprites.

## Plan

**1. The switch and the build step.** ~~`BuiltinPatchRef` + the catalogue + download/cache/verify;
ASM ▸ SA-1; a first build step that applies the pack and records its blocks as an `Owned` range.~~
Done (2026-10-25), below.

Addressing follows the mapper: once the header says SA-1, PC ≥ 2MB is `$80-$BF`, never LoROM's
`$40+` (BW-RAM on SA-1). `Rom.Map` follows the header live, and `FileOffset`, RATS allocation, the
build's `Owned` ranges, the overlap check and LM's code scans (`ScanCodeAddr`, the sprite bank
table, the GFX dispatch) go through it; the static `Rom.SnesToPc`/`PcToSnes` are the LoROM map, for
the base and the prep.

- **Catalogue and cache** (`BuiltinPatches`): an entry is id, version, release URL, SHA-256, entry
  file and the licence note. Only listed versions can be pinned — the SA-1 forms are measured
  against a release — so "latest at install" is the latest *listed*; v1.40 is also upstream's
  latest. The release zip is refused unless its hash is the catalogue's, then extracted to
  `%LOCALAPPDATA%\PipeDream\builtin\sa1\1.40` (a cache, so local rather than roaming;
  `PIPEDREAM_BUILTIN_DIR` overrides, and the test run points it at its temp folder and seeds it
  from `.resources/sa1/SA1-Pack-140`, so no test downloads).
- **The switch** (`EditorSession.SetSa1`, ASM ▸ SA-1): a `BuiltinPatchRef` in the .pdp; off keeps
  the pin. It is refused over a base not at `LmSa1Forms.PrepVersion` or used as provided. Both ways
  ask first; switching on names the download and the licence, and says custom ASM is not checked
  for SA-1 yet (the report, B, is phase 4).
- **The build step** (`RomBuilder.InstallTarget` → `RomTarget.Install`) runs **first**, before
  blocks, sprites, patches and data, so the image is SA-1 for everything after it. Measured
  against applying the pack last: 23,289 bytes differ, all layout — the pack's freecode lands at
  `$108150` instead of past our data, and its hooks' `JML` targets and our data move with it. The
  Mesen layer-3 checks hold on the pack-first build. Every block the pack writes in expanded space
  is inside a RATS tag (Asar's freecode), so the allocator steps around it; the overlap check owns
  the pack's blocks ("the SA-1 Pack") and every byte of the prep's SA-1 form, LM's `$0DE1F0`
  routine included (`RomPrep.Sa1Ranges`).
- Off builds byte-identical to a project that never had the switch (`Sa1BuildTests`); the LoROM
  prep hash is unchanged.

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

~~**3. The drivers**, least work first: blocks (already SA-1 aware in `defines.asm`) → user patches
(Asar sees the header) → music SNES side → UberASM → sprites (22 slots, moved tables, hooks on the
pack's code, the SA-1 multiplier, `invoke_sa1`/`invoke_snes`).~~ Done (2026-10-25), above. Every
hook asserts the bytes it expects, so a pack release that moves code fails the build with a
message, not the game — true of music's and UberASM's; the sprite driver checks only the loader
step, as on LoROM.

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
