; Speed bounce sprite X up as SMW's bounce blocks are, by its direction ($16C9,x bits 0-1):
; SMW's $0290CE into its Y speed, then $0290D2 into its X speed. Returns Y = X, A = the X speed,
; C and V from that add, N and Z from X. 8-bit A/X/Y, X = the bounce slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $029111 and reference/PIXI_ROUTINES.md.
    PHX
    LDA $16C9,x
    AND #$03
    TAX
    LDA.l $0290D2,x
    PHA
    LDA.l $0290CE,x
    PLY
    PLX
    CLC
    ADC $16B1,x
    STA $16B1,x
    TYA
    TXY
    CLC
    ADC $16B5,x
    STA $16B5,x
    PHX
    PLX
    RTL
