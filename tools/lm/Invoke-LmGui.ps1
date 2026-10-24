<#
.SYNOPSIS
Drive one of Lunar Magic's GUI-only save routes (Ctrl+S level save, or the 16x16 editor's F9
Map16 save) against a ROM, and report every dialog it raised.

.DESCRIPTION
The CLI cannot answer "what does LM write on a save" (reference/LUNAR_MAGIC.md): the routes
that matter are menu commands. Rather than synthesising keystrokes into a window that must be
in the foreground, this finds the menu item by its text and posts the same WM_COMMAND the
accelerator would — no focus, no coordinates, no DPI. Dialogs (class #32770 owned by LM's
process) are polled for; each one's title, static text and buttons are logged, then a button
is clicked by text through BM_CLICK. The rules:

    text mentions "Restore"   -> Cancel   (LM wants its restore point; Cancel still saves)
    anything else             -> OK, Yes, or the first button

LM is opened on the ROM in place — pass a copy. A level number on LM's command line makes it
exit at once (measured, LM 3.40), so the level is whatever LM opens by default. LM is closed
with WM_CLOSE at the end and killed if it does not go. The result has Dialogs, Changed, TimedOut.

.EXAMPLE
  tools\lm\Invoke-LmGui.ps1 -Rom work.smc -Action SaveLevel
  tools\lm\Invoke-LmGui.ps1 -Rom work.smc -Action SaveMap16
  tools\lm\Invoke-LmGui.ps1 -Rom work.smc -Action SaveExAnim     # slot 0 of the level, then Ctrl+S: installs LM's engine
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Rom,
    [Parameter(Mandatory)][ValidateSet('SaveLevel', 'SaveMap16', 'SaveExAnim', 'OpenOnly')][string]$Action,
    [int]$TimeoutSeconds = 60,
    # SaveLevel only: LM's Save Level to ROM writes nothing for an unmodified level, so reorder
    # its objects first (Select All, Bring to Front) — a modification that keeps the level's content.
    [switch]$Dirty,
    # SaveMap16 only: instead of F9, click the editor's Nth Button child (its toolbar is unlabelled
    # buttons; index 0 is the reload, measured by its "discard all changes" prompt). For probing.
    [int]$EditorButton = -1,
    # SaveExAnim only: what goes in slot 0 of "Edit Level ExAnimated Frames" (every value hex, as the
    # dialog takes them). The defaults are exanim_1's one 8x8 tile, three frames, no trigger — the
    # smallest slot that makes LM install its ExAnimation engine and hooks on a ROM without them.
    [int]$ExAnimType = 1,                 # index in the Type combo: 1 = "1 8x8"
    [int]$ExAnimTrigger = 0,              # index in the Trigger combo: 0 = None
    [string]$ExAnimDest = 'A0',
    [string[]]$ExAnimFrames = @('601', '655', '6AA'),
    [string]$LunarMagic = $env:PIPEDREAM_LUNAR_MAGIC
)
$ErrorActionPreference = 'Stop'
if (-not $LunarMagic) { $LunarMagic = 'C:\SMW\Projects\.resources\Lunar Magic\Lunar Magic.exe' }
if (-not (Test-Path $LunarMagic)) { throw "Lunar Magic not found at '$LunarMagic' (set PIPEDREAM_LUNAR_MAGIC)" }
$Rom = (Resolve-Path $Rom).Path

Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class LmWin {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr m);
  [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr m, int i);
  [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr m, int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr m, uint i, StringBuilder s, int n, uint f);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
  public static List<IntPtr> Windows(uint pid) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static List<IntPtr> Children(IntPtr h) { var r = new List<IntPtr>(); EnumChildWindows(h, (c, l) => { r.Add(c); return true; }, IntPtr.Zero); return r; }
  public static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string MenuStr(IntPtr m, int i) { var s = new StringBuilder(256); GetMenuString(m, (uint)i, s, 256, 0x400); return s.ToString(); }
  // Every item text in a window's menu bar, one per line, for logging what a window offers.
  public static string MenuDump(IntPtr hwnd) {
    var bar = GetMenu(hwnd); if (bar == IntPtr.Zero) return "(no menu)";
    var sb = new StringBuilder();
    for (int i = 0; i < GetMenuItemCount(bar); i++) {
      sb.Append(MenuStr(bar, i)).Append(": ");
      var sub = GetSubMenu(bar, i);
      if (sub != IntPtr.Zero) for (int j = 0; j < GetMenuItemCount(sub); j++) sb.Append(MenuStr(sub, j)).Append(" | ");
      sb.Append('\n');
    }
    return sb.ToString();
  }
  // Walk a window's menu bar for an item whose text (ampersands stripped, accelerator dropped) matches; returns its command id or 0.
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
}
"@

$WM_COMMAND = 0x111; $WM_CLOSE = 0x10; $BM_CLICK = 0xF5; $WM_KEYDOWN = 0x100; $WM_KEYUP = 0x101; $VK_F9 = 0x78
$WM_SETTEXT = 0xC; $CB_SETCURSEL = 0x14E; $CBN_SELCHANGE = 1
$dialogs = [Collections.Generic.List[string]]::new()
# A previous run's LM may still be letting go of its ROM: Stop-Process returns before it has.
Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$end = (Get-Date).AddSeconds(10); while ((Get-Date) -lt $end -and (Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500
$before = (Get-FileHash $Rom -Algorithm SHA256).Hash

function LmProcs { @(Get-Process 'Lunar Magic' -ErrorAction SilentlyContinue) }
function LmWindows { LmProcs | ForEach-Object { [LmWin]::Windows([uint32]$_.Id) } }
function MainWindow { LmWindows | Where-Object { [LmWin]::Cls($_) -eq 'LMFrame' } | Select-Object -First 1 }
function WindowLike([string]$titlePattern) { LmWindows | Where-Object { [LmWin]::Text($_) -match $titlePattern } | Select-Object -First 1 }

# Every visible #32770 owned by LM: log it, click the button the rules pick. Returns how many were handled.
function HandleDialogs {
    $n = 0
    foreach ($h in (LmWindows | Where-Object { [LmWin]::Cls($_) -eq '#32770' })) {
        $kids = [LmWin]::Children($h)
        # LM's editor windows are #32770 too (the 16x16 editor is one). A message box has nothing
        # but text and buttons in it; anything with other controls is an editor, and clicking its
        # first unlabelled toolbar button is how this script once reloaded the Map16 instead.
        if ($kids | Where-Object { [LmWin]::Cls($_) -notin @('Static', 'Button') }) { continue }
        $statics = @($kids | Where-Object { [LmWin]::Cls($_) -eq 'Static' } | ForEach-Object { [LmWin]::Text($_) } | Where-Object { $_ })
        $buttons = @($kids | Where-Object { [LmWin]::Cls($_) -eq 'Button' })
        $labels = @($buttons | ForEach-Object { [LmWin]::Text($_) -replace '&', '' })
        $text = ([LmWin]::Text($h) + ' | ' + ($statics -join ' / ') + ' | buttons: ' + ($labels -join ', '))
        $want = if ($text -match 'Restore') { 'Cancel' }
                elseif ($text -match 'discard') { 'No' }          # a reload prompt: never throw edits away
                elseif ($labels -contains 'OK') { 'OK' } elseif ($labels -contains 'Yes') { 'Yes' } else { $labels[0] }
        $script:dialogs.Add("$text  => $want")
        $btn = $buttons | Where-Object { ([LmWin]::Text($_) -replace '&', '') -eq $want } | Select-Object -First 1
        # PostMessage, not SendMessage: a send into a modal loop has deadlocked this script before.
        if ($btn) { [LmWin]::PostMessage($btn, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; $n++ }
        Start-Sleep -Milliseconds 400
    }
    $n
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { HandleDialogs | Out-Null; $v = & $cond; if ($v) { return $v }; Start-Sleep -Milliseconds 300 }
    $null
}
function Post([IntPtr]$h, [uint32]$cmd) { [LmWin]::PostMessage($h, $WM_COMMAND, [IntPtr][int64]$cmd, [IntPtr]::Zero) | Out-Null }
# A plain key (no modifier) reaches a window the same way whether it is an accelerator or handled in
# WM_KEYDOWN: TranslateAccelerator reads the modifier state from the keyboard, and none is held.
function Key([IntPtr]$h, [int]$vk) {
    # ...so wait until the person at the keyboard is holding none (they may be in a game).
    $end = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $end -and (@(0x10, 0x11, 0x12) | Where-Object { ([LmWin]::GetAsyncKeyState($_) -band 0x8000) -ne 0 })) { Start-Sleep -Milliseconds 200 }
    [LmWin]::PostMessage($h, $WM_KEYDOWN, [IntPtr]$vk, [IntPtr]1) | Out-Null
    Start-Sleep -Milliseconds 50
    [LmWin]::PostMessage($h, $WM_KEYUP, [IntPtr]$vk, [IntPtr]([int64]0xC0000001)) | Out-Null
}
function WindowList { @(LmWindows | ForEach-Object { "{0} '{1}'" -f [LmWin]::Cls($_), [LmWin]::Text($_) }) }
# Fill a dialog's controls by id. WM_SETTEXT and CB_SETCURSEL return at once, so they are sent; the
# selection-changed notification is posted like a click, since LM's handler may open a dialog of its own.
function SetEdit([IntPtr]$dlg, [int]$id, [string]$text) { [LmWin]::SendMessage([LmWin]::GetDlgItem($dlg, $id), $WM_SETTEXT, [IntPtr]::Zero, $text) | Out-Null }
function SelectCombo([IntPtr]$dlg, [int]$id, [int]$index) {
    $combo = [LmWin]::GetDlgItem($dlg, $id)
    [LmWin]::SendMessage($combo, $CB_SETCURSEL, [IntPtr]$index, [IntPtr]::Zero) | Out-Null
    [LmWin]::PostMessage($dlg, $WM_COMMAND, [IntPtr]([int64](($CBN_SELCHANGE -shl 16) -bor $id)), $combo) | Out-Null
    Start-Sleep -Milliseconds 300
}

$timedOut = $false
$windows = @()
try {
    LmProcs | Stop-Process -Force -ErrorAction SilentlyContinue
    $p = Start-Process $LunarMagic -ArgumentList @("`"$Rom`"") -PassThru
    $main = WaitFor { MainWindow } 20
    if (-not $main) { throw 'Lunar Magic main window never appeared' }
    Start-Sleep 4                                       # "Rendering level ...done"
    HandleDialogs | Out-Null

    switch ($Action) {
        'SaveLevel' {
            if ($Dirty) {
                Post $main ([LmWin]::FindCommand($main, '^Select All$')); Start-Sleep 1
                Post $main ([LmWin]::FindCommand($main, '^Bring to Front$')); Start-Sleep 1
            }
            $cmd = [LmWin]::FindCommand($main, '^Save Level to ROM$')
            if (-not $cmd) { throw 'Save Level to ROM not found in the menu' }
            Post $main $cmd
        }
        'SaveMap16' {
            $cmd = [LmWin]::FindCommand($main, '16x16 Tile Map Editor')
            if (-not $cmd) { throw '16x16 Tile Map Editor not found in the menu' }
            Post $main $cmd
            $ed = WaitFor { WindowLike '16x16' } 20
            if (-not $ed) { throw '16x16 editor window never appeared' }
            Start-Sleep 2
            # The editor has no menu bar (measured): F9 is a key it handles itself — in whichever
            # control has focus, so it goes to the frame and every child.
            if ($EditorButton -ge 0) {
                $btn = @([LmWin]::Children($ed) | Where-Object { [LmWin]::Cls($_) -eq 'Button' })[$EditorButton]
                [LmWin]::PostMessage($btn, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
            else { foreach ($h in @($ed) + [LmWin]::Children($ed)) { Key $h $VK_F9 } }
        }
        'SaveExAnim' {
            # The dialog's controls, by id (measured, LM 3.40): 101 slot, 102 type, 103 trigger combos;
            # 116 colours, 113 frame count, 349 destination; 401..464 the frame sources; 1 is OK.
            $cmd = [LmWin]::FindCommand($main, '^Edit Level ExAnimated Frames')
            if (-not $cmd) { throw 'Edit Level ExAnimated Frames not found in the menu' }
            Post $main $cmd
            $dlg = WaitFor { WindowLike 'Extended Animation Frames' } 20
            if (-not $dlg) { throw 'ExAnimation dialog never appeared' }
            Start-Sleep 1
            SelectCombo $dlg 101 0
            SelectCombo $dlg 102 $ExAnimType
            SelectCombo $dlg 103 $ExAnimTrigger
            SetEdit $dlg 113 ('{0:X}' -f $ExAnimFrames.Count)
            SetEdit $dlg 349 $ExAnimDest
            for ($i = 0; $i -lt $ExAnimFrames.Count; $i++) { SetEdit $dlg (401 + $i) $ExAnimFrames[$i] }
            Start-Sleep -Milliseconds 300
            [LmWin]::PostMessage([LmWin]::GetDlgItem($dlg, 1), $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            $gone = WaitFor { -not (WindowLike 'Extended Animation Frames') } 10
            if (-not $gone) { throw 'ExAnimation dialog did not close on OK' }
            Start-Sleep 1
            Post $main ([LmWin]::FindCommand($main, '^Save Level to ROM$'))
        }
    }
    # Let the save land: either the file changes, or nothing happens for a while.
    # LM holds the ROM exclusively while it writes, and a hash taken then throws — that is "not yet".
    $changed = WaitFor { try { (Get-FileHash $Rom -Algorithm SHA256).Hash -ne $before } catch { $false } } 15
    Start-Sleep 2; HandleDialogs | Out-Null
    $windows = WindowList

    foreach ($h in (LmWindows | Where-Object { [LmWin]::Cls($_) -ne 'LMFrame' })) { [LmWin]::PostMessage($h, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    Start-Sleep 1; HandleDialogs | Out-Null
    if ($m = MainWindow) { [LmWin]::PostMessage($m, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    $gone = WaitFor { -not (LmProcs) } 10
    if (-not $gone) { $timedOut = $true; LmProcs | Stop-Process -Force -ErrorAction SilentlyContinue }
}
finally { LmProcs | Stop-Process -Force -ErrorAction SilentlyContinue }

$after = (Get-FileHash $Rom -Algorithm SHA256).Hash
[pscustomobject]@{
    Action   = $Action
    Changed  = $before -ne $after
    TimedOut = $timedOut
    Dialogs  = $dialogs.ToArray()
    Windows  = $windows
}
