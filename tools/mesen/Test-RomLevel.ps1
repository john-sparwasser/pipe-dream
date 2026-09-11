<#
.SYNOPSIS
Headless in-game check on a CHOSEN level: walk the overworld to it, enter it, keep running.

.DESCRIPTION
What Test-RomBoots.ps1 could never do. That script pokes game mode $0B from the title screen,
which re-enters the TITLE DEMO's level — so the level was an observation, not an input, and a
structure only a particular level uses could not be reached at all.

This plays instead: Start is pulsed to the map (see Test-RomOverworld.ps1), then one step in
-Walk, then A. From where a new game starts on Yoshi's Island that selects the level:

    -Walk (none)   level $104, Yoshi's House
    -Walk left     level $105   <- this repo's canonical test level
    -Walk right    level $106

Only the immediate neighbours are reachable — the map stops at the next level tile and going
further needs that level beaten. So the way to test an arbitrary structure is to put it in
$105 and walk left, not to try to walk to some far tile. `--writedm16 <rom> 105 <out> <tile>
<row>` does exactly that for an extended Map16 tile.

THE ROW MATTERS. SMW looks a Map16 tile's definition up only when it DRAWS the tile, so
content parked in the level's empty sky (row 8, --writedm16's default) never exercises the
lookup. Row 13 is on the opening screen. Proven by mutation: with tile $1234 on row 13 and the
range dispatcher's extended path ($06F54A) replaced by STP, this script fails (exit 1); with
the same tile on row 8, or with no extended tile at all, the STP goes unnoticed.

.OUTPUTS
ExitCode 0 = pass; 1 = never reached the gameplay loop; 3 = frame counter frozen; -1 = timed
out. Level is the observed level number, and the caller should assert on it.

.EXAMPLE
  ./Test-RomLevel.ps1 -Rom build/with-ext-tile.smc -Walk left   # expect Level 0x105
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [ValidateSet('none', 'left', 'right', 'up', 'down')][string]$Walk = 'none',
    [int]$TimeoutSec = 90,
    # Frame by which the level is expected to be up; prelude.lua's M.LevelFrame.
    [int]$LevelFrame = 3400
)

$ErrorActionPreference = 'Stop'
$probe = Join-Path (Split-Path -Parent $PSCommandPath) 'New-MesenProbe.ps1'
$dir = if ($Walk -eq 'none') { 'nil' } else { "`"$Walk`"" }

$assert = @"
local tick = 0
T.each(function(f)
  T.bootToLevel(f, $dir)
  if f == $LevelFrame then
    if not T.inLevel() then T.fail(1) end
    tick = T.rb(0x7E0013)
  end
  if f == $LevelFrame + 60 then
    if (T.rb(0x7E0013) - tick) % 256 ~= 60 then T.fail(3) end
    T.pass()
  end
end)
"@

$result = & $probe -Rom $Rom -Body $assert -TimeoutSec $TimeoutSec

# The level number, in two runs because the channel is one byte.
function Observe([string]$addr) {
    $body = @"
T.each(function(f) T.bootToLevel(f, $dir) if f >= $LevelFrame then T.report(T.rb($addr)) end end)
"@
    (& $probe -Rom $Rom -Body $body -TimeoutSec $TimeoutSec).ExitCode
}

[pscustomobject]@{
    ExitCode = $result.ExitCode
    TimedOut = $result.TimedOut
    Seconds  = $result.Seconds
    Walk     = $Walk
    Level    = if ($result.ExitCode -eq 0) { '0x{0:X2}{1:X2}' -f (Observe '0x7E010C'), (Observe '0x7E010B') } else { $null }
    Rom      = $result.Rom
}
