; Spawn spinning coin B (the accumulator's high byte) at $04-$05 (X), $06-$07 (Y), offset by $00,
; $01 (signed), with Y speed $03, in the first free coin slot from 3 down: C clear and Y = the
; slot, or C set and Y = $FF. 8-bit A/X/Y; X and $00-$07 kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDY #$03
-   LDA $17D0,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $17D0,y
    LDA $04
    CLC
    ADC $00
    STA $17E0,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $17EC,y
    LDA $06
    CLC
    ADC $01
    STA $17D4,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $07
    STA $17E8,y
    LDA $03
    STA $17D8,y
    CLC
    RTL
