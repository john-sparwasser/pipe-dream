# SNES Hardware & 65816 Idioms — as used by SMW

The esoteric SNES-specific APIs and 65816 tricks the disassembly uses without
explanation, each with real usage sites (2026-07-24). Registers appear in the listing
as raw addresses (`STA.W $2118`); direct page is always $0000, so `$00–$FF` operands
are WRAM $7E0000–$7E00FF scratch.

Myth-busting first — things SMW **never** uses, despite being famous 65816 features:
- **No WAI/STP.** Frame sync is a RAM-flag spin on `$10` ($00806B).
- **No decimal mode (SED).** Cleared once at reset (`REP #$38`); score/coin digits are
  software tables, not BCD.
- **No direct-page relocation.** One `TCD` ever ($008022, D=$0000 at reset). No PHD/PLD.
  Every register access is absolute-addressed.
- **No PEA; one PER** in the whole ROM ($00BFC2). Stack-relative addressing is unused.
- **8 MVN/MVP total** (see B7) — block moves are rare; bulk copies go through DMA.

## Part A — Register catalog (what SMW touches, and where)

### PPU ($21xx)

| Reg | Name | SMW usage |
|---|---|---|
| $2100 | INIDISP | Force blank = `#$80` (reset $008016, pre-VRAM-work $0081AA, $009385). Brightness is mirror **$0DAE**, copied every NMI ($0082B0); fades inc/dec the mirror ($009F4C fade, $009740 → black, $0097BE → full) |
| $2101 | OBSEL | Once: `#$03` at boot $008064 (OBJ name base, 8×8+16×16 sizes) |
| $2102–04 | OAM addr/data | Never written directly with tile data — OAM goes via DMA ch0. `STZ $2102/$2103` resets address before the DMA ($00844E) |
| $2105 | BGMODE | `#$09` = Mode 1 + BG3-priority — the entire game runs in Mode 1 ($0081D5, rewritten every NMI $0082F7). `#$07` = **Mode 7 only on the $0D9B-flagged boss path** ($0083FB). IRQ restores from mirror `$3E` ($0083A8) |
| $2106 | MOSAIC | Off at screen setup ($008A7C); the mosaic level-intro effect writes it at $009F6B |
| $2107–0C | map/chr bases | Set together in screen setup $008A81–$008A95. IRQ mid-frame swap: `#$59 → $2107`, `#$07 → $210B` in `SETL1SCROLL` $008418 (status bar uses different bases than the playfield) |
| $210D–12 | BG scroll | Write-twice (lo,hi). BG1 ← $1A–$1D mirrors, BG2 ← $1E–$21, every NMI ($008248–$00826D). BG3 ← $22–$25 but **from the IRQ** ($008396/$0083A0) — the status bar scrolls independently below the split |
| $2115 | VMAIN | `STZ` = +1 per low-byte write (byte streaming, $008601); `#$80` = +1 per high-byte write (word transfers, $00A328) |
| $2116/17 | VMADD | VRAM word address before every DMA (e.g. `#$67F0` Mario tiles $00A336) |
| $2118/19 | VMDATA | Fed by DMA B-address, not CPU stores; $008638 switches the DMA target to $2119 for high-byte-only tilemap property updates |
| $211A–20 | Mode 7 | Matrix A–D + center copied from mirrors $2A–$35 every NMI, center biased `ADC #$80` ($008301–$00833F). Used for boss rooms (Iggy/Larry platform, Morton/Ludwig/Roy, Bowser) |
| $2121/22 | CGRAM | Bulk palettes via DMA ch2 ($00A30B: `#$86` = color index 134, Mario's row). Manual two-write color pokes at $00A42C (fixed-color effects) |
| $2123–25 | window mask | From mirrors $41–$43 every NMI ($0081B4+) |
| $2126–2B | window pos/logic | Positions are **HDMA-driven** (ch7 table at WRAM $04A0) — the iris-in/out and dark-room spotlight. Logic regs zeroed at setup ($008A9E) |
| $212C/2D | TM/TS | Main/sub screen layer enables, per level mode from tables → mirrors $0D9D/$0D9E (see level notes §C); written at $009402, level start $058108 |
| $212E/2F | TMW/TSW | Window-on-main/sub enables ($00A120, credits $0CAB8C) |
| $2130/31 | CGWSEL/CGADSUB | Color math from mirrors $44/$40 each NMI ($0081C3/$0081D0); IRQ re-writes $2131 below the split ($0083AF) — status bar is exempt from level color math |
| $2132 | COLDATA | Fixed color: $00AE5E ORs plane bits ($20/$40/$80 = R/G/B from `DATA_00AE44`) with 5-bit intensity — sunset/cave tint effects |
| $2133 | SETINI | `STZ` at setup — no interlace/hires/overscan/EXTBG, ever |

### CPU ($42xx/$43xx)

| Reg | Name | SMW usage |
|---|---|---|
| $4200 | NMITIMEN | `#$A1` = NMI + V-IRQ + auto-joypad, `#$81` = same minus IRQ. Toggled between these to arm/disarm the status-bar split ($0082A1, $0083C3, $0083E1) |
| $4202/03 → $4216/17 | 8×8 multiply | `CODE_008B2B`: write factors, **4×NOP** (the mandatory 8-cycle latency), read product. Sprite math $02D68B (note `ASL $4216` — shifting the product in place), overworld $0494B2 |
| $4204–06 → $4214/15 | 16÷8 divide | `CODE_00CC14`: dividend, divisor, **6×NOP** (16-cycle latency), read quotient; then feeds a multiply — fixed-point scaling. Overworld pixel→tile $0482ED, $04FF73 |
| $4207–0A | H/V timers | V-count IRQ for the status-bar split: line `#$24` (or `#$AE − $1888` from the bottom) → $4209 ($008297, $0083D5) |
| $420B | MDMAEN | Kicks every DMA; >50 sites. Mask `#$01`/`#$02`/`#$04` = ch0/1/2 |
| $420C | HDMAEN | From mirror $0D9F every NMI ($0082B9); `#$E0` = ch5/6/7 |
| $4210/$4211 | RDNMI/TIMEUP | Interrupt acks ($008176 NMI, $008380 IRQ) |
| $4212 | HVBJOY | `BIT $4212 : BVS/BVC` H-blank sync in `WaitForHBlank` $00843B — timing the mid-frame raster writes. Followed by a deliberate `LDY #$20` DEY-loop cycle burn to land inside the visible window |
| $4218–1B | auto-joypad | Read in NMI ($008650); edge detect = `EOR prev : AND cur`. Manual $4016 serial read only in file-select 2P sync ($009A6F) |

## Part B — 65816 idioms

**B1. `ExecutePtr` ($0086DF) — the jump-table trampoline.** The single most load-bearing
idiom: ~70 call sites; every dispatch table in the game uses it. Convention: `JSL
ExecutePtr` with a `.dw` table *immediately after the JSL*, A = index. Mechanics:

```
STY $03            ; save Y
PLY : STY $00      ; JSL pushed (return-1); low byte → $00
REP #$30
AND #$00FF : ASL   ; index × 2 (word entries)
TAY
PLA : STA $01      ; mid+bank bytes → $01/$02; $00-$02 now = table address - 1
INY                ; +1 compensates the -1
LDA [$00],Y        ; fetch entry
STA $00
SEP #$30 : LDY $03
JMP [$0000]        ; go — never returns to the caller
```

The "return address" JSL pushed is the *table*, not code — the routine hijacks it as a
data pointer. Destroys $00–$03. `ExecutePtrLong` ($0086FA) is the 3-byte-entry variant
(index ×3 via `ASL : ADC`), used for cross-bank tables (level object routines in 0D).

**B2. Game-mode master table.** `GetGameMode` $009322 = `LDA $0100 : JSL ExecutePtr` +
40-entry table `Ptrs009329`. State machines advance by `INC $0100`.

**B3. Code generated into RAM.** Boot writes an unrolled OAM-clear routine (opcode
bytes: `A9 F0`, ~128× `8D xx xx`, `6B`) into $7F8000, called by `JSL $7F8000` every
frame. Trades 384 bytes of WRAM for loop-free clearing. Second stub at $7F812E.
There's even a 3-byte instruction-as-data at $0CD5D5 (`.db $8D,$FB,$1D` = `STA $1DFB`).

**B4. REP/SEP discipline.** `REP #$20`/`SEP #$20` toggle A width constantly; `#$10` for
X/Y; `#$30` both. The pattern to know: 16-bit reads of hardware pairs (`REP #$20 : LDA
$4216`) and the tight pairing around tables — a stray 8-bit `LDA #$xx` under `REP #$20`
would swallow the next opcode byte as its operand, so SMW brackets every width change
immediately ($008B4F is a clean example: `REP #$20 : LSR ×5 : SEP`).

**B5. XBA (117 uses).** Swap A's bytes — the workhorse for building 16-bit values from
SMW's split lo/hi tables: load hi, `XBA`, load lo (e.g. product assembly $008B49,
GetDrawInfo position math $01A37C). Heaviest in bank 04 (54 uses, overworld
coordinate packing).

**B6. PHB / PHK:PLB (567 uses) — data-bank management.** `LDA $xxxx,X` (absolute) reads
from the current *data bank* (DB). Interrupts and cross-bank JSL targets can arrive
with DB pointing anywhere, so the idiom `PHB : PHK : PLB … PLB : RTL` (set DB = this
code's bank, restore on exit) wraps every such entry point — the NMI/IRQ prologues
($00837B), all the bank-0C "Wrapper" stubs ($0C938D…), and the sprite-engine shells
($01801A–$018042). It exists because absolute addressing is 2 bytes/faster than 24-bit
`LDA.L` — set DB once instead.

**B7. MVN/MVP — all 8 of them.** Block move: A=count-1, X=src, Y=dst, operand =
dst/src banks; side effect: **DB = destination bank**, hence every site is PHB-wrapped
(bank 0D comments literally say "Preserve DBR (MVN/MVP changes this one)").
- $00A5E4 `MVN $00,$00` — WRAM table shuffle ($0703 → $0905, $1EF bytes).
- $04DC60 `MVN $7E,$0C` — **overworld map load**: $800 bytes $0CF7DF → $7EC800.
- $0DE039/$0DE04E `MVN $7E,$7E` / `$7F,$7F` — level Map16 buffer copies.
- $02F0F0 `MVP $7F,$7F` — one descending copy ($7D bytes).

**B8. Hardware-latency NOP padding.** The mul/div units need 8/16 cycles after the
last operand write; SMW pads with literal NOPs (4 after $4203, 6 after $4206) instead
of interleaving useful work. Instantly recognizable "waiting for the ALU" blocks.

**B9. Interrupt-safe mirrors, not registers.** Game code almost never touches PPU
registers directly — it writes WRAM mirrors ($0DAE brightness, $0D9F HDMA enable,
$1DF9+ sound, $22+/$40+ scroll/math) and NMI ships them during vblank. The only
mid-frame register writes are the IRQ split's, synced to H-blank via $4212 (B/A14).

**B10. What dispatch looks like when it's done wrong.** Contrast `ExecutePtr` tables
(O(1)) with `Bnk3CallSprMain` $03A118 — a ~30-deep `CMP #$xx : BNE` chain doing the
same job; see `SPRITE_ENGINE_NOTES.md` §4.
