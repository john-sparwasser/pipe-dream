# SMW System Notes — Boot, Frame Loop, Interrupts, Banks

How SMW actually runs, traced from the disassembly (2026-07-24). Addresses are SNES
(`$BBAAAA`), matching labels in the bank files. Companion docs: `SNES_HARDWARE_NOTES.md`
(registers + 65816 idioms), `LEVEL_PIPELINE_NOTES.md` (level data → tiles),
`SPRITE_ENGINE_NOTES.md` (sprite engine + redundancy analysis).

## 1. Boot (RESET → main loop)

Vector: emulation RESET `$00FFFC` → `EmuRESET` $008000.

1. $008000–$008007: `SEI`, kill NMI/IRQ/auto-joypad (`STZ $4200`), HDMA off, DMA off.
2. $00800A–$008013: clear all four APU I/O ports ($2140–$2143).
3. $008016: force blank (`#$80 → $2100`).
4. $00801B: `CLC : XCE` — leave 6502 emulation. `REP #$38` (16-bit A/X/Y, binary mode),
   direct page = $0000 (`TCD`, the only TCD in the ROM), stack = $01FF.
5. $008027–$008050: **generates the OAM-clear routine into WRAM at $7F8000** (see §5).
6. $008052: SPC700 engine upload (§2). Then game mode = 0 (`STZ $0100`), RAM clear
   (`ClearStack` $008A4E), sample upload, `#$03 → $2101` (OBJ base/size), enter main loop.

## 2. SPC700 upload protocol (`UploadSPCEngine` $0080E8 / loop $008079)

The famous $2140-handshake, as implemented:
- Wait until the SPC boot ROM reports ready: spin until `$2140` == `#$BBAA` ($00807F).
- Write `#$CC` start token, target SPC address to $2142/$2143, counter to $2140/$2141.
- Per byte ($00808D–$0080A8): data → $2141, counter → $2140, then spin `CMP $2140` until
  the SPC echoes the counter back, `INC` counter, repeat.
- Multi-block: counter `ADC #$03`, skip zero; overflow flag decides next-block vs done.
- Finish: clear $2140–$2143. Sources: engine $0E8000, samples $0F8000, music banks
  $0E98B1/$0EAED6/$03E400. `StrtSPCMscUpld` $00811D writes `#$FF → $2141` = "jump to it".
- Wrapped in `SEI`/`CLI` (`UploadDataToSPC` $0080F7) — the handshake is timing-sensitive.

## 3. The frame: main loop + NMI + IRQ

**Main loop** ($008069–$008077): `INC $13` (frame counter) → `GetGameMode` $009322 →
`STZ $10` → spin `LDA $10 : BEQ` until NMI sets `$10`. **No WAI** — frame sync is a
RAM-flag busy-wait. All game logic runs here, on the main thread, outside interrupts.
`$14` (FrameCounterB) is incremented inside per-mode handlers, not the main loop — it
freezes when the game logically pauses while `$13` keeps counting.

**NMI** (`NMIStart` $00816A, vector $00FFEA):
1. Push state, DB=$00, ack via $4210.
2. Flush sound mirrors $1DF9–$1DFC/$1DFF → APU ports $2140–$2143, then zero them —
   this is how every music/SFX request reaches the SPC (write the mirror, NMI delivers).
3. Force blank, HDMA off, copy window/color-math mirrors ($41–$44 → $2123–$2125/$2130).
4. **Lag detection** ($0081DA): if `$10` is still 0, the main loop didn't finish this
   frame — skip the heavy work (DMA, status bar, controller) and only update scroll
   registers. This is why lag frames don't glitch the display.
5. Non-lag DMA order: `MoreDMA` $00A488 (animated tiles/CGRAM queue), `DrawStatusBar`
   $008DAC, `MarioGFXDMA` $00A300, `LoadScrnImage` $0085D2 (stripe image queue),
   `DoSomeSpriteDMA` $008449 (OAM), `ControllerUpdate` $008650.
6. Scroll registers from mirrors: BG1 $210D/$210E ← $1A–$1D (+$1888 layer-1 Y disp),
   BG2 $210F/$2110 ← $1E–$21, BG3 zeroed (IRQ sets it, see below).
7. Mode-7 path (gated on `$0D9B` bit 7, i.e. boss rooms): matrix A–D $211B–$211E and
   center $211F/$2120 refreshed from mirrors $2A–$35 every frame.
8. Re-enable: `#$A1` (or `#$81`) → $4200, restore regs, RTI.

**IRQ** (`IRQHandler` $008374, vector $00FFEE) — **the status-bar raster split.**
SMW does not use HDMA for the status bar; it programs a V-count timer IRQ
($4209/$420A, line ≈ `#$24`, adjusted by layer-1 Y disp at $0083D2). When it fires:
ack via $4211, `WaitForHBlank` $00843B (`BIT $4212` spin + a deliberate `LDY #$20`
cycle-burn loop), then mid-frame rewrites: BG3 scroll $2111/$2112 ← $22–$25, BG mode
$2105 ← `$3E`, color math $2131 ← `$40`, and `SETL1SCROLL` $008416 re-points BG1
($2107/$210B/$210D/$210E). $4200 toggles between `#$81`/`#$A1` control whether another
split fires. `$11` counts the split stage.

## 4. Game-mode dispatch

`RAM_GameMode` $0100, dispatched by `GetGameMode` $009322 via `JSL ExecutePtr` + inline
`.dw` table (`Ptrs009329`). Modes advance with `INC $0100`. The interesting ones:

| Mode | Handler | What |
|---|---|---|
| 00–01 | $009391/$00940F | "Nintendo Presents" |
| 02,05,0B,0D,15 | $009F6F | generic fade steps |
| 03–06 | $0096AE/$009A8B/$00941B | title screen load/circle FX |
| 07 | $009C64 | title screen |
| 08–0A | $009CD1/$009B1A/$009DFA | file select / erase / player select |
| 0C | `LoadMapGameMode` $00A087 | load overworld (JSLs into bank 04) |
| 0E | $00A1BE → `JSL $048241` | **on overworld** (`GameMode_0E_Prim`, bank 04) |
| 10–12 | $00968E/`MarioStartGameMode` $0096D5/$00A59C | OW→level, MARIO START, level prep |
| 14 | `InLevelGameMode` $00A1DA | **in level** |
| 16–17 | $009750/$009759 | game over |
| 18–28 | various | credits / cutscenes / ending (JSLs into bank 0C) |

**In-level frame anatomy** (mode 14, core block $00A28A–$00A2F0): `JSL $7F8000` (OAM
clear) → screen scroll $00F6DB → `JSL $05BC00` (layer-2/scroll commands) → status-bar
RAM build $008E1A → Mario GFX assemble $00E2BD → latch Mario X/Y to $D1/$D3 → Mario
state/physics/interaction `CODE_00C47E` → **sprites `JSL $01808C`** → misc timers
$028AB1 → joypad compress $008494. Mario's position lives at $94/$96.

## 5. DMA channel allocation

| Channel | Regs | Used for |
|---|---|---|
| 0 | $4300 | OAM: `DoSomeSpriteDMA` $008449 — $0220 bytes from shadow OAM $0200 → $2104, every frame |
| 1 | $4310 | VRAM tilemap/GFX uploads: status bar ($008DAC), stripe/screen images ($0085D2), level GFX ($008D13), overworld ($04D754) |
| 2 | $4320 | Palette → CGRAM and Mario GFX → VRAM: `MarioGFXDMA` $00A300, animated-tile queue `MoreDMA` $00A488 |
| 5,6,7 | $4350/60/70 | HDMA (setup $0092B2, enable mask `#$E0` → mirror $0D9F → $420C each NMI). Ch7 = the window-shape HDMA (iris/spotlight): table built per-scanline in WRAM $04A0 ($009250–$0092A0) |

**The $7F8000 trick**: at boot, SMW writes an *unrolled routine* into WRAM — `LDA #$F0`
followed by ~128 `STA $02xx` instructions and an `RTL` — then `JSL $7F8000` every frame
to set every shadow-OAM Y to $F0 (offscreen). Code generated into RAM to avoid a loop.

## 6. Controller input

Auto-joypad read ($4200 bit 0): NMI-time `ControllerUpdate` $008650 reads $4218/$4219
(P1) and $421A/$421B (P2). Newly-pressed = `EOR prev : AND current` (the classic edge
detect). Game-facing bytes: `$15` held (dpad+A/B), `$16` pressed, `$17` held dpad,
`$18` pressed ($0DA2–$0DA9 hold the raw current/previous pairs). Manual $4016 serial
read exists only in the file-select two-player sync path ($009A6F).

## 7. Bank-by-bank guide (mysteries resolved)

| Bank | Contents |
|---|---|
| 00 | Boot, main loop, NMI/IRQ, game-mode dispatch, DMA engine, SPC upload, controller, status bar, Mario state/interaction ($00C47E) + Mario OAM assemble ($00E2BD), block interaction |
| 01 | Core sprite engine: main loop $0180A7, status dispatch, 201-entry init/main tables, shared sprite subroutines, most enemy mains |
| 02 | More sprite mains, generators ($02B035+), cluster/extended sprites, `LoadSprFromLevel` spawn streamer $02A7FB, `FindFreeSprSlot` $02A9E4 |
| 03 | Sprite mains $A1–$C8 via `Bnk3CallSprMain` $03A118, bosses (Reznor/Big Boo/Bowser fight), bank-local utility copies |
| 04 | Overworld engine: game mode 0E ($048241), player path/movement logic ($049260, best-commented part), event/path-reveal engine ($04EAA0), level-name strings (~$0499xx), OW border tilemap ($04A400), self-contained OW-sprite engine ($04F675+, jump table $04F826) |
| 05 | Level loading: header decode, `LoadLevel` $0583AC, object stream decode $0585FF, layer 2/3 systems, scroll commands (`Ptrs05BC87/BCF0`), the 512-level pointer tables ($05E000/$05E600/$05EC00) |
| 06 | **(was "Unknown contents")** Level Layer-1 object data — 143 `DATA_06xxxx` streams, zero code. Targets of bank 05's `Layer1Ptrs`; a few levels overflow into bank 07. Tail is freespace |
| 07 | Overflow level data, sprite tweaker tables ($07F26C–$07F659), `InitSpriteTables` $07F7D2, sine table $07F7DB, misc sprite support |
| 08–0B | Graphics (GFX00–GFX31 4bpp/3bpp data; no code labels — not split into files) |
| 0C | **(was just "Credits")** Half credits engine, half overworld data: $0C8000–$0C938C OW Map16 definitions (read by bank 04's event engine), $0C938D–$0CD86F cutscene/credits/enemy-parade engine (dispatched by `$143E` scroll-sprite number through `Ptrs0CC9xx`), cutscene text $0CBE85+, $0CD86F–end OW Layer-1 tilemaps (the $0CF7DF block is MVN'd to $7EC800) |
| 0D | Map16 16×16 definitions ($0D8000–$0DA098: 8 bytes/tile = 4×(chr,prop)), the object draw-routine dispatch + tile-writing primitives ($0DA100+), enemy-name credits strings ($0DF300–$0DFE8D), freespace |
| 0E | Music data + SPC engine source block |
| 0F | Sample data |

## 8. Load-bearing RAM quick reference

| Addr | What |
|---|---|
| $10 | frame-ready flag (main loop spins on it; NMI sets it; lag detector) |
| $11 | IRQ split stage |
| $13 / $14 | frame counter (always) / frame counter B (freezes on pause) |
| $15–$18 | controller held/pressed (see §6) |
| $19 | Mario powerup |
| $1A–$21 | BG1/BG2 scroll mirrors (screen boundary) |
| $22–$25 | BG3 (status bar) scroll mirrors, written by IRQ |
| $2A–$35 | Mode-7 matrix mirrors |
| $3E / $40–$44 | BG mode mirror / color-math + window mirrors |
| $65–$67 / $68–$6A | Layer 1 / Layer 2 data pointer |
| $94/$96 | Mario X/Y |
| $0100 | game mode |
| $0200–$041F | shadow OAM (DMA'd whole every frame) |
| $0D9B | special/Mode-7 level type (boss rooms) |
| $0D9F | HDMA enable mirror → $420C |
| $0DAE | brightness mirror → $2100 (fades walk this) |
| $1462/$1464 etc. | layer positions (see level notes) |
| $1692 | sprite memory setting (from sprite data header!) |
| $1925 | level mode |
| $1DF9–$1DFC | sound request mirrors (write here; NMI ships them) |
| $7EC800/$7FC800 | level Map16 buffer lo/hi (also OW layer-1 buffer on the map) |
