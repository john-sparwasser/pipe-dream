# Portability and distribution

The editor is platform-agnostic by construction: file pickers are SDL3
(`FileDialog`), config lives in the platform's per-user config area, and there is no
P/Invoke, Win32 or registry code anywhere in `src/` — the only Windows-specific code is
`install/install.ps1`. What is Windows-only today is the *packaging*, not the app.

## What can be shipped  [VERIFIED against the package contents]

The matrix is capped by the native dependencies, not by our code:

| Target            | SDL3 (Foster 0.3.0) | cimgui (ImGui.NET 1.90.5.1) | Ship? |
|-------------------|---------------------|-----------------------------|-------|
| win-x64           | ✓                   | ✓                           | yes   |
| linux-x64         | ✓                   | ✓                           | yes   |
| osx-x64 + arm64   | ✓ universal         | ✓ universal                 | yes   |
| win-arm64         | ✗                   | ✓                           | no    |
| linux-arm64       | ✗                   | ✗                           | no    |

Two things worth knowing:

- **Every macOS dylib is a 2-arch universal binary** (`libSDL3.dylib`, `libcimgui.dylib`
  are FAT with 2 slices). Intel and Apple Silicon are covered by one payload, so a true
  universal `.app` only needs the two apphosts `lipo`'d — the managed assemblies are IL and
  the natives are already fat.
- **ARM is unavailable on Windows and Linux** until Foster ships SDL3 for those RIDs.
  ImGui.NET has `win-arm64`; Foster does not, so the intersection is empty.

Foster 0.3.0 dropped the old `FosterPlatform` native — SDL3 is the whole native surface now.

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
  `shared-mime-info` XML give the `.pdp` association and icon. SDL3 is bundled but dlopens
  the system X11/Wayland, GL and audio libraries, so those must be present.

`dotnet publish -r <rid>` cross-builds from any host (we do not AOT), but `codesign` and
`notarytool` need real macOS and AppImage tooling wants Linux — so releases want a
three-runner CI matrix rather than one machine. Each self-contained payload is ~85 MB;
`PublishTrimmed` would cut that but ImGui.NET and Foster rely on reflection, so it needs
proving before it is turned on.

## Platform-dependent code

Four places, all pinned by `PortabilityTests`:

- `Config.DirFor` — macOS gets `~/Library/Application Support`; Windows and Linux go
  through `ApplicationData` (`%APPDATA%` / `$XDG_CONFIG_HOME`), which is already right.
- `Config.PathComparison` — case-insensitive except on Linux, so two genuinely different
  files there cannot collapse into one recents entry.
- `ImGuiLayer.MonospaceCandidates` — Cascadia/Consolas, SF Mono/Menlo, DejaVu/Liberation.
  Anything missing falls back to the embedded Roboto, which is proportional but works.
- `ReferenceRoms.Root` — `PIPEDREAM_SMW_ROOT` relocates the reverse-engineering ROMs so
  `--selfcheck` and the `RealRomFact`/`LmRefRomFact` tests run off Windows. The historical
  `C:\SMW\Projects` stays the default. Checks whose oracle ROM is absent skip rather than
  fail, so a partial set is fine.
