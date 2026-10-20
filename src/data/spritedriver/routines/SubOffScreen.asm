; Erase sprite X once it is far enough off screen, freeing its level record so it can come back.
; A picks the margin (bits 0-2): 0-3 SMW bank 1's, 4-7 bank 3's, from tightest to widest.
; 8-bit A, X = the slot; DB on low RAM. Pipe Dream's, MIT, from SMW's $01AC31 (and $03B85D for
; bank 3's margins) and reference/PIXI_ROUTINES.md.
;
; As PIXI's: the load table is !1938, there is no special case for Magikoopa or for sprites 22
; and 24, and on a Lunar Magic level (!EXLEVEL) the bottom of a horizontal level is LM's: past
; its height ($13D7) or outside the range $0BF0-$0BF2 keeps of the camera. That test leaves
; X = $15E9, the slot SMW is running, as PIXI's does.
    AND #$07
    ASL
    STA $03
    LDA $15A0,x                 ; on screen: nothing to do
    ORA $186C,x
    BNE +
    RTL
+   PHB
    PHK
    PLB
    LDA $5B
    AND #$01
    BNE .vertical

if !EXLEVEL
    XBA                         ; B is kept
    PHA
    LDA $14D4,x
    XBA
    LDA $D8,x
    REP #$20
    CMP $13D7
    BPL .low
    SEC
    SBC $1C
    CMP $0BF2
    BPL .low
    CMP $0BF0
    BMI .low
    SEP #$20
    PLA
    XBA
    LDX $15E9
    BRA .sides
.low
    SEP #$20
    PLA
    XBA
    LDX $15E9
    BRA .erase
else
    LDA $D8,x                   ; below the level
    CLC
    ADC #$50
    LDA $14D4,x
    ADC #$00
    CMP #$02
    BPL .erase
endif

.sides                          ; even frames the right margin, odd frames the left
    LDA $167A,x
    AND #$04
    BNE .done
    LDA $13
    AND #$01
    ORA $03
    STA $01
    TAY
    LDA $1A
    CLC
    ADC .left,y
    ROL $00
    CMP $E4,x
    PHP
    LDA $1B
    LSR $00
    ADC .right,y
    PLP
    SBC $14E0,x
    STA $00
    LSR $01
    BCC +
    EOR #$80
    STA $00
+   LDA $00
    BPL .done

.erase
    LDA $14C8,x                 ; a level sprite still alive may load again
    CMP #$08
    BCC +
    LDY $161A,x
    CPY #$FF
    BEQ +
    PHX
    TYX
    LDA #$00
    STA !1938,x
    PLX
+   STZ $14C8,x
.done
    PLB
    RTL

.vertical                       ; every other frame, above then below
    LDA $167A,x
    AND #$04
    BNE .done
    LDA $13
    LSR
    BCS .done
    LDA $14E0,x
    CMP #$02
    BCS .erase
    LDA $13
    LSR
    AND #$01
    STA $01
    TAY
    LDA $1C
    CLC
    ADC .top,y
    ROL $00
    CMP $D8,x
    PHP
    LDA $1D
    LSR $00
    ADC .bottom,y
    PLP
    SBC $14D4,x
    STA $00
    LDY $01
    BEQ +
    EOR #$80
    STA $00
+   LDA $00
    BPL .done
    BRA .erase

; SMW's own margins, read from the ROM: bank 1's for A = 0-3, then bank 3's for 4-7.
.left
    !i = 0
    while !i < 8
        db read1($01AC11+!i)
        !i #= !i+1
    endwhile
    !i = 0
    while !i < 8
        db read1($03B83F+!i)
        !i #= !i+1
    endwhile
.right
    !i = 0
    while !i < 8
        db read1($01AC19+!i)
        !i #= !i+1
    endwhile
    !i = 0
    while !i < 8
        db read1($03B847+!i)
        !i #= !i+1
    endwhile
.top
    db read1($01AC0D), read1($01AC0E)
.bottom
    db read1($01AC0F), read1($01AC10)
