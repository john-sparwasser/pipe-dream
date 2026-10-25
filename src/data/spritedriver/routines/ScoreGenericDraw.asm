; Draw score sprite X as two 8x8 tiles side by side, in front of everything: $07/$08 the left
; and right tiles, $09/$0A their properties (| $30). By ScoreGetDrawInfo: C set when drawn, then
; Y = its OAM index / 4 and A = 0. 8-bit A/X/Y, X = the score slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02AEA5 and reference/PIXI_ROUTINES.md.
    %ScoreGetDrawInfo()
    BCC .out
    LDA $00
    STA $0200|!addr,y
    CLC
    ADC #$08
    STA $0204|!addr,y
    LDA $01
    STA $0201|!addr,y
    STA $0205|!addr,y
    LDA $07
    STA $0202|!addr,y
    LDA $08
    STA $0206|!addr,y
    LDA $09
    ORA #$30
    STA $0203|!addr,y
    LDA $0A
    ORA #$30
    STA $0207|!addr,y
    TYA
    LSR
    LSR
    TAY
    LDA #$00
    STA $0420|!addr,y
    STA $0421|!addr,y
    SEC
.out
    RTL
