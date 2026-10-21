; Draw bounce sprite X as one 16x16 tile, A: by BounceGetDrawInfo, with $1901,x's properties and
; the sprites' priority ($64). Returns C clear either way; drawn, Y = its OAM index / 4 and A its
; size byte.
; 8-bit A/X/Y, X = the bounce slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02922D and reference/PIXI_ROUTINES.md.
    STA $02
    %BounceGetDrawInfo()
    BCC .out
    LDA $00
    STA $0200,y
    LDA $01
    STA $0201,y
    LDA $02
    STA $0202,y
    LDA $1901,x
    ORA $64
    STA $0203,y
    TYA
    LSR
    LSR
    TAY
    LDA $03
    STA $0420,y
    CLC
.out
    RTL
