; Spawn extended sprite B (the accumulator's high byte) at $04-$05 (X), $06-$07 (Y), offset by $00,
; $01 (signed), with X speed $02 and Y speed $03, in the first free extended slot from 7 down (8
; and 9 are the player's fireballs): C clear and Y = the slot, or C set and Y = $FF.
; 8-bit A/X/Y; X and $00-$07 kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDY #$07
-   LDA $170B|!addr,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $170B|!addr,y
    LDA $04
    CLC
    ADC $00
    STA $171F|!addr,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $1733|!addr,y
    LDA $06
    CLC
    ADC $01
    STA $1715|!addr,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $07
    STA $1729|!addr,y
    LDA $02
    STA $1747|!addr,y
    LDA $03
    STA $173D|!addr,y
    CLC
    RTL
