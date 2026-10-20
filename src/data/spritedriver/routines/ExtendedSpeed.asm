; Move extended sprite X by its speeds. A, loaded just before the call, picks how:
; 0 gravity then Y then X, 1 Y then X, 2 X only, 3 Y only (more: nothing, A - 3 back).
; 8-bit A/X/Y, X = the extended slot; DB on low RAM. Returns Y = $00/$FF, the sign of the last
; speed applied, and X = $15E9 after an X move, as SMW's $02B554 leaves it.
; Pipe Dream's, MIT, from SMW's $02B554/$02B560 and reference/PIXI_ROUTINES.md.
;
; Mode 0 is taken on the Z flag the load leaves, not on A, as PIXI's is; and gravity is added
; to any Y speed but $80-$BF, with no cap, as SMW's extended sprites do it.
    BEQ .gravity
    DEC
    BEQ .both
    DEC
    BEQ .x
    DEC
    BEQ .y
    RTL
.gravity
    LDA $173D,x
    CMP #$40
    BPL .both
    CLC
    ADC #$03
    STA $173D,x
.both                           ; SMW's routines end in RTS: reached through bank 2's RTL at $02B889
    PHK
    PEA.w .x-1
    PEA.w $B889-1
    JML $02B560
.x
    PHK
    PEA.w .done-1
    PEA.w $B889-1
    JML $02B554
.y
    PHK
    PEA.w .done-1
    PEA.w $B889-1
    JML $02B560
.done
    RTL
