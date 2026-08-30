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
| `$00AACD` 4bpp upload | LM's byte sequence | — | the 32-byte loop is identical, **but it is the only piece of LM's 4bpp rework we share** — see §2 "4bpp graphics" (2026-08-29) |
| `$0DF08A`, `$0DFF50`, `$0D*FD` handlers | LM's own bytes (v10 restamps over v5's) | same | **CONFIRMED identical** (`LmLevelRender`) |
| `$0DA4B8` DM16 hijack | ours | LM's | same table entries — CONFIRMED identical |
| `$0EF100` sprite bank table | LM's layout, LM's `$0EF300` stub + `$0EF550` level word (v10) | same | **CONFIRMED identical** |
| `$00F4DE` acts-like call site | repoint one JSL | rewrites 43 B at `$00F478` | **different mechanisms for one feature** — see §2; the `$13D7` half of `$00F478..` is taken, the acts half is not |
| `$0DE190`-`$0DE1FF` ext-object handlers | LM's bytes (v10) | same | **CONFIRMED identical** — ext 01/03 carry the 32-row band |

## 2. LM writes, we do not — the inventory

### Block behaviour ("acts like") — a different mechanism, not a missing one
`$00BF36`, `$00BF81`, `$00C117`, `$019501`, `$0292FA`, `$0295ED`, `$02A6BB`, `$02BA72`,
`$02D18D` all change one read: `LDA $00BA70,X` → `LDA $000CC6,X`. LM relocates the block-type
table into RAM; we instead repoint the four `JSL $00F545` acts-like sites at our own remap
(`$06F5F0` → table at `$118000`). CONFIRMED different, INFERRED compatible. **This is the
likeliest place for the two to disagree about the same tile**, because both are live at once
on a ROM that has met both editors. Worth an explicit experiment.

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

Prep v10 stamps the blob at `$13BC00` with its tables at `$13B400`, and both hooks; `Rom.LmMidwayTable`
follows the `$05D9E3` operand into whichever ROM's copy, so ShaoBase and juz read the same way.
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

### 4bpp graphics — the overworld half is LM's now (prep v13); the rest is inventoried  [CONFIRMED 2026-08-29, ShaoBase + BigEye + DogsOfWar vs vanilla and exanim_1]

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
- Still ours, and still divergent from LM's 4bpp mode (none of it broke the game; all of it
  matters to LM reading our ROM): the `$00AA80` file dispatch keeps vanilla's filter path
  (LM: `CPY #$08/#$1E → #$32` and the filter baked into its files — GFX1E = plane 3 OR'd, GFX08
  a hand-made per-tile compromise that recolours nothing in tilesets 04/0A); GFX33 stays 3bpp
  with vanilla's `$00B888` expander (LM converts it, rewrites `$00B895/B89F`, and reads our
  3bpp GFX33 as 4bpp — the likely source of "garbled in LM" for anything drawn from AN1); the
  GFX0F/00 RAM expanders are our v4 rewrites (LM: `$00A830 → $0EFC00`, `BRA` at `$00A873`); the
  upload itself and the ExGFX/bypass loader are ours (LM: `$00AA50 → $0FF780`, `$00AA6C →
  $0FF160`, records at `$12AD08`, pointers at `$0FF200`). Taking those is a rework of the
  loader and the project's GFX layout, not a stamp.

## 3. What we write that LM does not

Direct Map16 object handlers (`$0DF150`, `$0DF08A` extent), the exit destination bit 8 before
LM's own patch exists on a vanilla base, the checksum balance at pc `0x80000`, our acts-like
remap at `$06F5F0` + table at `$118000`, and the v2 GFX loader. All RATS-tagged where they are
data; LM honours tags it did not write.

## 4. Where to start

0. **4bpp graphics** (§2 above) — the overworld half is done (v13); what remains is LM reading
   our GFX33 as 4bpp, the filter-path dispatch, and the loader itself.
1. **Acts-like.** Two live mechanisms for one feature, and it decides collision. Build the
   experiment: a tile whose behaviour we set, opened and saved in LM, read back here.
2. ~~Sprite extra bytes~~ — done: size table read at LM's registration and authored the same way.
3. **The Map16 ladder's remaining ranges**, so a ROM that has met LM does not address tiles we
   cannot.
4. Everything in bank 03 and the level-load pipeline is unmapped; treat as research, not work.
