; Let score sprite X rise a pixel, as often as its Y speed says (under $10 every 4th frame, under
; $20 every 2nd, faster every frame: SMW's $02AD9A), until it is A pixels (C set) or 4 (C clear,
; SMW's) from the screen's top. 8-bit A/X/Y, X = the score slot, kept; DB kept (this bank's while
; it runs, so it must map low RAM). Speeds from $40 up are not defined, as in PIXI's.
; Pipe Dream's, MIT, from SMW's $02AE38 and reference/PIXI_ROUTINES.md.
    BCS +
    LDA #$04
+   STA $00
    PHB
    PHK
    PLB
    LDA $16FF|!addr,x
    LSR
    LSR
    LSR
    LSR
    TAY
    LDA $13
    AND .every,y
    BNE .done
    LDA $16E7|!addr,x
    TAY
    SEC
    SBC $1C
    CMP $00
    BCC .done
    LDA $16E7|!addr,x
    BNE +
    DEC $16F9|!addr,x
+   DEC $16E7|!addr,x
    TYA
.done
    PLB
    RTL

.every                          ; SMW's
    db read1($02AD9A), read1($02AD9B), read1($02AD9C), read1($02AD9D)
