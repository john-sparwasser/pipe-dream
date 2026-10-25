; Kill sprite X as the player's star does: contact sound and graphic, knocked up and away from
; the player, and the next of the star's points (a 1-up from the eighth). Does not check that
; the player has a star, nor the sprite's "immune to star" bit: the caller does.
; 8-bit A/X/Y, X = the slot. Returns X kept, Y = the star count used (to 8), $0E-$0F = player X
; - sprite X, DB kept (this bank's while it runs, so it must map low RAM).
; Pipe Dream's, MIT, from the star branch of SMW's sprite contact ($01A847) and
; reference/PIXI_ROUTINES.md; in PIXI's order, by the player's X now ($94).
    PHB
    PHK
    PLB
    JSL $01AB6F
    LDA #$02
    STA !14C8,x
    LDA #$D0
    STA !AA,x
    LDY #$00                    ; away from the player
    LDA $94
    SEC
    SBC !E4,x
    STA $0E
    LDA $95
    SBC !14E0,x
    STA $0F
    BPL +
    INY
+   LDA .speed,y
    STA !B6,x
    LDA $18D2|!addr
    INC
    CMP #$08
    BCC +
    LDA #$08
+   STA $18D2|!addr
    JSL $02ACE5
    LDY $18D2|!addr
    LDA .sound,y
    STA $1DF9|!addr
    PLB
    RTL

.speed                          ; SMW's, $01A839
    db read1($01A839), read1($01A83A)
.sound                          ; none for a count wrapped to 0; SMW's $01A61E for 1-7; 1-up for 8
    db $00
    !i = 1
    while !i < 8
        db read1($01A61D+!i)
        !i #= !i+1
    endwhile
    db $03
