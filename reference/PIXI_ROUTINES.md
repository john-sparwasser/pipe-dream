# PIXI shared routines: interface contracts

These are clean-room interface contracts for the 27 shared sprite routines that ship with
PIXI 1.2.x (`routines/*.asm`, each one inserted once and called with `JSL` through `%Name()`).
I wrote them from the routines' observable behaviour. Each contract says what a caller can see:
inputs, outputs, RAM written, flags, stack effects and edge cases. The file holds **no PIXI code**:
no instruction sequences, no paraphrase of PIXI's code instruction by instruction, no PIXI comments,
and no PIXI data tables. A table is described only when it is SMW's own (its address is cited) or
when it can be defined by a formula.

- Date: 2026-10-20
- Source read: `D:\SMW\Projects\BigEye\Sprites\routines\*.asm` (the same files are in every project's `routines` folder)
- SMW addresses were checked against `D:\SMW\Projects\.resources\SMW.smc` with the PipeDream disassembler.

## Conventions that apply to every routine

- **Build.** The contracts describe the LoROM, non-SA-1 build. RAM is named by its SMW address:
  sprite tables are `$E4,x`, `$14E0,x`, etc.; "slot" means a normal-sprite slot `0..$0B`. The SA-1
  build moves the sprite tables (`!addr` = `$6000` and so on), uses the SA-1 arithmetic unit instead
  of `$4202`/`$211B`, and uses `$40/$41` instead of `$7E/$7F` for Map16. These differences are noted
  briefly where they apply.
- **Returning.** Every routine is entered with `JSL` and returns with `RTL`, unless "Stack" says otherwise.
- **Direct page.** D = `$0000` is assumed: `$00-$FF` is RAM `$7E:0000-00FF`.
- **Data bank.** "DB must map WRAM" means DB must be a bank where `$0000-$1FFF` mirrors low WRAM
  (`$00-$3F` or `$80-$BF`). Every routine that reads a sprite table with absolute addressing needs this.
- **Widths.** "8/8" means the routine expects M=1 (8-bit A) and X=1 (8-bit X/Y) on entry. Most of
  them do. When a routine runs with the wrong widths, its immediate operands shift and the result is
  garbage, so treat 8/8 as a hard precondition unless the contract says otherwise.
- **B.** The high byte of the accumulator, which `XBA` exposes. It is listed only where it is
  deterministic and might be relied on. Otherwise it is a by-product.
- **Flags.** Any flag not listed under **Returns** is a by-product. V is a by-product everywhere.
  A differential test should mask by-product flags. Flags that are listed are exact.
- **EXLEVEL.** PIXI decides at assembly time whether Lunar Magic's tall-level support is present
  (`!EXLEVEL`). It reads the Lunar Magic version string at ROM `$0FF0B4`; versions above 2.53 set
  it. Every ROM saved by Lunar Magic 3.x takes the EXLEVEL branch, so that branch is the one that
  matters for current ROMs. `SubOffScreen`, `GetMap16` and `ChangeMap16` behave differently
  depending on this flag.
- **SMW routines called.** These belong to the ROM, not to PIXI. A reimplementation may call the
  same addresses:
  - `$01ACF9`: random number generator.
  - `$02A9DE`: find a free slot, keeping 2 slots in reserve.
  - `$03B664`: player clipping, box B.
  - `$03B69F`: sprite clipping, box A.
  - `$03B72B`: box-vs-box contact test; returns C.
  - `$07F7D2`: initialise a slot's tables.
  - `$0187A7`: give a slot its custom tables. This address is PIXI's driver hook, not vanilla SMW code.
  - `$02ACE5`: give points.
  - `$01AB6F`: contact sound and contact graphic.
  - `$02A469`: extended sprite hurts the player.
  - `$02B554`/`$02B560`: extended sprite X/Y speed. These end in `RTS`.
  - `$00C0FB`: queue a Map16 tile to VRAM. Ends in `RTS`.

  PIXI reaches the `RTS`-ending routines (`$02B554`/`$02B560`, `$00C0FB`) from another bank with
  the usual "push a long return address, then push the address of an `RTL` in the target bank
  minus 1" trick. The `RTL` bytes it uses are at `$00804D` and `$02B889`. Any equivalent call
  mechanism is acceptable.
- **Box layout used by SMW's contact test `$03B72B`.** Box A is `$04` x-low, `$0A` x-high, `$05`
  y-low, `$0B` y-high, `$06` width, `$07` height. Box B is `$00`, `$08`, `$01`, `$09`, `$02`, `$03`
  in the same order. `$03B72B` preserves X and Y, clobbers `$0C` and `$0F`, and returns C=1 on contact.

---

## Aiming

Computes an 8-bit X speed and Y speed of magnitude about `speed`, pointing from the shooter
toward the target.

- **Called with:**
  - Any register widths. The routine saves P and sets 8/8 itself.
  - A (low byte) = speed `s`, unsigned.
  - `$00-$01` = 16-bit `dx` = shooter X − target X.
  - `$02-$03` = 16-bit `dy` = shooter Y − target Y.
  - DB is irrelevant.
- **Returns:**
  - `$00` = X speed (8-bit). `$02` = Y speed (8-bit). `$01` = `$03` = 0.
  - Each output is the value computed below, not negated when its input `d` had bit 15 set (target
    to the right / below), and two's-complement negated when bit 15 was clear. `d` = 0 gives a negated 0, which is 0.
  - P is restored exactly as on entry, so widths and C/V/D/I are the entry values, except that N
    and Z reflect the restored X.
  - X is preserved. Y is untouched.
  - A low byte = the final `$00`. B = high byte of the 16-bit `Vx` below.
- **Scratch RAM:** `$04-$05` = `S` (16-bit, defined below). `$06` = 1 if `dx` was negative,
  otherwise 0. `$07` = 0. `$0F` = `s`. The multiplier registers `$4202/$4203/$4216/$4217` are left
  holding the last product.
- **Algorithm:**
  1. Take absolute values: two's-complement negate `dx` or `dy` if bit 15 is set. `$8000` stays
     `$8000` and is then treated as unsigned.
  2. While `(|dx| OR |dy|) ≥ $100`, shift both right by 1. The shift count is the bit-length of the
     high byte of the OR. After this, `ax, ay ≤ $FF`.
  3. `d = ax² + ay²`, a 17-bit unsigned value.
  4. Choose `sh` as the smallest value in 0..5 with `d >> (2·sh) < $100`. The six bands are
     `d < $100`, `< $400`, `< $1000`, `< $4000`, `< $10000`, and `≥ $10000`. Then
     `k = d >> (2·sh)` and `m = R[k] >> sh` (16-bit logical shift).
  5. `S = floor(s · m / 256)`. This is exact and always fits in 16 bits.
  6. `Vy = floor(ay · S / 256)` and `Vx = floor(ax · S / 256)`, both 16-bit. The outputs are their
     low bytes, sign-applied as above.
- **Tables/data:** `R` is a 256-entry table of 16-bit words: `R[0] = $FFFF`, and
  `R[k] = min($FFFF, round(65536 / √k))` for k ≥ 1. All 256 entries were checked against this
  formula. SMW has no such table. `m` approximates `65536/√d`, so `S·a/256 ≈ s·a/√d`.
- **Stack / control flow:** normal.
- **Equivalent SMW code:** none. (SMW's aimed projectiles use other methods.)
- **Calls:** none. Uses the hardware multiplier at `$4202/$4203` (SA-1: `$2250-$2254/$2306-$2307`).
  Allow 8 cycles before each product read.
- **Edge cases:**
  - `dx = dy = 0` gives 0, 0.
  - `d = 1` uses `R[1] = $FFFF`.
  - The magnitude can slightly exceed `s` because of rounding. Only the low byte is kept, so `s`
    close to `$FF` can wrap.
  - The SA-1 build computes `m·s` through the SA-1 multiplier with a different path for `m ≥ $8000`.
    Its results are the same in intent but not guaranteed to be bit-identical, and this contract does
    not cover them.

## CapeContact

Checks whether sprite X is touching the spinning cape.

- **Called with:**
  - 8/8, X = sprite slot.
  - DB must map WRAM.
- **Returns:**
  - C=1 for contact, C=0 for none. X and Y are preserved.
- **RAM written / scratch:** when the box test runs: `$00-$0B` (both boxes), plus `$0C` and `$0F`
  (from `$03B72B`). The early "no" exits write nothing.
- **Behaviour:** returns C=0 without touching RAM if any of these holds:
  - `$13E8` (cape-spin active) = 0.
  - `$15D0,x | $154C,x | $1FE2,x` ≠ 0.
  - Layer mismatch: let `L = $1632,x`, with bit 0 flipped when `$74` (climbing) ≠ 0. Exit if `L` ≠ `$13F9`, comparing the whole byte.

  Otherwise:
  1. Box A comes from SMW's `$03B69F`.
  2. Box B is the cape box:
     - X = 16-bit (`$13E9`/`$13EA`) − 2, giving `$00`/`$08`. Width `$14` in `$02`.
     - Y = `$13EB`/`$13EC`, giving `$01`/`$09`. Height `$10` in `$03`.
  3. The result is `$03B72B`'s.
- **Stack / control flow:** the final step is a tail jump into `$03B72B`, so `$03B72B`'s `RTL` returns to the caller.
- **Equivalent SMW code:** the per-sprite body of SMW's cape-vs-sprite check, `$0293CE-$0293F2`.
  The cape box is SMW's `$029696` exactly. Differences:
  - PIXI does not test `$14C8,x ≥ 8` or `$166E,x` bit 5. SMW's surrounding loop does.
  - PIXI has no net-punch alternative box (`$029663`, which SMW chooses when `$0E` ≠ 0).
  - PIXI does not call SMW's hit handler (`$029404`).
- **Calls:** `$03B69F`, `$03B72B`.

## ChangeMap16

Writes a Map16 tile number at a level position and queues the VRAM update.

- **Called with:**
  - Any X width. The accumulator holds the tile number as a full 16-bit value. Callers normally
    set M=0 first; with M=1, `B:A` is taken as the 16-bit value.
  - `$98-$99` = block Y (16-bit). `$9A-$9B` = block X (16-bit).
  - `$1933` = layer: 0 = layer 1, 1 = layer 2. Other values index garbage.
  - DB must map WRAM. `$00C0FB` reads `$0FBE,y` and `$1933` absolute.
- **Returns:**
  - C=0 when the tile was written. C=1 when the position was out of range and nothing was written.
  - P is otherwise restored to its entry value, except that N and Z reflect X.
  - X and Y are preserved (restored at their entry widths). DB is preserved.
- **Address computation:** steps 1–3 below are identical to GetMap16's, including every scratch
  write and the swap of `$99`/`$9B` on vertical layers.
- **Range check:** the same as GetMap16's.
- **Write:**
  - `$7E:addr` = tile low byte.
  - `$7F:addr` = `(tile >> 8) & $7F`. Bit 15 of the tile is dropped.
- **VRAM queue:** after the write, the routine calls SMW's `$00C0FB` with:
  - M=0, X=0.
  - Y = `(tile × 2) & $FFFF`, its index into the tile-pointer table at `$0FBE`.
  - X = the Map16 address.
  - `$06/$07` = the VRAM tilemap address (step 3 of GetMap16).
  - `$0C-$0F` = the original block X/Y.
  - `$08/$0A` = camera values chosen by layer and orientation:

    | layer orientation | `$08` | `$0A` |
    |---|---|---|
    | horizontal L1 | `$1A` − `$80` | `$1C` |
    | horizontal L2 | `$1E` | `$20` − `$80` |
    | vertical L1 | `$1A` | `$1C` − `$80` |
    | vertical L2 | `$1E` − `$80` | `$20` |

    The orientation is bit 0 of the value saved in `$0A`, which is consumed by shifting `$0A` right
    once, so `$0A` holds half its GetMap16 value before being overwritten. All of these are 16-bit.

  `$00C0FB` decides whether the block is close enough to the camera to upload. If it is, it appends
  a stripe entry at `$7F837D + [$7F837B]` and adds `$10` to `$7F837B`. It clobbers `$00` and
  `$04-$06`.
- **Stack / control flow:** `$00C0FB` ends in `RTS` and is reached through the bank-0 `RTL` at `$00804D`.
- **Equivalent SMW code:** none as a unit. SMW's own tile changer, `$00BEB0`, takes a block *type* in
  `$9C`, not a raw tile number. The VRAM half is SMW's `$00C0FB` unchanged.
- **Calls:** `$00C0FB`.
- **Edge cases:**
  - Vertical layers leave `$99` and `$9B` swapped (see GetMap16).
  - Tile ≥ `$8000` is stored with bit 15 cleared, but `$00C0FB` receives `Y = tile×2`, also truncated to 16 bits.

## CheckForContactAlternate

Tests two boxes for overlap with 16-bit coordinates.

- **Called with:**
  - Any A width; the routine sets M=0.
  - Box 1: `$00` x, `$02` y, `$04` w, `$06` h.
  - Box 2: `$08` x, `$0A` y, `$0C` w, `$0E` h. All values are 16-bit.
- **Returns:**
  - M=1 on exit in every case.
  - C=1 for overlap, C=0 for none.
  - X and Y untouched. No RAM written.
  - N and Z come from the last 16-bit compare (by-product).
  - A holds the last 16-bit difference computed (by-product).
- **Rule:** both axes must pass, X first:
  - If `x1 ≥ x2`, X passes when `(x1 − x2) < w2`. Otherwise it passes when `(x2 − x1) < w1`.
  - Y works the same way with y and h.

  All comparisons are unsigned 16-bit, and the X axis is evaluated first.
- **Equivalent SMW code:** none. SMW's `$03B72B` uses 8-bit boxes with a different rule.
- **Calls:** none.
- **Edge cases:**
  - Equal coordinates use the first branch, so a zero-width box 2 never overlaps a box with the same x.
  - There is no wrap handling: a box near `$FFFF` does not overlap one near `$0000`.

## CircleX

Computes `radius · cos(angle)`.

- **Called with:**
  - 8/8.
  - `$04-$05` = angle. Only 9 bits count: `$05` bit 0 and `$04`, with `$000-$1FF` = 0–360°.
  - `$06` = radius.
- **Returns:**
  - `$07-$08` = 16-bit signed X displacement.
  - M=1.
  - X preserved (N and Z reflect X).
  - C = the half-circle sign bit used (see below).
  - A low = `$07`, B = `$08`.
  - `$04-$06` unchanged.
- **Computation:**
  1. `a' = (angle + $80)` mod `$200`. Index `i = a' & $FF`, sign `h` = bit 8 of `a'`. Bits 1-7 of
     `$05` are ignored. The +`$80` is a quarter turn, which turns sine into cosine.
  2. `T = H[i]` (0..`$FF`).
  3. The PPU signed multiply computes `P = T × r` with `r = $06` taken as a **signed** 8-bit value.
     Result = bits 8..23 of `P` as a 16-bit value, i.e. `floor(T·r / 256)` mod `$10000`.
  4. If `h = 1`, two's-complement negate.
- **Tables/data:** `H` is a 256-entry half-period sine, `H[i] = sin(π·i/256)·256`, clamped to `$FF`.
  It equals SMW's 16-bit sine table at `$07F7DB` (256 words), with the single `$0100` entry
  (i = `$80`) clamped to `$FF`. All 256 entries were verified.
- **Side effects:** writes `$211B` twice (`T`, then 0) and `$211C` (`r`). These are the Mode 7 matrix
  A/B registers and the Mode 7 write latch. This is observable if Mode 7 is in use. SA-1 uses its
  multiplier with `r` taken as **unsigned**, so the results differ for `r ≥ $80`.
- **Equivalent SMW code:** none as a routine. The table data is SMW's, as above.
- **Calls:** none.
- **Edge cases:** radius `$80-$FF` produces a negative/garbled magnitude on non-SA-1 (signed multiplier).

## CircleY

Computes `radius · sin(angle)`. The contract is the same as CircleX, with these differences:

- No quarter-turn offset: `i = $04`, `h` = `$05` bit 0.
- Output goes to `$09-$0A`. A low = `$09`, B = `$0A`, C = `h`.
- `$07-$08` are untouched.
- Same table, same multiply, same side effects.

## ExtendedCapeClipping

Checks whether extended sprite X is touching the cape, using a box the caller supplies.

- **Called with:**
  - 8/8, X = extended-sprite slot (0..9).
  - `$00` = X offset, `$01` = width, `$02` = Y offset, `$03` = height.
  - DB must map WRAM.
- **Returns:**
  - C = `$03B72B`'s contact result. X and Y preserved.
  - `$00-$03` are overwritten by the cape box. `$04-$0B`, `$0C` and `$0F` are clobbered.
- **Behaviour:**
  - Box A:
    - X = `$171F,x`/`$1733,x` + `$00`, unsigned. Only the carry reaches the high byte; the offset is **not** sign-extended.
    - Width = `$01`.
    - Y = `$1715,x`/`$1729,x` + `$02`, unsigned.
    - Height = `$03`.
  - Box B is the cape box, exactly SMW's `$029696` (see CapeContact).
  - No layer, spin or timer checks are made. The caller must check `$13E8` itself.
- **Equivalent SMW code:**
  - Box A is SMW's `$02A51C` with the caller's `$00-$03` in place of SMW's per-type tables at `$02A4E7`/`$02A4F3`/`$02A4FF`/`$02A50B`.
  - Box B is `$029696` verbatim.
- **Calls:** `$03B72B`.

## ExtendedGetDrawInfo

Finds the OAM slot for extended sprite X and the sprite's screen position. Erases or hides the
sprite when it is off screen.

- **Called with:**
  - 8/8, X = extended slot. **0..7 only.**
  - DB must map WRAM.
- **Returns:**
  - Y = OAM index for slot X, into the `$0200` page, not `$0300`.
  - `$00` = `$40` if `$1747,x` (X speed) bit 7 is clear, else `$00`. This is the OAM X-flip bit.
  - `$01` = low byte of `(X − $1A)`.
  - `$02` = low byte of `(Y − $1C)`, or `$F0` when the sprite is hidden or erased.
  - X preserved. No stack tricks: the caller must test the outcome itself (`$170B,x = 0` or `$02 = $F0`).
- **Rules:**
  - X axis: if the high byte of the 16-bit `X − camX` ≠ 0, then erase: `$170B,x` = 0 and `$02` = `$F0`.
  - Y axis: let `d` = `(Ylo − camYlo)` mod 256 and `c` = 1 if `Ylo ≥ camYlo`. Let
    `t = (Yhi + camYhi + c)` mod 256. Note the **addition**: this is not SMW's test.
    - `t ≠ 0`: erase if `d ≥ $F0`, otherwise visible.
    - `t = 0`: erase if `d < $C0`; else hide (`$02 = $F0`, not erased) if `d < $E0`; else visible.
- **Registers on return:**
  - Visible with `t ≠ 0`: A = `d`, C = 0.
  - Visible with `t = 0`: A = `d`, C = 1.
  - Hidden or erased: A = `$F0`, N = 1, Z = 0.
- **Tables/data:** the OAM index per slot is SMW's 8-byte table at `$02A153` (`$90,$94,…,$AC`).
- **Equivalent SMW code:** SMW's extended-sprite draw prologue at `$02A1A4-$02A1D3` (same table, same
  `$00`/`$01` values, same `$170B` erase). Differences:
  - SMW erases when the high byte of `Y − camY` ≠ 0 or `relY ≥ $F0`. PIXI uses the `t` rule above
    and has a "hide without erase" band.
  - PIXI writes no OAM.
- **Calls:** none.
- **Edge cases:** X = 8 or 9 (fireball slots) reads past the 8-entry table, so Y is whatever follows it in ROM. Callers must not use those slots.

## ExtendedHurt

Hurts the player if extended sprite X touches them. Uses a fixed 10×10 box.

- **Called with:**
  - 8/8, X = extended slot.
  - DB must map WRAM.
- **Returns:**
  - No contact: C=0.
  - Contact: SMW's `$02A469` has run (see Behaviour). C, A, Y and N/Z are its by-products.
  - X is preserved: `$02A469` preserves it on every path that matters.
  - DB is restored. Scratch `$00-$0F` per the box routines.
- **Behaviour:**
  1. Box A:
     - X = `$171F,x`/`$1733,x` + 3.
     - Y = `$1715,x`/`$1729,x` + 3.
     - Width and height = `$0A`.
  2. Box B = SMW's player box `$03B664`.
  3. If `$03B72B` reports contact, call `$02A469` with DB = `$02`. That routine:
     - With a star (`$1490` ≠ 0): turns the extended sprite into a smoke puff. Unless `$170B,x` = 4,
       it first moves the sprite 4 px up-left. Then `$170B,x` = 1 and `$176F,x` = 7.
     - Else, riding Yoshi: loses Yoshi (see LoseYoshi, but via `$18DF`).
     - Else: hurts the player via `$00F5B7`.
- **Stack / control flow:** the `RTS`-ending `$02A469` is reached through the `RTL` at `$02B889`.
- **Equivalent SMW code:** the tail of SMW's extended-vs-player check at `$02A3F6-$02A410` (then
  `$02A469`). Differences:
  - PIXI skips the layer test (`$13F9` vs `$1779,x`).
  - PIXI uses a fixed box instead of SMW's per-type box (`$02A519`).
  - PIXI has no special case for type `$0A`.
- **Calls:** `$03B664`, `$03B72B`, `$02A469`.

## ExtendedSpeed

Applies an extended sprite's speed to its position, optionally with gravity.

- **Called with:**
  - 8/8, X = extended slot.
  - A = mode: 0 = gravity then Y then X; 1 = Y then X; 2 = X only; 3 = Y only.
  - DB must map WRAM.
- **Returns:**
  - Modes 0, 1, 2: **X = `$15E9`** on return, because SMW's X updater reloads it. In extended-sprite
    code `$15E9` holds the current extended index, so in practice X is unchanged.
  - Mode 3: X unchanged.
  - Y = `$00` or `$FF`: the sign of the last speed applied. In modes 0, 1 and 2 that is the X speed;
    in mode 3 it is the Y speed. SMW's X updater runs the Y updater on the X fields.
  - A ≥ 4: returns immediately with A = A − 3 (8-bit) and changes nothing.
- **Dispatch quirk:** mode 0 is selected by the **Z flag at entry**, not by testing A. The modes after
  it are selected by decrementing A. A caller that loads A = 0 but arrives with Z=0 gets "A ≥ 4"
  behaviour (A = `$FD`, nothing done). A caller arriving with Z=1 and A ≠ 0 gets mode 0. Callers
  normally load A immediately before the call, so this does not arise.
- **Behaviour:**
  - Gravity (mode 0): when `$173D,x` − `$40` has bit 7 set (the `CMP`/`BPL` test, not a true signed
    compare), add 3 to `$173D,x`. So gravity applies when the Y speed is in `$C0-$FF` or `$00-$3F`,
    and **not** when it is in `$80-$BF` (fast upward). There is no cap at exactly `$40`: `$3F` becomes `$42`.
  - Y update: SMW `$02B560`. 4.4 fixed-point: fraction `$1751,x` += speed·16; position
    `$1715/$1729,x` += sign-extended speed>>4 + carry.
  - X update: SMW `$02B554`. The same on `$1747`/`$175B`/`$171F`/`$1733` (via X + `$0A`), then X = `$15E9`.
- **Stack / control flow:** both SMW routines end in `RTS` and are reached through the `RTL` at `$02B889`.
- **Equivalent SMW code:** `$02B554` and `$02B560` themselves. The gravity rule matches SMW's inline
  extended-sprite gravity idiom.
- **Calls:** `$02B554`, `$02B560`.

## FinishOAMWrite

Finalises a sprite's OAM tiles: sets each tile's size bit and hides tiles that are off screen.

- **Called with:**
  - 8/8, X = sprite slot.
  - A = number of tiles − 1.
  - Y = size: `$00` = 8×8, `$02` = 16×16, any value with bit 7 set = keep each tile's existing size bit.
  - The tiles are already written at `$0300 + $15EA,x` onward, with screen-relative coordinates.
  - DB must map WRAM: `$0300`/`$0460` are accessed absolute with the caller's DB.
- **Returns:**
  - X = `$15E9`. N and Z reflect X. C = 1.
  - Y = `$15EA,x + 4·(A+1)` mod 256, i.e. one past the last tile.
  - A low = `$F0` if the last tile was hidden, else the low byte of that tile's Y check value. B = 0.
- **Scratch:**
  - `$00-$01` = 16-bit `spriteY − camY`.
  - `$02-$03` = 16-bit `spriteX − camX`.
  - `$08` = `$FF` (the loop count runs out).
  - `$0B` = the size argument.
  - `$04-$07`, `$09` and `$0A` are untouched.
- **Per tile** (OAM index `o`, size index `j = o/4`):
  - `X16 = relX16 + sext8($0300,o − relX_lo)`.
  - `$0460,j = base + (X16 ≥ $100 ? 1 : 0)`, an 8-bit **add**. `base` is `$0B` if `$0B` bit 7 is
    clear, else `$0460,j & $02`.
  - `Y16 = relY16 + sext8($0301,o − relY_lo) + $10`. If `Y16 ≥ $100` (unsigned), set `$0301,o` = `$F0`.
  - Repeat for `A+1` tiles. A = `$80..$FF` gives `A+1` tiles via the decrement-and-test-sign loop,
    the same as SMW.
- **Equivalent SMW code:** `$01B7B3` (its body is at `$01B7BB`). It is identical in effect, except:
  - SMW stores `base` and then ORs bit 0; PIXI adds. They differ only when `$0B` is non-negative and odd.
  - SMW sets DB to `$01` internally and uses a different scratch layout (`$00-$0A`).
  - The carry on return differs (SMW: from the last Y test).
- **Calls:** none.

## FireballContact

Checks whether sprite X is touching either player fireball.

- **Called with:**
  - 8/8, X = sprite slot.
  - DB must map WRAM.
- **Returns:**
  - Contact: C=1, Y = 0 or 1, meaning extended slot 8 or 9.
  - None: C=0, Y = `$FF`.
  - X is preserved: it is restored last, so N and Z reflect X.
  - Scratch `$00-$0B`, `$0C` and `$0F` are clobbered.
- **Behaviour:** check slot 8, then slot 9. Skip a slot whose `$170B` is 0. For an occupied slot:
  - Box B = the fireball box:
    - X = 16-bit `$171F/$1733` − 2, width `$0C`.
    - Y = 16-bit `$1715/$1729` − 4, height `$13`.
  - Box A = `$03B69F` for sprite X.
  - Test with `$03B72B`. Stop at the first contact.
- **Equivalent SMW code:** the fireball box is exactly SMW's `$02A547` (indexed by the full extended
  slot there). SMW runs the check from the fireball's side. Differences:
  - PIXI accepts **any** non-zero extended type in slots 8 and 9, not only fireballs (type `$05`).
  - PIXI tests neither layers nor sprite status.
- **Calls:** `$03B69F`, `$03B72B`.

## GetDrawInfo

Sets sprite X's off-screen flags and returns its OAM index and screen position. When the sprite is
too far off screen, it abandons the routine that called it.

- **Called with:**
  - 8/8, X = sprite slot.
  - DB must map WRAM **and** must equal the program bank of the code that called the graphics routine (see Stack).
- **Writes, always:**
  - `$186C,x` = 0 first.
  - `$00-$01` = 16-bit `relX = (X16 − $1A16)`.
  - `$15A0,x` = 1 if the high byte of `relX` ≠ 0, else 0.
  - `$15C4,x` = 1 if `relX + $40 ≥ $180` (unsigned 16-bit), i.e. `relX` is not in `[−$40, $140)`. Else 0.
- **Normal return** (`$15C4,x` = 0):
  - `$186C,x` = bit 1 | bit 0:
    - Bit 1 is set only if `$190F,x` bit 5 is set and the high byte of `(Y16 + $20 − $1C16)` ≠ 0.
    - Bit 0 is set if the high byte of `(Y16 + $10 − $1C16)` ≠ 0.
  - `$01` = low byte of `(Y − $1C)`.
  - Y = `$15EA,x`. N and Z reflect Y.
  - A = high byte of `(Y16 + $10 − $1C16)`. B = `$01`.
  - C = 1 if the low byte of `(Y16 + $10 − $1C16)` ≥ `$10`.
  - M=1. X preserved.
- **Off-screen return** (`$15C4,x` = 1):
  - The stack is rewritten and the routine returns to the **caller's caller** (see Stack).
  - `$186C,x` = 0. `$01` keeps the high byte of `relX` (not a Y value).
  - A = low byte, and Y = high byte, of the caller's 2-byte return address.
  - C = 0. N and Z reflect Y. M=1.
- **Stack / control flow:** off screen, the routine:
  1. Discards its own 3-byte `JSL` return.
  2. Pops the 2-byte `JSR` return of the routine that called it (the sprite's graphics routine).
  3. Pushes that as a 3-byte return with **the current DB** as the bank.
  4. Executes `RTL`.

  The net effect is to pop 5 bytes and resume just after the `JSR` that called the graphics routine,
  in bank DB. This requires that the graphics routine was entered with `JSR`, that nothing else was
  pushed in between, and that DB equals that code's bank. PIXI sprites set DB = their own bank at
  their entry.
- **Equivalent SMW code:** `$01A365` (bank 1). The vertical offsets `$10`/`$20` and the bits `$01`/`$02`
  match SMW's table at `$01A361`. Bank 3's `$03B760` uses `$0C`/`$1C` and tests `$1662` instead.
  Differences from `$01A365`:
  - PIXI does not exclude carried sprites (`$14C8,x = 9`) from the tall check.
  - Off screen, SMW drops only its own 2-byte return and still sets Y/`$00`/`$01` before `RTS`ing
    to the graphics routine's caller. PIXI sets neither Y nor `$01`.
  - Return A and C differ (SMW: A = `$01`).
- **Calls:** none.

## GetMap16

Reads the Map16 tile number at a level position.

- **Called with:**
  - Any widths; the routine saves P.
  - `$98-$99` = block Y (16-bit). `$9A-$9B` = block X (16-bit).
  - `$1933` = layer (0 or 1).
  - DB must map WRAM: it reads `$1933`, `$1925` and `$13D7` absolute. DB is pushed and pulled but never changed.
- **Returns, valid position:**
  - A = tile number: 16-bit if entry M=0; otherwise A = low byte and B = high byte.
  - Y = tile high byte. With 16-bit index on entry, Y = `$00hh`.
  - P = entry P except that N and Z reflect the restored X. X preserved.
- **Returns, invalid position:**
  - A = `$FFFF` (8-bit entry: A = `$FF`, B = `$FF`).
  - Y = `$FF`/`$FFFF` matching the entry index width.
  - N=1, Z=0 (from Y). Other flags = entry values. X preserved.
- **Steps:**
  1. Save the original coordinates: `$0E-$0F` = `$98-$99`, `$0C-$0D` = `$9A-$9B`.
  2. Orientation and range check:
     - `$0A` = `$5B`, or `$5B >> 1` when `$1933` ≠ 0. Bit 0 of `$0A` = "this layer is vertical".
     - If vertical: **swap the bytes `$99` and `$9B` in RAM.** They are not swapped back.
     - Range check, EXLEVEL, horizontal layer: invalid if the 16-bit `$98-$99 ≥ $13D7` (unsigned).
     - Range check, EXLEVEL, vertical layer, and every non-EXLEVEL case: invalid if `$99 ≥ 2`, using the post-swap `$99`.
     - There is no check on the screen number (`$9B` after the swap).
  3. Scratch values. All positions here are post-swap except `$0D`/`$0F`:
     - `$0B` = screen number = `$9B`.
     - `$08-$09` = `($98-$99 & $FFF0) | ($9A >> 4)`.
     - `$06` = `$20 | ($1933 ? $10 : 0) | (($0F & 1) << 3) | (($0D & 1) << 2) | (($98 >> 6) & 3)`.
     - `$07` = `((($98 >> 4) & 3) << 6) | (($9A >> 3) & $1E)`.
     - `$06/$07` together form the BG tilemap VRAM word address that ChangeMap16 uses.
  4. Map16 address, a chain of three 16-bit adds. Each add carries in the previous one's carry; the
     first starts with C=0. In practice every carry is 0.
     1. `p1 = word[$00BEA8 + 2·$1933] + 2·$1925`
     2. `p2 = word[$00:p1] + 3·$0B`
     3. `addr = word[$00:p2] + $08-$09`

     This is SMW's own pointer chain: `$00BEA8` = {`$BDA8`, `$BDE8`, …} → per-level-mode tables →
     3-byte per-screen pointers. Only their low 16 bits are used.
  5. Tile = `$7F:addr` << 8 | `$7E:addr`.
- **Equivalent SMW code:** none as a routine. SMW computes Map16 addresses inline (for example
  `$00C1AC`); the pointer tables are SMW's.
- **Calls:** none.
- **Edge cases:**
  - Callers that reuse `$98-$9B` after a call on a vertical layer see `$99`/`$9B` swapped. A second
    call without restoring them reads the wrong block.
  - `$1933` values other than 0/1 index past the `$00BEA8` table.
  - With SA-1, the `$40/$41` banks are read instead of `$7E/$7F`.

## LoseYoshi

If the player is riding Yoshi, Yoshi runs away as if the player had been hit.

- **Called with:**
  - 8/8, no inputs.
  - DB must map WRAM.
- **Returns:**
  - A, X and Y preserved. N and Z reflect A. C and V are unchanged.
- **Behaviour:** if `$187A` = 0, nothing happens. Otherwise, with `s = $18E2 − 1` as Yoshi's slot:
  - `$163E,s` = `$10`.
  - Sound effects: `$1DFA` = `$03`, `$1DFC` = `$13`.
  - `$C2,s` = 2 (running).
  - `$187A` = 0, `$0DC1` = 0.
  - Player speed: `$7D` = `$C0`, `$7B` = 0.
  - `$B6,s` = SMW `$02A4B3[$157C,s]`, i.e. `$10` when facing right (0), `$F0` when facing left (1).
  - `$1594,s` = 0, `$151C,s` = 0, `$18AE` = 0.
  - `$1497` = `$30`.
- **Equivalent SMW code:** `$02A473-$02A4AD` (the Yoshi branch of `$02A469`), with the same writes in
  the same order. Differences:
  - SMW takes Yoshi's slot from **`$18DF`**; PIXI uses **`$18E2`**.
  - PIXI also preserves A and Y and tests `$187A` itself.
- **Tables/data:** SMW's `$02A4B3` (2 bytes).
- **Calls:** none.
- **Edge cases:** if `$18E2` = 0 while `$187A` ≠ 0, then `s` = `$FF` and the writes land at `table−1`. SMW's version has the same hazard with `$18DF`.

## Random

Returns a random number from 0 to `max` inclusive.

- **Called with:**
  - Any widths.
  - A (low byte) = `max`.
  - DB must map WRAM: `$148D` is read absolute.
- **Returns:**
  - A low byte = the result. B = entry B.
  - P = entry P except that N and Z reflect the restored X.
  - X preserved. Y preserved (by SMW's routine).
- **Behaviour:**
  1. Call SMW's `$01ACF9`. It advances the generator twice and updates `$148B-$148E`.
  2. Let `r` = `$148D`.
  3. If `max` = `$FF`, the result is `r`. Otherwise the result is `floor(r·(max+1) / 256)`, the high
     byte of the hardware product.
- **Side effects:** `$4202/$4203` (SA-1: `$2250-$2254`).
- **Equivalent SMW code:** none (it wraps `$01ACF9`).
- **Calls:** `$01ACF9`.

## SetPlayerClippingAlternate

Writes the player's interaction box as clipping 1, with 16-bit values.

- **Called with:**
  - X must be 8-bit. Any A width; the routine leaves M=1.
  - DB must map WRAM: `$187A` is read absolute.
- **Returns:**
  - `$00-$01` = `$94` + 2.
  - `$02-$03` = `$96` + `yoff[i]`.
  - `$04-$05` = `$000C`.
  - `$06` = `height[i]`, `$07` = 0.
  - Here `i` = (`$73` ≠ 0 or `$19` = 0 ? 1 : 0) + (`$187A` ≠ 0 ? 2 : 0).
  - M=1. X preserved (N and Z reflect X).
  - C = carry out of the 16-bit `$96` add.
  - A = low byte of `$02`, B = `$03`.
- **Tables/data:** SMW's `$03B65C` (Y offsets `$06,$14,$10,$18`) and `$03B660` (heights `$1A,$0C,$20,$18`).
- **Equivalent SMW code:** `$03B664` computes the same box as 8-bit box B (`$00/$08/$02/$01/$09/$03`).
  This is the 16-bit rewrite in clipping-1 layout.
- **Calls:** none.

## SetSpriteClippingAlternate

Turns a caller-supplied box into clipping 2 for sprite X by adding the sprite's position.

- **Called with:**
  - 8/8, X = sprite slot.
  - `$08-$09` = X offset, `$0A-$0B` = Y offset, `$0C-$0D` = width, `$0E-$0F` = height (all 16-bit).
- **Returns:**
  - `$08-$09` += sprite X (`$14E0:$E4`). `$0A-$0B` += sprite Y (`$14D4:$D8`). `$0C-$0F` unchanged.
  - M=1. C = carry of the Y add. A = low byte of the new `$0A`, B = new `$0B`. X and Y untouched.
- **Equivalent SMW code:** none.
- **Calls:** none.

## ShooterMain

Decides whether shooter X fires this frame and finds a free sprite slot for the shot.

- **Called with:**
  - 8/8, X = shooter slot (0..7).
  - A = timer reload value.
  - C=1 to apply the "player is next to the shooter" veto, C=0 to skip it.
  - DB must map WRAM.
- **Returns:**
  - C=0 means fire: Y = free slot from `$02A9DE`, A = Y, N=0, Z = (Y = 0).
  - C=1 means don't fire.
  - On every C=1 path except "no free slot", Y is **unchanged**. On "no free slot", Y = `$FF`.
  - B is clobbered. X preserved.
- **Behaviour, in order** (each "stop" returns C=1):
  1. If `$17AB,x` ≠ 0: stop. The timer is not changed.
  2. `$17AB,x` = A, whether or not the shooter fires afterwards.
  3. If the veto is requested: stop if `($94 − $179B,x + $11) mod 256 < $22`. This uses low bytes
     only, so it means "within about 16 px horizontally".
  4. Stop if the high byte of the 16-bit `($1793,x:$178B,x) − ($1D:$1C)` ≠ 0.
  5. Stop if the high byte of `(shooterX − camX)` ≠ 0.
  6. Stop if `(($179B,x − $1A) + $10) mod 256 < $10`, i.e. the shooter is in the rightmost 16 px of the screen.
  7. Call `$02A9DE`. Stop if it returns `$FF`.
- **Equivalent SMW code:** the Bullet Bill shooter, `$02B466-$02B4A5` (timer `$60`). Differences:
  - PIXI makes the player veto optional and tests it first. SMW tests it last. There are no side
    effects between the steps, so the outcome is the same.
- **Calls:** `$02A9DE`. It uses `$0E/$0F` and searches from the top of the `$1692`-dependent slot range down, keeping 2 slots in reserve.

## SpawnExtended

Spawns an extended sprite at sprite X's position plus an offset, with initial speeds.

- **Called with:**
  - 8/8, X = parent sprite slot.
  - A = extended sprite number.
  - `$00` = X offset (signed), `$01` = Y offset (signed), `$02` = X speed, `$03` = Y speed.
  - DB must map WRAM.
- **Returns:**
  - Success: C=0, Y = the new extended slot (0..7).
  - Failure: C=1. Two cases:
    - (a) The parent is off screen or being eaten (`$15A0,x | $186C,x | $15D0,x` ≠ 0). Y is **unchanged**.
    - (b) No free slot. Y = `$FF`.
  - X preserved. `$00-$03` unchanged.
- **Search:** slots 7 down to 0, taking the first with `$170B,y` = 0. Slots 8 and 9 (fireballs) are never used.
- **Writes:**
  - `$170B,y` = number.
  - `$171F,y`/`$1733,y` = parent X16 + sext(`$00`).
  - `$1715,y`/`$1729,y` = parent Y16 + sext(`$01`).
  - `$1747,y` = `$02`, `$173D,y` = `$03`.
  - Nothing else: fractions, `$1765`, `$176F`, `$1779` (layer) are not touched.
- **Equivalent SMW code:** none as a routine. SMW spawns inline.
- **Calls:** none.

## SpawnSmoke

Spawns a smoke sprite at sprite X's position plus an offset.

- **Called with:**
  - 8/8, X = parent slot.
  - A = smoke type.
  - `$00` = X offset, `$01` = Y offset, `$02` = timer.
  - DB must map WRAM.
- **Returns:**
  - Success: C=0, Y = slot (0..3).
  - Failure: C=1, Y = `$FF`.
  - X preserved.
- **Search:** slots 3 down to 0, taking the first with `$17C0,y` = 0.
- **Writes:**
  - `$17C0,y` = type.
  - `$17CC,y` = `$02`.
  - `$17C4,y` = (`$D8,x` + `$01`) mod 256.
  - `$17C8,y` = (`$E4,x` + `$00`) mod 256.
  - Smoke positions are 8-bit only, so offsets are not sign-extended.
- **Equivalent SMW code:** SMW's inline smoke spawns, for example inside `$01AB6F`. Difference: when
  no slot is free, SMW **reuses slot 0**; PIXI fails.
- **Calls:** none. No on-screen test is made.

## SpawnSprite

Spawns a normal or custom sprite at sprite X's position plus an offset, with initial speeds.

- **Called with:**
  - 8/8, X = parent slot.
  - A = sprite number.
  - C=1 for a custom sprite, C=0 for a vanilla one.
  - `$00` = X offset (signed), `$01` = Y offset (signed), `$02` = X speed, `$03` = Y speed.
  - DB must map WRAM.
- **Returns:**
  - Success: C=0, Y = new slot, X = parent. N and Z come from loading `$03`.
  - Failure: C=1, Y = `$FF`, X = parent (N and Z reflect X).
  - `$00-$03` are preserved, even across `$0187A7`.
- **Search:** slots `$0B` down to 0 (SA-1: `$15` down to 0), taking the first with `$14C8` = 0. All
  slots are searched: the `$1692` sprite-memory limits are ignored.
- **Writes, in order, for slot n:**
  1. `$9E,n` = number.
  2. Call `$07F7D2`, which clears the slot and loads vanilla tweakers for `$9E,n`. The carry flag
     must survive this call; it does in vanilla and in a PIXI-patched ROM.
  3. Custom only:
     1. `$7FAB9E,n` = number.
     2. Call `$0187A7` (the driver's "load custom tables" hook). It clobbers `$00-$02`; PIXI saves and
        restores `$00-$03` around it.
     3. `$7FAB10,n` = `$08` (custom flag set, extra bits clear).
  4. `$14C8,n` = 1 (init).
  5. `$E4,n`/`$14E0,n` = parent X16 + sext(`$00`).
  6. `$D8,n`/`$14D4,n` = parent Y16 + sext(`$01`).
  7. `$B6,n` = `$02`, `$AA,n` = `$03`.
  - Nothing else is set: no direction, no layer (`$1632`), no off-screen flags.
- **Equivalent SMW code:** none as a routine. Compare SMW's inline spawns that use `$02A9DE`/`$07F7D2`.
- **Calls:** `$07F7D2`, `$0187A7` (custom only).
- **Edge cases:** for a vanilla spawn, the custom-state cleanup relies on the driver's hook inside `$07F7D2` (at `$07F785`).

## SpawnSpriteSafe

The same as SpawnSprite, except that the search starts at slot **`$04`** (caller-supplied) and goes
down to 0.

- **Edge cases:**
  - `$04` must be < the slot count. With `$04` ≥ `$80`, the first probe is the out-of-range slot
    `$04`, then the search runs from `$7F` down.
  - `$04` is not preserved across `$0187A7`.

## Star

Star-kills sprite X and gives the player points, as a star hit does.

- **Called with:**
  - 8/8, X = sprite slot.
  - The routine does not check that the player has a star.
- **Returns:**
  - X preserved.
  - Y = the sound index used (`$18D2` capped to 8).
  - `$0E-$0F` = 16-bit (`$94` − sprite X).
  - DB is preserved: it is set to the routine's own bank internally, so that bank must map WRAM.
  - N and Z come from restoring DB.
- **Behaviour, in order:**
  1. `$01AB6F`: `$1DF9` = `$03`, plus a contact-graphic smoke when the sprite is on screen.
  2. `$14C8,x` = 2 and `$AA,x` = `$D0`.
  3. `$B6,x` = `$F0` if the player is to the right (`$94/$95 ≥` sprite X), else `$10`. This is SMW's
     table at `$01A839`, indexed by the SubHorzPos result.
  4. `$18D2` += 1, then capped at 8.
  5. `$02ACE5` with A = `$18D2`: gives points, or a 1-up at 8.
  6. `$1DF9` = the sound for `$18D2`: index 1-7 gives `$13-$19`, index ≥ 8 gives `$03`, index 0 gives `$00`.
- **Equivalent SMW code:** the star branch of SMW's sprite-player contact, `$01A847-$01A87D`. Differences:
  - Order: SMW gives points and sound first and sets the status and speeds afterwards.
  - Direction: SMW uses `$D1/$D2` (`$01AD30`, the player's X at the start of the frame) and writes only `$0F`. PIXI uses `$94/$95` and writes `$0E-$0F`.
  - Count ≥ 8: SMW leaves `$1DF9` as `$01AB6F` set it (`$03`). PIXI writes `$03` itself, so the result is the same.
  - Count wrapped to 0 (`$18D2` was `$FF`): SMW reads `$01A61D` (`$60`); PIXI uses `$00`.
  - PIXI does not check the `$167A` bit 1 "immune to star" flag. The caller's code does that.
- **Tables/data:** X speeds = SMW `$01A839` (`$F0,$10`). Sounds = SMW `$01A61E-$01A624` (`$13-$19`), plus `$00` and `$03` as above.
- **Calls:** `$01AB6F`, `$02ACE5`.

## SubHorzPos

Reports which side of sprite X the player is on.

- **Called with:**
  - 8/8, X = sprite slot.
- **Returns:**
  - `$0E-$0F` = 16-bit `$94/$95` − `$E4/$14E0,x`: **low byte in `$0E`, high byte in `$0F`**.
  - Y = 1 if that difference is negative (player to the left), else 0.
  - A = high byte of the difference.
  - Y = 0: N=0, Z = (high byte = 0), C = no-borrow of the high-byte subtraction.
  - Y = 1: N=0, Z=0.
- **Equivalent SMW code:** `$03B817` / `$02D4FA`. Difference: SMW stores the **low** byte in `$0F` and
  does not write `$0E`. Sprites written against SMW's convention that read `$0F` as the low byte get
  the high byte here. `$01AD30` is the variant that uses `$D1/$D2`.
- **Calls:** none.

## SubOffScreen

Erases sprite X when it has moved too far off screen. A selects the size of the off-screen margin.

- **Called with:**
  - M=1, X 8-bit, X = sprite slot.
  - A = margin set. Only bits 0-2 are used:
    - 0-3 select SMW bank 1's X0-X3 margins.
    - 4-7 select bank 3's X0-X3 margins.
  - The routine sets DB to its own bank after the first test, so that bank must map WRAM.
- **Returns:**
  - Always: `$03` = `(A & 7) · 2`.
  - If `$15A0,x | $186C,x` = 0: returns at once with A = 0, Z=1, N=0, C=0. Nothing else is changed.
  - Otherwise DB is restored on every exit, so N and Z reflect the caller's DB byte.
  - X preserved, except under EXLEVEL (see "X under EXLEVEL" below).
  - Y = unchanged, or the margin index, or `$161A,x` (see "Y on return" below).
  - C: on the erase paths, C = 1 exactly when `$14C8,x` ≥ 8 and `$161A,x` = `$FF`, else 0. On the
    horizontal-level keep path after the margin test, C = `$13` bit 0. On every other path C is a by-product.
  - `$00` and `$01` are clobbered. On paths that test margins, `$01` ends as `A & 7` (horizontal) or the parity index (vertical).
- **Horizontal level** (`$5B` bit 0 = 0):
  1. Bottom-of-level test.
     - EXLEVEL: erase when **any** of these hold, where bit 15 means bit 15 of the 16-bit result:
       - bit 15 of (`Y16 − $13D7`) is clear;
       - bit 15 of (`(Y16 − $1C) − $0BF2`) is clear;
       - bit 15 of (`(Y16 − $1C) − $0BF0`) is set.

       `$13D7` = level height. `$0BF2` and `$0BF0` are the Y range limits kept in RAM.
     - Non-EXLEVEL: erase when `(Yhi + carry(Ylo + $50) − 2)` has bit 7 clear.
  2. If `$167A,x` bit 2 is set (process while off screen): return.
  3. Margin test:
     - `i = (A&7)·2 + ($13 & 1)`.
     - `D = high byte of ($1A16 + sext16(off[i]) − X16)`, where `off` = `t15[i]:t14[i]`.
     - If `$13` bit 0 is set, `D ^= $80`.
     - Erase if `D` bit 7 is set, otherwise return.

     The effect: on even frames, test the right margin (erase if the sprite is beyond it); on odd
     frames, test the left margin.
- **Vertical level:**
  1. If `$167A,x` bit 2 is set, or `$13` is odd: return.
  2. If `$14E0,x ≥ 2`: erase.
  3. `j = ($13 >> 1) & 1`. `D = high byte of ($1C16 + (t13[j]:t12[j]) − Y16)`. If `j = 1`, `D ^= $80`. Erase if `D` bit 7 is set.
- **Erase:**
  1. If `$14C8,x ≥ 8` and `$161A,x` ≠ `$FF`: sprite load table`[$161A,x]` = 0. The table is at
     **`$7FAF00`** (or `$1938` when PIXI is built with `!Disable255SpritesPerLevel`; SA-1: `$418A00`).
     This lets the sprite reload.
  2. `$14C8,x` = 0.
- **Y on return:** unchanged, the margin index `i`/`j`, or `$161A,x` on the reload-clearing erase.
- **X under EXLEVEL:** in the bottom-of-level test, X is temporarily used and then reloaded from
  `$15E9`, so X = `$15E9` afterwards. B is restored.
- **Tables/data:** all SMW's own.
  - Vertical: `t12` = `$40,$B0` and `t13` = `$01,$FF` (SMW `$01AC0D`/`$01AC0F`, the same at `$03B83B`).
  - Horizontal, 16 entries: entries 0-7 = SMW `$01AC11` (t14) / `$01AC19` (t15); entries 8-15 =
    SMW `$03B83F` / `$03B847`.
- **Equivalent SMW code:**
  - A=0..3 ≈ `$01AC31` / `$01AC2B` / `$01AC27` / `$01AC21`.
  - A=4..7 ≈ `$03B85D` / `$03B857` / `$03B853` / `$03B84F`.

  Differences from bank 1:
  - There is no Magikoopa (`$9E` = `$1F`) `$18C0`/`$18C1` handling.
  - Sprites `$22`/`$24` are not exempt on vertical odd-half frames.
  - The load table is `$7FAF00`, not `$1938`.
  - The EXLEVEL bottom test.
  - The routine is long-called and switches DB itself.
- **Calls:** none.

## SubVertPos

Reports whether the player is above or below sprite X.

- **Called with:**
  - 8/8, X = sprite slot.
- **Returns:**
  - `$0F` = low byte of (`$96/$97` − `$D8/$14D4,x`).
  - Y = 1 if the 16-bit difference is negative (player above), else 0.
  - A = high byte of the difference.
  - Flags: as for SubHorzPos.
  - `$0E` untouched.
- **Equivalent SMW code:** `$03B829` exactly (`RTL` instead of `RTS`). The `$02D50C` variant writes
  `$0E` instead, and `$01AD42` uses `$D3/$D4` and `$0E`.
- **Calls:** none.

---

## Summary

Counts are files under `D:\SMW\Projects\*\Sprites\**\*.asm`, excluding `routines` folders (82
files), that contain `%Name(`. The count of distinct files by content is in brackets.

| routine | used by | SMW equivalent | difficulty notes |
|---|---|---|---|
| Aiming | 0 | none | Hardest to get bit-exact: band selection, `R[k]` formula, three floor-multiplies, sign rules. Fully specified for non-SA-1. |
| CapeContact | 0 | `$0293CE-$0293F2` body + `$029696` | Easy; layer/climb rule is the only subtlety. |
| ChangeMap16 | 1 [1] | none (`$00C0FB` reused) | Address math shared with GetMap16; bit 15 dropped on store; leaves `$99`/`$9B` swapped. |
| CheckForContactAlternate | 0 | none | Easy; returns M=1 always. |
| CircleX | 0 | table = `$07F7DB` clamped | Signed PPU multiply (radius ≥ `$80` is negative); writes Mode 7 registers. |
| CircleY | 0 | table = `$07F7DB` clamped | As CircleX. |
| ExtendedCapeClipping | 0 | `$02A51C` + `$029696` | Easy; offsets unsigned. |
| ExtendedGetDrawInfo | 0 | `$02A1A4-$02A1D3` (≈) | Odd vertical rule (`t` uses addition); slots 8-9 read past the table. |
| ExtendedHurt | 0 | `$02A3F6`-tail + `$02A469` | Easy; call `$02A469` with DB = `$02`. |
| ExtendedSpeed | 7 [1] | `$02B554`/`$02B560` | X = `$15E9` after X update; gravity sign quirk. |
| FinishOAMWrite | 0 | `$01B7B3` | Size bit added, not ORed; scratch layout differs from SMW. |
| FireballContact | 0 | box = `$02A547` | Any type in slots 8/9 counts. |
| GetDrawInfo | 15 [5] | `$01A365` | Stack rewrite on off-screen (5 bytes out, needs DB = caller's bank); no status-9 exclusion. |
| GetMap16 | 0 | none | Pointer chain via `$00BEA8`; EXLEVEL range check; RAM swap side effect. |
| LoseYoshi | 1 [1] | `$02A473-$02A4AD` | Uses `$18E2` where SMW uses `$18DF`. |
| Random | 0 | wraps `$01ACF9` | Easy. |
| SetPlayerClippingAlternate | 0 | `$03B664` (16-bit form) | Easy. |
| SetSpriteClippingAlternate | 0 | none | Trivial. |
| ShooterMain | 0 | `$02B466-$02B4A5` | Y unchanged on most "don't fire" paths. |
| SpawnExtended | 0 | none (inline idiom) | Y unchanged on off-screen refusal. |
| SpawnSmoke | 0 | inline idiom (`$01AB6F`) | Fails instead of reusing slot 0. |
| SpawnSprite | 10 [2] | none | Carry through `$07F7D2`; `$0187A7` with `$00-$03` saved; write order. |
| SpawnSpriteSafe | 1 [1] | none | As SpawnSprite, start at `$04`. |
| Star | 8 [1] | `$01A847-$01A87D` | Order and `$94` vs `$D1`; `$0E-$0F` written. |
| SubHorzPos | 13 [3] | `$03B817`/`$02D4FA` | Writes `$0E`=low and `$0F`=high, unlike SMW. |
| SubOffScreen | 13 [3] | `$01AC31`… / `$03B85D`… | EXLEVEL branch, `$7FAF00` load table, X ← `$15E9`, DB switch. |
| SubVertPos | 0 | `$03B829` | Exact. |

## sa1def.asm macros

Clean-room interface contracts for the assembler macros PIXI defines in `asm/sa1def.asm`, the
file every PIXI sprite is assembled with. Same rules as the rest of this file: behaviour only,
no PIXI code, no paraphrase of its instructions, no PIXI comments.

- Date: 2026-10-20
- Source read: `D:\SMW\Projects\.resources\PIXI\asm\sa1def.asm` (7 macros) and, for comparison,
  `D:\SMW\Projects\Secret\tools\pixi\asm\sa1def.asm` (a newer copy: the same 7 plus `invoke_sa1`, and it
  does not first include an optional `config.asm`).
- Usage scanned: every `.asm` under `D:\SMW\Projects\*\Sprites\**` and `D:\SMW\Projects\Secret\tools\pixi\**`,
  `routines` folders excluded (107 files), for `%Name(` calls that are not a shared routine
  (`BigEye\Sprites\routines`) and not a macro defined in the calling file or its folder's `_header.asm`.

### Defines the macros depend on

- `!SA1` — 1 when the ROM header byte `$00FFD5` is `$23` (SA-1 map mode), else 0. Set by `sa1def.asm`.
- `!Base2` — `$6000` on SA-1, `$0000` on LoROM. Set by `sa1def.asm`.
- `!extra_bits` — the extra-bits sprite table, indexed by slot: `$7FAB10` (LoROM) / `$6040` (SA-1).
  Set by `sa1def.asm` through `define_sprite_table`.
- `!__debug` — optional, from the user or the command line; treated as 0 when undefined.

### debugmsg(msg)

- **Purpose.** Assembly-time trace: tells the user where a piece of code landed.
- **Effect.** Emits no bytes. If `!__debug` is undefined it becomes defined as 0. When `!__debug`
  equals 1, the assembler prints a line made of `$`, the current PC in hex, a space and the text
  `msg`. Otherwise nothing is printed.
- **Registers/RAM.** None (no code generated).
- **SA-1.** Identical.

### BES(label) — branch if extra bit set

- **Precondition.** M=1 (8-bit A); X = the sprite's slot. On SA-1 the table operand is the absolute
  `$6040,x`, so DB must reach I-RAM/BW-RAM there (`$00-$3F`/`$80-$BF`); on LoROM it is the long
  `$7FAB10,x` and DB does not matter.
- **Effect.** Tests bit 2 (value `$04`) of the sprite's extra-bits byte `!extra_bits,x` — the
  "extra bit" Lunar Magic stores per placed sprite. Branches to `label` when the bit is **set**,
  falls through when clear.
- **Leaves.** A = `$04` when the branch is taken, `$00` when it falls through; B, X, Y unchanged.
  Z clear when taken, set when not; N clear; C and V unchanged. No RAM written.
- **Limits.** Short relative branch (±128 bytes): `label` must be near.
- **SA-1.** Same, with the table at `$6040,x`.

### BEC(label) — branch if extra bit clear

- Exactly as `BES` with the sense inverted: branches to `label` when bit 2 of `!extra_bits,x` is
  **clear** (A = `$00`, Z set), falls through when it is set (A = `$04`, Z clear).

### Between(x, label)

- **Parameters.** `x` is an operand written as it would follow an instruction, normally an immediate
  such as `#$10`. The macro also needs "two times x", which the assembler forms by appending `*2` to
  that text (so `#$10` gives the immediate `$20`). `label` is a near (short-branch) label.
- **Effect.** Signed range check on A: falls through when `-x <= A < x`, branches to `label`
  otherwise. In effect: A + x (carry cleared first) is compared, unsigned, with `2*x`; the branch is
  taken when the sum is greater than or equal.
- **Leaves.** A = original A + x, wrapped to the register width — A is **modified**. C set when the
  branch is taken, clear when it falls through; N and Z from the comparison; V from the addition.
  X, Y and RAM untouched.
- **Width.** Works in whichever width M selects. With an 8-bit A, `2*x` must fit in a byte
  (x ≤ `$7F`).
- **SA-1.** Identical.

### invoke_snes(addr)

- **Purpose.** Run from **SA-1 code** to make the SNES CPU execute a routine (work the SA-1 cannot
  do, such as touching PPU or DMA registers). Relies on the SA-1 Pack (1.10+) SNES-side IRQ handler.
- **Parameters.** `addr` — 24-bit address of a routine for the SNES CPU, ending in `RTL`.
- **Effect.** Stores `addr` little-endian at `$0183` (low), `$0184` (high), `$0185` (bank); writes
  `$D0` to SA-1 register `$2209` (raises the SA-1-to-SNES IRQ with the custom IRQ vector selected);
  waits until `$018A` reads non-zero (the SA-1 Pack's handler sets it once the SNES routine has
  returned); then stores 0 to `$018A`.
- **Leaves.** Assumes M=1. A clobbered; X, Y unchanged; Z set, N clear at exit.
  RAM `$0183-$0185` = `addr`, `$018A` = 0.
- **LoROM.** Meaningless: on a non-SA-1 ROM it hangs, since nothing sets `$018A`. Only assemble it
  inside `if !SA1` blocks.

### invoke_sa1(label) — newer sa1def only

- Present only in the `Secret\tools\pixi` copy. The SNES-side counterpart: makes the SA-1 CPU run
  `label` (ending in `RTL`; DB is not set for it, so it must set its own). Stores the 24-bit `label`
  at `$3180` (low), `$3181` (high), `$3182` (bank), then calls, with a near `JSR`, the SA-1 Pack's
  call-SA-1 helper at `$1E80`, which returns once the SA-1 routine has finished. The calling code
  must run in a bank where `$1E80` is that helper (`$00-$3F`/`$80-$BF`). Assumes M=1; A clobbered.
  SA-1 only.

### define_sprite_table(name, addr, addr_sa1)

- **Effect.** Assembly time only, no bytes: defines `!name` as `addr` when `!SA1` is 0 and as
  `addr_sa1` when `!SA1` is 1. `name` is used as text: it may be passed quoted and may start with a
  digit (`sa1def.asm` itself defines both `!7FAB10` and `!extra_bits` this way, each resolving to
  `$7FAB10` / `$6040`).
- **Note.** `!SA1` must be set before it is called. Calling it again for an existing name overwrites it.

### define_base2_address(name, addr)

- **Effect.** Assembly time only: defines `!name` as `addr` when `!SA1` is 0, and as `addr` OR
  `!Base2` (`addr | $6000`) when `!SA1` is 1. Meant for `$0100-$1FFF` WRAM addresses that the SA-1
  build relocates to `$6100-$7FFF`.

### Who calls them

| Macro | Files | Sprites | Where |
|---|---|---|---|
| BEC | 11 | 3 | KoopaShell: BigEye, CastleOfLoneliness, DogsOfWar, ShaoBase, TestRom, Secret `pixi\sprites\koopa_shell.asm`; UpsideDownKoopaShell: BigEye, CastleOfLoneliness, ShaoBase; FlyingQuestionBlock: DogsOfWar, TestRom |
| debugmsg | 1 | 0 | `Secret\tools\pixi\asm\main.asm` — PIXI's own driver, not a sprite |
| BES, Between, invoke_snes, invoke_sa1, define_sprite_table, define_base2_address | 0 | 0 | — |

The scan's other non-routine `%Name(` calls are **not** from `sa1def.asm`:

- `SpawnClusterGeneric`, `SpawnExtendedGeneric`, `SpawnMinorExtendedGeneric`, `SpawnScoreGeneric`,
  `SpawnSmokeGeneric`, `SpawnSpinningCoinGeneric`, `BounceSetMarioSpeed`, `BounceSetSpeed`,
  `BounceSetupMap16`, `BounceUpdatePos`, `MinorExtendedSpeed`, `ScoreUpdateYPos`,
  `SpinningCoinUpdateYPos` — called from the `_header.asm` files of Secret's PIXI (`cluster`,
  `extended`, `misc_sprites\*`). They are shared routines of the newer PIXI, in subfolders of
  `Secret\tools\pixi\routines\` (`Spawn`, `Bounce`, `MinorExtended`, `Score`, `SpinningCoin`, ...),
  which the BigEye routine set documented above does not have.
- `CallSprite`, `CallStatusPtr`, `CallPerLevelStatusPtr`, `CallExtCape` — PIXI driver internals
  (`Secret\tools\pixi\asm\pointer_caller.asm`), used only by the driver's own `asm\*.asm`.
- `BeginImage`, `BeginImageData`, `EndImage`, `EndImageData`, `DefineAnimation`,
  `MakeAnimationOffsets`, `MakeImagesPerAnimation`, `MakeTile`, `jslrts`, `SpriteJumpTable` —
  DogsOfWar's CustomLemmyWendy, defined in its own included `CustomLemmyWendyDefines.asm`.

## Measured against PIXI's (2026-10-20)

Pipe Dream's routines (`src/data/spritedriver/routines`, MIT) and its versions of the macros above
(in `src/data/spritedriver/defines.asm`, all but the SA-1 `invoke_*` ones) were written from this
file and SMW's code. The routines were then run against PIXI's own in `PixiRoutineTests`:
thousands of random states each, both on the same vanilla ROM in the CPU interpreter, every
register, flag and byte of RAM compared, only what this file calls a by-product masked. Where that
disagreed with the contracts above, PIXI's behaviour was measured black-box (inputs varied one at
a time, outputs read) and ours made to match. The corrections:

- **SpawnSprite / SpawnSpriteSafe, no free slot:** A is whatever the search last read, and Y is
  where the search stopped: `$FF` when it started below `$80` (always, in practice). Started at
  `$81` or above, the first `DEY` makes it negative and the search ends there, so Y = start − 1;
  from `$80` exactly it carries on down from `$7F`.
- **GetDrawInfo, off screen:** B = 0.
- **GetMap16 / ChangeMap16:** 2 × `$1933`, 2 × `$1925` and 3 × the screen are each taken modulo
  `$100`, and the tripling keeps the carry out of its doubling (3 × `$80` = `$81`). Only out-of-range
  inputs see this. `$1926`, `$1934` and `$0C` do not count.
- **ChangeMap16:** A on return is a by-product.
- **Star:** C on return is a by-product (PIXI's is mostly set; it is not in the contract).
- **LoseYoshi:** confirmed: with `$18E2` = 0 the writes land one below each table.
- **FinishOAMWrite:** the size index starts at `$15EA,x / 4` and counts one per tile **without**
  wrapping with the OAM index, so past OAM `$FC` it goes on to `$0460 + $40` and up.
- **ExtendedHurt:** PIXI's runs `$02A469` with DB = `$00`, not `$02` as above. The one read there
  that goes by DB is Yoshi's run-off speed (`$A4B3,y`), so with PIXI's, Yoshi runs off at a speed
  read from bank 0. **Ours keeps DB = `$02`, as SMW does: the one deliberate difference.** The test
  keeps the player off Yoshi for this routine.

What the random states had to be kept to, and why, is in the test's setups: real level modes and
screens, Map16 pointers into ROM at `$0FBE`, a short stripe buffer, a data bank below `$10`.
Outside those, SMW's own pointers lead into low RAM, where each routine's own stack sits, and the
two differ by construction.

## PIXI 1.40: new routines

Clean-room interface contracts for the 23 shared routines that PIXI 1.40 has and 1.2.x does not.
Same rules as the rest of this file: behaviour only, no PIXI code, no paraphrase of its
instructions, no PIXI comments, no PIXI data tables (a table is cited only when it is SMW's own or
equal to SMW's own, by address).

- Date: 2026-10-20
- Source read: `D:\SMW\Projects\Secret\tools\pixi\routines\**\*.asm` (PIXI 1.40, 50 files). The
  macro name is the path with the folders run together: `Bounce/SetSpeed.asm` is `%BounceSetSpeed()`.
  Defines resolved through `D:\SMW\Projects\Secret\tools\pixi\asm\sa1def.asm`. Intended use read from
  the `_header.asm` files of `cluster`, `extended`, `shooters`, `sprites` and `misc_sprites\*`.
  The PIXI folder has no README.
- SMW addresses checked against `D:\SMW\Projects\.resources\SMW.smc` with the PipeDream disassembler.

### Conventions for this section

Everything under "Conventions that apply to every routine" above still holds (LoROM, D = 0,
`JSL`/`RTL`, 8/8 on entry, by-product flags). In addition:

- **X = the slot of the calling sprite's own kind** (bounce slot, score slot, ...), 8-bit. No routine
  range-checks it. Every sprite table is read with absolute addressing, so **DB must map WRAM** for
  every routine in this section. ROM tables (`$02xxxx`) are read long and do not depend on DB.
- **RAM per sprite kind** (LoROM; the SA-1 build ORs `$6000` into each, as `define_base2_address` does):

  | kind | slots | number | X lo/hi | Y lo/hi | X speed | Y speed | X frac | Y frac | other |
  |---|---|---|---|---|---|---|---|---|---|
  | bounce | 4 | `$1699` | `$16A5`/`$16AD` | `$16A1`/`$16A9` | `$16B5` | `$16B1` | `$16B9` | `$16BD` | `$16C9` direction (bits 0-1), `$1901` properties |
  | score | 6 | `$16E1` | `$16ED`/`$16F3` | `$16E7`/`$16F9` | — | `$16FF` | — | — | `$1705` layer |
  | smoke | 4 | `$17C0` | `$17C8`/— | `$17C4`/— | — | — | — | — | `$17CC` timer |
  | spinning coin | 4 | `$17D0` | `$17E0`/`$17EC` | `$17D4`/`$17E8` | — | `$17D8` | — | `$17DC` | `$17E4` layer |
  | minor extended | 12 | `$17F0` | `$1808`/`$18EA` | `$17FC`/`$1814` | `$182C` | `$1820` | `$1844` | `$1838` | `$1850` timer |
  | cluster | 20 | `$1892` | `$1E16`/`$1E3E` | `$1E02`/`$1E2A` | — | — | — | — | |
  | extended | 8 (+2 fireball) | `$170B` | `$171F`/`$1733` | `$1715`/`$1729` | `$1747` | `$173D` | `$175B` | `$1751` | |

  The bounce fractions: PIXI's names put the **X** fraction at `$16B9` and the **Y** fraction at
  `$16BD`. SMW's own bounce code uses them the other way round (see BounceUpdatePos).
- **The 4.4 position update `U(s, f, p)`**, used by several routines. `s` = 8-bit speed (signed,
  1/16 px per frame), `f` = the 8-bit fraction byte, `p` = the 16-bit position (two bytes):
  1. `f ← (f + ((s & $0F) << 4)) mod 256`; `c` = 1 if that sum was ≥ 256.
  2. `p ← (p + q + c) mod $10000`, where `q` = `floor(sext8(s) / 16)` (a value −8..7) as 16 bits.
  3. On exit: **Y = `$00` if `s` < `$80`, else `$FF`**. A = the new high byte of `p`. N, Z, V and C
     are those of the high-byte add (`hi(q) + hi(p) + carry from the low byte`); C = carry out of the
     16-bit sum.

  This is exactly SMW's `$02B526` (bounce Y), `$02B560` (extended Y) and `$02B58E` (spinning coin Y).
- **The spawn routines take the number in B, not A.** Every `Spawn/*Generic` routine stores the high
  byte of the accumulator (B) as the new sprite's number; the low byte of A on entry is ignored.
  The callers' macros in every `_header.asm` do `XBA` and then fill `$04-$07` with their own position,
  so the number travels in B. A reimplementation must take it from B.

### BounceGetDrawInfo

Finds the OAM slot and screen position of bounce sprite X. Handles the X high bit and layer 2.

- **Called with:** 8/8, X = bounce slot (0..3). DB must map WRAM (the camera is read absolute, `$001A+i`).
- **Behaviour:**
  1. `$03` = `$02` (16×16 size bit).
  2. Camera: layer 1 (`camX` = `$1A/$1B`, `camY` = `$1C/$1D`, `i` = 0) when `$1901,x` bit 7 is clear;
     layer 2 (`$1E/$1F`, `$20/$21`, `i` = 4) when it is set.
  3. `$00` = low byte of `X16 − camX`. If the high byte of the 16-bit `X16 − camX` is non-zero,
     `$03` = `$03` (size bit plus X high bit). The sprite is **not** rejected for being off screen horizontally.
  4. `$01` = low byte of `Y16 − camY`. `hy` = high byte of the 16-bit `Y16 − camY`.
- **Returns:**
  - `hy` ≠ 0 (not drawable): C=0, A = `hy`, Z=0, N = bit 7 of `hy`, **Y = `i`** (0 or 4).
  - `hy` = 0: C=1, Y = A = SMW's OAM index byte at `$0291ED + x` (`$10,$14,$18,$1C`), N=0, Z=0.
  - X preserved. `$00`, `$01` and `$03` are written on both paths; `$02` and `$04-$0F` are untouched.
- **Equivalent SMW code:** the bounce-sprite draw prologue `$0291F8-$02922D`, same OAM table. Differences:
  - SMW takes the layer from `$16C9,x` bit 7; PIXI from `$1901,x` bit 7.
  - SMW rejects the sprite when the X high byte is non-zero; PIXI keeps it and sets the X high bit in `$03`.
  - SMW leaves the camera bytes in `$02-$05`; PIXI writes only `$00`, `$01`, `$03`.
- **Calls:** none.
- **Edge cases:** X ≥ 4 reads past SMW's 4-byte OAM table (into code at `$0291F1`).

### BounceGenericDraw

Draws bounce sprite X as one 16×16 tile.

- **Called with:** 8/8, X = bounce slot, A = tile number. DB must map WRAM.
- **Behaviour:** `$02` = A. Then BounceGetDrawInfo. If that reports C=0, return at once with its exit
  state (`$02` = tile is the only extra write). Otherwise, with `o` = the OAM index it returned:
  - `$0200+o` = `$00`, `$0201+o` = `$01`, `$0202+o` = `$02`, `$0203+o` = `$1901,x OR $64`.
  - `$0420 + o/4` = `$03`.
- **Returns:**
  - Drawn: Y = `o/4`, A = `$03` (`$02` or `$03`), N=0, Z=0, **C=0**. The routine gives no draw
    status: C is 0 on both paths (for slots 0..3).
  - Not drawn: BounceGetDrawInfo's C=0 exit.
  - X preserved.
- **Equivalent SMW code:** the rest of SMW's bounce draw from `$02922D` writes the same OAM bytes
  (position, tile, `$1901,x | $64`, size into `$0420`) with SMW's own tile choice and off-screen rule.
- **Calls:** BounceGetDrawInfo.

### BounceSetMarioSpeed

Gives the player the push of a bounce block.

- **Called with:** 8/8, X = bounce slot. DB must map WRAM.
- **Behaviour:** `d` = `$16C9,x & 3`.
  - `v` = SMW's `$0290D6[d]` (`$80,$80,$80,$00`). If `v` ≠ `$80`, `$7D` = `v`.
  - `w` = SMW's `$0290DA[d]` (`$80,$E0,$20,$80`). If `w` ≠ `$80`, `$7B` = `w`.
  - So: `d`=0 nothing; 1: `$7B` = `$E0`; 2: `$7B` = `$20`; 3: `$7D` = 0.
  PIXI carries its own copy of these 8 bytes; the values equal SMW's.
- **Returns:** X preserved. **Y = X** (the slot). A = `w`. Flags from comparing `w` with `$80`:
  `d`=0 or 3: Z=1, C=1, N=0; `d`=1: Z=0, C=1, N=0; `d`=2: Z=0, C=0, N=1.
- **Equivalent SMW code:** `$0290F3-$02910A` exactly (same tables, `$80` = "leave alone").
- **Calls:** none.

### BounceSetSpriteSpeed

Gives a normal sprite the push of a bounce block.

- **Called with:** 8/8, X = bounce slot, `$0F` = the normal sprite's slot. DB must map WRAM.
- **Behaviour:** `d` = `$16C9,x & 3`. `v` = SMW `$0290D6[d]`; if `v` ≠ `$80`, `$AA` of slot `[$0F]` = `v`.
  `w` = SMW `$0290DA[d]`; if `w` ≠ `$80`, `$B6` of slot `[$0F]` = `w`. The same values as BounceSetMarioSpeed.
- **Returns:** X preserved, Y = X, A = `w`, flags as BounceSetMarioSpeed. `$0F` unchanged.
- **Equivalent SMW code:** none for sprites; SMW applies these tables only to the player (`$0290F3`).
- **Calls:** none.

### BounceSetSpeed

Accelerates a bounce sprite as SMW's bounce blocks are accelerated.

- **Called with:** 8/8, X = bounce slot. DB must map WRAM.
- **Behaviour:** `d` = `$16C9,x & 3`. `$16B1,x` (Y speed) += SMW `$0290CE[d]` (`$10,$00,$00,$F0`);
  then `$16B5,x` (X speed) += SMW `$0290D2[d]` (`$00,$F0,$10,$00`). 8-bit, wrapping. PIXI's copy of
  the tables equals SMW's.
- **Returns:** X preserved, Y = X, A = the new `$16B5,x`. N, Z, V, C from that last add.
- **Equivalent SMW code:** `$029111-$02912A` exactly.
- **Calls:** none.

### BounceSetupMap16

Sets `$98-$9B` and `$1933` to the block under bounce sprite X, ready for SMW's `$00BEB0`.

- **Called with:** 8/8, X = bounce slot. DB must map WRAM (`$1933` absolute).
- **Behaviour:**
  - `$98` = `(Ylo + 8) & $F0`, `$99` = `Yhi` + carry of `Ylo + 8`.
  - `$9A` = `(Xlo + 8) & $F0`, `$9B` = `Xhi` + carry of `Xlo + 8`.
  - `$1933` = bit 7 of `$1901,x` (0 or 1).
- **Returns:** A = the `$1933` value, Z = (A = 0), N=0, **C = bit 6 of `$1901,x`**. X and Y preserved.
  `$9C` is not touched (the `_header.asm` macros set it before calling).
- **Equivalent SMW code:** `$0291BC-$0291E5` (inside `$0291BA`) computes the same four bytes (X first)
  and the layer. Difference: SMW takes the layer from `$16C9,x` bit 7, PIXI from `$1901,x` bit 7.
  SMW then calls `$00BEB0` itself.
- **Calls:** none.

### BounceUpdatePos

Moves bounce sprite X by its speeds.

- **Called with:** 8/8, X = bounce slot. DB must map WRAM.
- **Behaviour:** `U($16B1,x, $16BD,x, $16A9:$16A1,x)` (Y), then `U($16B5,x, $16B9,x, $16AD:$16A5,x)` (X).
- **Returns:** as `U` for the X update: Y = sign byte of `$16B5,x`, A = new `$16AD,x`, N/Z/V/C of its
  high-byte add. X preserved.
- **Equivalent SMW code:** `$02B526` (Y) then `$02B51A` (X), as SMW calls them at `$02910B`. Differences:
  - **The fraction bytes are swapped:** SMW's Y update uses `$16B9,x` and its X update `$16BD,x`.
    PIXI uses `$16BD,x` for Y and `$16B9,x` for X.
  - SMW's X update leaves X = `$1698`; PIXI preserves X.
- **Calls:** none.

### ScoreGetDrawInfo

Finds the OAM slot and screen position of score sprite X.

- **Called with:** 8/8, X = score slot (0..5). DB must map WRAM (camera read absolute).
- **Behaviour:**
  1. `i` = `(4 · $1705,x) mod 256`. `$02-$03` = the 16-bit word at `$001C+i`, `$04-$05` = the word at
     `$001A+i` (layer 0: `$1C`/`$1A`; layer 1: `$20`/`$1E`). Written on every path; M=0 only for these
     two copies.
  2. `rx` = `(X16 − [$04-$05]) mod $10000`, `ry` = `(Y16 − [$02-$03]) mod $10000`.
  3. Drawable when `rx` ≤ `$F3` and the high byte of `ry` is 0. Tested in this order, each failure
     returning at once: (a) high byte of `(X16 + $0C − camX) mod $10000` ≠ 0; (b) high byte of `rx` ≠ 0;
     (c) high byte of `ry` ≠ 0.
- **Returns:**
  - Drawable: `$00` = low byte of `rx`, `$01` = low byte of `ry`, Y = A = SMW `$02AD9E + x`
    (`$B0,$B8,$C0,$C8,$D0,$D8`), C=1, N=1, Z=0.
  - Rejected: C=0, A = the non-zero high byte that failed, Z=0, N = its bit 7, Y = `i`. `$00/$01` not written.
  - X preserved. M=1.
- **Equivalent SMW code:** `$02AE5B-$02AEA9` (score-sprite draw): the same tests in the same order and
  the same OAM table. Difference: SMW uses OAM index 4 instead of the table while `$0D9B` bit 6 is set
  (boss battles); PIXI has no such case.
- **Calls:** none.

### ScoreGenericDraw

Draws score sprite X as two 8×8 tiles side by side, in front of all layers.

- **Called with:** 8/8, X = score slot. `$07` = left tile, `$08` = right tile, `$09` = left
  properties, `$0A` = right properties. DB must map WRAM.
- **Behaviour:** ScoreGetDrawInfo. If C=0, return with its exit state. Else, with `o` = its OAM index:
  - `$0200+o` = `$00`, `$0204+o` = `($00 + 8) mod 256`; `$0201+o` = `$0205+o` = `$01`.
  - `$0202+o` = `$07`, `$0206+o` = `$08`; `$0203+o` = `$09 | $30`, `$0207+o` = `$0A | $30`.
  - `$0420 + o/4` = 0 and `$0421 + o/4` = 0.
- **Returns:** drawn: C=1, A=0, Z=1, N=0, Y = `o/4`. Not drawn: ScoreGetDrawInfo's C=0 exit. X
  preserved. `$07-$0A` unchanged.
- **Equivalent SMW code:** `$02AEA5-$02AEF1` writes the same eight OAM bytes and two size bytes, with
  SMW's own tiles and properties.
- **Calls:** ScoreGetDrawInfo.

### ScoreUpdateYPos

Lets score sprite X rise one pixel at a time, stopping near the top of the screen.

- **Called with:** 8/8, X = score slot.
  - C=0: the limit is `$04` (SMW's). A is ignored.
  - C=1: the limit is A.
  - The routine sets DB to its own bank for its body, so that bank must map WRAM (every LoROM bank
    `$00-$3F`/`$80-$BF` does). DB is restored on exit.
- **Behaviour:**
  1. `$00` = limit.
  2. `t` = `$16FF,x >> 4`. Mask `k` = SMW's `$02AD9A[t]` for `t` 0..3 (`$03,$01,$00,$00`): speed
     `$00-$0F` moves every 4th frame, `$10-$1F` every 2nd, `$20-$3F` every frame. PIXI keeps its own
     4-byte copy of these values; **for `t` ≥ 4 (speed ≥ `$40`) it reads whatever PIXI assembled after
     that copy, which is not defined.** Callers keep the speed below `$40` (SpawnScoreGeneric uses `$30`);
     a differential test must too.
  3. If `$13 & k` ≠ 0, stop.
  4. If `($16E7,x − $1C) mod 256` < `$00`, stop (low bytes and layer 1 only, whatever `$1705,x` says).
  5. Otherwise decrement the 16-bit `$16F9:$16E7,x` by 1.
- **Returns:** X preserved. N and Z reflect the caller's DB byte (restored last). By path:
  - Stopped at 3: Y = `t`, A = `$13 & k`, C = bit 3 of `$16FF,x`.
  - Stopped at 4: Y = old `$16E7,x`, A = `($16E7,x − $1C) mod 256`, C=0.
  - Moved: Y = A = old `$16E7,x`, C=1.
- **Equivalent SMW code:** `$02AE38-$02AE5A` with the limit fixed at 4 (C=0 gives exactly that).
  SMW indexes `$02AD9A` directly, so its `t` ≥ 4 reads the next SMW table (`$02AD9E`).
- **Calls:** none.

### SmokeGetDrawInfo

Finds the OAM slot and screen position of smoke sprite X.

- **Called with:** 8/8, X = smoke slot (0..3). DB must map WRAM.
- **Behaviour:** `d` = `($17C8,x − $1A) mod 256`. Low bytes only: no high byte or layer is considered.
- **Returns:**
  - `d` ≥ `$F0`: C=0, A = `d`, Y unchanged, Z = (`d` = `$F0`), N = bit 7 of `d − $F0`. Nothing written.
  - Otherwise: `$00` = `d`, `$01` = `($17C4,x − $1C) mod 256`, Y = A = SMW `$0296B8 + x`
    (`$20,$24,$28,$2C`), C=1, N=0, Z=0.
  - X preserved. The smoke sprite is never erased.
- **Equivalent SMW code:** none exact. SMW's smoke draw at `$02974A` uses the same OAM table but
  rejects at `d` ≥ `$F4` and also at a relative Y ≥ `$E0`.
- **Calls:** none.

### SmokeGenericDraw

Draws smoke sprite X as one tile.

- **Called with:** 8/8, X = smoke slot. `$02` = tile, `$03` = properties (YXPPCCCT), `$04` = the
  `$0420` byte (size and X high bit). DB must map WRAM.
- **Behaviour:** SmokeGetDrawInfo; if C=0, return with its exit state. Else `$0200+o` = `$00`,
  `$0201+o` = `$01`, `$0202+o` = `$02`, `$0203+o` = `$03`, `$0420 + o/4` = `$04`.
- **Returns:** drawn: C=1, Y = `o/4`, A = `$04`, N/Z from `$04`. X preserved. `$02-$04` unchanged.
- **Equivalent SMW code:** none as a routine.
- **Calls:** SmokeGetDrawInfo.

### SpinningCoinGetDrawInfo

Meant to find the OAM slot and screen position of spinning-coin sprite X, erasing it when off the
side of the screen. **As shipped it compares the wrong camera bytes** (see Behaviour). The contract
describes what it does, not what it was meant to do.

- **Called with:** 8/8, X = spinning-coin slot (0..3). DB must map WRAM.
- **Behaviour:**
  1. `i` = `(4 · $17E4,x) mod 256`. `$02-$03` = word at `$001C+i` (camera **Y**), `$04-$05` = word at
     `$001A+i` (camera **X**). For layer 1: `$02`=`$1C`, `$03`=`$1D`, `$04`=`$1A`, `$05`=`$1B`.
  2. `b` = 1 if `$17D4,x` < `$02`, else 0. `h` = `($17E8,x − $04 − b) mod 256`: the coin's Y high
     byte minus the camera's **X low** byte. `c` = 1 if `$17E8,x` ≥ `$04 + b` (no borrow), else 0.
     If `h` ≠ 0: return, not drawn and not erased.
  3. `d` = `($17E0,x − $03 − (1 − c)) mod 256`: the coin's X low byte minus the camera's **Y high**
     byte, minus a borrow that is 0 unless `$17E8,x` = 0, `$04` = `$FF` and `b` = 1.
     If `d` ≥ `$F8`: `$17D0,x` = 0 (erase) and return, not drawn.
  4. Otherwise drawable.
- **Returns:**
  - Not drawn at step 2: C=0, A = `h`, Z=0, N = bit 7 of `h`, Y = `i`.
  - Erased at step 3: C=0, A = `d`, Y = `i`, N/Z from comparing `d` with `$F8`.
  - Drawable: `$00` = `d`, `$01` = `($17D4,x − $02) mod 256`, Y = A = SMW `$0299E9 + x`
    (`$30,$38,$40,$48`), C=1, N=0, Z=0.
  - `$02-$05` are written on every path. X preserved. The X high byte `$17EC,x` is never read.
- **Equivalent SMW code:** `$029A08-$029A3D`. SMW stores the camera bytes as `$02` = Y lo, `$03` = X lo,
  `$04` = Y hi and compares against those; PIXI stores two words (`$02-$03` = Y, `$04-$05` = X) but
  compares against the same offsets, hence the mix-up. SMW also starts the X subtraction with no
  borrow, and erases through its own path at `$0299E3`.
- **Calls:** none.
- **For the differential test:** with real camera values `h` is almost never 0, so the routine almost
  always returns "not drawn". Random states are needed to reach steps 3 and 4.

### SpinningCoinUpdateYPos

- **Called with:** 8/8, X = spinning-coin slot. DB must map WRAM.
- **Behaviour:** `U($17D8,x, $17DC,x, $17E8:$17D4,x)`.
- **Returns:** as `U`. X preserved.
- **Equivalent SMW code:** `$02B58E` exactly (`RTL` for `RTS`).
- **Calls:** none.

### MinorExtendedGetDrawInfo

Finds the OAM slot and screen position of minor extended sprite X; erases it when well off screen.

- **Called with:** 8/8, X = minor extended slot (0..11). DB must map WRAM.
- **Behaviour:**
  1. `$01` = `ry` = `($17FC,x − $1C) mod 256`. If `ry` is in `$E0..$EF`: erase. Only the low bytes
     are used; the Y high bytes are never read.
  2. `$02` = 0. `$00` = low byte of `X16 − camX` (`X16` = `$18EA:$1808,x`, `camX` = `$1B:$1A`).
     If the high byte of that 16-bit difference is non-zero: when `($00 + $38) mod 256` ≥ `$70`, erase;
     otherwise `$02` = 1 and carry on. Any non-zero high byte counts, not only ±1 screen.
  3. Drawable.
  - Erase means `$17F0,x` = 0 and return, not drawn.
- **Returns:**
  - Erased at step 1: C=0, A = `(ry + $10) mod 256` (`$F0..$FF`), Y unchanged. `$00` and `$02` not written.
  - Erased at step 2: C=0, A = `($00 + $38) mod 256`, Y unchanged. `$00`, `$01` and `$02` (= 0) written.
  - Drawable: C=1, Y = A = **the byte at `$028B77 + x`**, N/Z from it. `$00`, `$01`, `$02` written.
  - X preserved.
- **Tables/data:** SMW's minor-extended OAM table is at **`$028B78`** (12 bytes, `$50,$54,…,$78,$7C`;
  SMW reads it at `$028C6E` and seven other places, never at `$028B77`). PIXI reads one byte earlier,
  so slot `n` gets SMW's index for slot `n−1`, and slot 0 gets `$60` (the byte before the table), the
  same index as slot 5. In a vanilla or PIXI-patched ROM the bytes `$028B77-$028B82` are
  `$60,$50,$54,$58,$5C,$60,$64,$68,$6C,$70,$74,$78`. To match PIXI, read `$028B77 + x`.
- **Equivalent SMW code:** none as a routine; SMW's minor extended sprites each have their own draw code.
- **Calls:** none.

### ClusterGetDrawInfo

The same contract as MinorExtendedGetDrawInfo, on the cluster tables:

- Number `$1892,x`, Y low `$1E02,x`, X `$1E3E:$1E16,x`. X = cluster slot (0..19).
- Drawable: Y = A = SMW's byte at `$02FF50 + x` (20 bytes: `$E0,$E4,…,$FC,$5C,$58,…,$30`), the table
  SMW's cluster code reads at `$02FCD9` and elsewhere. Here the address is SMW's own (no offset).
- Erase means `$1892,x` = 0.
- **Calls:** none.

### MinorExtendedSpeed

Moves minor extended sprite X along one axis.

- **Called with:** 8/8, X = minor extended slot, A = mode. Only bits 0-1 count: bit 0 = fast, bit 1 =
  Y axis. DB must map WRAM.
  - 0: slow X: `U($182C,x, $1844,x, $18EA:$1808,x)`.
  - 1: fast X: `$18EA:$1808,x` += `sext8($182C,x)` (whole pixels, no fraction).
  - 2: slow Y: `U($1820,x, $1838,x, $1814:$17FC,x)`.
  - 3: fast Y: `$1814:$17FC,x` += `sext8($1820,x)`.
- **Returns:** every mode: Y = `$00` if the speed used is < `$80`, else `$FF`; A = the new position
  high byte; N, Z, V, C from the high-byte add (C = carry out of the 16-bit sum). X preserved.
- **Equivalent SMW code:** the slow modes are SMW's 4.4 update (as `$02B560`) on the minor extended
  tables. SMW has no routine for it; its minor extended sprites update inline.
- **Calls:** none.

### SpawnClusterGeneric (`Spawn/ClusterGeneric.asm`)

Spawns a cluster sprite at an origin plus an offset.

- **Called with:** 8/8. **B = cluster number.** `$00` = X offset, `$01` = Y offset (both signed).
  **`$02-$03` = origin X, `$04-$05` = origin Y** (16-bit). DB must map WRAM.
- **Search:** slots 19 down to 0, taking the first with `$1892,y` = 0.
- **Writes (slot `n`):** `$1892,n` = B; `$1E3E:$1E16,n` = `[$02-$03] + sext($00)`;
  `$1E2A:$1E02,n` = `[$04-$05] + sext($01)`. Nothing else: no misc tables are cleared.
- **Returns:**
  - Success: C=0, Y = `n`, A = the new `$1E2A,n`, N/Z/V from its add, **B = 0**.
  - No free slot: C=1, Y = `$FF`, A = `$1892` (slot 0's number, non-zero), N=1, Z=0, B = entry B.
    Nothing written.
  - X untouched. `$00-$07` unchanged.
- **Equivalent SMW code:** none (SMW spawns cluster sprites inline).
- **Calls:** none.
- **Edge case: the callers disagree.** Every `_header.asm` `%SpawnCluster()` macro puts the caller's
  position in `$04-$07`, as for the other `*Generic` spawns. With this routine's layout, that gives the
  new cluster sprite X = whatever is in `$02-$03` and Y = the caller's **X**. Implement the routine's
  layout (`$02`/`$04`); what the header macros should pass is a separate matter.

### SpawnExtendedGeneric (`Spawn/ExtendedGeneric.asm`)

- **Called with:** 8/8. B = extended number. `$00`/`$01` = X/Y offset (signed), `$02` = X speed,
  `$03` = Y speed, `$04-$05` = origin X, `$06-$07` = origin Y. DB must map WRAM.
- **Search:** slots 7 down to 0, first with `$170B,y` = 0. The fireball slots 8 and 9 are never used.
- **Writes:** `$170B,n` = B; `$1733:$171F,n` = origin X + sext(`$00`); `$1729:$1715,n` = origin Y +
  sext(`$01`); `$1747,n` = `$02`; `$173D,n` = `$03`. Fractions, `$1765`, `$176F` and `$1779` are not touched.
- **Returns:** success: C=0, Y = `n`, A = `$03`, N/Z from `$03`, B = 0. No free slot: as
  SpawnClusterGeneric (A = `$170B`). X untouched; `$00-$07` unchanged.
- **Equivalent SMW code:** none. Unlike SpawnExtended there is no parent off-screen test.
- **Calls:** none.

### SpawnGeneric (`Spawn/Generic.asm`), the minor extended spawner

- **Called with:** as SpawnExtendedGeneric, with B = minor extended number.
- **Search:** slots 11 down to 0, first with `$17F0,y` = 0.
- **Writes:** `$17F0,n` = B; `$18EA:$1808,n` = origin X + sext(`$00`); `$1814:$17FC,n` = origin Y +
  sext(`$01`); `$182C,n` = `$02`; `$1820,n` = `$03`. Fractions and `$1850` (timer) are not touched.
- **Returns:** as SpawnExtendedGeneric (failure A = `$17F0`).
- **Calls:** none.
- **The name:** by the path rule its macro is `%SpawnGeneric()`, but every `_header.asm` calls
  `%SpawnMinorExtendedGeneric()`, which no file in `routines\` provides. A PIXI-compatible set needs
  at least the name the headers use.

### SpawnScoreGeneric (`Spawn/ScoreGeneric.asm`)

- **Called with:** 8/8. B = score number. `$00`/`$01` = X/Y offset, `$04-$05` = origin X, `$06-$07` =
  origin Y. DB must map WRAM (`$18F7` absolute).
- **Search, which never fails:** slots 5 down to 0, first with `$16E1,y` = 0. If none is free:
  decrement `$18F7`; if the result has bit 7 set, `$18F7` = 5; use slot `$18F7`.
- **Writes:** `$16E1,n` = B; `$16F3:$16ED,n` = origin X + sext(`$00`); `$16F9:$16E7,n` = origin Y +
  sext(`$01`); `$16FF,n` = `$30`. `$1705` (layer) is not touched.
- **Returns:** Y = `n`, A = `$30`, N=0, Z=0. C = carry out of the Y high-byte add (by-product: the
  routine reports no status). B = 0 if a free slot was found; otherwise B = `$16E1` (slot 0's
  number, as the search last read it), or `$05` when `$18F7` wrapped. X untouched.
- **Equivalent SMW code:** the slot choice is SMW's `$02AD34` exactly (same search, same `$18F7`
  round-robin). SMW's `$02ACEF` then places the score at the sprite's position − 8, nudged down when
  above the screen; PIXI uses origin + offset.
- **Calls:** none.

### SpawnSmokeGeneric (`Spawn/SmokeGeneric.asm`)

- **Called with:** 8/8. B = smoke number. `$00`/`$01` = X/Y offset, `$02` = timer, `$04` = origin X
  low, `$06` = origin Y low (`$05` and `$07` are ignored). DB must map WRAM.
- **Search:** slots 3 down to 0, first with `$17C0,y` = 0.
- **Writes:** `$17C0,n` = B; `$17C8,n` = `($04 + $00) mod 256`; `$17C4,n` = `($06 + $01) mod 256`;
  `$17CC,n` = `$02`. 8-bit positions only: the high bytes `$1964`/`$1960` are not written.
- **Returns:** success: C=0, Y = `n`, A = `$02`, N/Z from `$02`, B = 0. No free slot: as
  SpawnClusterGeneric (A = `$17C0`); slot 0 is not reused, unlike SMW's inline smoke spawns. X untouched.
- **Calls:** none.

### SpawnSpinningCoinGeneric (`Spawn/SpinningCoinGeneric.asm`)

- **Called with:** 8/8. B = number. `$00`/`$01` = X/Y offset, `$03` = Y speed, `$04-$05` = origin X,
  `$06-$07` = origin Y. `$02` is ignored. DB must map WRAM.
- **Search:** slots 3 down to 0, first with `$17D0,y` = 0.
- **Writes:** `$17D0,n` = B; `$17EC:$17E0,n` = origin X + sext(`$00`); `$17E8:$17D4,n` = origin Y +
  sext(`$01`); `$17D8,n` = `$03`. `$17DC` (fraction) and `$17E4` (layer) are not touched.
- **Returns:** success: C=0, Y = `n`, A = `$03`, N/Z from `$03`, B = 0. No free slot: as
  SpawnClusterGeneric (A = `$17D0`).
- **Calls:** none.

### Edge cases common to the `*Generic` spawns

- B = 0 "succeeds" but leaves the slot marked free.
- No on-screen test is made. The `sprites\_header.asm` version of `%SpawnMinorExtended()` makes its own
  (`$15A0 | $186C | $15D0` of the parent) before calling; the other macros do not.
- Positions are 16-bit adds of a sign-extended 8-bit offset, carry included; they wrap at `$10000`.

## PIXI 1.40: changes to the 1.2.x routines

- Date: 2026-10-20
- Compared: `D:\SMW\Projects\BigEye\Sprites\routines\*.asm` (1.2.x, the source of the contracts above)
  with the 1.40 files of the same macro name in `D:\SMW\Projects\Secret\tools\pixi\routines\`
  (`ExtendedX` → `Extended/X.asm`, `SpawnX` → `Spawn/X.asm`). Both were reduced to their instructions
  (comments, label spelling and 1.40's local-label syntax removed, defines resolved) and compared.
  The two `sa1def.asm` files give the same values for every define these routines use.

**Changed in behaviour: 1 of 27 (ExtendedGetDrawInfo).**

### ExtendedGetDrawInfo: changed

- **Vertical rule.** The 1.2.x rule (`t` formed by addition) is gone. 1.40 uses the true high byte of
  the 16-bit `Y16 − camY16`. With `d` = `(Ylo − camYlo) mod 256` and `h` = that high byte:
  - `h` = 0: erase if `d` ≥ `$F0`, otherwise visible.
  - `h` = `$FF` (up to one screen above): erase if `d` < `$C0`; hide (`$02` = `$F0`, not erased) if
    `d` < `$E0`; otherwise visible.
  - any other `h`: erase.

  Registers on return keep the same shape: visible with `h` = 0: A = `d`, C=0; visible with
  `h` = `$FF`: A = `d`, C=1; hidden or erased: A = `$F0`, N=1, Z=0. The X-axis rule, `$00`, `$01`
  and the erase (`$170B,x` = 0) are unchanged.
- **OAM index table.** 1.40 reads SMW's `$02A153` directly instead of its own copy. Slots 0..7 get the
  same values. Slots 8 and 9 now get SMW's next bytes, `$2C` and `$2D` (defined, but not usable OAM
  indexes), instead of whatever followed PIXI's copy. Callers still must not use those slots.

### Same behaviour, different code

- **Star:** the player-side test is now a `JSL` to the shared SubHorzPos instead of a private copy
  reached by `JSR`. Same writes (`$0E-$0F`, Y) and results; it uses one more byte of stack, and a 1.40
  Star needs SubHorzPos inserted too.
- **SpawnExtended:** the search start is written as `!ExtendedSize − 1`, which is 7, as before.
- **ExtendedHurt:** the bank-2 `RTL` address is written as `$B888` instead of `$B889 − 1`. Same value.
- **ChangeMap16, GetMap16, SubOffScreen, Aiming:** the `!EXLEVEL` / `!SA1` conditionals are written as
  comparisons. Both defines are only ever 0 or 1, so the same branch is assembled.

### Identical

Aiming, CapeContact, ChangeMap16, CheckForContactAlternate, CircleX, CircleY, ExtendedCapeClipping,
ExtendedHurt, ExtendedSpeed, FinishOAMWrite, FireballContact, GetDrawInfo, GetMap16, LoseYoshi,
Random, SetPlayerClippingAlternate, SetSpriteClippingAlternate, ShooterMain, SpawnExtended,
SpawnSmoke, SpawnSprite, SpawnSpriteSafe, Star, SubHorzPos, SubOffScreen and SubVertPos behave the
same (only labels, comments, layout and the spellings noted above differ). The corrections under
"Measured against PIXI's" apply to the 1.40 versions unchanged.

## Measured against PIXI 1.40's (2026-10-20)

`PixiRoutineTests` runs ours against each PIXI version on the machine that has the routine: 1.2.x
(BigEye's copy) and 1.40 (Secret's). Ours match 1.2.x for 26 routines and 1.40 for all 50 but the
deliberate differences below. Corrections to the 1.40 contracts above, found that way and measured
black-box:

- **BounceGenericDraw:** the property byte is `$1901,x` ORed with the byte at direct page `$64`
  (SMW's sprite priority), not with the value `$64`. With RAM cleared it is `$1901,x` exactly.
- **BounceSetMarioSpeed, BounceSetSpriteSpeed:** C is from comparing the X speed with `$80`, but N
  and Z are from X (the slot is restored last), not from that compare.
- **BounceSetSpeed:** N and Z are from X likewise; C and V from the add.
- **MinorExtendedGetDrawInfo, ClusterGetDrawInfo, erasing:** just above the screen, N and Z come
  from A (the `$F0-$FF` returned); off a side, from the test against `$70`.
- **ExtendedGetDrawInfo:** erased for being more than a screen above or below, C = 0.

Where ours is deliberately not PIXI 1.40's (each has a test of its own):

- **ExtendedGetDrawInfo** is 1.40's, so it is not 1.2.x's (whose vertical test adds the camera's
  high byte).
- **MinorExtendedGetDrawInfo** gives each slot SMW's OAM index (`$028B78 + x`); PIXI's reads one
  byte early, so its slot 0 shares slot 5's. Everything else in it matches PIXI's.
- **SpinningCoinGetDrawInfo** compares the coin with the camera as SMW's own coins do (`$029A08`):
  tested against SMW's coin code itself, which it matches in every trial (drawn, erased or left,
  and where it is drawn, the same place and OAM index). PIXI's compares the wrong camera bytes and
  almost never draws.
- **SpawnMinorExtendedGeneric** exists: PIXI 1.40's `_header.asm` files call it, but PIXI ships it
  only as `Spawn/Generic.asm` (`%SpawnGeneric()`). Ours has both names.

Found on the way, in Pipe Dream's build: routines called only from a `_header.asm` were never
inserted (the build scanned only the sprites), and our `defines.asm` defined `%LDE()`, which PIXI
1.40's headers define themselves. Both fixed; a sprite using PIXI 1.40's `sprites/_header.asm`
spawn macros now builds against our routines.
