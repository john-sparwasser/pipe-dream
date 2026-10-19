; Pipe Dream's custom sprite driver (reference/PIXI_PORT.md, phase 2).
;
; Written from SMW's own code at each hook and PIXI's documented on-ROM format, so sprites written
; for PIXI run unchanged and anything that reads a PIXI ROM finds the table. The build assembles
; this after every sprite, with defines.asm and a generated file that sets:
;   !SizeTable  Lunar Magic's sprite size table (0x400 bytes, record length per extra bits/number)
;   !LmStep     where $02A846 jumped before (Lunar Magic's own record step), or 0 on a vanilla loader
; and holds SpriteTable: 0x100 entries of 0x10 bytes — type, acts-like, the six tweaker bytes,
; INIT and MAIN (long), the two extra property bytes.
;
; A sprite is custom when its level data sets extra bit $08; its number is then the table's, and
; $9E holds its acts-like so SMW's own code treats it as that sprite wherever we do not step in.

lorom

; ---- The header PIXI keeps at $02FFE2, so a tool reading a PIXI ROM finds the table ----
org $02FFE2
    db "STSD"
    db 143                      ; the format's version: PIXI 1.43's
    db $00                      ; flags: no per-level sprites
    db $00,$00
    db $FF,$FF,$FF,$FF
    dl SpriteTable
    dl $FFFFFF,$FFFFFF,$FFFFFF,$FFFFFF   ; per-level tables, then three unused
    dl $FFFFFF                  ; extra status pointers: not kept

; ---- The level loader ----
; $02A846 steps Y from one record's second byte to the next record: 3 bytes in SMW, so a record
; with extra bytes would be read from its middle. Step by the size table's length instead.
org $02A846
    JML StepRecord
; The spawn stores the record's first byte AND #$0D into a position's high byte: bit 0 is the
; position, bits 2-3 are the extra bits, which belong in a table of their own. $02A94B is the
; vertical-level store (into $14E0), $02A963 the one Lunar Magic's horizontal path calls (into
; $14D4, and the caller then ANDs A with #$01, so A must still hold bit 0).
org $02A94B
    JSL KeepBitsX
    NOP
org $02A963
    JSL KeepBitsY
    NOP
; The spawn's JSL to the table setup: for a custom sprite, its own tables after SMW's.
org $02A9C9
    JSL Spawn
; The first test of a record's number (CMP #$E7 : BCC), before SMW sorts it into scroll sprites,
; generators (CB-D9), shooters (C9-CA) and the rest: a custom C0-CF is a shooter, D0-DF a
; generator, whatever SMW would have made of the number.
org $02A866
    JML Kind
; The shooter spawn's `LDA $04 : SEC : SBC #$C8` (then STA $1783,x): a custom shooter keeps its
; number there. Kind hands it over with bit 7 clear, which no SMW shooter (C9, CA) has.
org $02ABA0
    JML ShooterNumber
    NOP
; The shooter run's `DEC : JSL $0086DF`, by $1783,x: a custom one (C0-CF) runs its MAIN.
org $02B3AB
    JML ShooterRun
    NOP
; The generator run's `LDA $18B9 : BEQ`: a custom one (D0-DF; SMW's are 1-F) runs its MAIN.
org $02AFFE
    JML GeneratorRun
    NOP

; ---- Slot setup ----
; The end of $07F722, which every spawn runs through to clear a slot: clear the custom state too,
; so a slot a custom sprite left is a vanilla one again.
org $07F785
    JML ClearSlot
; Custom sprites call $0187A7 to give a slot its custom tables (after setting !new_sprite_num and
; running $07F7D2). SMW keeps sprite 9B's INIT here, so that moves to a stub of its own.
org $0187A7
    JML SetTables
org $0182B3
    dw Init9B
org $01FFF0
Init9B:
    JSL $02DA59
    RTS

; ---- The sprite loop ----
org $018127                     ; the status dispatch: LDA $14C8,x : BEQ
    JML Status
org $018172                     ; status 01, INIT: LDA #$08 : STA $14C8,x
    JML Init
    NOP
org $0185C3                     ; status 08, MAIN: STZ $1491 : LDA $9E,x
    JML Main
    NOP

freecode

; Y at the record's second byte; X counts records. 8-bit A/X/Y.
StepRecord:
    PHX
    DEY
    LDA [$CE],y                 ; extra bits → the table's high byte
    AND #$0C
    LSR
    LSR
    XBA
    INY
    INY
    LDA [$CE],y                 ; the number → its low byte
    DEY
    REP #$10
    TAX
    LDA.l !SizeTable,x
    SEP #$10
    PLX
    CMP #$03
    BCS +
    LDA #$03
+   DEC                         ; Y was one byte in: it moves on by length - 1
    CLC
    PHY
    ADC $01,s
    TAY
    PLA
    INX
if !LmStep != 0
    TYA                         ; Lunar Magic's step goes on with BPL on Y, rebasing $CE
    JML !LmStep+3               ; past its own INX : INY : INY
else
    JML $02A82E
endif

KeepBitsX:
    PHA
    AND #$01
    STA $14E0,x
    PLA
    AND #$0C
    STA !extra_bits,x
    RTL

KeepBitsY:
    PHA
    AND #$0C
    STA !extra_bits,x
    PLA
    AND #$01
    STA $14D4,x
    RTL

; Y at the record's number. SMW's setup clears the slot (and with it the bits KeepBits put there),
; so they are kept across it.
Spawn:
    LDA !extra_bits,x
    PHA
    JSL $07F7D2
    PLA
    STA !extra_bits,x
    AND #$08
    BEQ .done
    LDA [$CE],y
    STA !new_sprite_num,x
    JSR ExtraBytes
    JSL SetTables
.done
    RTL

; The placement's extra bytes: up to four copied out, more than four given as a pointer.
ExtraBytes:
    PHY
    LDA $00
    PHA
    LDA !extra_bits,x
    AND #$0C
    LSR
    LSR
    XBA
    LDA !new_sprite_num,x
    PHX
    REP #$10
    TAX
    LDA.l !SizeTable,x
    SEP #$10
    PLX
    SEC
    SBC #$03
    BEQ .done
    BCC .done
    CMP #$05
    BCS .pointer
    STA $00
    INY
    LDA [$CE],y
    STA !extra_byte_1,x
    DEC $00
    BEQ .done
    INY
    LDA [$CE],y
    STA !extra_byte_2,x
    DEC $00
    BEQ .done
    INY
    LDA [$CE],y
    STA !extra_byte_3,x
    DEC $00
    BEQ .done
    INY
    LDA [$CE],y
    STA !extra_byte_4,x
    BRA .done
.pointer
    TYA                         ; carry set: the first extra byte is one past Y
    ADC $CE
    STA !extra_byte_1,x
    LDA $CF
    ADC #$00
    STA !extra_byte_2,x
    LDA $D0
    ADC #$00
    STA !extra_byte_3,x
.done
    PLA
    STA $00
    PLY
    RTS

ClearSlot:
    LDA #$00
    STA !extra_bits,x
    LDA #$01
    STA $15A0,x
    RTL

; X = the slot, !new_sprite_num,x its custom number. Gives the slot the table's acts-like and
; tweakers; a tweak-only entry (type 0) then drops the custom bit, so SMW runs it.
SetTables:
    PHX
    PHY
    PHP
    SEP #$20
    REP #$10
    TXY
    LDA !new_sprite_num,x
    JSR EntryOf
    LDA.l SpriteTable+1,x
    STA.w $009E,y
    LDA.l SpriteTable+2,x
    STA $1656,y
    LDA.l SpriteTable+3,x
    STA $1662,y
    LDA.l SpriteTable+4,x
    STA $166E,y
    AND #$0F
    STA $15F6,y
    LDA.l SpriteTable+5,x
    STA $167A,y
    LDA.l SpriteTable+6,x
    STA $1686,y
    LDA.l SpriteTable+7,x
    STA $190F,y
    LDA.l SpriteTable,x
    PHA
    LDA.l SpriteTable+14,x
    PHA
    LDA.l SpriteTable+15,x
    TYX
    STA !extra_prop_2,x
    PLA
    STA !extra_prop_1,x
    PLA
    BNE +
    LDA !extra_bits,x
    AND #$F7
    STA !extra_bits,x
+   PLP
    PLY
    PLX
    RTL

Init:
    LDA #$08
    STA $14C8,x
    LDA !extra_bits,x
    AND #$08
    BNE .custom
    JML $018177
.custom
    JSL CallInit
    JML $0185C2                 ; an RTS in bank 01: back to whoever ran the sprite

Main:
    STZ $1491
    LDA !extra_bits,x
    AND #$08
    BNE .custom
    LDA $9E,x
    JML $0185C8
.custom
    JSL CallMain
    JML $0185C2

; Statuses other than INIT and MAIN run SMW's code for the acts-like, unless the second extra
; property byte says otherwise: bit 7 runs MAIN in their place, bit 6 runs MAIN before them.
Status:
    LDA !extra_bits,x
    AND #$08
    BEQ .vanilla
    LDA $14C8,x
    CMP #$02
    BCC .vanilla
    CMP #$08
    BEQ .vanilla
    LDA !extra_prop_2,x
    BMI .mainOnly
    AND #$40
    BEQ .vanilla
    JSL CallMain
.vanilla
    LDA $14C8,x
    BNE +
    JML $018151
+   JML $01812C
.mainOnly
    JSL CallMain
    JML $0185C2

; Enter the sprite's INIT or MAIN with JML [$00], X = the slot, so its RTL comes back to the JSL
; that called these. CallMainOf runs a number's MAIN without a slot: shooters and generators.
CallInit:
    PHX
    LDA !new_sprite_num,x
    JSR EntryOf
    LDA.l SpriteTable+8,x
    STA $00
    LDA.l SpriteTable+9,x
    STA $01
    LDA.l SpriteTable+10,x
    BRA CallEntry
CallMainOf:
    PHX
    BRA +
CallMain:
    PHX
    LDA !new_sprite_num,x
+   JSR EntryOf
    LDA.l SpriteTable+11,x
    STA $00
    LDA.l SpriteTable+12,x
    STA $01
    LDA.l SpriteTable+13,x
CallEntry:
    STA $02
    SEP #$10
    PLX
    JML [$0000]

; A = a sprite number (8-bit). X = its table entry's offset, 16-bit X and 8-bit A on return: a
; per-level number (B0-BF) in a level that has its own sprites reads that level's block, which
; LevelBlocks gives already less B0's offset, so both cases are base + number * 16.
EntryOf:
    REP #$30
    AND #$00FF
    ASL
    ASL
    ASL
    ASL
    PHA
    CMP #$0B00
    BCC .global
    CMP #$0C00
    BCS .global
    LDA $010B                   ; the level being played
    AND #$01FF
    ASL
    TAX
    LDA.l LevelBlocks,x
    BRA .add
.global
    LDA #$0000
.add
    CLC
    ADC $01,s
    TAX
    PLA
    SEP #$20
    RTS

; ---- Shooters and generators ----

; A = the record's number, Y at its second byte, X counts records.
Kind:
    PHA
    DEY
    LDA [$CE],y
    INY
    AND #$08
    BEQ .vanilla
    LDA $01,s
    CMP #$C0
    BCC .vanilla
    CMP #$E0
    BCS .vanilla
    CMP #$D0
    BCS .generator
    PLA
    AND #$7F                    ; ShooterNumber's sign that this one is custom
    JML $02A8D8                 ; SMW's shooter spawn: JSR $AB78 : BRA
.generator
    PLA
    STA $18B9
    STZ $1938,x                 ; as SMW leaves a generator's record: not marked loaded
    JML $02A846                 ; on to the next record
.vanilla
    PLA
    CMP #$E7
    BCC +
    JML $02A86A
+   JML $02A88C

ShooterNumber:
    LDA $04
    BMI +                       ; SMW's own: C9 or CA
    ORA #$80
    JML $02ABA5                 ; STA $1783,x
+   SEC
    SBC #$C8
    JML $02ABA5

; A = $1783,x (not 0), X = the shooter slot. A custom shooter's timer counts down every other
; frame as SMW's do (PIXI's shooters test it and set it, and leave the counting to the driver).
ShooterRun:
    CMP #$C0
    BCS .custom
    LDY $17AB,x
    BEQ +
    JML $02B39A
+   JML $02B3A4
.custom
    LDY $17AB,x
    BEQ +
    PHA
    LDA $13
    LSR
    BCC ++
    DEC $17AB,x
++  PLA
+   JSL CallMainOf
    JML $02B3A7                 ; on to the next shooter

GeneratorRun:
    LDA $18B9
    BEQ .none
    CMP #$D0
    BCS .custom
    JML $02B003
.custom
    LDY $9D                     ; not while sprites are frozen, as SMW's
    BNE .none
    JSL CallMainOf
.none
    JML $02B02A                 ; an RTS in bank 02