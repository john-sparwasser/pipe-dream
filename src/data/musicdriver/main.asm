; Pipe Dream's music engine: an SPC700 program that plays songs compiled by music/MmlCompiler.cs.
;
; Written from AddmusicK's documented commands and from measuring what its engine tells the DSP
; (reference/AMK_ENGINE.md); no part of AddmusicK's engine is used. Assembled with Asar's spc700
; mode onto a flat buffer: file offset 0 is ARAM $0400, where the engine starts.
;
; Memory:  $0000-$00EF direct page (some eight-byte per-channel arrays at $40-$DF)   $0100-$01FF stack
;          $0200-$03EF per-channel state, eight-byte arrays   $03F0 song info   $0400 this program and its tables,
;          then the sample directory, the song and its samples (music/MusicLayout.cs), the echo buffer at the top.
;
; The main CPU talks to it through the four ports (each echoed back once read):
;   port 0  sound effect for channel 6   port 1  control   port 2  song to play ($FF stops)   port 3  effect for channel 7

arch spc700
norom
org $0000
base $0400

!SongInfo = $03F0                 ; the song as music/MusicLayout.cs placed it: its address (word), its instrument table (word),
                                  ; the sample directory's page
!SfxInfo = $03F5                  ; the sound effects: instruments (word), the $1DF9 table (word), the $1DFC table (word), how many
                                  ; of each table there are (two bytes)
!GlobalInfo = $03FD               ; the global songs' table (word): a count, then number, song (word), instrument table (word)

; ---- sound-effect channels: two-byte arrays, index 0 = voice 6 ($1DF9), 1 = voice 7 ($1DFC and $1DFA)
!s_ptrL = $0100                   ; (the bottom of the stack page: the stack itself stays near $01EF)
!s_ptrH = $0102
!s_startL = $0104                 ; where the effect began, for $FE
!s_startH = $0106
!s_wait = $0108                   ; ticks until the next read; 0: no effect playing
!s_koff = $010A                   ; ticks until the note is released
!s_volL = $010C
!s_volR = $010E
!s_tune = $0110
!s_note = $0112                   ; the note sounding (whole semitones) and its fraction, for slides
!s_frac = $0114
!s_stepL = $0116                  ; a slide: the step a tick (1/256 semitone, signed), ticks left, ticks before it starts
!s_stepH = $0118
!s_slLeft = $011A
!s_slDelay = $011C
!s_dur = $011E                    ; the length notes are given
!s_target = $0120                 ; where the slide ends (it lands there exactly)
!s_lastL = $0122                  ; where the last note byte was, for $FF
!s_lastH = $0124
!s_claim = $0126                  ; set when an effect has been asked for but not yet taken its voice

; ---- direct page ----
!t0 = $00
!t1 = $01
!t2 = $02
!t3 = $03
!t4 = $04
!t5 = $05
!t6 = $06
!t7 = $07
!ptr = $08                           ; working pointer into a channel's data (word)
!cur = $0A                           ; the channel being worked on
!tickAcc = $0B
!tempo = $0C
!playing = $0D
!master = $0E                           ; global volume (w)
!konMask = $0F
!koffMask = $10
!lastP2 = $11
!active = $12                           ; channels still running in this phrase
!phrase = $13                           ; where the phrase list is read from (word)
!instr = $15                           ; address of the song's custom instrument table (word)
!ticks = $17                           ; timer ticks still to run in the main loop
!rerun = $18                           ; set once a phrase has restarted within this tick
!gtrans = $19                           ; $E4: semitones every channel is transposed by
!frac = $1A                             ; ComputePitch: the fraction of a semitone above the note
!masterF = $1B                          ; $E1 global volume fade: the fraction, ticks left, step (word), target
!mfLeft = $1C
!mfStepL = $1D
!mfStepH = $1E
!mfTarget = $1F
!tempoF = $20                           ; $E3 tempo fade, likewise
!tfLeft = $21
!tfStepL = $22
!tfStepH = $23
!tfTarget = $24
!eon = $25                              ; echo: channels, volume left and right, the delay set, the delay reserved ($FA $04)
!evolL = $26
!evolR = $27
!edl = $28
!reserved = $29
!efLeft = $2A                           ; $F2 echo volume fade: ticks left, fractions, steps (words)
!evolLF = $2B
!evolRF = $2C
!efLStepL = $2D
!efLStepH = $2E
!efRStepL = $2F
!efRStepH = $30
!pmon = $31                             ; pitch modulation and noise channels, written every tick
!non = $32
!nspcVel = $33                          ; $F4 $08: the N-SPC velocity table instead of SMW's
!lstacc = $34                           ; $F4 $02: light staccato, every channel
!inRemote = $35                         ; set while remote code runs: its $00 returns instead of ending the channel
!syncOn = $36                           ; $F4 $05: the song's tick count goes out on ports 0 and 1
!syncL = $37
!syncH = $38
!sfxAcc = $39                           ; sound effects: their tick accumulator (a fixed tempo, 55),
!sfxBusy = $3A                          ;   the voices they hold ($40, $80): music leaves those alone,
!lastP0 = $3B                           ;   and the ports as last read, so a new value starts one
!lastP1 = $3C
!lastP3 = $3D
!sfxEcho = $3E                          ;   $1DFA $06: effects echo as the song does ($05: they do not, the default)
!sfxNon = $3F                           ;   the effect voices playing noise
!paused = $E0                           ; $1DF9 $11 (pause): the song stands still, every voice off, until $12
!drums = $E1                            ; $1DFA $02/$03: Yoshi's drums heard (channels marked with $F4 $00 or $F4 $06)
!hurry = $E2                            ; $1DF9 $FF: added to the tempo from then on (10), until the engine starts again
!c_yoshi = $E8                          ; per channel (eight bytes): this channel is Yoshi's drums
!c_offL = $40                           ; per channel (eight bytes each, in the direct page): the pitch effects' offset last sent
!c_offH = $48
!c_arpType = $50                        ; $FB, per channel in the direct page: 0 none, 1 arpeggio, 2 trill, 3 glissando,
!c_arpLen = $58                         ;   ticks a step, how many notes, where the list loops to,
!c_arpCount = $60
!c_arpLoop = $68
!c_arpPtrL = $70                        ;   the list (in the song's data),
!c_arpPtrH = $78
!c_arpIdx = $80                         ;   the step it is on, ticks to the next,
!c_arpWait = $88
!c_arpSemis = $90                       ;   the trill's or glissando's interval,
!c_arpOff = $98                         ;   the semitones the note sounds away from itself (signed),
!c_arpKoff = $A0                        ;   and ticks until this step is released (two before the next, measured)
!c_remKonL = $A8                        ; $FC remote code: what runs at each key-on (high byte 0: none),
!c_remKonH = $B0
!c_remL = $B8                           ;   the other event's code,
!c_remH = $C0
!c_remType = $C8                        ;   its type (0 none, 1 after the key-on, 2 before the release, 3 at the release),
!c_remArg = $D0                         ;   its ticks,
!c_remWait = $D8                        ;   and ticks until it runs

; ---- per-channel arrays, eight bytes each ----
!c_ptrl = $0200
!c_ptrh = $0208
!c_dur = $0210                         ; ticks left in the current note
!c_len = $0218                         ; the note length last set
!c_qd = $0220                         ; quantisation: the key-off fraction index 0-7
!c_vel = $0228                         ; velocity 0-15
!c_vol = $0230                         ; channel volume (v)
!c_pan = $0238                         ; pan 0-20
!c_surr = $0240                         ; surround: bit 7 left, bit 6 right
!c_ins = $0248                         ; instrument number
!c_srcn = $0250
!c_ad1 = $0258
!c_ad2 = $0260
!c_gain = $0268
!c_tdd = $0270                         ; tuning, whole part
!c_tee = $0278                         ; tuning, fraction
!c_koff = $0280                         ; ticks until key-off, 0 for none
!c_act = $0288                         ; 1 while the channel is running
!c_retl = $0290                         ; $E9 call: where to return to
!c_reth = $0298
!c_rcnt = $02A0                         ; $E9 call: times left
!c_cal = $02A8                         ; $E9 call: the subroutine
!c_cah = $02B0
!c_lpl = $02B8                         ; $E6 loop start
!c_lph = $02C0
!c_lcnt = $02C8                         ; $E6 loop: repeats left, $FF when the loop is not yet counting
!c_dirty = $02D0                         ; instrument registers to send with the next note
!c_koff2 = $02E0                        ; the key-off every note gets two ticks before it ends
!c_perc = $02D8                         ; percussion note to play at the next note byte, $FF none
!c_note = $02E8                         ; the note byte last played, before transposition
!c_trans = $02F0                        ; $FA $02: semitones this channel is transposed by
!c_fine = $02F8                         ; $EE: fraction of a semitone added to every note
!c_amp = $0300                          ; $FA $03: volume amplification, P + (P x amp >> 8)
!c_vdirty = $0308                       ; the voice volume is to be sent at the end of this tick
!c_age = $0310                          ; song ticks since the note was keyed on (stops at 255)
!c_vibDelay = $0318                     ; $DE: ticks before vibrato starts, its phase step a tick, its depth (0: off)
!c_vibRate = $0320
!c_vibDepth = $0328
!c_bendDelay = $0330                    ; $DD after a note: ticks before the bend, how many it takes (0: no bend),
!c_bendLen = $0338
!c_bendDiff = $0340                     ;   semitones to the target (signed),
!c_bendStepL = $0348                    ;   and the step a tick in 1/256 semitone (signed word)
!c_bendStepH = $0350
!c_volF = $0358                         ; $E8 volume fade: the fraction, ticks left, step (word), target
!c_vfLeft = $0360
!c_vfStepL = $0368
!c_vfStepH = $0370
!c_vfTarget = $0378
!c_panF = $0380                         ; $DC pan fade, likewise
!c_pfLeft = $0388
!c_pfStepL = $0390
!c_pfStepH = $0398
!c_pfTarget = $03A0
!c_vibFade = $03A8                      ; $EA: ticks the vibrato takes to reach its depth after key-on (0: at once)
!c_trmDelay = $03B0                     ; $E5 tremolo: delay, phase step a tick, depth (0: off)
!c_trmRate = $03B8
!c_trmDepth = $03C0
!c_envType = $03C8                      ; $EB/$EC pitch envelope on every note: 0 none, 1 release (bend away), 2 attack (bend in)
!c_envDelay = $03D0
!c_envLen = $03D8
!c_envSemis = $03E0
!c_legato = $03E8                       ; $F4 $01: bit 0 legato on, bit 7 a legato note is sounding

macro dsp(reg)                          ; write A to a DSP register
    mov $F2,#<reg>
    mov $F3,a
endmacro

macro dspi(reg, value)
    mov $F2,#<reg>
    mov $F3,#<value>
endmacro

; ---------------------------------------------------------------- start
Start:
    mov x,#$EF
    mov sp,x
    mov a,#$00
    mov x,#$EF
.clear:
    mov $00+x,a
    dec x
    bne .clear
    mov $00,a
    call InitDsp
    mov a,#$00
    mov x,#$27
-   mov !s_ptrL+x,a
    dec x
    bpl -
    mov $FA,#16                         ; timer 0: 8 kHz / 16 = 500 ticks a second
    mov $F1,#$01
    mov a,#$C0
    mov !master,a
    mov a,#$FF
    mov !lastP2,a                       ; nothing asked for yet
    jmp Main

InitDsp:
    %dspi($6C,$E0)                      ; reset, mute, echo writes off
    %dspi($5C,$FF)                      ; every voice off
    %dspi($0C,$7F)                      ; main volume
    %dspi($1C,$7F)
    %dspi($2C,$00)                      ; no echo
    %dspi($3C,$00)
    %dspi($0D,$00)
    %dspi($4D,$00)
    %dspi($2D,$00)
    %dspi($3D,$00)
    %dspi($7D,$00)
    %dspi($6D,$FF)
    mov x,#$0F
    mov a,#$00
.fir:
    mov $F2,x
    mov $F3,a
    clrc
    mov a,x
    adc a,#$10
    mov x,a
    mov a,#$00
    cmp x,#$8F
    bcc .fir
    %dspi($6C,$20)
    ret

; ---------------------------------------------------------------- the main loop
Main:
    mov a,$FD                           ; timer ticks since last looked
    beq .ports
    mov !ticks,a
.tick:
    call Tick
    dec !ticks
    bne .tick
    call Effects                        ; once however many ticks it was, so a busy moment cannot snowball
.ports:
    call Ports
    bra Main

; One 500 Hz tick: the tempo accumulator decides whether it is also a song tick.
Tick:
    mov a,!sfxAcc                       ; sound effects keep their own time: tempo 55 whatever the song does (measured)
    clrc
    adc a,#56
    mov !sfxAcc,a
    bcc +
    mov x,#$00
    call SfxTick
    mov x,#$01
    call SfxTick
+   mov a,!playing
    beq .done
    mov a,!paused
    bne .done
    mov a,!tempo
    clrc
    adc a,!hurry
    setc
    adc a,!tickAcc                      ; + tempo + 1
    mov !tickAcc,a
    bcc .done
    call SongTick
.done:
    ret

; Pitch and volume effects move every timer tick, between song ticks too, as AddmusicK's do.
Effects:
    mov a,!playing
    beq .done
    mov x,#$07
.e:
    mov a,!c_act+x
    beq .next
    mov a,!c_pfLeft+x
    beq +
    call PanGlide
+   mov a,!c_trmDepth+x
    beq +
    call Tremolo
+   mov a,!c_vibDepth+x
    or a,!c_bendLen+x
    beq .next
    call PitchEffects
.next:
    dec x
    bpl .e
.done:
    ret

; ---------------------------------------------------------------- ports
; Port 2 starts a song; the value is echoed on the output port once it has been taken.
Ports:
    mov a,$F4                           ; port 0: a $1DF9 effect on voice 6
    cmp a,!lastP0
    beq +
    mov !lastP0,a
    cmp a,#$00
    beq +
    cmp a,#$FF
    bne ++
    call Hurry
    bra +
++  mov x,#$00
    call SfxStart
+   mov a,$F7                           ; port 3: a $1DFC effect on voice 7
    cmp a,!lastP3
    beq +
    mov !lastP3,a
    cmp a,#$00
    beq +
    mov x,#$01
    call SfxStart
+   mov a,$F5                           ; port 1: $1DFA
    cmp a,!lastP1
    beq +
    mov !lastP1,a
    call Port1
+   mov a,$F6
    cmp a,!lastP2
    beq .out
    mov !lastP2,a
    cmp a,#$00
    beq .out
    cmp a,#$FF
    beq .fade
    call StartSong
    bra .out
.fade:
    call FadeOut
.out:
    mov a,!lastP2
    mov $F6,a
    ret

; Port 2 $FF (and a song the ROM does not have): the song's volume down to 0 over 240 song ticks, as AddmusicK's fades
; (measured: the time scales with the tempo, the step with the volume). The song plays on, unheard.
FadeOut:
    mov a,#$F0
    mov !mfLeft,a
    mov y,a
    mov a,#$00
    mov !mfTarget,a
    mov !t3,a
    mov !masterF,a
    mov a,!master
    mov !t2,a
    call FadeStep
    movw ya,!t0
    mov !mfStepL,a
    mov !mfStepH,y
    ret

; $1DF9 $FF, SMW's "time is running out": the song 10 faster from now on, and SMW's jingle on both effect voices, $1DF9 $1D
; and $1DFC $1D (AddmusicK's and SMW's own engine do both, measured).
Hurry:
    mov a,#10
    mov !hurry,a
    mov a,#$1D
    mov x,#$00
    call SfxStart
    mov a,#$1D
    mov x,#$01
    jmp SfxStart

; ---------------------------------------------------------------- songs
; A = the song asked for on port 2: a global one (always in ARAM) by its number, else the local one the main CPU uploaded.
StartSong:
    mov !t0,a
    mov a,!GlobalInfo
    mov !t2,a
    mov a,!GlobalInfo+1
    mov !t3,a
    beq .local
    mov y,#$00
    mov a,(!t2)+y                       ; how many
    beq .local
    mov !t1,a
    inc y
.find:
    mov a,(!t2)+y
    cmp a,!t0
    beq .global
    mov a,y
    clrc
    adc a,#$05
    mov y,a
    dbnz !t1,.find
.local:
    mov a,!SongInfo+1
    bne +
    ret                                 ; no local song uploaded: nothing to play
+   mov a,!SongInfo
    mov y,!SongInfo+1
    movw !phrase,ya
    mov a,!SongInfo+2
    mov y,!SongInfo+3
    movw !instr,ya
    bra .start
.global:
    inc y
    mov a,(!t2)+y
    mov !phrase,a
    inc y
    mov a,(!t2)+y
    mov !phrase+1,a
    inc y
    mov a,(!t2)+y
    mov !instr,a
    inc y
    mov a,(!t2)+y
    mov !instr+1,a
.start:
    mov a,!SongInfo+4                   ; the sample directory (a global song plays the local one's samples)
    %dsp($5D)
    mov a,#$35
    mov !tempo,a                        ; until the song sets one: 53, as measured
    mov a,#$00
    mov !tickAcc,a
    mov a,#$C0
    mov !master,a
    mov a,#$00
    mov !nspcVel,a
    mov !lstacc,a
    mov !inRemote,a
    mov !drums,a
    mov !syncOn,a
    mov !eon,a
    mov !pmon,a
    mov !non,a
    mov !evolL,a
    mov !evolR,a
    mov !efLeft,a
    call EchoReset
    mov a,#$00
    mov !gtrans,a
    mov !masterF,a
    mov !mfLeft,a
    mov !tempoF,a
    mov !tfLeft,a
    mov x,#$07
.reset:
    call ResetChannel
    dec x
    bpl .reset
    call NextPhrase
    mov a,#$01
    mov !playing,a
    ret

; Read the next phrase from the list: a pointer to a table of channel pointers, a jump ($00FF then an address), or the end.
NextPhrase:
.read:
    mov y,#$00
    mov a,(!phrase)+y
    mov !t0,a
    incw !phrase
    mov a,(!phrase)+y
    mov !t1,a
    incw !phrase
    mov a,!t1
    bne .table
    mov a,!t0
    beq .end
    cmp a,#$FF
    bne .end                            ; only the jump is understood
    call EchoReset                      ; AddmusicK's empties the echo buffer each time round
    mov y,#$00
    mov a,(!phrase)+y                   ; the address to carry on from
    mov !t2,a
    incw !phrase
    mov a,(!phrase)+y
    mov !t3,a
    movw ya,!t2
    movw !phrase,ya
    bra .read
.end:
    mov a,#$00
    mov !playing,a
    ret
.table:
    movw ya,!t0                         ; the channel table
    movw !ptr,ya
    mov x,#$00
    mov !active,#$00
.chan:
    mov y,#$00
    mov a,(!ptr)+y
    mov !t2,a
    incw !ptr
    mov a,(!ptr)+y
    mov !t3,a
    incw !ptr
    mov a,!t2
    or a,!t3
    beq .unused
    mov a,!t2
    mov !c_ptrl+x,a
    mov a,!t3
    mov !c_ptrh+x,a
    call InitChannel
    inc !active
    bra .next
.unused:
    mov a,#$00
    mov !c_act+x,a
.next:
    inc x
    cmp x,#$08
    bne .chan
    ret

InitChannel:                            ; X = channel: where it is in its data, at the start of a phrase
    mov a,#$01
    mov !c_act+x,a
    mov !c_dur+x,a                      ; fetch on the first song tick
    mov a,#$FF
    mov !c_perc+x,a
    mov !c_lcnt+x,a
    mov a,#$00
    mov !c_koff+x,a
    mov !c_koff2+x,a
    mov !c_rcnt+x,a
    ret

; What a channel sounds like at the start of a song. Phrases carry it on: the compiler writes @, v, y and q only when they change.
ResetChannel:                           ; X = channel
    mov a,#$FF
    mov !c_vol+x,a
    mov a,#$0A
    mov !c_pan+x,a
    mov a,#$0F
    mov !c_vel+x,a
    mov a,#$07
    mov !c_qd+x,a
    mov a,#$00
    mov !c_surr+x,a
    mov !c_len+x,a
    mov !c_trans+x,a
    mov !c_fine+x,a
    mov !c_amp+x,a
    mov !c_vdirty+x,a
    mov !c_vibDepth+x,a
    mov !c_vibFade+x,a
    mov !c_trmDepth+x,a
    mov !c_envType+x,a
    mov !c_legato+x,a
    mov !c_yoshi+x,a
    mov !c_remKonH+x,a
    mov !c_remType+x,a
    mov !c_remWait+x,a
    mov !c_volF+x,a
    mov !c_vfLeft+x,a
    mov !c_panF+x,a
    mov !c_pfLeft+x,a
    mov a,#$00                          ; a song that never says @n plays instrument 0
    jmp SetInstrument

; ---------------------------------------------------------------- a song tick
SongTick:
    call GlobalFades
    mov a,#$00
    mov !rerun,a
.again:
    mov a,#$00
    mov !konMask,a
    mov !koffMask,a
    mov x,#$00
.chan:
    mov a,!c_act+x
    beq .skip
    mov !cur,x
    call ChannelTick
    mov x,!cur
    mov a,!c_act+x
    beq .skip
    mov a,!c_vdirty+x                   ; a volume, pan or amplification change takes effect on its tick, mid-note too
    beq .skip
    mov a,#$00
    mov !c_vdirty+x,a
    call SendVolume
.skip:
    inc x
    cmp x,#$08
    bne .chan
    mov a,!active
    bne .flush
    mov !konMask,a                      ; a channel ended this tick: notes the others began are dropped with the phrase
.flush:
    call FlushDsp
    mov a,!syncOn                       ; $F4 $05: one more tick, told to the main CPU (after the channels, from the next tick)
    beq +
    cmp a,#$01
    beq ++
    incw !syncL
    mov a,!syncL
    mov $F4,a
    mov a,!syncH
    mov $F5,a
    bra +
++  inc !syncOn                         ; (2: counting)
+
    mov a,!active
    bne .done
    call NextPhrase                     ; a channel has ended: the next phrase starts on this very tick
    mov a,!playing
    beq .done
    mov a,!rerun
    bne .done                           ; (once, so an empty phrase cannot spin)
    inc !rerun
    bra .again
.done:
    ret

; Tell the DSP what this tick decided: voices to release, then voices to start.
FlushDsp:
    mov a,!sfxBusy                      ; voices a sound effect holds are the effect's
    eor a,#$FF
    and a,!koffMask
    mov $F2,#$5C
    mov $F3,a
    beq +                               ; (flags from the AND: neither MOV sets any)
    ; The DSP reads KOFF only every 64 cycles: a key-off must stand that long before the key-on below clears it, or the DSP can
    ; miss it and the note rings on. (AddmusicK's stand the rest of the tick; found playing a song against libgme.)
    mov y,#12                           ; 12 x 6 cycles
-   dbnz y,-
+   mov a,!sfxBusy
    eor a,#$FF
    and a,!konMask
    beq .done
    mov $F2,#$5C
    mov $F3,#$00
    mov $F2,#$4C
    mov $F3,a
.done:
    ret

; ---------------------------------------------------------------- a channel tick (X = channel)
ChannelTick:
    call ChannelFades
    mov a,!c_remWait+x                  ; remote code that is due
    beq +
    dec a
    mov !c_remWait+x,a
    bne +
    mov a,!c_remH+x
    mov y,a
    mov a,!c_remL+x
    call RunRemote
+
    mov a,!c_arpKoff+x                  ; an arpeggio step's release
    beq +
    dec a
    mov !c_arpKoff+x,a
    bne +
    call KeyOffVoice
+   mov a,!c_arpWait+x                  ; an arpeggio's next step
    beq +
    dec a
    mov !c_arpWait+x,a
    bne +
    call ArpStep
+
    mov a,!c_age+x
    inc a
    beq +
    mov !c_age+x,a
+   mov a,!c_koff+x                     ; the note is cut short by quantisation
    beq .dur
    dec a
    mov !c_koff+x,a
    bne .dur
    mov a,!c_remType+x                  ; $FC event 3: code instead of this release (the one two ticks before the end stays)
    cmp a,#$03
    bne +
    mov a,!c_remH+x
    mov y,a
    mov a,!c_remL+x
    call RunRemote
    bra .dur
+   call CutIfNoTie
.dur:
    mov a,!c_koff2+x
    beq .dur2
    dec a
    mov !c_koff2+x,a
    bne .dur2
    call CutIfNoTie
.dur2:
    mov a,!c_dur+x
    dec a
    mov !c_dur+x,a
    bne .ret
.fetch:
    mov a,!c_ptrl+x
    mov !ptr,a
    mov a,!c_ptrh+x
    mov !ptr+1,a
    call ReadData
    mov a,!ptr
    mov !c_ptrl+x,a
    mov a,!ptr+1
    mov !c_ptrh+x,a
.ret:
    ret

CutIfNoTie:                             ; a tie carries the note on: no key-off
    call TieFollows
    bne .ret
    call KeyOffVoice
.ret:
    ret

; Z clear when the channel's next data is a tie. Durations, quantisation and commands before it are stepped over; a call, a loop,
; an arpeggio or the end of the data is taken as no tie.
TieFollows:
    mov a,!c_ptrl+x
    mov !t0,a
    mov a,!c_ptrh+x
    mov !t1,a
    mov y,#$00
.next:
    mov a,(!t0)+y
    cmp a,#$C6
    beq .yes
    cmp a,#$00
    beq .no
    bpl .skip1                          ; a duration or quantisation byte
    cmp a,#$DA
    bcc .no                             ; a note, rest or percussion
    push x
    setc
    sbc a,#$DA
    mov x,a
    mov a,ParamCount+x
    pop x
    cmp a,#$FF
    beq .no
    inc a                               ; the command and its parameters
    clrc
    adc a,!t0
    mov !t0,a
    bcc .next
    inc !t1
    bra .next
.skip1:
    incw !t0
    bra .next
.yes:
    mov a,#$01
    ret
.no:
    mov a,#$00
    ret

KeyOffVoice:                            ; X = channel: add its voice to this tick's key-off mask
    call VoiceBit
    or a,!koffMask
    mov !koffMask,a
    ret

Muted:                                  ; X = channel -> Z clear when a sound effect holds its voice
    call VoiceBit
    and a,!sfxBusy
    ret

VoiceBit:                               ; X = channel -> A = 1 << X (X kept)
    mov a,x
    mov y,a
    mov a,#$01
    cmp y,#$00
    beq .ret
.s:
    asl a
    dec y
    bne .s
.ret:
    ret

; Read the channel's data at !ptr until something sets a duration (a note, tie or rest) or the channel ends.
ReadData:
.next:
    call GetByte
    bne +
    jmp .end
+   cmp a,#$00
    cmp a,#$80
    bcc .length
    cmp a,#$C6
    bcc .note
    beq .tie
    cmp a,#$C7
    beq .rest
    cmp a,#$DA
    bcc .percussion
    jmp Command
.length:
    mov !c_len+x,a
    mov y,#$00
    mov a,(!ptr)+y                      ; a quantisation byte may follow
    bmi .more
    beq .more
    mov !t0,a
    incw !ptr
    and a,#$0F
    mov !c_vel+x,a
    mov a,!t0
    xcn a
    and a,#$07
    mov !c_qd+x,a
.more:
    bra .next
.note:
    call NoteOn
    mov y,#$00
    mov a,(!ptr)+y
    cmp a,#$DD                          ; a pitch bend is read with the note it bends
    bne .armNote
    incw !ptr
    call ReadBend
.armNote:
    mov a,!c_len+x
    mov !c_dur+x,a
    call ArmKeyOff
    mov a,!c_remType+x                  ; $FC event 2: code so many ticks before this note's release
    cmp a,#$02
    bne +
    mov a,!c_koff+x
    setc
    sbc a,!c_remArg+x
    beq +
    bcc +
    mov !c_remWait+x,a
+   ret
.tie:
    bra .arm
.rest:
    mov a,#$00
    mov !c_arpWait+x,a                  ; no arpeggio steps through a rest
    mov !c_arpKoff+x,a
    mov a,!c_legato+x
    and a,#$01
    mov !c_legato+x,a                   ; a rest ends a run of legato notes
    call KeyOffVoice                    ; a rest is released and re-released like a note
    bra .arm
.percussion:
    call PercussionOn
.arm:
    mov a,!c_len+x
    mov !c_dur+x,a
    call ArmKeyOff
    ret
.end:
    mov a,!inRemote
    beq +
    ret                                 ; the end of remote code
+   jmp EndOfData

; A note is released ((length x fraction) >> 8) ticks in, and again two ticks before it ends; under legato, neither.
ArmKeyOff:
    mov a,!c_legato+x
    and a,#$01
    beq +
    mov a,#$00
    mov !c_koff+x,a
    mov !c_koff2+x,a
    ret
+
    mov a,!c_qd+x
    mov y,a
    mov a,QTable+y
    mov y,a
    mov a,!c_len+x
    mul ya                              ; YA = length x fraction
    mov a,y                             ; >> 8, and at least 1
    bne .set
    inc a
.set:
    mov !c_koff+x,a
    mov a,!c_len+x
    setc
    sbc a,#$02
    clrc
    adc a,!lstacc                       ; light staccato: one tick before the end instead (measured)
    cmp a,!c_len+x
    bcc .two
    mov a,#$00
.two:
    mov !c_koff2+x,a
    ret

GetByte:                                ; A = next byte of the data, !ptr advanced
    mov y,#$00
    mov a,(!ptr)+y
    incw !ptr
    cmp a,#$00                          ; the flags from the byte, not from INCW's pointer
    ret

; $00: the end of this stretch of data: return from a call, or the channel is finished.
EndOfData:
    mov a,!c_rcnt+x
    beq .finished
    dec a
    mov !c_rcnt+x,a
    beq .return
    mov a,!c_cal+x                      ; once more
    mov !ptr,a
    mov a,!c_cah+x
    mov !ptr+1,a
    jmp ReadData
.return:
    mov a,!c_retl+x
    mov !ptr,a
    mov a,!c_reth+x
    mov !ptr+1,a
    jmp ReadData
.finished:                              ; the first channel to finish ends the phrase for all of them
    mov a,#$00
    mov !c_act+x,a
    mov !active,a
    ret

; ---------------------------------------------------------------- notes
; A = note byte $80-$C5. Sets the voice's registers and starts it.
NoteOn:
    mov !c_note+x,a
    mov a,!c_remKonH+x                  ; $FC event -1: code at every key-on, before the note's registers go out
    beq +
    mov y,a
    mov a,!c_remKonL+x
    call RunRemote
+   mov a,!c_remType+x                  ; event 1: code so many ticks after the key-on
    cmp a,#$01
    bne +
    mov a,!c_remArg+x
    mov !c_remWait+x,a
+   mov a,!c_note+x
    mov a,#$00
    mov !c_age+x,a
    mov !c_offL+x,a                     ; keyed at the plain note: no offset sent yet
    mov !c_offH+x,a
    mov !c_bendLen+x,a
    mov a,!c_envType+x                  ; $EB/$EC: every note bends
    beq +
    call EnvelopeBend
+   mov a,#$00
    mov !c_arpOff+x,a
    mov !c_arpKoff+x,a                  ; (a step that fell on this tick is the new note's now)
    mov a,!c_arpType+x                  ; $FB: the note starts on the pattern's first step
    beq +
    cmp a,#$03
    bne ++
    mov a,!c_arpWait+x                  ; a glissando is over once its note is: off for this one
    beq ++
    mov a,#$00
    mov !c_arpType+x,a
    mov !c_arpWait+x,a
    bra +
++  mov a,!c_arpType+x
    call ArpStart
+
    call SendInstrument
    call NotePitch
    call SendPitch
    mov a,#$00
    mov !c_vdirty+x,a
    call SendVolume
    mov a,!c_trmDepth+x                 ; a tremolo already moving lowers the volume the note starts at
    beq +
    call Tremolo
+   mov a,!c_legato+x                   ; legato: once a note is sounding, the next ones only change its pitch
    bpl +
    ret
+   and a,#$01
    beq +
    mov a,#$81
    mov !c_legato+x,a
+   mov a,!c_yoshi+x                    ; Yoshi's drums stay silent until he is there ($1DFA $02); the first note after
    beq +                               ; that is still silent, as in AddmusicK
    mov a,!drums
    beq ++
    mov a,!c_yoshi+x
    cmp a,#$02
    beq +
    mov a,#$02
    mov !c_yoshi+x,a
++  ret
+   call VoiceBit
    or a,!konMask
    mov !konMask,a
    ret

; Percussion bytes $D0-$D8 (@21-@29) play their instrument at its own note, and the channel keeps the instrument after.
PercussionOn:
    setc
    sbc a,#$BB                          ; the instrument number
    mov !t5,a
    call SetInstrument
    mov a,!t5
    setc
    sbc a,#21
    bcs +
    mov a,#$A4                          ; ponytail: $CA-$CF (@15-@20) are never compiled; o4 c if a hex block writes one
    jmp NoteOn
+   mov y,a
    mov a,PercussionNotes+y
    jmp NoteOn

SendInstrument:                         ; X = channel
    mov a,!c_dirty+x
    beq .done
    call Muted
    bne .done                           ; (still dirty: sent once the effect is over)
    mov a,#$00
    mov !c_dirty+x,a
    call VoiceBase                      ; A = voice * 16
    mov !t5,a
    clrc
    adc a,#$04
    mov $F2,a
    mov a,!c_srcn+x
    bmi +                               ; a noise instrument leaves the sample as it was (measured)
    mov $F3,a
+   inc $F2
    mov a,!c_ad1+x
    mov $F3,a
    inc $F2
    mov a,!c_ad2+x
    mov $F3,a
    inc $F2
    mov a,!c_gain+x
    mov $F3,a
.done:
    ret

VoiceBase:                              ; X = channel -> A = X * 16
    mov a,x
    asl a
    asl a
    asl a
    asl a
    ret

; The channel's note as it sounds: the note byte, both transpositions and the fine tuning -> !t0/!t1.
NotePitch:                              ; X = channel
    call NoteValue
    bcc ComputePitch
    ret

; The note as it sounds, before effects: A = note byte, !frac = fraction; C set (and !t0/!t1 = 0) when it is above the top note.
NoteValue:
    mov a,!c_fine+x
    mov !frac,a
    mov a,!c_note+x
    clrc
    adc a,!c_trans+x
    clrc
    adc a,!gtrans
    clrc
    adc a,!c_arpOff+x
    and a,#$7F                          ; the sum wraps in seven bits, so $85 is +5 as much as $05 is (measured)
    cmp a,#$48
    bcc +
    mov a,#$00                          ; above the top note: silent, pitch 0 (measured)
    mov !t0,a
    mov !t1,a
    setc
    ret
+   or a,#$80
    clrc
    ret

; Note byte and !frac -> pitch. The top octave comes from a table of twelve (and the next C), a fraction of a semitone
; interpolating between neighbours there; each octave below is half of the one above; the instrument's tuning multiplies it:
; pitch = P1 x whole + (P1 x fraction >> 8).
ComputePitch:                           ; A = note byte, X = channel -> !t0/!t1 = pitch
    call TablePitch
    ; fall into TunePitch

TunePitch:                              ; !t0/!t1 times the channel's tuning
    mov a,!c_tdd+x
    mov !t6,a
    mov a,!c_tee+x
    mov !t7,a
    mov a,!t0                           ; P1 low x whole
    mov y,!t6
    mul ya
    mov !t4,a
    mov !t5,y
    mov a,!t1                           ; P1 high x whole, into the high byte
    mov y,!t6
    mul ya
    clrc
    adc a,!t5
    mov !t5,a
    mov a,!t0                           ; P1 low x fraction, its high byte
    mov y,!t7
    mul ya
    mov a,y
    clrc
    adc a,!t4
    mov !t4,a
    bcc +
    inc !t5
+   mov a,!t1                           ; P1 high x fraction, whole
    mov y,!t7
    mul ya
    movw !t2,ya
    clrc
    mov a,!t4
    adc a,!t2
    mov !t0,a
    mov a,!t5
    adc a,!t3
    mov !t1,a
    ret

; Note byte and !frac -> the pitch before tuning in !t0/!t1 (X kept).
TablePitch:
    setc
    sbc a,#$80
    mov !t2,x
    mov y,#$00
    mov x,#$0C
    div ya,x                            ; A = octave 0-5, Y = semitone
    mov x,!t2
    mov !t3,a
    mov a,PitchLo+1+y                   ; the step to the next semitone, times the fraction
    setc
    sbc a,PitchLo+y
    mov !t4,y
    mov y,!frac
    mul ya
    mov !t5,y                           ; (step x fraction) >> 8
    mov y,!t4
    mov a,PitchLo+y
    clrc
    adc a,!t5
    mov !t0,a
    mov a,PitchHi+y
    adc a,#$00
    mov !t1,a
    mov a,#$05
    setc
    sbc a,!t3                           ; shifts down: 5 - octave
    mov y,a
    beq .done
.shift:
    lsr !t1
    ror !t0
    dec y
    bne .shift
.done:
    ret

; Pitch effects, every timer tick: the note moved by the bend and the vibrato. t is song ticks since key-on plus the tempo
; accumulator's fraction, so both move smoothly between song ticks as AddmusicK's do.
PitchEffects:                           ; X = channel
    mov a,#$00
    mov !t6,a                           ; !t6/!t7: the offset in 1/256 semitone (signed)
    mov !t7,a
    mov a,!c_bendLen+x
    beq +
    call Bend
+   mov a,!c_vibDepth+x
    beq +
    call Vibrato
+   mov a,!t6                           ; the same offset as last time: nothing to send
    cmp a,!c_offL+x
    bne +
    mov a,!t7
    cmp a,!c_offH+x
    beq .ret
+   mov a,!t6
    mov !c_offL+x,a
    mov a,!t7
    mov !c_offH+x,a
    push x
    call NoteValue
    pop x
    bcs .ret
    mov !t4,a                           ; the note and !frac, plus the offset
    mov a,!frac
    clrc
    adc a,!t6
    mov !frac,a
    mov a,!t4
    adc a,!t7
    call ComputePitch
    jmp SendPitch
.ret:
    ret

; The bend's offset so far: step x (t - delay), one timer tick ahead (measured), held at the whole difference once done.
Bend:                                   ; X = channel -> adds to !t6/!t7
    mov a,!tempo                        ; t + one timer tick - delay: !t1 whole ticks, !t0 the fraction
    clrc
    adc a,!hurry
    setc
    adc a,!tickAcc
    mov !t0,a
    mov a,!c_age+x
    adc a,#$00
    setc
    sbc a,!c_bendDelay+x
    bcc .ret                            ; not yet
    mov !t1,a
    cmp a,!c_bendLen+x
    bcs .done
    ; |step| x elapsed (whole !t1, fraction !t0) >> 8
    mov a,!c_bendStepH+x
    mov !t3,a
    mov a,!c_bendStepL+x
    mov !t2,a
    mov a,!t3
    bpl +
    mov a,!t2                           ; |step|
    eor a,#$FF
    clrc
    adc a,#$01
    mov !t2,a
    mov a,!t3
    eor a,#$FF
    adc a,#$00
    mov !t3,a
+   mov a,!t2
    mov y,!t1
    mul ya                              ; stepL x whole
    movw !t4,ya
    mov a,!t3
    mov y,!t1
    mul ya                              ; stepH x whole, << 8
    clrc
    adc a,!t5
    mov !t5,a
    mov a,!t2
    mov y,!t0
    mul ya                              ; stepL x fraction >> 8
    mov a,y
    clrc
    adc a,!t4
    mov !t4,a
    adc !t5,#$00
    mov a,!t3
    mov y,!t0
    mul ya                              ; stepH x fraction
    clrc
    adc a,!t4
    mov !t4,a
    mov a,y
    adc a,!t5
    mov !t5,a
    mov a,!c_bendStepH+x
    bpl .add
    mov a,!t6                           ; a bend down
    setc
    sbc a,!t4
    mov !t6,a
    mov a,!t7
    sbc a,!t5
    mov !t7,a
    ret
.add:
    mov a,!t6
    clrc
    adc a,!t4
    mov !t6,a
    mov a,!t7
    adc a,!t5
    mov !t7,a
    ret
.done:
    mov a,!c_bendDiff+x                 ; there: the target note
    clrc
    adc a,!t7
    mov !t7,a
.ret:
    ret

; $DD delay length note, just after a note: bend from that note to this one. The step is the difference over the length.
ReadBend:                               ; X = channel, !ptr at the delay
    call GetByte
    mov !c_bendDelay+x,a
    call GetByte
    mov !c_bendLen+x,a
    call GetByte
    setc
    sbc a,!c_note+x
    ; fall into SetBendStep

SetBendStep:                            ; A = signed semitones to bend by, X = channel; the delay and length are set
    mov !c_bendDiff+x,a
    mov !t3,a
    bpl +
    eor a,#$FF
    inc a
+   push x                              ; |difference| x 256 / length: the high byte, then the remainder's
    mov !t2,a
    mov a,!c_bendLen+x
    beq .zero
    mov x,a
    mov y,#$00
    mov a,!t2
    div ya,x
    mov !t1,a
    mov a,#$00
    div ya,x
    mov !t0,a
    pop x
    mov a,!t3
    bpl .store
    mov a,!t0                           ; negative
    eor a,#$FF
    clrc
    adc a,#$01
    mov !t0,a
    mov a,!t1
    eor a,#$FF
    adc a,#$00
    mov !t1,a
.store:
    mov a,!t0
    mov !c_bendStepL+x,a
    mov a,!t1
    mov !c_bendStepH+x,a
    ret
.zero:
    pop x
    mov a,#$00
    mov !c_bendStepL+x,a
    mov !c_bendStepH+x,a
    ret

; Vibrato: once t passes the delay the phase is (t - delay) x rate, a triangle (up to +depth/256 of a semitone at a quarter, down
; to -depth/256 at three quarters) added to !t6/!t7.
Vibrato:                                ; X = channel
    mov a,!c_age+x
    setc
    sbc a,!c_vibDelay+x
    bcc .ret                            ; still waiting
    mov !t1,a
    mov a,!c_vibRate+x
    mov y,a
    mov a,!t1
    mul ya
    mov !t2,a                           ; whole ticks x rate (the low byte is the phase)
    mov a,!c_vibRate+x
    mov y,a
    mov a,!tickAcc
    mul ya
    mov a,y
    clrc
    adc a,!t2                           ; + the fraction's share: the phase
    cmp a,#$40
    bcc .tri
    cmp a,#$C0
    bcs .tri
    eor a,#$FF                          ; the falling half: $80 - phase
    clrc
    adc a,#$81
.tri:                                   ; A = signed position on the triangle, -64 ... 64
    mov !t3,a
    cmp a,#$00                          ; (MOV to memory sets no flags)
    bpl +
    eor a,#$FF
    inc a
+   cmp a,#$40
    bne +
    call VibratoDepth                   ; the very top: the whole depth
    mov !t0,a
    bra ++
+   asl a                               ; |position| x 4: depth x that >> 8 is depth x |position| >> 6
    asl a
    mov !t5,a
    call VibratoDepth
    mov y,!t5
    mul ya
    mov !t0,y
++  mov a,#$00
    mov !t1,a
    mov a,!t3
    bmi .down
    mov a,!t6
    clrc
    adc a,!t0
    mov !t6,a
    mov a,!t7
    adc a,#$00
    mov !t7,a
    ret
.down:
    mov a,!t6
    setc
    sbc a,!t0
    mov !t6,a
    mov a,!t7
    sbc a,#$00
    mov !t7,a
.ret:
    ret

; The vibrato's depth now: with $EA it grows from 0 at key-on to the full depth over that many ticks.
VibratoDepth:                           ; X = channel -> A. Keeps !t3, !t5, !t6, !t7.
    mov a,!c_vibFade+x
    beq .full
    mov a,!c_age+x
    cmp a,!c_vibFade+x
    bcs .full
    mov !t4,a
    mov a,!c_vibDepth+x                 ; (depth x age + depth x fraction >> 8) / fade
    mov y,!tickAcc
    mul ya
    mov !t2,y
    mov a,!c_vibDepth+x
    mov y,!t4
    mul ya
    clrc
    adc a,!t2
    bcc +
    inc y
+   mov !t0,a
    push x
    mov a,!c_vibFade+x
    mov x,a
    mov a,!t0
    div ya,x
    pop x
    ret
.full:
    mov a,!c_vibDepth+x
    ret

; Tremolo, every timer tick, one song tick ahead (measured): the phase is (t + 1 - delay) x rate, and the volume is
; v x (1 - depth/256 x u), u a triangle from 0 at phase 0 to 1 at phase $80 and back.
Tremolo:                                ; X = channel
    mov a,!tickAcc
    mov !t0,a
    mov a,!c_age+x
    inc a
    setc
    sbc a,!c_trmDelay+x
    bcc .ret
    mov !t1,a
    mov a,!c_trmRate+x
    mov y,a
    mov a,!t1
    mul ya
    mov !t2,a
    mov a,!c_trmRate+x
    mov y,a
    mov a,!t0
    mul ya
    mov a,y
    clrc
    adc a,!t2                           ; the phase
    bpl +
    eor a,#$FF                          ; the second half comes back up: 255 - phase
+   asl a                               ; u x 256 (0 ... 254)
    mov !t3,a
    mov a,!c_trmDepth+x
    mov y,a
    mov a,!t3
    mul ya                              ; depth x u >> 8: how much of v to take away, x 256
    mov a,y
    eor a,#$FF                          ; 255 - that: the share of v kept
    mov !t3,a
    mov a,!c_vol+x
    push a
    mov y,!t3
    mul ya
    mov a,y
    mov !c_vol+x,a
    call SendVolume
    pop a
    mov !c_vol+x,a
.ret:
    ret

CmdTremolo:                             ; $E5 delay rate depth
    call GetByte
    mov !c_trmDelay+x,a
    call GetByte
    mov !c_trmRate+x,a
    call GetByte
    mov !c_trmDepth+x,a
    bne +
    mov a,#$01                          ; off: the volume as it is
    mov !c_vdirty+x,a
+   jmp ReadData

CmdVibratoFade:                         ; $EA ticks
    call GetByte
    mov !c_vibFade+x,a
    jmp ReadData

CmdEnvelopeRelease:                     ; $EB delay length semitones: every note bends away
    mov a,#$01
    bra CmdEnvelope2
CmdEnvelopeAttack:                      ; $EC delay length semitones: every note starts away and bends in
    mov a,#$02
CmdEnvelope2:
    mov !c_envType+x,a
    call GetByte
    mov !c_envDelay+x,a
    call GetByte
    mov !c_envLen+x,a
    bne +
    mov !c_envType+x,a                  ; a length of 0: off
+   call GetByte
    mov !c_envSemis+x,a
    jmp ReadData

; NoteOn under $EB/$EC: release bends from the note by the semitones; attack starts the note that far the other way and bends back.
EnvelopeBend:                           ; X = channel
    mov a,!c_envDelay+x
    mov !c_bendDelay+x,a
    mov a,!c_envLen+x
    mov !c_bendLen+x,a
    mov a,!c_envSemis+x
    mov !t3,a
    mov a,!c_envType+x
    cmp a,#$02
    bne +
    mov a,!c_note+x                     ; attack: key on at note - semitones (measured: $F4 starts an octave up)
    setc
    sbc a,!t3
    mov !c_note+x,a
+   mov a,!t3
    jmp SetBendStep

CmdVibrato:                             ; $DE delay rate depth
    call GetByte
    mov !c_vibDelay+x,a
    call GetByte
    mov !c_vibRate+x,a
    call GetByte
    mov !c_vibDepth+x,a
    jmp ReadData

CmdVibratoOff:                          ; $DF: and the note back where it was
    mov a,#$00
    mov !c_vibDepth+x,a
    mov a,!c_bendLen+x
    bne +                               ; (a bend carries on sending its own)
    mov a,#$00
    mov !c_offL+x,a
    mov !c_offH+x,a
    call NotePitch
    call SendPitch
+   jmp ReadData

SendPitch:                              ; X = channel, !t0/!t1 = pitch
    call Muted
    bne +
    call VoiceBase
    clrc
    adc a,#$02
    mov $F2,a
    mov a,!t0
    mov $F3,a
    inc $F2
    mov a,!t1
    mov $F3,a
+   ret

; Voice volumes: P = ((((v x velocity) >> 8) x master) >> 8)^2 >> 9, then each side is P x pan >> 7.
SendVolume:                             ; X = channel
    call Muted
    beq +
    ret
+   mov a,!c_vel+x
    mov y,a
    mov a,!nspcVel
    beq +
    mov a,VelTable+y
    bra ++
+   mov a,SmwVelTable+y
++  mov !t0,a
    mov a,!c_vol+x
    mov y,a
    mov a,!t0
    mul ya
    mov a,y                             ; (v x velocity) >> 8
    mov y,!master
    mul ya
    mov a,y                             ; x master >> 8
    mov y,a
    mul ya                              ; squared
    movw !t2,ya
    mov a,!t3                           ; >> 9: the high byte, halved
    lsr a
    mov !t6,a                           ; P
    mov a,!c_amp+x                      ; $FA $03: P + (P x amp >> 8)
    mov y,a
    mov a,!t6
    mul ya
    mov a,y
    clrc
    adc a,!t6
    mov !t6,a
    mov a,!c_panF+x                     ; left = P x pan[y] >> 7, the table interpolated for a fading pan
    mov !frac,a
    mov a,!c_pan+x
    call PanAt
    mov y,!t6
    mul ya
    asl a
    mov a,y
    rol a
    mov !t0,a
    mov a,#$00                          ; right = P x pan[20 - y] >> 7
    setc
    sbc a,!c_panF+x
    mov !frac,a
    mov a,#$14
    sbc a,!c_pan+x
    call PanAt
    mov y,!t6
    mul ya
    asl a
    mov a,y
    rol a
    mov !t1,a
    mov a,!c_surr+x                     ; surround flips a side's sign
    bpl +
    mov a,#$00
    setc
    sbc a,!t0
    mov !t0,a
+   mov a,!c_surr+x
    and a,#$40
    beq +
    mov a,#$00
    setc
    sbc a,!t1
    mov !t1,a
+   call VoiceBase
    mov $F2,a
    mov a,!t0
    mov $F3,a
    inc $F2
    mov a,!t1
    mov $F3,a
    ret

PanAt:                                  ; A = pan 0-20, !frac its fraction -> A = the table there (truncated)
    mov y,a
    mov a,PanTable+1+y
    setc
    sbc a,PanTable+y
    mov !t7,y
    mov y,!frac
    mul ya
    mov a,y
    mov y,!t7
    clrc
    adc a,PanTable+y
    ret

; ---------------------------------------------------------------- commands $DA-$FF
; Each handler reads its own parameters from !ptr and returns by jumping back into ReadData.
Command:
    setc
    sbc a,#$DA
    asl a
    mov y,a
    mov a,CommandTable+1+y             ; the handler's address, pushed so that RET jumps to it (X stays the channel)
    push a
    mov a,CommandTable+y
    push a
    ret

Unknown:
    jmp ReadData

; $FB count length notes... (arpeggio; a $80 among the notes is where it loops back to), $FB $80 length interval (trill),
; $FB $81 length interval (glissando, for one note). A count of 0 turns it off.
CmdArpeggio:
    call GetByte
    mov !t0,a
    call GetByte
    mov !c_arpLen+x,a
    mov a,!t0
    beq .off
    bmi .special
    mov !c_arpCount+x,a
    mov a,!ptr                          ; the notes are read from where they are
    mov !c_arpPtrL+x,a
    mov a,!ptr+1
    mov !c_arpPtrH+x,a
    mov a,#$00
    mov !c_arpLoop+x,a
    mov y,#$00
-   mov a,(!ptr)+y                      ; find the loop point, and step past the notes
    cmp a,#$80
    bne +
    mov a,y
    inc a
    mov !c_arpLoop+x,a
+   inc y
    mov a,y
    cmp a,!t0
    bne -
    mov a,!ptr
    clrc
    adc a,!t0
    mov !ptr,a
    bcc +
    inc !ptr+1
+   mov a,#$01
    mov !c_arpType+x,a
    jmp ReadData
.special:
    and a,#$01
    clrc
    adc a,#$02                          ; $80 trill (2), $81 glissando (3)
    mov !c_arpType+x,a
    mov a,#$00
    mov !c_arpWait+x,a                  ; (not yet used on a note)
    call GetByte
    mov !c_arpSemis+x,a
    jmp ReadData
.off:
    mov !c_arpType+x,a
    mov !c_arpWait+x,a
    jmp ReadData

; The note's first step: arpeggio the first note of the list, trill and glissando the interval.
ArpStart:                               ; X = channel
    mov a,#$00
    mov !c_arpIdx+x,a
    mov a,!c_arpType+x
    cmp a,#$01
    bne +
    call ArpNote
    bra ++
+   mov a,!c_arpSemis+x
++  mov !c_arpOff+x,a
    mov a,!c_arpLen+x
    mov !c_arpWait+x,a
    ; fall into ArpRelease

ArpRelease:                             ; this step is released two ticks before the next
    mov a,!c_arpLen+x
    setc
    sbc a,#$02
    beq +
    bcs ++
+   mov a,#$00
++  mov !c_arpKoff+x,a
    ret

ArpNote:                                ; A = the list's note at c_arpIdx (a $80 is stepped over)
    mov a,!c_arpPtrL+x
    mov !t0,a
    mov a,!c_arpPtrH+x
    mov !t1,a
    mov y,!c_arpIdx+x
    mov a,(!t0)+y
    cmp a,#$80
    bne +
    inc y
    mov !c_arpIdx+x,y
    mov a,(!t0)+y
+   ret

; Each step re-keys the voice at the next pitch of the pattern.
ArpStep:                                ; X = channel
    mov a,!c_arpLen+x
    mov !c_arpWait+x,a
    mov a,!c_arpType+x
    cmp a,#$02
    beq .trill
    bcs .gliss
    mov a,!c_arpIdx+x                   ; arpeggio: the next note, back to the loop point at the end
    inc a
    cmp a,!c_arpCount+x
    bcc +
    mov a,!c_arpLoop+x
+   mov !c_arpIdx+x,a
    call ArpNote
    bra .set
.trill:
    mov a,!c_arpOff+x                   ; trill: the interval and back
    beq +
    mov a,#$00
    bra .set
+   mov a,!c_arpSemis+x
    bra .set
.gliss:
    mov a,!c_arpOff+x                   ; glissando: one interval further each step
    clrc
    adc a,!c_arpSemis+x
.set:
    mov !c_arpOff+x,a
    call ArpRelease
    call NotePitch
    call SendPitch
    call VoiceBit
    or a,!konMask
    mov !konMask,a
    ret

CmdInstrument:                          ; $DA n
    call GetByte
    cmp a,#$13
    bcc .set
    cmp a,#$15
    bcc .keep                           ; @19 and @20 leave the instrument as it is, as AddmusicK's do
.set:
    call SetInstrument
.keep:
    jmp ReadData

; A = instrument number: load its registers into the channel's state.
SetInstrument:
    mov !c_ins+x,a
    mov a,#$01
    mov !c_dirty+x,a
    mov a,!c_ins+x
    cmp a,#$1E
    bcs .custom
    mov y,#$06                          ; stock and percussion: six bytes each
    mul ya
    movw !t2,ya
    clrc
    mov a,!t2
    adc a,#(StockInstruments&$FF)
    mov !t2,a
    mov a,!t3
    adc a,#(StockInstruments>>8)
    mov !t3,a
    bra .load
.custom:
    setc
    sbc a,#$1E                          ; custom instruments start at 30
    mov y,#$06
    mul ya
    movw !t2,ya
    clrc
    mov a,!t2
    adc a,!instr
    mov !t2,a
    mov a,!t3
    adc a,!instr+1
    mov !t3,a
.load:
    mov y,#$00
    mov a,(!t2)+y
    mov !c_srcn+x,a
    bpl .tone
    call NoiseOn                        ; a noise instrument ($80 | clock)
    bra .rest
.tone:
    call VoiceBit                       ; any other: no noise on this channel
    eor a,#$FF
    and a,!non
    cmp a,!non
    beq .rest
    mov !non,a
    %dsp($3D)
.rest:
    mov y,#$00
    incw !t2
    mov a,(!t2)+y
    mov !c_ad1+x,a
    incw !t2
    mov a,(!t2)+y
    mov !c_ad2+x,a
    incw !t2
    mov a,(!t2)+y
    mov !c_gain+x,a
    incw !t2
    mov a,(!t2)+y
    mov !c_tdd+x,a
    incw !t2
    mov a,(!t2)+y
    mov !c_tee+x,a
    ret

CmdPan:                                 ; $DB p: low five bits the pan, bit 7 and 6 surround
    call GetByte
    mov !t0,a
    and a,#$1F
    mov !c_pan+x,a
    mov a,#$00
    mov !c_panF+x,a
    mov !c_pfLeft+x,a
    mov a,!t0
    and a,#$C0
    mov !c_surr+x,a
    jmp VolumeChanged

CmdGlobalVolume:                        ; $E0 w
    call GetByte
    mov !master,a
    ; fall into AllVolumesChanged

AllVolumesChanged:                      ; every channel's volume depends on the global one
    mov y,#$07
    mov a,#$01
.c:
    mov !c_vdirty+y,a
    dec y
    bpl .c
    jmp ReadData

VolumeChanged:
    mov a,#$01
    mov !c_vdirty+x,a
    jmp ReadData

CmdTempo:                               ; $E2 t
    call GetByte
    mov !tempo,a
    jmp ReadData

CmdVolume:                              ; $E7 v
    call GetByte
    mov !c_vol+x,a
    jmp VolumeChanged

; ---- fades: a value with a fraction moves by a signed step each song tick, the first on the tick the command is read

; !t2 = from, !t3 = to, Y = ticks -> !t0/!t1 = (to - from) x 256 / ticks, signed. X kept.
FadeStep:
    push x
    mov a,y
    mov x,a
    mov a,!t3
    setc
    sbc a,!t2
    bcs +
    mov a,!t2                           ; downwards: the size, and remember the sign
    setc
    sbc a,!t3
    clrc
+   push p
    mov y,#$00
    div ya,x
    mov !t1,a
    mov a,#$00
    div ya,x
    mov !t0,a
    pop p
    bcs +
    mov a,!t0
    eor a,#$FF
    clrc
    adc a,#$01
    mov !t0,a
    mov a,!t1
    eor a,#$FF
    adc a,#$00
    mov !t1,a
+   pop x
    ret

CmdVolumeFade:                          ; $E8 ticks volume
    call GetByte
    mov !c_vfLeft+x,a
    call GetByte
    mov !c_vfTarget+x,a
    mov !t3,a
    mov a,!c_vol+x
    mov !t2,a
    mov a,#$00
    mov !c_volF+x,a
    mov a,!c_vfLeft+x
    bne +
    jmp ReadData                        ; a length of 0 does nothing (measured)
+
    mov y,a
    call FadeStep
    mov a,!t0
    mov !c_vfStepL+x,a
    mov a,!t1
    mov !c_vfStepH+x,a
    call VolumeFadeTick
    jmp ReadData

CmdPanFade:                             ; $DC ticks pan
    call GetByte
    mov !c_pfLeft+x,a
    call GetByte
    and a,#$1F
    mov !c_pfTarget+x,a
    mov !t3,a
    mov a,!c_pan+x
    mov !t2,a
    mov a,#$00
    mov !c_panF+x,a
    mov a,!c_pfLeft+x
    bne +
    jmp ReadData                        ; a length of 0 does nothing (measured)
+
    mov y,a
    call FadeStep
    mov a,!t0
    mov !c_pfStepL+x,a
    mov a,!t1
    mov !c_pfStepH+x,a
    call PanFadeTick
    jmp ReadData

CmdGlobalVolumeFade:                    ; $E1 ticks volume
    call GetByte
    mov !mfLeft,a
    call GetByte
    mov !mfTarget,a
    mov !t3,a
    mov a,!master
    mov !t2,a
    mov a,#$00
    mov !masterF,a
    mov a,!mfLeft
    bne +
    jmp ReadData                        ; a length of 0 does nothing (measured)
+
    mov y,a
    call FadeStep
    movw ya,!t0
    mov !mfStepL,a
    mov !mfStepH,y
    call MasterFadeTick
    jmp ReadData

CmdTempoFade:                           ; $E3 ticks tempo
    call GetByte
    mov !tfLeft,a
    call GetByte
    mov !tfTarget,a
    mov !t3,a
    mov a,!tempo
    mov !t2,a
    mov a,#$00
    mov !tempoF,a
    mov a,!tfLeft
    bne +
    jmp ReadData                        ; a length of 0 does nothing (measured)
+
    mov y,a
    call FadeStep
    movw ya,!t0
    mov !tfStepL,a
    mov !tfStepH,y
    call TempoFadeTick
    jmp ReadData

; A fading pan between song ticks: the voice volume at pan + step x (fraction + one timer tick), as AddmusicK's moves (measured).
PanGlide:                               ; X = channel
    mov a,!c_pan+x
    push a
    mov a,!c_panF+x
    push a
    mov a,!tempo
    clrc
    adc a,!hurry
    setc
    adc a,!tickAcc
    mov !t5,a
    bcc +
    mov a,!c_panF+x                     ; a whole step more
    clrc
    adc a,!c_pfStepL+x
    mov !c_panF+x,a
    mov a,!c_pan+x
    adc a,!c_pfStepH+x
    mov !c_pan+x,a
+   mov a,!c_pfStepL+x                  ; + step x fraction >> 8 (signed)
    mov y,!t5
    mul ya
    mov !t4,y
    mov a,!c_pfStepH+x
    mov y,!t5
    mul ya
    movw !t2,ya                         ; stepH x f: !t2 adds to the fraction, !t3 to the pan
    mov a,!c_pfStepH+x
    bpl +
    mov a,!t3                           ; a negative step's high byte is -1 + ...: take f off the pan
    setc
    sbc a,!t5
    mov !t3,a
+   mov a,!c_panF+x
    clrc
    adc a,!t4
    mov !t4,a
    mov a,#$00
    adc a,!t3
    mov !t3,a
    mov a,!t4
    clrc
    adc a,!t2
    mov !c_panF+x,a
    mov a,!c_pan+x
    adc a,!t3
    cmp a,#$15                          ; past either end: hold there
    bcc +
    mov a,!c_pfTarget+x
+   mov !c_pan+x,a
    call SendVolume
    pop a
    mov !c_panF+x,a
    pop a
    mov !c_pan+x,a
    ret

ChannelFades:                           ; X = channel, each song tick
    mov a,!c_vfLeft+x
    beq +
    call VolumeFadeTick
+   mov a,!c_pfLeft+x
    beq +
    call PanFadeTick
+   ret

VolumeFadeTick:
    mov a,!c_volF+x
    clrc
    adc a,!c_vfStepL+x
    mov !c_volF+x,a
    mov a,!c_vol+x
    adc a,!c_vfStepH+x
    mov !c_vol+x,a
    mov a,!c_vfLeft+x
    dec a
    mov !c_vfLeft+x,a
    bne +
    mov a,!c_vfTarget+x                 ; the last step lands on the target exactly
    mov !c_vol+x,a
    mov a,#$00
    mov !c_volF+x,a
+   mov a,#$01
    mov !c_vdirty+x,a
    ret

PanFadeTick:
    mov a,!c_panF+x
    clrc
    adc a,!c_pfStepL+x
    mov !c_panF+x,a
    mov a,!c_pan+x
    adc a,!c_pfStepH+x
    mov !c_pan+x,a
    mov a,!c_pfLeft+x
    dec a
    mov !c_pfLeft+x,a
    bne +
    mov a,!c_pfTarget+x
    mov !c_pan+x,a
    mov a,#$00
    mov !c_panF+x,a
+   mov a,#$01
    mov !c_vdirty+x,a
    ret

GlobalFades:                            ; each song tick
    mov a,!mfLeft
    beq +
    call MasterFadeTick
+   mov a,!tfLeft
    beq +
    call TempoFadeTick
+   mov a,!efLeft
    beq +
    call EchoFadeTick
+   mov a,!pmon                         ; PMON, NON and EON every tick, as AddmusicK's
    %dsp($2D)
    mov a,!sfxBusy
    eor a,#$FF
    and a,!non
    or a,!sfxNon
    %dsp($3D)
    mov a,!sfxEcho                      ; effects' voices echo only when $1DFA $06 said so
    beq +
    mov a,!eon
    bra ++
+   mov a,!sfxBusy
    eor a,#$FF
    and a,!eon
++  %dsp($4D)
    ret

MasterFadeTick:
    mov a,!masterF
    clrc
    adc a,!mfStepL
    mov !masterF,a
    mov a,!master
    adc a,!mfStepH
    mov !master,a
    dbnz !mfLeft,+
    mov a,!mfTarget
    mov !master,a
    mov a,#$00
    mov !masterF,a
+   mov y,#$07                          ; every channel's volume follows
    mov a,#$01
-   mov !c_vdirty+y,a
    dec y
    bpl -
    ret

TempoFadeTick:
    mov a,!tempoF
    clrc
    adc a,!tfStepL
    mov !tempoF,a
    mov a,!tempo
    adc a,!tfStepH
    mov !tempo,a
    dbnz !tfLeft,+
    mov a,!tfTarget
    mov !tempo,a
    mov a,#$00
    mov !tempoF,a
+   ret

; ---- echo

CmdEchoOn:                              ; $EF channels left right (as compiled)
    call GetByte
    mov !eon,a
    %dsp($4D)
    call GetByte
    mov !evolL,a
    %dsp($2C)
    call GetByte
    mov !evolR,a
    %dsp($3C)
    jmp ReadData

CmdEchoOff:                             ; $F0
    mov a,#$00
    mov !eon,a
    mov !evolL,a
    mov !evolR,a
    mov !efLeft,a
    %dsp($4D)
    %dsp($2C)
    %dsp($3C)
    %dspi($6C,$20)                      ; echo writes off
    jmp ReadData

; $F1 delay feedback filter. A delay beyond the buffer $FA $04 reserved moves the buffer and waits for it to clear, the song
; standing still meanwhile (as AddmusicK's does: 13 timer ticks per unit of delay, twice).
CmdEchoSetup:
    call GetByte
    mov !edl,a
    cmp a,!reserved
    beq +
    bcc +
    call EchoBuffer
    bra ++
+   %dsp($7D)
++  %dspi($6C,$00)
    call GetByte
    %dsp($0D)                           ; feedback
    call GetByte                        ; the filter: eight coefficients from the table
    and a,#$03
    asl a
    asl a
    asl a
    mov y,a
    mov a,#$0F
-   mov $F2,a
    push a
    mov a,FirTables+y
    mov $F3,a
    pop a
    inc y
    clrc
    adc a,#$10
    bpl -
    mov a,!evolL
    %dsp($2C)
    mov a,!evolR
    %dsp($3C)
    jmp ReadData

; The echo buffer back to nothing, as AddmusicK's does at the start and at each loop: muted with writes off, $FF00 and
; no delay, then writes on and unmuted. The next delay set has to make its buffer again.
EchoReset:
    mov a,#$00
    mov !edl,a
    mov !reserved,a
    %dspi($6C,$60)
    %dspi($6D,$FF)
    %dspi($7D,$00)
    %dspi($6C,$40)
    %dspi($6C,$00)
    ret

; A = the delay: point the buffer to the top of ARAM for it and let it clear. It stays that big until EchoReset.
EchoBuffer:
    mov !reserved,a
    mov !t4,a
    %dspi($6C,$60)
    mov a,!t4
    asl a
    asl a
    asl a
    eor a,#$FF
    inc a                               ; $100 - delay x 8, and $FF for no delay
    bne +
    dec a
+   %dsp($6D)
    mov a,!t4
    %dsp($7D)
    call EchoWait
    %dspi($6C,$40)
    call EchoWait
    %dspi($6C,$00)
    ret

EchoWait:                               ; 13 timer ticks per unit of the delay in !t4
    mov a,!t4
    mov y,#13
    mul ya
    mov !t5,a
    cmp a,#$00                          ; (MUL sets Z from the high byte)
    beq .done
-   mov a,$FD
    beq -
    mov !t6,a
    mov a,!t5
    setc
    sbc a,!t6
    mov !t5,a
    beq .done
    bcs -
.done:
    ret

CmdEchoReserve:                         ; $FA $04 n: the buffer for the longest delay, made now unless it is already that big
    cmp a,!reserved
    beq +
    bcc +
    mov !edl,a
    call EchoBuffer
+   jmp ReadData

CmdEchoFade:                            ; $F2 ticks left right
    call GetByte
    mov !efLeft,a
    call GetByte
    mov !t3,a
    mov a,!evolL
    mov !t2,a
    mov y,!efLeft
    beq .none
    call FadeStep
    movw ya,!t0
    mov !efLStepL,a
    mov !efLStepH,y
    call GetByte
    mov !t3,a
    mov a,!evolR
    mov !t2,a
    mov y,!efLeft
    call FadeStep
    movw ya,!t0
    mov !efRStepL,a
    mov !efRStepH,y
    mov a,#$00
    mov !evolLF,a
    mov !evolRF,a
    call EchoFadeTick
    jmp ReadData
.none:
    call GetByte
    jmp ReadData

; One step of the echo volume fade. AddmusicK's takes one step fewer than the length and stops short of the target (measured).
EchoFadeTick:
    dec !efLeft
    beq .done
    mov a,!evolLF
    clrc
    adc a,!efLStepL
    mov !evolLF,a
    mov a,!evolL
    adc a,!efLStepH
    mov !evolL,a
    mov a,!evolRF
    clrc
    adc a,!efRStepL
    mov !evolRF,a
    mov a,!evolR
    adc a,!efRStepH
    mov !evolR,a
.done:
    mov a,!evolL
    %dsp($2C)
    mov a,!evolR
    %dsp($3C)
    ret

; $FC lo hi type ticks: remote code (written from the readme; the AddmusicK build the oracle uses cannot compile it). -1 ($FF)
; runs at every key-on, 1 so many ticks after it, 2 so many before the release, 3 at the release instead of it, 4 once now,
; 0 turns them all off. One of 1-3 at a time, beside the key-on one.
CmdRemote:
    call GetByte
    mov !t0,a
    call GetByte
    mov !t1,a
    call GetByte
    mov !t2,a
    call GetByte
    mov !t3,a
    mov a,!t2
    beq .off
    cmp a,#$FF
    beq .keyon
    cmp a,#$04
    beq .now
    mov !c_remType+x,a
    mov a,!t0
    mov !c_remL+x,a
    mov a,!t1
    mov !c_remH+x,a
    mov a,!t3
    mov !c_remArg+x,a
    mov a,#$00
    mov !c_remWait+x,a
    jmp ReadData
.keyon:
    mov a,!t0
    mov !c_remKonL+x,a
    mov a,!t1
    mov !c_remKonH+x,a
    jmp ReadData
.now:
    movw ya,!t0
    call RunRemote
    jmp ReadData
.off:
    mov !c_remKonH+x,a
    mov !c_remType+x,a
    mov !c_remWait+x,a
    jmp ReadData

; Run the commands at YA (up to their $00) for channel X, then carry on where the channel was.
RunRemote:
    movw !t0,ya
    mov a,!ptr
    push a
    mov a,!ptr+1
    push a
    mov a,!inRemote
    push a
    movw ya,!t0
    movw !ptr,ya
    mov a,#$01
    mov !inRemote,a
    call ReadData                       ; commands only: it returns at the $00
    pop a
    mov !inRemote,a
    pop a
    mov !ptr+1,a
    pop a
    mov !ptr,a
    ret

CmdNoise:                               ; $F8 n: noise at clock n on this channel, until an instrument without it
    call GetByte
    call NoiseOn
    jmp ReadData

NoiseOn:                                ; A = clock, X = channel
    and a,#$1F
    %dsp($6C)
    call VoiceBit
    or a,!non
    mov !non,a
    %dsp($3D)
    ret

CmdSampleLoad:                          ; $F3 sample tuning: this sample at this whole tuning, the envelope as it was
    call GetByte
    mov !c_srcn+x,a
    call GetByte
    mov !c_tdd+x,a
    mov a,#$00
    mov !c_tee+x,a
    inc a
    mov !c_dirty+x,a
    jmp ReadData

CmdDataSend:                            ; $F9 a b: b to port 0, a to port 1, for the main CPU
    call GetByte
    mov $F5,a
    call GetByte
    mov $F4,a
    jmp ReadData

CmdDspWrite:                            ; $F6 register value
    call GetByte
    mov !t0,a
    call GetByte
    mov y,!t0
    mov $F2,y
    mov $F3,a
    jmp ReadData

CmdFir:                                 ; $F5 c0 ... c7
    mov a,#$0F
-   push a
    call GetByte
    pop y
    mov $F2,y
    mov $F3,a
    mov a,y
    clrc
    adc a,#$10
    bpl -
    jmp ReadData

CmdFineTune:                            ; $EE f: a fraction of a semitone on every note
    call GetByte
    mov !c_fine+x,a
    jmp ReadData

CmdTranspose:                           ; $E4 n: every channel, signed semitones
    call GetByte
    mov !gtrans,a
    jmp ReadData

CmdEnvelope:                            ; $ED: ADSR (aa < $80: ADSR1 = aa | $80, ADSR2) or $80 gg: GAIN (ADSR1 cleared)
    call GetByte
    bmi .gain
    or a,#$80
    mov !c_ad1+x,a
    call GetByte
    mov !c_ad2+x,a
    bra .dirty
.gain:
    mov a,#$00
    mov !c_ad1+x,a
    call GetByte
    mov !c_gain+x,a
.dirty:
    mov a,#$01
    mov !c_dirty+x,a
    jmp ReadData

CmdFA:                                  ; $FA n v
    call GetByte
    mov !t0,a
    call GetByte
    mov y,!t0
    cmp y,#$01
    beq .gain
    cmp y,#$02
    beq .transpose
    cmp y,#$03
    beq .amplify
    cmp y,#$04
    bne +
    jmp CmdEchoReserve
+   cmp y,#$00
    bne +
    and a,#$FE                          ; $00: pitch modulation channels (not channel 0)
    mov !pmon,a
    %dsp($2D)
+   jmp ReadData
.gain:                                  ; GAIN instead of the ADSR
    mov !c_gain+x,a
    mov a,!c_ad1+x
    and a,#$7F
    mov !c_ad1+x,a
    mov a,#$01
    mov !c_dirty+x,a
    jmp ReadData
.transpose:                             ; signed semitones, this channel
    mov !c_trans+x,a
    jmp ReadData
.amplify:
    mov !c_amp+x,a
    jmp VolumeChanged

CmdF4:                                  ; $F4 n
    call GetByte
    cmp a,#$00
    bne +
    mov a,#$01                          ; $00: channel 5 is Yoshi's drums (silent till he comes)
    mov !c_yoshi+5,a
    push x
    mov x,#$05
    call KeyOffVoice
    pop x
    jmp ReadData
+   cmp a,#$06
    bne +
    mov a,#$01                          ; $06: this channel is
    mov !c_yoshi+x,a
    call KeyOffVoice
    jmp ReadData
+   cmp a,#$01
    bne +
    mov a,!c_legato+x                   ; $01: legato on or off for this channel (off: the next note keys on again)
    eor a,#$01
    and a,#$01
    mov !c_legato+x,a
    jmp ReadData
+   cmp a,#$02
    bne +
    mov a,!lstacc                       ; $02: light staccato on or off, every channel
    eor a,#$01
    mov !lstacc,a
    jmp ReadData
+   cmp a,#$05
    bne +
    mov a,!syncOn                       ; $05: count the song's ticks out on ports 0 and 1 from here, or stop (and say 0)
    beq +++
    mov a,#$FF
+++ inc a
    mov !syncOn,a
    mov a,#$00
    mov !syncL,a
    mov !syncH,a
    mov a,!syncOn
    bne ++
    mov $F4,a
    mov $F5,a
++  jmp ReadData
+   cmp a,#$08
    bne +
    mov a,#$01                          ; $08: the N-SPC velocity table from now on
    mov !nspcVel,a
    jmp AllVolumesChanged
+   cmp a,#$03
    bne +
    call VoiceBit                       ; $03: this channel's echo on or off
    eor a,!eon
    mov !eon,a
    %dsp($4D)
    jmp ReadData
+   cmp a,#$09
    bne .done
    mov a,!c_ins+x                      ; $09: the instrument again, undoing $ED and $FA $01
    call SetInstrument
.done:
    jmp ReadData

CmdSubloop:                             ; $E6 n: 0 starts a loop, n repeats it n more times
    call GetByte
    beq .start
    mov !t0,a
    mov a,!c_lcnt+x
    cmp a,#$FF
    bne .counting
    mov a,!t0                           ; first time at the end: n repeats to go
    bra .test
.counting:
    dec a
.test:
    mov !c_lcnt+x,a
    bne .again
    mov a,#$FF                          ; done: forget the count
    mov !c_lcnt+x,a
    jmp ReadData
.again:
    mov a,!c_lpl+x
    mov !ptr,a
    mov a,!c_lph+x
    mov !ptr+1,a
    jmp ReadData
.start:
    mov a,!ptr
    mov !c_lpl+x,a
    mov a,!ptr+1
    mov !c_lph+x,a
    mov a,#$FF
    mov !c_lcnt+x,a
    jmp ReadData

CmdCall:                                ; $E9 lo hi n: run the subroutine n times
    call GetByte
    mov !c_cal+x,a
    call GetByte
    mov !c_cah+x,a
    call GetByte
    mov !c_rcnt+x,a
    mov a,!ptr
    mov !c_retl+x,a
    mov a,!ptr+1
    mov !c_reth+x,a
    mov a,!c_cal+x
    mov !ptr,a
    mov a,!c_cah+x
    mov !ptr+1,a
    jmp ReadData

; Commands this version only reads past.
macro skip(n)
    mov a,#<n>
    jmp SkipBytes
endmacro

SkipBytes:                              ; A = how many parameter bytes to ignore
    mov !t0,a
.s:
    call GetByte
    dec !t0
    bne .s
    jmp ReadData

Skip1: %skip(1)
Skip2: %skip(2)
Skip3: %skip(3)
Skip8: %skip(8)
Skip4: %skip(4)
Skip0: jmp ReadData

CommandTable:                           ; $DA ... $FC
    dw CmdInstrument, CmdPan, CmdPanFade, Skip3, CmdVibrato, CmdVibratoOff ; DA DB DC DD DE DF
    dw CmdGlobalVolume, CmdGlobalVolumeFade, CmdTempo, CmdTempoFade, CmdTranspose, CmdTremolo ; E0 E1 E2 E3 E4 E5
    dw CmdSubloop, CmdVolume, CmdVolumeFade, CmdCall, CmdVibratoFade, CmdEnvelopeRelease ; E6 E7 E8 E9 EA EB
    dw CmdEnvelopeAttack, CmdEnvelope, CmdFineTune, CmdEchoOn, CmdEchoOff, CmdEchoSetup ; EC ED EE EF F0 F1
    dw CmdEchoFade, CmdSampleLoad, CmdF4, CmdFir, CmdDspWrite, Unknown ; F2 F3 F4 F5 F6 F7
    dw CmdNoise, CmdDataSend, CmdFA, CmdArpeggio, CmdRemote        ; F8 F9 FA FB FC

ParamCount:                             ; $DA ... $FC: how many bytes follow each command; $FF where TieFollows stops looking
    db 1,1,2,3,3,0, 1,2,1,2,1,3, $FF,1,2,$FF,1,3, 3,2,1,3,0,3, 3,2,1,8,2,$FF, 1,2,2,$FF,4

; ---------------------------------------------------------------- sound effects
; $1DF9 effects play on voice 6, $1DFC and $1DFA's on voice 7, as AddmusicK puts them (SMW's own engine used 4, 6 and 7). The
; bytecode is SMW's: a length (under $80) with one or two volumes after it, notes from $80, $DA instrument, $DD note delay
; length note (a slide), $EB delay length note (a slide from the note sounding), $FF the last note again, $FE from the start,
; $00 the end. An instrument is the voice's eight registers (volume, pitch, sample, ADSR, GAIN) and a tuning: nine bytes.
; Notes are released two ticks before they end; slides move a step a tick. While an effect plays, the music on that voice
; goes on unheard and its next note puts its own instrument back.

SfxVoice:   db $60,$70                  ; X = effect channel -> the voice's registers and its bit
SfxBit:     db $40,$80

SfxStart:                               ; A = effect number, X = effect channel
    mov !t0,a
    cmp a,#$00                          ; (MOV to memory sets no flags; the caller's MOV X did)
    beq .none
    cmp a,!SfxInfo+6+x                  ; past the end of the table: nothing
    bcs .none
    mov a,x
    asl a
    mov y,a
    mov a,!SfxInfo+2+y                  ; the table: $1DF9's or $1DFC's
    mov !t2,a
    mov a,!SfxInfo+3+y
    mov !t3,a
    mov a,!t0
    asl a
    mov y,a
    mov a,(!t2)+y
    push a
    inc y
    mov a,(!t2)+y
    mov y,a
    pop a
    jmp SfxBegin
.none:
    ret

SfxBegin:                               ; YA = the effect's data, X = effect channel
    mov !s_ptrL+x,a
    mov !s_startL+x,a
    mov a,y
    mov !s_ptrH+x,a
    mov !s_startH+x,a
    mov a,#$03                          ; it takes the voice on the next effect tick and starts on the third (measured)
    mov !s_wait+x,a
    mov a,#$01
    mov !s_claim+x,a
    mov a,#$00
    mov !s_koff+x,a
    mov !s_slLeft+x,a
.none:
    ret

SfxTick:                                ; X = effect channel, every effect tick
    mov a,!s_wait+x
    beq .ret
    mov a,!s_claim+x
    beq +
    mov a,#$00
    mov !s_claim+x,a
    mov a,SfxBit+x                      ; the voice is the effect's from now: released
    or a,!sfxBusy
    mov !sfxBusy,a
    mov a,SfxBit+x
    %dsp($5C)
+
    mov a,!s_slLeft+x                   ; a slide
    beq .koff
    mov a,!s_slDelay+x
    beq +
    dec a
    mov !s_slDelay+x,a
    bra .koff
+   call SfxSlideStep
.koff:
    mov a,!s_koff+x
    beq +
    dec a
    mov !s_koff+x,a
    bne +
    mov a,!s_slLeft+x                   ; still sliding: no release
    bne +
    mov a,SfxBit+x
    %dsp($5C)
+   mov a,!s_wait+x
    dec a
    mov !s_wait+x,a
    bne .ret
    call SfxRead
.ret:
    ret

SfxSlideStep:                           ; one step of the slide, and the pitch sent
    mov a,!s_frac+x
    clrc
    adc a,!s_stepL+x
    mov !s_frac+x,a
    mov a,!s_note+x
    adc a,!s_stepH+x
    mov !s_note+x,a
    mov a,!s_slLeft+x
    dec a
    mov !s_slLeft+x,a
    bne +
    mov a,!s_target+x                   ; the last step lands on the target
    mov !s_note+x,a
    mov a,#$00
    mov !s_frac+x,a
+   jmp SfxSendPitch

SfxGet:                                 ; A = the effect's next byte (X = effect channel), Z and N from it
    mov a,!s_ptrL+x
    mov !t0,a
    mov a,!s_ptrH+x
    mov !t1,a
    mov y,#$00
    mov a,(!t0)+y
    push a
    incw !t0
    mov a,!t0
    mov !s_ptrL+x,a
    mov a,!t1
    mov !s_ptrH+x,a
    pop a
    cmp a,#$00
    ret

SfxPeek:                                ; A = the next byte without taking it
    mov a,!s_ptrL+x
    mov !t0,a
    mov a,!s_ptrH+x
    mov !t1,a
    mov y,#$00
    mov a,(!t0)+y
    ret

SfxRead:
    call SfxGet
    bne +
    jmp SfxEnd
+   bmi .cmd
    mov !s_dur+x,a                      ; a length, and maybe one volume (both sides) or two: bytes under $80, 0 included
    call SfxPeek
    bmi SfxRead
    call SfxGet
    mov !s_volL+x,a
    mov !s_volR+x,a
    call SfxPeek
    bmi SfxRead
    call SfxGet
    mov !s_volR+x,a
    bra SfxRead
.cmd:
    cmp a,#$DA
    bne +
    jmp .instrument
+   cmp a,#$DD
    bne +
    jmp .slide
+   cmp a,#$EB
    bne +
    jmp .slideOn
+   cmp a,#$FF
    bne +
    jmp .again
+   cmp a,#$FE
    bne +
    jmp .restart
+   cmp a,#$C6
    bcc +
    jmp .rest
+   push a                              ; a note: remember where it was
    mov a,!s_ptrL+x
    setc
    sbc a,#$01
    mov !s_lastL+x,a
    mov a,!s_ptrH+x
    sbc a,#$00
    mov !s_lastH+x,a
    pop a
    jmp SfxNote
.instrument:                            ; $DA n, or $DA ($80 | noise clock) n: noise from the instrument's voice
    call SfxGet
    bpl .tone
    and a,#$1F
    %dsp($6C)
    mov a,SfxBit+x
    or a,!sfxNon
    mov !sfxNon,a
    call SfxGet
    call SfxInstrument
    jmp SfxRead
.tone:
    push a
    mov a,SfxBit+x
    eor a,#$FF
    and a,!sfxNon
    mov !sfxNon,a
    pop a
    call SfxInstrument
    jmp SfxRead
.slide:                                 ; $DD note x length target: the note, sliding from its first tick (the second byte,
    call SfxGet                         ; documented as a delay, does not delay it: measured)
    push a
    call SfxGet
    mov a,#$00
    mov !s_slDelay+x,a
    call SfxGet
    mov !s_slLeft+x,a
    call SfxGet
    mov !t4,a
    pop a
    push a
    call SfxSlideTo
    pop a
    jmp SfxNote
.slideOn:                               ; $EB x length target: the note sounding goes on (not keyed again), sliding to the target
    call SfxGet
    mov a,#$00
    mov !s_slDelay+x,a
    call SfxGet
    mov !s_slLeft+x,a
    call SfxGet
    mov !t4,a
    mov a,!s_note+x
    call SfxSlideTo
    call SfxVolume
    jmp SfxArmRelease                   ; (its slide moves from the next tick: measured on the jump)
.again:                                 ; $FF: back to the last note, read again on the next tick, and so on (Drumroll ends so)
    mov a,!s_lastL+x
    mov !s_ptrL+x,a
    mov a,!s_lastH+x
    mov !s_ptrH+x,a
    mov a,#$01
    mov !s_wait+x,a
    ret
.restart:                               ; $FE: from the start
    mov a,!s_startL+x
    mov !s_ptrL+x,a
    mov a,!s_startH+x
    mov !s_ptrH+x,a
    jmp SfxRead
.rest:                                  ; $C6: the note sounding keyed on again (as Jump and Egg hatch end); $C7 and up: a rest
    bne +
    mov a,!s_note+x
    jmp SfxNote
+   cmp a,#$FD
    beq .pause
    mov a,SfxBit+x
    %dsp($5C)
    mov a,!s_dur+x
    mov !s_wait+x,a
    ret
.pause:                                 ; $FD n (ours, from SfxCompiler): 1 pauses the song, 0 lets it go on
    call SfxGet
    call Pause
    jmp SfxRead

; A = the note to slide from, !t4 the target; the delay and length are set: the step is (target - from) x 256 / length.
SfxSlideTo:
    mov !t3,a
    mov a,!t4
    mov !s_target+x,a
    mov a,!t4
    setc
    sbc a,!t3
    mov !t3,a                           ; signed semitones
    mov a,!s_slLeft+x
    beq .none
    push x
    mov y,a                             ; FadeStep wants from/to in !t2/!t3 and the length in Y: use it on (0, difference)
    mov a,!t3
    mov !t5,a
    mov a,#$00
    mov !t2,a
    mov a,!t5
    bpl +
    eor a,#$FF                          ; (FadeStep is unsigned: do the size, then the sign)
    inc a
+   mov !t3,a
    call FadeStep
    pop x
    mov a,!t5
    bpl +
    mov a,!t0
    eor a,#$FF
    clrc
    adc a,#$01
    mov !t0,a
    mov a,!t1
    eor a,#$FF
    adc a,#$00
    mov !t1,a
+   mov a,!t0
    mov !s_stepL+x,a
    mov a,!t1
    mov !s_stepH+x,a
.none:
    ret

; A = note: its pitch and volume to the voice, keyed on; released two ticks before its length is up.
SfxNote:
    mov !s_note+x,a
    mov a,#$00
    mov !s_frac+x,a
    call SfxSendPitch
    call SfxVolume
    mov a,SfxBit+x
    %dsp($4C)
    call SfxArmRelease
    mov a,!s_slLeft+x                   ; a slide with no delay moves on the note's own tick
    beq +
    mov a,!s_slDelay+x
    bne +
    call SfxSlideStep
+   ret

; The note lasts its length and is released two ticks before the end, unless a slide is still moving then (SfxTick): one as
; long as its note runs on into what follows (measured: Koopaling defeated's slides never let go, Lose Yoshi's shorter ones do).
SfxArmRelease:
    mov a,!s_dur+x
    mov !s_wait+x,a
    setc
    sbc a,#$02
    bcs +
    mov a,#$00
+   mov !s_koff+x,a
    ret

SfxVolume:
    mov a,SfxVoice+x
    mov $F2,a
    mov a,!s_volL+x
    mov $F3,a
    inc $F2
    mov a,!s_volR+x
    mov $F3,a
    ret

; $1DF9 $11 and $12's pause and unpause: every voice off and the song held; then on again from where it stood.
Pause:                                  ; A = 1 pause, 0 go on
    mov !paused,a
    beq +
    %dspi($5C,$FF)
+   ret

SfxSendPitch:                           ; the note (and fraction) at the instrument's tuning to the voice
    mov a,!s_frac+x
    mov !frac,a
    mov a,!s_note+x
    call TablePitch
    mov a,!s_tune+x                     ; x the tuning (whole numbers only)
    mov y,a
    mov a,!t0
    mul ya
    mov !t4,a
    mov !t5,y
    mov a,!s_tune+x
    mov y,a
    mov a,!t1
    mul ya
    clrc
    adc a,!t5
    mov !t5,a
    mov a,SfxVoice+x
    or a,#$02
    mov $F2,a
    mov a,!t4
    mov $F3,a
    inc $F2
    mov a,!t5
    mov $F3,a
    ret

; A = effect instrument: its eight registers to the voice as they are, and its tuning.
SfxInstrument:
    mov y,#$09
    mul ya
    clrc
    adc a,!SfxInfo
    mov !t0,a
    mov a,y
    adc a,!SfxInfo+1
    mov !t1,a
    mov a,SfxVoice+x
    mov !t2,a
    mov y,#$00
-   mov a,!t2
    mov $F2,a
    mov a,(!t0)+y
    mov $F3,a
    inc !t2
    inc y
    cmp y,#$08
    bne -
    mov a,(!t0)+y
    mov !s_tune+x,a
    mov y,#$00                          ; the instrument's volume is the effect's until a length says otherwise
    mov a,(!t0)+y
    mov !s_volL+x,a
    inc y
    mov a,(!t0)+y
    mov !s_volR+x,a
    ret

; The effect is over: the voice goes back to the music, whose next note sends its instrument and volume again.
SfxEnd:
    mov a,#$00
    mov !s_wait+x,a
    mov a,SfxBit+x
    eor a,#$FF
    push a
    and a,!sfxBusy
    mov !sfxBusy,a
    pop a
    and a,!sfxNon
    mov !sfxNon,a
    mov a,x
    push x
    clrc
    adc a,#$06                          ; the music channel on that voice
    mov x,a
    mov a,#$01
    mov !c_dirty+x,a                    ; (the volume too, at that note: until then the effect's stays, as in AddmusicK)
    pop x
    ret

; $1DFA: $01 the jump on voice 7, $04 (girder) on voice 7, $05/$06 effects' echo off/on, $07/$08 $1DF9's $11/$12
; (pause/unpause sounds) on voice 6, $FF back to the IPL ROM for an upload.
Port1:
    cmp a,#$FF
    bne +
    jmp ToIpl
+   cmp a,#$01
    bne +
    mov x,#$01                          ; the jump
    mov a,#(JumpSfx&$FF)
    mov y,#(JumpSfx>>8)
    jmp SfxBegin
+   cmp a,#$04
    bne +
    mov x,#$01
    mov a,#(Sfx1DFA04&$FF)
    mov y,#(Sfx1DFA04>>8)
    jmp SfxBegin
+   cmp a,#$02
    bne +
    mov a,#$01                          ; Yoshi's drums on
    mov !drums,a
    ret
+   cmp a,#$03
    bne +
    mov a,#$00                          ; and off: the drums' channels released, back to waiting a note
    mov !drums,a
    mov !t0,a
    mov x,#$07
-   mov a,!c_yoshi+x
    beq ++
    mov a,#$01
    mov !c_yoshi+x,a
    call VoiceBit
    or a,!t0
    mov !t0,a
++  dec x
    bpl -
    mov a,!t0
    %dsp($5C)
    ret
+   cmp a,#$07
    bne +
    mov x,#$00                          ; $1DF9's pause sound (and the pause), as AddmusicK plays $07
    mov a,#$11
    jmp SfxStart
+   cmp a,#$08
    bne +
    mov x,#$00
    mov a,#$12
    jmp SfxStart
+   cmp a,#$05
    bne +
    mov a,#$00
    mov !sfxEcho,a
    ret
+   cmp a,#$06
    bne +
    mov a,#$01
    mov !sfxEcho,a
    ret
+   ret

; $1DFA's own sounds, in the effects' bytecode, written to sound as AddmusicK's (and SMW's) do: measured, not copied.
JumpSfx:    db $DA,$08,$05,$38,$38,$DD,$B2,$00,$05,$B5,$2B,$EB,$00,$12,$B9,$00
Sfx1DFA04:  db $DA,$07,$0C,$28,$DD,$A4,$00,$0C,$A4,$DA,$07,$0C,$28,$A4,$00

ToIpl:                                  ; everything off and over to the boot ROM, which waits for the main CPU to upload
    %dspi($6C,$E0)
    %dspi($5C,$FF)
    mov $F1,#$B0                        ; the ROM back at $FFC0, ports cleared
    jmp $FFC0

; ---------------------------------------------------------------- tables
FirTables:                              ; $F1's filters (measured; 3 repeats 0)
    db $FF,$08,$17,$24,$24,$17,$08,$FF
    db $7F,$00,$00,$00,$00,$00,$00,$00
    db $5F,$08,$DE,$08,$65,$09,$F4,$09
    db $FF,$08,$17,$24,$24,$17,$08,$FF

PitchLo: db $5F,$DE,$65,$F4,$8C,$2C,$D6,$8B,$4A,$14,$EB,$CD,$BE   ; and the C above, for interpolating from B
PitchHi: db $08,$08,$09,$09,$0A,$0B,$0B,$0C,$0D,$0E,$0E,$0F,$10

QTable:    db $33,$66,$80,$99,$B3,$CC,$E6,$FF
SmwVelTable: db $08,$12,$1B,$24,$2C,$35,$3E,$47,$51,$5A,$62,$6B,$7D,$8F,$A1,$B3   ; SMW's: the default before $F4 $08 (SMW disassembly; fits the measured volumes)
VelTable:  db $19,$32,$4C,$65,$72,$7F,$8C,$98,$A5,$B2,$BF,$CB,$D8,$E5,$F2,$FC
PanTable:  db $00,$01,$03,$07,$0D,$15,$1E,$29,$34,$42,$51,$5E,$67,$6E,$73,$77,$7A,$7C,$7D,$7E,$7F,$7F   ; (and once more, to interpolate from 20)

; SMW's stock instruments: sample, ADSR1, ADSR2, GAIN, tuning (whole, fraction).
StockInstruments:
    db $00,$FE,$6A,$B8,$06,$00   ; @0
    db $01,$FA,$6A,$B8,$03,$00   ; @1
    db $02,$AE,$2F,$B8,$04,$00   ; @2
    db $03,$FE,$6A,$B8,$03,$00   ; @3
    db $04,$A9,$6A,$B8,$03,$00   ; @4
    db $07,$AE,$26,$B8,$07,$00   ; @5
    db $08,$FA,$6A,$B8,$03,$00   ; @6
    db $09,$9E,$1F,$B8,$03,$00   ; @7
    db $05,$AE,$26,$B8,$1E,$00   ; @8
    db $0A,$EE,$6A,$B8,$02,$00   ; @9
    db $0B,$FE,$6A,$B8,$08,$00   ; @10
    db $01,$F7,$6A,$B8,$03,$00   ; @11
    db $10,$0E,$6A,$7F,$04,$00   ; @12
    db $0C,$FE,$6A,$B8,$03,$00   ; @13
    db $0D,$AE,$26,$B8,$07,$00   ; @14
    db $12,$8E,$E0,$B8,$03,$00   ; @15
    db $0C,$FE,$70,$B8,$03,$00   ; @16
    db $11,$FE,$6A,$B8,$05,$00   ; @17
    db $01,$E9,$6A,$B8,$03,$00   ; @18
    db $00,$00,$00,$00,$00,$00   ; @19 and @20 are never loaded (CmdInstrument keeps the instrument)
    db $00,$00,$00,$00,$00,$00
    db $0F,$0F,$6A,$7F,$03,$00   ; @21, the percussion: tuning measured from the notes played after a hit
    db $06,$0E,$6A,$40,$07,$00   ; @22
    db $06,$8C,$E0,$70,$07,$00   ; @23
    db $0E,$FE,$6A,$B8,$07,$00   ; @24
    db $0E,$FE,$6A,$B8,$08,$00   ; @25
    db $0B,$FE,$6A,$B8,$02,$00   ; @26
    db $0B,$7E,$6A,$7F,$08,$00   ; @27
    db $0B,$7E,$6A,$30,$08,$00   ; @28
    db $0E,$0E,$6A,$7F,$03,$00   ; @29

; Percussion @21-@29: sample, ADSR1, ADSR2, GAIN and the fixed pitch (word).
PercussionNotes:                        ; the note each of @21-@29 plays (measured)
    db $A8,$A4,$A1,$A4,$A4,$9C,$A6,$A6,$A1

EngineEnd:
