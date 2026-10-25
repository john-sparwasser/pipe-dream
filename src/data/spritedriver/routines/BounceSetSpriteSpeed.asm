; Sprite $0F's push from bounce block X, as BounceSetMarioSpeed gives the player: SMW's speeds
; into its $AA (Y) and $B6 (X). Returns Y = X, A = the X speed, C from comparing it with $80,
; N and Z from X. 8-bit A/X/Y, X = the bounce slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $0290F3 and reference/PIXI_ROUTINES.md.
    PHX
    LDA $16C9|!addr,x
    AND #$03
    TAX
    LDY $0F
    LDA.l $0290D6,x
    CMP #$80
    BEQ +
    STA !AA,y
+   LDA.l $0290DA,x
    CMP #$80
    BEQ +
    STA !B6,y
+   PLX
    TXY
    RTL
