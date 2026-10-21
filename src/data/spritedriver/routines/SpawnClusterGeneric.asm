; Spawn cluster sprite B (the accumulator's high byte, as PIXI's generic spawns take it) at
; $02-$03 (X), $04-$05 (Y), offset by $00, $01 (signed), in the first free cluster slot from 19
; down: C clear and Y = the slot, or C set and Y = $FF. 8-bit A/X/Y; X and $00-$07 kept.
; DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDY #$13
-   LDA $1892,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $1892,y
    LDA $02
    CLC
    ADC $00
    STA $1E16,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $03
    STA $1E3E,y
    LDA $04
    CLC
    ADC $01
    STA $1E02,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $1E2A,y
    CLC
    RTL
