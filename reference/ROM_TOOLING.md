# Reference repos: the SMW ROM tool chain

Read-only clones of the tools the SMW hacking scene actually patches ROMs with. None is a
dependency of pipe-dream — they are **source-of-truth documentation** for formats, addresses
and behaviours this editor has to match, and all are far more precise than community lore.

| repo | local path | pinned at | what it is |
|---|---|---|---|
| [RPGHacker/asar](https://github.com/RPGHacker/asar) | `~/asar` | `5fd539c` | the SNES assembler everything else calls |
| [JackTheSpades/SpriteToolSuperDelux](https://github.com/JackTheSpades/SpriteToolSuperDelux) | `~/SpriteToolSuperDelux` | `a38c1a8` | PIXI — inserts custom sprites into a ROM |
| [HertzDevil/AddmusicK](https://github.com/HertzDevil/AddmusicK) | `~/AddmusicK` | `c6f8f46` | AMK — inserts music and sound effects |
| [VitorVilela7/UberASMTool](https://github.com/VitorVilela7/UberASMTool) | `~/UberASMTool` | `828aff3` | runs custom code per level / gamemode / frame |
| GPS V1.4.5 ([SMWC](https://www.smwcentral.net/?p=section&a=details&id=40056)) | `~/GPS` | zip, Mar 2024 | Gopher Popcorn Stew — inserts custom blocks |

None is built. The first four are git clones — `git pull` to refresh. **GPS is not a git repo**:
it has no public upstream, so `~/GPS` is an extracted release zip with `PROVENANCE.txt`
recording where it came from. Re-fetch per the note in that file.

They stack: **Asar is the assembler the other four drive.** PIXI, AMK, UberASM and GPS each
claim ROM space through it, each ship their own `list.txt`-style manifest, and each hijack SMW's
main loop. A hack with all five applied is the normal case, which is why their conflict notes
matter as much as their formats.

Everything below is **[TOOL-DOC]** — stated in these repos' own manuals, headers, or build
files, read this session. None of it is byte-verified against a ROM here. Promote a fact to
`reference/CONTRACT.md` with **[CONFIRMED]** only after checking it in Mesen or Ghidra.

## Why these matter to pipe-dream

- **PIXI writes the same Lunar Magic aux files pipe-dream reads** — `.ssc`, `.mwt`, `.mw2`,
  `.s16`, plus `extmod` logging. It is a second independent implementation of the aux-file
  contract in `CONTRACT.md`, so a disagreement between it and our reader is a real bug in one
  of them.
- **Sprite data layout**: PIXI's `MeiMei` component exists purely to remap per-level sprite
  data when insertion shifts it. Any editor that rewrites sprite data has the same problem.
- **Freespace semantics**: Asar's `freespace`/`freecode`/`freedata`, **RATS** tags, `autoclean`
  and `prot` are how third-party patches claim and release ROM space. Writing over a RATS
  region is how you corrupt someone's hack.
- **Address translation**: Asar implements every mapper (`lorom`, `hirom`, `exlorom`,
  `exhirom`, `sa1rom`, `fullsa1rom`, `sfxrom`, `norom`). `snestopc()` in `src/asar/libsmw.cpp`
  is the reference implementation of the conversion `CONTRACT.md` §1 does by hand.
- **UberASM documents the hijack points and RAM claims by address** — see its table below.
  These are the addresses a level editor must not stomp, written down concretely rather than
  inferred, and they double as a map of where SMW's per-frame and per-level hooks live.
- **AMK is where the audio side of the ROM is specified** — the two SFX ports, the SPC engine
  upload, and the ARAM budget that constrains anything music-related.
- **GPS defines what a map16 tile *does*** — pipe-dream places map16 tiles; GPS is what gives a
  tile custom behaviour, via SMW's `db $42` block system and Lunar Magic's contact handlers.
  Its `defines.asm` and `main.asm` are the clearest statement anywhere of how a block is reached
  from a collision, and its `routines/` folder documents the map16 read/write path we duplicate.
- **Asar and PIXI expose a C ABI**, so if pipe-dream ever needs to assemble or insert rather
  than reimplement, there is a linkable path (see the API notes below).

## Asar — what to look at

| you want | look at |
|---|---|
| the language, every directive, every trade-off | `docs/manual/index.html` (large; read in slices) |
| every named error / warning ID | `docs/manual/errors-list.html`, `warnings-list.html` |
| SNES↔PC address maths, mappers | `src/asar/libsmw.cpp` |
| the embeddable API | `src/asar/interface-lib.cpp`, `src/asar-dll-bindings/` |
| bindings to copy from | `asar.h`, `asardll.h` (C), `asar.cs` (C#), `asar.py` (Python) |

- Build targets are `asar-standalone` (ships as `asar`), `asar` (shared) and `asar-static`,
  all three from one shared source list. DLL API version **3.3.0** per `src/asar/CMakeLists.txt`.
- The **C# bindings (`asar.cs`) are the relevant ones for us** — pipe-dream is C#, and they
  are already written and maintained upstream.
- Asar is **multi-pass**; labels are not final until the last pass. Its manual is explicit that
  this is why several features (static-label restrictions in conditionals, `includeonce`)
  behave the way they do. Worth reading before assuming a single-pass mental model.
- It emits **symbol files** for debuggers (WLA address-to-line, no$sns) — a ready-made way to
  map generated code back to source if we ever assemble.
- **Licensing is unresolved in-repo**: WTFPL, LGPL and GPL texts all ship, but no build file
  assigns a licence to a target. The only guidance is `README.md:28`, telling static-link users
  to have "an Asar compatible license". Resolve this with upstream before linking Asar into
  anything shipped.

## PIXI — what to look at

| you want | look at |
|---|---|
| the whole tool, in prose | `README.md` (the single best doc; long and dense) |
| sprite config formats | the `.cfg` and `.json` sections of `README.md`, `src/cfg.cpp`, `src/json.cpp` |
| the LM aux files it writes | `src/lmdata.cpp`, `src/iohandler.h` |
| sprite data remapping | `src/MeiMei/` |
| the insertion pipeline | `src/sprite.cpp` — `patch_sprites_all_in_one()`, `patch_sprite()` |
| a C# GUI that edits sprite configs | `src/CFG Editor/` |

- Sprite slots are **fixed ranges**: normal `00-BF`, per-level `B0-BF`, shooters `C0-CF`,
  generators `D0-DF`, plus cluster, extended and misc types. `list.txt` groups them with type
  headlines (`SPRITE:`, `CLUSTER:`, `EXTENDED:` …).
- Four different "extra data" concepts, easy to confuse: **tweak bytes**, **extra property
  bytes**, **extra bytes**, **shooter extra bytes**, and the **indirect data pointer**. The
  indirect pointer exists because Lunar Magic caps per-sprite data at 12 bytes — that cap is a
  constraint on us too.
- Ships **`.cfg` and `.json`** config formats; the CFG Editor converts between them. If
  pipe-dream ever reads sprite configs, JSON is the modern one.
- Bundles **Asar v1.91** via CMake `FetchContent` and links it — the concrete example of
  embedding Asar in another tool.
- Also exposes a **library API with C/C++/C#/Python bindings** plus a C-ABI **plugin system**.

## UberASMTool — what to look at

| you want | look at |
|---|---|
| the hijack and dispatch framework | `assets/asm/base/` — `main`, `global`, `level`, `gamemode`, `overworld`, `sprites`, `statusbar` |
| every SMW sprite table named, LoROM and SA-1 | `assets/other/macro_library.asm` |
| the formal file-format spec (v2.0) | `Specifications/UberAssemblyFile.txt` |
| the `list.txt` format, prose | `assets/readme.txt`, with `assets/list.txt` as the worked example |
| minimal working user code | `assets/*/example.asm`, `examples/*.asm` |
| the tool itself (C#) | `UberASMTool/` — `Program.cs`, `UberConfigProcessor`, `DataCollector` |

Hijack points, all `autoclean JML` except the bank-`$05` loader:

| address | what it takes over |
|---|---|
| `$00804E` | reset — clears pointer tables |
| `$00806B` | main game loop, per frame (global code) |
| `$008176` | NMI / vblank |
| `$05808C` | `load` entry |
| `$05D8B7` | level-number latch |
| `$00A242` / `$00A295` | level `main` |
| `$00A5EE` | level `init` |
| `$009322` | game mode jump table |
| `$00A1C3` / `$00A18F` | overworld `main` / `init` |
| `$008E1A` | status bar |

- **Dispatch is one shape everywhere**: id × 3 (`ASL`+`ADC`), two loads pull a 24-bit pointer,
  low word to `$00`, bank in A, then `JML [!dp]`. `null_pointer` (a bare `RTL`) fills empty
  slots so there are no null checks. NMI uses a twin path through `$6E`-`$70` because `$00`
  isn't safe there. Index sources: `!level = $010B`, gamemode `$0100`, overworld `$1F11,x`
  indexed by `$0DB3` (player).
- **RAM it claims**: sprite tables default to `$7FAC80` (LoROM, 38 bytes) / `$41AC80`
  (SA-1, 68 bytes — SA-1 Pack's sprite table is 22 slots, not 12). Map16 level-table writes
  target `$7E`/`$7F:C800-FFFF`, or `$40`/`$41:C800-FFFF` on SA-1. **These overlap what a level
  editor writes — check before allocating.**
- **SA-1 is autodetected at assembly time** via `read1($00FFD5) == $23`, which rewrites
  `!sa1` / `!dp` / `!addr` / `!bank` / `!sprite_slots`. Every address above therefore has two
  forms, and any code we generate has to pick the same way.
- ROM space is per-resource **RATS**-tagged (8 bytes of tag each, `0x7FF8` max per bank), and
  the tables are fenced by literal `db "uber"` / `db "tool"` markers the tool scans for when
  cleaning a previous insertion. Those markers are how you detect UberASM in a ROM.
- It uses **the same hijacks as the older uberASM/levelASM patches**, so it can be applied over
  them — back up first.
- **Doc/code discrepancy found**: `assets/readme.txt` documents `;` as the `list.txt` comment
  character; the shipped `assets/list.txt` uses `#` throughout. Unresolved — check the parser
  (`UberConfigProcessor.ParseList()`) before relying on either.

## AddmusicK — what to look at

| you want | look at |
|---|---|
| the MML language | `doc/readme_files/syntax_reference.html` |
| every `$xx` hex command and DSP register | `doc/readme_files/hex_command_reference.html` (the densest doc in any of these repos) |
| the insertion workflow and CLI | `doc/readme_files/general_basic_use.html`, `general_advanced_use.html` |
| the SFX system | `doc/readme_files/sound_effects.html` |
| real minimal SFX sources | `test/1DF9/`, `test/1DFC/` — 93 tiny MML files |
| the manifest formats | `test/Addmusic_list.txt`, `Addmusic_sample groups.txt`, `Addmusic_sound effects.txt` |
| the compiler | `AddmusicK/` — `AMKd::MML` (parsing), `AMKd::Music`, `AMKd::Binary` (output) |

- **The two SFX banks are `1DF9` and `1DFC`** — SMW's two sound-effect ports. Per AMK's docs the
  *only* difference between them is the SPC channel used (#6 vs #7). `$1DFA` is echo control and
  `$1DFB` is song upload.
- `Addmusic_sound effects.txt` declares SFX as `<hex slot> [flag] TAB <filename>`, resolved
  against the same-named folder. `*` = emit the slot as a pointer to an already-inserted effect
  (saves ARAM); `?` = suppress AMK's auto-appended `$00` terminator.
- **The real SFX sources are pure `$xx` hex** — not a single note letter across all 93 files.
  Grammar is: length byte, optional volume, optional second volume (= L/R pan), then a note
  byte ≥ `$80`; bare notes inherit the previous header.
- **ARAM is the binding constraint** on everything audio. Global songs never transfer but cost
  ARAM permanently; the echo buffer must be reserved up front (`$FA $04`) or you get
  "Echo buffer exceeded total space in ARAM"; echo delay is capped `$00-$0F` by ARAM cost.
  The docs warn a rogue echo buffer "can cause irreparable damage".
- **`<ROMNAME>.msc` is the Lunar Magic handoff** — AMK writes it so LM knows the song list.
  That is a file pipe-dream will have to read or write if it ever manages music.
- Prefers **`asar.dll` over `asar.exe`**, both expected in the program directory. Backs the ROM
  up as `ROMNAME~`.
- Also worth knowing: **channels 6-7 are shared with sound effects and get cut**, channel 0
  can't be pitch-modulated, and loops cannot nest (`[[ ]]` is layer two, remote event 4 is an
  effective third).
- **Dead data found in the shipped test corpus**: `test/1DFC/0E L + R scroll.txt` and
  `1DFC/1D Time low 2.txt` each hold a mid-stream `$00` followed by a verbatim copy of a 1DF9
  effect. A zero length byte ends the effect, so those trailing blocks are unreachable. Flagged
  AMBIGUOUS in the graph — verify against `SoundEffect.cpp` before citing it.

## GPS — what to look at

| you want | look at |
|---|---|
| the address map blocks are written against | `defines.asm` — SA-1 aware, this is the important one |
| the block dispatch framework | `main.asm` |
| **the block API contract** | `blocks/template.asm` — the entry points a block implements |
| the shared routine library | `routines/` — 27 routines, map16 / spawn / destroy / score |
| the tool itself | `src/main.cpp` (30KB, ships in `src.zip`) |
| the format and CLI docs | `README.txt`, `Changes.txt` |

- **A block is reached through Lunar Magic's contact handlers.** GPS writes twelve identical
  16-byte stubs over `$06F690`…`$06F7E0` (Below, Above, Side, SpriteV, SpriteH, WallFeet,
  WallBody, …). Each stub loads `id*3+1` — the byte offset of that contact type's `JMP` in the
  block header, because `db $42` is followed by three-byte `JMP`s — and `JSL block_execute`.
- **`block_execute`** checks `block_bank_byte` (0 = not inserted), pulls a 16-bit pointer from
  `block_pointers_1`, or `block_pointers_2-$8000` for ids ≥ `$4000` where the already-shifted
  negative X *is* the range test, adds the contact offset, `LDX $15E9`, then `JML [$0000|!dp]`.
- **`db $37` is the wall-run header.** Contact offsets ≥ `#$001E` (WallFeet/WallBody) are only
  honoured when the block's first byte reads `$37` — that byte is the entire opt-in mechanism.
  Two extra hijacks support it: `WallRun` at `$06F67B` and `FixSpriteH` at `$06F717`.
- **SA-1 is resolved at assembly time**, same trick as UberASM: `read1($00FFD5)==$23` detects
  SA-1 and `$00FFD7==$0D` picks `fullsa1rom` over `sa1rom`, flipping `!dp` `$0000`→`$3000`,
  `!addr` `$0000`→`$6000`, `!bank` `$800000`→`$000000`, `!sprite_slots` `$0C`→`$16`. The
  `define_sprite_table` macro emits two aliases per table (`!sprite_status` *and* `!14C8`), which
  is why routines can be written with raw vanilla addresses and still be SA-1 correct.
- **Block positions live in `$98-$99` (Y) and `$9A-$9B` (X)**, level space, and `$1933` is the
  layer being processed. `$5B` bit 0 (vertical level) is why `get_map16`/`change_map16` swap
  `$99`/`$9B` internally — and why a `swap_XY` routine exists purely to undo it. Worth knowing
  before trusting either convention.
- **`-O2` is a documented crash risk**: it truncates the bank-byte table, so undefined block IDs
  end up with no bank byte and **will crash if Mario touches them**. `-O2` also silently implies
  `-O1` (not in the README). `-O3` parses but errors as unimplemented.
- Block IDs run **`0x200`-`0x7FFF`**, acts-like must be `< 0x8000` — bounds the README only
  implies. The shipped `list.txt` is empty apart from a comment.
- GPS finds its own data in an already-patched ROM via a `"GPS_VeRsIoN"` marker embedding the
  table addresses and sizes. It can also **detect and strip BTSD** (string-scans for
  "Blocktool Super Deluxe"), but only hack version `$3136`.
- **README is out of date in one spot**: `-nw` was added in V1.4.5 and appears in the tool's own
  `-h` output but not the README. Flagged AMBIGUOUS in the graph.
- Sharp edges found in the routine library, all flagged in the graph: `$185E` is overloaded
  across three routines; `move_spawn_above_block` does `TAX` with no `PHX` and so clobbers the
  caller's X, unlike all four of its siblings; `rainbow_shatter_block` differs from
  `shatter_block` by exactly one omitted `LDA #$00`.

## Querying them

All five have a graphify knowledge graph built (asar/PIXI/AMK/UberASM 2026-08-19, GPS
2026-08-20), same layout as this repo's:

```
~/asar/graphify-out/                    1091 nodes / 2252 edges /  67 communities
~/SpriteToolSuperDelux/graphify-out/    1641 nodes / 2954 edges / 111 communities
~/AddmusicK/graphify-out/               1018 nodes / 1899 edges /  75 communities
~/UberASMTool/graphify-out/              329 nodes /  583 edges /  20 communities
~/GPS/graphify-out/                      239 nodes /  420 edges /  12 communities
```

`graph.html` for browsing, `GRAPH_REPORT.md` for the community map, `graph.json` to query.
Run `graphify query "<question>"` from inside any of them. Rebuild with `graphify . --update`.

Caveats recorded at build time. All five carry dangling-endpoint edges, where the AST
referenced symbols outside the corpus (asar 74 of 2350, PIXI 333 of 3327, AMK 82 of 1991,
UberASM 50 of 686, GPS 14 of 443), plus some collapsed parallel edges where the same pair got
both `implements` and `references`. Files with C++ parse errors are only partially extracted:
`arch-65816.cpp`, `arch-superfx.cpp`, `sprite.cpp`, `Music.cpp`, `MML/Lexers/Core.cpp`, and the
`asar.h`/`asardll.h` headers. Not extracted at all: PIXI's 89 CFG-Editor resource PNGs, and
AMK's `back.png` nav arrow.

**One gap worth remembering**: graphify does not recognise `.asm`, so UberASM's 15 and GPS's 30
assembly files were extracted by a subagent reading them directly, and they are **absent from
those repos' `manifest.json`** — a `--update` will not notice when they change. Re-extract them
by hand, or accept the graph going stale on the assembly side. Since the assembly *is* the
interesting part of both tools, that matters. The same blind spot applies to PIXI's `asm/` tree,
which was never extracted at all.

So treat a *missing* edge as "look at the source", never as "no such relation".
