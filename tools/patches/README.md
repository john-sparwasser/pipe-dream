# Standalone patches

Findings from a sprite-engine performance pass (see `reference/smw-disasm/SPRITE_ENGINE_NOTES.md`).
Neither is wired into the editor's build.

- `sprite_speed.asm` — Asar patch for vanilla SMW or any ROM that leaves `$03A118` and `$07F767` alone
  (Lunar Magic does). Bank 3 sprite dispatch as a jump table (about 60 cycles a sprite a frame on
  average, up to ~145; Ninji and $C6-$C8 get up to 27 slower) and five dead tweaker clears skipped
  per spawn (22 cycles). Test: `src/tests/SpriteSpeedPatchTests.cs` (vanilla vs patched in the CPU
  interpreter; needs `PIPEDREAM_ASAR` and `PIPEDREAM_SMW_ROOT`).
- `loader_step_fastpath.diff` — `git apply` against the repo. The driver's hook on the loader's record
  step (`StepRecord`) is skipped when no sprite takes extra bytes, and plain records take a fast path
  when one does. 1659 -> 1083 interpreter instructions per loader run with 255 sprites (no extra
  bytes), 1659 -> 1227 with. Adds `LoaderCostTests.cs` and a `Cpu65816.Instructions` counter.
