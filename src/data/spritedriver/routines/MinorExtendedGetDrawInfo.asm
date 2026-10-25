; For minor extended sprite X's graphics, by layer 1's camera: erased ($17F0,x = 0, C clear) when
; just above the screen ($E0-$EF up) or well off a side; else C set, $00 its X on screen, $01 its
; Y, $02 = 1 when off by a screen but near its edge, Y = A = its OAM index (SMW's, $028B78).
; 8-bit A/X/Y, X = the slot (0-11), kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md. PIXI 1.40's reads SMW's OAM index
; table a byte early ($028B77), so its slot 0 shares slot 5's index: this one reads SMW's.
    LDA $17FC|!addr,x
    SEC
    SBC $1C
    STA $01
    CLC
    ADC #$10
    CMP #$F0
    BCS .above
    STZ $02
    LDA $1808|!addr,x
    SEC
    SBC $1A
    STA $00
    LDA $18EA|!addr,x
    SBC $1B
    BEQ .on
    LDA $00
    CLC
    ADC #$38
    CMP #$70
    BCS .erase
    INC $02
.on
    LDA.l $028B78,x
    TAY
    SEC
    RTL
.above                          ; just above the screen: N and Z from A, as PIXI's
    STZ $17F0|!addr,x
    CLC
    ORA #$00
    RTL
.erase                          ; off a side: N and Z from that test
    STZ $17F0|!addr,x
    CLC
    RTL
