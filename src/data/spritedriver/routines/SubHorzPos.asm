; Which side of sprite X the player is on: Y = 1 if left of it, else 0; A = the high byte of
; player X - sprite X, whose low byte is in $0E and high byte in $0F (PIXI's order; SMW's own,
; $03B817, keeps the low byte in $0F). Flags from the subtraction. 8-bit A/X/Y, DB on low RAM.
; Pipe Dream's, MIT, from SMW's $03B817 and reference/PIXI_ROUTINES.md.
    LDY #$00
    LDA $94
    SEC
    SBC !E4,x
    STA $0E
    LDA $95
    SBC !14E0,x
    STA $0F
    BPL +
    INY
+   RTL
