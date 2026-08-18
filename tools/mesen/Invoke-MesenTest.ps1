<#
.SYNOPSIS
Run a Lua script against a ROM in Mesen's HEADLESS test runner and return its exit code.

.DESCRIPTION
Mesen's `/testrunner` mode loads a ROM plus a companion Lua script with no GUI, no video,
no audio and no input device, running the emulation as fast as it can. This replaces the
old way of checking in-game behaviour (driving the Mesen window with synthetic clicks and
keystrokes), which needed the foreground, stole focus from whatever the user was doing, and
took minutes per check. Here a check is a couple of seconds and nothing appears on screen.

  Mesen.exe /testrunner <rom> <script.lua> /timeout=<wall-clock seconds>

THE EXIT CODE IS THE ONLY OUTPUT CHANNEL. The Lua sandbox has no `io` and no `os` (verified:
`io` is nil), `emu.log` goes nowhere in this mode, no .srm is flushed on stop, and
`emu.takeScreenshot()` returns data rather than writing a file. So a script reports by
calling `emu.stop(code)`, and anything richer is expressed as more runs — scripts are
GENERATED per probe (see New-MesenProbe.ps1), so baking a different question into each one
costs nothing.

Protocol (see README.md):
    0        pass
    1..99    a failure reason the script chose
    0..255   an observed byte, for probe scripts (the caller knows which it asked for)
    -1       Mesen killed the run on /timeout — the script never called emu.stop

.PARAMETER TimeoutSec
WALL CLOCK seconds, not emulated. Headless SMW runs ~210 fps (~3.5x realtime) on this
machine, so budget roughly frames/200 seconds plus a second of startup.

.EXAMPLE
  ./Invoke-MesenTest.ps1 -Rom build/test.smc -Script probe.lua -TimeoutSec 30
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [Parameter(Mandatory)][string]$Script,
    [int]$TimeoutSec = 60,
    # Override with PIPEDREAM_MESEN; falls back to PATH, then the usual home-dir drop.
    [string]$MesenPath = $env:PIPEDREAM_MESEN
)

$ErrorActionPreference = 'Stop'

if (-not $MesenPath) {
    $MesenPath = (Get-Command 'Mesen.exe' -ErrorAction SilentlyContinue).Source
    if (-not $MesenPath) { $MesenPath = Join-Path $HOME 'Mesen.exe' }
}
foreach ($p in @($MesenPath, $Rom, $Script)) {
    if (-not (Test-Path $p)) { throw "not found: $p (set PIPEDREAM_MESEN for the emulator)" }
}

# Mesen.exe is a GUI-subsystem binary: PowerShell does NOT wait for it with the call
# operator, which silently reports an empty exit code and leaves the run orphaned in the
# background. Start-Process -Wait is the only reliable form here.
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $MesenPath -Wait -PassThru -ArgumentList @(
    '/testrunner'
    (Resolve-Path $Rom).Path
    (Resolve-Path $Script).Path
    "/timeout=$TimeoutSec"
)

[pscustomobject]@{
    ExitCode  = $proc.ExitCode
    TimedOut  = $proc.ExitCode -eq -1
    Seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    Rom       = (Resolve-Path $Rom).Path
    Script    = (Resolve-Path $Script).Path
}
