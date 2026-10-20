; After a sprite's graphics routine: for each of its A + 1 tiles (from $0300 + $15EA,x), the
; size bit Y gives ($00 8x8, $02 16x16; bit 7 set: keep each tile's) plus the X high bit, and
; tiles below the screen hidden ($F0). 8-bit A/X/Y, X = the slot; DB on low RAM.
; Returns X = $15E9, Y one past the last tile, C set; $00-$03 the sprite on screen, $08 = $FF,
; $0B the size. Pipe Dream's, MIT, from SMW's $01B7B3 and reference/PIXI_ROUTINES.md (the size
; bit is added, as PIXI's does).
    STY $0B
    STA $08
    LDA $14D4,x                 ; $00-$01 the sprite's Y on screen, $02-$03 its X
    XBA
    LDA $D8,x
    REP #$20
    SEC
    SBC $1C
    STA $00
    SEP #$20
    LDA $14E0,x
    XBA
    LDA $E4,x
    REP #$20
    SEC
    SBC $1A
    STA $02
    SEP #$20
    LDA $15EA,x                 ; Y the OAM index, X the size index (counting on past $3F)
    TAY
    LSR
    LSR
    TAX
.tile
    LDA $0300,y                 ; the tile's X, by the sprite's: past the screen's right?
    SEC
    SBC $02
    REP #$20
    AND #$00FF
    CMP #$0080
    BCC +
    ORA #$FF00
+   CLC
    ADC $02
    CMP #$0100
    SEP #$20
    LDA $0B
    BPL +
    LDA $0460,x
    AND #$02
+   ADC #$00
    STA $0460,x
    LDA $0301,y                 ; its Y: below the screen?
    SEC
    SBC $00
    REP #$20
    AND #$00FF
    CMP #$0080
    BCC +
    ORA #$FF00
+   CLC
    ADC $00
    CLC
    ADC #$0010
    CMP #$0100
    SEP #$20
    BCC +
    LDA #$F0
    STA $0301,y
+   INY
    INY
    INY
    INY
    INX
    DEC $08
    BPL .tile
    XBA                         ; B = 0
    LDA #$00
    XBA
    SEC
    LDX $15E9
    RTL
