; Put Map16 tile A (16-bit; with 8-bit A, B:A) at block $9A-$9B (X), $98-$99 (Y) on layer $1933,
; and queue it to VRAM through SMW's $00C0FB if it is near the camera. C clear when written,
; set out of range (nothing written). Any widths; P, X and Y come back as they went in, N and Z
; from X. Bit 15 of the tile is dropped. Scratch and the $99/$9B trade as GetMap16.
; DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md around SMW's
; $00BEA8 pointers and $00C0FB.
    PHX
    PHY
    PHP
    REP #$20
    PHA
    SEP #$30
    REP #$20                    ; the block as given, kept in $0C-$0F
    LDA $98
    STA $0E
    LDA $9A
    STA $0C
    SEP #$20
    LDA $5B                     ; this layer's orientation, in bit 0 of $0A
    LDY $1933
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
    CMP $13D7
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
    LDA $1933
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
    LDA $1933
    ASL
    AND #$00FF
    TAX
    LDA $1925
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
    LDA $01,s                   ; $00C0FB's Y: the tile's index in $0FBE
    ASL
    TAY
    LDA $01,s                   ; the tile, read before it is written
    SEP #$20
    STA $7E0000,x
    XBA
    AND #$7F
    STA $7F0000,x
    REP #$20                    ; $08/$0A the camera by layer and orientation
    SEP #$20
    LSR $0A
    REP #$20
    LDA $1933
    AND #$00FF
    BNE .layer2
    BCS +
    LDA $1A
    SEC
    SBC #$0080
    STA $08
    LDA $1C
    STA $0A
    BRA .queue
+   LDA $1A
    STA $08
    LDA $1C
    SEC
    SBC #$0080
    STA $0A
    BRA .queue
.layer2
    BCS +
    LDA $1E
    STA $08
    LDA $20
    SEC
    SBC #$0080
    STA $0A
    BRA .queue
+   LDA $1E
    SEC
    SBC #$0080
    STA $08
    LDA $20
    STA $0A
.queue                          ; it ends in RTS: reached through bank 0's RTL at $00804D
    PHK
    PEA.w .queued-1
    PEA.w $804D-1
    JML $00C0FB
.queued
    REP #$20
    PLA
    PLP
    CLC
    PLY
    PLX
    RTL
.invalid
    REP #$20
    PLA
    PLP
    SEC
    PLY
    PLX
    RTL
