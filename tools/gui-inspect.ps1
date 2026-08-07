<#
.SYNOPSIS
    Inspect and drive the dnSpy WPF GUI from a headless agent session via UI Automation.

.DESCRIPTION
    Some dgSpy defects are only visible in the GUI. The most important class is MEF
    composition failure: a part whose imports cannot be satisfied simply does not exist,
    nothing is logged, and everything that imported it disappears too. No automated suite
    sees that -- see the "MEF composition fails silently" section of docs/DGSPY_BASELINE.md.

    This script gives an agent eyes on the running GUI without a human at the keyboard:
    enumerate windows, dump control trees, expand combo boxes, click buttons, and capture
    screenshots for anything UI Automation cannot express.

    ASCII-only by policy: an em dash in a .ps1 parses as a stray quote under CP1252.

.EXAMPLE
    # What windows does the running dnSpy have?
    powershell -NoProfile -STA -File tools\gui-inspect.ps1 -List

.EXAMPLE
    # Launch the published dnSpy, open Debug > Start Debugging, list the debug engines.
    powershell -NoProfile -STA -File tools\gui-inspect.ps1 -Launch -Keys "{F5}" `
        -WaitWindow "Debug Program" -ExpandCombo 0

.EXAMPLE
    # Open the environment editor from the .NET Framework page and photograph it.
    powershell -NoProfile -STA -File tools\gui-inspect.ps1 -Window "Debug Program" `
        -ClickNear envTextBox -WaitWindow "Edit Environment Variables" -Dump -Screenshot env.png

.NOTES
    Must run with -STA. UI Automation refuses to marshal from an MTA thread.
#>
[CmdletBinding()]
param(
    # Title of the window to operate on. Substring match, case-insensitive.
    # Defaults to the first top-level window of the target process.
    [string]$Window,

    # Restrict to a single process id. Useful when several dnSpy instances are running.
    [int]$TargetProcessId,

    # List every top-level window with its pid and title, then exit.
    [switch]$List,

    # Launch dnSpy before doing anything else.
    [switch]$Launch,

    # Executable to launch. Defaults to the net10 x64 self-contained publish output.
    [string]$Exe,

    # Seconds to wait after -Launch before the main window is expected.
    [int]$LaunchWaitSeconds = 15,

    # SendKeys string delivered to the focused target window, e.g. "{F5}" or "%dS".
    [string]$Keys,

    # Wait for a window whose title matches this before continuing.
    [string]$WaitWindow,

    # Dump the control tree of the target window.
    [switch]$Dump,

    # Max depth for -Dump.
    [int]$Depth = 12,

    # Zero-based index of a combo box in the target window to expand and enumerate.
    [int]$ExpandCombo = -1,

    # Invoke a button by its visible name, e.g. "OK".
    [string]$Click,

    # Invoke the button sharing a row with the edit control having this AutomationId.
    # This is how the "..." browse buttons are reached, since they all share the name "...".
    [string]$ClickNear,

    # Save a full virtual-screen PNG here. Relative paths resolve against the current directory.
    [string]$Screenshot,

    # Seconds to wait for -WaitWindow.
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw "gui-inspect.ps1 must run under -STA (powershell -NoProfile -STA -File ...)"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GuiInspectNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$root = $AE::RootElement
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker

# --- helpers ---------------------------------------------------------------

# A WPF ListItem bound to a view model reports the VM's ToString() as its Name,
# so the automation name is often a CLR type name while the text the user sees
# lives on a child Text element. Always resolve through this.
function Get-Label($element) {
    if ($null -eq $element) { return '' }
    $name = $element.Current.Name
    $looksLikeType = $name -match '^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*){2,}$'
    if ($name -and -not $looksLikeType) { return $name }

    # The label can sit several levels down inside a cell or item template, so
    # search descendants rather than only direct children.
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)
    try {
        $text = $element.FindFirst($TS::Descendants, $cond)
        if ($text -and $text.Current.Name) { return $text.Current.Name }
    } catch { }
    return $name
}

function Get-TopLevelWindows {
    $result = @()
    $w = $walker.GetFirstChild($root)
    while ($w) {
        try {
            if ($w.Current.Name -or $w.Current.NativeWindowHandle -ne 0) { $result += $w }
        } catch { }
        $w = $walker.GetNextSibling($w)
    }
    return $result
}

function Find-Window([string]$title, [int]$procId, [int]$timeoutSec) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSec)
    do {
        foreach ($w in Get-TopLevelWindows) {
            try {
                if ($procId -gt 0 -and $w.Current.ProcessId -ne $procId) { continue }
                if ($title) {
                    if ($w.Current.Name -and $w.Current.Name.ToLowerInvariant().Contains($title.ToLowerInvariant())) { return $w }
                } else {
                    return $w
                }
            } catch { }
        }
        # Dialogs are not always children of the desktop; sweep descendants too.
        if ($title) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $title)
            $hit = $root.FindFirst($TS::Descendants, $cond)
            if ($hit) {
                if ($procId -le 0 -or $hit.Current.ProcessId -eq $procId) { return $hit }
            }
        }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

# Windows refuses SetForegroundWindow to a process that does not already own the
# foreground, so calling it alone leaves the window inactive and WPF popups never
# render. AutomationElement.SetFocus() goes through UIA and does activate.
function Set-Foreground($element) {
    $h = [IntPtr]$element.Current.NativeWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        [void][GuiInspectNative]::ShowWindow($h, 9)   # SW_RESTORE: never resize a dialog
        [void][GuiInspectNative]::SetForegroundWindow($h)
    }
    try { $element.SetFocus() } catch { }
    Start-Sleep -Milliseconds 800
}

function Save-Screenshot([string]$path) {
    if ([IO.Path]::IsPathRooted($path)) { $full = $path }
    else { $full = [IO.Path]::GetFullPath((Join-Path (Get-Location) $path)) }
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "screenshot: $full"
}

function Write-Tree($element, [int]$depth, [int]$maxDepth) {
    if ($depth -gt $maxDepth) { return }
    $pad = ' ' * ($depth * 2)
    $c = $element.Current
    $line = $pad + $c.ControlType.ProgrammaticName.Replace('ControlType.', '')
    $label = Get-Label $element
    if ($label) { $line += " '" + $label + "'" }
    if ($c.AutomationId) { $line += " #" + $c.AutomationId }
    if (-not $c.IsEnabled) { $line += " [disabled]" }
    Write-Output $line
    $child = $walker.GetFirstChild($element)
    while ($child) {
        Write-Tree $child ($depth + 1) $maxDepth
        $child = $walker.GetNextSibling($child)
    }
}

function Get-Descendants($element, $controlType) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $controlType)
    return $element.FindAll($TS::Descendants, $cond)
}

# --- actions ---------------------------------------------------------------

if ($List) {
    foreach ($w in Get-TopLevelWindows) {
        try { Write-Output ("pid=" + $w.Current.ProcessId + "  '" + $w.Current.Name + "'") } catch { }
    }
    return
}

$launched = $null
if ($Launch) {
    if (-not $Exe) {
        $repo = Split-Path -Parent $PSScriptRoot
        $Exe = Join-Path $repo 'dnSpy\dnSpy\bin\Release\net10.0-windows\win-x64\publish\dnSpy.exe'
    }
    if (-not (Test-Path $Exe)) { throw "executable not found: $Exe (run build.ps1 net-x64 -NoMsbuild first)" }
    $launched = Start-Process -FilePath $Exe -PassThru
    $TargetProcessId = $launched.Id
    Write-Output ("launched pid " + $launched.Id + ": " + $Exe)
    Start-Sleep -Seconds $LaunchWaitSeconds
}

$target = Find-Window $Window $TargetProcessId $TimeoutSeconds
if (-not $target) { throw "no window matching '$Window' (pid filter: $TargetProcessId)" }
Write-Output ("target: pid=" + $target.Current.ProcessId + " '" + $target.Current.Name + "'")
if ($TargetProcessId -le 0) { $TargetProcessId = $target.Current.ProcessId }

if ($Keys) {
    Set-Foreground $target
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Write-Output "sent keys: $Keys"
}

# -WaitWindow means "retarget onto the window the preceding action opened", so it has
# to run after the clicks when a click is what opens it, and before them when the
# clicks are meant to land on a window that -Keys opened.
$clicking = [bool]($Click -or $ClickNear)

if ($WaitWindow -and -not $clicking) {
    $next = Find-Window $WaitWindow $TargetProcessId $TimeoutSeconds
    if (-not $next) { throw "window '$WaitWindow' did not appear within $TimeoutSeconds s" }
    $target = $next
    Write-Output ("target: '" + $target.Current.Name + "'")
}

if ($ClickNear) {
    $rowY = $null
    foreach ($e in (Get-Descendants $target $CT::Edit)) {
        if ($e.Current.AutomationId -eq $ClickNear) { $rowY = $e.Current.BoundingRectangle.Y }
    }
    if ($null -eq $rowY) { throw "no edit control with AutomationId '$ClickNear'" }
    $btn = $null
    foreach ($b in (Get-Descendants $target $CT::Button)) {
        if ([Math]::Abs($b.Current.BoundingRectangle.Y - $rowY) -lt 25) { $btn = $b }
    }
    if (-not $btn) { throw "no button on the same row as '$ClickNear'" }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output ("clicked '" + (Get-Label $btn) + "' next to #" + $ClickNear)
    Start-Sleep -Milliseconds 1500
}

if ($Click) {
    $btn = $null
    foreach ($b in (Get-Descendants $target $CT::Button)) {
        if ((Get-Label $b) -eq $Click) { $btn = $b; break }
    }
    if (-not $btn) { throw "no button named '$Click'" }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "clicked '$Click'"
    Start-Sleep -Milliseconds 1500
}

if ($WaitWindow -and $clicking) {
    $next = Find-Window $WaitWindow $TargetProcessId $TimeoutSeconds
    if (-not $next) { throw "window '$WaitWindow' did not appear within $TimeoutSeconds s" }
    $target = $next
    Write-Output ("target: '" + $target.Current.Name + "'")
}

if ($ExpandCombo -ge 0) {
    $combos = Get-Descendants $target $CT::ComboBox
    if ($ExpandCombo -ge $combos.Count) { throw "combo index $ExpandCombo out of range (found $($combos.Count))" }
    $combo = $combos[$ExpandCombo]
    # The popup is only realized when the owning window is active, so items come
    # back empty against a background window. Foreground it first.
    Set-Foreground $target
    try { $combo.SetFocus() } catch { }
    $ecp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ecp.Expand()
    Start-Sleep -Milliseconds 1500
    Write-Output "combo[$ExpandCombo] items:"
    $items = Get-Descendants $combo $CT::ListItem
    if ($items.Count -eq 0) { Write-Output "  (none -- the popup may not have rendered; retry or use -Screenshot)" }
    foreach ($it in $items) { Write-Output ("  - " + (Get-Label $it)) }
    if ($Screenshot) { Save-Screenshot $Screenshot; $Screenshot = $null }
    $ecp.Collapse()
    Start-Sleep -Milliseconds 400
}

if ($Dump) {
    Write-Output "control tree:"
    Write-Tree $target 0 $Depth
}

if ($Screenshot) { Save-Screenshot $Screenshot }

if ($launched) { Write-Output ("still running: pid " + $launched.Id + " (stop it with Stop-Process -Id " + $launched.Id + ")") }
