# SMW Sprite Engine — Redundancy & Inefficiency Notes

Analysis of sprite handling across `bank_00/01/02/03/05/07.asm` (2026-07-24).
Addresses are SNES (`$BB:AAAA` collapsed to `$BBAAAA`), matching the labels in the bank files.

## 0. Our copy of the disassembly had a bug (fixed 2026-07-24)

**`bank_01.asm` contained the same ROM region twice** (the sprite init routines from
`InitGreyLavaPlat`/$018311 through the `SpriteMainPtr` table). Same code and addresses, two
different comment revisions — an artifact of the original SMWDisC etherpad dump. The stale first
copy (older comments, pre-rename labels like `DiagBoospeeds`/`DATA_0183B3`) was deleted and the
call graph rebuilt with `tools/build_disasm_graph.py`.

## 1. Architecture (for orientation)

- **Main loop** `CODE_0180A7`: linear pass over all 12 sprite slots (X=$0B→0) every frame.
  Per slot: `CODE_0180D2` (OAM index + timers) → `HandleSprite` $018127.
- **Dispatch**: status byte `$14C8,X` → hardcoded fast paths for 0 (erase) and 8 (main), else
  `ExecutePtr` through `SpriteStatusRtPtr` $018139. Status 1/8 route to 201-entry `.dw` tables
  `SpriteInitPtr` $01817D / `SpriteMainPtr` $0185C9 indexed by sprite number `$9E`.
- **Spawning**: `LoadSprFromLevel` $02A7FB (every other frame) walks the level sprite list,
  gates on `$1938` load-status, allocates a slot (`LoadNormalSprite` $02A8DC), then
  `JSL InitSpriteTables` $07F7D2 = `ZeroSpriteTables` $07F721 + `LoadSpriteTables` $07F78B.
- **OAM**: static per-slot tile budgets from bank-07 tables keyed by sprite-memory setting `$1692`.
  Every frame: clear all 128 OAM entries (generated routine at `$7F8000`), sprites draw into their
  fixed windows, high-table repack `CODE_008494`, full 544-byte DMA `DoSomeSpriteDMA` $00844A.

## 2. Cross-bank utility clones (the JSR-locality tax)

`JSR` can't cross banks, so leaf utilities were copy-pasted per bank instead of made `JSL`-able.
Any bug fix or hack hook must be applied 3–4×:

| Routine | Copies | Addresses |
|---|---|---|
| `GetDrawInfo` (~130 bytes, the OAM pre-draw setup) | 3 | `GetDrawInfoBnk1` $01A365, `GetDrawInfo2` $02D378, `GetDrawInfoBnk3` $03B760 — 80+ call sites total |
| `SubOffscreen` (4 entry points each, ~200 bytes) | 3 | $01AC21.. , $02D016.. , $03B84F.. — ~95% identical; Bnk1 alone has the MagiKoopa-respawn special case |
| `SubHorizPos` (Mario-vs-sprite X compare) | 3 | $01AD30, `SubHorzPosBnk2` $02848D, `SubHorzPosBnk3` $03B817 |
| vertical twin of SubHorizPos | 2+ | `CODE_01AD42`, `SubVertPosBnk3` $03B829 — same 9 instructions with Y-axis addresses |
| `IsSprOffScreen` (2 instructions!) | 4 | $0180CB, $02849F **and** $02D0C9 (twice in bank 02), $03B8FB |

The per-bank `DATA_` tables these reference (`SpriteOffScreen3/4` vs `DATA_02D007/0F` vs
`DATA_03B83F..`) are byte-identical too.

Counter-example done right: `FindFreeSprSlot`/`FindFreeSlotLowPri` $02A9E4/$02A9DE share one body
(`FindFreeSlotRt` $02A9EF) with two prologues, and are `JSL`'d cross-bank.

## 3. Per-sprite GFX routine clone family

- **~35 hand-rolled OAM-writer clones**: ~29 in bank 03 (`BowserStatueGfx` $038B3D,
  `TimedPlatformGfx` $038E12, `WoodSpikeGfx` $039420, `DinoGfxRt` $039B4F, `MegaMoleGfxRt`, …)
  and ~6 in bank 02 (`HammerBroGfx` $02DAF8, `SumoBroGfx`, `TorpedoGfxRt`, …). All share the exact
  skeleton: `GetDrawInfo` → loop { disp-X/Y add → OAM store, tile store, prop `ORA $64` store,
  `TYA/LSR/LSR` size-index math } → `LDY #$FF : LDA #count : JSL FinishOAMWrite`. Only the five
  per-sprite data tables (DispX/DispY/Tiles/Prop/TileSize) and the tile count differ.
- Generic writers exist but are underused: `GenericSprGfxRt0/1/2` ($018042/$019D5F/$0190B2) serve
  only ~15 sprites; `VolcanoLotusGfx` $02E7A6 even JSRs into `MushroomScaleGfx`, proving sharing works.
- Inside bank 01 the three "shared" builders triple-compute the same math: `SubSprGfx0/1/2` each
  redo the `SprTilemapOffset[SpriteNum]` tile-base and direction→`$40` flip-bit logic
  ($019D04 vs $019D83 vs $019F17).
- Dozens of bank-01 sprite routines additionally inline raw `STA OAM_*,Y` blocks instead of calling
  any builder ($01E911, $01EE8F, $01F07D, $01FEC5, …).

## 4. Dispatch inefficiency

- **`Bnk3CallSprMain` $03A118 is a linear CMP/BNE chain over ~30 sprite numbers** ($A1–$C8), run
  per sprite per frame — worst case ~30 compares, with the `PLB/RTL` epilogue copy-pasted into every
  arm. The same bank uses proper `ExecutePtr` jump tables elsewhere (`BowserFightPtrs` $03A32B).
  Single biggest per-frame dispatch waste.
- `HandleSprite` hardcodes statuses 0 and 8, yet `SpriteStatusRtPtr` still carries those two
  (now-unreachable) entries.
- Pointer-table padding: `GeneratorPtrs` has 3 slots all → `GenParaEnemy`; `BounceSpritePtrs`
  slots 01–06 all → `BounceBlockSpr`; the 201-entry init/main tables are largely
  `Return0185C2`/`InitStandardSprite` filler (the price of O(1) dispatch — fine, but relevant when
  counting "real" sprites).

## 5. Spawn-path waste

- `ZeroSpriteTables` $07F721: **35 unrolled stores per spawn**, of which the 5 tweaker-table STZs
  ($07F767–$07F773: `$1656/$1662/$166E/$167A/$1686`) are dead work — `LoadTweakerBytes` $07F7A0
  overwrites all five immediately after.
- `Sprite166EVals` is long-indexed **twice** per spawn ($07F790 palette nibble, $07F7BA tweaker),
  and both `LoadSpriteTables` and `LoadTweakerBytes` independently re-fetch `SpriteNum` and rebuild
  the index.
- **9 generator routines, one skeleton** (`GenerateFire` $02B035, `GenerateBullet` $02B07C,
  `GenMultiBullets` $02B0CD, `GenerateDolphin` $02B26C, `GenerateEerie` $02B2D6, `GenParaEnemy`
  $02B329, …): frame-gate → `FindFreeSlotLowPri` → status/sprite-num → `InitSpriteTables` →
  RNG placement. `GenerateFire` vs `GenerateBullet` are ~95% identical. Note: the label
  `GenerateBullet` is **defined twice** ($02B07C and $02B4A7 — Torpedo Ted's spawn) — an annotation
  collision in the disassembly.
- Ad-hoc free-slot scanners bypass the shared one: `DropRiFindSlot` $028019, `GenSpriteFromBlk`,
  and the inline loop in `LoadNormalSprite` $02A916 each hand-roll `LDX #$0B / LDA $14C8,X / DEX`.
- The `$1938` load-status contract (set on spawn, clear to allow respawn) is open-coded at ~10 sites
  across banks 01/02/03 ($01AC9E, $02A8BB, $02AB9B, $02D08A, $038714, $03B8BC, …).
- `InitSpriteTables` is `JSL`'d from ~65 sites, including two adjacent branches at $02A9B9/$02A9C9
  that could converge before the call; callers also set fields it then re-clears/re-sets
  (status at $02A975, `OffscreenHorz` at $02A9CD).

## 6. Per-frame structural costs

- **`SubSprSprInteract` $01A40D is an O(n²) all-pairs scan** — 66 pair checks across 12 slots every
  other frame (frame-parity gate only, no spatial pruning).
- `CODE_0180D2` recomputes each slot's OAM index from `DATA_07F0B4[$1692]` + `DATA_07F000` every
  slot every frame, though `$1692` is constant for the whole level. 7 unrolled timer-decrement
  blocks also run 12×/frame even for empty slots.
- `LoadSprFromLevel` $02A7FB restarts the level-sprite-list walk **from index 0** every other frame
  — O(total sprites in level), no cursor; only saved by the early `BCS` once past the boundary column.
- OAM fixed costs: boot builds a 384-byte unrolled clear routine in `$7F8000` (called from 11+
  sites), high-table repack $008494 rebuilds all 128 entries' size/X-high bits each frame, and
  `DoSomeSpriteDMA` $00844A DMAs the full $220 bytes unconditionally.
- `FinishOAMWriteRt` $01B7BA is reached three different ways (13× JSR, 7× JMP tail-call, 1× via the
  `FinishOAMWrite` JSL wrapper) — the JSR+RTS sites could all be tail-calls.
- `JumpOverShells` (~$01870A) linear-scans all slots per qualifying Koopa to find shells.

## 7. Data-layout costs

- **Split lo/hi coordinate tables** (`XLo $E4`/`XHi $14E0`, `YLo $D8`/`YHi $14D4`) force manual
  byte-wise carry propagation everywhere; the 16-bit reconstruct idiom is re-implemented in at least
  6 core routines (`GetDrawInfoBnk1`, `FinishOAMWriteRt`, both `SubOffscreen` axes,
  `SubSprYPosNoGrvty`, `KoopaWingGfxRt`) and inline in every generator ("boundary + offset → pos"
  with `ADC #$00`). Caveat: the layout is load-bearing — X tables sit exactly +$0C after Y tables so
  `SubSprXPosNoGrvty` $01ABCC can reuse the Y routine by index offset.
- Direction-flip idiom `EOR #$FF / INC A` open-coded ~32× in bank 02 alone; bank 01 wraps it in
  `InvertAccum` $01804A — a 3-byte routine whose JSR costs as much as inlining (used twice).
- **Six parallel 256-byte tweaker tables** ($07F26C/$07F335/$07F3FE/$07F4C7/$07F590/$07F659) store
  per-sprite-number constants column-major; every spawn does 6 separate long-indexed loads instead
  of one 6-byte-stride row read.
- **`CircleCoords` $07F7DB stores 512 bytes for a half-sine whose second half mirrors the first** —
  2× (arguably 4×, with quarter symmetry) larger than needed.
- The 8-byte power-of-two mask table `80 40 20 ... 01` exists 3× ($00C005, $00C0AA, $05B35B).
- Offscreen boundary constants are split into 4 tables by axis × lo/hi
  (`SpriteOffScreen1/2/3/4` $01AC0D–$01AC19), forcing the `$03` stride-flag indirection that is the
  whole reason `SubOffscreen` needs 4 entry points.
- Vestigial: 163 bytes of `$FF` filler mid-bank-05 (`Empty05D665`); `ImagePointers[0]` $00857D
  marked "Not used?".

## 8. If we ever ship optimization patches (ranked)

1. **`Bnk3CallSprMain` → `ExecutePtr` jump table** — removes ~30 compares/sprite/frame; mechanical.
2. **Hoist the OAM-index computation to level load** — `$15EA` per-slot values are level-constant.
3. **Drop the 5 dead tweaker STZs** in `ZeroSpriteTables` — free 15 bytes + ~30 cycles/spawn.
4. **Merge the utility clones behind JSL** (`GetDrawInfo`, `SubOffscreen`, `SubHorizPos`,
   `IsSprOffScreen`) — ~600+ bytes freed, one patch point instead of 3–4. Costs JSL/RTL overhead
   (+~2 cycles/call); hacks (SA-1 etc.) do exactly this.
5. **Table-driven generic GFX writer** replacing the ~35 clones — biggest byte win (est. 1.5–2 KB),
   but touches every sprite's draw path; highest risk.
6. Halve `CircleCoords` with a mirror lookup; dedupe the mask tables.

## 9. What this means for pipe-dream

- **Sprite rendering is more data than code.** The ~35 GFX clones differ only in their 5 data tables
  (DispX/DispY/Tiles/Prop/TileSize) + `GetDrawInfo`'s screen-relative setup. An editor-side renderer
  that emulates "generic OAM writer + per-sprite tables" covers most enemies without per-sprite code.
- **The tweaker tables ($07F26C..) are the canonical per-sprite-number property source** (palette,
  interaction, process-offscreen bit `$167A & 4`, etc.) — index by sprite number, 6 bytes per sprite.
- **Treat clone families as one logical routine when navigating**: `GetDrawInfo*`, `SubOffscreen*`,
  `SubHorzPos*`, `IsSprOffScreen*` each have 3–4 addresses for the same logic. Graph queries and
  breakpoints should cover all copies.
- **Spawn semantics** (for accurate sprite placement/preview): sprites spawn when their screen
  column crosses the boundary, gated by `$1938`; slot ranges depend on the level's sprite-memory
  header via `SpriteSlotMax/Start[$1692]` — the same setting that fixes each slot's OAM budget,
  which is why sprite-memory settings cause visible tile-limit differences per level.
- **Repo hygiene**: done — bank_01.asm de-duplicated and the disasm graph rebuilt (§0). The graph
  is now regenerable via `tools/build_disasm_graph.py` instead of a lost one-off.
