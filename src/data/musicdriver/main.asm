; Pipe Dream's music engine: an SPC700 program that plays songs compiled by music/MmlCompiler.cs.
;
; Written from AddmusicK's documented commands and from measuring what its engine tells the DSP
; (reference/AMK_ENGINE.md); no part of AddmusicK's engine is used. Assembled with Asar's spc700
; mode onto a flat buffer: file offset 0 is ARAM $0400, where the engine starts.
;
; Memory:  $0000-$00EF direct page   $0100-$01FF stack   $0200-$03FF per-channel state, a page of
;          eight-byte arrays   $0400 this program and its tables   SongBase the song.
;
; The main CPU talks to it through the four ports (each echoed back once read):
;   port 0  sound effect for channel 6   port 1  control   port 2  song to play ($FF stops)   port 3  effect for channel 7

arch spc700
norom
org $0000
base $0400

!SongBase = $2400                 ; where a local song is uploaded
!SongInfo = $03F0                 ; engine-visible facts about the song: instrument table address (word)

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
    %dspi($5D,$24)                      ; the sample directory
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
.ports:
    call Ports
    bra Main

; One 500 Hz tick: the tempo accumulator decides whether it is also a song tick.
Tick:
    mov a,!playing
    beq .done
    mov a,!tickAcc
    setc
    adc a,!tempo                        ; + tempo + 1
    mov !tickAcc,a
    bcc .effects
    call SongTick
.effects:                               ; pitch effects move every timer tick, between song ticks too, as AddmusicK's do
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
    mov a,$F6
    cmp a,!lastP2
    beq .out
    mov !lastP2,a
    cmp a,#$00
    beq .out
    cmp a,#$FF
    beq .stop
    call StartSong
    bra .out
.stop:
    call StopSong
.out:
    mov a,!lastP2
    mov $F6,a
    ret

StopSong:
    mov a,#$00
    mov !playing,a
    mov !active,a
    mov x,#$07
.c:
    mov !c_act+x,a
    dec x
    bpl .c
    %dspi($5C,$FF)
    ret

; ---------------------------------------------------------------- songs
StartSong:
    mov a,#(!SongBase&$FF)
    mov y,#(!SongBase>>8)
    movw !phrase,ya
    mov a,#$35
    mov !tempo,a                        ; until the song sets one: 53, as measured
    mov a,#$00
    mov !tickAcc,a
    mov a,#$C0
    mov !master,a
    mov a,#$00
    mov !gtrans,a
    mov !masterF,a
    mov !mfLeft,a
    mov !tempoF,a
    mov !tfLeft,a
    mov a,!SongInfo
    mov y,!SongInfo+1
    movw !instr,ya
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
    mov a,!koffMask
    mov $F2,#$5C
    mov $F3,a
    mov a,!konMask
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
    mov a,!c_age+x
    inc a
    beq +
    mov !c_age+x,a
+   mov a,!c_koff+x                     ; the note is cut short by quantisation
    beq .dur
    dec a
    mov !c_koff+x,a
    bne .dur
    call CutIfNoTie
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
    cmp a,#$00
    beq .end
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
    bne .arm
    incw !ptr
    call ReadBend
    bra .arm
.tie:
    bra .arm
.rest:
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
    jmp EndOfData

; A note is released ((length x fraction) >> 8) ticks in, and again two ticks before it ends.
ArmKeyOff:
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
    bcs .two
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
    mov a,#$00
    mov !c_age+x,a
    mov !c_bendLen+x,a
    mov a,!c_envType+x                  ; $EB/$EC: every note bends
    beq +
    call EnvelopeBend
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
    mov a,#$00
    mov !c_dirty+x,a
    call VoiceBase                      ; A = voice * 16
    mov !t5,a
    clrc
    adc a,#$04
    mov $F2,a
    mov a,!c_srcn+x
    mov $F3,a
    inc $F2
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
    beq .tuned
.shift:
    lsr !t1
    ror !t0
    dec y
    bne .shift
.tuned:
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
+   push x
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
    mov a,!tickAcc                      ; t + one timer tick - delay: !t1 whole ticks, !t0 the fraction
    setc
    adc a,!tempo
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
+   mov !t5,a
    call VibratoDepth
    mov y,!t5
    mul ya                              ; depth x |position|, >> 6
    movw !t0,ya
    mov y,#$06
-   lsr !t1
    ror !t0
    dbnz y,-
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

CmdVibratoOff:                          ; $DF
    mov a,#$00
    mov !c_vibDepth+x,a
    jmp ReadData

SendPitch:                              ; X = channel, !t0/!t1 = pitch
    call VoiceBase
    clrc
    adc a,#$02
    mov $F2,a
    mov a,!t0
    mov $F3,a
    inc $F2
    mov a,!t1
    mov $F3,a
    ret

; Voice volumes: P = ((((v x velocity) >> 8) x master) >> 8)^2 >> 9, then each side is P x pan >> 7.
SendVolume:                             ; X = channel
    mov a,!c_vel+x
    mov y,a
    mov a,VelTable+y
    mov !t0,a
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

SkipArpeggio:                           ; $FB count length, then count notes ($80 and up: trill or glissando, no notes)
    call GetByte
    mov !t0,a
    call GetByte
    mov a,!t0
    bmi .done
    beq .done
.note:
    call GetByte
    dbnz !t0,.note
.done:
    jmp ReadData

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
    mov a,!tickAcc
    setc
    adc a,!tempo
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
+   ret

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
    jmp ReadData                        ; $00 pitch modulation, $04 echo reserve: later
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
    cmp a,#$09
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
    dw CmdEnvelopeAttack, CmdEnvelope, CmdFineTune, Skip3, Skip0, Skip3 ; EC ED EE EF F0 F1
    dw Skip3, Skip2, CmdF4, Skip8, Skip2, Unknown                  ; F2 F3 F4 F5 F6 F7
    dw Skip1, Skip2, CmdFA, SkipArpeggio, Skip4                    ; F8 F9 FA FB FC

ParamCount:                             ; $DA ... $FC: how many bytes follow each command; $FF where TieFollows stops looking
    db 1,1,2,3,3,0, 1,2,1,2,1,3, $FF,1,2,$FF,1,3, 3,2,1,3,0,3, 3,2,1,8,2,$FF, 1,2,2,$FF,4

; ---------------------------------------------------------------- tables
PitchLo: db $5F,$DE,$65,$F4,$8C,$2C,$D6,$8B,$4A,$14,$EB,$CD,$BE   ; and the C above, for interpolating from B
PitchHi: db $08,$08,$09,$09,$0A,$0B,$0B,$0C,$0D,$0E,$0E,$0F,$10

QTable:    db $33,$66,$80,$99,$B3,$CC,$E6,$FF
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
