; The Map16 tile at block $9A-$9B (X), $98-$99 (Y) on layer $1933 (0: 1, 1: 2): A = the tile
; (with 8-bit A, B its high byte), Y = its high byte; or A = Y = $FF(FF), N set, out of range.
; Any widths, kept. P and X come back as they went in, N and Z from X (from Y out of range).
; Leaves $06-$0F as ChangeMap16 uses them, and on a vertical layer $99 and $9B traded, as PIXI's
; does. DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md around SMW's
; pointers at $00BEA8.
    PHX
    PHP
    SEP #$30
    REP #$20                    ; the block as given, kept in $0C-$0F
    LDA $98
    STA $0E
    LDA $9A
    STA $0C
    SEP #$20
    LDA $5B                     ; this layer's orientation, in bit 0 of $0A
    LDY $1933|!addr
    BEQ +
    LSR
+   STA $0A
    LSR
    BCC .rows
    LDA $99                     ; vertical: the screen and Y's high byte change places (and stay so)
    XBA
    LDA $9B
    STA $99
    XBA
    STA $9B
if !EXLEVEL
    BRA .columns
.rows
    REP #$20                    ; horizontal on a Lunar Magic level: within its height
    LDA $98
    CMP $13D7|!addr
    SEP #$20
    BCC +
    JMP .invalid
+
    BRA .inside
.columns
else
.rows
endif
    LDA $99
    CMP #$02
    BCC +
    JMP .invalid
+
.inside
    LDA $9B                     ; $0B the screen, $08-$09 the block within it
    STA $0B
    REP #$20
    LDA $98
    AND #$FFF0
    STA $08
    SEP #$20
    LDA $9A
    LSR #4
    ORA $08
    STA $08
    LDA $0D                     ; $06-$07 the BG tilemap word in VRAM
    AND #$01
    ASL #2
    STA $06
    LDA $0F
    AND #$01
    ASL #3
    ORA $06
    STA $06
    LDA $98
    LSR #6
    ORA $06
    ORA #$20
    STA $06
    LDA $1933|!addr
    BEQ +
    LDA #$10
    TSB $06
+   LDA $98
    AND #$30
    ASL #2
    STA $07
    LDA $9A
    LSR #3
    AND #$1E
    ORA $07
    STA $07
    LDA $0B                     ; the Map16 address, by SMW's pointers from $00BEA8 (one carry run);
    ASL                         ; 3 x the screen and 2 x the level mode each wrap at a byte, as PIXI's
    ADC $0B
    REP #$30
    AND #$00FF
    PHA
    LDA $1933|!addr
    ASL
    AND #$00FF
    TAX
    LDA $1925|!addr
    ASL
    AND #$00FF
    CLC
    ADC.l $00BEA8,x
    TAX
    LDA.l $000000,x
    ADC $01,s
    TAX
    LDA.l $000000,x
    ADC $08
    TAX
    PLA
    SEP #$20
    LDA !BankA+$010000,x        ; the Map16 tables: $7E/$7F, on SA-1 the pack's $40/$41
    XBA
    LDA !BankA,x
    REP #$20
    PHA
    AND #$FF00
    XBA
    TAY
    PLA
    PLP
    PLX
    RTL
.invalid
    REP #$30
    LDA #$FFFF
    LDY #$0000
    PLP
    PLX
    DEY
    RTL
