<#
.SYNOPSIS
Rewrite src/rom/GfxSettingNames.cs from Lunar Magic's own names for the level header's two
graphics settings, read off its running "GFX Index in header" dialog.

.DESCRIPTION
That dialog's two combo boxes list the FG/BG GFX setting and the sprite GFX setting, one entry
per setting, as `0: FG1=14, FG2=17, BG=19, FG3=15 T:0 (Normal 1)` — the setting's files, and LM's
name for it in parentheses at the end. Read-LmHeaderCombos.ps1 brings the entries back; this
keeps the parenthesised name per setting number, "" where LM lists no entry (its FG/BG list
stops at E), and writes the two 16-entry C# arrays. Our dialog adds the number itself, so only
the name is kept. LM 3.40: the FG/BG combo is control 416, the sprite combo 417; they are told
apart here by their entries' `FG1=` / `SP1=` fields, not their ids.

.EXAMPLE
  tools\lm\Update-GfxSettingNames.ps1 -Rom copy.smc
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [string]$Out = (Join-Path $PSScriptRoot '..\..\src\rom\GfxSettingNames.cs'),
    [string]$LunarMagic = $env:PIPEDREAM_LUNAR_MAGIC
)
$ErrorActionPreference = 'Stop'
$combos = @(& (Join-Path $PSScriptRoot 'Read-LmHeaderCombos.ps1') -Rom $Rom -MenuItem '^GFX Index in header' -LunarMagic $LunarMagic)

function Names([string]$field) {
    $combo = $combos | Where-Object { $_.Items.Count -gt 0 -and $_.Items[0] -match "^0: $field=" } | Select-Object -First 1
    if (-not $combo) { throw "no combo whose entries start with '0: $field=' in the GFX Index dialog" }
    $names = @('') * 16
    foreach ($item in $combo.Items) {
        if ($item -notmatch '^([0-9A-F]): .*\(([^()]*)\)\s*$') { throw "entry not in the expected shape: '$item'" }
        $names[[Convert]::ToInt32($Matches[1], 16)] = $Matches[2].Trim()
    }
    , $names
}
$layer1 = Names 'FG1'
$sprites = Names 'SP1'

function Rows([string[]]$names) {
    (@(0, 8) | ForEach-Object { '        ' + (($names[$_..($_ + 7)] | ForEach-Object { '"' + ($_ -replace '"', '\"') + '",' }) -join ' ') }) -join "`n"
}
$text = @"
namespace PipeDream;

/// <summary>
/// Lunar Magic's names for the level header's two graphics settings, as its Change Properties
/// dialog lists them: the FG/BG GFX setting (the header's tileset, "layer 1") and the sprite GFX
/// setting, by setting number 0-F. Taken from LM's exe, never guessed; "" is a setting LM gives
/// no name, and the graphics-header dialog then shows the number and its GFX files alone.
///
/// Read off LM 3.40's Level ▸ GFX Index in header dialog by tools/lm/Update-GfxSettingNames.ps1,
/// which rewrites this file; the parenthesised name of each entry is what is kept.
/// </summary>
public static class GfxSettingNames
{
    public static readonly string[] Layer1 =
    [
$(Rows $layer1)
    ];

    public static readonly string[] Sprites =
    [
$(Rows $sprites)
    ];
}
"@
$text = $text -replace "`r?`n", "`n"
[IO.File]::WriteAllText((Resolve-Path $Out), $text, (New-Object Text.UTF8Encoding $false))
"wrote $Out"
"Layer1:  " + (($layer1 | ForEach-Object { if ($_) { $_ } else { '(none)' } }) -join ' | ')
"Sprites: " + (($sprites | ForEach-Object { if ($_) { $_ } else { '(none)' } }) -join ' | ')
