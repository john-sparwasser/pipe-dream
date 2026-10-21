; Sprite speed patch for vanilla SMW (and ROMs that leave these two places alone, as Lunar Magic does).
; Behaviour-preserving; apply with Asar to any SMW-based ROM:  asar sprite_speed.asm rom.smc
;
; 1. Bnk3CallSprMain ($03A118): every bank 3 sprite (Reznor, Boo stream, Mega Mole, Blargg,
;    Ninji, ...) ran this once a frame, and found its routine by comparing its number against up
;    to 36 constants in a row, 5 cycles a miss: Blargg and Bowling Ball paid ~170 cycles, a
;    Reznor ~150. Now one range check and an indexed jump, ~50 cycles for all of them. Numbers it
;    never handled ($A0, $A3-$A7, ...) still run Bowser's routine, as the end of the old chain did.
;    Frees ~150 bytes at $03A1xx.
; 2. ZeroSpriteTables ($07F721): five of its stores clear the tweaker bytes ($1656, $1662, $166E,
;    $167A, $1686), which LoadTweakerBytes overwrites the instant after in InitSpriteTables (the
;    only caller). The one ZeroSpriteTables entry point has no other caller. Skipping them saves
;    22 cycles a spawn. The code after (incl. $07F785) is untouched, so hooks there still work.

lorom

; ---- 1. bank 3 dispatch ----
org $03A118
Bnk3CallSprMain:
    PHB : PHK : PLB
    LDA $9E,x
    SEC : SBC.b #$A0
    CMP.b #$29 : BCS .Outside           ; $A0-$C8 are the table's
    ASL : TAY
    PEA.w .Done-1                       ; the routine's RTS comes back here
    LDA.w Targets+1,y : PHA             ; then RTS jumps to the routine (its address-1)
    LDA.w Targets,y : PHA
    RTS
.Done:
    PLB
    RTL

.Outside:                               ; the four below $A0
    LDA $9E,x
    CMP.b #$1B : BNE + : JSR.w $8012 : PLB : RTL : +    ; Football
    CMP.b #$51 : BNE + : JSR.w $C34C : PLB : RTL : +    ; Ninji
    CMP.b #$7A : BNE + : JSR.w $C816 : PLB : RTL : +    ; Firework
    CMP.b #$7C : BNE + : JSR.w $AC97 : PLB : RTL : +    ; Princess Peach
    JMP.w $A259                         ; BowserFight, which does its own PLB : RTL

Bowser:                                 ; BowserFight as a JSR, for the table's unused numbers
    JSL $03DFCC
    JSR.w $A279
    JSR.w $B43C
    RTS

Targets:                                ; sprite number $A0 ... $C8
    dw Bowser-1,$B163-1,$B2A9-1,Bowser-1,Bowser-1; A0 A1 BowlingBall A2 MechaKoopa A3 A4
    dw Bowser-1,Bowser-1,Bowser-1,$9F38-1,$9890-1; A5 A6 A7 A8 Blargg A9 Reznor
    dw $96F6-1,$9517-1,$9423-1,$9423-1,$9065-1; AA Fishbone AB Rex AC AD WoodenSpike AE FishinBoo
    dw Bowser-1,$8F7A-1,$9284-1,$9214-1,$8EEC-1; AF B0 BooStream B1 EatBlock B2 FallingSpike B3 StatueFireball
    dw Bowser-1,Bowser-1,$8F75-1,$8C2F-1,$8C2F-1; B4 B5 B6 ReflectingFireball B7 B8 CarrotTopLift
    dw $8D6F-1,$8DBB-1,$8E79-1,$8A3C-1,$8958-1; B9 InfoBox BA TimedLift BB GreyCastleBlock BC BowserStatue BD SlidingKoopa
    dw $88A3-1,$8770-1,$86FF-1,$85F6-1,$84CA-1; BE Swooper BF MegaMole C0 GrayLavaPlatform C1 FlyingTurnBlocks C2 Blurp
    dw $852F-1,$8454-1,$8087-1,$C4DC-1,$C30F-1; C3 PorcuPuffer C4 GreyFallingPlat C5 BigBooBoss C6 DarkRoom C7 InvisMushroom
    dw $C1F5-1; C8 LightSwitch
assert pc() <= $03A258                          ; the old chain ended at $03A257; BowserFight ($03A259) stays
padbyte $FF
pad $03A258

; ---- 2. dead stores ----
org $07F767
    BRA ZeroRest                       ; over the five tweaker-byte STZs (15 bytes, $07F767-$07F775)
    padbyte $FF
    pad $07F776
ZeroRest:
