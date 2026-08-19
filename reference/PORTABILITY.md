# Portability and distribution

The editor is platform-agnostic by construction: file pickers are the platform's own (through
Avalonia's `StorageProvider`), and config lives in the platform's per-user config area. There is
exactly one piece of Windows-specific code in the app — `Program.AttachParentConsole`, six lines
guarded by `OperatingSystem.IsWindows()`, explained under "One executable" below — plus
`install/install.ps1`. What is Windows-only today is the *packaging*, not the app.

## What can be shipped

The native surface is Avalonia 11.3.11 (`ui/PipeDream.Ui`); the storage and services layers are
pure managed code. Avalonia ships natives for every desktop RID that matters:

| Target          | Avalonia | Ship? |
|-----------------|----------|-------|
| win-x64         | ✓        | yes   |
| win-arm64       | ✓        | yes   |
| linux-x64       | ✓        | yes   |
| linux-arm64     | ✓        | yes   |
| osx-x64         | ✓        | yes   |
| osx-arm64       | ✓        | yes   |

This is wider than it used to be. The ImGui/Foster stack the editor was built on first capped
the matrix at three targets — Foster shipped no SDL3 for `win-arm64` or `linux-arm64`, so those
were simply unavailable. Nothing in the app ever needed them; the dependency did.

## Packaging shape per platform

- **Windows** — `install/install.ps1` (per-user: `%LOCALAPPDATA%`, Start Menu, `.pdp` via
  HKCU). An Inno Setup `.exe` would make it double-clickable.
- **macOS** — a `.app` bundle: `Contents/MacOS`, `Info.plist`, `Contents/Resources/*.icns`.
  File association is declarative via `CFBundleDocumentTypes` — no install step at all.
  Ship as `.dmg`.
  **Gatekeeper is the real cost:** unsigned bundles are quarantined ("damaged, move to
  Trash") and need a right-click→Open or `xattr -dr com.apple.quarantine`. A clean
  double-click install needs an Apple Developer ID (~$99/yr) plus `codesign` +
  notarization. Apple Silicon additionally refuses to run a binary with no signature at
  all, so ad-hoc signing (`codesign -s -`) is the bare minimum.
- **Linux** — AppImage: one file, no install, distro-agnostic. `.desktop` +
  `shared-mime-info` XML give the `.pdp` association and icon.

`dotnet publish -r <rid>` cross-builds from any host (we do not AOT), but `codesign` and
`notarytool` need real macOS and AppImage tooling wants Linux — so releases want a
three-runner CI matrix rather than one machine. Each self-contained payload is ~85 MB.

## One executable, two halves

`ui/PipeDream.Ui` is the whole application. Run it plainly and it opens the editor; run it with
`--headless`, or with any command flag, and it runs the ROM toolbelt instead (`--selfcheck`,
`--diff`, `--render`, headless project create and build — the things CI uses). `PipeDream.csproj`
and `services/PipeDream.Services` are libraries.

That costs one piece of platform-specific code, in `Program.AttachParentConsole`. A Windows
GUI-subsystem binary gets no console, so a command would run and print nothing; it borrows the
launching terminal's console instead. The alternative — a console-subsystem binary — would flash
a console window every time someone opens the editor, which is the worse trade. Unix has no
subsystem distinction and skips it entirely.

## Platform-dependent code

All pinned by `PortabilityTests`:

- `Config.DirFor` — macOS gets `~/Library/Application Support`; Windows and Linux go
  through `ApplicationData` (`%APPDATA%` / `$XDG_CONFIG_HOME`), which is already right.
- `Config.PathComparison` — case-insensitive except on Linux, so two genuinely different
  files there cannot collapse into one recents entry.
- `ReferenceRoms.Root` — `PIPEDREAM_SMW_ROOT` relocates the reverse-engineering ROMs so
  `--selfcheck` and the `RealRomFact`/`LmRefRomFact` tests run off Windows. The historical
  `C:\SMW\Projects` stays the default. Checks whose oracle ROM is absent skip rather than
  fail, so a partial set is fine.
