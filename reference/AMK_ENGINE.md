# The music engine: what AddmusicK's does, as measured, and what ours must do

Working notes for AMK_PORT.md phase 5. Everything here was read off AddmusicK's *behaviour* (its songs run in
`rom/Spc700.cs`, the DSP writes read back by `rom/SpcTrace.cs`, `tools/amk/amk_events.py`), never its engine source.
Our engine is SPC700 code of our own, assembled by Asar's `spc700` mode, and is tested by playing the same song and
comparing what each tells the DSP.

## Timing

- Timer 0 runs at 500 Hz (target 16 of the 8 kHz clock). Each timer tick adds `tempo + 1` to a one-byte accumulator;
  a carry is a song tick. So **ticks per second = 500/256 × (T+1)** for `t T` (measured at 13 tempos from 10 to 200,
  within 0.2%). A quarter note is 48 ticks.
- The default note's key-off comes a little before the next key-on (quantisation, below).

## What the DSP is told for a note (the order matters on hardware)

Measured for one note on a fresh voice:

1. `SRCN`, `ADSR1`, `ADSR2`, `GAIN` of the instrument (only when it changed).
2. `PITCH` low, high.
3. `VOL` left, right.
4. `KOFF = 0`, then `KON = 1<<voice`.
5. Every tick afterwards `KOFF = 0`, `KON = 0` are written again.

At song start: `FLG = $20`, `KOFF = $FF`, then the echo is reconfigured with `FLG = $60`, `ESA`, `EDL`, `FLG = $40`, `FLG = $00`.
`PMON`, `NON` and `EON` are rewritten with their current values every tick.

## Channel volume

The left and right voice volumes are `L = (P × pan[y]) >> 7`, `R = (P × pan[20 − y]) >> 7` with the N-SPC pan table
`00 01 03 07 0D 15 1E 29 34 42 51 5E 67 6E 73 77 7A 7C 7D 7E 7F` (358 of 359 measured points agree). A set surround bit
flips that side's sign. `P` is about 69 for channel volume 255, velocity 15 and the default global volume of 192, and
grows with the square of each factor.

## Defaults

Global volume `w` defaults to 192. `l` defaults to 8. Octave 4. Quantisation/velocity byte `$7F`.

(The rest is filled in as it is measured: the pitch table, the stock instruments, the velocity and quantisation
tables, echo, vibrato and the other effects, and the port protocol.)
