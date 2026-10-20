; Whether the player is above sprite X: Y = 1 if above it, else 0; $0F = the low byte of
; player Y - sprite Y, A the high byte. Flags from the subtraction. 8-bit A/X/Y, DB on low RAM.
; Pipe Dream's, MIT: SMW's $03B829, returning long.
    LDY #$00
    LDA $96
    SEC
    SBC $D8,x
    STA $0F
    LDA $97
    SBC $14D4,x
    BPL +
    INY
+   RTL
