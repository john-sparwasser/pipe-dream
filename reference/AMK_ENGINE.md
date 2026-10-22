# The music engine: what AddmusicK's does, as measured, and what ours must do

Working notes for AMK_PORT.md phase 5. Everything here was read off AddmusicK's *behaviour* (its songs run in
`rom/Spc700.cs`, the DSP writes read back by `rom/SpcTrace.cs`, `tools/amk/amk_events.py`, `amk_voice.py` and the
ad-hoc comparison in `tests/MusicEngineTests.cs`), or taken from its documentation (`doc/readme_files/*.html`), never from
its engine source. Our engine is `data/musicdriver/main.asm`, SPC700 code of our own assembled by Asar's `spc700` mode,
tested by playing the same song in both engines and comparing what each tells the DSP.

## How the two engines are compared

`MusicEngineTests.Differences`: for each voice, the key-ons in order (pitch, sample, ADSR, GAIN exact; volume within 1), the
gap from each key-on and key-off to the one before (within 1.5 ticks + 6%), volume and pitch at fixed points into each note
(against theirs over a quarter tick either side; pitch within 1/128 of itself), and each song-wide DSP register's sequence of
values. `corpus_report` (PIPEDREAM_ENGINE_REPORT=1) runs the whole oracle corpus; `adhoc` (tools/amk/adhoc.sh FILE) any
song; `load` (PIPEDREAM_MML) profiles our engine's time.

The interpreter's instruction and timer timing were checked against blargg's snes_spc in libgme (`tools/amk/cycle_check.py`):
every instruction group and the timer counter's overflow agree to the cycle.

### Where AddmusicK's engine is not the reference

- **It falls behind.** With many channels running effects it cannot finish a tick inside a timer tick and loses some: about
  1% with six vibrato channels, 5% on dv.mml, 23% on ek.mml (t192, eight channels). snes_spc agrees, so hardware would too.
  Ours keeps time (effects run once per main-loop pass, unchanged pitches are not resent), so the comparison times each
  event from the one before, not from the start.
- **`$DD` straight after a loop's end, or before any note, stops the song for good** (exp2.mml). Not copied.
- **`$E5` with depth `$FF` after a note hangs it.** Not copied.

## Timing

- Timer 0 runs at 500 Hz (target 16). Each timer tick adds `tempo + 1` to a one-byte accumulator; a carry is a song tick:
  **ticks per second = 500/256 × (T+1)**. A quarter note is 48 ticks. With no `t`, the tempo is **53**.
- Effects (vibrato, bends, pan fades, tremolo) move every *timer* tick, the time into the note being song ticks plus the
  accumulator's fraction. Bends, tremolo and pan fades run one timer tick (or tick) ahead, as measured.
- The first channel to reach the end of its data ends the phrase for every channel; notes others started that tick are
  dropped, and the next phrase starts on the same tick. A new phrase keeps each channel's instrument, volume, pan and `q`.

## Notes

- **Quantisation:** a note is released at tick `max(len × T[q] >> 8, 1)` and again at `len − 2`, T = `33 66 80 99 B3 CC E6 FF`.
  Rests are released at their start and get the same two. A release is skipped when a tie follows (durations, `q` bytes and
  commands stepped over; a call, loop, arpeggio or the end of data counts as no tie).
- **Pitch:** the top octave from a 12-entry table (`085F ... 0FCD`, then `10BE`), a fraction of a semitone interpolating
  between neighbours there (truncated), each octave below half the one above, then × the instrument's tuning
  (`P × whole + (P × fraction >> 8)`).
- **Transposition:** `$FA $02` (channel) and `$E4` (all) add semitones; the sum wraps in **seven** bits (`$85` is +5, `$7B`
  is −5), and a result past the top note plays at pitch 0. Percussion is transposed too.
- **`$EE f`:** f/256 of a semitone on every note.
- **Instruments:** @0-@18 SMW's; **@19 and @20 leave the instrument as it is**; @21-@29 are percussion: each plays a fixed
  note (`A8 A4 A1 A4 A4 9C A6 A6 A1`) on its own instrument (tuning `3 7 7 7 8 2 8 8 3`), and the channel keeps that
  instrument for later notes. No `@` at all plays @0.
- **`$ED`:** `aa < $80`: ADSR1 = aa | $80, ADSR2; `$80 gg`: ADSR1 = 0 and GAIN = gg. **`$FA $01 gg`:** ADSR1 &= $7F, GAIN = gg.
  **`$F4 $09`** or a new `@` undoes both.

## Volume

- `P = ((((v × vel) >> 8) × w) >> 8)² >> 9`; `$FA $03 a` makes it `P + (P × a >> 8)`; then left = `P × pan[y] >> 7`, right =
  `P × pan[20 − y] >> 7`, pan = `00 01 03 07 0D 15 1E 29 34 42 51 5E 67 6E 73 77 7A 7C 7D 7E 7F` (interpolated, truncated,
  while a pan fades). A surround bit flips that side's sign. Defaults: `w` 192, `v` 255, `y` 10, velocity table N-SPC's.
- A `v`, `y`, `w` or `$FA $03` change is sent on its tick, mid-note too.

## Effects

- **Vibrato `$DE d r a`:** after d ticks, phase = (t − d) × r; a triangle, +a/256 semitone at a quarter, −a/256 at three
  quarters. **`$EA n`**: the depth grows from 0 at key-on over n ticks. **`$DF`** returns the note to its pitch.
- **Bend `$DD d l n`** (read with the note just before it): from the note to n, starting d ticks in, over l ticks, linear
  in semitones; then held at n.
- **`$EB d l s` / `$EC d l s`:** every later note bends by s semitones (release), or starts at note − s and bends back
  (attack). A length of 0 turns them off.
- **Tremolo `$E5 d r a`:** volume × (1 − a/256 × u), u a triangle from 0 up to 1 at half a period; phase (t + 1 − d) × r.
  It applies at key-on too. Depth 0 turns it off.
- **Fades** `$E8` volume, `$DC` pan, `$E1` global volume, `$E3` tempo: a signed 8.8 step a tick, the first on the tick the
  command is read, landing on the target. **A length of 0 does nothing.** Volume steps per tick; a pan fade also glides
  between ticks.

## Echo

- At song start and **every time the phrase list loops**: FLG `$60`, ESA `$FF`, EDL 0, FLG `$40`, FLG `$00`; the buffer
  reserved is forgotten.
- **`$EF`** (as compiled: channels, left, right become EON, EVOL L, EVOL R in the byte order AddmusicK stores them).
- **`$F1 d f n`:** if d is beyond the buffer already made: FLG `$60`, ESA = `$100 − 8d` (`$FF` for 0), EDL = d, then the whole
  song stands still for 13 × d timer ticks, FLG `$40`, another 13 × d, FLG `$00`. Otherwise just EDL. Then EFB = f, the
  eight FIR coefficients from filter n (0: `FF 08 17 24 24 17 08 FF`, 1: `7F 00 ...`, 2: `5F 08 DE 08 65 09 F4 09`), and the
  echo volumes again.
- **`$FA $04 n`** makes the buffer for delay n up front (with the same wait) so later `$F1`s up to n do not pause.
- **`$F0`**: EON, EVOL 0, FLG `$20`. **`$F2 l L R`:** echo volume fade, one step fewer than l, not landing on the target.
  **`$F5`**: the eight FIR coefficients. **`$F4 $03`**: this channel's echo bit toggled.
- PMON, NON and EON are written every song tick.

## Still to do

`$F4 $01` legato, `$02` light staccato, `$08` velocity table, `$00`/`$06` Yoshi drums, `$05` sync, `$07` tempo hike; `$F6`
DSP write; `$F8` noise; `$FA $00` pitch modulation; `$FB` arpeggio, trill, glissando; `$FC` remote commands; `$F3` sample
load; `$F9` data send; sound effects on channels 6 and 7; the port protocol.
