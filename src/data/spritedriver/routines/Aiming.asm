; X and Y speeds of size A (the low byte, unsigned) aimed along $00-$01 = shooter X - target X,
; $02-$03 = shooter Y - target Y (16-bit): into $00 and $02 ($01 = $03 = 0), toward the target.
; Any widths; P and X come back as they went in (N and Z from X), Y is not used. Leaves $04-$05
; the scale, $06 = 1 when the target is to the right, $07 = 0, $0F the size. Returns A = $00.
; Pipe Dream's, MIT, written from reference/PIXI_ROUTINES.md: the distance's 1/sqrt from a
; table by formula, round(65536 / sqrt(k)), and the CPU's multiplier.
    PHX
    PHP
    SEP #$30
    STA $0F
    STZ $07
    STZ $06
    LDA $01                     ; |X| (and which side)
    BPL +
    INC $06
    REP #$20
    LDA $00
    EOR #$FFFF
    INC
    STA $00
    SEP #$20
+   LDA $03                     ; |Y|, its sign kept for the end
    PHA
    BPL +
    REP #$20
    LDA $02
    EOR #$FFFF
    INC
    STA $02
    SEP #$20
+
-   LDA $01                     ; both halved down to a byte
    ORA $03
    BEQ +
    REP #$20
    LSR $00
    LSR $02
    SEP #$20
    BRA -
+   LDA $00                     ; d = X^2 + Y^2, 17 bits
    STA $4202
    STA $4203
    NOP #4
    REP #$20
    LDA $4216
    STA $04
    SEP #$20
    LDA $02
    STA $4202
    STA $4203
    NOP #4
    REP #$20
    LDA $4216
    CLC
    ADC $04
    LDX #$00                    ; k = d / 4^n, under $100
    BCS .big
-   CMP #$0100
    BCC .k
    LSR
    LSR
    INX
    BRA -
.big
    ROR
    LSR #9
    LDX #$05
.k
    PHX                         ; m = (65536 / sqrt(k)) / 2^n
    ASL
    REP #$10
    TAX
    LDA.l .r,x
    SEP #$10
    PLX
-   CPX #$00
    BEQ +
    LSR
    DEX
    BRA -
+   STA $04                     ; $04-$05 = size x m / 256
    SEP #$20
    LDA $04
    STA $4202
    LDA $0F
    STA $4203
    NOP #4
    LDX $4217
    LDA $05
    STA $4202
    LDA $0F
    STA $4203
    NOP #4
    REP #$20
    TXA
    AND #$00FF
    CLC
    ADC $4216
    STA $04
    SEP #$20
    LDA $02                     ; Y speed = Y x that / 256, toward the target
    STA $4202
    LDA $04
    STA $4203
    NOP #4
    LDX $4217
    LDA $05
    STA $4203
    NOP #4
    REP #$20
    TXA
    AND #$00FF
    CLC
    ADC $4216
    SEP #$20
    TAX
    PLA
    BMI +
    TXA
    EOR #$FF
    INC
    BRA ++
+   TXA
++  STA $02
    LDA $00                     ; and X's
    STA $4202
    LDA $04
    STA $4203
    NOP #4
    LDX $4217
    LDA $05
    STA $4203
    NOP #4
    REP #$20
    TXA
    AND #$00FF
    CLC
    ADC $4216
    SEP #$20
    LDX $06
    BNE +
    EOR #$FF
    INC
+   STA $00
    PLP
    PLX
    RTL

.r                              ; round(65536 / sqrt(k)), $FFFF for k = 0 and 1
    dw $FFFF,$FFFF,$B505,$93CD,$8000,$727D,$6883,$60C2
    dw $5A82,$5555,$50F4,$4D30,$49E7,$4700,$446B,$4219
    dw $4000,$3E17,$3C57,$3ABB,$393E,$37DD,$3694,$3561
    dw $3441,$3333,$3235,$3144,$3061,$2F8A,$2EBD,$2DFB
    dw $2D41,$2C90,$2BE7,$2B46,$2AAB,$2A16,$2987,$28FE
    dw $287A,$27FB,$2780,$270A,$2698,$262A,$25BF,$2557
    dw $24F3,$2492,$2434,$23D9,$2380,$232A,$22D6,$2285
    dw $2236,$21E8,$219D,$2154,$210D,$20C7,$2083,$2041
    dw $2000,$1FC1,$1F83,$1F46,$1F0B,$1ED2,$1E99,$1E62
    dw $1E2B,$1DF6,$1DC2,$1D8F,$1D5D,$1D2D,$1CFC,$1CCD
    dw $1C9F,$1C72,$1C45,$1C1A,$1BEF,$1BC4,$1B9B,$1B72
    dw $1B4A,$1B23,$1AFC,$1AD6,$1AB1,$1A8C,$1A68,$1A44
    dw $1A21,$19FE,$19DC,$19BB,$199A,$1979,$1959,$1939
    dw $191A,$18FC,$18DD,$18C0,$18A2,$1885,$1869,$184C
    dw $1831,$1815,$17FA,$17DF,$17C5,$17AB,$1791,$1778
    dw $175F,$1746,$172D,$1715,$16FD,$16E6,$16CE,$16B7
    dw $16A1,$168A,$1674,$165E,$1648,$1633,$161D,$1608
    dw $15F4,$15DF,$15CB,$15B7,$15A3,$158F,$157C,$1568
    dw $1555,$1542,$1530,$151D,$150B,$14F9,$14E7,$14D5
    dw $14C4,$14B2,$14A1,$1490,$147F,$146E,$145E,$144D
    dw $143D,$142D,$141D,$140D,$13FE,$13EE,$13DF,$13CF
    dw $13C0,$13B1,$13A2,$1394,$1385,$1377,$1368,$135A
    dw $134C,$133E,$1330,$1322,$1315,$1307,$12FA,$12ED
    dw $12DF,$12D2,$12C5,$12B8,$12AC,$129F,$1292,$1286
    dw $127A,$126D,$1261,$1255,$1249,$123D,$1231,$1226
    dw $121A,$120F,$1203,$11F8,$11EC,$11E1,$11D6,$11CB
    dw $11C0,$11B5,$11AA,$11A0,$1195,$118A,$1180,$1176
    dw $116B,$1161,$1157,$114D,$1142,$1138,$112E,$1125
    dw $111B,$1111,$1107,$10FE,$10F4,$10EB,$10E1,$10D8
    dw $10CF,$10C5,$10BC,$10B3,$10AA,$10A1,$1098,$108F
    dw $1086,$107E,$1075,$106C,$1064,$105B,$1052,$104A
    dw $1042,$1039,$1031,$1029,$1020,$1018,$1010,$1008
