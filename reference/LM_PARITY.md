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
| `$00AACD` 4bpp upload | LM's byte sequence | — | **CONFIRMED identical** — byte-compared against ShaoBase |
| `$0DF08A`, `$0DFF50`, `$0D*FD` handlers | DM16 object handlers | LM's own, larger block | INFERRED overlap; both dispatch the same object numbers, internals never compared |
| `$0DA4B8` DM16 hijack | ours | LM's | INFERRED same purpose |
| `$0EF100` sprite bank table | 535 B | 1132 B | LM's table is BIGGER — ours may under-declare |
| `$00F4DE` acts-like call site | repoint one JSL | rewrites 43 B at `$00F478` | **different mechanisms for one feature** — see §2 |
| `$0DE1B0` ext-object handler | ours | LM writes from `$0DE190` (112 B) | INFERRED same table, different extent |

## 2. LM writes, we do not — the inventory

### Block behaviour ("acts like") — a different mechanism, not a missing one
`$00BF36`, `$00BF81`, `$00C117`, `$019501`, `$0292FA`, `$0295ED`, `$02A6BB`, `$02BA72`,
`$02D18D` all change one read: `LDA $00BA70,X` → `LDA $000CC6,X`. LM relocates the block-type
table into RAM; we instead repoint the four `JSL $00F545` acts-like sites at our own remap
(`$06F5F0` → table at `$118000`). CONFIRMED different, INFERRED compatible. **This is the
likeliest place for the two to disagree about the same tile**, because both are live at once
on a ROM that has met both editors. Worth an explicit experiment.

### Sprite stream — per-sprite extra bytes (PIXI)
`$02A67A`, `$02A826` (→ `JML $1090A3` / `$109198` / `$10917B` / `$108F2D`), `$02A95B`
(→ `JML $108F70`). CONFIRMED: this is the sprite-advance hijack CONTRACT §11 documents. We
READ it (`Rom.LmSpriteSizeBase`) and honour entry sizes, but we never install it — a base
prepped here supports vanilla 3-byte entries only. Feature it powers: custom sprites with
extra bytes. **Needed before we can author PIXI-style sprites.**

### Sprite bank relocation — same feature, different hook
`$05D8E2` (→ `JSL $0EF550`, `JSL $0EF300`). We hook `$05D8F5` at the same target `$0EF300`.
CONFIRMED both exist; the sites differ by 19 bytes. Low risk, but two hooks into one path is
exactly the shape of a latent conflict.

### Map16 machinery — LM's is much larger
`$06F540` (260 B), `$06F65C`, `$06F690` (218 B), `$06F780` (80 B), `$06FA00` (1536 B, `$20`
fill). Ours is `$06F538` (73 B) + `$06F5D0` + `$06F5F0`. INFERRED: LM covers all eight ladder
ranges plus per-tileset page tables; prep v3 covers four. Feature: extended Map16 pages beyond
`$3FFF`, per-tileset tables. We already read the ladder slots LM uses (§7a-rev).

### Level load and GFX staging
`$0580C0` (→ `JSL $1FA69A`, `$1FA41C`), `$058DA4` (→ `JSL $0EFD00`), `$0586A1`, `$00A6B8`
(→ `JSL $0EF560`, `$05DD00`), plus LM's routines at `$05DCB0`, `$05DD00`, `$05DD30`,
`$0EFD00`. INFERRED: LM's level-load pipeline (its own GFX/ExAnimation staging). Ours is the
v2 loader at `$0FF770`/`$0FF780`. Two independent loaders on one ROM is a real hazard if both
ever install.

### Entrance positions — "method 2" and the separate midway  [CONFIRMED from four hacks]

The two limits our Entrances mode enforces are **vanilla's**, not the medium's, and LM's help
says so outright (`level_main_entrance.htm`): the bank-05 tables are "method 1", and *"Method 2
does not use table-based coordinates, and is an enhancement inserted by Lunar Magic"*, which is
*"practically required for reaching most areas in horizontal levels that are taller than the
original game"*. Separately, *"In the original game the two are tied together... However Lunar
Magic adds an option to use separate settings for the midway entrance"*.

The hook is one instruction, and every real hack here has it — the reference bases do not:

| ROM | `$05D979` |
|---|---|
| vanilla, prepped, `after.smc` | `29 38 4A 4A` — vanilla `AND #$38 : LSR : LSR` |
| ShaoBase, BigEye | `JSL $10FE7F` |
| juz | `JSL $11FB03` |
| DogsOfWar | `JSL $12EFC0` |

Read out of juz, whose copy sits at `$11FB03`:

- **Gate**: `BIT $192A : BVC vanilla` — **bit 6 of `$192A`**, which is the very bit an LM
  extended EXIT sets from its flags bit 3 (CONTRACT §9d-2, and prep v7 already writes it). The
  exit says "this arrival is an extended one" and the entrance decode answers.
- **Tables**, per level, RATS-allocated so the addresses differ per ROM (juz's shown):
  `$138008+lvl` flags — bit 5 = use method 2, bit 3 = X high bit, bits 0-2/6-7 → `$192A`;
  `$138208+lvl` position — high nibble = Y low, low nibble << 4 = X low, i.e. **16px granularity
  in both axes**; `$138608+lvl` Y high, 6 bits, so Y spans the whole level; `$138408+lvl` FG/BG
  scroll; plus `$06FC00+lvl` and `$06FE00+lvl`.
- The SCREEN still comes from the vanilla field: LM's routine writes `$01` and re-enters the
  decode at `$05D9A1`, so the shared tail's `LDA $01 : AND #$1F : STA $95` still runs.

**Prep v10 does the same job our own way** (CONTRACT §9d-3): stubs on the two `JMP $05DA17`
sites, a per-level table, exact pixel positions and an independent midway. It deliberately does
NOT match LM's layout — LM's tables are RATS-allocated at per-ROM addresses baked into code it
generates, so there is nothing fixed to agree with. Consequence, stated rather than hidden: a
ROM re-saved by LM keeps working and free positions revert to the grid. Matching LM would mean
decoding how it FINDS its tables (as `LmMap16Slot` does for the Map16 ladder) and emitting the
same operand positions; that is the upgrade path if the round trip ever has to carry them. Note `$138008` collides with the
address our prep pins for the ExGFX pointer table — LM allocates dynamically, we do not, so
whichever lands first must be RATS-respected by the other.

### Object handlers with `$13D7`
`$0DA963`, `$0DA9D6` — both do arithmetic against `$13D7`, the level's screen count.
INFERRED: LM's variable level width. We have no equivalent and no feature that wants one yet.

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

## 3. What we write that LM does not

Direct Map16 object handlers (`$0DF150`, `$0DF08A` extent), the exit destination bit 8 before
LM's own patch exists on a vanilla base, the checksum balance at pc `0x80000`, our acts-like
remap at `$06F5F0` + table at `$118000`, and the v2 GFX loader. All RATS-tagged where they are
data; LM honours tags it did not write.

## 4. Where to start

1. **Acts-like.** Two live mechanisms for one feature, and it decides collision. Build the
   experiment: a tile whose behaviour we set, opened and saved in LM, read back here.
2. **Sprite extra bytes.** The one gap that blocks a feature users will ask for by name.
3. **The Map16 ladder's remaining ranges**, so a ROM that has met LM does not address tiles we
   cannot.
4. Everything in bank 03 and the level-load pipeline is unmapped; treat as research, not work.
