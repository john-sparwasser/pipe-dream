; For score sprite X's graphics, by its layer's camera ($1705,x: 0 layer 1, 1 layer 2, copied to
; $02-$03 Y and $04-$05 X): drawable when within the screen, the two tiles' 16 pixels included.
; C set: $00 its X on screen, $01 its Y, Y = A = its OAM index (SMW's, $02AD9E). C clear: A the
; high byte that failed, Y = 4 x the layer. 8-bit A/X/Y, X = the score slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02AE5B and reference/PIXI_ROUTINES.md.
    LDA $1705|!addr,x
    ASL
    ASL
    TAY
    REP #$20
    LDA $001C|!dp,y
    STA $02
    LDA $001A|!dp,y
    STA $04
    SEP #$20
    LDA $16F3|!addr,x                 ; its right edge, past the screen's?
    XBA
    LDA $16ED|!addr,x
    REP #$20
    CLC
    ADC #$000C
    SEC
    SBC $04
    SEP #$20
    XBA
    BEQ +
    CLC
    RTL
+   LDA $16F3|!addr,x                 ; its left, before the screen's?
    XBA
    LDA $16ED|!addr,x
    REP #$20
    SEC
    SBC $04
    SEP #$20
    XBA
    BEQ +
    CLC
    RTL
+   LDA $16F9|!addr,x                 ; on the screen up and down?
    XBA
    LDA $16E7|!addr,x
    REP #$20
    SEC
    SBC $02
    SEP #$20
    XBA
    BEQ +
    CLC
    RTL
+   XBA
    STA $01
    LDA $16ED|!addr,x
    SEC
    SBC $04
    STA $00
    LDA.l $02AD9E,x
    TAY
    SEC
    RTL
