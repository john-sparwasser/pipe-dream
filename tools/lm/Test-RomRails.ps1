<#
.SYNOPSIS
Did a Lunar Magic write route tread on anything prep owns? Diffs two ROMs and flags every
changed run that overlaps one of our structures.

.DESCRIPTION
The question every LM save route has to answer, and the one that produced prep v21, v22, v23,
v26 and v29: LM writes to the ROM for its own reasons, and if a write lands on a structure we
stamped, the ROM breaks or our reader goes blind. This turns "run it and squint at the diff"
into a check.

Overlapping is NOT automatically a failure — `-ImportGFX` legitimately takes over the GFX
pointer tables and the boot-blob operands, and our readers follow it because they read through
those operands. What it means is "look here", so a flagged route needs the in-game check
(tools/mesen/Test-RomBoots.ps1, Test-RomOverworld.ps1) before it is called safe.

.EXAMPLE
  copy base.smc after.smc
  tools\lm\Invoke-LunarMagic.ps1 -Rom after.smc -LmArgs @('-ImportGFX','after.smc')
  tools\lm\Test-RomRails.ps1 -Before base.smc -After after.smc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [string]$PipeDream = "$PSScriptRoot\..\..\src\bin\Debug\net10.0\PipeDream.exe"
)

$ErrorActionPreference = 'Stop'

# What prep owns comes FROM THE PREP (`--stampranges`), not from a list kept here: a hand-kept
# one silently missed v10's plane-pointer patches the first time this was run.
$ours = [Collections.Generic.List[object]]::new()
& $PipeDream --stampranges 2>$null | ForEach-Object {
    $f = $_.Trim() -split '\s+'
    if ($f.Count -ge 2) { $ours.Add([pscustomobject]@{ Lo = [Convert]::ToInt32($f[0], 16); Len = [int]$f[1] }) }
}
if ($ours.Count -eq 0) { throw "no stamp ranges from $PipeDream — build it first" }

# ...plus what Apply writes as DATA rather than as a stamp, which --stampranges cannot report:
# ConvertGfxTo4bpp and BakeLmFourBppFiles (v6/v25) repoint the three GFX pointer tables and the
# boot blobs' operands, and MigrateSecondaryDestinationBit (v10) sets a bit per record. Short,
# stable, and the reason this list is here at all: without it the audit called -ImportGFX clear
# when it had in fact taken the blob operands over.
foreach ($d in @(
    @(0x00B992, 0x32),   # Gfx.PtrLow  — 0x32 vanilla file pointers
    @(0x00B9C4, 0x32),   # Gfx.PtrHigh
    @(0x00B9F6, 0x32),   # Gfx.PtrBank
    @(0x00B88B, 2),      # GFX33 address (v25)
    @(0x00B890, 1),      # the blobs' shared bank byte (v25)
    @(0x00B8D8, 2),      # GFX32 address (v25)
    @(0x05FE00, 0x200)   # secondary-entrance destination bits (v10)
)) { $ours.Add([pscustomobject]@{ Lo = $d[0]; Len = $d[1] }) }

# PIPE, do not assign: PipeDream.exe is a GUI-subsystem binary, and PowerShell captures its
# stdout only through a pipeline — `$x = & $exe …` comes back EMPTY and the audit then passes
# everything. Same trap tools/mesen/Invoke-MesenTest.ps1 documents for Mesen.
# Changed runs say "len"; the tail of the diff lists NEW RATS BLOCKS with "size", which are
# allocations rather than overwrites and so are counted, not flagged.
$lines = [Collections.Generic.List[string]]::new()
$blocks = [Collections.Generic.List[string]]::new()
& $PipeDream --diff $Before $After 2>$null | ForEach-Object {
    if ($_ -match '^\s+SNES \$[0-9A-F]+\s+PC\s+[0-9A-F]+\s+len\s') { $lines.Add($_) }
    elseif ($_ -match '^\s+SNES \$[0-9A-F]+\s+PC\s+[0-9A-F]+\s+size\s') { $blocks.Add($_) }
}
$hits = 0
foreach ($line in $lines) {
    if ($line -notmatch '^\s+SNES \$([0-9A-F]+)\s+PC\s+[0-9A-F]+\s+len\s+(\d+)') {
        "  ?? could not parse: $line"; continue
    }
    $start = [Convert]::ToInt32($Matches[1], 16)
    $len = [int]$Matches[2]
    foreach ($r in $ours) {
        if ($start -lt ($r.Lo + $r.Len) -and ($start + $len) -gt $r.Lo) {
            '  !! ${0:X6} len {1} overlaps a stamp at ${2:X6} len {3}' -f $start, $len, $r.Lo, $r.Len
            $hits++
            break
        }
    }
}
[pscustomobject]@{
    Runs      = $lines.Count
    NewBlocks = $blocks.Count
    Stamps    = $ours.Count
    Overlaps  = $hits
    Verdict   = if ($hits -eq 0) { 'clear' } else { 'LOOK — then check it in game' }
}
