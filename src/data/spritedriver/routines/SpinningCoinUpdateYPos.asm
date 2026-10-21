; Move spinning coin X by its Y speed, 4.4 fixed point. Returns Y = $00/$FF, the speed's sign,
; A = its new Y high byte. 8-bit A/X/Y, X = the coin's slot, kept. DB on low RAM.
; Pipe Dream's, MIT: SMW's $02B58E, returning long.
    LDA $17D8,x
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $17DC,x
    STA $17DC,x
    PHP
    LDY #$00
    LDA $17D8,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $17D4,x
    STA $17D4,x
    TYA
    ADC $17E8,x
    STA $17E8,x
    RTL
