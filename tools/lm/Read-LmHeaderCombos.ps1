<#
.SYNOPSIS
Read the combo boxes of Lunar Magic's "Change Properties in Header" dialog off the running
program: every entry of every list, with the caption that labels it.

.DESCRIPTION
LM ships no data files, so the names it gives the header's graphics settings ("Normal 1",
"Castle 1", ...) exist only inside the exe and in the dialog that shows them. Rather than hunt
the string tables, this opens LM on a ROM, posts the menu command the way Invoke-LmGui.ps1 does
(WM_COMMAND, no focus or coordinates), waits for the dialog, and asks each ComboBox for its items
with CB_GETCOUNT/CB_GETLBTEXT — messages Windows marshals across processes for the standard
control, so the text comes back in our buffer. Each combo is paired with the nearest Static
caption above-left of it, which is how the two 16-entry GFX lists tell themselves apart.

The output is one object per combo: Caption, Id (the dialog control id), Rect, Items. Message
boxes LM raises on the way (its restore prompt on a fresh folder, for one) are answered as
Invoke-LmGui.ps1 answers them. LM is closed at the end and killed if it lingers. Pass a COPY of
a ROM: LM may write restore information beside it.

.EXAMPLE
  $combos = tools\lm\Read-LmHeaderCombos.ps1 -Rom copy.smc
  $combos | Where-Object Caption -match 'GFX' | ForEach-Object { $_.Caption; $_.Items }
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    # The menu item's text, as a regex over the item with its accelerator stripped.
    [string]$MenuItem = 'Properties in Header',
    [int]$TimeoutSeconds = 30,
    # Print LM's menu bar (every item's text) instead, to find the -MenuItem to ask for.
    [switch]$ListMenu,
    [string]$LunarMagic = $env:PIPEDREAM_LUNAR_MAGIC
)
$ErrorActionPreference = 'Stop'
if (-not $LunarMagic) { $LunarMagic = 'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe' }
if (-not (Test-Path $LunarMagic)) { throw "Lunar Magic not found at '$LunarMagic' (set PIPEDREAM_LUNAR_MAGIC)" }
$Rom = (Resolve-Path $Rom).Path

Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class LmCombo {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
  [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr m, int i);
  [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr m, int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr m, uint i, StringBuilder s, int n, uint f);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, StringBuilder l);
  public static List<IntPtr> Windows(uint pid) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static List<IntPtr> Children(IntPtr h) { var r = new List<IntPtr>(); EnumChildWindows(h, (c, l) => { r.Add(c); return true; }, IntPtr.Zero); return r; }
  public static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string MenuStr(IntPtr m, int i) { var s = new StringBuilder(256); GetMenuString(m, (uint)i, s, 256, 0x400); return s.ToString(); }
  public static List<string> MenuLines(IntPtr hwnd) {
    var r = new List<string>(); var bar = GetMenu(hwnd); if (bar == IntPtr.Zero) return r;
    for (int i = 0; i < GetMenuItemCount(bar); i++) {
      var sub = GetSubMenu(bar, i); if (sub == IntPtr.Zero) continue;
      for (int j = 0; j < GetMenuItemCount(sub); j++) r.Add(MenuStr(bar, i).Replace("&", "") + " > " + MenuStr(sub, j).Replace("&", ""));
    }
    return r;
  }
  public static uint FindCommand(IntPtr hwnd, string pattern) {
    var bar = GetMenu(hwnd); if (bar == IntPtr.Zero) return 0;
    for (int i = 0; i < GetMenuItemCount(bar); i++) {
      var sub = GetSubMenu(bar, i); if (sub == IntPtr.Zero) continue;
      for (int j = 0; j < GetMenuItemCount(sub); j++) {
        var t = MenuStr(sub, j).Replace("&", ""); int tab = t.IndexOf('\t'); if (tab >= 0) t = t.Substring(0, tab);
        if (System.Text.RegularExpressions.Regex.IsMatch(t, pattern)) return GetMenuItemID(sub, j);
      }
    }
    return 0;
  }
  // CB_GETCOUNT / CB_GETLBTEXTLEN / CB_GETLBTEXT: the standard control's messages, marshalled by Windows.
  public static List<string> ComboItems(IntPtr combo) {
    var items = new List<string>();
    int n = (int)SendMessage(combo, 0x0146, IntPtr.Zero, IntPtr.Zero);
    for (int i = 0; i < n; i++) {
      int len = (int)SendMessage(combo, 0x0149, (IntPtr)i, IntPtr.Zero);
      var s = new StringBuilder(Math.Max(len, 1) + 1);
      SendMessage(combo, 0x0148, (IntPtr)i, s);
      items.Add(s.ToString());
    }
    return items;
  }
}
"@

$WM_COMMAND = 0x111; $WM_CLOSE = 0x10; $BM_CLICK = 0xF5
Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$end = (Get-Date).AddSeconds(10); while ((Get-Date) -lt $end -and (Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }

function LmProcs { @(Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue) }
function LmWindows { LmProcs | ForEach-Object { [LmCombo]::Windows([uint32]$_.Id) } }
function MainWindow { LmWindows | Where-Object { [LmCombo]::Cls($_) -eq 'LMFrame' } | Select-Object -First 1 }
function Kids([IntPtr]$h) { @([LmCombo]::Children($h)) }
# A dialog with controls beyond text and buttons: the one we are after, not a message box.
function EditorDialog { LmWindows | Where-Object { [LmCombo]::Cls($_) -eq '#32770' -and (Kids $_ | Where-Object { [LmCombo]::Cls($_) -eq 'ComboBox' }) } | Select-Object -First 1 }
# Message boxes get Invoke-LmGui's answers: Restore -> Cancel, discard -> No, else OK/Yes/first.
function HandleMessageBoxes {
    foreach ($h in (LmWindows | Where-Object { [LmCombo]::Cls($_) -eq '#32770' })) {
        $kids = Kids $h
        if ($kids | Where-Object { [LmCombo]::Cls($_) -notin @('Static', 'Button') }) { continue }
        $buttons = @($kids | Where-Object { [LmCombo]::Cls($_) -eq 'Button' })
        $labels = @($buttons | ForEach-Object { [LmCombo]::Text($_) -replace '&', '' })
        $text = [LmCombo]::Text($h) + ' | ' + (($kids | Where-Object { [LmCombo]::Cls($_) -eq 'Static' } | ForEach-Object { [LmCombo]::Text($_) }) -join ' / ')
        $want = if ($text -match 'Restore') { 'Cancel' } elseif ($text -match 'discard') { 'No' }
                elseif ($labels -contains 'OK') { 'OK' } elseif ($labels -contains 'Yes') { 'Yes' } else { $labels[0] }
        Write-Verbose "message box: $text => $want"
        $btn = $buttons | Where-Object { ([LmCombo]::Text($_) -replace '&', '') -eq $want } | Select-Object -First 1
        if ($btn) { [LmCombo]::PostMessage($btn, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        Start-Sleep -Milliseconds 400
    }
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { HandleMessageBoxes; $v = & $cond; if ($v) { return $v }; Start-Sleep -Milliseconds 300 }
    $null
}

$result = @()
try {
    $null = Start-Process $LunarMagic -ArgumentList @("`"$Rom`"") -PassThru
    $main = WaitFor { MainWindow } 20
    if (-not $main) { throw 'Lunar Magic main window never appeared' }
    Start-Sleep 4                                       # "Rendering level ...done"
    HandleMessageBoxes
    if ($ListMenu) {
        $result = @([LmCombo]::MenuLines($main))
        [LmCombo]::PostMessage($main, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        WaitFor { -not (LmProcs) } 10 | Out-Null
        return $result
    }
    $cmd = [LmCombo]::FindCommand($main, $MenuItem)
    if (-not $cmd) { throw "no menu item matching '$MenuItem'" }
    [LmCombo]::PostMessage($main, $WM_COMMAND, [IntPtr][int64]$cmd, [IntPtr]::Zero) | Out-Null
    $dlg = WaitFor { EditorDialog } $TimeoutSeconds
    if (-not $dlg) { throw "the dialog for '$MenuItem' never appeared" }
    Start-Sleep 1                                       # let it fill its lists

    $kids = Kids $dlg
    $statics = @($kids | Where-Object { [LmCombo]::Cls($_) -eq 'Static' -and [LmCombo]::Text($_) } | ForEach-Object {
        $r = New-Object LmCombo+RECT; [LmCombo]::GetWindowRect($_, [ref]$r) | Out-Null
        [pscustomobject]@{ Text = [LmCombo]::Text($_); L = $r.L; T = $r.T; R = $r.R; B = $r.B }
    })
    foreach ($c in ($kids | Where-Object { [LmCombo]::Cls($_) -eq 'ComboBox' })) {
        $r = New-Object LmCombo+RECT; [LmCombo]::GetWindowRect($c, [ref]$r) | Out-Null
        # The caption: the nearest static whose top is at or above the combo's, preferring the
        # same row (a caption to the left) over one higher up.
        $cap = $statics | Where-Object { $_.T -le $r.T + 4 } |
            Sort-Object @{ Expression = { [math]::Abs($_.T - $r.T) } }, @{ Expression = { [math]::Abs($_.L - $r.L) } } |
            Select-Object -First 1
        $result += [pscustomobject]@{
            Caption = $(if ($cap) { $cap.Text } else { '' })
            Id      = [LmCombo]::GetDlgCtrlID($c)
            Rect    = '{0},{1}-{2},{3}' -f $r.L, $r.T, $r.R, $r.B
            Items   = @([LmCombo]::ComboItems($c))
        }
    }
    [LmCombo]::PostMessage($dlg, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 500; HandleMessageBoxes
    if ($m = MainWindow) { [LmCombo]::PostMessage($m, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    WaitFor { -not (LmProcs) } 10 | Out-Null
}
finally { LmProcs | Stop-Process -Force -ErrorAction SilentlyContinue }
$result
