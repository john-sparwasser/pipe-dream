<#
.SYNOPSIS
Build a runnable Lua probe from prelude.lua + a body, and (by default) run it.

.DESCRIPTION
Because a headless run can only answer with one exit-code byte, probes are GENERATED rather
than parameterised: ask a different question by pasting a different body. That is cheap
(~2s per run), so sweeping a value across frames is a loop in PowerShell, not a cleverer
Lua script.

The body is Lua with `T` bound to the prelude table (T.rb, T.rw, T.each, T.pass, T.fail,
T.report, T.hold, T.bootPulse).

.EXAMPLE
  # What is the game mode at frame 900, with Start being pulsed?
  ./New-MesenProbe.ps1 -Rom base.smc -Body 'T.each(function(f) T.bootPulse(f)
      if f >= 900 then T.report(T.rb(0x7E0100)) end end)'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [Parameter(Mandatory)][string]$Body,
    [int]$TimeoutSec = 60,
    [string]$OutDir = $env:TEMP,
    [switch]$KeepScript
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$path = Join-Path $OutDir ("mesenprobe-" + [guid]::NewGuid().ToString('N').Substring(0, 8) + ".lua")

# The prelude `return M`s, so load it as a chunk and bind the result to T.
$prelude = Get-Content (Join-Path $here 'prelude.lua') -Raw
@"
local T = (function()
$prelude
end)()
$Body
"@ | Set-Content -Path $path -Encoding UTF8

try {
    & (Join-Path $here 'Invoke-MesenTest.ps1') -Rom $Rom -Script $path -TimeoutSec $TimeoutSec
}
finally {
    if (-not $KeepScript) { Remove-Item $path -ErrorAction SilentlyContinue }
}
