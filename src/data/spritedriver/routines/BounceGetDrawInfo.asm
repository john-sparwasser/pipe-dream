; For bounce sprite X's graphics: $00 its X on screen, $01 its Y, $03 = $02 (16x16) or $03 (with
; the X high bit), by layer 1's camera or, with $1901,x bit 7, layer 2's. Off the screen up or
; down: C clear, A that high byte, Y = 0 or 4 (the camera's offset). Else C set, Y = A = its OAM
; index (SMW's, $0291ED). 8-bit A/X/Y, X = the bounce slot (0-3), kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $0291F8 and reference/PIXI_ROUTINES.md.
    LDA #$02
    STA $03
    LDY #$00
    LDA $1901|!addr,x
    BPL +
    LDY #$04
+   LDA $16A5|!addr,x
    SEC
    SBC $001A|!dp,y
    STA $00
    LDA $16AD|!addr,x
    SBC $001B|!dp,y
    BEQ +
    LDA #$03
    STA $03
+   LDA $16A1|!addr,x
    SEC
    SBC $001C|!dp,y
    STA $01
    LDA $16A9|!addr,x
    SBC $001D|!dp,y
    BEQ +
    CLC
    RTL
+   LDA.l $0291ED,x
    TAY
    SEC
    RTL
