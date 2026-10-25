; The minor extended spawn under the name PIXI 1.40's _header.asm files call: SpawnGeneric,
; whose file it repeats (each routine is its own file). PIXI ships no routine by this name.
    LDY #$0B
-   LDA $17F0|!addr,y
    BEQ .found
    DEY
    BPL -
    SEC
    RTL
.found
    XBA
    STA $17F0|!addr,y
    LDA $04
    CLC
    ADC $00
    STA $1808|!addr,y
    LDA $00
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $05
    STA $18EA|!addr,y
    LDA $06
    CLC
    ADC $01
    STA $17FC|!addr,y
    LDA $01
    AND #$80
    BEQ +
    LDA #$FF
+   ADC $07
    STA $1814|!addr,y
    LDA $02
    STA $182C|!addr,y
    LDA $03
    STA $1820|!addr,y
    CLC
    RTL
