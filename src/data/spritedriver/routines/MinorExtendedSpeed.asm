; Move minor extended sprite X along one axis by A (bits 0-1): 0 X, 4.4 fixed point; 1 X, whole
; pixels; 2 Y, 4.4; 3 Y, whole pixels. Returns Y = $00/$FF, the speed's sign, A = the new
; position's high byte. 8-bit A/X/Y, X = the slot, kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's 4.4 update ($02B560) and reference/PIXI_ROUTINES.md.
    AND #$03
    BEQ .slowx
    DEC
    BEQ .fastx
    DEC
    BEQ .slowy
    LDY #$00                    ; fast Y
    LDA $1820,x
    BPL +
    DEY
+   CLC
    ADC $17FC,x
    STA $17FC,x
    TYA
    ADC $1814,x
    STA $1814,x
    RTL
.fastx
    LDY #$00
    LDA $182C,x
    BPL +
    DEY
+   CLC
    ADC $1808,x
    STA $1808,x
    TYA
    ADC $18EA,x
    STA $18EA,x
    RTL
.slowy
    LDA $1820,x
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $1838,x
    STA $1838,x
    PHP
    LDY #$00
    LDA $1820,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $17FC,x
    STA $17FC,x
    TYA
    ADC $1814,x
    STA $1814,x
    RTL
.slowx
    LDA $182C,x
    ASL
    ASL
    ASL
    ASL
    CLC
    ADC $1844,x
    STA $1844,x
    PHP
    LDY #$00
    LDA $182C,x
    LSR
    LSR
    LSR
    LSR
    CMP #$08
    BCC +
    ORA #$F0
    DEY
+   PLP
    ADC $1808,x
    STA $1808,x
    TYA
    ADC $18EA,x
    STA $18EA,x
    RTL
