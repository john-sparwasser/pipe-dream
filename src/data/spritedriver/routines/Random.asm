; A random number from 0 to A (the low byte), by SMW's generator ($01ACF9): its $148D, scaled.
; Any widths; P, X, Y and B come back as they went in, N and Z from X. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md around SMW's $01ACF9.
    PHX
    PHP
    SEP #$30
    XBA                         ; B kept
    PHA
    XBA
    PHA
    JSL $01ACF9
    PLA
    CMP #$FF                    ; 0-FF: the number itself
    BEQ .all
    INC                         ; else the high byte of number x (max + 1)
    %mul_a()
    LDA $148D|!addr
    %mul_b()
    NOP #4
    LDA !MulHi
    BRA .done
.all
    LDA $148D|!addr
.done
    XBA
    PLA
    XBA
    PLP
    PLX
    RTL
