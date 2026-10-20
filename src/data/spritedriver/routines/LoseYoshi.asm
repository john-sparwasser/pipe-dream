; If the player is riding Yoshi, Yoshi runs off as when the player is hit. A, X and Y are kept
; (N and Z from A), C and V untouched. 8-bit A/X/Y; DB on low RAM.
; Pipe Dream's, MIT, from SMW's $02A473 (the Yoshi half of $02A469) and
; reference/PIXI_ROUTINES.md; Yoshi's slot is $18E2 - 1, as PIXI's takes it (SMW's: $18DF).
    PHA
    PHX
    PHY
    LDA $187A
    BEQ .done
    LDX $18E2                   ; Yoshi's slot + 1, so each table less one
    LDA #$10
    STA $163E-1,x
    LDA #$03
    STA $1DFA
    LDA #$13
    STA $1DFC
    LDA #$02
    STA $C2-1,x
    STZ $187A
    STZ $0DC1
    LDA #$C0
    STA $7D
    STZ $7B
    PHX                         ; running the way it faces: SMW's speeds at $02A4B3
    LDA $157C-1,x
    TAX
    LDA.l $02A4B3,x
    PLX
    STA $B6-1,x
    STZ $1594-1,x
    STZ $151C-1,x
    STZ $18AE
    LDA #$30
    STA $1497
.done
    PLY
    PLX
    PLA
    RTL
