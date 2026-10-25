; Spawn score sprite B (the accumulator's high byte) at $04-$05 (X), $06-$07 (Y), offset by $00,
; $01 (signed), rising at $30: in the first free score slot from 5 down, or, with none free, the
; next one round ($18F7), as SMW's $02AD34 chooses. Returns Y = the slot. 8-bit A/X/Y; X and
; $00-$07 kept. DB on low RAM. Pipe Dream's, MIT, from SMW's $02AD34 and
; reference/PIXI_ROUTINES.md.
    LDY #$05
-   LDA $16E1|!addr,y
    BEQ .found
    DEY
    BPL -
    DEC $18F7|!addr
    BPL +
    LDA #$05
    STA $18F7|!addr
+   LDY $18F7|!addr
.found
    XBA
    STA $16E1|!addr,y
    LDA $04
    CLC
    ADC $00
    STA $16ED|!addr,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $16F3|!addr,y
    LDA $06
    CLC
    ADC $01
    STA $16E7|!addr,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $07
    STA $16F9|!addr,y
    LDA #$30
    STA $16FF|!addr,y
    RTL
