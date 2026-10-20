; Whether extended sprite X touches the cape, by the box the caller gives: $00 X offset, $01
; width, $02 Y offset, $03 height (offsets unsigned). C set if so. Checks nothing else: the
; caller sees to the cape spinning ($13E8). 8-bit A/X/Y, X = the extended slot, kept with Y.
; $00-$0C and $0F are left as the boxes. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02A51C and $029696 and reference/PIXI_ROUTINES.md.
    LDA $171F,x                 ; its box
    CLC
    ADC $00
    STA $04
    LDA $1733,x
    ADC #$00
    STA $0A
    LDA $01
    STA $06
    LDA $1715,x
    CLC
    ADC $02
    STA $05
    LDA $1729,x
    ADC #$00
    STA $0B
    LDA $03
    STA $07
    LDA $13E9                   ; the cape's, SMW's ($029696)
    SEC
    SBC #$02
    STA $00
    LDA $13EA
    SBC #$00
    STA $08
    LDA #$14
    STA $02
    LDA $13EB
    STA $01
    LDA $13EC
    STA $09
    LDA #$10
    STA $03
    JML $03B72B
