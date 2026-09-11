# What Lunar Magic writes that we don't

CONTRACT §0 requires a ROM to survive editing in either tool. That question has two halves:
do we *read* what LM writes (mostly yes — the detectors in `LunarMagic.cs`), and do we *write*
what LM writes (this file). The second half is where a ROM can quietly end up with two
mechanisms for one job.

## How this was produced

`after.smc` is vanilla plus a plain Lunar Magic save — the MINIMUM set of things LM installs
just by saving a level once. So the diff against vanilla is LM's baseline footprint, not one
hack's ambitions.

```sh
PipeDream.exe --diff SMW.smc after.smc          # 60 changed runs, LM's baseline
PipeDream.exe --prep ours.smc 9
PipeDream.exe --diff SMW.smc ours.smc           # 42 changed runs, ours
```

Overlap: 11 addresses both write. 49 LM writes and we do not. A handful we write and LM does
not (our own extensions — DM16 handlers, the exit high bit, the checksum balance).

**Confidence is marked per row.** CONFIRMED means the disassembly or a test says so;
INFERRED means the address and shape say so but nothing has been run against it.

## 1. Both write it — check these for drift first

| site | ours | LM's | status |
|---|---|---|---|
| `$05D7CE` → `$05DC50` | exit destination bit 8 | same hijack, same flag layout | **CONFIRMED identical** — emulated over every flag combination (`v7_decides_the_high_byte_exactly_as_lunar_magic_does`) |
| `$00AACD` 4bpp upload | LM's byte sequence | — | the 32-byte loop is identical; through v24 it was the only piece of LM's 4bpp rework we shared, from v25 the rest is stamped too — see §2 "4bpp graphics" |
| `$0DF08A`, `$0DFF50`, `$0D*FD` handlers | LM's own bytes (v10 restamps over v5's) | same | **CONFIRMED identical** (`LmLevelRender`) |
| `$0DA4B8` DM16 hijack | ours | LM's | same table entries — CONFIRMED identical |
| `$0EF100` sprite bank table | LM's layout, LM's `$0EF300` stub + `$0EF550` level word (v10) | same | **CONFIRMED identical** |
| `$00F4DE` acts-like call site | repoint one JSL | rewrites 43 B at `$00F478` | **different mechanisms for one feature** — see §2; the `$13D7` half of `$00F478..` is taken, the acts half is not |
| `$0DE190`-`$0DE1FF` ext-object handlers | LM's bytes (v10) | same | **CONFIRMED identical** — ext 01/03 carry the 32-row band |

## 2. LM writes, we do not — the inventory

### Block behaviour ("acts like") — a different mechanism, not a missing one
We repoint the four vanilla `JSL $00F545` acts-like sites at our own remap (`$06F800` from v26,
`$06F5F0` before — table at `$118000`) where LM reaches its lookup through four per-site
trampolines. CONFIRMED different, and now CONFIRMED compatible: the table round-trips exactly
(below), and v28 took the one behavioural difference. LM's trampolines carry per-site stack reads
for its blocktool block cases and JML a bank-95/96 engine we do not install, so there is nothing
for them to route on our base; from v26 LM's own lookup is stamped beside ours reading the SAME
table (§2 "Map16 F9"), so both mechanisms answer identically.

**`LDA $00BA70,X → LDA $000CC6,X` is NOT part of this** — an earlier version of this section had
the two features tangled, and the reads were never ours to take because we already had them. The
seven sites (`$00F50D`, `$019509`, `$029301`, `$0295F4`, `$02A6C2`, `$02BA79`, `$02D194`, each also
getting `$0CB6`/`$0CD6`/`$0CE6` for its three sibling tables) are vanilla's per-screen **Map16
plane-pointer** low/high byte tables, which LM rebuilds in RAM per level because the column stride
is no longer a constant. They came in with **prep v10's variable-height engine**
(`LmLevelEntry.HeightHooks`, and the doc comment there says so: "the block-probe tables the same
way (`$BA60… → $0CB6…`)"), they are pinned byte for byte against LM's own ROM by
`EntrancePlacementTests`, and the RAM they build is asserted at vanilla and non-vanilla heights by
`level_pointer_chain_leaves_identical_ram_except_the_level_word` and
`a_taller_level_gets_its_height_into_ram_and_its_pointer_tables_restrided`.
Measured again 2026-09-11: neither our v28 base nor ShaoBase references `$00BA70` anywhere, and
both read `$000CC6` at all seven sites.

**The round trip, measured 2026-09-11 through LM's own command line** (`-ExportAllMap16` /
`-ImportAllMap16`, whose `.map16` format carries "Act As data" as its own indexed section, 2 bytes
per tile, tile-numbered — see reference/lm-help/html/info_map16_file_format.htm):

- **Ours → LM: exact.** Write entries for tiles `030`, `105` and `2A5` into `$118000` on a v27
  base, export: LM's Act As section holds `0E0`, `02F`, `1C0` at those tiles, identity everywhere
  below 0x200 and `130` above — our table's defaults, verbatim.
- **LM → ours: exact.** Edit the same file's Act As bytes and import: LM writes them straight into
  our table (the diff lands on `$118060`, `$118140`, `$11820A`, `$11854A`, `$118620` — tiles
  `030`, `0A0`, `105`, `2A5`, `310`), and the ROM still boots and builds a level. So the address,
  the layout, the 2-byte order and the defaults are all right in both directions.
- **The one real divergence was CHAINING, and prep v28 closes it.** LM's lookup treats an entry of
  0x200 or more not as a behaviour but as another TILE, and reads ITS entry (`BCS` back to its own
  `TAY`); ours stopped and kept the original tile. LM's import stores such an entry without
  complaint — tile `310` → `2A5` → `1C0` survived a full round trip — so a hack can carry one, and
  on our base the game would have gone one way while LM's editor predicted the other. V28 adds the
  same loop to the remap (two bytes) and `Rom.ActsAsResolved` to the reader, so the hitboxes and
  the behaviour readouts follow the chain while the acts-like FIELD still edits the entry it shows.
  The reader is bounded: a cycle hangs the game — LM's hazard too — but must not hang the editor.

Nothing is left of this item: the table is shared, the round trip is exact, the chain is followed,
and the RAM plane-pointer reads it used to be confused with have been ours since v10.

### Sprite stream — LM's loader, TAKEN (v10, block C of the level engine)
`$02A67A`, `$02A826` (→ `JML $1090A3` / `$109198` / `$10917B` / `$108F2D`), `$02A95B`
(→ `JML $108F70`) and the rest of the bank-02 hooks are LM's sprite-list loader: the per-screen
index cache, spawn-range window, extended list (`FF nn` bands, CONTRACT §11) and per-sprite extra
bytes. All transplanted byte-for-byte (`LmLevelEntry.Height.cs`). The size table is read where LM
registers it (`$0EF30C` + `0x42` at `$0EF30F`, CONTRACT §11) and AUTHORED the same way
(`Rom.SetSpriteEntrySize`, written at build for every sprite carrying extra bytes; Level ▸ Sprite
data… sets them) — TAKEN.

### Sprite bank relocation — LM's shape, TAKEN (v10)
`$05D8E2 → JSL $0EF550` (level word → `$010B`, Y = level×2) and `$05D8F5 → JSL $0EF300` (bank
from `$0EF100`) are LM's two routines, restamped over v1's longer private stub which had overrun
LM's `$0EF30C` pointer and `$0EF310` table.

### Map16 machinery — LM's is much larger
`$06F540` (260 B), `$06F65C`, `$06F690` (218 B), `$06F780` (80 B), `$06FA00` (1536 B, `$20`
fill). Ours is `$06F538` (73 B) + `$06F5D0` + `$06F5F0`. INFERRED: LM covers all eight ladder
ranges plus per-tileset page tables; prep v3 covers four. Feature: extended Map16 pages beyond
`$3FFF`, per-tileset tables. We already read the ladder slots LM uses (§7a-rev).

### Level load — LM's engine, TAKEN (v10)
`$0580C0/C4/C8` (level-mode tables in LM's bank-$1F block), `$058DA4` (→ `JSL $0EFD00`),
`$0586A1`, `$00A6B8`/`$00A6CC` (→ `$0EF560`, `$05DD00`), `$05DCB0`, `$05DD30`, `$0EFD00` — all
part of the level engine transplanted in v10 (sections below). This is not a GFX loader: ours
(v2, `$0FF770`/`$0FF780` — LM-compatible per §7) stays the one GFX path.

### Entrance positions — "method 2" IS installed by every save, and prep v10 stamps it  [CONFIRMED, after.smc + ShaoBase + juz]

The two limits our Entrances mode once enforced are **vanilla's**, and LM's help says so
(`level_main_entrance.htm`): the bank-05 tables are "method 1", and *"Method 2 does not use
table-based coordinates, and is an enhancement inserted by Lunar Magic"*. What the help does not
say is that method 2 is not a rival table — it **reinterprets the record's own two index nibbles
as 16px steps** and adds a flags byte and a Y-high byte per level. A PLAIN save installs it, for
main and secondary entrances alike; the reference bases (vanilla, prepped &lt; v10) did not:

| site | vanilla | after.smc, ShaoBase, juz, DogsOfWar, BigEye |
|---|---|---|
| `$05D97D` | `LSR : STA $192A` | `JSL $05DD30` — main entrance, method 2 |
| `$05D833` | `LDA $FE00,Y : AND #$07 : STA $192A` | `LDA $FE00,Y : TYX : JSL $03BCE0` — secondary, method 2 + 9-bit destination |
| `$05DC80/85/8A` | `$FF` | `LDA $05FE00,X : RTL`, then two `LDA long,X : RTL` whose operands are per-ROM tables |
| `$05D9C3` | `STA $13CD` | `LDA $13CD` (see below) |
| `$05D9E7` | `STA $95 : JMP $05DA17` | `STA $01 : NOP NOP NOP` — midway screen through the shared tail |
| `$05DE00`, `$06FC00` | `$FF` | zeroed (bit 5 of `$FF` would switch method 2 on everywhere) |
| `$06FE00` | `$FF` | `$1A` per level |

Both routines are **byte-identical** across after.smc and every hack (`$05DD30`, 0x46 bytes;
`$03BCE0`, 0xB6 bytes) and sit at fixed addresses in vanilla free space, so prep v10 stamps
exactly those bytes and the hooks above. `EntrancePlacementTests.v10_stamps_exactly_the_routines_
lunar_magic_installs` pins the equality; `lunar_magics_routines_put_mario_where_the_editor_says`
runs both routines under `Cpu65816` over records this editor wrote.

**Main entrance** (`$05DD30`, decoded m8): `$05DE00+lvl` bit 5 = method 2, bit 3 = X bit 7,
bit 4 = X bit 8 (vertical levels), bits 6-7 → `$192A`; `$06FC00+lvl` bits 0-5 = Y high; then
`$94 = F200 bits 0-2 << 4 | bit3 << 7`, `$96 = F000 bits 0-3 << 4`, `$97 = Y high`. The screen
still arrives through the vanilla tail (`$05D9EC: LDA $01 : AND #$1F : STA $95`), which is why
`EntrancePlacement.Method2X` is `screen << 8 | xHigh << 7 | xIndex << 4`. `$06FE00+lvl` goes to
`$13CD` — LM's FG/BG-relative setting, consumed by its per-ROM tail hook at `$05DA17` (not
installed here). Vanilla put the midway screen in `$13CD` and its only reader (`$00F2D8`, the
midway tape) tests it for zero, which is what LM's `$1A` and the `STA`→`LDA` at `$05D9C3` are for.

**Secondary entrance** (`$03BCE0`): `$05FE00` bit 3 → `$0F` = destination bit 8 (**replacing the
submap guess** — LM's save sets it on every record `$100-$1FF`, and so does the prep), bits 4-5 =
X bits 7-8, bit 6 = method 2, bit 7 → `$192A`; the fifth table (reader at `$05DC85`) bits 0-5 = Y
high; `$94 = FC00 bits 5-7 << 4 | bit4 << 7`, `$96 = FA00 bits 0-3 << 4`. The sixth table (reader
at `$05DC8A`) carries FG/BG bits and an "exit to overworld" flag we leave zero. LM allocates the
two tables per ROM (after.smc `$1086C9`/`$1088CF`, ShaoBase `$10F0C5`/`$10F1CB`, sized 0x1FE —
its last two records read the next RATS tag); ours are `$13B000`/`$13B200`, and
`Rom.LmSecondaryYHighTable` reads whichever a ROM names.

**Separate midway settings — installed on demand, and here too.** A plain save does not carry
it; juz, ShaoBase and DogsOfWar do, as one 0xC4-byte blob (juz `$11FA63`, ShaoBase `$10FDDF`,
DogsOfWar `$12EF20`) that is byte-identical apart from four table operands and its own address:

| site | vanilla | with the feature |
|---|---|---|
| `$05D9E3` | `LSR x4` | `JSL blob` — A = `$F400` byte in, screen out |
| `$05D979` | `AND #$38 : LSR : LSR` | `JSL blob+$A0` — an exit flagged for the midway (`$192A` bit 6, `$141A != 0`) arrives at it |
| tables | — | one RATS block of 4 x 0x200: flags, position, FG/BG, Y high (juz `$138008`, ShaoBase `$128008`) |

Flags: bit 5 = separate settings on, bit 4 = midway screen bit 4, bit 3 → `$95` = X bit 8
(vertical levels), bits 0-2/6-7 → `$192A`. Position: low nibble = X bits 4-7 (the WHOLE nibble,
unlike method 2's 3+1 split), high nibble = Y bits 4-7. FG/BG: `$05F400`'s layout in bits 0-3,
bits 6-7 → `$13CD`, bit 5 = "redirect midway to another level" (position byte = that level, FG/BG
bit 0 its high bit; the blob restarts the load at `$05D8B7`). Y high: bits 0-5; LM sets bit 6 on
every record it writes, and so does the prep. With bit 5 clear the blob hands the screen back and
touches nothing, so an unplaced level plays as vanilla with a fifth screen bit.

Prep v10 stamps the blob at `$13BC00`, and both hooks; `Rom.LmMidwayTable`
follows the `$05D9E3` operand into whichever ROM's copy, so ShaoBase and juz read the same way.

**The tables need a RATS block of their OWN — v21.**  [MEASURED 2026-09-10, ShaoBase as the silent control]
V10 put them 0x400 into the secondary block's run (`$13B400` inside the 0xD00 block at
`$13B000`), and **every LM level save on a prepped base refused with "Existing data format or
size not recognized! Midway entrance data"** — once per write attempt, so the box returns as
fast as it is dismissed. It had been that way since v10 and nothing had checked. LM follows the
blob's own operand to the tables and wants a tag 8 bytes ahead of them with the size it uses
itself (`STAR`, 0x800, data at tag+8 — ShaoBase `$128000`/`$128008`); anything else is refused.
Prep **v21** moves them to `$13BD08` in a block of exactly that shape and changes nothing else:
LM's save then goes through silently, writing the midway screen into vanilla's own `$05F400`
table where our reader reads it (measured with "separate settings" off), and the editor needed
no change because `LmMidwayTable` already followed the operand. `HasLmMidwayTableBlock` is the
property, and ShaoBase and juz have it too.

**LM can RELOCATE the four vanilla secondary tables, so read them through the operands.**
`Modify Secondary Entrances` + save on a v21 build came back with `$05F800/FA00/FC00/FE00` all
copied into bank $10, 0xCC5 apart, and the four readers repointed (`$0DE191`/`$0DE198`/`$0DE19F`
in LM's reader block, `$05DC81` in its secondary readers). The same save on ShaoBase left the
vanilla four alone and merely re-allocated the two extension tables 2 bytes lower, so this is
not something every save does — but once it has happened, reading the vanilla address shows a
stale copy and writing there goes where the game no longer looks. `Rom.SecondaryEntranceTable`
therefore follows the reader's operand when the reader is present and keeps the vanilla address
when it is not; the relocation itself is left to LM (it copies the contents across, and no
warning is raised). What triggers it on a prepped base and not on ShaoBase is unprobed.
`EntrancePlacementTests.v10_stamps_lunar_magics_separate_midway_routine` pins the blob equality;
`the_midway_routine_places_mario_from_its_own_record` runs it. ShaoBase and DogsOfWar ALSO route
the midway branch through a bank-14 block (`$05D9DA JML $14EE49`, keyed on RAM `$7FB426` that a
larger `$7FB4xx` engine populates); juz does not and its midway works, so that block is a
different feature and stays unmapped. Also not installed: LM's per-ROM hooks at `$05D9A1`
(vertical check) and `$05DA17` (FG/BG init), which none of this depends on for vanilla-height
levels.

The previous v10 used private stubs on the two `JMP $05DA17` sites and its own table. Driven
through LM's GUI (CONTRACT §0), LM wiped the stubs and left `$05D9FE` jumping into cleared space.
That is what a mechanism LM does not know looks like after LM touches the ROM.

### Level entry — height, FG/BG initial position, "relative to player"  [CONFIRMED, after.smc + ShaoBase]

Method 2 puts Mario anywhere; the camera's first frame is still one of vanilla's four fixed
offsets (`$05F400` bits 0-3 → `$1C`/`$20`). LM's answer is its **level-entry engine**: two code
blocks a plain save installs, fed by fourteen hooks. Block A (after.smc `$108141`, 0x510) is the
`$05DA17` tail — layer-2 scroll setting, `$1443-$1452`, then the FG/BG initial position, from
vanilla's table or **relative to Mario's Y** when `$13CD` bit 7 is set — plus the `$009708` level
start (width from the header, `$142A`/`$F9`), the layer-2 scroll bounds (`$00F871/$00F77B/$00F79D`)
and two bounds checks (`$05BCA5`, `$00E966`). Block B (`$108AD5`, 0x3C0) holds a per-level height
byte, a height LUT (index 0 = `$1B0`), and the code that gives the level a height in RAM: `$13D7`,
`$1936`, the per-screen tilemap pointer tables `$0BF6`/`$0C56` (`$05DA8A/$05DB5F`, `$0C9436`,
`$02950B/$0295BE`, `$00F70D`, and the `$05D9A1` vertical check).

LM relocates the blocks per ROM and the ONLY bytes that change are 16-bit in-bank operands (two
jump tables, `JSR`/`JMP`) and the bank byte of four long operands — after.smc vs ShaoBase
(`$139F01`, `$10F4D1`) differs nowhere else. So prep v10 keeps LM's in-bank offsets and moves the
bank: `$1F:8141` and `$1F:8AD5`, four bank bytes patched, fourteen hooks with the bank byte
rewritten, `$06FA00` (LM's extended layer-2 scroll byte) at its initial `$20`. `LmLevelEntry` has
the bytes and the map; `EntrancePlacementTests.v10_transplants_lunar_magics_level_entry_engine`
pins the equality and `the_tail_hook_sets_the_camera_relative_to_mario_when_asked` runs it.

**The relative camera, decoded** (block A +0x8D..): with `$13CD` bit 7 set, `$1C` (FG Y) =
Mario's `$96` + the entrance's `$05F400` low nibble x16, negative when `$06FC00` bit 6 is set
(`ORA #$FF00`), clamped to the level height less a screen; `$20` (BG Y) then comes from the
layer-2 scroll rate handler (`JSR ($8334,X)`, indexed by `$1414`), the BG height in `$13CD` bits
0-5, and `$13CD` bit 6 = "BG relative to FG only". The main entrance's byte is `$06FE00` (LM
default `$1A` = 26 BG tiles), the midway's is its FG/BG table byte, a secondary's the sixth table.
`MainEntrance.FgBgRelative/FgOffsetNegative/BgRelativeToFg/BgHeight` and `SecondaryEntrance.FgBg`
carry them; the level-properties dialog exposes them.

**Vanilla-height only, by construction** — was the state until the height half was transplanted
too (`LmLevelEntry.Height.cs`): block C (`$108EED`, LM's sprite-stream loader with its per-screen
cache and spawn-range tables), the three small blocks (`$108E9D`, `$108EC5`, `$1092AD`), twenty-one
hooks, and every in-place edit that reads the height from RAM — the object engine's column stride
(`ADC/SBC #$B0` → `$13D7` at `$0DA963/96B/9D6/9F7/9F9`, `$0DBB16`), the loader's plane-pointer maps
(`$00BDA8`… → `$0BF6`/`$0C26`/`$0C56`/`$0C86`) with vanilla's ROM→RAM copy skipped (`$00A873`), the
block-probe tables (`$BA60`… → `$0CB6`…) in banks 00/01/02, the sprite Y compares, the layer-scroll
bounds (`$00F478`…), LM's `$05DD00`/`$0EF560` level-init routines, and LM's extended-object handlers
(`$0DA112`, `$0DE1AC`). Same diff discipline: block C differs from ShaoBase's copy in eight in-bank
operands and five bank bytes; the rest is identical. `EntrancePlacementTests.v10_transplants_…`
pins all of it; `RomPrepTests.a_taller_level_gets_its_height_into_ram…` runs the chain at LUT
index 0x17 and sees `$13D7 = $950`, the pointer tables at that stride, the block-probe copies.

**How a tall level places past row 31 — decoded by running DogsOfWar `$109`/`$10F` through the
emulated loader with attribution (`--tallprobe`).** Standard-object Y stays 5 bits. LM's ext 01
handler (`$0DE1D0`) sets `$8B` = X-nibble × 2 besides the screen, and its ext 03 (`$0DE1E0`) sets
`$8B` = Y × 2 and the screen from X; the object loader adds `$8A` (16-bit: band × 0x200 bytes = 32
rows) to every object's plane pointer (`$0586A1`) and zeroes it at layer start (`$0583C7 → $0DE1F0`).
So the stream carries a **32-row band**: `row = band × 32 + Y`, set by a jump before the objects
that use it — DogsOfWar `$109` (one column, 896 rows) is `ext03 y=17 … objects … ext03 y=18 …`.
`LevelObject.Band`/`AbsoluteY`, the parser's band tracking and `LevelEncoder.NormalizeStream`'s band
jumps (ext 01 with the band in X below 16; ext 03 above — which only occurs in one-column levels,
where ext 03's 4-bit screen is enough) carry it; `LevelHeightTests` pins the round trip and renders
Dogs' levels to their full height through their own engine.

**The height LUT is the width×height trade.** Block B's 32 heights are exactly those with
`columns × height ≤ 0x3800` (the tilemap RAM): `1B0` (32 columns) … `950` (6) … `3800` (1). Width is
the header's screen count; `EditorSession.ApplyEntry` refuses a pair that does not fit. Height byte
bits: 0-4 LUT index, 5 extended sprite stream, 7 "vertical positioning" (LM sets it on every level).

**The render half — LM's own, at LM's own address (`LmLevelRender`).** Drawing a tall level to VRAM
past row 27 is LM's redraw engine in its bank-$1F block (after.smc `$1F8008-$1FB397`, 13 KB):
`$0586F7 → $1FB00F` tracks layer positions in `$7F830B`… and redraws columns via `$1FA70E`/`$1FA777`,
with the level-mode tables at `$1FA41C`/`$1FA626`/`$1FA69A` (`$0580C0/C4/C8`), `$0580A9 → $1FAFF1`,
`$008751 → $1FA2D2`, `$00A5A2 → $1FA28D`, the layer-2 scroll code at `$1FAFA0`/`$1FAFC4`
(`$00F6E4`/`$00F7E8`), and LM's VRAM patch (`$0081E2 → $1F8008`, which is what `HasLmVramPatch`
sees — BG2/BG3 GFX bypass now uploads in-game). Scanned for cross-bank operands: it references only
itself and fixed bank-0E/05 addresses, so v10 copies the whole RATS block verbatim to `$1F8008`
(our own blocks A-F sit in bank $1E to leave it room) and takes the pieces it calls: `$0EFD00/50`
(VRAM column stride `$1B0`/`$200` by the layer-3 flag LM keeps in `$7FC00B`), `$0EF510` (the
`$05803B` hook that sets it from a per-level byte at `$0EF310`), LM's 12-byte sprite-bank stub at
`$0EF300` (ours was a longer private version overrunning LM's `$0EF30C` pointer and that table)
with its `$05D8E2 → $0EF550` level-word mirror, LM's Direct-Map16 handlers (`$0DF08A..`, `$0DFEA0..`
— whole regions, replacing v5's private versions), the exit/secondary plumbing (`$05D7E2/7EA/81C`
readers, `$05DBC2 → $03BB00`, `$01E762 → $03BCA0` overworld exit, `$04E5F1 → $05DCB0`, `$0DA536`),
LM's extra FG/BG init entries (`$05D718/728`), and the small bank-00/01/05 tweaks LM makes
alongside (`$00BF3C..`, `$00AF4C..`, `$00C07D..` + `$00C25C` through the ladder entry we already
share, `$05BAAA..`). Every byte is asserted equal to after.smc's.

**Still ours, by choice:** the Map16 ladder body (`$06F540..`) and LM's per-site acts-like pointers
(`$019534`, `$02961B`, `$02A6EC`, `$00F4DE` with the `$00F4A0`/`$00F4EB` fallback tied to it), the
loader-side ladder calls (`$058A65..` and their `$3F` masks), the OW helper `$04DCFA`, LM's metadata
(`$0FEFCD`, `$0FFFE6..`), and after.smc's re-saved level `$105`. LM's extended sprite LIST (header
bit 5, `FF nn` bands — CONTRACT §11) is read and written by the editor.

**A lesson from doing it.** Copying LM code by vanilla-vs-LM diff RUNS is unsafe wherever one of our
own stamps already sits: a byte that happens to equal vanilla's (`$FF` in `JSR $FF10`) splits the
run and leaves our byte behind — the DM16 handlers did exactly that. Copy whole regions there.

Two RAM effects worth knowing: block A sets bit 15 of `$5A` as its marker, and `$13CD` stops
being "midway screen" (vanilla) and becomes the FG/BG byte — which is why every LM save also turns
`$05D9C3`'s `STA $13CD` into a load (§ above).

### Object handlers with `$13D7`
`$0DA963`, `$0DA9D6` — vanilla's `ADC #$B0 / ADC #$01` screen stride is a 16-bit `ADC $13D7`, the
level height the entry engine keeps in RAM. Transplanted with the height half (above).

### Bank 03 — new code in vanilla free space
`$03BB00`, `$03BCA0`, `$03BCDC` (196 B of new code, `JML $1092AD`, `JSL $05DC80`), `$03FDFF`
(513 B). INFERRED: overworld/level-entry support. Unmapped by us entirely.

### Tables LM initialises
`$05D718` (20 B), `$05FACE` / `$05FCCE` / `$05FECE` (the secondary entrance tables — DATA, and
we read and write these already), `$0EFD50`, `$0FEFCD` (371 B), `$0FFFE6`, `$05DE00`,
`$03FDFF`. Mixed data and unknown; the entrance ones are explained, the rest are not.

### Not in a plain save, but LM installs on demand
From LM's help, these install when their dialog is merely OPENED, so any LM user will add them:
Super GFX Bypass, Layer 3 GFX bypass, ExAnimation (`level_super_bypass.htm`,
`level_layer3_gfx.htm`, `level_extend_ani.htm`), and the VRAM patch + FastROM on the next level
save (`option_vram.htm`). We detect all of these; we install none. `HasLmVramPatch` being false
is why our BG2/BG3 bypass slots stay editor-only.

### 4bpp graphics — the overworld half is LM's since prep v13, the rest since v25  [CONFIRMED 2026-08-29, ShaoBase + BigEye + DogsOfWar vs vanilla and exanim_1; closed 2026-09-11]

Found by the overworld: on every prep from v6 to v12 the OW's animated tiles (water, clouds)
were garbage in Mesen (v3 clean; LM's plain save exanim_1 clean). Root cause, traced with Lua in
MesenCE (VRAM/RAM dumps and PC traps — note bank-04 code writes RAM through `$04:xxxx`, which a
trap on `$00:`/`$7E:` addresses never sees):

- The OW copies eleven tiles (three water, then cloud frames) out of the **GFX decompression
  buffer** into `$0AF6` — on load and then every frame, frame-indexed — with its own reader:
  an offset table at `$048000` (67 words, `$AD00 + tile*24`), bank hard-coded `$7E`
  (`$048095`, `$04814F`), and a 3bpp expander at `$0480B9`. The file it reads is whatever was
  decompressed last (GFX14 on Yoshi's Island). V4 had moved the buffer to `$7FA000`, so this
  reader read stale `$7EAD00` memory; had it found the buffer, it would have read 4bpp as 3bpp.
- **LM's 4bpp mode** (identical in the three hacks, absent from exanim_1): buffer stays at
  `$7EAD00`; the table is rescaled to `tile*32`; the expander copies 16 word rows
  (`$0480BD: 08→10`) and drops its plane-2 loop (`$0480D0: →RTS`); and because a 4bpp file now
  runs to `$7EBCFF`, the OW sprite tables at `$7EB9xx/$7EBAxx` move to `$7FC5xx/$7FC6xx` — 21
  two-byte operands in `$04F2B8-$04F3D0`, all of bank 04's references. The layer-2 tile buffer
  at `$7EB900` is overrun by LM too; every LM hack lives with it. **Prep v13 stamps exactly
  these bytes** (`RomPrep.AppendV13Stamps`, compared against ShaoBase in
  `v13_overworld_tile_reader_takes_4bpp_like_lunar_magic`), verified in Mesen: fresh prep and
  the dev project on an upgraded base both render the OW identically to vanilla.
- **The whole overworld pipeline now has an in-game check** (2026-09-11, `Test-RomOverworld.ps1`;
  reference/MESEN.md). A prep v28 base reaches the map by playing — pulsed Start, mode `$0E`,
  submap 1 — and its tile graphics come out **byte-identical to Lunar Magic's** (two LM-saved
  vanillas agree) and deliberately NOT to stock vanilla's: `$2000-$2FFF` is where they part, which
  is the GFX08 compromise v25 adopted. That covers v13's reader, v18/v19's stub and v25's AN2 pass
  end to end, which is what the "not verifiable headless" note here used to disclaim.
- **The rest is prep v25** (2026-09-11, CONTRACT §7d-25). Through v24 these stayed ours: the
  `$00AA80` dispatch kept vanilla's filter path (LM: `CPY #$08/#$1E → #$32` with the plane baked
  into its files), GFX33 stayed 3bpp behind vanilla's `$00B888` expander (LM reads it as 4bpp —
  the source of "garbled in LM" for anything drawn from AN1), the GFX0F/00 RAM expanders were our
  v4 rewrites (LM: `$00A830 → $0EFC00`, vanilla loops kept), and `$00A149` stayed live. V25
  stamps LM's bytes for all of it and bakes the data they expect. Two measurements settled the
  data: LM's stock GFX1E is the plain 4bpp conversion with plane 3 = `p0|p1|p2` on every tile,
  its GFX08 the same on exactly 24 tiles (0x37-3B, 0x47-4B, 0x56-5B, 0x60, 0x6E-70, 0x7A-7B,
  0x7E-7F — the ones the overworld draws; a level's GFX08 tiles stay as they were, which is the
  "compromise": one file cannot serve both tilesets), its GFX33 the plain conversion — all three
  identical across TestRom, DogsOfWar and ShaoBasePrepatch, where ShaoBase and BigEye redraw
  GFX08 and GFX33 (those two are hacks; the earlier "hand-made" reading came from them). The
  blobs' layout is the one deliberate divergence: LM keeps GFX33 at `$088000` with GFX32 behind
  it, compressing the 4bpp blob to 0x1C68 where our LZ2 needs 0x262D — past vanilla's 0x59F9-byte
  footprint — so v25 parks both in one RATS block behind the converted files and repoints the
  three operands (`$00B88B`, `$00B890`, `$00B8D8`). LM follows them: `-ExportGFX` on a v25 base
  returns GFX08/1E/32/33 byte-identical to ours, and `-ImportExGFX` on a v25 base writes its file
  and bookkeeping and **no code at all** (v24: five code sites). The `$00A149` NOP needed the
  loader to own the buffer's last file, so v25 adds an AN2 pass (`RomPrep.An2Pass`): record w0's
  file for an enabled level or a submap, GFX14 otherwise. What stays ours: the loader itself
  (LM: `$00AA50 → $0FF780`, `$00AA6C → $0FF160`, records at `$12AD08`, pointers at `$0FF200`).
  **The palette engine** went the same way one version earlier: prep v24 stamps LM's `$0EFC00`
  block, `$0095E9` and `$00A5BF → $0EF570` byte for byte.

### Per-submap GFX — LM reads and writes ours from prep v19, without its loader  [MEASURED 2026-09-09/10; four LM installs on a v17 base, then eight round trips on v18/v19 builds]

Prep v18 gave each submap its own FG/SP file list in LM's own table at LM's own index with LM's
own record layout (reference/OVERWORLD.md §4, CONTRACT §7d-18/19), and prep v19 is what makes
LM's *Overworld ▸ Submap GFX* dialog read and write it. It is **not** the loader transplant this
section used to call for; the probe is why:

- LM's install of this hack replaces its whole GFX loader block — `$0FF02A`, `$0FF15C`,
  `$0FF780`, `$0FF8A0`, `$0FF9C0`, `$0FFAB0`, `$0FFAF0`, `$0FFB20`, `$0FFD80`, `$0FFE93`,
  `$0FFFE7` — precisely the region prep v2/v14/v15/v16 authored. Transplanting it means
  re-establishing v14-v16 on LM's newer internals.
- But **with LM's layout stamp `4C 4D 03 01` at `$0FF15C` present, LM never installs any of
  that.** It reads the seven records, writes an edited slot straight into our table, and leaves
  our loader, our stub and our records alone: an LM overworld save over a marker-carrying build
  changed 55 runs, none of them in `$0FF7xx-$0FFDxx`, and the one byte it wrote in the table was
  the slot the dialog changed (`$12D0AC`). So the ~2KB transplant buys nothing the stamp does.
- **The address comes from the stub, and only from the stub.** LM parses `$0FFAB0`'s `ADC #imm`
  at +9/+10 and `LDA #imm` at +18: with the stamp faked over v18's own stub the dialog read the
  address those bytes happened to spell (`$FF0201`) and showed every slot as 0; setting those
  three bytes to our table made it show our files. v19 therefore emits LM's 35-byte stub
  verbatim with our address in those fields, and replaces only LM's closing `RTL` with the `$FE`
  arming our own loader reads.
- **An LM save does NOT lose the lists** — the earlier claim here that it would was inferred, and
  it is wrong. Measured both ways: on a v18 ROM (no stamp) LM's overworld save is a no-op,
  0 changed runs, records untouched; on a v19 ROM LM updates the slot it was asked to and leaves
  the other six records alone.
- **Two saves in, LM still keeps its hands off our GFX code.** It does repoint our v17
  overworld-ExAnimation transplant's settings operand (`$1EA84A`) and zero our tag at `$1EB458`
  — that is LM's FIRST overworld save installing its own hack suite, settled under "RATS block
  shapes" below; the record table our reader follows is untouched.

What stays divergent: the loader is ours (see the bullet above this section). AN2 (record w0) was
editor-only through v24 — the overworld still ran vanilla's `LDY #$14 : JSL $00BA28` at `$00A147`
where LM NOPs the JSL — and is loaded from v25 (the loader's AN2 pass, CONTRACT §7d-25).

### The LEVEL Super GFX Bypass dialog — readable and writable from prep v20  [MEASURED 2026-09-10]

Found while closing the submap one: LM's *Level ▸ Super GFX Bypass* dialog showed `0` for all
eleven slots on any prepped base, which had been true and unnoticed since prep **v2**. Two
independent causes, both now measured, and neither was a bug in our record:

- **LM takes the table's address from a FIXED offset**, the 24-bit operand of the `LDA base,X`
  in its own loader at `$0FF7FF` (`RomPrep.LmGfxBaseOperand`) — the one place in an LM save
  where that address appears at all. It does not scan: our loader carried the identical fetch
  idiom 0x67 bytes earlier and the dialog read `$E8E8FA` out of whatever sat at `$0FF7FF`.
  Planting the address there made the dialog read our records. **Prep v20 therefore parks the
  loader's own record fetch at that address** (a code motion inside the block — the body moves
  into the gap the fetch leaves, the enable test becomes `BMI` to pay for it, and `PadTo` /
  `AssertAt` make a future byte over budget a prep-time throw).
- **The record LAYOUT was right all along** (w7 FG1, w6 FG2, w5 BG1, w4 FG3, w3 BG2, w2 BG3,
  w11-w8 SP1-SP4, w0 AN2 — CONTRACT §7d). The first measurement looked one word off because a
  base without the v19 layout stamp makes LM read an OLDER record layout; with the stamp in, a
  record whose word k held `0x10+k` read back as FG1=0x17, FG2=0x16 … AN2=0x10, exactly ours.

Verified on plain `--buildproject` output: a v20 build with level `$105`'s FG1/BG1/SP1 repointed
shows `F` / `3` / `C` in LM's dialog with the rest "7F Skip File", and a slot changed in LM lands
at `$12B0AC` — `base + 0x105*0x20 + 0x0C`, w6 — with our other three slots untouched.

**LM's level SAVE still warns** "Existing data format or size not recognized! — Midway entrance
data" (our v10 entrance structures; a separate, older gap). It writes the record anyway, and its
two routine bank-00 level-save patches (`$00F4A0` `PLX : LDY #$25` → `STY $1693`, and `$00F4EB`)
are the same two it applies to its own saves, so they are LM being LM rather than a reaction to
our ROM.

Not verified in-game on hardware: the headless Mesen harness cannot reach the overworld at all
(reference/MESEN.md, "The OVERWORLD is unreachable too"), so the overworld load path is checked
under `Cpu65816` — the real stub, the real loader, the VRAM writes captured.

### RATS block shapes — every table the prep stamps, audited against LM  [MEASURED 2026-09-10, ShaoBase/juz as controls]

The midway lesson (above): Lunar Magic validates the SHAPE of a block it owns — a dedicated block,
data at tag + 8, the size LM itself would allocate — and byte-correct data in the wrong shape
fails in one of three ways: a refusal box, a silent relocation, or a silent release that zeroes
the block. Every prep-stamped table was checked for the shape LM uses and for what LM does to it
on the save that touches it. Nobody needs to audit these again; the rows say what was run.

| table (prep) | ours | LM's own | read via | what LM's save did | verdict |
|---|---|---|---|---|---|
| separate-midway tables (v21) | `$13BD08`, own 0x800 block | ShaoBase `$128008`, 0x800 | blob operand (`LmMidwayTable`) | level save silent, writes flags into ours | **right** |
| separate-midway routine (v10 → **v22**) | v10: tail of the 0xD00 secondary block; v22: own 0xD0 block at `$13C510` | ShaoBase `$10FDD7`: tag + 0xC4 blob + 8×`FF` + `LM 10 01` = 0xD0 | hook operands `$05D9E3`/`$05D979` | **secondary save on v21 ZEROED it** (below); on v22 untouched | **fixed, v22** |
| secondary Y-high / FG-BG (v10) | `$13B000`/`$13B200` in one 0xD00 block | two 0x1FE blocks (ShaoBase `$10F0C5`/`$10F1CB`) | reader operands `$05DC85`/`$05DC8A` (`LmSecondaryYHighTable`) | *Modify Secondary Entrances* save re-allocates both into bank $10 (3261-byte blocks), repoints the readers, **releases ours: tag AND all 0xD00 bytes zeroed**; the four vanilla tables move too (`Rom.SecondaryEntranceTable`) | fine once nothing else shares the block; no warning either way |
| level ExAnimation table (v11) | `$1EA0A0`, own 0x600 block | ShaoBase `$12A313`, 0x600 (`STAR FF05 00FA`) | engine operand (`LmExAnimBase`) | *Edit Level ExAnimated Frames* + save: LM wrote the 17-byte record as its own block and repointed **our** entry in place; tag untouched; ShaoBase identical | **right** |
| OW ExAnimation record table (v17) | `$1EB440`, own 0x15 block | LM's install: 0x15 | setup operand (`LmOwExAnimBase`) | two overworld saves: untouched | **right** |
| OW ExAnimation settings (v17) | `$1EB460`, own 7-byte block | LM's: 7-byte block | blob + 0x4A | see below — LM re-installs its own on the first OW save | right; nothing to fix |
| GFX bypass records (v2/v18) | `$129000`, own 0x40E0 block | ShaoBase: 0x2D08 INTO one 0x6E00 block at `$108000` (ExGFX pointers first) | `$0FF7FF` operand (v20) | Super GFX Bypass save writes the slot in place | right — but the size rule is EXACT (below) |
| ExGFX 0x100+ pointers (v2) | `$138008`, own 0x2D00 block | first 0x2D00 of that same 0x6E00 block | `$0FF873` operand (v27) | `-ImportExGFX` writes entry 0 in place (below) | right from v27 — and the shape never mattered |
| extended Map16 defs (v1) | `$128008`, own 0x800 block (page 2, all default) | one full bank per range | ladder slot 0 | Map16 F9 save drops range 0 to bank 0 and populates ranges 1-2 of its own; frees our block if we claim its marker (below) | shape right; the marker is the rule |
| acts-like table (v1) | `$118000`, own 0x8000 block | LM: same table, its lookup's operand | our `$06F800` remap (v26) AND LM's `$06F617` core, both reading it | untouched by every save tried, and the Map16 save keeps our address (below) | right from v26 |
| checksum balance (v9) | `0x80000`, own 0x140 block | — (ours only) | `RatsWriter.Balance` | untouched by every save tried | right |
| level engine blocks A-F, render bank $1F (v10), ExAnim engine/MVN/clear (v11), OW ExAnim blob (v17) | LM's own bytes, each `Pc(x) - 8` = tag + 8 | same code, LM-allocated | hook operands | untouched by level, secondary, ExAnim, overworld and Map16 saves | right |
| sprite size table (`SetSpriteEntrySize`) | `RatsWriter.Allocate`: tag + 8, 0x400 | 0x400 (help "Custom Sprite List Sizes") | `$0EF30C` + `0x42` | not exercised | right by construction |
| ExGFX 0x80-0xFF pointers `$0FF600`, palette table `$0EF600`, sprite bank table `$0EF100`, `$03BCC0` alt-ExGFX, method-2 bytes `$05DE00`/`$06FC00`/`$06FE00` | vanilla-space tables at LM's own addresses | same | fixed | — | not RATS blocks; nothing to shape |

**The midway ROUTINE — a secondary-entrance save wiped it (v10-v21), fixed by v22.** Found in
yesterday's F2/F3 probes and reproduced today: *Modify Secondary Entrances* + save on a v21 build
re-allocates the two extension tables into fresh blocks, repoints `$05DC85`/`$05DC8A`, and RELEASES
the block they came from — LM's release zeroes the tag AND the data, all 0xD00 bytes of
`$13AFF8..$13BD00`. V10 had put the midway routine at `$13BC00`, the tail of that block, so after
the save `$05D9E3` and `$05D979` JSL into zeros (`BRK`) and `HasFreeMidwayPosition` reads false.
LM raised nothing — a release is not an error to it. ShaoBase's routine has a 0xD0-byte block of
its own (`$10FDD7`: tag, blob, eight `$FF`, `LM 10 01`) and the same save left it alone. Prep
**v22** stamps exactly that block at `$13C510` and repoints the two hooks; the readers already
followed the hook operand. Re-run on a v22 build: the save released the secondary block as before
and the routine block, its tag and both hooks came through untouched; a Main/Midway save with
"separate settings" on then wrote `0x20` into the v21 flags table at `$13BE0D` and nothing else
of ours; Mesen builds a level on both. `HasLmMidwayRoutineBlock` is the property (ShaoBase and
juz have it, after.smc does not).

**The overworld-ExAnimation settings — LM re-installs, once; not a shape problem.** The `$1EA84A`
+ `$1EB458` runs in every overworld save of ours (A-E, L5, R2, RT, U, Z, and ow1 today) are LM's
FIRST overworld save installing its whole overworld hack suite (55 runs: bank 03/04/05 hooks,
three big bank-$10 blocks). As part of it LM writes the seven settings bytes into a fresh 7-byte
block of its own (`$10CF82`, `STAR 0600 F9FF` — the same shape as ours), repoints the blob's
`LDA long,X` at +0x4A to it, and releases ours by zeroing the tag (the data was already zero). The
record table at `$1EB440` and its tag are untouched, so `LmOwExAnimBase` and `ExAnimation.ReadSubmap`
read the right thing. A second overworld save (ow1 → ow2) changed two bytes — the slot edited and
one in `$0FF06F` — so this is not a per-save relocation. Nothing of ours reads the settings; if
something ever does, it reads them through the operand at blob+0x4A (the hook at `$048086` names
the blob), never at `$1EB460`. No control on an LM-authored base exists: none of the reference
ROMs carries the overworld hack (`$048086` is vanilla in ShaoBase, juz, after.smc, exanim_0-4).

**The GFX bypass table — LM's size check is EXACT, and 0x40E0 is exactly right.** Four probes on a
v21 base, tag size word set to 0x1000, 0x4000 (the v2-v17 shape), 0x5000, and the tag removed:
LM's Super GFX Bypass dialog showed `0` for every slot in all four. Only 0x40E0 — 0x207 records
of 0x20, the count LM's layout stamp `LM 03 01` defines — reads. LM's own table is not a block of
its own at all: in ShaoBase, gfx_after and juz it sits 0x2D08 into one 0x6E00 block at `$108000`
(ExGFX 0x100+ pointers, then 0x40E0 of records, then a 0x20 default record), so LM evidently
accepts either its own layout or a standalone block of the exact standalone size. Nothing to fix;
do not resize this block.

**`-ImportExGFX` no longer breaks the ROM — prep v23.**  [MEASURED 2026-09-10/11] Vanilla's GFX00
re-inserted as ExGFX100 on a v21 base ("ExGFX Insertion Complete", exit 0) left a ROM that no
longer built a level in Mesen. The killer was one write: LM re-initialises its ExGFX 0x80-0xFF
pointer table (`$0FF600`, 0x180 bytes) on import, and **prep v2 had parked the GFX arm stub at
`$0FF770` — the table's last sixteen bytes, ids 0xFB-0xFF** — so `$0583B8` JSL'd into zeros.
Proof by subtraction: restoring vanilla's five bytes at `$0583B8` on the broken ROM, and nothing
else, made it build a level again (the stub's re-arm has been redundant since v10, because LM's
own `$0EF550` stub stores level+1 into `$FE` at `$05D8E2`, before `$0583B8` runs).

**But the JSL at `$0583B8` is part of how LM decides its GFX bypass is installed.** The first
v23 candidate put vanilla's bytes back there; the same import then installed LM's ENTIRE loader
over ours — `$0FF780`-`$0FFE93`, a 0x6E00 pointers+records block, `$0583B8 → JSL $0FF7F0`, the
five DM16 dispatch entries — and the ROM stopped booting (Mesen exit 1). On a base with a JSL at
`$0583B8`, any target, it does not. So v23 keeps the hook and moves only the body, to `$0FF890`:
the gap LM's own layout leaves between its loader group (`$0FF780`-`$0FF884`) and the slot
tables at `$0FF8A0`, which both LM installs over our bases left untouched. LM's own target,
`$0FF7F0`, is its record-cache fetch (0x43 bytes, `PHP … STA $7FC006/8/9 … PLP : LDA $1925 : CMP
#$09 : RTL`) and needs the address our resolver occupies. Verified on a v23 build: the same
import leaves `$0583B8`, `$0FF890` and `$0FF770` alone, installs no loader, and the ROM boots.

What the import DOES install on a v23 base, and what is still open about it:

- **LM's 4bpp/palette bundle lands on our v1 and v4 stubs.** `$0EFC00`-`$0EFCAF` (176 bytes,
  byte-identical in ShaoBase/BigEye/DogsOfWar; juz lacks it), `$0095E9` → `JML $0EFC50 : JSR
  LoadPalette : JML $0EFC80 : NOP NOP` (its first four bytes are our v1 hook, which is why byte
  `$0095E9 == 0x5C` was ever a detector), `$00A830`/`$03DDC9 → JML $0EFC00` (its GFX00/0F RAM
  expander, over our v4 rewrites), `$00B895` `LDY #$2000 → #$7D00` (GFX33 read as 4bpp — ours is
  3bpp, §2 "4bpp graphics"), `$0093F7 → JSL` a per-ROM 13-byte NMI-wait routine (`LDA #$81 : BIT
  $4212 … STA $4200 : RTL` + `LM 00 01`; xg `$108150`, ShaoBase `$10EE10`), `$00A149` NOPped, and
  373 bytes of bookkeeping at `$0FEFCB`. **The palette half of this is prep v24** (CONTRACT
  §7d-24): `$0EFC00`-`$0EFCAF`, the `$0095E9` hook, `$00A5BF → JSL $0EF570` with LM's 0x57-byte
  fade-in routine — the one piece the import did NOT write, which had left v1's `JSL $0EFC60`
  pointing into the middle of LM's `$0EFC50` loop — and the `$0093F7` NMI fix, all byte for byte
  (ShaoBase, BigEye and DogsOfWar agree); v1's stubs are retired. On a v24 base the import wrote
  only the 4bpp-mode pieces: `$00A830`/`$03DDC9 → JML $0EFC00`, `$00AB0B`'s filter body, `$00B895`,
  and the `$00A149` NOP — LM's code over our 3bpp GFX33 and unbaked GFX08/1E, plus a NOPped GFX14
  decompress with no AN2 pass behind it. **Prep v25 stamps those too** (§2 "4bpp graphics"), and
  the same import on a v25 base writes its file, the `$0FEFAD` bookkeeping, `$0FFFE7` and the size
  byte — no code (measured 2026-09-11; both ROMs boot in Mesen).  [CLOSED]
- **The imported file's pointer went where we do not read — LM was reading our opcodes.**
  [FIXED in prep v27]  LM allocated the file at `$208000` (a 2230-byte block in the 2MB expansion
  it grew the ROM to) and wrote the pointer at `$258AA5`, unprotected in that expansion, while
  entry 0 of our 0x100+ table at `$138008` stayed zero. **`$258AA5` was our own code**: LM takes
  the table's address from the 24-bit operand at the fixed `$0FF873`, and through v26 that byte
  was the `A5 8A 25` of our resolver's `LDA $8A : AND $8B` — `$258AA5` read little-endian. This is
  `$0FF7FF` for the records (v20) all over again, and the block shape had nothing to do with it.

  Measured, in this order: (1) on **ShaoBase** the same `-ImportExGFX` writes the pointer straight
  into `$108008`, exactly the value at its `$0FF873` — so LM reads that address and does not
  validate the block; (2) planting the address at `$0FF937` (LM's *other* copy of it) changed
  nothing, and neither did FF-filling our table to look like LM's; (3) hand-patching
  `BF 08 80 13` at `$0FF872` made the very same import write `00 80 20` into `$138008`. Every LM
  ROM carrying the feature holds its own table's address at `$0FF873` behind a `BF` opcode
  (gfx_after, juz, ShaoBase, ShaoBasePrepatch, BigEye, DogsOfWar, TestRom); `after.smc`, which
  lacks the feature, has `FF` there.

  **Prep v27 relays the resolver** so the 0x100+ fetch's operand lands on `$0FF873`
  (`RomPrep.ExGfxPtrOperand`, CONTRACT §7d-27) — only the order of its three paths changes.
  Verified on a fresh v27 base: the import writes `$138008` entry 0, `--gfxsheet <rom> 100`
  decodes the file (128 tiles, 4bpp), and base and post-import ROM both boot to level `0xC7`.

  Tooling note that cost an hour here: `--diff` compares only up to the SMALLER file, so every
  byte LM wrote in the expansion — the `$258AA5` pointer included — was invisible in the diff of a
  1MB base against a 2MB result. Scan the whole file when a claim rests on the expansion.

**Map16 F9 on a prepped base: LM's acts-like core landed on our remap.**  [FIXED in prep v26]
Editors ▸ 16x16 Tile Map Editor, F9, no edits, on a v25 base: LM wrote 13 runs — the ladder slots
at `$06F553`, its acts-like core at `$06F5E4` (96 B, **over our remap at `$06F5F0`**, which the
four vanilla `JSL $00F545` sites still pointed at), hooks at `$04DCFA` and
`$058A65`/`B45`/`C33`/`D2A`, `$06F65C`/`$06F70B`/`$06F7A0`, a 0x8000 acts-like table at `$1A8000`,
and slot 0 of the ladder set to `$00:F000` ("no defs" — our page 2 is all default tiles). The four
sites then met LM's `STA $65 : LDY #$0000 : RTL`: the ROM no longer built a level in Mesen
(`Test-RomBoots` level `0x00`, exit 1). ShaoBase: F9 changed nothing, because it has the core.

**Prep v26 is that core, at LM's address with LM's bytes, and our remap moved to `$06F800`** —
the free run behind LM's trampolines, FF in juz, TestRom, ShaoBase, BigEye, DogsOfWar and
ShaoBasePrepatch (CONTRACT §7d-26). Measured after: the same F9 boots and builds level `0xC7`,
LM keeps our acts-like table (its lookup's operand stays `$118000`, where on v25 it had allocated
a fresh one), leaves the four sites and our remap alone, and a second F9 writes nothing.

Two findings worth keeping from getting there:

- **Claim only the hack you carry.** The first v26 also stamped LM's *Map16* marker (`LM 12 01` at
  `$06F65C`, which the save writes). With it in, LM reads the extended-def block as its own to
  manage: it FREED our 0x800 block at `$128000`, tag and all. Without it, LM leaves the block and
  only re-points the slot. Same family as the RATS-shape rule below — a marker is a promise.
- **LM's install is staged.** Once the acts-like core is present, that save moves on to the next
  piece: it stops writing the `$04DCFA`/`$058A65` hooks and instead populates ladder ranges 1 and 2
  (banks `$1A`/`$1C`, two fresh 0x8000 blocks) with its per-tileset pages, leaving range 0 at "no
  defs". Legal by §7a-rev (holes are), and our reader follows the slots — but it does mean
  `IsPrepped`'s v1 clause (`LmMap16Defs.Bank != 0`) reads false on such a base and the app offers
  an upgrade, which restamps our block and slot 0. Same as it already is for `after.smc`.

**The methods that worked, for next time.** `lm-savelevel.ps1`, `lm-entrance.ps1` (`-MenuDown 3`
= ExAnimated Frames, 7 = Main/Midway, 8 = Secondary; `-Combos`/`-Fields`/`-Checks` dirty the
level), `lm-levelgfx.ps1`, `lm-submapgfx.ps1`, `lm-map16.ps1` (F9), and
`tools/lm/Invoke-LunarMagic.ps1 -LmArgs @('-ImportExGFX', rom)` with an `ExGraphics/ExGFX100.bin`
beside the ROM. `--diff` prints at most 60 runs and 20 new blocks and compares only up to the
smaller file; read the tag bytes directly when a claim rests on one address.

## 2b. LM's WRITE ROUTES — the audit  [MEASURED 2026-09-11, prep v29 base]

Every way Lunar Magic writes to a ROM, and whether it lands on something prep owns.
`tools/lm/Test-RomRails.ps1` is the check: it asks the prep itself for every byte it stamps
(`--stampranges`, 409 ranges) plus the handful `Apply` writes as DATA, and flags any changed run
that overlaps. **An overlap is "look", not "broken"** — three routes legitimately co-own a
structure with us, and the in-game checks say they are fine.

| route | runs | verdict |
|---|---|---|
| Level save (Ctrl+S) | — | clear (earlier sessions; v21/v22 fixed the midway refusals) |
| Secondary entrances save | — | co-owns: relocates the vanilla tables and repoints the readers (below) |
| Main/midway entrance save | — | clear from v21/v22 |
| ExAnimated frames save | — | clear |
| Overworld save | 55 | clear — nothing in the loader block, records or `$0FF15C` |
| Map16 F9 save | 25 | clear from **v26**; before that it landed on the acts remap and the ROM stopped building levels |
| Submap GFX / Submap Layer 3 dialogs | — | clear; both round-trip our records |
| `-ImportExGFX` | 6 | clear from **v25/v27**: no code at all, and the pointer lands in our table |
| `-ImportAllMap16` | 34 | clear; writes acts-like into our `$118000` |
| `-ImportCustomPalette` | — | clear; stores what we wrote, byte for byte |
| `-ImportSharedPalette` | 2 | clear |
| `-ExpandROM 2MB` | 3 | clear |
| `-TransferLevelGlobalExAnim` | 2 | clear |
| `-ImportLevel` (MWL) | 28 | **co-owns** 5 — see below; boots and reaches `$105` |
| `-ImportMultLevels` | 32 | **co-owns** 6 — same set; boots and reaches `$105` |
| `-ImportGFX` | 11 | **co-owns** 3 — takes GFX over wholesale; verified in game |

**The three legitimate overlaps**, all of them structures LM has every right to write:

- **`-ImportGFX` takes over graphics management.** It re-inserts every file from the `Graphics`
  folder, moves the GFX32/GFX33 boot blobs back to its own layout (`$08:8000` / `$08:9C68`,
  exactly TestRom's), rewrites the three pointer tables at `$00B992`, and FREES prep v6's
  converted-GFX blocks and v25's blob block. Safe because everything of ours that reads those
  files reads through the operands rather than constants: after the import our reader still
  decodes GFX08/1E/32/33 at their new homes, v25's `$00B895` 4bpp load is untouched, the ROM
  boots, and the overworld's tile graphics still match the base exactly.
- **`-ImportLevel` / `-ImportMultLevels` touch the secondary-entrance machinery** — `$05DC81`
  and `$0DE191` (the table readers) and the block at `$13AFF8` — which is the "silently
  relocate" behaviour already recorded under RATS block shapes. `Rom.SecondaryEntranceTable`
  follows the operand for exactly this reason.
- ...and **`$0EF100`**, LM's per-level sprite bank table (v10): a level import setting the
  imported level's entry is the table being used as intended, not damage.

**Not exercised**, and why: `-ImportMap16` (needs a single-level `.map16`, which only the GUI can
produce); `-ImportTitleMoves`/`-ExportTitleMoves` (refused — "ASM code not detected", the playback
hack is not installed on our base); and the GUI-only title-screen, credits and recording saves.
Those are the remaining unknowns in this table.

**Blind spot worth knowing about:** `--stampranges` reports STAMPS. What `Apply` writes as data —
`ConvertGfxTo4bpp`, `BakeLmFourBppFiles`, `MigrateSecondaryDestinationBit` — is listed by hand in
the script, and that list is what caught `-ImportGFX`. A new data step needs adding there or the
audit will wave it through.

## 3. What we write that LM does not

Direct Map16 object handlers (`$0DF150`, `$0DF08A` extent), the exit destination bit 8 before
LM's own patch exists on a vanilla base, the checksum balance at pc `0x80000`, our acts-like
remap at `$06F800` (v26; `$06F5F0` before) + table at `$118000`, and the v2 GFX loader. All RATS-tagged where they are
data; LM honours tags it did not write.

## 4. Where to start

0. **4bpp graphics** (§2 above) — done: the overworld half in v13, the rest in v25 (GFX33 as
   4bpp, the filter dispatch and body, the expander wrapper, the AN2 pass). What stays ours is
   the loader itself.
1. ~~**Acts-like**~~ — done. From v26 both mechanisms read ONE table (`$118000`); the round trip
   through `-ExportAllMap16`/`-ImportAllMap16` is exact in both directions, and v28 adds LM's
   chaining to the remap and to the reader (§1 "Block behaviour"). The `$00BA70 → $000CC6` reads
   this item used to list with it are a different feature — LM's per-level Map16 plane-pointer
   tables — and have been ours since v10's variable-height engine, pinned against LM's own ROM.
2. ~~Sprite extra bytes~~ — done: size table read at LM's registration and authored the same way.
3. ~~**The Map16 ladder's remaining ranges**~~ — measured 2026-09-11, and the reading half was
   already done: all eight slots are decoded, with sgdq2024 (ranges 4 and 5) as the oracle and
   `Map16RangeTests.the_reader_knows_all_eight_slots` pinning it. What that measurement DID turn
   up is a numbering collision — ranges 4-7 are LM's FG tiles 0x4000-0x7FFF, which is where this
   editor renumbers LM's BG pages — so `Map16TileCount` now stops at the BG boundary rather than
   letting the ladder shadow the fixed `$0D9100` table (CONTRACT §7a-rev). Writing those ranges
   stays out of scope: our numbering has nowhere to put LM's high FG tiles, and LM's own
   Direct-Map16 objects cannot address them either.
4. Everything in bank 03 and the level-load pipeline is unmapped; treat as research, not work.
