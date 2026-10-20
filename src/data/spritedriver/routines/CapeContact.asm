; Whether sprite X touches the spinning cape: C set if so. Not while the cape is not spinning
; ($13E8), nor for a sprite being eaten or ignoring the cape ($15D0, $154C, $1FE2), nor one on
; the other layer than the player ($1632, which climbing flips). Does not hit the sprite.
; 8-bit A/X/Y, X = the slot, kept with Y. DB on low RAM.
; Pipe Dream's, MIT, from SMW's cape check ($0293CE) and reference/PIXI_ROUTINES.md.
    LDA $13E8
    BEQ .no
    LDA $15D0,x
    ORA $154C,x
    ORA $1FE2,x
    BNE .no
    LDA $74
    BEQ +
    LDA #$01
+   EOR $1632,x
    CMP $13F9
    BNE .no
    JSL $03B69F                 ; the sprite's box
    LDA $13E9                   ; the cape's, SMW's ($029696)
    SEC
    SBC #$02
    STA $00
    LDA $13EA
    SBC #$00
    STA $08
    LDA #$14
    STA $02
    LDA $13EB
    STA $01
    LDA $13EC
    STA $09
    LDA #$10
    STA $03
    JML $03B72B
.no
    CLC
    RTL
