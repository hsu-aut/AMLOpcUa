# Opens a file in the running editor through File > Open > Open AML File / Open AML Container.
# The file dialog is filled by window messages (its controls are not reachable through UI
# Automation here). A message box while the document loads stops the script; "The PlugIn
# AMLOpcUa raised an exception!" is a failure of the plugin (exit 3), any other one exit 2.
param([string]$Path, [switch]$Container)
. "$PSScriptRoot\uia.ps1"
$ErrorActionPreference = 'Continue'
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

function Dialogs($w) {
  $out = @()
  foreach ($x in Get-EditorWindows) { if ($x.Current.ClassName -eq '#32770') { $out += $x } }
  foreach ($x in $w.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')))) { $out += $x }
  return $out
}

function Text-Of($dlg) {
  (($dlg.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.AutomationId -eq '65535' } | ForEach-Object { $_.Current.Name }) -join ' ').Trim()
}

$w = Get-EditorWindow 'AutomationML Editor*'
$file = Find-El $w 'File' 'MenuItem' 3
$ecf = Get-Pattern $file ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
if ($ecf.Current.ExpandCollapseState -ne 'Expanded') { $ecf.Expand(); Start-Sleep -Milliseconds 600 }
$open = Find-El $file 'Open' 'MenuItem' 3
(Get-Pattern $open ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand(); Start-Sleep -Milliseconds 600
$item = Find-El $open ($(if ($Container) { 'Open AML Container' } else { 'Open AML File' })) 'MenuItem' 3
$ps = [PowerShell]::Create(); [void]$ps.AddScript({ param($b) $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }).AddArgument($item); [void]$ps.BeginInvoke()

$dlg = $null; $end = (Get-Date).AddSeconds(20)
while (-not $dlg -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 300; $dlg = Dialogs $w | Where-Object { $_.Current.Name -like 'Select AML*' } | Select-Object -First 1 }
if (-not $dlg) { 'no file dialog'; exit 1 }
$h = [IntPtr]$dlg.Current.NativeWindowHandle
$combo = [Win32Dlg]::GetDlgItem($h, 1148)
$edit = [Win32Dlg]::FindWindowEx([Win32Dlg]::FindWindowEx($combo, [IntPtr]::Zero, 'ComboBox', $null), [IntPtr]::Zero, 'Edit', $null)
[void][Win32Dlg]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $Path)
[void][Win32Dlg]::PostMessage($h, 0x0111, [IntPtr]1, [Win32Dlg]::GetDlgItem($h, 1))
"opened: $Path"

# Watch for message boxes while the document loads.
$end = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $end) {
  Start-Sleep -Milliseconds 700
  foreach ($m in Dialogs $w) {
    if ($m.Current.Name -like 'Select AML*') { continue }
    $t = Text-Of $m
    if ($t -like '*AMLOpcUa raised an exception*') { "PLUGIN FAILURE, left open: $t"; exit 3 }
    else { "OTHER MESSAGE, left open: '$($m.Current.Name)': $t"; exit 2 }
  }
}
