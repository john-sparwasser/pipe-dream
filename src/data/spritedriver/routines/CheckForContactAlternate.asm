; Whether box 1 ($00 X, $02 Y, $04 width, $06 height) overlaps box 2 ($08, $0A, $0C, $0E),
; all 16-bit and unsigned: C set if so. Any A width; returns with 8-bit A. X, Y and RAM kept.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    REP #$20
    LDA $00                     ; X: the left one's width covers the gap
    CMP $08
    BCC +
    SEC
    SBC $08
    CMP $0C
    BCS .no
    BRA .y
+   LDA $08
    SEC
    SBC $00
    CMP $04
    BCS .no
.y
    LDA $02                     ; Y: the upper one's height covers the gap
    CMP $0A
    BCC +
    SEC
    SBC $0A
    CMP $0E
    BCS .no
    BRA .yes
+   LDA $0A
    SEC
    SBC $02
    CMP $06
    BCS .no
.yes
    SEP #$20
    SEC
    RTL
.no
    SEP #$20
    CLC
    RTL
