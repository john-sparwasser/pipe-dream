; For extended sprite X's graphics: Y = its OAM index (SMW's, $02A153, into $0200), $00 = $40
; when it moves right (X flip), $01 = its X on screen, $02 its Y on screen. Off the screen it
; is erased ($170B,x = 0) and $02 = $F0; a little above the screen it is only hidden ($02 = $F0).
; Returns A = $02, C set when in the screen's top part; N set when hidden or erased.
; 8-bit A/X/Y, X = the extended slot (0-7). DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02A1A4 and reference/PIXI_ROUTINES.md, as PIXI 1.40's (1.2's vertical
; test added the screen's Y high byte instead of subtracting it).
    LDA.l $02A153,x
    TAY
    LDA $1747|!addr,x
    BMI +
    LDA #$40
    BRA ++
+   LDA #$00
++  STA $00
    LDA $171F|!addr,x
    SEC
    SBC $1A
    STA $01
    LDA $1733|!addr,x
    SBC $1B
    BNE .erase
    LDA $1715|!addr,x
    SEC
    SBC $1C
    STA $02
    LDA $1729|!addr,x
    SBC $1D
    BEQ .on
    INC                         ; up to a screen above
    BEQ .top
    CLC
    BRA .erase
.on
    LDA $02
    CMP #$F0
    BCS .erase
    RTL
.top
    LDA $02
    CMP #$C0
    BCC .erase
    CMP #$E0
    BCC .hide
    RTL
.erase
    STZ $170B|!addr,x
.hide
    LDA #$F0
    STA $02
    RTL
