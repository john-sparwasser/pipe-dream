; Clipping 1, 16-bit, as the player's box: $00 X, $02 Y, $04 width, $06 height. The box is
; SMW's ($03B664): by ducking or small ($73, $19) and riding Yoshi ($187A).
; 8-bit X, any A width; returns 8-bit A = $02 (B $03), C from the Y add, X kept.
; DB on low RAM. Pipe Dream's, MIT, from SMW's $03B664 and reference/PIXI_ROUTINES.md.
    SEP #$20
    PHX
    LDX #$00
    LDA $73
    BNE +
    LDA $19
    BNE ++
+   INX
++  LDA $187A
    BEQ +
    INX
    INX
+   REP #$20
    LDA $94
    CLC
    ADC #$0002
    STA $00
    LDA #$000C
    STA $04
    LDA.l $03B660,x             ; SMW's heights
    AND #$00FF
    STA $06
    LDA.l $03B65C,x             ; and Y offsets
    AND #$00FF
    CLC
    ADC $96
    STA $02
    SEP #$20
    PLX
    RTL
