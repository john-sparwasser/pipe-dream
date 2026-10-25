; Move bounce sprite X by its speeds, Y then X, 4.4 fixed point as SMW's $02B526. The fractions
; are PIXI's: X's in $16B9,x, Y's in $16BD,x (SMW's own bounce code has them the other way).
; Returns Y = $00/$FF, the X speed's sign, A = its new X high byte. 8-bit A/X/Y, X = the
; bounce slot, kept. DB on low RAM. Pipe Dream's, MIT, from SMW's $02B526 and
; reference/PIXI_ROUTINES.md.
    LDA $16B1|!addr,x                 ; Y
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $16BD|!addr,x
    STA $16BD|!addr,x
    PHP
    LDY #$00
    LDA $16B1|!addr,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $16A1|!addr,x
    STA $16A1|!addr,x
    TYA
    ADC $16A9|!addr,x
    STA $16A9|!addr,x
    LDA $16B5|!addr,x                 ; X
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $16B9|!addr,x
    STA $16B9|!addr,x
    PHP
    LDY #$00
    LDA $16B5|!addr,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $16A5|!addr,x
    STA $16A5|!addr,x
    TYA
    ADC $16AD|!addr,x
    STA $16AD|!addr,x
    RTL
