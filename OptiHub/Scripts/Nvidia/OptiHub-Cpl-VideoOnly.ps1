# OptiHub — Control Panel pass for video NVIDIA radios + per-monitor scaling.
# Critical: desktop size page applies No scaling + GPU + Override ON for EACH monitor
# (select monitor -> set -> Apply -> Keep), not once after clicking all icons.
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
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int a, int b, int c, int d);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  public static void Max(IntPtr h) { ShowWindow(h, 3); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(40);
    mouse_event(0x02, 0, 0, 0, 0);
    System.Threading.Thread.Sleep(25);
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
    $h = $p.MainWindowHandle
    [void][OptiHubVideoWin]::BringWindowToTop($h)
    [void][OptiHubVideoWin]::SetForegroundWindow($h)
    [void][OptiHubVideoWin]::Max($h)
    Start-Sleep -Milliseconds 120
    return $p
}

function Get-Root {
    $p = Focus-Cpl
    if (-not $p) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
    catch { return $null }
}

function Find-ByName($Root, [string]$Name) {
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Name)) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-ById($Root, [string]$Id) {
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Id)) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-ByNameContains($Root, [string]$Substr) {
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Substr)) { return $null }
    try {
        $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $lim = [Math]::Min(800, $all.Count)
        for ($i = 0; $i -lt $lim; $i++) {
            $nm = [string]$all.Item($i).Current.Name
            if ($nm -and $nm.IndexOf($Substr, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $all.Item($i)
            }
        }
    } catch { }
    return $null
}

function Find-Control([string[]]$Ids = @(), [string[]]$Names = @(), [string]$NameContains = $null) {
    # Refresh root every search; NVIDIA CPL often invalidates handles after clicks.
    Focus-Cpl | Out-Null
    $root = Get-Root
    if (-not $root) { return $null }
    foreach ($id in $Ids) {
        $el = Find-ById $root $id
        if ($el) {
            try {
                $r = $el.Current.BoundingRectangle
                if ($r.Width -ge 2 -and $r.Height -ge 2) { return $el }
            } catch { }
        }
    }
    foreach ($n in $Names) {
        $el = Find-ByName $root $n
        if ($el) {
            try {
                $r = $el.Current.BoundingRectangle
                if ($r.Width -ge 2 -and $r.Height -ge 2) { return $el }
            } catch { }
        }
    }
    if ($NameContains) {
        $el = Find-ByNameContains $root $NameContains
        if ($el) { return $el }
    }
    return $null
}

function Click-RectCenter($El) {
    if (-not $El) { return $false }
    Focus-Cpl | Out-Null
    try {
        $r = $El.Current.BoundingRectangle
        if ($r.Width -lt 2 -or $r.Height -lt 2) { return $false }
        [OptiHubVideoWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        return $true
    } catch { return $false }
}

function Click-RectLeft($El, [int]$XOffset = 10) {
    # NVIDIA custom panes: radio/checkbox graphic is on the LEFT of the label.
    if (-not $El) { return $false }
    Focus-Cpl | Out-Null
    try {
        $r = $El.Current.BoundingRectangle
        if ($r.Width -lt 2 -or $r.Height -lt 2) { return $false }
        $x = [int]($r.X + [Math]::Min($XOffset, [Math]::Max(4, $r.Width / 8)))
        $y = [int]($r.Y + $r.Height / 2)
        [OptiHubVideoWin]::Click($x, $y)
        return $true
    } catch { return $false }
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
    return (Click-RectCenter $El)
}

function Ensure-Cpl {
    Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
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
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Get-Cpl) { Focus-Cpl | Out-Null; Start-Sleep -Milliseconds 400; return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Go-Page([string]$Name) {
    $root = Get-Root
    $el = Find-ByName $root $Name
    if (-not $el) { Write-VLog "Missing page: $Name"; return $false }
    if (-not (Click-RectCenter $el)) { Write-VLog "Could not open: $Name"; return $false }
    Write-VLog "Page: $Name"
    Start-Sleep -Milliseconds 1200
    Focus-Cpl | Out-Null
    return $true
}

function Click-ApplyAndKeep {
    $root = Get-Root
    $apply = Find-ByName $root 'Apply'
    $clicked = $false
    if ($apply) {
        $enabled = $true
        try { $enabled = [bool]$apply.Current.IsEnabled } catch { }
        if ($enabled) {
            if (Click-RectCenter $apply) {
                Write-VLog 'Apply'
                $clicked = $true
            }
        } else {
            Write-VLog 'Apply disabled (no change detected)'
        }
    } else {
        Focus-Cpl | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('%a')
        Write-VLog 'Apply Alt+A'
        $clicked = $true
    }
    if (-not $clicked) { return $false }
    Start-Sleep -Milliseconds 450
    $end = [datetime]::UtcNow.AddSeconds(8)
    while ([datetime]::UtcNow -lt $end) {
        $desk = [System.Windows.Automation.AutomationElement]::RootElement
        foreach ($n in @('Yes', 'Keep changes', 'Keep Changes', 'Keep these settings', 'OK')) {
            $el = Find-ByName $desk $n
            if ($el -and $el.Current.IsEnabled -and $el.Current.Name -notmatch '(?i)^no|revert') {
                [void](Click-RectCenter $el)
                Write-VLog "Keep: $n"
                Start-Sleep -Milliseconds 300
                return $true
            }
        }
        Focus-Cpl | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 280
    }
    return $true
}

function Get-MonitorPoints {
    # Returns plain Object[] of points. NEVER @($List[T]) on PS7.
    $root = Get-Root
    $strip = Find-ById $root '528'
    $count = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
    if (-not $strip) {
        Write-VLog 'No display strip 528 — single current display only'
        return ,@([pscustomobject]@{ Index = 0; X = 0; Y = 0 })
    }
    try {
        $r = $strip.Current.BoundingRectangle
        if ($r.Width -lt 40 -or $r.Height -lt 20) {
            Write-VLog 'Display strip too small'
            return ,@([pscustomobject]@{ Index = 0; X = 0; Y = 0 })
        }
        $arr = New-Object object[] $count
        for ($i = 0; $i -lt $count; $i++) {
            # Icons sit in the strip; use center of each equal slot.
            $x = [int]($r.X + ($r.Width * ($i + 0.5) / $count))
            $y = [int]($r.Y + $r.Height * 0.50)
            $arr[$i] = [pscustomobject]@{ Index = $i; X = $x; Y = $y }
        }
        Write-VLog "Display strip: $count monitor slot(s), width=$([int]$r.Width)"
        return $arr
    } catch {
        Write-VLog "Strip error: $($_.Exception.Message)"
        return ,@([pscustomobject]@{ Index = 0; X = 0; Y = 0 })
    }
}

function Select-Monitor([int]$Index, [int]$X, [int]$Y) {
    if ($X -le 0) {
        Write-VLog "Monitor $Index (current — no strip click)"
        return
    }
    Focus-Cpl | Out-Null
    [OptiHubVideoWin]::Click($X, $Y)
    Write-VLog "SELECTED monitor $Index @ $X,$Y"
    # CPL reloads scaling controls for that display — wait for it.
    Start-Sleep -Milliseconds 1400
    Focus-Cpl | Out-Null
}

function Write-NvtweakScaling {
    try {
        $base = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
        if (Test-Path $base) {
            Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'ScalingOverride' -Value 1 -Type DWord -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'PerformScalingOn' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'AppControlledScaling' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'ScalingMode' -Value 2 -Type DWord -Force -ErrorAction SilentlyContinue
                Set-ItemProperty -LiteralPath $_.PSPath -Name 'Scaling' -Value 2 -Type DWord -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
}

function Ensure-DesktopSizePage {
    $el = Find-Control -Ids @('327', '9330') -Names @('No scaling') -NameContains 'Override the scaling'
    if ($el) { return $true }
    Write-VLog 'Desktop size controls gone — hard restart CPL + re-open page'
    if (-not (Ensure-Cpl)) { return $false }
    if (-not (Go-Page 'Adjust desktop size and position')) { return $false }
    Start-Sleep -Milliseconds 800
    return [bool](Find-Control -Ids @('327', '9330') -Names @('No scaling') -NameContains 'Override')
}

function Set-NoScaling {
    $el = Find-Control -Ids @('327') -Names @('No scaling', 'No Scaling')
    if (-not $el) { Write-VLog 'No scaling control missing'; return $false }
    if (Click-RectLeft $el 12) {
        Write-VLog 'No scaling'
        Start-Sleep -Milliseconds 400
        return $true
    }
    return $false
}

function Set-PerformScalingOnGpu {
    # Do NOT open combo 9329 in UI automation:
    # - List items are not exposed to UIA
    # - Clicking/typing in it hides Override (9330) and can trash the page tree
    # GPU is forced via NVTweak PerformScalingOn=0 (same keys CPL writes).
    Write-NvtweakScaling
    Write-VLog 'Perform scaling on: GPU via registry (UI combo skipped — unreliable in this CPL build)'
    return $true
}

function Set-OverrideOn {
    # Custom pane checkbox — box is on the LEFT of the label.
    $el = Find-Control -Ids @('9330') -Names @(
        'Override the scaling mode set by games and programs'
    ) -NameContains 'Override the scaling mode'
    if (-not $el) {
        Write-VLog 'Override control missing'
        return $false
    }

    $r = $el.Current.BoundingRectangle
    Write-VLog ("Override control @ {0},{1} {2}x{3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)

    # Click checkbox glyph once on the left (not the text). Double-click risks toggling OFF.
    Focus-Cpl | Out-Null
    $ax = [int]($r.X + 10)
    $ay = [int]($r.Y + $r.Height / 2)
    [OptiHubVideoWin]::Click($ax, $ay)
    Start-Sleep -Milliseconds 500
    Write-VLog "Override: left glyph click @ $ax,$ay"

    # If Apply is still disabled, first click may have missed the box — one retry at +18px only.
    $apply = Find-Control -Names @('Apply')
    $en = $false
    if ($apply) { try { $en = [bool]$apply.Current.IsEnabled } catch { } }
    if (-not $en) {
        Focus-Cpl | Out-Null
        [OptiHubVideoWin]::Click([int]($r.X + 18), $ay)
        Start-Sleep -Milliseconds 400
        Write-VLog 'Override: retry @ +18px (Apply was still disabled)'
    }

    Write-NvtweakScaling
    Write-VLog 'Override ON (UI + registry ScalingOverride=1)'
    return $true
}

function Apply-ScalingForCurrentMonitor([int]$MonIndex, $Point) {
    Write-VLog "---- Configure monitor $MonIndex ----"
    if (-not (Ensure-DesktopSizePage)) {
        Write-VLog "Cannot recover desktop size page for mon $MonIndex"
        return
    }
    if ($Point) {
        Select-Monitor -Index $MonIndex -X ([int]$Point.X) -Y ([int]$Point.Y)
    }

    # Order matters: No scaling first (enables Apply), then Override checkbox, GPU via registry.
    if (-not (Set-NoScaling)) {
        [void](Ensure-DesktopSizePage)
        if ($Point) { Select-Monitor -Index $MonIndex -X ([int]$Point.X) -Y ([int]$Point.Y) }
        [void](Set-NoScaling)
    }

    [void](Set-PerformScalingOnGpu)

    if (-not (Set-OverrideOn)) {
        [void](Ensure-DesktopSizePage)
        if ($Point) { Select-Monitor -Index $MonIndex -X ([int]$Point.X) -Y ([int]$Point.Y) }
        [void](Set-NoScaling)
        [void](Set-OverrideOn)
    }

    [void](Click-ApplyAndKeep)
    Write-NvtweakScaling
    Start-Sleep -Milliseconds 800
    Focus-Cpl | Out-Null
}

# ---- main ----
Write-VLog 'CPL pass: video NVIDIA + per-monitor GPU/No scaling/Override ON'
if (-not (Ensure-Cpl)) {
    Write-VLog 'FATAL: Control Panel did not start'
    exit 1
}

# Video color (per monitor if strip exists)
if (Go-Page 'Adjust video color settings') {
    $pts = Get-MonitorPoints
    if ($null -eq $pts) { $pts = @([pscustomobject]@{ Index = 0; X = 0; Y = 0 }) }
    if ($pts -isnot [System.Array]) { $pts = @($pts) }
    foreach ($pt in $pts) {
        Select-Monitor -Index ([int]$pt.Index) -X ([int]$pt.X) -Y ([int]$pt.Y)
        $root = Get-Root
        $el = Find-ById $root '1302'
        if (-not $el) { $el = Find-ByName $root 'With the NVIDIA settings' }
        if ($el -and (Select-El $el)) { Write-VLog "Video color mon$($pt.Index) -> NVIDIA" }
        else { Write-VLog "Video color mon$($pt.Index) radio missing" }
        [void](Click-ApplyAndKeep)
    }
}

# Video image
if (Go-Page 'Adjust video image settings') {
    $pts = Get-MonitorPoints
    if ($null -eq $pts) { $pts = @([pscustomobject]@{ Index = 0; X = 0; Y = 0 }) }
    if ($pts -isnot [System.Array]) { $pts = @($pts) }
    foreach ($pt in $pts) {
        Select-Monitor -Index ([int]$pt.Index) -X ([int]$pt.X) -Y ([int]$pt.Y)
        $root = Get-Root
        foreach ($id in @('1402', '1409')) {
            $el = Find-ById $root $id
            if ($el) { [void](Select-El $el) }
        }
        try {
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for ($i = 0; $i -lt $all.Count; $i++) {
                $nm = [string]$all.Item($i).Current.Name
                if ($nm -eq 'Use the NVIDIA setting' -or $nm -eq 'With the NVIDIA settings') {
                    [void](Select-El $all.Item($i))
                }
            }
        } catch { }
        Write-VLog "Video image mon$($pt.Index) -> NVIDIA"
        [void](Click-ApplyAndKeep)
    }
}

# Desktop size — THE important fix: each monitor fully configured before next
if (Go-Page 'Adjust desktop size and position') {
    $pts = Get-MonitorPoints
    if ($null -eq $pts) { $pts = @([pscustomobject]@{ Index = 0; X = 0; Y = 0 }) }
    if ($pts -isnot [System.Array]) { $pts = @($pts) }
    Write-VLog "Desktop size: configuring $($pts.Count) monitor(s) one-by-one (select -> No scaling -> Override -> Apply)"
    foreach ($pt in $pts) {
        Apply-ScalingForCurrentMonitor -MonIndex ([int]$pt.Index) -Point $pt
    }
    # No second Override pass — re-clicking the checkbox can toggle it OFF.
    Write-NvtweakScaling
}

Get-Process nvcplui -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-VLog 'CPL pass done (per-monitor GPU + Override)'
exit 0
