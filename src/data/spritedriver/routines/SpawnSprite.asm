; Spawn sprite A (C set: a custom one) at sprite X's position offset by $00, $01 (signed), with
; X speed $02 and Y speed $03. In the first free slot from 0B down, whatever the level's sprite
; memory allows. Status 1, so its INIT runs next; nothing else is set.
; 8-bit A/X/Y, X = the parent; DB on low RAM. Returns C clear and Y = the new slot, or C set and
; Y = $FF when none is free; X and $00-$03 kept.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md around SMW's $07F7D2.
    LDY #$0B
    XBA                         ; the number waits in B; the first free slot, from there down
-   LDA $14C8,y
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
    STA $9E,x
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
    STA $14C8,x
    TXY
    PLX
    LDA $E4,x                   ; at the parent, offset by $00 and $01 (signed)
    CLC
    ADC $00
    STA $00E4,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $14E0,x
    STA $14E0,y
    LDA $D8,x
    CLC
    ADC $01
    STA $00D8,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $14D4,x
    STA $14D4,y
    LDA $02
    STA $00B6,y
    LDA $03
    STA $00AA,y
    CLC
    RTL
