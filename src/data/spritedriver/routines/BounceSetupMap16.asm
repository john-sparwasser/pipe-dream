; $98-$9B and $1933 to the block bounce sprite X sits on (by its middle), for SMW's $00BEB0;
; the layer is $1901,x bit 7. Returns A = $1933, C = $1901,x bit 6. 8-bit A/X/Y, X = the
; bounce slot; X and Y kept. DB on low RAM.
; Pipe Dream's, MIT, from SMW's $0291BC and reference/PIXI_ROUTINES.md.
    LDA $16A1|!addr,x
    CLC
    ADC #$08
    AND #$F0
    STA $98
    LDA $16A9|!addr,x
    ADC #$00
    STA $99
    LDA $16A5|!addr,x
    CLC
    ADC #$08
    AND #$F0
    STA $9A
    LDA $16AD|!addr,x
    ADC #$00
    STA $9B
    LDA $1901|!addr,x
    ASL
    ASL
    LDA $1901|!addr,x
    BMI +
    LDA #$00
    BRA ++
+   LDA #$01
++  STA $1933|!addr
    RTL
