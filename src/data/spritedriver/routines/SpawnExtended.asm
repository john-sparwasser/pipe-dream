; Spawn extended sprite A at sprite X's position offset by $00, $01 (signed), with X speed $02
; and Y speed $03, in the first free extended slot from 7 down (8 and 9 are the player's
; fireballs). Not from a parent off the screen or being eaten. C clear and Y = the slot; or C
; set, Y = $FF when none is free (Y kept when the parent cannot). 8-bit A/X/Y, X = the parent,
; kept. DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    XBA                         ; the number waits in B
    LDA !15A0,x
    ORA !186C,x
    ORA !15D0,x
    BEQ +
    SEC
    RTL
+   LDY #$07
-   LDA $170B|!addr,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $170B|!addr,y
    LDA !E4,x
    CLC
    ADC $00
    STA $171F|!addr,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC !14E0,x
    STA $1733|!addr,y
    LDA !D8,x
    CLC
    ADC $01
    STA $1715|!addr,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC !14D4,x
    STA $1729|!addr,y
    LDA $02
    STA $1747|!addr,y
    LDA $03
    STA $173D|!addr,y
    CLC
    RTL
