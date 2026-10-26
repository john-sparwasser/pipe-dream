<#
.SYNOPSIS
The SA-1 driver checks (reference/SA1_PORT.md, phase 3): every driver's ROM pair, LoROM and SA-1,
read back in game.

.DESCRIPTION
Builds the pairs with Sa1DriversSetup (src/tests/Sa1DriversSetup.cs: one project per driver, each
leaving marks in RAM on level $105), then runs every check below on both ROMs of its pair and
prints a table. A check passes when both ROMs give what it wants; one that wants nothing in
particular passes when the two agree. Exit code 1 when any fails.

A check's Lua runs with T = prelude.lua; on the SA-1 ROM T.sa1() comes first, so T.ram (SMW's
RAM by its vanilla address) and T.mark (the projects' own marks) read the right place on both.
Counters are checked by how much they grew between two frames: RAM is not cleared for us.

.EXAMPLE
  ./Test-Sa1Drivers.ps1                 # build, then every check
  ./Test-Sa1Drivers.ps1 -NoBuild -Only uber
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path ([IO.Path]::GetTempPath()) 'pipedream-sa1-drivers'),
    [switch]$NoBuild,
    # Only checks whose name contains this.
    [string]$Only = '',
    [int]$Throttle = 8,
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$repo = Resolve-Path (Join-Path $here '..\..')

if (-not $NoBuild) {
    $env:PIPEDREAM_SA1_DRIVERS = $OutDir
    $build = & dotnet test (Join-Path $repo 'src\tests') --filter 'FullyQualifiedName~Sa1DriversSetup' 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 30; throw 'building the ROM pairs failed' }
}

# ---- the checks ----
# Value: a byte at frame n of level $105. Delta: a counter's growth from frame A to B. Lua: a whole body.
function Value([string]$Expr, [int]$At = 60) { "T.inLevel105(function(n) if n == $At then T.report($Expr) end end)" }
function Delta([string]$Expr, [int]$From = 60, [int]$To = 120) {
    "local a T.inLevel105(function(n) if n == $From then a = $Expr end if n == $To then T.report(($Expr - a) % 256) end end)"
}
# A body that writes RAM at frame 120 of the level (the game's own requests), then reads at -At.
function After([string]$Write, [string]$Expr, [int]$At) {
    "T.inLevel105(function(n) if n == 120 then $Write end if n == $At then T.report($Expr) end end)"
}
# The loudest envelope (ENVX) of the given voices from frame A to B: 0 when none of them sounds.
# No buttons: the walk's A pulses are jumps, and a jump plays an effect.
function Loudest([string]$Voices, [int]$From, [int]$To, [string]$Write = '') {
    "local m = 0 T.inLevel105(function(n) T.hold({}) if n == 120 then $Write end if n >= $From then for _, v in ipairs({$Voices}) do m = math.max(m, emu.read(v * 16 + 8, emu.memType.spcDspRegisters, false)) end end if n == $To then T.report(m) end end)"
}
# A byte on the map, 60 frames after a new game reaches it (Yoshi's Island); 255 when it never does.
function OnMap([string]$Expr) {
    "T.each(function(f) T.bootPulse(f) if f == 2460 then if not T.onOverworld() then T.fail(255) end T.report($Expr) end end)"
}
$tilemaps = '(function() local s = 0 for a = 0x4000, 0x7FFF do s = s + T.vram(a) end return s end)()'
$positive = { param($v) $v -gt 0 }
$frames = { param($v) $v -ge 50 }             # a counter that ran nearly every frame of 60

$checks = @(
    # blocks: a column of a block that sets lives to 99; Mario walks right into it
    @{ Name = 'blocks: lives set by the block';  Rom = 'blocks'; Want = { param($v) $v -eq 99 }; Says = '99'
       Lua = "T.inLevel105(function(n) T.hold({ right = true }) if n == 300 then T.report(T.rb(T.ram(0x0DBE))) end end)" }
    @{ Name = 'blocks: Mario stopped at the column'; Rom = 'blocks'; Want = { param($v) $v -eq 8 }; Says = 'X/4 = 8'
       Lua = "T.inLevel105(function(n) T.hold({ right = true }) if n == 300 then T.report(T.rw(T.ram(0x94)) // 4) end end)" }

    # music: the level's song ($0B) uploaded and echoed; a change, the star and P-switch songs, an effect
    @{ Name = 'music: the level song loaded';     Rom = 'music'; Want = { param($v) $v -eq 0x0B }; Says = '$0B'; Lua = Value 'T.rb(T.ram(0x1DFD))' 120 }
    @{ Name = 'music: the engine echoed it';      Rom = 'music'; Want = { param($v) $v -eq 0 }; Says = '0'; Lua = Value 'T.rb(T.ram(0x1DFE))' 120 }
    @{ Name = 'music: the song sounds (voice 0)'; Rom = 'music'; Want = $positive; Says = '> 0'; Lua = Loudest '0' 10 70 }
    @{ Name = 'music: a song change uploads';     Rom = 'music'; Want = { param($v) $v -eq 0x0A }; Says = '$0A'
       Lua = After 'T.wb(T.ram(0x1DFB), 0x0A)' 'T.rb(T.ram(0x1DFD))' 420 }
    @{ Name = 'music: the star song, no upload';  Rom = 'music'; Want = { param($v) $v -eq 0x0B }; Says = '$0B kept'
       Lua = After 'T.wb(T.ram(0x1DFB), 0x05)' 'T.rb(T.ram(0x1DFD))' 300 }
    @{ Name = 'music: the star song echoed';      Rom = 'music'; Want = { param($v) $v -eq 0 }; Says = '0'
       Lua = After 'T.wb(T.ram(0x1DFB), 0x05)' 'T.rb(T.ram(0x1DFE))' 300 }
    @{ Name = 'music: the P-switch song echoed';  Rom = 'music'; Want = { param($v) $v -eq 0 }; Says = '0'
       Lua = After 'T.wb(T.ram(0x1DFB), 0x06)' 'T.rb(T.ram(0x1DFE))' 300 }
    @{ Name = 'music: effect voices quiet before'; Rom = 'music'; Want = { param($v) $v -eq 0 }; Says = '0'; Lua = Loudest '6, 7' 60 119 }
    @{ Name = 'music: an effect sounds ($1DFC 01)'; Rom = 'music'; Want = $positive; Says = '> 0'
       Lua = Loudest '6, 7' 121 180 'T.wb(T.ram(0x1DFC), 0x01)' }

    # UberASM: every code point
    @{ Name = 'uber: level load:';   Rom = 'uber'; Want = { param($v) $v -eq 0x44 }; Says = '$44'; Lua = Value 'T.rb(T.mark(4))' }
    @{ Name = 'uber: level init:';   Rom = 'uber'; Want = { param($v) $v -eq 0x11 }; Says = '$11'; Lua = Value 'T.rb(T.ram(0x0F5E))' }
    @{ Name = 'uber: level main:';   Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F5F))' }
    @{ Name = 'uber: level end:';    Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(3))' }
    @{ Name = 'uber: level nmi:';    Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F60))' }
    @{ Name = 'uber: game mode 14';  Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F61))' }
    @{ Name = 'uber: status bar';    Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F63))' }
    @{ Name = 'uber: invoke_sa1';    Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F64))' }
    @{ Name = 'uber: global init:';  Rom = 'uber'; Want = { param($v) $v -eq 0x33 }; Says = '$33'; Lua = Value 'T.rb(T.mark(0))' }
    @{ Name = 'uber: global main:';  Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(1))' }
    @{ Name = 'uber: global nmi:';   Rom = 'uber'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(2))' }
    @{ Name = 'uber: map main:';     Rom = 'uber'; Want = $frames; Says = '~60'
       Lua = "local a T.each(function(f) T.bootToLevel(f, 'left') if f == 2300 then a = T.rb(T.ram(0x0F62)) end if f == 2360 then T.report((T.rb(T.ram(0x0F62)) - a) % 256) end end)" }
    @{ Name = 'uber: paused';        Rom = 'uber'; Want = $positive; Says = 'paused'
       Lua = "T.inLevel105(function(n) T.hold((n >= 60 and n < 64) and { start = true } or {}) if n == 100 then T.report(T.rb(T.ram(0x13D4))) end end)" }
    @{ Name = 'uber: level main: while paused'; Rom = 'uber'; Want = $frames; Says = '~60'
       Lua = "local a T.inLevel105(function(n) T.hold((n >= 60 and n < 64) and { start = true } or {}) if n == 100 then a = T.rb(T.ram(0x0F5F)) end if n == 160 then T.report((T.rb(T.ram(0x0F5F)) - a) % 256) end end)" }

    # sprites: a probe in 1A with extra bytes AB CD, calling routines
    @{ Name = 'sprites: INIT';             Rom = 'sprites'; Want = { param($v) $v -eq 0x11 }; Says = '$11'; Lua = Value 'T.rb(T.ram(0x0F5E))' }
    @{ Name = 'sprites: its extra byte';   Rom = 'sprites'; Want = { param($v) $v -eq 0xAB }; Says = '$AB'; Lua = Value 'T.rb(T.ram(0x0F60))' }
    @{ Name = 'sprites: MAIN';             Rom = 'sprites'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.ram(0x0F5F))' }
    @{ Name = 'sprites: SubHorzPos';       Rom = 'sprites'; Want = $null; Says = 'both alike'; Lua = Value 'T.rb(T.ram(0x0F61))' }
    @{ Name = 'sprites: Random up to $10'; Rom = 'sprites'; Want = { param($v) $v -le 0x10 }; Says = '<= $10'; Lua = Value 'T.rb(T.ram(0x0F62))' }
    @{ Name = 'sprites: Aiming X';         Rom = 'sprites'; Want = $null; Says = 'both alike'; Lua = Value 'T.rb(T.ram(0x0F63))' }
    @{ Name = 'sprites: Aiming Y';         Rom = 'sprites'; Want = $null; Says = 'both alike'; Lua = Value 'T.rb(T.ram(0x0F64))' }

    # shooters, generators, more than four extra bytes
    @{ Name = 'kinds: shooter MAIN';       Rom = 'kinds'; Want = $positive; Says = '> 0'; Lua = Delta 'T.rb(T.ram(0x0F65))' 120 180 }
    @{ Name = 'kinds: generator MAIN';     Rom = 'kinds'; Want = $positive; Says = '> 0'; Lua = Delta 'T.rb(T.ram(0x0F66))' 120 180 }
    @{ Name = 'kinds: 5th of 6 extra bytes'; Rom = 'kinds'; Want = { param($v) $v -eq 0x55 }; Says = '$55'; Lua = Value 'T.rb(T.ram(0x0F67))' 120 }

    # the listed kinds, spawned by a sprite; per-level sprites; a status entry point; the cape
    @{ Name = 'listed: cluster MAIN';        Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x10))' }
    @{ Name = 'listed: extended MAIN';       Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x11))' }
    @{ Name = 'listed: minor extended MAIN'; Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x12))' }
    @{ Name = 'listed: smoke MAIN';          Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x13))' }
    @{ Name = 'listed: spinning coin MAIN';  Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x14))' }
    @{ Name = 'listed: score MAIN';          Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x15))' }
    @{ Name = 'listed: bounce MAIN';         Rom = 'listed'; Want = $frames; Says = '~60'; Lua = Delta 'T.rb(T.mark(0x16))' }
    @{ Name = 'listed: the level''s own B0'; Rom = 'listed'; Want = { param($v) $v -eq 0x44 }; Says = '$44'; Lua = Value 'T.rb(T.mark(0x18))' }
    @{ Name = 'listed: CARRIABLE';           Rom = 'listed'; Want = $positive; Says = '> 0'; Lua = Delta 'T.rb(T.mark(0x19))' }
    @{ Name = 'listed: extended CAPE';       Rom = 'listed'; Want = $positive; Says = '> 0'
       Lua = "local a T.inLevel105(function(n) T.wb(T.ram(0x19), 2) T.hold((n % 16 < 2) and { y = true } or {}) if n == 100 then a = T.rb(T.mark(0x17)) end if n == 300 then T.report((T.rb(T.mark(0x17)) - a) % 256) end end)" }

    # the overworld: layer 2 all sea, a layer 1 tile and colour $22 edited (the plain build's tilemaps
    # hash 143/140 here, colour $22 is 0: measured on blocks-*, 2026-10-25)
    @{ Name = 'overworld: colour $22 low';   Rom = 'overworld'; Want = { param($v) $v -eq 0x34 }; Says = '$34'; Lua = OnMap 'emu.read(0x44, emu.memType.snesCgRam, false)' }
    @{ Name = 'overworld: colour $22 high';  Rom = 'overworld'; Want = { param($v) $v -eq 0x12 }; Says = '$12'; Lua = OnMap 'emu.read(0x45, emu.memType.snesCgRam, false)' }
    @{ Name = 'overworld: tilemaps (mod 251)'; Rom = 'overworld'; Want = { param($v) $v -ne 143 -and $v -ne 255 }; Says = 'not plain''s'; Lua = OnMap "$tilemaps % 251" }
    @{ Name = 'overworld: tilemaps (mod 239)'; Rom = 'overworld'; Want = { param($v) $v -ne 140 -and $v -ne 255 }; Says = 'not plain''s'; Lua = OnMap "$tilemaps % 239" }
) | Where-Object { $_.Name -like "*$Only*" }

# ---- run both ROMs of every check ----
$probe = Join-Path $here 'New-MesenProbe.ps1'
$jobs = foreach ($c in $checks) {
    foreach ($kind in 'lorom', 'sa1') {
        $rom = Join-Path $OutDir "$($c.Rom)-$kind.smc"
        $body = $(if ($kind -eq 'sa1') { 'T.sa1() ' } else { '' }) + $c.Lua
        Start-ThreadJob -ThrottleLimit $Throttle -ArgumentList $probe, $rom, $body, $TimeoutSec, $c.Name, $kind -ScriptBlock {
            param($probe, $rom, $body, $timeout, $name, $kind)
            $r = & $probe -Rom $rom -Body $body -TimeoutSec $timeout
            [pscustomobject]@{ Name = $name; Kind = $kind; Value = $r.ExitCode }
        }
    }
}
$results = $jobs | Wait-Job | Receive-Job
$jobs | Remove-Job

$failed = 0
$table = foreach ($c in $checks) {
    $lo = ($results | Where-Object { $_.Name -eq $c.Name -and $_.Kind -eq 'lorom' }).Value
    $sa = ($results | Where-Object { $_.Name -eq $c.Name -and $_.Kind -eq 'sa1' }).Value
    $ok = if ($c.Want) { (& $c.Want $lo) -and (& $c.Want $sa) -and $lo -ge 0 -and $sa -ge 0 } else { $lo -eq $sa -and $lo -ge 0 }
    if (-not $ok) { $failed++ }
    [pscustomobject]@{ Check = $c.Name; LoROM = $lo; 'SA-1' = $sa; Want = $c.Says; OK = $(if ($ok) { 'ok' } else { 'FAIL' }) }
}
$table | Format-Table -AutoSize | Out-String -Width 200
"{0} of {1} passed  (253 = never reached the level; -1 = timed out)" -f ($checks.Count - $failed), $checks.Count
if ($failed) { exit 1 }
