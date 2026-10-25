; Spawn smoke A at sprite X's position offset by $00, $01 (8-bit, as smoke is), timer $02, in
; the first free smoke slot from 3 down: C clear and Y = the slot, or C set and Y = $FF.
; 8-bit A/X/Y, X = the parent, kept. DB on low RAM.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md (SMW's own spawns reuse slot 0
; when all are taken; this fails, as PIXI's does).
    XBA                         ; the type waits in B
    LDY #$03
-   LDA $17C0|!addr,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $17C0|!addr,y
    LDA $02
    STA $17CC|!addr,y
    LDA !D8,x
    CLC
    ADC $01
    STA $17C4|!addr,y
    LDA !E4,x
    CLC
    ADC $00
    STA $17C8|!addr,y
    CLC
    RTL
