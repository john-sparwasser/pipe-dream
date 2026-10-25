; Move spinning coin X by its Y speed, 4.4 fixed point. Returns Y = $00/$FF, the speed's sign,
; A = its new Y high byte. 8-bit A/X/Y, X = the coin's slot, kept. DB on low RAM.
; Pipe Dream's, MIT: SMW's $02B58E, returning long.
    LDA $17D8|!addr,x
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $17DC|!addr,x
    STA $17DC|!addr,x
    PHP
    LDY #$00
    LDA $17D8|!addr,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $17D4|!addr,x
    STA $17D4|!addr,x
    TYA
    ADC $17E8|!addr,x
    STA $17E8|!addr,x
    RTL
