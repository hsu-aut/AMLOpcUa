# Helpers for driving the AMLOpcUa plugin in a running AutomationML Editor and
# taking pictures of it. Dot-source this file: . .\test\editor\editor.ps1
# See README.md beside it for what it needs and what it changes.

. "$PSScriptRoot\uia.ps1"
$ErrorActionPreference = 'Continue'
$ManualDir = (Resolve-Path "$PSScriptRoot\..\..\docs\manual").Path
$EditorExe = if ($env:AML_EDITOR) { $env:AML_EDITOR } else { 'C:\Dev\91_AMLEditor\AMLEditor.exe' }
$EditorData = "$env:LOCALAPPDATA\AutomationML\AutomationMLEditor"

if (-not ('Win32Dlg' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Dlg {
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dlg, int id);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@ }
if (-not ('Win32Top' -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win32Top {
  delegate bool Each(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(Each each, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  // The visible top-level windows of a process, front first, with their titles.
  public static List<KeyValuePair<IntPtr, string>> Of(int pid) {
    var found = new List<KeyValuePair<IntPtr, string>>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { var t = new StringBuilder(512); GetWindowText(h, t, 512); found.Add(new KeyValuePair<IntPtr, string>(h, t.ToString())); }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, Each each, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
  // The file name box of a file dialog: the visible Edit with the id 1001, which a
  // save dialog keeps deeper than an open dialog does (below FloatNotifySink).
  public static IntPtr FileNameEdit(IntPtr dialog) {
    var found = IntPtr.Zero;
    EnumChildWindows(dialog, (h, l) => {
      var c = new StringBuilder(64); GetClassName(h, c, 64);
      if (c.ToString() == "Edit" && GetDlgCtrlID(h) == 1001 && IsWindowVisible(h)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@ }

function Main { Get-EditorWindow '*AutomationML Editor*' }

# The element with this AutomationId (x:Name), below the main window unless a root is given.
function ById([string]$id, [int]$timeout = 10, $root = $null) {
  if (-not $root) { $root = Main }
  if (-not $root) { return $null }
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  $end = (Get-Date).AddSeconds($timeout)
  do {
    $el = $root.FindFirst($TS::Descendants, $cond)
    if ($el) { return $el }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $end)
  return $null
}

# The first element below root with this name (wildcards) and control type.
function ByName($root, [string]$name, [string]$type = '', [int]$timeout = 5) { Find-El $root $name $type $timeout }

# The plugin's whole view (its tab content in the editor).
function PluginRoot {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'AMLOpcUa')
  @((Main).FindAll($TS::Descendants, $cond) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Custom })[0]
}

# Invokes without waiting, for a control that opens a modal dialog.
function Invoke-Async($el) {
  $ps = [PowerShell]::Create()
  [void]$ps.AddScript({ param($b) $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }).AddArgument($el)
  [void]$ps.BeginInvoke()
}

function Toggle($el) { (Get-Pattern $el ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() }

function Tab([string]$id) { Select-El (ById $id); Start-Sleep -Milliseconds 800 }

# A window of the editor whose title matches: a top-level one, or one UI
# Automation lists below the main window (WPF dialogs with an owner). Editor
# 6.4.3.8 no longer has the file dialogs among the windows UI Automation lists,
# so the process's own top-level windows are asked as well.
function Dialog([string]$title, [int]$timeout = 15) {
  $end = (Get-Date).AddSeconds($timeout)
  do {
    foreach ($w in Get-EditorWindows) { if ($w.Current.Name -like $title) { return $w } }
    $m = Main
    if ($m) {
      $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
      foreach ($w in $m.FindAll($TS::Descendants, $cond)) { if ($w.Current.Name -like $title) { return $w } }
    }
    $p = Get-EditorProcess
    if ($p) {
      foreach ($kv in [Win32Top]::Of($p.Id)) {
        if ($kv.Value -like $title) { try { return $AE::FromHandle($kv.Key) } catch { } }
      }
    }
    Start-Sleep -Milliseconds 400
  } while ((Get-Date) -lt $end)
  return $null
}

# Fills a Win32 file dialog (open or save) with a path and confirms it.
function Fill-FileDialog($dlg, [string]$path) {
  $h = [IntPtr]$dlg.Current.NativeWindowHandle
  $edit = [IntPtr]::Zero
  $combo = [Win32Dlg]::GetDlgItem($h, 1148)
  if ($combo -ne [IntPtr]::Zero) {
    $edit = [Win32Dlg]::FindWindowEx([Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, 'ComboBox', $null), [IntPtr]::Zero, 'Edit', $null)
    if ($edit -eq [IntPtr]::Zero) { $edit = [Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, 'Edit', $null) }
  }
  if ($edit -eq [IntPtr]::Zero) { $edit = [Win32Top]::FileNameEdit($h) }
  if ($edit -eq [IntPtr]::Zero) {
    $box = $dlg.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1001')))
    if ($box) { Set-Text $box $path }
  } else {
    [void][Win32Dlg]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $path)
  }
  [void][Win32Dlg]::PostMessage($h, 0x0111, [IntPtr]1, [Win32Dlg]::GetDlgItem($h, 1))
}

# The nearest window with a handle of its own: a dialog is one, though UI
# Automation lists it below the main window.
function TopWindowOf($el) {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $cur = $el
  while ($cur -ne $null -and $cur -ne $AE::RootElement) {
    if ($cur.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and $cur.Current.NativeWindowHandle -ne 0) { return $cur }
    $cur = $walker.GetParent($cur)
  }
  return $null
}

# A picture of one element, or of the box around several, cut out of their
# window, scaled to 1/scale and saved as docs/manual/<name>.png.
function Shot($els, [string]$name, [double]$scale = 1.5, [int]$pad = 6) {
  $els = @($els)
  if ($els.Count -eq 0 -or ($els | Where-Object { $_ -eq $null })) { "shot $name skipped: element missing"; return }
  $win = TopWindowOf $els[0]
  if (-not $win) { "shot $name skipped: no window"; return }
  $hwnd = [IntPtr]$win.Current.NativeWindowHandle
  $r = New-Object Win32Shot+RECT
  [void][Win32Shot]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][Win32Shot]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $left = ($els | ForEach-Object { $_.Current.BoundingRectangle.Left } | Measure-Object -Minimum).Minimum
  $top = ($els | ForEach-Object { $_.Current.BoundingRectangle.Top } | Measure-Object -Minimum).Minimum
  $right = ($els | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
  $bottom = ($els | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum
  $x = [Math]::Max(0, [int]$left - $r.Left - $pad); $y = [Math]::Max(0, [int]$top - $r.Top - $pad)
  $cw = [Math]::Min($w - $x, [int]($right - $left) + 2 * $pad); $ch = [Math]::Min($h - $y, [int]($bottom - $top) + 2 * $pad)
  $crop = $bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $cw, $ch), $bmp.PixelFormat)
  $bmp.Dispose()
  $ow = [int]($cw / $scale); $oh = [int]($ch / $scale)
  $out = New-Object System.Drawing.Bitmap $ow, $oh
  $g2 = [System.Drawing.Graphics]::FromImage($out)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.DrawImage($crop, 0, 0, $ow, $oh)
  $g2.Dispose(); $crop.Dispose()
  $path = Join-Path $ManualDir "$name.png"
  $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
  "shot: $name ($ow x $oh)"
}

function PluginShot([string]$name) { Shot (PluginRoot) $name }

# Brings the plugin's tab to the front among the plugins in the editor's middle.
function Show-Plugin {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'AMLOpcUa')
  $tab = @((Main).FindAll($TS::Descendants, $cond) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem })[0]
  if ($tab) { Select-El $tab; Start-Sleep -Milliseconds 800 }
}

# Starts the editor (English plugin texts with -English) with the WebView2 debug
# port, and answers its start prompts without changing anything: backup files
# with Cancel, the update offer by its close box.
function Start-Editor([switch]$English) {
  if ($English) { $env:AMLOPCUA_LANGUAGE = 'en' }
  if (-not (Get-EditorProcess)) {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=0'
    Start-Process $EditorExe
  }
  $end = (Get-Date).AddSeconds(90)
  while (-not (Main) -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Seconds 10
  $b = Dialog 'Backupfiles Selection' 2; if ($b) { Invoke-El (ById 'CancelButton' 3 $b) }
  $u = Dialog 'AutomationML Editor Update' 2; if ($u) { Invoke-El (ById 'Close' 3 $u) }
  if (Main) { 'editor running' } else { 'editor did not start' }
}

function Open-Doc([string]$path) {
  & "$PSScriptRoot\open-file.ps1" -Path $path | Out-Null
  $leaf = [IO.Path]::GetFileNameWithoutExtension($path)
  $end = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds 2
    if (Find-El (Main) "*$leaf*" 'Text' 1) { return "opened $leaf" }
  }
  return "NOT opened $leaf"
}

# Hides the editor's side panes for wide pictures. The editor saves this in its
# layout when it is closed normally: take Save-EditorState first and
# Restore-EditorState after the editor has ended.
function Hide-SidePanes {
  $pins = @((Main).FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'unpinBtn'))))
  foreach ($p in $pins) { try { if (-not $p.Current.IsOffscreen) { Invoke-El $p; Start-Sleep -Milliseconds 500 } } catch { } }
}

# The files a session in the editor may change: its layout, its settings (the
# CAEX version follows the last document opened), the plugin's settings.
function Save-EditorState([string]$folder) {
  New-Item -ItemType Directory -Force $folder | Out-Null
  Copy-Item "$EditorData\customLayout.xml", "$EditorData\config.xml", "$env:APPDATA\AMLOpcUa\settings.json" $folder
  # The backup files there before, so only the session's own are moved away later.
  Set-Content "$folder\backups-before.txt" -Value @(Get-ChildItem "$EditorData\Backup" -File -ErrorAction SilentlyContinue | ForEach-Object Name)
}

# Puts them back with the editor closed, and moves the backup files the session
# left (a hard stop leaves one per open document) into the folder too; backup
# files that were there before stay.
function Restore-EditorState([string]$folder) {
  if (Get-EditorProcess) { 'close the editor first'; return }
  Copy-Item "$folder\customLayout.xml", "$folder\config.xml" $EditorData
  Copy-Item "$folder\settings.json" "$env:APPDATA\AMLOpcUa\settings.json"
  $left = Join-Path $folder 'backups-left'
  New-Item -ItemType Directory -Force $left | Out-Null
  $before = @(Get-Content "$folder\backups-before.txt" -ErrorAction SilentlyContinue)
  Get-ChildItem "$EditorData\Backup" -File -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Name } | Move-Item -Destination $left
  'editor state restored'
}
