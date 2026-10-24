# fixtures — the oracle ROMs, as patches

The tests and the generators compare against ROMs Lunar Magic saved: `after.smc` (a plain level
save on vanilla), the `exanim_*` series (one ExAnimation setting changed at a time), the `layer3/`
and `backgrounds/` series, `sa1/lm-sa1.smc` (clean + SA-1 Pack, one level saved) and
`sa1/lm-sa1-exanim.smc` (the prep's SA-1 image with an ExAnimation slot saved). A ROM is never
committed; each lives here as a BPS patch against the clean US ROM, under the path it has in
`.resources` (`sa1/lm-sa1.bps` is `.resources/sa1/lm-sa1.smc`).

**Using them.** Point `PIPEDREAM_SMW_ROOT` at a folder holding `.resources/SMW.smc` (the clean,
headered US ROM, 524,800 bytes). `ReferenceRoms.Resource` looks in `.resources` first and otherwise
applies the patch here over that ROM into `PipeDream\fixtures\<patch hash>\<name>` under the
per-user local application data folder (`%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux,
`~/Library/Application Support` on macOS), once per patch content. The SA-1 tests also need the
extracted SA-1 Pack beside the SA-1 oracles (`.resources/sa1/SA1-Pack-140/`, from the pack's
release zip; reference/SA1_PORT.md) — a patch cannot carry it.

**Making them.** `PipeDream.exe --fixtures` writes a patch for every ROM under `.resources` except
vanilla, Lunar Magic's `sysLMRestore` copies, the `test/` scratch projects and `prepped-*` images
the prep rebuilds. Reference hacks (ShaoBase, Dogs of War, juz, BigEye, sgdq2024) are other
people's work and are not here; the tests that read them (`[HackRomFact]`) skip without them.

The writer (`src/rom/BpsWriter.cs`) has no source-copy matching, so a run of vanilla data Lunar
Magic relocated is stored as-is rather than referenced; the patches are 20-90 KB, the SA-1 ones
larger because the pack's code is in them.
