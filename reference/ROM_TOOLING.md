# Reference repos: Asar and PIXI

Two read-only clones of the tools the SMW hacking scene actually patches ROMs with. Neither
is a dependency of pipe-dream — they are **source-of-truth documentation** for formats and
behaviours this editor has to match, and both are far more precise than community lore.

| repo | local path | pinned at | what it is |
|---|---|---|---|
| [RPGHacker/asar](https://github.com/RPGHacker/asar) | `~/asar` | `5fd539c` | the SNES assembler everything else calls |
| [JackTheSpades/SpriteToolSuperDelux](https://github.com/JackTheSpades/SpriteToolSuperDelux) | `~/SpriteToolSuperDelux` | `a38c1a8` | PIXI — inserts custom sprites into a ROM |

Neither is built. Clone with `git clone --depth 1` if you re-fetch; `git pull` to refresh.

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
- **Both expose a C ABI**, so if pipe-dream ever needs to assemble or insert rather than
  reimplement, there is a linkable path (see the API notes below).

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

## Querying them

Both have a graphify knowledge graph built (2026-08-19), same layout as this repo's:

```
~/asar/graphify-out/                    1091 nodes / 2252 edges / 67 communities
~/SpriteToolSuperDelux/graphify-out/    1641 nodes / 2954 edges / 111 communities
```

`graph.html` for browsing, `GRAPH_REPORT.md` for the community map, `graph.json` to query.
Run `graphify query "<question>"` from inside either repo. Rebuild with `graphify . --update`.

Caveats recorded at build time: both graphs carry dangling-endpoint edges (74 of 2350 in asar,
333 of 3327 in PIXI) where the AST referenced symbols outside the corpus, and a few files with
C++ parse errors are only partially extracted — `arch-65816.cpp`, `arch-superfx.cpp`,
`sprite.cpp`, and the `asar.h`/`asardll.h` headers. PIXI's 89 CFG-Editor resource PNGs were
not extracted. So treat a *missing* edge as "look at the source", never as "no such relation".
