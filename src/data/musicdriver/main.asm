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
    bcc .done
    call SongTick
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
    mov a,#$00                          ; a song that never says @n plays instrument 0
    jmp SetInstrument

; ---------------------------------------------------------------- a song tick
SongTick:
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
    mov a,!c_koff+x                     ; the note is cut short by quantisation
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

; Z clear when the channel's next data is a tie (a duration and quantisation byte may come first).
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
    bmi .no
    incw !t0                            ; a duration or quantisation byte
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
    mov a,y                             ; >> 8
    cmp a,!c_len+x
    bcc .set
    mov a,#$00                          ; not before the note ends
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
    mov !t4,a
    call SendInstrument
    mov a,!t4
    call ComputePitch                   ; !t0/!t1 = the pitch
    call SendPitch
    call SendVolume
    call VoiceBit
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

; Note byte -> pitch. The top octave comes from a table of twelve; each octave below is half of the one above;
; the instrument's tuning multiplies it: pitch = P1 x whole + (P1 x fraction >> 8).
ComputePitch:                           ; A = note byte, X = channel -> !t0/!t1 = pitch
    setc
    sbc a,#$80
    mov !t2,x
    mov y,#$00
    mov x,#$0C
    div ya,x                            ; A = octave 0-5, Y = semitone
    mov x,!t2
    mov !t3,a
    mov a,PitchLo+y
    mov !t0,a
    mov a,PitchHi+y
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
    mov a,!c_pan+x                      ; left = P x pan[y] >> 7
    mov y,a
    mov a,PanTable+y
    mov y,!t6
    mul ya
    asl a
    mov a,y
    rol a
    mov !t0,a
    mov a,#$14                          ; right = P x pan[20 - y] >> 7
    setc
    sbc a,!c_pan+x
    mov y,a
    mov a,PanTable+y
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
    mov a,!t0
    and a,#$C0
    mov !c_surr+x,a
    jmp ReadData

CmdGlobalVolume:                        ; $E0 w
    call GetByte
    mov !master,a
    jmp ReadData

CmdTempo:                               ; $E2 t
    call GetByte
    mov !tempo,a
    jmp ReadData

CmdVolume:                              ; $E7 v
    call GetByte
    mov !c_vol+x,a
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
    dw CmdInstrument, CmdPan, Skip2, Skip3, Skip3, Skip0           ; DA DB DC DD DE DF
    dw CmdGlobalVolume, Skip2, CmdTempo, Skip2, Skip1, Skip3       ; E0 E1 E2 E3 E4 E5
    dw CmdSubloop, CmdVolume, Skip2, CmdCall, Skip1, Skip3         ; E6 E7 E8 E9 EA EB
    dw Skip3, Skip2, Skip1, Skip3, Skip0, Skip3                    ; EC ED EE EF F0 F1
    dw Skip3, Skip2, Skip1, Skip8, Skip2, Unknown                  ; F2 F3 F4 F5 F6 F7
    dw Skip1, Skip2, Skip2, SkipArpeggio, Skip4                    ; F8 F9 FA FB FC

; ---------------------------------------------------------------- tables
PitchLo: db $5F,$DE,$65,$F4,$8C,$2C,$D6,$8B,$4A,$14,$EB,$CD
PitchHi: db $08,$08,$09,$09,$0A,$0B,$0B,$0C,$0D,$0E,$0E,$0F

QTable:    db $33,$66,$80,$99,$B3,$CC,$E6,$FC
VelTable:  db $19,$32,$4C,$65,$72,$7F,$8C,$98,$A5,$B2,$BF,$CB,$D8,$E5,$F2,$FC
PanTable:  db $00,$01,$03,$07,$0D,$15,$1E,$29,$34,$42,$51,$5E,$67,$6E,$73,$77,$7A,$7C,$7D,$7E,$7F

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
