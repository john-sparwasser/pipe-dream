; Radius $06 x cos(angle $04-$05, 9 bits for a full turn) into $07-$08, signed 16-bit. By SMW's
; sine ($07F7DB, its one $0100 taken as $FF) a quarter turn on, and the PPU's signed multiply, so
; a radius from $80 counts as negative. Returns A = $07, B = $08, C the half turn, X kept (N and
; Z from it). 8-bit A/X/Y; $04-$06 kept. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
    PHX
    REP #$30
    LDA $04
    CLC
    ADC #$0080
    AND #$01FF
    PHA
    AND #$00FF
    ASL
    TAX
    LDA.l $07F7DB,x
    CMP #$0100
    BCC +
    LDA #$00FF
+
if !SA1                         ; the SA-1 CPU has no PPU: its own signed multiply, 16 x 16
    STZ $2250
    STA $2251
    LDA $06
    AND #$00FF
    CMP #$0080
    BCC ++
    ORA #$FF00                  ; the radius sign-extended
++  STA $2253                   ; (the write to $2254 starts it)
    NOP #3
    LDA $2307                   ; the product's bits 8-23, as the PPU's $2135
else
    SEP #$20
    STA $211B
    STZ $211B
    LDA $06
    STA $211C
    REP #$20
    LDA $2135
endif
    STA $07
    PLA                         ; the second half turn: negative
    AND #$0100
    BEQ +
    LDA $07
    EOR #$FFFF
    INC
    STA $07
    SEC
    BRA ++
+   LDA $07
    CLC
++  SEP #$30
    PLX
    RTL
