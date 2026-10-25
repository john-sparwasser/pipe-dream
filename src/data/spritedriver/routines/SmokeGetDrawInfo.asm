; For smoke sprite X's graphics, by layer 1's camera and the low bytes alone: C set, $00 its X on
; screen, $01 its Y, Y = A = its OAM index (SMW's, $0296B8); C clear, A its X on screen, when
; that is $F0 or more (nothing written). Never erases. 8-bit A/X/Y, X = the smoke slot, kept.
; DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDA $17C8|!addr,x
    SEC
    SBC $1A
    CMP #$F0
    BCC +
    CLC
    RTL
+   STA $00
    LDA $17C4|!addr,x
    SEC
    SBC $1C
    STA $01
    LDA.l $0296B8,x
    TAY
    SEC
    RTL
