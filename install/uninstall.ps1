<#
.SYNOPSIS
  Remove a per-user Pipe Dream install.

.DESCRIPTION
  Undoes exactly what install.ps1 created: the program folder, the Start Menu shortcut and
  the .pdp registration. Leaves user data alone — the config in %APPDATA%\PipeDream and
  your project folders are untouched.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\PipeDream'
$lnk    = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Pipe Dream.lnk'

Get-Process PipeDream -ErrorAction SilentlyContinue | ForEach-Object {
    throw 'Pipe Dream is running — close it and re-run.'
}

if (Test-Path $target) { Remove-Item $target -Recurse -Force; Write-Host "Removed $target" }
if (Test-Path $lnk)    { Remove-Item $lnk -Force;             Write-Host "Removed $lnk" }

foreach ($key in 'HKCU:\Software\Classes\PipeDream.Project', 'HKCU:\Software\Classes\.pdp') {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force; Write-Host "Removed $key" }
}

Add-Type -Namespace Win32 -Name Shell32u -MemberDefinition @'
[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
'@
[Win32.Shell32u]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ''
Write-Host 'Uninstalled. Config (%APPDATA%\PipeDream) and your projects were left in place.'
