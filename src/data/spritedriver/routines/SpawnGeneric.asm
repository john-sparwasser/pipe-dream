; Spawn minor extended sprite B (the accumulator's high byte) at $04-$05 (X), $06-$07 (Y), offset
; by $00, $01 (signed), with X speed $02 and Y speed $03, in the first free slot from 11 down:
; C clear and Y = the slot, or C set and Y = $FF. 8-bit A/X/Y; X and $00-$07 kept.
; DB on low RAM. Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md.
; (PIXI 1.40 calls this file Spawn/Generic.asm; SpawnMinorExtendedGeneric is the same, under the
; name its _header.asm files call.)
    LDY #$0B
-   LDA $17F0,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $17F0,y
    LDA $04
    CLC
    ADC $00
    STA $1808,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $18EA,y
    LDA $06
    CLC
    ADC $01
    STA $17FC,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $07
    STA $1814,y
    LDA $02
    STA $182C,y
    LDA $03
    STA $1820,y
    CLC
    RTL
