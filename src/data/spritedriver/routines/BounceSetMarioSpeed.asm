; The player's push from bounce block X, by its direction ($16C9,x bits 0-1): SMW's speeds
; ($0290D6 Y, $0290DA X; $80 leaves one alone). Returns Y = X, A = the X speed, C from comparing
; it with $80, N and Z from X. 8-bit A/X/Y, X = the bounce slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $0290F3 and reference/PIXI_ROUTINES.md.
    PHX
    LDA $16C9|!addr,x
    AND #$03
    TAX
    LDA.l $0290D6,x
    CMP #$80
    BEQ +
    STA $7D
+   LDA.l $0290DA,x
    CMP #$80
    BEQ +
    STA $7B
+   PLX
    TXY
    RTL
