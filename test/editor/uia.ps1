# UI Automation helpers for driving the AutomationML Editor. Dot-source this file.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$ErrorActionPreference = 'Stop'
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

if (-not ('Win32Shot' -as [type])) {
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Shot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

function Get-EditorProcess { Get-Process AMLEditor -ErrorAction SilentlyContinue | Select-Object -First 1 }

# Top-level windows of the editor process (main window, dialogs).
function Get-EditorWindows {
  $p = Get-EditorProcess
  if (-not $p) { return @() }
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
  return @($AE::RootElement.FindAll($TS::Children, $cond))
}

function Get-EditorWindow([string]$titleLike = '*') {
  foreach ($w in Get-EditorWindows) { if ($w.Current.Name -like $titleLike) { return $w } }
  return $null
}

# First descendant whose name matches (wildcards) and, optionally, whose control type matches.
function Find-El($root, [string]$name = '*', [string]$type = '', [int]$timeout = 10, [string]$automationId = '') {
  $end = (Get-Date).AddSeconds($timeout)
  do {
    $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
      try {
        $c = $e.Current
        if ($type -and $c.ControlType.ProgrammaticName -ne "ControlType.$type") { continue }
        if ($automationId -and $c.AutomationId -ne $automationId) { continue }
        if ($c.Name -like $name) { return $e }
      } catch { }
    }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $end)
  return $null
}

function Find-All($root, [string]$name = '*', [string]$type = '') {
  $out = @()
  foreach ($e in $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    try {
      $c = $e.Current
      if ($type -and $c.ControlType.ProgrammaticName -ne "ControlType.$type") { continue }
      if ($c.Name -like $name) { $out += $e }
    } catch { }
  }
  return $out
}

function Show-Tree($el, [int]$depth = 3, [int]$indent = 0) {
  if ($indent -gt $depth) { return }
  $c = $el.Current
  $line = ('  ' * $indent) + ($c.ControlType.ProgrammaticName -replace 'ControlType.', '') + " '" + $c.Name + "'"
  if ($c.AutomationId) { $line += " #" + $c.AutomationId }
  if (-not $c.IsEnabled) { $line += " (disabled)" }
  $line
  $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $ch = $w.GetFirstChild($el)
  while ($ch) { Show-Tree $ch $depth ($indent + 1); $ch = $w.GetNextSibling($ch) }
}

function Get-Pattern($el, $pattern) {
  $p = $null
  if ($el.TryGetCurrentPattern($pattern, [ref]$p)) { return $p }
  return $null
}

function Invoke-El($el) {
  $p = Get-Pattern $el ([System.Windows.Automation.InvokePattern]::Pattern)
  if ($p) { $p.Invoke(); return }
  $p = Get-Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)
  if ($p) { $p.Select(); return }
  $p = Get-Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  if ($p) { $p.Expand(); return }
  $p = Get-Pattern $el ([System.Windows.Automation.TogglePattern]::Pattern)
  if ($p) { $p.Toggle(); return }
  throw "No pattern to invoke '$($el.Current.Name)'"
}

function Select-El($el) { (Get-Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
function Expand-El($el) { (Get-Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() }
function Set-Text($el, [string]$text) { (Get-Pattern $el ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($text) }

# A picture of a window, including WebView2 content (PW_RENDERFULLCONTENT).
function Save-WindowShot($el, [string]$path) {
  $hwnd = [IntPtr]$el.Current.NativeWindowHandle
  $r = New-Object Win32Shot+RECT
  [void][Win32Shot]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][Win32Shot]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  "picture: $path ($w x $h)"
}
