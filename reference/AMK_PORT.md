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
**Status (2026-10-21): done.** `tests/AmkOracle.cs` runs `addmusick -norom -noblock <songs>` in a copy of the folder
`PIPEDREAM_AMK` names and reads back each `.spc` with `rom/SpcFile.cs`; `AmkFact` skips without it. `rom/Spc700.cs` is
the interpreter: all 256 opcodes with the documented cycle counts, the ports, three timers, and a DSP register file that
logs every write with its cycle. It runs AMK's own engine from its own snapshot (ports reloaded from `$F4-$F7` of the
dump) and keys voice 0 on in time. The corpus is 13 of AMK's 16 `test/music` songs (`ww`, `ww2`, `ww3` name a sample the
folder lacks); the 93 effects ride along in every compile. Our own minimal song per command comes with phases 2 and 3.
The tests are `AmkOracleTests`, `Spc700Tests` (programs assembled by Asar) and `Spc700AsarTests`.
- An oracle harness: run a user-supplied `addmusick` (one is built at
  `~/projects/romhacking/AddmusicK/test/addmusick`, with `libasar`) on a song with `-norom`, collect its
  compiled data and `.spc`. Tests that need it read `PIPEDREAM_AMK` and **skip without it**, like the
  Asar and SMW-ROM tests; CI has none.
- `rom/Spc700.cs`: an SPC700 interpreter (64 KB ARAM, the four I/O ports, timers) with a DSP register
  file that records writes. Enough to run an engine and compare, not to make sound.
- A corpus: `test/music/*.mml` (16 songs), `test/1DF9`, `test/1DFC` (93 effects) and a few minimal
  songs written for each command.

### 1. Model, import, session API: no insertion
**Status (2026-10-21): done.** `data/MusicState.cs` is the model (`ProjectFile.Music`, so an older .pdp loads empty),
`data/AmkImport.cs` reads and writes the three manifests, the options file and an AMK folder (every `.txt`/`.mml` under
`music/`, every file under `samples/`, `1DF9/`, `1DFC/`, not only the ones the manifests name; a name with no file is a
problem in the status), and `services/EditorSession.Music.cs` is the API: list, new, import a song (with the samples
it names from beside it), set text, number a song global or local, delete, import a sample, delete a sample, import
and export an AMK folder. Tests are `MusicTests`; the oracle one compiles five corpus songs from our export and from
AMK's own folder and compares the ARAM and DSP state byte for byte. Not done here: the stock `#default` and
`#optimized` samples are AMK's files (SMW's own samples); a project should take them from the user's ROM instead of
shipping them (phase 4).
- `ProjectFile.Music`: songs (name → MML), samples (name → BRR), groups (name → ordered samples with
  the important flag), sound effects (1DF9/1DFC lists with the `*`/`?` flags), the song list
  (global/local, number → song), options (AMK's flags as fields).
- Import an AMK folder: `Addmusic_list.txt`, `Addmusic_sample groups.txt`, `Addmusic_sound effects.txt`,
  the songs, samples, banks and effects they name, and `Addmusic_options.txt`. Export the same.
- `services/EditorSession.Music…`: list, new, import, set text, delete, slot a song, add a sample,
  each returning a status string.
- Tests: songs, samples and effects come back byte for byte; the manifests are rewritten in AMK's own layout
  (CRLF, tab before the effect file name, `!`, `*`, `?`), so they match where AMK's are canonical and read the
  same where they are not. The real check is AMK's: given the folder we export instead of its own, `addmusick`
  makes the same ARAM and DSP state.

### 2. The MML front end
**Status (2026-10-21): done**, with phase 3 (they were developed together against the oracle). `music/MmlParser.cs` reads
the preprocessor, every directive, replacements and the whole channel language into the tree in `music/MmlSong.cs`; errors
carry line and column. Remote code (`(!n)[...]`, `(!n, type, arg)`) is parsed and compiled from the documentation only:
AddmusicK 1.0.5 cannot compile its own documented syntax, so there is nothing to check it against.
- Preprocessor, lexer and parser for everything in "What AMK does", to a syntax tree with line and
  column on every node. Errors read as AMK's do and point at the text.
- Tests: the whole corpus parses; each documented command has a case; each documented error
  (nested loop, bad hex, unknown sample) fails the way the docs say.

### 3. The back end: songs to N-SPC data
**Status (2026-10-21): done.** `music/MmlCompiler.cs`. All 13 corpus songs compile to AddmusicK's exact bytes (up to 33 KB),
65+36 golden vectors run without the oracle, and `tools/amk/fuzz.py` (random songs from the whole command set, compared
with the oracle) agrees on about 99.5% of songs. What the oracle taught, none of it in its documentation: the layout (phase
list, pointer tables, instrument table, channels, then every subroutine after every channel), `[ ]` loops compiled as
subroutines called with `$E9`, the duration/quantisation bytes written only when they changed and forgotten after loops,
intro markers and channel re-selection, how a long note splits (halves up to 254 ticks, else 127s), a slide (`&`)
splitting differently and emitting `$DD` before the next note, the loop-versus-text rules for `h` and `@`, empty
channels, the option bytes living in channel 0, `$EF`'s rotated parameters, tempo division of every duration field,
and the hex validations. Known gaps: percussion after a pitch-envelope hex command in a superloop (one fuzz case), and
anything only a later AMK version does.
- Tree to bytes: note and rest durations, ties, triplets, quantisation, loops, superloops, remote
  code, replacements, hex commands, intro and `?`, tempo and `#halvetempo`/`dividetempo`.
- A per-channel size report (what `-v` prints).
- Tests: **compiled bytes equal the oracle's** for every corpus song, command by command. This is
  the phase that decides whether existing songs work.

### 4. Samples, instruments and ARAM layout
**Status (2026-10-22): done.** `music/MusicLayout.cs` lays one song out: engine at `$0400`, song
info at `$03F0` (song, instrument table, directory page), the sample directory on the next page, the song, its samples (groups'
then its own, the compiler's numbering), and the echo buffer at the top for the song's longest delay, refusing a song that runs
into it with AMK's error. `music/SmwSound.cs` reads SMW's 20 samples from the ROM's own upload at `$0F8000` (byte for byte AMK's
`samples/default/*.brr`), so no AMK folder is needed for them. `MusicLayoutTests` checks every custom instrument's sample in dv,
ek and ct against AMK's ARAM. A group's sample that is not important (no `!`) and that nothing in the song names (an instrument set
or defined, `$F3`) is not uploaded, as AMK leaves it out (measured); important ones stay in ARAM from power-on.
- BRR validation, `#instruments` tables, sample groups, per-song dropping of unused samples,
  duplicate sharing, global songs, the ARAM map (engine, songs, sample table aligned `$100`, samples,
  sound effects, echo buffer) and the echo-bounds check, with AMK's error.
- Tests: layout equals the oracle's `.spc` ARAM for the corpus, byte for byte where the layout is
  specified and by region where it is ours to choose.

### 5. Our engine and the SNES side
**Status (2026-10-22): done; Mesen check outstanding.** `data/musicdriver/main.asm` plays every music command and matches
AddmusicK's engine, register by register, on every corpus song (`MusicEngineTests`, `reference/AMK_ENGINE.md` for what was measured
and where AddmusicK is not followed: its lag under load, two hangs, and the remote-code syntax its 1.0.5 build cannot compile).
`data/musicdriver/snes.asm` is the main CPU's side, written from SMW's disassembly and the boot ROM's protocol: power-on uploads the
engine, effects, global songs and the resident samples; the NMI uploads a local song when `$1DFB` asks for one not in ARAM, then
hands songs and effects to the ports. `data/MusicInsert.cs` builds the block lists and runs from `RomBuilder` after the patches.
`$1DFB` `$FF` or a song the ROM lacks fades out over 240 ticks; `$1DF9` `$FF` (hurry up) adds 10 to the tempo and plays SMW's
jingle, until a level entered from the map uploads its song again (all measured against AddmusicK and SMW's own engine). SMW's song
numbers are rewritten as AddmusicK's at every `$1DFB` site and table (`MusicInsert.SongSites`, matched by playing SMW's songs
against AddmusicK's originals); a site another patch changed is left with a warning, and a ROM whose sound code is not SMW's is
refused. `MusicSnesTests` runs SMW's 65816 code and ours against the SPC interpreter with the boot ROM modelled. Not done: a
`.msc` for Lunar Magic (Pipe Dream names the songs itself), and the check in Mesen.
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
**Status (2026-10-22): done.** ASM ▸ Music… lists songs, sound effects and samples. Effects are imported into a slot of `$1DF9`
or `$1DFC`, edited in the song editor (checked by `SfxCompiler`) and emptied; a slot the project fills replaces SMW's effect there
and the others stay SMW's. The song editor shows each channel's bytes and an ARAM bar (the room a local song has, as the build
lays it out). The overworld has no music picker to name.
- ASM ▸ Music… lists the project's songs, samples and effects to add, import, slot, edit and remove.
- A song editor: the MML text with colouring, the error list with jump to line, the per-channel
  sizes, an ARAM usage bar, and Open in your editor, as blocks and sprites have.
- Music names in the level header and the overworld pickers come from the list instead of numbers. (Done for the level header;
  the overworld has no music picker yet.)
- Build runs music after patches and blocks, before sprites; its status and warnings show in the
  build panel.

### 7. Sound effects
**Status (2026-10-22): done.** All 93 of AddmusicK's effects play as in its engine (`SfxTests`); `music/SfxCompiler.cs` compiles the
note syntax as AddmusicK 1.0.5 does (measured, not as its readme says); `#jsr` to pause and unpause become the engine's own.
- 1DF9 and 1DFC effects through the same compiler (the note subset, noise, stereo volume, raw
  hex, repeat) and inline `#asm`/`#jsr`.
- Tests: the 93 effects compile to the oracle's bytes.

### 8. Preview
**Status (2026-10-22): export done.** The song editor's Export .spc… writes the song as it stands, laid out with our engine and
the project's samples and effects, with ID666 tags from `#SPC` and `#length`; libgme plays the files. The in-app player is not done.
- Export `.spc` from any song (the format is public and the oracle gives us files to compare).
- Optional: an in-app SPC700 + DSP player so a song plays from the editor with no emulator.

### 9. Compatibility
**Status (2026-10-22): done but for `.bnk`.** `#am4` and `#amm` songs are converted as AMK converts them (measured): the header's
echo-buffer writes dropped, `$ED $80`/`$81` (Addmusic 4's own), stray bytes under `$80`, notes before the first channel. Five real
Addmusic 4 songs compile to AMK's bytes, and so do AMK 1.0.6's 39 originals that 1.0.5 can compile. `.bnk` banks (exactly `$8000`
bytes, ARAM `$8000-$FFFF`) are not read: the oracle build crashes loading one, so there is nothing to measure against.
- AM4 and AMM conversion (`-c`), `.bnk` banks, `#amk` versions, the options that change how a song
  is read, as far as the docs describe them.

## Open questions

1. **Which AMK?** The docs here describe the HertzDevil fork of 1.0.5. Later releases (1.0.9, 1.0.10,
   1.1) added hex commands and options. Do we target 1.0.5 as the floor and add later features when a
   song needs them, or pick a newer version's documentation first?
2. **The oracle's version.** Output differs between releases. The oracle is the local 1.0.5 build; a
   song that relies on a later feature will not compile there and has no oracle.
3. **Does the bundled Quasar assemble `spc700`? Yes (2026-10-21).** `norom` + `arch spc700` + `org $0000` +
   `base $0200` gives flat bytes at file offset 0 and labels at their ARAM addresses, through
   `Asar.Patch` on an in-memory buffer (`Spc700AsarTests`). Syntax notes for the engine's author: no
   `!` absolute prefix (the operand's size picks direct page or absolute; `.b`/`.w` force it), and
   `immediate,dp` stores are written `mov $f2,#$6c`, `tset`/`tclr` take the accumulator (`tset $0030,a`), and
   `adc a,x` is not an instruction (`adc a,(x)` is).
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
