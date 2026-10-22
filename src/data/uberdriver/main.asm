; Pipe Dream's UberASM driver (reference/UBERASM_PORT.md, phase 2).
;
; Written from UberASM Tool 2.1's readme (when each label runs, what a resource gets) and SMW's disassembly
; (reference/smw-disasm), so resources written for UberASM Tool run unchanged; none of the tool's ASM is used. The build
; assembles this after every resource, library file and shared routine, with defines.asm and a generated file that gives:
;   !FreeRam                 two bytes for the game mode last seen (the list's freeram:)
;   !UberGlobalInit/Main/Nmi/Load, !UberStatusMain   1 when the global or status bar file has that label
;   LevelLoadDispatch, LevelInitDispatch, LevelMainDispatch, LevelEndDispatch, LevelNmiDispatch,
;   GameModeInitDispatch, …MainDispatch, …EndDispatch, …NmiDispatch,
;   OverworldInitDispatch, …MainDispatch, …EndDispatch, …NmiDispatch
;                            each JSL'd with A/X/Y 8-bit; each runs the resources the list gives the level ($010B), game
;                            mode ($0100) or map ($1F11 by the player at $0DB3), * first, and returns with RTL.
; Every hook jumps out, runs what it displaced and jumps back, so the hooked code itself is unchanged; the build checks each
; site still holds SMW's bytes first.

lorom

; ---- Hooks ----
org $008064                     ; reset, after RAM is cleared: LDA #$03 : STA $2101
    JSL GlobalInit
    NOP
org $008070                     ; the main loop's frame: INC $13 : JSR $9322 (the game mode) : STZ $10
    JML Frame
org $008174                     ; NMI, registers pushed and DBR = 0: SEP #$30 : LDA $4210
    JML Nmi
    NOP
org $008E1A                     ; the status bar's first instructions: LDA $1493 : ORA $9D
    JML StatusBar
    NOP
org $00A179                     ; game mode $0C loading the overworld: LDA #$02 : STA $0D9B
    JML OverworldInit
    NOP
org $00A1C3                     ; game mode $0E: JSL $7F8000 : JSL $048241 (the map) : JMP $8494 ($0420 → $0400)
    JML Overworld
org $00A242                     ; game mode $14, past the message box, before the pause check: LDA $13D4 : BEQ $A28A
    JML LevelMain
    NOP
org $00A2EA                     ; game mode $14's end: PLA : STA $1D : PLA : STA $1C : JMP $8494 ($0420 → $0400)
    JML LevelEnd
org $00A5E8                     ; game mode $12, the level loaded, before fade-in: LDX $0701 : STX $0903
    JML LevelInit
    NOP #2
org $05808F                     ; just after LoadLevel filled the level table: SEP #$30 : LDA $0100
    JSL LevelLoad
    NOP

freecode

; Reset. The game mode word starts at $FFFF, which no game mode is, so game mode 0's init runs on the first frame.
GlobalInit:
    LDA #$03
    STA.l $002101
    REP #$20
    LDA #$FFFF
    STA.l !FreeRam
    SEP #$20
if !UberGlobalInit
    JSR UberGlobal_init
    SEP #$30
endif
    RTL

; Each frame: the game mode's init (its first frame) or main, SMW's game mode code, then its end. SMW's is a JSR into bank 00
; ending in RTS, so it is entered with a return through the RTL at $0084CF.
Frame:
    INC $13
if !UberGlobalMain                  ; 1.x global main, as 2.x's readme moves it (game mode *)
    JSR UberGlobal_main
    SEP #$30
endif
    REP #$20
    LDA.l $7E0100
    AND #$00FF
    CMP.l !FreeRam
    STA.l !FreeRam
    SEP #$20
    BEQ .main
    JSL GameModeInitDispatch
    BRA .game
.main:
    JSL GameModeMainDispatch
.game:
    PHK
    PEA.w .back-1
    PEA.w $84CF-1
    JML $009322
.back:
    SEP #$30
    JSL GameModeEndDispatch
    STZ $10
    JML $00806B

; NMI: the game mode's, then the level's (modes $13-14, and $05-07 for the title screen) or the map's ($0D-0E). $00-$02 are
; the dispatch's and the extra bytes' pointer; the code NMI interrupted may be using them.
Nmi:
    SEP #$30
    LDA $4210
    LDA $00
    PHA
    LDA $01
    PHA
    LDA $02
    PHA
if !UberGlobalNmi                   ; 1.x global nmi (game mode *)
    JSR UberGlobal_nmi
    SEP #$30
endif
    JSL GameModeNmiDispatch
    LDA.l $7E0100
    CMP #$13
    BEQ .level
    CMP #$14
    BEQ .level
    CMP #$0D
    BEQ .map
    CMP #$0E
    BEQ .map
    CMP #$05
    BCC .done
    CMP #$08
    BCS .done
.level:
    JSL LevelNmiDispatch
    BRA .done
.map:
    JSL OverworldNmiDispatch
.done:
    PLA
    STA $02
    PLA
    STA $01
    PLA
    STA $00
    JML $008179

StatusBar:
if !UberStatusMain
    JSR UberStatus_main
    SEP #$30
endif
    LDA $1493
    ORA $9D
    JML $008E1F

OverworldInit:
    LDA #$02
    STA $0D9B
    JSL OverworldInitDispatch
    JML $00A17E

Overworld:
    JSL $7F8000
    JSL OverworldMainDispatch
    JSL $048241
    JSL OverworldEndDispatch
    JML $008494

LevelMain:
    JSL LevelMainDispatch
    LDA $13D4
    BEQ +
    JML $00A247                     ; paused: SMW's BRA to the select check
+   JML $00A28A

LevelEnd:
    PLA
    STA $1D
    PLA
    STA $1C
    JSL LevelEndDispatch
    JML $008494

LevelInit:
    LDX $0701                       ; (X is 16-bit here: absolute either way)
    STX $0903
    SEP #$30
    JSL LevelInitDispatch
    JML $00A5EE

LevelLoad:
    SEP #$30
if !UberGlobalLoad                  ; 1.x global load (level *)
    JSR UberGlobal_load
    SEP #$30
endif
    JSL LevelLoadDispatch
    LDA.l $7E0100
    RTL
