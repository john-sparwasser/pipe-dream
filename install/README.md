# Installing Pipe Dream

Per-user install — no admin rights, nothing outside your own profile.

```powershell
.\install\install.ps1
```

That publishes a self-contained build and then:

- copies it to `%LOCALAPPDATA%\Programs\PipeDream`
- adds a **Pipe Dream** Start Menu shortcut
- registers `.pdp` so double-clicking a project opens it

Re-run it any time to upgrade in place. `-SkipPublish` reuses the last published build in
`bin\publish` instead of rebuilding.

```powershell
.\install\uninstall.ps1
```

Removes all three. Your config (`%APPDATA%\PipeDream`) and project folders are left alone,
so uninstalling never costs you work.

## Notes

- Close Pipe Dream before installing or uninstalling — both refuse to run while the exe is
  in use rather than leaving a half-copied folder behind.
- The `.pdp` association is written under `HKCU\Software\Classes`, and registered through
  `OpenWithProgids` as well as the default, so it does not silently steal the extension
  from another tool you may have pointed at it.
- The published build is self-contained (~80 MB): it carries its own .NET runtime, so the
  machine you install on needs nothing preinstalled.
