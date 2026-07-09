# OptiHub — surgical Control Panel pass for VIDEO pages only.
# Does NOT open resolution lists, custom res, or color combos.
# Only: video color -> NVIDIA radio, video image -> NVIDIA radios, Apply if enabled.
$ErrorActionPreference = 'Continue'

function Write-VLog([string]$Msg) {
    $line = "[VIDEO] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

if (-not ('OptiHubVideoWin' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubVideoWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int a, int b, int c, int d);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  public static void Max(IntPtr h) { ShowWindow(h, 3); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(0x02, 0, 0, 0, 0);
    mouse_event(0x04, 0, 0, 0, 0);
  }
}
"@
}

function Get-Cpl {
    Get-Process nvcplui -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Focus-Cpl {
    $p = Get-Cpl
    if (-not $p) { return $null }
    [void][OptiHubVideoWin]::SetForegroundWindow($p.MainWindowHandle)
    [void][OptiHubVideoWin]::Max($p.MainWindowHandle)
    Start-Sleep -Milliseconds 100
    return $p
}

function Get-Root {
    $p = Focus-Cpl
    if (-not $p) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
    catch { return $null }
}

function Find-ByName($Root, [string]$Name) {
    if (-not $Root) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-ById($Root, [string]$Id) {
    if (-not $Root) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Select-El($El) {
    if (-not $El) { return $false }
    Focus-Cpl | Out-Null
    try {
        $sel = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if (-not $sel.Current.IsSelected) { $sel.Select() }
        return $true
    } catch { }
    try {
        $inv = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        return $true
    } catch { }
    try {
        $r = $El.Current.BoundingRectangle
        if ($r.Width -gt 2 -and $r.Height -gt 2) {
            [OptiHubVideoWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            return $true
        }
    } catch { }
    return $false
}

function Ensure-Cpl {
    Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    $exe = $null
    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'NVIDIAControlPanel' } | Select-Object -First 1
    if ($pkg) {
        $hit = Get-ChildItem $pkg.InstallLocation -Recurse -Filter nvcplui.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { $exe = $hit.FullName }
    }
    if (-not $exe) {
        foreach ($c in @(
            (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client\nvcplui.exe'),
            (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe')
        )) { if (Test-Path $c) { $exe = $c; break } }
    }
    if ($exe) { Start-Process $exe | Out-Null }
    else {
        Start-Process 'shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel' -ErrorAction SilentlyContinue
    }
    $deadline = [datetime]::UtcNow.AddSeconds(12)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Get-Cpl) { Focus-Cpl | Out-Null; return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Go-Page([string]$Name) {
    $root = Get-Root
    $el = Find-ByName $root $Name
    if (-not $el) { Write-VLog "Missing page: $Name"; return $false }
    if (-not (Select-El $el)) { Write-VLog "Could not open: $Name"; return $false }
    Write-VLog "Page: $Name"
    Start-Sleep -Milliseconds 900
    Focus-Cpl | Out-Null
    return $true
}

function Click-ApplyIfEnabled {
    $root = Get-Root
    $apply = Find-ByName $root 'Apply'
    if (-not $apply) {
        Focus-Cpl | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('%a')
        Write-VLog 'Apply Alt+A'
        Start-Sleep -Milliseconds 400
        return
    }
    try {
        if (-not $apply.Current.IsEnabled) {
            Write-VLog 'Apply disabled (already set)'
            return
        }
    } catch { }
    if (Select-El $apply) { Write-VLog 'Apply' }
    Start-Sleep -Milliseconds 500
    # Keep/Yes if it appears (short)
    $end = [datetime]::UtcNow.AddSeconds(6)
    while ([datetime]::UtcNow -lt $end) {
        $desk = [System.Windows.Automation.AutomationElement]::RootElement
        foreach ($n in @('Yes', 'Keep changes', 'Keep Changes', 'OK')) {
            $el = Find-ByName $desk $n
            if ($el -and $el.Current.IsEnabled -and $el.Current.Name -notmatch '(?i)^no') {
                [void](Select-El $el)
                Write-VLog "Keep: $n"
                return
            }
        }
        Start-Sleep -Milliseconds 250
    }
}

function Click-MonitorSlots {
    # Optional: click display strip if present so each monitor gets video NVIDIA settings
    $root = Get-Root
    $strip = Find-ById $root '528'
    $count = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
    if (-not $strip) { return @(0) }
    try {
        $r = $strip.Current.BoundingRectangle
        if ($r.Width -lt 40) { return @(0) }
        $idxs = @()
        for ($i = 0; $i -lt $count; $i++) {
            $x = [int]($r.X + ($r.Width * ($i + 0.5) / $count))
            $y = [int]($r.Y + $r.Height * 0.55)
            Focus-Cpl | Out-Null
            [OptiHubVideoWin]::Click($x, $y)
            Write-VLog "Monitor slot $i"
            Start-Sleep -Milliseconds 600
            $idxs += $i
        }
        return $idxs
    } catch { return @(0) }
}

# ---- main ----
Write-VLog 'Video-only CPL pass (NVIDIA color/image radios only)'
if (-not (Ensure-Cpl)) {
    Write-VLog 'FATAL: Control Panel did not start'
    exit 1
}

# Video color
if (Go-Page 'Adjust video color settings') {
    $slots = @(Click-MonitorSlots)
    if ($slots.Count -eq 0) { $slots = @(0) }
    # If strip missing, still apply once on current
    $root = Get-Root
    $ok = $false
    $el = Find-ById $root '1302'
    if (-not $el) { $el = Find-ByName $root 'With the NVIDIA settings' }
    if ($el -and (Select-El $el)) {
        Write-VLog 'Video color -> With the NVIDIA settings'
        $ok = $true
    } else {
        Write-VLog 'Video color NVIDIA radio not found'
    }
    # If multi-mon strip worked, re-select each and set again
    if ($slots.Count -gt 1) {
        foreach ($i in $slots) {
            $root = Get-Root
            $el = Find-ById $root '1302'
            if (-not $el) { $el = Find-ByName $root 'With the NVIDIA settings' }
            if ($el) { [void](Select-El $el) }
            Start-Sleep -Milliseconds 200
        }
    }
    Click-ApplyIfEnabled
}

# Video image
if (Go-Page 'Adjust video image settings') {
    [void](Click-MonitorSlots)
    $root = Get-Root
    foreach ($id in @('1402', '1409')) {
        $el = Find-ById $root $id
        if ($el -and (Select-El $el)) { Write-VLog "Video image id=$id" }
    }
    # All "Use the NVIDIA setting" radios
    try {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            $nm = [string]$all.Item($i).Current.Name
            if ($nm -eq 'Use the NVIDIA setting' -or $nm -eq 'With the NVIDIA settings') {
                [void](Select-El $all.Item($i))
                Write-VLog "Selected: $nm"
            }
        }
    } catch { }
    Click-ApplyIfEnabled
}

# Desktop size: only No scaling + GPU + Override — exact IDs, no combos except GPU pick by key
if (Go-Page 'Adjust desktop size and position') {
    [void](Click-MonitorSlots)
    $root = Get-Root
    $no = Find-ById $root '327'
    if (-not $no) { $no = Find-ByName $root 'No scaling' }
    if ($no) { [void](Select-El $no); Write-VLog 'No scaling' }

    $combo = Find-ById $root '9329'
    if ($combo) {
        $cur = [string]$combo.Current.Name
        if ($cur -notmatch '(?i)\bGPU\b') {
            [void](Select-El $combo)
            Start-Sleep -Milliseconds 350
            $gpu = Find-ByName (Get-Root) 'GPU'
            if (-not $gpu) {
                $gpu = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) 'GPU'
            }
            if ($gpu) { [void](Select-El $gpu); Write-VLog 'Perform scaling on: GPU' }
            else {
                Focus-Cpl | Out-Null
                [System.Windows.Forms.SendKeys]::SendWait('g{ENTER}')
                Write-VLog 'Perform scaling on: GPU (keys)'
            }
        } else {
            Write-VLog "Perform scaling on already: $cur"
        }
    }

    $ovr = Find-ById $root '9330'
    if (-not $ovr) {
        $ovr = Find-ByName $root 'Override the scaling mode set by games and programs'
    }
    if ($ovr) {
        try {
            $tp = $ovr.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
                $tp.Toggle()
                Write-VLog 'Override = On'
            } else {
                Write-VLog 'Override already On'
            }
        } catch {
            [void](Select-El $ovr)
            Write-VLog 'Override clicked'
        }
    } else {
        Write-VLog 'Override checkbox not found'
    }
    Click-ApplyIfEnabled
}

# Close CPL — leave system clean
Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-VLog 'Video/scaling CPL pass done'
exit 0
