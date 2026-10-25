; Whether sprite X touches one of the player's fireballs (extended slots 8 and 9, any sprite in
; them): C set and Y = 0 or 1 for the slot, else C clear and Y = $FF. X kept (N and Z from it).
; 8-bit A/X/Y, X = the slot. $00-$0C and $0F are left as the boxes. DB on low RAM.
; Pipe Dream's, MIT, from SMW's fireball box ($02A547) and reference/PIXI_ROUTINES.md.
    PHX
    LDY #$00
.slot
    LDA $170B|!addr+8,y
    BEQ .next
    LDA $171F|!addr+8,y               ; the fireball's box, SMW's
    SEC
    SBC #$02
    STA $00
    LDA $1733|!addr+8,y
    SBC #$00
    STA $08
    LDA #$0C
    STA $02
    LDA $1715|!addr+8,y
    SEC
    SBC #$04
    STA $01
    LDA $1729|!addr+8,y
    SBC #$00
    STA $09
    LDA #$13
    STA $03
    PHY
    JSL $03B69F                 ; the sprite's
    JSL $03B72B
    PLY
    BCS .hit
.next
    INY
    CPY #$02
    BCC .slot
    LDY #$FF
    CLC
.hit
    PLX
    RTL
