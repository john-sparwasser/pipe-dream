# Driving Lunar Magic as a test harness

Lunar Magic is the compatibility oracle. `reference/CONTRACT.md` §0 states the requirement —
a ROM must survive editing in either tool and reopening in the other — and this file is how
you *check* it, because on this question analysis is worth very little and running the real
binary is worth a lot. Everything below was measured against `Lunar Magic.exe` directly.

```
"Lunar Magic.exe" -ExportAllMap16 "rom.smc" "out.map16"
"Lunar Magic.exe" -ExportLevel    "rom.smc" "105.mwl" 105
"Lunar Magic.exe" -ImportLevel    "rom.smc" "105.mwl" 105
```

Full switch list: `reference/lm-help/html/info_command_line.htm`. Numbers are hex.

Tooling lives in `tools/lm/`:

| file | what it is |
|---|---|
| `Invoke-LunarMagic.ps1` | runs one operation, returns `{ExitCode, TimedOut, Output, AccessDenied, ChecksumWarned, Changed}` |

Set `PIPEDREAM_LUNAR_MAGIC` to point at the binary; otherwise it looks in
`C:\SMW\Projects\.resources\Lunar Magic\`.

## Two hazards, both structural

**LM reports through message boxes.** From `info_command_line.htm`: *"Most warnings and error
messages from Lunar Magic will still be displayed using message boxes."* There is no documented
`-quiet`, no documented exit codes, and nothing to redirect a dialog into. A dialog means the
process never exits — so **every invocation needs a timeout and a kill**, and a timeout is a
FINDING, not a flake: it means LM had something to say. Hiding the window does not stop a
dialog from blocking, it only keeps it off the screen.

**LM is not a console program.** A caller does not wait on it by default, and its output only
exists if redirected to a file (UTF-8). `Invoke-LunarMagic.ps1` does both.

## Measured facts

- **`$0DF100` must be `$FF`.** It is LM's level-access flag: any other value and *every*
  operation fails with *"Lunar Magic : Access Denied! The author of this hack has chosen to
  restrict level access."* It is documented **nowhere** in LM's help. Prep v1–v4 wrote handler
  code over it, so every base this editor produced was unopenable in LM until v5. LM's own
  codegen leaves it alone — in ShaoBase the surrounding block stops at `$0DF0F8`.
- **Reading a v5-prepped base works.** `-ExportAllMap16` exits 0. On a v4 base it exits 1.
- **The checksum warning is benign.** *"The ROM's checksum has been tampered with"* appears on
  our bases and LM proceeds anyway. It is not an invalid checksum — ours computes correctly —
  and restoring vanilla's `$A0DA`/`5F25` bytes does **not** silence it. LM itself expands ROMs
  without touching the checksum (a 1MB LM save still carries vanilla's), so it is detecting
  third-party modification some other way.
- **Vanilla and LM-saved ROMs are clean controls.** Both export with exit 0 and no warnings,
  which is what makes a warning on our ROM meaningful.

## Driving the GUI when the CLI will not do

The CLI cannot answer "what does LM WRITE when you use feature X" — its write switches hang on a
dialog. The GUI can, and it is scriptable enough:

1. Launch LM on a copy, `AppActivate` the process, then send menu accelerators as ONE
   invocation — `%l` then `m` opens Level > Modify Main and Midway Entrance. Split across two
   calls the second `AppActivate` closes the menu first.
2. Click dialog controls with `SetCursorPos` + `mouse_event` at DEVICE coordinates after
   `SetProcessDPIAware`. A capture taken without that call is in scaled coordinates and clicks
   land 25%% off.
3. `Ctrl+A` does not select the contents of LM's hex fields; `{END}` then backspaces does.
4. Ctrl+S saves the level. Expect **"Restore System Issue"** first — LM wants the original
   unmodified ROM for its restore point. Cancel proceeds with the save.
5. Diff against the copy taken before.

That sequence is what produced the finding in CONTRACT §0 that LM can write to a prepped base
and the result does not boot.

## The bisect method

This is how `$0DF100` was found, and it generalises to any "LM dislikes our ROM" report:

1. Confirm the control. Run the same operation on an untouched vanilla ROM and on a real LM
   save. If either misbehaves, the harness is wrong, not the ROM.
2. Bisect by **prep version** — `--prep <rom> [version]` takes a version for exactly this.
   That tells you which stamp list introduced the problem.
3. Bisect by **region**. Copy vanilla's bytes back over part of the prepped ROM and re-probe;
   whichever restoration clears the symptom contains the cause. Preserve the size code at
   `$FFD7` and the checksum bytes when reverting, or you change two things at once.
4. Diff vanilla against the prepped ROM to get the exact stamped runs, then binary-search
   within the guilty run down to the byte.
5. Confirm the value, not just the location: set that byte to a range of values and find which
   ones LM accepts. `$0DF100` accepts only `$FF`.
6. Check what a real LM hack does at the same address. If LM's own output avoids it, it is
   reserved and you must too.

## Unsolved: the write path

`-ImportLevel` succeeds on a vanilla copy (exit 0) but **hangs on a message box** on a
v5-prepped base, printing nothing at all — so the dialog comes up before any output. Reading
is unaffected; only writing trips it.

Next step is to read the dialog. There is no way to suppress it, so the harness has to
enumerate the process's windows and pull their static text. A first attempt at that
(`EnumWindows` + `EnumChildWindows` via `Add-Type`) hung the calling shell and needs a more
careful approach — likely a separate short-lived process doing the enumeration, so a blocked
caller cannot take the harness down with it.

Suspects, none confirmed:

- A conflicted- or nested-RATS warning on save. `option_restore.htm`: *"A nested RAT is often
  an indication that a third party tool or patch has misapplied changes… If these are detected
  the program will warn you."* Saving is exactly when LM would look. Our expansion blocks are
  RATS-tagged, and `info_rats_format.htm` is emphatic: *"Nested RATs are not allowed!!"*
- LM refusing to install its own hijacks around structures it did not write. LM installs
  hijacks on save (`option_vram.htm`) and some on merely opening a dialog
  (`level_super_bypass.htm`, `level_layer3_gfx.htm`, `level_extend_ani.htm`), so an import
  will try to write over ground we already stamped.

Expect quiet failures rather than loud ones. `option_vram.htm`: *"If Lunar Magic does not
recognize the version of the patch currently installed, all options may be disabled."* A
stamped-but-unrecognised hack greys out LM's UI instead of raising anything.
