; SpawnSprite, searching from slot $04 down rather than from 0B: for a caller keeping the upper
; slots free. $04 is not kept across a custom sprite's tables.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md around SMW's $07F7D2.
    LDY $04
    XBA                         ; the number waits in B; the first free slot, from there down
-   LDA !14C8,y
    BEQ .found
    DEY
    BPL -
                                ; none free: Y as the search left it ($FF from below $80)
    INX                         ; N and Z from X
    DEX
    SEC
    RTL
.found
    XBA
    PHX
    TYX
    STA !9E,x
    PHA
    JSL $07F7D2                 ; clear it and give it SMW's tables for the number (C survives)
    PLA
    BCC .vanilla
    STA !new_sprite_num,x       ; custom: the driver's tables, keeping $00-$03 across them
    REP #$20
    LDA $00
    PHA
    LDA $02
    PHA
    SEP #$20
    JSL $0187A7
    REP #$20
    PLA
    STA $02
    PLA
    STA $00
    SEP #$20
    LDA #$08
    STA !extra_bits,x
.vanilla
    LDA #$01
    STA !14C8,x
    TXY
    PLX
    LDA !E4,x                   ; at the parent, offset by $00 and $01 (signed)
    CLC
    ADC $00
    STA !E4,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC !14E0,x
    STA !14E0,y
    LDA !D8,x
    CLC
    ADC $01
    STA !D8,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC !14D4,x
    STA !14D4,y
    LDA $02
    STA !B6,y
    LDA $03
    STA !AA,y
    CLC
    RTL
