; Radius $06 x sin(angle $04-$05, 9 bits for a full turn) into $09-$0A, signed 16-bit. By SMW's
; sine ($07F7DB, its one $0100 taken as $FF) and the PPU's signed multiply, so a radius from $80
; counts as negative. Returns A = $09, B = $0A, C the half turn, X kept (N and Z from it).
; 8-bit A/X/Y; $04-$08 kept. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    PHX
    REP #$30
    LDA $04
    AND #$01FF
    PHA
    AND #$00FF
    ASL
    TAX
    LDA.l $07F7DB,x
    CMP #$0100
    BCC +
    LDA #$00FF
+   SEP #$20
    STA $211B
    STZ $211B
    LDA $06
    STA $211C
    REP #$20
    LDA $2135
    STA $09
    PLA                         ; the second half turn: negative
    AND #$0100
    BEQ +
    LDA $09
    EOR #$FFFF
    INC
    STA $09
    SEC
    BRA ++
+   LDA $09
    CLC
++  SEP #$30
    PLX
    RTL
