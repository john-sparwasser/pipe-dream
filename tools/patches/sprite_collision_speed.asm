; Sprite collision speed patch for vanilla SMW (opt-in; apply with Asar over any ROM that leaves
; $019441-$01953B alone, as Lunar Magic does):  asar sprite_collision_speed.asm rom.smc
;
; Sprite <-> block collision looks up a Map16 tile twice per ground sprite per frame (the side
; point, the floor/ceiling point) through $019441. Measured in the interpreter this was ~240
; cycles a call and a fifth of a six-Koopa frame. Same inputs, same outputs, less work:
;
;  - The horizontal-level block ($0194BF) is rewritten: the 16-bit "past the bottom of the level"
;    test becomes a `BEQ` on the high byte, and the tile-generate test that picked one of two
;    pointer tables twice is one test and an index offset (the two tables sit $10 apart).
;  - The JSL to $00F545 (POW switch recolouring, muncher/coin swap) is skipped when it could not
;    do anything: it only acts on tiles $29, $2B and $EC-$FB with a zero high byte, and on $2F and
;    $32 with a non-zero one; for every other tile it returns A unchanged (the high byte, or 0).
;
; The vertical-level block, the out-of-level exit and the entry code are left exactly as they were.
; The tile fetch moves to the 65 spare bytes at $01FFBF; $019523, where the vertical block's JSR
; lands, jumps there. Everything the routine leaves behind is kept: A, Y = $0F, the flags of CMP #0, $0F, $00, $01,
; $05-$07, $0A-$0D, $1693 (and $1694 on the way out). Two things differ: the hidden high byte of
; the accumulator (B), which vanilla's 16-bit LDA $0C leaves $0D in, and the overflow flag, which
; vanilla's $00F545 leaves from its SBC. The three callers are 8-bit, load A before they use it, and
; branch only on Z. Tested in src/tests/SpriteCollisionPatchTests.cs.

lorom

!i #= 0                                  ; the spare bytes at the end of bank 1 must still be spare
while !i < 65
    assert read1($01FFBF+(!i)) == $FF, "the spare bytes at $01FFBF are in use"
    !i #= !i+1
endwhile

OutOfLevel = $0194B4                     ; vanilla's exit, kept: no tile, no push

; ---- horizontal level: vanilla's block at $0194BF, which BEQ at $019453 reaches ----
org $0194BF
Horizontal:
    LDA $D8,x                            ; Y of the point
    CLC
    ADC.w $90F7,y
    STA $0C
    AND.b #$F0
    STA $00
    LDA.w $14D4,x
    ADC.b #$00
    STA $0D
    BEQ .YIn                             ; the level is $1B0 tall: high byte 0 is always inside
    CMP.b #$01
    BNE OutOfLevel
    LDA $0C
    CMP.b #$B0
    BCS OutOfLevel
.YIn:
    LDA $E4,x                            ; X of the point
    CLC
    ADC.w $90BA,y
    STA $0A
    STA $01
    LDA.w $14E0,x
    ADC.b #$00
    STA $0B
    BMI OutOfLevel
    CMP $5D
    BCS OutOfLevel
    LDA $01
    LSR : LSR : LSR : LSR
    ORA $00
    STA $00
    LDX $0B
    LDY.w $185E                          ; tile generate: the other pointer tables are $10 on
    BEQ +
    TXA
    CLC
    ADC.b #$10
    TAX
+   LDA.l $00BA60,x
    CLC
    ADC $00
    STA $05
    LDA.l $00BA9C,x
    ADC $0D
    STA $06
    JMP.w FetchTile
assert pc() <= $019523, "horizontal block ends at $", hex(pc())
padbyte $FF
pad $019523

; ---- where the vertical block's JSR lands ----
org $019523
    JMP.w FetchTile
padbyte $FF
pad $01953C

; ---- the tile at [$05] (low plane $7E, high plane $7F), recoloured only where $00F545 would ----
org $01FFBF
FetchTile:
    LDA.b #$7E
    STA $07
    LDX.w $15E9
    LDA [$05]
    STA.w $1693
    TAY                                  ; the tile
    INC $07
    LDA [$05]                            ; A = the high byte
    BEQ .HighZero
    CPY.b #$32 : BEQ .Recolour
    CPY.b #$2F : BEQ .Recolour
.Done:
    LDY $0F
    CMP.b #$00
    RTS
.HighZero:                               ; $29-$2B and $EC-$FB are the ones it acts on
    CPY.b #$29 : BCC .Done
    CPY.b #$2C : BCC .Recolour
    CPY.b #$EC : BCC .Done
    CPY.b #$FC : BCS .Done
.Recolour:
    JSL $00F545
    BRA .Done
assert pc() <= $020000, "fetch ends at $", hex(pc())
