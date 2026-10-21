; For spinning coin X's graphics, by its layer's camera ($17E4,x; copied to $02-$03 Y and $04-$05
; X), as SMW's coins decide it ($029A08): off the screen up or down, not drawn (C clear, A that
; high byte, Y = 4 x the layer); its X on screen $F8 or more, erased ($17D0,x = 0; C clear, A that
; X); else C set, $00 its X on screen, $01 its Y, Y = A = its OAM index (SMW's, $0299E9).
; 8-bit A/X/Y, X = the coin's slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $029A08 and reference/PIXI_ROUTINES.md. PIXI 1.40's stores the
; camera as two words but compares by SMW's byte offsets, so tests the coin's Y against the
; camera's X: this one compares what SMW does.
    LDA $17E4,x
    ASL
    ASL
    TAY
    REP #$20
    LDA $001C,y
    STA $02
    LDA $001A,y
    STA $04
    SEP #$20
    LDA $17D4,x
    CMP $02
    LDA $17E8,x
    SBC $03
    BEQ +
    CLC
    RTL
+   LDA $17E0,x
    SEC
    SBC $04
    CMP #$F8
    BCC +
    STZ $17D0,x
    CLC
    RTL
+   STA $00
    LDA $17D4,x
    SEC
    SBC $02
    STA $01
    LDA.l $0299E9,x
    TAY
    SEC
    RTL
