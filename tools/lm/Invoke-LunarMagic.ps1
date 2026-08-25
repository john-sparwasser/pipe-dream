<#
.SYNOPSIS
Run one Lunar Magic command-line operation against a ROM, without letting it hang the caller.

.DESCRIPTION
Lunar Magic is a GUI program with a command-line front end (see reference/LUNAR_MAGIC.md).
Two consequences shape this wrapper:

  * It reports most warnings and errors through MESSAGE BOXES, with no documented way to
    suppress them. A dialog means the process never exits, so every call needs a timeout and
    a kill. A TimedOut result is a FINDING — it means LM wanted to tell you something — not
    an infrastructure flake.
  * It is not a console program, so a caller does not wait on it by default and its output
    only appears if you redirect it to a file (UTF-8).

Returns an object rather than printing, so callers can assert on it:
    ExitCode, TimedOut, Output, AccessDenied, ChecksumWarned, Changed

.PARAMETER Rom
The ROM to operate on. LM writes IN PLACE — pass a copy unless you mean it.

.EXAMPLE
$r = tools\lm\Invoke-LunarMagic.ps1 -Rom work.smc -LmArgs @('-ExportAllMap16','work.smc','out.map16')
if ($r.AccessDenied) { throw 'LM thinks this hack is access-restricted ($0DF100 is not $FF)' }
#>
param(
    [Parameter(Mandatory)] [string]   $Rom,
    [Parameter(Mandatory)] [string[]] $LmArgs,
    [int]    $TimeoutSeconds = 45,
    [string] $LunarMagic = $env:PIPEDREAM_LUNAR_MAGIC
)

if (-not $LunarMagic) { $LunarMagic = 'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe' }
if (-not (Test-Path $LunarMagic)) { throw "Lunar Magic not found at '$LunarMagic' (set PIPEDREAM_LUNAR_MAGIC)" }
if (-not (Test-Path $Rom))        { throw "ROM not found: $Rom" }

$out    = [IO.Path]::GetTempFileName()
$err    = [IO.Path]::GetTempFileName()
$before = (Get-FileHash $Rom -Algorithm SHA256).Hash

try {
    # WindowStyle Hidden keeps a stray dialog off the user's screen; it does NOT stop one
    # from blocking, which is what the timeout is for.
    $p = Start-Process $LunarMagic -ArgumentList $LmArgs -PassThru -WindowStyle Hidden `
                       -RedirectStandardOutput $out -RedirectStandardError $err
    $exited = $p.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) { $p.Kill(); $p.WaitForExit(5000) }

    $text = (Get-Content $out -Raw -ErrorAction SilentlyContinue) +
            (Get-Content $err -Raw -ErrorAction SilentlyContinue)

    # The hash can only be read once the handle is gone, which a killed process may not have
    # released yet — so a Changed of $null means "unknown", never "unchanged".
    $after = $null
    try { $after = (Get-FileHash $Rom -Algorithm SHA256).Hash } catch { }

    [pscustomobject]@{
        ExitCode       = if ($exited) { $p.ExitCode } else { $null }
        TimedOut       = -not $exited
        Output         = $text
        AccessDenied   = [bool]($text -match 'Access Denied')
        ChecksumWarned = [bool]($text -match 'checksum')
        Changed        = if ($null -eq $after) { $null } else { $before -ne $after }
    }
}
finally { Remove-Item $out, $err -ErrorAction SilentlyContinue }
