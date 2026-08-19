<#
.SYNOPSIS
  Publish Pipe Dream and install it for the current user.

.DESCRIPTION
  Publishes a self-contained build, copies it to %LOCALAPPDATA%\Programs\PipeDream,
  adds a Start Menu shortcut, and registers the .pdp file type so double-clicking a
  project opens it.

  Everything is per-user (HKCU + LOCALAPPDATA) so no elevation is needed, and
  uninstall.ps1 undoes exactly what this created.

.PARAMETER SkipPublish
  Install from an existing publish output instead of rebuilding.
#>
[CmdletBinding()]
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$repo    = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'bin\publish'
$target  = Join-Path $env:LOCALAPPDATA 'Programs\PipeDream'
$exe     = Join-Path $target 'PipeDream.exe'

if (-not $SkipPublish) {
    Write-Host 'Publishing self-contained build...'
    & dotnet publish (Join-Path $repo 'src/PipeDream.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o $publish | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $publish 'PipeDream.exe'))) {
    throw "no published build at $publish — run without -SkipPublish"
}

# The app may be running from a previous install; a locked exe gives a clearer error here
# than a half-copied folder later.
Get-Process PipeDream -ErrorAction SilentlyContinue | ForEach-Object {
    throw 'Pipe Dream is running — close it and re-run.'
}

Write-Host "Installing to $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $publish '*') $target -Recurse -Force

# Start Menu shortcut
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$lnk = Join-Path $startMenu 'Pipe Dream.lnk'
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $target
$sc.Description = 'Pipe Dream — SMW Editor'
$sc.Save()
Write-Host "Start Menu shortcut: $lnk"

# .pdp association (per-user). ProgId carries the icon + open command; the extension key
# points at it, and OpenWithProgids lets Explorer offer it without hijacking an existing
# default the user may have set.
$progId = 'PipeDream.Project'
New-Item -Path "HKCU:\Software\Classes\$progId" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId" -Name '(Default)' -Value 'Pipe Dream Project'
New-Item -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Name '(Default)' -Value "$exe,0"
New-Item -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\shell\open\command" -Name '(Default)' -Value "`"$exe`" `"%1`""
New-Item -Path 'HKCU:\Software\Classes\.pdp' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\.pdp' -Name '(Default)' -Value $progId
New-Item -Path 'HKCU:\Software\Classes\.pdp\OpenWithProgids' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\Classes\.pdp\OpenWithProgids' -Name $progId -Value ([byte[]]@()) -Type Binary
Write-Host 'Registered .pdp -> Pipe Dream'

# Explorer caches associations until told otherwise.
Add-Type -Namespace Win32 -Name Shell32 -MemberDefinition @'
[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
'@
[Win32.Shell32]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ''
Write-Host "Installed. Launch from the Start Menu, or double-click any project.pdp."
