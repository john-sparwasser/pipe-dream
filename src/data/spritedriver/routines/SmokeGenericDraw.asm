; Draw smoke sprite X as one tile: $02 the tile, $03 its properties, $04 its size byte. By
; SmokeGetDrawInfo: C set when drawn, then Y = its OAM index / 4 and A = $04.
; 8-bit A/X/Y, X = the smoke slot, kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    %SmokeGetDrawInfo()
    BCC .out
    LDA $00
    STA $0200|!addr,y
    LDA $01
    STA $0201|!addr,y
    LDA $02
    STA $0202|!addr,y
    LDA $03
    STA $0203|!addr,y
    TYA
    LSR
    LSR
    TAY
    LDA $04
    STA $0420|!addr,y
    SEC
.out
    RTL
