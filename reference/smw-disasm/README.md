# SMW Disassembly — split + knowledge graph

Source: `C:\Users\johns\Documents\SMWDisC.txt` (community-annotated Super Mario World
disassembly, ~123k lines). Chopped here into per-bank files and turned into a routine
call graph so the pipe-dream editor work can navigate it and Claude can query it.

## Files

- `bank_XX.asm` — the disassembly split by ROM bank (00–07, 0C, 0D). Every line is
  `LABEL/$addr:  hex bytes  MNEMONIC operand  ; comment`. Banks 08–0B (raw graphics
  data, no code labels) are not separate files.
- `index.txt` — the original header/bank index from the top of the dump. Two of its
  claims are now outdated: bank 06 ("Unknown contents") is level Layer-1 object data,
  and bank 0C ("Credits") is half overworld map data — see `SYSTEM_NOTES.md` §7.

## Analysis docs (start here)

- `SYSTEM_NOTES.md` — boot/RESET, SPC700 upload protocol, main loop + NMI + IRQ
  (status-bar raster split), game-mode dispatch, DMA channel allocation, per-bank
  guide, load-bearing RAM map.
- `SNES_HARDWARE_NOTES.md` — every PPU/CPU register SMW touches and how, plus the
  65816 idioms (`ExecutePtr` trampoline explained instruction-by-instruction, XBA,
  MVN, PHB/PLB, code-generated-into-RAM) and the myth-busting list (no WAI, no SED,
  no DP relocation).
- `LEVEL_PIPELINE_NOTES.md` — level number → tiles: 5-byte header bit layout,
  pointer tables, 3-byte object format, object→Map16 engine in bank 0D, Layer 2/3.
- `SPRITE_ENGINE_NOTES.md` — sprite engine architecture + redundancy/inefficiency
  catalog (utility clones, GFX clone families, dispatch and per-frame costs).
- Graph outputs live in `graph/` (rebuild: `tools/build_disasm_graph.py`, run with
  graphify's Python; edit `graph/disasm_labels.json` then rerun with `--relabel`):
  - `graph.html` — interactive call graph (open in a browser).
  - `graph.json` — 735 nodes (routines + the analysis docs), 1501 `calls` edges +
    149 `documents` edges (each .md linked to every routine it mentions), 53
    communities.
  - `GRAPH_REPORT.md` — god nodes, communities, suggested questions.

Note: the original SMWDisC dump contained bank $01's init-routine region twice
(two comment revisions of $018311–$01875B). The stale first copy was removed
from `bank_01.asm` on 2026-07-24; the graph was rebuilt after the fix. See
`SPRITE_ENGINE_NOTES.md` for the sprite-engine redundancy/inefficiency analysis.

## What the graph is

Nodes = human-named routines. Edges = `JSR`/`JSL`/`JML`/`JMP` calls between them
(deterministically extracted — 100% EXTRACTED, no LLM guessing). Calls that route
through auto-labelled `CODE_xxxxxx` basic blocks are NOT edges, so the graph is a
navigation map of named routines, not a complete control-flow graph. For full detail
on any routine, open its `bank_XX.asm` at the address `explain` reports.

## Routines that matter for the editor (read path: open → decode → render)

| Concern | Community | Key routines | Bank |
|---|---|---|---|
| Level data load | 20 "Level Data Loading" | `LoadLevelData` ($058601), `LevLoadContinue`, `LevLoadExtObj`, `LoadNoHiCoord`, `LoadAgain` | 05 |
| Level object parse | 39 "Level Normal-Object Load" | `LevLoadNrmObj`, `LevLoadJsrNrm` | 05 |
| Level + Map16 | 40 "Level & Map16 Load" | `LoadLevel`, `TilesetMAP16Loc` | 05 |
| Sprites from level | 22 | `LoadSprFromLevel`, `GenSpriteFromBlk`, `CallGenerator` | 02 |
| FG/BG GFX upload | 24 "FG/BG GFX Upload" | `UploadGFXFile`, `PrepLoadFGBG`, `OBJECTGFXLIST`, `GFXTransferLoop` | — |

Bank 05 is the main level-loading bank. Start there for the object stream / Map16
decode logic that `reference/CONTRACT.md` marks `[verify]`.

## Querying (from the pipe-dream project root)

```
graphify explain "LoadLevelData"          # a routine's address, community, callers/callees
graphify query "how are sprites loaded"   # BFS over the graph (start-node picker is fuzzy)
graphify path "A" "B"                      # shortest call path (only if a named-routine path exists)
```

`explain` is the most reliable — it gives the exact `bank_XX.asm` + address to open.
