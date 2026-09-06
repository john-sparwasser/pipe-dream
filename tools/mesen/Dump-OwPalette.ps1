<#
.SYNOPSIS
Dump the overworld's real CGRAM out of a headless Mesen run, one byte per run.

.DESCRIPTION
Ground truth for the Overworld canvas's colours. The editor builds the overworld palette from
the ROM's tables (Palette.LoadOverworld); this reads what the console actually holds once the
game is standing on the world map, so the two can be diffed row by row.

A headless run answers with one exit-code byte (see reference/MESEN.md), so a 128-colour dump
is 256 runs of ~5 s — about 20 minutes. Rows can be narrowed with -Rows.

The game is put on the map by poking the game mode at frame 1200 (the title screen has
settled by then) and sampling at frame 2400. SMW's overworld runs as mode $0E; $0C is "load
the overworld". If the first sanity run reports a mode other than $0E, try -Mode 0x0D.

.EXAMPLE
  ./Dump-OwPalette.ps1 -Rom C:\smw\base.smc -Out ow-cgram.txt
  # then, on the Mac:  dotnet run -- --owcell <rom> 6 39   and compare rows 4-7 (Yoshi's Island)

.EXAMPLE
  ./Dump-OwPalette.ps1 -Rom base.smc -Rows 4,5 -Source mirror
  # only rows 4 and 5, read from the game's CGRAM mirror at $7E:0703 instead of CGRAM itself
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [string]$Out = 'ow-cgram.txt',
    [int]$Mode = 0x0C,
    [int[]]$Rows = 0..7,
    [ValidateSet('cgram', 'mirror')][string]$Source = 'cgram',
    [int]$SampleFrame = 2400
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$probe = Join-Path $here 'New-MesenProbe.ps1'

function Body([string]$reportExpr) {
@"
T.each(function(f)
    if f == 1200 then emu.write(0x7E0100, $Mode, emu.memType.snesMemory) end
    if f == $SampleFrame then $reportExpr end
end)
"@
}

# Sanity first: where did the game settle, and is it ticking?
$r = & $probe -Rom $Rom -Body (Body 'T.report(T.rb(0x7E0100))') -TimeoutSec 60
if ($r.TimedOut) { throw "Mesen never reached frame $SampleFrame (timeout). Is another Mesen instance running?" }
$mode = $r.ExitCode
Write-Host ("game mode at frame {0}: ${1:X2}" -f $SampleFrame, $mode)
if ($mode -ne 0x0E) {
    throw ("expected the overworld (mode `$0E) but the game is in mode `${0:X2}. Try -Mode 0x0D, or a later -SampleFrame." -f $mode)
}
$sub = (& $probe -Rom $Rom -Body (Body 'T.report(T.rb(0x7E1F11))') -TimeoutSec 60).ExitCode
Write-Host ("submap `$1F11 = {0}  (0 main, 1 Yoshi's Island, 2 Vanilla Dome, 3 Forest, 4 Valley, 5 Special, 6 Star)" -f $sub)

$lines = @("# overworld CGRAM, mode ${Mode:X2} sampled at frame $SampleFrame, submap $sub, source $Source", "# row: 16 BGR555 words")
foreach ($row in $Rows) {
    $words = @()
    for ($c = 0; $c -lt 16; $c++) {
        $idx = $row * 16 + $c
        $bytes = @()
        foreach ($half in 0, 1) {
            $expr = if ($Source -eq 'cgram') { "T.report(emu.read($($idx * 2 + $half), emu.memType.snesCgRam, false))" }
                    else { "T.report(T.rb($(0x7E0703 + $idx * 2 + $half)))" }
            $res = & $probe -Rom $Rom -Body (Body $expr) -TimeoutSec 60
            if ($res.TimedOut) { throw "timeout reading colour $idx" }
            $bytes += $res.ExitCode
        }
        $words += ('{0:X4}' -f ($bytes[0] + $bytes[1] * 256))
    }
    $line = ('row {0}: {1}' -f $row, ($words -join ' '))
    Write-Host $line
    $lines += $line
}
$lines | Set-Content -Path $Out -Encoding UTF8
Write-Host "wrote $Out"
