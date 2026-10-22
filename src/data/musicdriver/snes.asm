; Pipe Dream's music, the main CPU's side: SMW's own uploads give way to ours, and the NMI hands songs and sound effects to
; our engine (data/musicdriver/main.asm). Written for this project from SMW's disassembly (reference/smw-disasm/bank_00.asm)
; and the SPC700 boot ROM's documented protocol; no part of AddmusicK's patch is used. LoROM. data/MusicInsert.cs assembles
; it with the tables after it: BootList (power-on), SongLists (a long pointer per song number, 0 for none) and Global (1 per
; song number that is always in ARAM).
;
; An upload is a block list: a count, then a long pointer per block; a block is a length, an ARAM address and that many bytes
; (a length of 0: the address is where the SPC jumps). Lists, not one stream, because a song and its samples outgrow a bank.

!Loaded = $1DFD                         ; the local song in ARAM (0: none), and the song asked of the engine until it says it
!Pending = $1DFE                        ; has it ($1DFD-$1DFF were the old sound code's own, which this replaces)

; ---- power-on: our engine, effects, global songs and SMW's samples, instead of SMW's engine ----
org $0080E8                             ; UploadSPCEngine (called once, from the reset code)
    JSL Boot
    RTS

org $0080FD                             ; UploadSamples: samples come with the songs now
    RTS
org $00810E                             ; UploadMusicBank1 (overworld)
    RTS
org $008148                             ; the level music bank, when a level is entered from the map: the song is
    STZ !Loaded                         ; uploaded again, which starts the engine afresh as SMW's upload did (the hurry-up
    RTS                                 ; ends). ponytail: a level whose song is global keeps a hurry-up; never seen in SMW
org $008159                             ; UploadMusicBank3 (the ending)
    RTS

; ---- every frame: the song and the effects to the engine ----
org $008179                             ; NMI, after reading $4210, where SMW wrote $1DF9-$1DFC to the ports
    JSL Nmi
    BRA $0081AA

freecode

Boot:
    PHP
    PHB
    PHK
    PLB
    SEP #$30
    LDA.b #BootList
    LDY.b #BootList>>8
    LDX.b #BootList>>16
    JSR Upload
    PLB
    PLP
    RTL

; A song number in $1DFB goes to port 2, after its blocks are uploaded if it is local and not the one in ARAM already. It
; stays on port 2 until the engine echoes it (it may be busy: an echo buffer being made holds it up to a few hundred ms),
; then port 2 goes back to 0 so the same song can be asked for again. Then the effects, as SMW wrote them.
Nmi:
    LDA !Pending
    BEQ .new
    CMP $2142
    BNE .send
    STZ !Pending
    STZ $2142
    BRA .effects
.new:
    LDA $1DFB
    BEQ .effects
    STZ $1DFB
    STA !Pending
    TAX
    LDA.l Global,x
    BNE .ask
    CPX !Loaded
    BEQ .ask
    REP #$30
    TXA                                 ; its list: three bytes a song number
    AND #$00FF
    PHA
    ASL
    CLC
    ADC $01,s
    TAX
    PLA
    SEP #$20
    LDA.l SongLists+2,x
    BNE +
    SEP #$30
    LDA #$FF                            ; no such song: the music fades out (AddmusicK's)
    STA !Pending
    BRA .send
+   PHA
    LDA.l SongLists+1,x
    PHA
    LDA.l SongLists,x
    SEP #$10
    PLY
    PLX
    PHA
    LDA #$FF                            ; the engine to the boot ROM, to take the upload
    STA $2141
    PLA
    JSR Upload
    SEP #$30
    LDA !Pending
    STA !Loaded
.ask:
    LDA !Pending
.send:
    STA $2142
.effects:
    LDA $1DF9
    STA $2140
    LDA $1DFA
    STA $2141
    LDA $1DFC
    STA $2143
    STZ $1DF9
    STZ $1DFA
    STZ $1DFC
    RTL

; A/Y/X = the block list (low, high, bank), 8-bit registers. $00-$0B are its own while it runs, put back after: the code the
; NMI interrupted may be using them. $00-$02 the list, $04 the next kick, $05 blocks left, $06-$08 the block, $0A the place
; in the list.
Upload:
    PHP
    PEI ($00)
    PEI ($02)
    PEI ($04)
    PEI ($06)
    PEI ($08)
    PEI ($0A)
    STA $00
    STY $01
    STX $02
    REP #$30
    LDA #$BBAA                          ; the boot ROM is ready
-   CMP $2140
    BNE -
    SEP #$20
    LDA [$00]
    STA $05
    LDA #$CC                            ; the first block's kick
    STA $04
    REP #$20
    LDA #$0001
    STA $0A
.block:
    REP #$30
    LDY $0A
    LDA [$00],y
    STA $06
    INY
    INY
    SEP #$20
    LDA [$00],y
    STA $08
    INY
    STY $0A
    JSR Block
    SEP #$20
    DEC $05
    BNE .block
    REP #$30
    PLA
    STA $0A
    PLA
    STA $08
    PLA
    STA $06
    PLA
    STA $04
    PLA
    STA $02
    PLA
    STA $00
    PLP
    RTS

; [$06] = one block: its length, its ARAM address, its bytes. $04 holds the kick for it, and on return the next one's.
Block:
    REP #$30
    LDY #$0000
    LDA [$06],y                         ; length
    TAX
    INY
    INY
    LDA [$06],y                         ; address
    STA $2142
    INY
    INY
    SEP #$20
    CPX #$0000
    BEQ .jump
    LDA #$01                            ; data follows
    STA $2141
    LDA $04
    STA $2140
-   CMP $2140
    BNE -
    LDA #$00                            ; the counter, on port 0, each byte on port 1 beside it
.byte:
    XBA
    LDA [$06],y
    INY
    XBA
    REP #$20
    STA $2140
    SEP #$20
-   CMP $2140
    BNE -
    INC A
    DEX
    BNE .byte
    CLC                                 ; the next kick: past the counter, never 0
    ADC #$03
    BNE +
    LDA #$03
+   STA $04
    RTS
.jump:
    STZ $2141                           ; 0: jump to the address
    LDA $04
    STA $2140
-   CMP $2140
    BNE -
    RTS
