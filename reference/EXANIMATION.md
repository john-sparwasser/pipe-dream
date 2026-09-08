# ExAnimation — how Lunar Magic's system works, and the editor we want on top of it

Research notes, 2026-08-29. Sources: LM help (`lm-help/html/level_ex20.htm`,
`level_super_bypass.htm`, `info_gfx_format.htm`), CONTRACT §12d-f (our decode + engine
emulation), the exanim_0..4 controlled-diff ROMs, ShaoBase/DogsOfWar/BigEye.

## 1. The model in one paragraph

An ExAnimation **slot** is a small program the game runs during vblank: every N frames,
DMA *this many bytes* from *this source* into *this VRAM (or CGRAM) destination*, cycling
through a list of **frames** (one source address per frame). There are **0x20 per-level
slots** (stored per level) and **0x20 global slots** (one list, runs in every level). The
Map16 tiles the user places never change — only the 8x8 graphics under them are rewritten.
Everything the LM dialog asks for (type, trigger, frames, destination, source tile numbers)
is a field of that program. The pain is that LM makes the user compute the operands.

## 2. Sources — where frame graphics come from

| LM tile range | Storage | What it is | How it gets there |
|---|---|---|---|
| `0x600-0x77F` | RAM `$7E:7D00` (AN1) | vanilla animated tiles, GFX33 | always loaded |
| `0x780-0x857` | RAM `$7E:AD00` (AN2) | **extended animated area**, 0xD8 tiles 4bpp | Super GFX Bypass slot **AN2** (record w0, `LunarMagic.GfxBypass`) — a normal compressed GFX/ExGFX file, ≤ 0x1A00 bytes (0xD0 tiles) |
| `0x900-0xBE7` | RAM `$7E:2000` | Mario's GFX32 (berry frames live here) | always loaded |
| `0xC00-0xFFF` | **ROM**, uncompressed | ExGFX **60** | "Use alternate ExGFX file for source" |
| `0x1000-0x13FF` | ROM | ExGFX **61** | 〃 |
| `0x1400-0x17FF` | ROM | ExGFX **62** | 〃 |
| `0x1800-0x1BFF` | ROM | ExGFX **63** | 〃 |

Files 60-63 are the answer to "4 bins globally": they are up to **32KB each (0x400 4bpp
tiles)**, stored **uncompressed** in a RATS block, and the engine DMAs frames **straight
from ROM** (CONTRACT §12f emulation: ShaoBase sources `$20:9Exx`). Constraints from LM:

- The alt-file flag is **per slot**, but the **file choice is one per level** (per-level
  list) plus **one for the global list**. So "4 bins" is really: at most one of 60-63 per
  level list, one for the global list, four files total to choose from.
- RAM sources (AN1/AN2/Mario) and the ROM alt file can be mixed across slots in one list —
  the flag is per slot (dest word bit 15). All 13 of ShaoBase's global slots happen to use
  file 60; the earlier reading of slot 6 as RAM `0x680/0x700/0x780` was a misparse — those
  are file offsets `0x600..0x780`.
- Within one frame the N tiles of a slot must be **consecutive in the source** — the type
  says how many (`N 8x8s: line`), the frame word says where the run starts.

**ROM side (CONFIRMED this session):** the four file pointers are a fixed 4×3-byte table at
**`$03BCC0`** (60,61,62,63; `FF FF FF` = absent; vanilla all FF; LM zeroes it when it installs
the ExAnimation ASM). ShaoBase: `$03BCC0 = E4 9E 20` → file 60 data at `$20:9EE4`, preceded by
its `STAR` tag; the data is the ExGraphics/ExGFX60.bin bytes verbatim (0x1000 of them).
A list picks its file through the **high byte of its record's first word** (index 0-3 into
that table → `$7FC013/14`); ShaoBase's global record starts `19 00` = 25 slots, file 60.

## 3. Destinations

VRAM 8x8 tiles `0x000-0x2FF` (layer 1/2 graphics — the FG/BG bins), `0x400-0x5FF` (sprite
graphics), `0x1C00-0x1DFF` (layer 3), or a palette colour index (`0x00-0xFF`) for palette
types; advanced: raw VRAM word. The user then builds Map16 tiles out of the destination 8x8s
exactly as with any other tile — that is the only link between the animation and the level.

Encoding (`ExAnimation.WordToLmTile`/`LmTileToWord`): the record's dest word is the raw VRAM
word for every range — layer 1/2 tile × $10 (BG12NBA=0), sprite $6000 + (tile − $400) × $10
(OBSEL=3), layer 3 $4000 + (tile − $1C00) × 8 (BG34NBA=4, 2bpp tile = 8 words). Vanilla's CHR
bases, which LM keeps; the engine passes the word to VMADD unchanged (oracle-verified). All
three ranges stay below $8000, so bit 15 remains free for the alt-file flag.

## 4. Slot types (what one slot can move per frame)

- `1..0x20 8x8s: line` — N consecutive tiles to N consecutive destination tiles. The
  workhorse; `4 8x8s: line` + a Map16 tile pointing at the 4 dest tiles = an animated 16x16.
- `2 8x8s: stacked`, `4 8x8s: 16x16`, `8 8x8s: 32x16` — same, destination arranged as a
  block (top row then bottom row). Source still a line. Slightly more CPU than `line`.
- `1 8x8 2bpp` — layer 3.
- Palette family: `Palette` (N colours from frame data), `+Working`, `+Working stop on fade`,
  `Back area colour`, `Rotate right/left` (± reverse on trigger). Frames = colours or a
  file offset to a colour run; "Colors" = count.

## 5. Triggers

`None` (continuous) · `POW` · `Silver POW` · `ON/OFF` · `Have Star` · `Timer<100` (± one
shot) · `≥5 Yoshi coins` (± one shot) · `Precision timer palette rotate` · `Manual 0-F`
(frame byte at `$7FC070+n`, set by custom blocks) · `Custom 0-F` (bit flags) · `One shot
0-F`. Triggered types double the frame list: first half untriggered, second half triggered.
Per-level "Trigger Init" sets manual/custom initial states on level load.

## 6. Timing and budget (this shapes the UI)

- Base rate 7.5 fps: the game spreads animation over 8 consecutive frames. Slot *k* runs on
  game frame *k mod 8*; slots `k, k+8, k+16, k+24` run on the **same** frame (and the global
  list's on the same frames as the level's). So per game frame: 4 level + 4 global slots.
- Faster animation = the same destination in slots 4 apart (15 fps: 0 and 4; 30 fps:
  0,2,4,6; 60 fps: 0-7).
- Vblank is the hard limit: total DMA bytes per game frame across the ≤ 8 slots that run
  then. LM's own test: 0x40 slots of `4 8x8s: line` fits with a couple of scanlines to spare
  on a layer-2-image level. One 4bpp tile = 0x20 bytes.
- Frame values readable at `$7FC080 + slot` (level) / `+0x20` (global) for custom blocks.

## 7. What the repo already has

| | State |
|---|---|
| Records, both lists (`ExAnimation.ParseSlots`, `ReadLevel`, `ReadGlobal`) | **decoded** (CONTRACT §12e "SLOT FORMAT PINNED"): header count/alt-file index/masks/selector, offset table, per slot type · trigger · frames−1 · dest word (bit 15 = alt file) · frame words (×2 for stateful triggers). Pinned by exanim_a..n; ShaoBase's 13 global slots decode and agree with the emulated timeline |
| Global timing | still via emulation (`ResolveGlobal`/`GlobalStates`) — the per-slot rate/phase lives in the engine's slot-number scheduling, not in the record |
| Rendering | both lists overlay onto FgTiles per display phase (approximate 8 frames/phase) |
| Alt file 60-63 | pointer table `$03BCC0`; `Rom.LmAltExGfx(i)` / `Rom.SetLmAltExGfx(i, bytes)` (RATS block, old one released); per-level overlay renders alt-file slots from ROM |
| Engine | **prep v11** (`LmExAnimEngine`, `RomPrep.AppendV11Stamps`): LM's engine from exanim_1 (`$108640`, 0xC30) relocated to `$1E:9400`, helpers `$1E:A040/A068`, empty per-level table `$1E:A0A0`, 7 hooks, `$03BCC0` zeroed. `RomPrepTests.v11_exanimation_engine_runs_a_written_slot` emulates it on a written slot |
| Writers | `Rom.WriteLevelExAnim` / `Rom.WriteGlobalExAnim` (release old RATS, allocate `ExAnimation.Encode`, repoint table / patch the setup immediates) |
| Project / build | `ProjectFile.ExAnimation` (record hex per level + global), files 60-63 under `Gfx["060".."063"]`; `RomBuilder.ReplayExAnimation` shared by build and session hydrate; `ExAnimationFlowTests` covers session → project → build → reopen |
| UI | Animations mode = the timeline, no dialogs: **Add slot** creates a working slot at once (next free number, 1 8x8, one frame of tile 600); the open slot is edited inline — type / trigger / destination (or first colour + count) rewrite the record on change; the frame strip: click a frame → `TilePickerWindow` (the AN1 / AN2 / Mario / alt-file sheet, click a run of N tiles), × removes, + appends (both halves of a doubled list); animated preview at 7.5 fps. `ExAnimSlotWindow` remains only as the field editor / type-trigger tables. Header: Level/Global toggle, source file, preview palette row |
| Graphics drawer | "Animation slots" section after the ten bins: AN1 (GFX33, bypass w1), AN2 (bypass w0), E60-E63 — files 60-63 open in the pixel editor (created blank on first open, Load imports a .bin, Save writes the RATS block). `Gfx.SourceSnes` now resolves GFX32/GFX33 (the fixed-operand blobs; LM numbering: 32 = Mario, 33 = AN1) |

## 8. The editor we want

The user's framing: *timeline of frames × groups of tiles → batches that animate together →
pick a destination.* That maps onto LM's slots cleanly if the editor owns the source file:

1. **The source IS the run.** (DONE.) A frame of an N-tile slot is picked on any source sheet
   as a run of N *consecutive* tiles — the line the engine DMAs — so a 16x16 frame is drawn
   as four tiles in a row (TL TR BL BR), exactly the layout Lunar Magic asks for; the frame
   word names the run directly and no bytes are copied. An earlier packer that copied a
   16x16 *square* into a fresh run at the end of file 60 on every pick was dropped: every
   pick (and every mispick) grew the file by 0x80 bytes and left orphaned runs behind.
   Files 60-63 are ours (uncompressed, 0x400 tiles, DMA'd from ROM) and are edited in the
   Graphics editor like any sheet; AN2 stays as the small/compressed option for RAM sources
   when the user already has an AN2 file.
2. **A batch = one slot.** Its tile group size N (1..0x20) and shape (line / stacked / 16x16
   / 32x16) is the type; its frame count is the number of timeline columns; its trigger is
   a dropdown; its destination is picked by clicking a VRAM tile in the 8x8 view (the
   Animations drawer's job: show the FG/BG/SP bins with destinations highlighted and the
   Map16 tiles that use them).
3. **Timeline** rows = batches, columns = frames, cells = the packed tile group thumbnail
   for that frame; scrub head previews the composite in the drawer's VRAM view. Speed is
   chosen as 7.5/15/30 fps and realised by slot placement (§6), so the UI never exposes slot
   numbers — it assigns them, keeping same-frame groups for things the user wants in sync.
4. **Budget meter** per game frame (DMA bytes over the ≤ 8 slots that share it), so the
   vblank ceiling is visible instead of showing up as flicker on hardware.
5. **Global vs level** is a tab on the same timeline; the global list's file choice is one
   of 60-63; the level's is another (or the same file — one file can serve both if packed
   with distinct regions).

## 9. What has to be decoded/built before the write side exists (in order)

1. **Per-slot record format** — DONE (CONTRACT §12e "SLOT FORMAT PINNED", saves exanim_a..t):
   types, triggers, list doubling, palette colour count, alt-file flag, slot placement, and
   the line-code → tile-count table read off LM's own engine (`--exanimtypes`). Only the
   palette variants 14-17/19-1B and triggers 05-0F rest on LM's list order rather than a save.
2. **File 60-63 writer** — DONE (`Rom.SetLmAltExGfx`).
3. **Engine + record writers** — DONE: prep v11 transplants LM's engine (`LmExAnimEngine`);
   `WriteLevelExAnim` / `WriteGlobalExAnim` write the records; the emulated engine resolves a
   written slot (`RomPrepTests.v11_exanimation_engine_runs_a_written_slot`).
4. **Project model + build** — DONE (`ProjectFile.ExAnimation`, `RomBuilder.ReplayExAnimation`,
   `ExAnimationFlowTests`). First UI is the slot form; the timeline of §8 is the next layer.
5. **Timing fidelity in the preview**: `GlobalStates` approximates 8 frames/phase; the
   timeline preview wants the real per-slot period, which (1) will give us.

Nothing above changes the ROM contract: LM opens the result and shows the same slots in its
own dialog, and a hack saved in LM round-trips into the timeline.

## 10. The overworld's animations — Lunar Magic's own hack, not ours yet

Research 2026-09-08, prompted by adding the Animations mode's **Overworld** tab (a submap
picker beside Global and Level). The tab is in, and it refuses to write, with the reason on
screen. This section is why, and what would unblock it.

**What Lunar Magic offers.** `ov_overworld_ex20.htm` — *Overworld ▸ Edit Submap ExAnimated
Frames* — is the level dialog plus a submap field: "Each submap can have its own set of
animations, just like individual levels." There is a second dialog for an overworld GLOBAL
list (`ov_overworld_ex20_global.htm`), separate from the level editor's global one, and an
*Edit Animation Settings* dialog per submap that can switch off the game's own animated
tiles, the level-dot and lightning palette cycles, LM's global list and LM's submap list.
Documented differences from a level's: triggers 8-F become **Event Manual 8-F** (activate on
an overworld event number, the event taken from the Manual frame of the same number, up to 8
per submap); the four *Stop on Fade* triggers are marked **[Do Not Use]**; the palette
*Back Area Colour* type must not be used (the overworld uses it for menus and the exit-tile
transition); `$7FC004` is not available; and the source **tile numbers are shifted −0x100**
against the level dialog's (OW AN1 `0x500`, Mario `0x800`, alt files `0x700/0xB00/0xF00/0x1300`).
The RAM map is the same map: `$7FC070` manual frames, `$7FC080` "frame counters for **submap**
slots 0-1F" where the level page says "level slots", `$7FC0A0` global, `$7FC0F8`/`$7FC0FC`
triggers.

**Why our engine cannot hold one.** The transplanted engine (`LmExAnimEngine`, LM's own bytes)
fetches a record from the per-level table in exactly one place, engine `+0xD3`:

```
+0xD3  LDA $FE        ; level number + 1, left there by LM's level-load hijack
+0xD5  BEQ  …         ; 0 = this level has no list
+0xD7  DEC : ASL : CLC : ADC $FE : DEC : TAX      ; X = 3 * ([$FE] - 1)
+0xDE  LDA.L table+1,X                            ; the two operands the transplant re-points
```

so **the index is `[$FE] - 1` and nothing else**. A byte scan of the engine finds no read of
`$1F11` (submap), `$0100` (game mode), `$010B` or `$13BF`: there is no submap path in it. Nor
does the setup ever run on the overworld — it is hooked into vanilla's `LoadLevel` (`$0583AD`,
`bank_05.asm`), which is reached only from `JSR LoadLevel` at `$05808C` in the LEVEL loader;
the overworld loader enters bank 05 at `$05809E` and returns without calling it. Nothing on
the overworld's load path writes `$FE`. (The per-tick processor at engine `+0x4B0` is hooked at
three sites, none of them overworld code: `$0095B6` is the ending's Yoshi's House, `$00A2A6`
the in-level main loop, `$00A5FE` the level-entry priming loop. It reads no game mode, so on
the overworld it would keep ticking whatever the last level left in `$7FC0xx`.)

**What LM actually did.** `changes.htm`, LM 2.40: "added a new ASM hack to implement
ExAnimation for the overworld, which is based on the same system as the one for levels." A
separate hack, with its own hooks, its own record table and its own global list — consistent
with the second global dialog, the different trigger handlers (the handler table lives inside
the blob at `+0xB4A..+0xC22`, so the overworld needs its own blob), `$7FC004` being unavailable
there, and the overworld's animated tiles not going through `$00A390` ("Nintendo simply
transferred all of them every single frame").

**The probe that settles it**, in order — the same method that produced `LmExAnimEngine`, and
it has to run on the Windows box, since Lunar Magic is Windows-only
(`tools/lm/Invoke-LunarMagic.ps1`):

1. In LM ≥ 2.40, open a prepped base, add ONE submap ExAnimation slot, save; repeat into a
   second copy with the slot on a DIFFERENT submap, and a third with an overworld global slot.
   Diff each against the pre-install ROM: the new RATS blocks are the blob and the table, and
   the blob's equivalent of `+0xD3` gives the table operand and the index arithmetic directly.
   Two ROMs differing only in which submap holds the slot pin the index rule on their own.
2. Diff `bank_00`/`bank_04`/`bank_05` for the hooks the hack installs — especially
   `$00A087..$00A18F` (game mode 0C, the overworld load), `$00A1BE..` (mode 0E), `$04DC09`, and
   the overworld's animated-tile DMA in NMI. That gives the setup site, and so what holds the
   submap number at entry (expect `$1F11,X` with `X = $0DB3`, `bank_00.asm` `$00A12A`).
3. Only if 1-2 leave it ambiguous: a Mesen probe breaking on reads of the discovered table and
   of `$1F11`, walking onto each submap and logging the index.

`dev/base.smc` cannot answer any of this: it is LM-prepped but carries **our** transplanted
level engine, not LM's overworld hack.

**What is in the editor now.** The Animations mode's Overworld tab, with the seven submaps in a
picker, and `EditorSession.ExAnimSubmap` / `ExAnimListIndex` pointing the level path at a
submap's index — so when `ExAnimation.SubmapListIndex` can return a real index, reading,
writing, the project and the build all work through the level path unchanged (a submap's record
is a record at a table index, and `ExAnimation.Levels` is keyed by that index). Until then the
index is -1, the tab shows `ExAnimation.NoSubmapLists` where the timeline would be, and a write
is refused with the same words.

