; Spawn smoke B (the accumulator's high byte) at $04 (X), $06 (Y) offset by $00, $01 (8-bit, as
; smoke is), timer $02, in the first free smoke slot from 3 down: C clear and Y = the slot, or C
; set and Y = $FF. 8-bit A/X/Y; X and $00-$07 kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDY #$03
-   LDA $17C0|!addr,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $17C0|!addr,y
    LDA $04
    CLC
    ADC $00
    STA $17C8|!addr,y
    LDA $06
    CLC
    ADC $01
    STA $17C4|!addr,y
    LDA $02
    STA $17CC|!addr,y
    CLC
    RTL
