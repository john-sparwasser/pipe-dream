; Hurt the player if extended sprite X touches them, by a 10x10 box 3 pixels in from its
; position: SMW's $02A469 (a star puffs the sprite away, Yoshi runs off, or the player is hit).
; C clear if not touching. 8-bit A/X/Y, X = the extended slot, kept. DB on low RAM, kept.
; Pipe Dream's, MIT, from SMW's extended sprite contact ($02A3F6) and reference/PIXI_ROUTINES.md.
; Unlike PIXI's, $02A469 runs with DB 2, as in SMW: PIXI's leaves DB 0, so Yoshi runs off at a
; speed read from bank 0 rather than SMW's table.
    LDA $171F,x
    CLC
    ADC #$03
    STA $04
    LDA $1733,x
    ADC #$00
    STA $0A
    LDA $1715,x
    CLC
    ADC #$03
    STA $05
    LDA $1729,x
    ADC #$00
    STA $0B
    LDA #$0A
    STA $06
    STA $07
    JSL $03B664                 ; the player's box
    JSL $03B72B
    BCC .out
    PHB                         ; $02A469 reads bank 2 and ends in RTS: DB 2, and its RTL
    LDA #$02
    PHA
    PLB
    PHK
    PEA.w .back-1
    PEA.w $B889-1
    JML $02A469
.back
    PLB
.out
    RTL
