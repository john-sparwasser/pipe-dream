<#
.SYNOPSIS
Headless in-game smoke: does this ROM reach SMW's gameplay loop and keep running?

.DESCRIPTION
The cheapest real check on an edited/prepped ROM. Getting into a level exercises the object
engine, the Map16 def lookup ($00C17A, called constantly while building the level), the GFX
loader, the palette hook and the sprite hooks — so "a level builds and the game keeps
ticking" covers every structure RomPrep inserts. A broken hijack shows up as a hang, a crash
out of the gameplay mode, or a frozen frame counter.

WHAT THIS DOES NOT DO: choose the level. See "Selecting a level" in reference/MESEN.md —
poking $7E:010B or $7E:13BF does NOT select one (measured: every value still yields the same
level and a byte-identical Map16 map). Forcing game mode $0B from the title screen re-enters
the TITLE DEMO's level, which is what this runs. So the level is an OBSERVATION here, not an
input, and this script deliberately makes no assertion about which level loaded — an earlier
version did, and passed green by holding $010B in RAM while the game ran something else.

.OUTPUTS
The runner result plus the observed level. ExitCode 0 = pass; 1 = never reached the gameplay
loop; 3 = frame counter frozen (hung); -1 = timed out.

.EXAMPLE
  ./Test-RomBoots.ps1 -Rom base.smc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [int]$TimeoutSec = 60,
    # Frames to let the ROM settle before forcing the level load, and to let it build.
    [int]$BootFrames = 600,
    [int]$LoadFrames = 500
)

$ErrorActionPreference = 'Stop'
$check = $BootFrames + $LoadFrames
$probe = Join-Path (Split-Path -Parent $PSCommandPath) 'New-MesenProbe.ps1'

$drive = @"
local function drive(f)
  if f == $BootFrames then emu.write(0x0100, 0x0B, emu.memType.snesWorkRam) end   -- enter a level
end
"@

$assert = @"
$drive
local tick = 0
T.each(function(f)
  drive(f)
  if f == $check then
    if T.rb(0x7E0100) ~= 0x14 then T.fail(1) end   -- 0x14 is the gameplay loop
    tick = T.rb(0x7E0013)
  end
  if f == $check + 60 then
    -- The frame counter must have advanced by the 60 frames that elapsed. A hung game keeps
    -- its game mode but stops ticking, which a mode check alone would not catch.
    if (T.rb(0x7E0013) - tick) % 256 ~= 60 then T.fail(3) end
    T.pass()
  end
end)
"@

$result = & $probe -Rom $Rom -Body $assert -TimeoutSec $TimeoutSec

# Which level actually ran — an observation, and it varies with boot timing.
$observe = @"
$drive
T.each(function(f) drive(f) if f >= $check then T.report(T.rb(0x7E010B)) end end)
"@

[pscustomobject]@{
    ExitCode   = $result.ExitCode
    TimedOut   = $result.TimedOut
    Seconds    = $result.Seconds
    Level      = '0x{0:X2}' -f (& $probe -Rom $Rom -Body $observe -TimeoutSec $TimeoutSec).ExitCode
    Rom        = $result.Rom
}
