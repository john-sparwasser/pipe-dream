# themeshot

Renders the real editor headlessly (Avalonia headless + Skia) to PNG: every dialog window, the
menu-bar dropdowns, and the main window in each canvas mode. For reviewing theme and layout
changes without clicking through the app.

```sh
dotnet run --project tools/themeshot -c Release -- <outDir> <rom-or-pdp> [pieceName ...]
```

- `<rom-or-pdp>`: a vanilla SMW ROM (a fresh project is made from a copy) or a `.pdp` (its folder
  is copied). The input is never written to. Config and the built-in patch cache go to a temp
  folder (`PIPEDREAM_CONFIG_DIR` / `PIPEDREAM_BUILTIN_DIR`), so the user's config is untouched.
- `pieceName`: any of the names below, case-insensitive; none means all. An unknown name lists
  the known ones and exits 2.
- Output: `<outDir>/<piece>.png`, then `<piece>-2.png` … for a piece with several states, and
  `<outDir>/pieces.json`: `[{"name", "axaml", "images": [...]}]`.
- Exit code 0 when everything rendered, 1 when a piece failed (logged, the rest still render).
  About 25 s for everything.

## Pieces

- `Main` (MainWindow.axaml): 1500x900, one image per mode: Level, Map16, Graphics, Palette,
  Background, Animations, Overworld.
- `Menus` (MainWindow.axaml): one image per top-level dropdown (File, Edit, Level, View, ASM,
  Help), plus any submenu that has items. Headless popups draw into the window, so each image is
  the window cut down to the dropdown and its menu header.
- One per `src/ui/*Window.axaml` except MainWindow, named without `Window`. Each is built with
  the arguments its real call site passes (`grep "new XWindow(" src/ui`), shown modal over the main
  window at its natural size, and captured. A dialog type with no piece is reported as a warning,
  so a new window does not go missing without anyone noticing.

## Data

The project is opened through MainWindow's own `OpenProjectPath`, on level 105. Libraries a
fresh project has empty are seeded through the same session calls the dialogs' New buttons use:
three blocks, two patches, two songs (one numbered), two custom sprites, three UberASM files
(one run in levels 105 and 106), a Course Bot level, and a global ExAnimation slot.

Stand-ins, where the real value would be wrong for a screenshot:

- `Update`: a made-up 1.4.0 release. A real one needs the network.
- `Start`: three made-up recent project paths. The real list holds this run's temp copies.
- `RomInfo`: the temp folder in the file path is replaced with `/Users/mario/Hacks`.
- `TilePicker` (first image): switched to the AN1 source. It opens on file 60, which is blank on a
  vanilla base.
- `BackgroundPicker` and `ExAnimSlot`: nothing in the app opens these. They are built from
  `Backgrounds()` and the seeded slot.
- `LocateBase`: real text, taken from opening a copy of the project with no base ROM beside it.
