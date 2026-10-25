; Clipping 2 for sprite X: adds its position to the box the caller set up, 16-bit:
; $08-$09 X offset, $0A-$0B Y offset ($0C-$0F, width and height, as they are).
; 8-bit A/X/Y, X = the slot. Returns A = $0A, B = $0B, C from the Y add.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    LDA !14E0,x
    XBA
    LDA !E4,x
    REP #$20
    CLC
    ADC $08
    STA $08
    SEP #$20
    LDA !14D4,x
    XBA
    LDA !D8,x
    REP #$20
    CLC
    ADC $0A
    STA $0A
    SEP #$20
    RTL
