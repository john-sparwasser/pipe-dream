; Pipe Dream's custom sprite driver (reference/PIXI_PORT.md, phase 2).
;
; Written from SMW's own code at each hook and PIXI's documented on-ROM format, so sprites written
; for PIXI run unchanged and anything that reads a PIXI ROM finds the table. The build assembles
; this after every sprite, with defines.asm and a generated file that sets:
;   !SizeTable  Lunar Magic's sprite size table (0x400 bytes, record length per extra bits/number)
;   !LmStep     where $02A846 jumped before (Lunar Magic's own record step), or 0 on a vanilla loader
; and holds SpriteTable: 0x100 entries of 0x10 bytes — type, acts-like, the six tweaker bytes,
; INIT and MAIN (long), the two extra property bytes — and StatusTable, laid out the same, each
; entry the sprite's status entry points (see StatusSlot).
;
; A sprite is custom when its level data sets extra bit $08; its number is then the table's, and
; $9E holds its acts-like so SMW's own code treats it as that sprite wherever we do not step in.

if !SA1 == 0                    ; on SA-1 defines.asm has set sa1rom
    lorom
endif

; ---- The header PIXI keeps at $02FFE2, so a tool reading a PIXI ROM finds the table ----
org $02FFE2
    db "STSD"
    db 143                      ; the format's version: PIXI 1.43's
    db $00                      ; flags: no per-level sprites
    db $00,$00
    db $FF,$FF,$FF,$FF
    dl SpriteTable
    dl $FFFFFF,$FFFFFF,$FFFFFF,$FFFFFF   ; per-level tables, then three unused
    dl $FFFFFF                  ; extra status pointers: none in PIXI's layout (ours are StatusTable)

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
; number there. Kind hands it over with bit 7 clear, which no SMW shooter (C9, CA) has. On SA-1 the
; pack's own JML at $02AB9E covers the LDA (ShooterFix2: REP #$10 : PLY : LDA $04), so ours takes
; its place and does its part first.
if !SA1
org $02AB9E
    JML ShooterNumber
else
org $02ABA0
    JML ShooterNumber
    NOP
endif
; The shooter run's `DEC : JSL $0086DF`, by $1783,x: a custom one (C0-CF) runs its MAIN.
org $02B3AB
    JML ShooterRun
    NOP
; The generator run's `LDA $18B9 : BEQ`: a custom one (D0-DF; SMW's are 1-F) runs its MAIN.
org $02AFFE
    JML GeneratorRun
    NOP

; ---- The load table: 255 sprites a level ----
; SMW marks each level sprite list record it has spawned in $1938, one byte per record by its
; index ($161A,x), so a sprite that leaves the screen is not spawned twice. That table is 128
; bytes, so a 129th record writes past it. PIXI moves it to $7FAF00 with room for 255 (index FF
; is "not from the level"), and !1938 means $7FAF00 to every sprite since PIXI 1.2.15, so ours
; does too. These are every place SMW or Lunar Magic's loader reads or writes $1938, found by
; scanning a built ROM for each instruction that addresses it (the hits in bank 0D are data).
; None of these places is long-addressable with Y, so each calls a routine. On SA-1 the pack has
; done all of this already, at these very places, for its own table at $418A00 (!1938 there).
if !SA1 == 0
org $02A856                     ; the loader: LDA $1938,x : BNE next : STX $02 : INC $1938,x
    JML LoadedCheck
    NOP #6
org $02ABF2                     ; level load clears it (LDX #$7F : STZ $1938,x : DEX : BPL)
    JSL LoadedClearAll
    NOP #4
org $02A8BB                     ; a generator's record is never marked: STZ $1938,x : BRA
    JML LoadedGenerator
org $02A936                     ; no free slot, so the record may spawn later: LDX $02 : STZ $1938,x
    JSL LoadedClearRecord
    NOP
org $02FAE6                     ; LDX $0FBC,y : STZ $1938,x
    JSL LoadedClearFBC
    NOP #2
; A sprite gone for good (or killed in a way it may come back) frees its record:
; `LDA #$00 : STA $1938,y` with Y its $161A,x (or a shooter's $17B3,x).
org $01AC9C
    JSL LoadedClearY
    NOP
org $02AB99
    JSL LoadedClearY
    NOP
org $02D088
    JSL LoadedClearY
    NOP
org $02FF15
    JSL LoadedClearY
    NOP
org $038712
    JSL LoadedClearY
    NOP
org $03B8BA
    JSL LoadedClearY
    NOP
endif
; Lunar Magic warns about a level with more than 128 sprites unless bit 0 here is clear (its help,
; "Custom Sprite List Sizes": "0x801E0 PC", which counts the copier header). Every PIXI ROM on
; this machine has it clear.
org $0FFFE0
    db read1($0FFFE0)&$FE

; ---- Cluster and extended sprites ----
; A number from !ClusterOffset / !ExtendedOffset up (past SMW's own) is a custom one, run from
; ClusterTable / ExtendedTable by its list number. SMW reads the number as an index nowhere else
; but the extended sprites' cape check, hooked below.
; The cluster loop's `LDA $1892,X : BEQ` (then a JSR to SMW's dispatch).
org $02F815
    JML ClusterRun
    NOP
; The extended loop's `LDA $170B,X : BEQ`.
org $029B16
    JML ExtendedRun
    NOP
; The cape's check of each extended sprite, `LDA $170B,X : CMP #$02 : BCC`: its clipping is a
; table by number, which a custom one would read past, so that runs the sprite's CAPE instead.
org $029636
    JML ExtendedCape
    NOP
    NOP
    NOP

; ---- Minor extended, bounce, smoke, spinning coin and score sprites ----
; The same for the minor kinds: each loop's `LDA number,X : BEQ` sends a number from the kind's
; offset up to its table. Every other read of these numbers looks for a free slot or compares
; with one of SMW's; only the dispatches below index by it.
; The minor extended loop's (then STX $1698 and a JSR to SMW's dispatch).
org $028B69
    JML MinorExtendedRun
    NOP
; The bounce dispatch's, at its top (it is a JSR from the loop that runs smoke too).
org $02904D
    JML BounceRun
    NOP
; The smoke dispatch's, at its top (then AND #$7F: bit 7 is a flag).
org $0296C0
    JML SmokeRun
    NOP
; The spinning coin loop's (then a JSR to SMW's one kind).
org $0299D7
    JML SpinningCoinRun
    NOP
; The score loop's (then a JSR to SMW's, which reads its tables by number).
org $02ADBD
    JML ScoreRun
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

; 16-bit X and Y for a table lookup, and back as they were: on LoROM the loader's are 8-bit, on SA-1 they
; may already be 16-bit, and a SEP #$10 there would cut Y's high byte.
macro index16()
    if !SA1
        PHP
    endif
    REP #$10
endmacro
macro index_back()
    if !SA1
        PLP
    else
        SEP #$10
    endif
endmacro

; Y at the record's second byte; X counts records. 8-bit A/X/Y — but 16-bit X and Y on SA-1, where
; the pack runs the loader so (boost/sprite_load.asm), and Y then goes past 255.
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
    %index16()
    TAX
    LDA.l !SizeTable,x
    %index_back()
    PLX
    CMP #$03
    BCS +
    LDA #$03
+   DEC                         ; Y was one byte in: it moves on by length - 1
if !SA1
    REP #$21                    ; all 16 bits of Y
    AND #$00FF
    PHY
    ADC $01,s
    TAY
    PLA
    SEP #$20
else
    CLC
    PHY
    ADC $01,s
    TAY
    PLA
endif
    INX
if !LmStep != 0
if !SA1
    DEY                         ; Lunar Magic's step goes on with BPL on Y: Y's own sign, all 16 bits
    INY
else
    TYA                         ; Lunar Magic's step goes on with BPL on Y, rebasing $CE
endif
    JML !LmStep+3               ; past its own INX : INY : INY
else
    JML $02A82E
endif

KeepBitsX:
    PHA
    AND #$01
    STA !14E0,x
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
    STA !14D4,x
    RTL

; Y at the record's number. SMW's setup clears the slot (and with it the bits KeepBits put there),
; so they are kept across it. The setup is whatever the hook displaced (!SpawnSetup): SMW's $07F7D2,
; or on SA-1 the pack's wrapper of it for the loader's 16-bit index registers.
Spawn:
    LDA !extra_bits,x
    PHA
    JSL !SpawnSetup
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
    %index16()
    TAX
    LDA.l !SizeTable,x
    %index_back()
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
if !SA1                         ; Y is 16-bit here, and past 255 in a long list
    REP #$20
    TYA                         ; carry set: the first extra byte is one past Y
    ADC $CE
    SEP #$20
    STA !extra_byte_1,x
    XBA
    STA !extra_byte_2,x
    LDA $D0
    ADC #$00
    STA !extra_byte_3,x
else
    TYA                         ; carry set: the first extra byte is one past Y
    ADC $CE
    STA !extra_byte_1,x
    LDA $CF
    ADC #$00
    STA !extra_byte_2,x
    LDA $D0
    ADC #$00
    STA !extra_byte_3,x
endif
.done
    PLA
    STA $00
    PLY
    RTS

ClearSlot:
    LDA #$00
    STA !extra_bits,x
    LDA #$01
    STA !15A0,x
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
    STA.w !9E,y
    LDA.l SpriteTable+2,x
    STA !1656,y
    LDA.l SpriteTable+3,x
    STA !1662,y
    LDA.l SpriteTable+4,x
    STA !166E,y
    AND #$0F
    STA !15F6,y
    LDA.l SpriteTable+5,x
    STA !167A,y
    LDA.l SpriteTable+6,x
    STA !1686,y
    LDA.l SpriteTable+7,x
    STA !190F,y
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
    STA !14C8,x
    LDA !extra_bits,x
    AND #$08
    BNE .custom
    JML $018177
.custom
    JSL CallInit
    JML $0185C2                 ; an RTS in bank 01: back to whoever ran the sprite

Main:
    STZ $1491|!addr
    LDA !extra_bits,x
    AND #$08
    BNE .custom
    LDA !9E,x
    JML $0185C8
.custom
    JSL CallMain
    JML $0185C2

; Statuses other than INIT and MAIN run the sprite's own code for the status where it printed
; one (CARRIABLE, KICKED, CARRIED, MOUTH, GOAL), else SMW's code for the acts-like. The second
; extra property byte overrides both: bit 7 runs MAIN in their place, bit 6 runs MAIN first.
Status:
    LDA !extra_bits,x
    AND #$08
    BEQ .vanilla
    LDA !14C8,x
    CMP #$02
    BCC .vanilla
    CMP #$08
    BEQ .vanilla
    LDA !extra_prop_2,x
    BMI .mainOnly
    AND #$40
    BEQ +
    JSL CallMain
+   JSL CallStatus
    BCS .done
.vanilla
    LDA !14C8,x
    BNE +
    JML $018151
+   JML $01812C
.mainOnly
    JSL CallMain
.done
    JML $0185C2

; X = the slot. Runs the sprite's own code for its status ($14C8, read again: MAIN may have just
; changed it) with the data bank set to the code's, and returns carry set; carry clear when it
; has none.
CallStatus:
    LDA !14C8,x
    CMP #$10
    BCS .none
    PHX
    TAX
    LDA.l StatusSlot,x
    PLX
    CMP #$FF
    BEQ .none
    STA $00
    STZ $01
    PHB
    PHX
    LDA !new_sprite_num,x
    JSR EntryOf
    REP #$21
    TXA
    ADC $00
    TAX
    SEP #$20
    LDA.l StatusTable+2,x
    BEQ .noCode                 ; bank 00: no code for this status
    STA $02
    PHA
    PLB
    REP #$20
    LDA.l StatusTable,x
    STA $00
    SEP #$30
    PLX
    JSL .enter
    PLB
    SEC
    RTL
.enter
    JML [$0000|!dp]
.noCode
    SEP #$10
    PLX
    PLB
.none
    CLC
    RTL

; Status → where its pointer sits in a StatusTable entry ($FF: the status has none). An entry is
; CARRIABLE (09), KICKED (0A), CARRIED (0B), MOUTH (07), GOAL (0C), a long each, then a pad byte,
; so it is 16 bytes and EntryOf finds it as it finds the sprite's SpriteTable entry.
StatusSlot:
    db $FF,$FF,$FF,$FF,$FF,$FF,$FF,$09,$FF,$00,$03,$06,$0C,$FF,$FF,$FF

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
    JML [$0000|!dp]

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
    LDA $010B|!addr                   ; the level being played
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
    STA $18B9|!addr
    LDA #$00                    ; as SMW leaves a generator's record: not marked loaded
    STA !1938,x
    JML $02A846                 ; on to the next record
.vanilla
    PLA
    CMP #$E7
    BCC +
    JML $02A86A
+   JML $02A88C

ShooterNumber:
if !SA1
    REP #$10
    PLY
endif
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
    LDY $17AB|!addr,x
    BEQ +
    JML $02B39A
+   JML $02B3A4
.custom
    LDY $17AB|!addr,x
    BEQ +
    PHA
    LDA $13
    LSR
    BCC ++
    DEC $17AB|!addr,x
++  PLA
+   JSL CallMainOf
    JML $02B3A7                 ; on to the next shooter

GeneratorRun:
    LDA $18B9|!addr
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

; ---- Cluster and extended sprites ----

; A = a list number (the custom number less its kind's offset): run what <table> holds for it,
; through CallLong.
macro call_listed(table)
    PHA
    LDA.b #<table>>>16
    LDY.b #<table>>>8
    STY $04
    LDY.b #<table>
    STY $03
    STA $05
    PLA
    JSL CallLong
endmacro

; X = the cluster slot. Bit 7 of a custom number is its extra bit, so it is not the number.
ClusterRun:
    LDA $1892|!addr,x
    BEQ .next
    AND #$7F
    CMP #!ClusterOffset
    BCS .custom
    JML $02F81A                 ; SMW's: JSR to its dispatch, then the next slot
.custom
    SBC #!ClusterOffset         ; carry set
    %call_listed(ClusterTable)
.next
    JML $02F81D

; X = the extended slot. A custom one's timer ($176F) counts down as SMW's do.
ExtendedRun:
    LDA $170B|!addr,x
    BNE +
    JML $029B15                 ; an RTS: the slot is empty
+   CMP #!ExtendedOffset
    BCS .custom
    JML $029B1B                 ; SMW's: its timer, then its dispatch
.custom
    LDY $9D
    BNE +
    LDY $176F|!addr,x
    BEQ +
    DEC $176F|!addr,x
+   SEC
    SBC #!ExtendedOffset
    %call_listed(ExtendedTable)
    JML $029B15

; X = the extended slot, while the cape spins. SMW's own (02 up) go through its contact check;
; a custom one runs its CAPE, if it printed one.
ExtendedCape:
    LDA $170B|!addr,x
    CMP #!ExtendedOffset
    BCS .custom
    CMP #$02
    BCS +
    JML $029653                 ; on to the next
+   JML $02963D                 ; SMW's: clipping, contact, a puff of smoke
.custom
    SBC #!ExtendedOffset        ; carry set
    %call_listed(ExtendedCapeTable)
    JML $029653

; ---- Minor extended, bounce, smoke, spinning coin and score sprites ----
; X = the slot in each. SMW's own run as before; a custom one runs its MAIN, frozen or not, and
; only bounce sprites have a timer the loop counts down for them.

MinorExtendedRun:
    LDA $17F0|!addr,x
    BEQ .next
    CMP #!MinorExtendedOffset
    BCS .custom
    JML $028B6E                 ; SMW's: STX $1698, JSR to its dispatch
.custom
    STX $1698|!addr                   ; the slot being run, as SMW's dispatch has it
    SBC #!MinorExtendedOffset   ; carry set
    %call_listed(MinorExtendedTable)
.next
    JML $028B74                 ; the next slot

; A custom one's timer ($16C5) counts down as SMW's do.
BounceRun:
    LDA $1699|!addr,x
    BNE +
    JML $02904C                 ; an RTS: the slot is empty
+   CMP #!BounceOffset
    BCS .custom
    JML $029052                 ; SMW's: its timer, then its dispatch
.custom
    LDY $9D
    BNE +
    LDY $16C5|!addr,x
    BEQ +
    DEC $16C5|!addr,x
+   SEC
    SBC #!BounceOffset
    %call_listed(BounceTable)
    JML $02904C

SmokeRun:
    LDA $17C0|!addr,x
    BEQ .none
    AND #$7F
    CMP #!SmokeOffset
    BCS .custom
    JML $0296C7                 ; SMW's dispatch
.custom
    SBC #!SmokeOffset           ; carry set
    %call_listed(SmokeTable)
.none
    JML $0296D7                 ; an RTS

SpinningCoinRun:
    LDA $17D0|!addr,x
    BEQ .next
    CMP #!SpinningCoinOffset
    BCS .custom
    JML $0299DC                 ; SMW's: JSR to it
.custom
    SBC #!SpinningCoinOffset    ; carry set
    %call_listed(SpinningCoinTable)
.next
    JML $0299DF                 ; the next slot

ScoreRun:
    LDA $16E1|!addr,x
    BEQ .next
    CMP #!ScoreOffset
    BCS .custom
    JML $02ADC2                 ; SMW's: JSR to it
.custom
    SBC #!ScoreOffset           ; carry set
    %call_listed(ScoreTable)
.next
    JML $02ADC5                 ; the next slot

; $03-$05 = a table of longs (0: none), A = an index into it (0-7F), X = the slot. Runs the code
; there with Y = X and the data bank set to its bank, as cluster sprites expect, and returns carry
; set; carry clear when there is none. Uses $00-$05.
CallLong:
    CMP #$80
    BCS .out
    PHB
    PHX
    REP #$30
    AND #$00FF
    STA $00
    ASL
    ADC $00
    TAY
    SEP #$20
    INY
    INY
    LDA [$03],y
    BEQ .none                   ; bank 00: nothing there
    STA $02
    PHA
    PLB
    DEY
    DEY
    REP #$20
    LDA [$03],y
    STA $00
    SEP #$30
    PLX
    TXY
    JSL .enter
    PLB
    SEC
    RTL
.enter
    JML [$0000|!dp]
.none
    SEP #$30
    PLX
    PLB
.out
    CLC
    RTL

; ---- The load table: 255 sprites a level ----
; Each leaves the registers (and flags) the code after its hook reads as the instructions it
; replaces did. A, X and Y are 8-bit at every site. LoROM only, as the hooks are.
if !SA1 == 0

; X = the record's index: on to the next record if it was spawned already, else mark it and spawn.
LoadedCheck:
    LDA !1938,x
    BEQ +
    JML $02A846
+   INC
    STA !1938,x
    DEC
    STX $02
    JML $02A860

; X ends $FF with N set, as SMW's loop leaves it.
LoadedClearAll:
    PHA
    LDX #$00
    LDA #$00
-   STA !1938,x
    INX
    BNE -
    PLA
    LDX #$FF
    RTL

LoadedGenerator:
    PHP
    PHA
    LDA #$00
    STA !1938,x
    PLA
    PLP
    JML $02A89A

LoadedClearRecord:
    LDX $02
    PHP
    PHA
    LDA #$00
    STA !1938,x
    PLA
    PLP
    RTL

LoadedClearFBC:
    LDX $0FBC|!addr,y
    PHP
    PHA
    LDA #$00
    STA !1938,x
    PLA
    PLP
    RTL

; Y = the record's index. A = 0 after, as SMW's LDA #$00 left it.
LoadedClearY:
    PHX
    TYX
    LDA #$00
    STA !1938,x
    PLX
    LDA #$00
    RTL
endif
