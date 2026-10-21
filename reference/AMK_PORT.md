# Music: bringing AddmusicK into Pipe Dream

Plan for doing AddmusicK's job (MML songs, samples, sound effects, inserted into the ROM) inside
Pipe Dream, with a library window like Custom blocks, Patches and Custom sprites and a song editor.
Started 2026-10-21. Modelled on `PIXI_PORT.md` (custom sprites), `GPS_PORT.md` (blocks) and
`PATCHES.md`. Background on the tool is in `ROM_TOOLING.md` § "AddmusicK — what to look at"; this file
is the plan and its decision log. Nothing here is built yet.

## Decisions (2026-10-21)

| question | answer | consequence |
|---|---|---|
| Whose compiler and engine? | **Our own, clean room, MIT**, as the sprite driver and routines were | The HertzDevil/AddmusicK repo declares **no licence** (GitHub reports `null`; the readme credits only Asar). So none of AMK's C++ or SPC700 ASM ships or is copied. It works with no AMK folder installed. Songs written for AMK must still compile and sound the same, so the target is AMK's *documented behaviour*, below. |
| What is the source of truth for behaviour? | **AMK's documentation** (`doc/readme_files/*.html`: syntax, hex commands, sound effects, basic and advanced use), **SMW's own N-SPC behaviour** (the disassembly notes in this repo), the SPC700/DSP hardware docs, and **AMK's output as a black-box oracle** | The oracle is `addmusick` run on a song and its compiled bytes, ARAM image and `.spc` compared with ours. Running AMK is allowed; **nobody writing the compiler or engine opens AMK's `.cpp` or its `asm/*.asm`** (see "Clean-room boundary"). |
| Where do songs live? | **In project.pdp**: songs (name → MML text), samples (name → BRR bytes), sample groups, sound effects (1DF9 and 1DFC lists), and the global/local song list | Per `pdp-is-the-whole-project`: nothing is written to disk to build, and sharing the .pdp shares the music. Samples and songs use AMK's own file names inside the project so an AMK folder imports and exports one to one. |
| What is the song format? | **AMK's MML, unchanged** (`.txt`/`.mml` with `#samples`, `#instruments`, `#option`, remote code, superloops, hex commands) | The existing library of AMK songs imports as is. Our editor adds no dialect. |
| How is it assembled? | **Our SPC700 engine is written in Asar's `spc700` mode** and assembled at build, like every other ASM feature, with the SNES-side hooks in a second Asar patch | One toolchain. Needs the bundled Quasar to assemble `spc700` (open question 3). |
| How do we test it without hearing it? | **A small SPC700 interpreter in C#**, as `Cpu65816` is for the 65816, to run our engine against the oracle's output; **Mesen** for the real game | The ARAM image and register writes can be compared exactly. Whether it sounds right is checked by ear in Mesen and by comparing against AMK's `.spc`. |
| Preview inside the app? | **Later phase, optional**: an SPC700 + DSP player | Until then: build and Run in Mesen, or export `.spc`. A DSP (BRR, ADSR/GAIN, echo, FIR) is a project of its own and is not on the path to inserting music. |
| SA-1? | **Same rule as everything else**: LoROM first, SA-1 with `SA1_PORT.md` | AMK detects `$23` at `$00FFD5` and switches its generated addressing; our SNES patch is written the same way from the start, but unproven until `SA1_PORT.md` phase 0. |
| Lunar Magic handoff | **Write `<ROM>.msc`** beside the ROM on every build, as AMK does | LM lists song names from it (one line per song per `0`/`1` variant). |

## What AMK does (the facts we work from)

From the documentation, not the source.

- **A song** is a text file of up to 8 channels (`#0`-`#7`; 6 and 7 are shared with sound effects and
  cut when one plays). Notes `cdefgab r ^` with `+`/`-`, lengths as note values, `l` default length,
  `o < >` octave (1-6), `h` tuning, `{ }` triplets, `@` instrument (0-18, 21-29, 30+ custom), `v`/`w`
  channel and global volume, `y` pan (0-20, optional surround flags), `q` quantisation, `t` tempo,
  `p` vibrato, `n` noise, `&` pitch slide, `?` no loop, `/` intro marker, `("x.brr",$tt)` sample load.
- **Loops:** `[ ]n`, labelled `(n)[ ]m` and `(n)k` calls, `*` recall; none nest, but `[[ ]]n`
  superloops nest around them and a remote-code event type 4 is an effective third level.
- **Remote code:** `(!n)[...]` defines hex commands with no notes; `(!n, type, arg)` attaches one to an
  event (note on, N ticks into a note, before a note ends, key off, run now, disable).
- **`=` replacements** (greedy, recursive, only for text after them), and **preprocessing**
  (`#define #undef #ifdef #ifndef #if #endif #error`) that runs before everything else.
- **Hex commands:** 106 rows in `hex_command_reference.html` (`$DA` instrument ... `$E0-$E4` volume,
  tempo, transpose, `$E5` tremolo, `$E6` subloops, and AMK's `$FA` family). They are validated unless
  `-h` is given; validation turning off also turns echo bounds checking and AM4/AMM conversion off.
- **Directives:** `#samples { #group "x.brr" ... }`, `#instruments { "x.brr" $aa $bb $cc $dd $ee }`
  (ADSR1 ≥ `$80` selects ADSR else GAIN; `dd.ee` tuning), `#path`, `#pad`, `#halvetempo`,
  `#option tempoimmunity | dividetempo n | smwvtable | noloop`, `#spc { #author ... #length }`,
  `#amk n` (parser version, appended when absent; `#am4` and `#amm` override it).
- **Samples:** BRR files, grouped in `Addmusic_sample groups.txt`. `#default` is every stock SMW sample,
  `#optimized` only those global songs and sound effects need. A trailing `!` marks a sample
  "important" (always resident). Unused samples are dropped per song unless `-u`; duplicates are shared
  unless `-d`.
- **Global and local songs** in `Addmusic_list.txt`: globals never transfer and cost ARAM permanently;
  locals upload on a song change, with their samples unless `$1DFB`'s caller keeps the old set.
- **Sound effects:** two folders, `1DF9` (SPC channel 6) and `1DFC` (channel 7), listed in
  `Addmusic_sound effects.txt` with `*` (pointer to another effect) and `?` (no auto `$00`). Commands
  are the note subset plus `@x`, `@x,y` noise, `v x,y`, raw `$xx`, and `#asm`/`#jsr` inline SPC700.
- **The SMW side:** writing `$1DFB` uploads a song (`$FF` or a missing song fades out); `$1DFA` `#$06`
  turns sound-effect echo on, `#$05` off (default off); echo is disabled on upload and the echo buffer
  must be reserved or the build stops with "Echo buffer exceeded total space in ARAM".
- **ROM side:** data goes in RATS-tagged free space beyond SMW's own, preferring banks `$40+` on a 4 MB
  ROM (`-b` turns that off); `-a` takes untagged space too; `-p` writes the patch and files without
  touching the ROM; `-norom` makes `.spc` files alone. A `Addmusic_options.txt` replaces the arguments.
- **Conversion:** AddmusicM songs need an `#amm` header and an `sXX_..` line rewritten as a `#samples`
  list; Addmusic 4.05 songs need `#am4`; `.bnk` sample banks are named in `#samples`.

## Clean-room boundary

The compiler and the engine are written from the section above and from the hex command reference,
not from AMK's source. Anyone writing them should not have `AddmusicK/*.cpp`, `Music/`, `MML/`,
`Binary/` or `test/asm/*.asm` open; gaps go to the oracle (compile and compare), Mesen, or the SMW
disassembly's own sound notes. Disclosure: the author of this plan read `AddmusicK.cpp`'s function
list, its command-line option parsing, `generateMSC`, the two short functions that assemble the SNES
patch and the SPC program (they only name files and scan them for a few labels), and the docs above,
and nothing deeper. The shipped docs and the CLI flags are public behaviour, so that is within the rule, but a later reader
should still start from this file and not from that source.

## Phases

Each phase ends shippable.

### 0. Fixtures, the oracle and the SPC700 interpreter
- An oracle harness: run a user-supplied `addmusick` (one is built at
  `~/projects/romhacking/AddmusicK/test/addmusick`, with `libasar`) on a song with `-norom`, collect its
  compiled data and `.spc`. Tests that need it read `PIPEDREAM_AMK` and **skip without it**, like the
  Asar and SMW-ROM tests; CI has none.
- `rom/Spc700.cs`: an SPC700 interpreter (64 KB ARAM, the four I/O ports, timers) with a DSP register
  file that records writes. Enough to run an engine and compare, not to make sound.
- A corpus: `test/music/*.mml` (16 songs), `test/1DF9`, `test/1DFC` (93 effects) and a few minimal
  songs written for each command.

### 1. Model, import, session API: no insertion
- `ProjectFile.Music`: songs (name → MML), samples (name → BRR), groups (name → ordered samples with
  the important flag), sound effects (1DF9/1DFC lists with the `*`/`?` flags), the song list
  (global/local, number → song), options (AMK's flags as fields).
- Import an AMK folder: `Addmusic_list.txt`, `Addmusic_sample groups.txt`, `Addmusic_sound effects.txt`,
  the songs, samples, banks and effects they name, and `Addmusic_options.txt`. Export the same.
- `services/EditorSession.Music…`: list, new, import, set text, delete, slot a song, add a sample,
  each returning a status string.
- Tests: import then export is byte-identical on AMK's own `test/` folder.

### 2. The MML front end
- Preprocessor, lexer and parser for everything in "What AMK does", to a syntax tree with line and
  column on every node. Errors read as AMK's do and point at the text.
- Tests: the whole corpus parses; each documented command has a case; each documented error
  (nested loop, bad hex, unknown sample) fails the way the docs say.

### 3. The back end: songs to N-SPC data
- Tree to bytes: note and rest durations, ties, triplets, quantisation, loops, superloops, remote
  code, replacements, hex commands, intro and `?`, tempo and `#halvetempo`/`dividetempo`.
- A per-channel size report (what `-v` prints).
- Tests: **compiled bytes equal the oracle's** for every corpus song, command by command. This is
  the phase that decides whether existing songs work.

### 4. Samples, instruments and ARAM layout
- BRR validation, `#instruments` tables, sample groups, per-song dropping of unused samples,
  duplicate sharing, global songs, the ARAM map (engine, songs, sample table aligned `$100`, samples,
  sound effects, echo buffer) and the echo-bounds check, with AMK's error.
- Tests: layout equals the oracle's `.spc` ARAM for the corpus, byte for byte where the layout is
  specified and by region where it is ours to choose.

### 5. Our engine and the SNES side
- The SPC700 engine in `data/musicdriver/` (Asar `spc700`): the N-SPC command set and AMK's hex
  commands per the reference, the song and sound-effect channels, volume, pan, pitch, vibrato,
  tremolo, echo, the `$1DFA`/`$1DFB` protocol, and `#asm`/`#jsr` slots.
- The SNES patch: hook SMW's upload routines and its music and sound-effect tables to the
  library's, RATS-tag the data, write `<ROM>.msc`. Prep's `RomPrep.StampedRanges` lists the hooks so
  overlap warnings keep working.
- Tests: our engine and AMK's, fed the same song, leave the DSP register writes the same in the
  interpreter; in Mesen a level plays its song, a song change uploads, the P-switch and star songs
  play from the global set, a sound effect plays and cuts a channel.

### 6. The window and the editor
- ASM ▸ Music… lists the project's songs, samples and effects to add, import, slot, edit and remove.
- A song editor: the MML text with colouring, the error list with jump to line, the per-channel
  sizes, an ARAM usage bar, and Open in your editor, as blocks and sprites have.
- Music names in the level header and the overworld pickers come from the list instead of numbers.
- Build runs music after patches and blocks, before sprites; its status and warnings show in the
  build panel.

### 7. Sound effects
- 1DF9 and 1DFC effects through the same compiler (the note subset, noise, stereo volume, raw
  hex, repeat) and inline `#asm`/`#jsr`.
- Tests: the 93 effects compile to the oracle's bytes.

### 8. Preview
- Export `.spc` from any song (the format is public and the oracle gives us files to compare).
- Optional: an in-app SPC700 + DSP player so a song plays from the editor with no emulator.

### 9. Compatibility
- AM4 and AMM conversion (`-c`), `.bnk` banks, `#amk` versions, the options that change how a song
  is read, as far as the docs describe them.

## Open questions

1. **Which AMK?** The docs here describe the HertzDevil fork of 1.0.5. Later releases (1.0.9, 1.0.10,
   1.1) added hex commands and options. Do we target 1.0.5 as the floor and add later features when a
   song needs them, or pick a newer version's documentation first?
2. **The oracle's version.** Output differs between releases. The oracle is the local 1.0.5 build; a
   song that relies on a later feature will not compile there and has no oracle.
3. **Does the bundled Quasar assemble `spc700`?** Asar does; confirm the pinned fork's `arch spc700`
   works through `Asar.Patch`, and that a standalone SPC700 program can be assembled to bytes (AMK
   assembles its engine to a `.bin`).
4. **Engine compatibility without AMK's engine.** Songs depend on AMK's engine details (timing of
   fades, slides, the velocity table, how `q` and ties interact). The hex reference and the oracle pin
   most of it; some will only show up as songs that sound different. How much divergence is
   acceptable, and is "same DSP writes in the interpreter" the bar?
5. **ARAM layout.** Songs that fit AMK might not fit ours, or the reverse, if our engine is bigger or
   smaller. The echo-bounds check will report that honestly, but it can change which songs build.
6. **ROM space.** AMK writes into RATS-tagged free space and prefers `$40+` on 4 MB ROMs. How
   does that sit with `RatsWriter` and the sprite driver's data?
7. **Existing music on the base.** A base already carrying AMK's data (any ROM made with AMK) must
   be refused or cleaned, as a PIXI-patched base is by the sprite driver (`STSD` check). What marks AMK's
   insertion, and does cleaning it need the tool's own undo?
8. **Preview scope.** Is an in-app player worth its cost, or is Mesen plus `.spc` export enough?
9. **Sample sources.** BRR is the only format. Do we want WAV import (resample and encode to BRR)
   later? AMK itself has none.
