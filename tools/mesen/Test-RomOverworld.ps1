<#
.SYNOPSIS
Headless in-game smoke for the OVERWORLD: does this ROM reach the map and keep running?

.DESCRIPTION
The companion to Test-RomBoots.ps1, and the one that covers everything a level never touches:
the overworld's own GFX load, its 4bpp tile reader in bank 04 (prep v13), the per-submap GFX
lists (v18/v19) and whatever the level loader last left in the decompression buffer, which
from v25 is the AN2 pass. A broken overworld shows up here as never reaching mode 0x0E, or as
a frozen frame counter on the map.

HOW IT GETS THERE: by playing, not by poking. Start is pulsed from boot, which clears the
title screen and the file select, starts a new game, sits through the intro level (mode 0x14
by frame ~600) and arrives on Yoshi's Island — mode 0x0E, submap 1 — by frame 2400. Measured
identically on vanilla and on a prep v28 base, so vanilla is a valid control.

This only works because emu.setInput is applied from the inputPolled event; see prelude.lua.
An earlier harness set it at frame start, saw nothing happen, and recorded in
reference/MESEN.md that input does not reach the game and the overworld is unreachable.

WHAT IT DOES NOT DO: choose the submap. It lands where a new game lands. Reaching the others
means walking the map, which is now possible in principle (input works) but unwritten.

.OUTPUTS
The runner result plus the observed submap. ExitCode 0 = pass; 1 = never reached the map;
3 = frame counter frozen on the map; -1 = timed out.

.EXAMPLE
  ./Test-RomOverworld.ps1 -Rom dev/ow-probe.smc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [int]$TimeoutSec = 90,
    # Frames of pulsing Start before the map is expected. The default is prelude.lua's
    # M.OverworldFrame with room to spare; raise it if a hack's intro runs longer.
    [int]$MapFrames = 2400,
    # Another ROM to compare the map's TILE GRAPHICS against, e.g. an LM-saved vanilla. Fills
    # VramMatchesReference. Two windows are deliberately excluded:
    #   $0800-$0FFF  the animated tiles, which cycle — comparing them at a fixed frame catches
    #                two ROMs at different animation phases and reports a difference that is
    #                not one (measured: vanilla's own checksum there changes between frame 2400
    #                and 2408, while $2000-$2FFF does not).
    #   $4000+ maps  the tilemaps, which are the hack's CONTENT: an edited overworld differs
    #                there for good reason. Measured on TestRom (an LM-saved vanilla whose map
    #                was edited): its tilemaps differ from both vanilla and a prepped base,
    #                while every graphics window matches. Comparing them made this switch
    #                report a mismatch for a reason that had nothing to do with graphics.
    [string]$Reference
)

$ErrorActionPreference = 'Stop'
$probe = Join-Path (Split-Path -Parent $PSCommandPath) 'New-MesenProbe.ps1'

$assert = @"
local tick = 0
T.each(function(f)
  T.bootPulse(f)
  if f == $MapFrames then
    if not T.onOverworld() then T.fail(1) end
    tick = T.rb(0x7E0013)
  end
  if f == $MapFrames + 60 then
    -- A hung game keeps its mode but stops ticking, which a mode check alone would miss.
    if (T.rb(0x7E0013) - tick) % 256 ~= 60 then T.fail(3) end
    T.pass()
  end
end)
"@

$result = & $probe -Rom $Rom -Body $assert -TimeoutSec $TimeoutSec

# Which submap it landed on — an observation, not an assertion: it is wherever a new game
# starts, which is Yoshi's Island (1) on an unmodified overworld.
$observe = @"
T.each(function(f) T.bootPulse(f) if f >= $MapFrames then T.report(T.rb(0x7E1F11)) end end)
"@

$out = [ordered]@{
    ExitCode = $result.ExitCode
    TimedOut = $result.TimedOut
    Seconds  = $result.Seconds
    Submap   = (& $probe -Rom $Rom -Body $observe -TimeoutSec $TimeoutSec).ExitCode
    Rom      = $result.Rom
}

if ($Reference) {
    # Two mods, because the channel is one byte: a single checksum collides too easily to
    # call two ROMs identical on.
    $ranges = @(@(0x0000, 0x07FF), @(0x1000, 0x3FFF), @(0x8000, 0x9FFF))
    $same = $true
    foreach ($r in $ranges) {
        foreach ($m in 251, 239) {
            $body = @"
T.each(function(f)
  T.bootPulse(f)
  if f >= $MapFrames then
    local s = 0
    for a = $($r[0]), $($r[1]) do s = s + T.vram(a) end
    T.report(s % $m)
  end
end)
"@
            $a = (& $probe -Rom $Rom -Body $body -TimeoutSec $TimeoutSec).ExitCode
            $b = (& $probe -Rom $Reference -Body $body -TimeoutSec $TimeoutSec).ExitCode
            if ($a -ne $b) { $same = $false }
        }
    }
    $out.VramMatchesReference = $same
    $out.Reference = (Resolve-Path $Reference).Path
}

[pscustomobject]$out
