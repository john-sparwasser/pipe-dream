; Sprite X's off-screen flags, OAM index and position on screen, for its graphics routine.
; 8-bit A/X/Y, X = the slot; DB on low RAM and equal to the bank of the code that JSRed to the
; graphics routine. Pipe Dream's, MIT, from SMW's $01A365 and reference/PIXI_ROUTINES.md.
;
; Returns Y = the OAM index ($15EA,x), $00 = X on screen, $01 = Y on screen, $15A0,x / $15C4,x /
; $186C,x the off-screen flags, A = the vertical test's high byte and C its low byte >= $10.
; Too far off screen to draw, it returns past the graphics routine instead: to just after the
; JSR that called it, in bank DB, as SMW's does from bank 1.
    STZ !186C,x
    LDA !14E0,x                 ; $00-$01 = X - the camera's X
    XBA
    LDA !E4,x
    REP #$20
    SEC
    SBC $1A
    STA $00
    CLC                         ; drawn only from $40 left of the screen to $40 right of it
    ADC #$0040
    CMP #$0180
    SEP #$20
    LDA #$00
    ROL
    STA !15C4,x
    LDA $01                     ; off the screen at all
    BEQ +
    LDA #$01
+   STA !15A0,x
    LDA !15C4,x
    BNE .gone

    LDA !14D4,x                 ; d = Y - the camera's Y, kept on the stack
    XBA
    LDA !D8,x
    REP #$20
    SEC
    SBC $1C
    PHA
    CLC                         ; bit 1: a tall sprite's lower half is off the screen
    ADC #$0020
    SEP #$20
    XBA
    BEQ +
    LDA !190F,x
    AND #$20
    BEQ +
    LDA #$02
    STA !186C,x
+   REP #$20                    ; bit 0: its own 16 rows are
    LDA $01,s
    CLC
    ADC #$0010
    SEP #$20
    CMP #$10
    XBA
    TAY
    BEQ +
    INC !186C,x
+   PLA                         ; $01 = d's low byte, left in B; A = the test's high byte
    STA $01
    XBA
    PLA
    TYA
    LDY !15EA,x
    RTL

.gone
    LDA #$00                    ; B = 0, as PIXI's leaves it
    XBA
    PLA                         ; our own return
    PLA
    PLA
    PLA                         ; the graphics routine's, as a long one in bank DB
    PLY
    PHB
    PHY
    PHA
    CLC
    INY
    DEY
    RTL
