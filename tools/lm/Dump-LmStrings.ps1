<#
.SYNOPSIS
Pull the human-readable strings out of Lunar Magic.exe, for mining its tile and behaviour text.

.DESCRIPTION
Lunar Magic ships no data files: the words its Map16 editor shows for a tile's "Act as" setting
live inside the executable, as ASCII and as UTF-16. This walks the binary for both encodings and
writes every run of printable text (MinLength+ characters) to a UTF-8 file, one per line, in file
order — neighbouring strings in the file are neighbouring in the table they came from, which is
how you tell a behaviour list from a menu.

Run it on the machine that has LM (see reference/LUNAR_MAGIC.md for where that is), then bring
the output back and grep it:
    Select-String -Path lm-strings.txt -Pattern 'Cement|Note block|Muncher|Vine' -Context 20,40

.EXAMPLE
tools\lm\Dump-LmStrings.ps1 -Exe 'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe' -Out lm-strings.txt
#>
param(
    [string]$Exe = $(if ($env:PIPEDREAM_LUNAR_MAGIC) { $env:PIPEDREAM_LUNAR_MAGIC }
                    else { 'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe' }),
    [string]$Out = 'lm-strings.txt',
    [int]$MinLength = 4
)
$bytes = [IO.File]::ReadAllBytes($Exe)
$lines = New-Object System.Collections.Generic.List[string]
function Printable([int]$b) { $b -ge 0x20 -and $b -le 0x7E }

# ASCII runs.
$start = -1
for ($i = 0; $i -le $bytes.Length; $i++) {
    $ok = $i -lt $bytes.Length -and (Printable $bytes[$i])
    if ($ok -and $start -lt 0) { $start = $i }
    elseif (-not $ok -and $start -ge 0) {
        if ($i - $start -ge $MinLength) { $lines.Add("A@{0:X7} {1}" -f $start, [Text.Encoding]::ASCII.GetString($bytes, $start, $i - $start)) }
        $start = -1
    }
}
# UTF-16LE runs: printable low byte, zero high byte.
$start = -1
for ($i = 0; $i -le $bytes.Length - 1; $i += 2) {
    $ok = $i -lt $bytes.Length - 1 -and (Printable $bytes[$i]) -and $bytes[$i + 1] -eq 0
    if ($ok -and $start -lt 0) { $start = $i }
    elseif (-not $ok -and $start -ge 0) {
        if (($i - $start) / 2 -ge $MinLength) { $lines.Add("U@{0:X7} {1}" -f $start, [Text.Encoding]::Unicode.GetString($bytes, $start, $i - $start)) }
        $start = -1
    }
}
[IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding $false))
"{0} strings -> {1}" -f $lines.Count, $Out
