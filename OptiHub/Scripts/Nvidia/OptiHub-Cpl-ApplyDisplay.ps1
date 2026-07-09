# OptiHub — NVIDIA Control Panel automation (robust)
# Maximize CPL, skip PhysX/Surround, per-monitor color+scaling, highest BPC,
# skip already-correct controls, Apply + Keep after every page that changes.
# Continues on failure so later pages still run.
$ErrorActionPreference = 'Continue'

function Write-CplLog([string]$Msg) {
    $line = "[CPL] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not ('OptiHubWin' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);
  public const int SW_MAXIMIZE = 3;
  public const int MOUSEEVENTF_LEFTDOWN = 0x02;
  public const int MOUSEEVENTF_LEFTUP = 0x04;
  public static void Maximize(IntPtr h) { ShowWindow(h, SW_MAXIMIZE); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
  }
}
"@
}

function Get-NvcplProcess {
    Get-Process -Name 'nvcplui' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Focus-Nvcpl {
    $p = Get-NvcplProcess
    if ($p) {
        [void][OptiHubWin]::SetForegroundWindow($p.MainWindowHandle)
        [void][OptiHubWin]::Maximize($p.MainWindowHandle)
    }
}

function Get-NvcplRoot {
    $p = Get-NvcplProcess
    if (-not $p) { return $null }
    try {
        [void][OptiHubWin]::Maximize($p.MainWindowHandle)
        return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    } catch { return $null }
}

function Find-ByName($Root, [string]$Name) {
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Name)) { return $null }
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

function Click-El($El) {
    if (-not $El) { return $false }
    Focus-Nvcpl
    Start-Sleep -Milliseconds 50
    try {
        $inv = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke(); return $true
    } catch { }
    try {
        $pt = $El.GetClickablePoint()
        [OptiHubWin]::Click([int]$pt.X, [int]$pt.Y); return $true
    } catch { }
    try {
        $r = $El.Current.BoundingRectangle
        if ($r.Width -gt 1 -and $r.Height -gt 1) {
            [OptiHubWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            return $true
        }
    } catch { }
    return $false
}

function Click-NameOrId {
    param($Root, [string[]]$Names = @(), [string[]]$Ids = @())
    foreach ($id in $Ids) {
        $el = Find-ById $Root $id
        if ($el -and (Click-El $el)) { Write-CplLog "Click id=$id ($($el.Current.Name))"; return $true }
    }
    foreach ($n in $Names) {
        $el = Find-ByName $Root $n
        if ($el -and (Click-El $el)) { Write-CplLog "Click '$n'"; return $true }
    }
    return $false
}

function Test-NamePresent($Root, [string]$Name) {
    return [bool](Find-ByName $Root $Name)
}

function Get-OpenListOptions {
    # After opening a dropdown, collect option names from desktop
    $desk = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $desk.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $opts = New-Object System.Collections.Generic.List[string]
    $limit = [Math]::Min(400, $all.Count)
    for ($i = 0; $i -lt $limit; $i++) {
        $n = [string]$all.Item($i).Current.Name
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        if ($n.Length -gt 40) { continue }
        [void]$opts.Add($n)
    }
    return @($opts | Select-Object -Unique)
}

function Select-FromCombo {
    param($Root, [string[]]$CurrentLabels, [string]$WantExact = $null, [scriptblock]$PickBest = $null)
    $combo = $null
    $cur = $null
    foreach ($c in $CurrentLabels) {
        $el = Find-ByName $Root $c
        if ($el) { $combo = $el; $cur = $c; break }
    }
    if (-not $combo) {
        Write-CplLog "Combo not found (looking for: $($CurrentLabels -join ', '))"
        return $false
    }

    # Determine target
    $target = $WantExact
    if ($PickBest) {
        # open list to inspect options
        [void](Click-El $combo)
        Start-Sleep -Milliseconds 450
        $opts = Get-OpenListOptions
        $target = & $PickBest $opts $cur
        if (-not $target) {
            Write-CplLog "No suitable option in list; closing"
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            return $false
        }
        if ($cur -eq $target) {
            Write-CplLog "Already best/current: $target"
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            return $true
        }
        # list still open - click target
        $item = Find-ByName (Get-NvcplRoot) $target
        if (-not $item) { $item = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) $target }
        if ($item -and (Click-El $item)) {
            Write-CplLog "Selected '$target' (was '$cur')"
            Start-Sleep -Milliseconds 250
            return $true
        }
        # keyboard fallback
        Focus-Nvcpl
        try { $combo.SetFocus() } catch { }
        [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
        foreach ($ch in $target.ToCharArray()) {
            if ($ch -match '\s') { continue }
            [System.Windows.Forms.SendKeys]::SendWait([string]$ch)
            Start-Sleep -Milliseconds 30
        }
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Write-CplLog "Selected '$target' via keyboard"
        Start-Sleep -Milliseconds 200
        return $true
    }

    if ($WantExact -and $cur -eq $WantExact) {
        Write-CplLog "Already '$WantExact' — skip"
        return $true
    }

    [void](Click-El $combo)
    Start-Sleep -Milliseconds 400
    $item = Find-ByName (Get-NvcplRoot) $WantExact
    if (-not $item) { $item = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) $WantExact }
    if ($item -and (Click-El $item)) {
        Write-CplLog "Selected '$WantExact' (was '$cur')"
        Start-Sleep -Milliseconds 250
        return $true
    }
    Focus-Nvcpl
    try { $combo.SetFocus() } catch { }
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    foreach ($ch in $WantExact.ToCharArray()) {
        if ($ch -match '\s') { continue }
        [System.Windows.Forms.SendKeys]::SendWait([string]$ch)
        Start-Sleep -Milliseconds 30
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Write-CplLog "Selected '$WantExact' via keyboard"
    Start-Sleep -Milliseconds 200
    return $true
}

function Confirm-KeepChanges([int]$Seconds = 12) {
    $deadline = [datetime]::UtcNow.AddSeconds($Seconds)
    $clicked = $false
    Write-CplLog "Waiting for Keep/Yes dialog (${Seconds}s)..."
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $desk = [System.Windows.Automation.AutomationElement]::RootElement
            $winCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)
            $windows = $desk.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)
            for ($wi = 0; $wi -lt $windows.Count; $wi++) {
                $win = $windows.Item($wi)
                $blob = [string]$win.Current.Name
                try {
                    $nodes = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition)
                    $lim = [Math]::Min(50, $nodes.Count)
                    for ($ti = 0; $ti -lt $lim; $ti++) {
                        $tn = [string]$nodes.Item($ti).Current.Name
                        if ($tn) { $blob += " $tn" }
                    }
                } catch { }
                if ($blob -notmatch '(?i)keep these|do you want to keep|revert|display settings|seconds remaining|keep changes') {
                    continue
                }
                foreach ($n in @('Yes', 'Keep changes', 'Keep Changes', 'Keep these settings', 'Keep', 'OK', '&Yes')) {
                    $el = Find-ByName $win $n
                    if (-not $el) { $el = Find-ByName $desk $n }
                    if ($el -and $el.Current.IsEnabled -and $el.Current.Name -notmatch '(?i)^no$|revert') {
                        if (Click-El $el) {
                            Write-CplLog "KEEP via '$($el.Current.Name)'"
                            $clicked = $true
                            Start-Sleep -Milliseconds 400
                        }
                    }
                }
                try { $win.SetFocus() } catch { }
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                Write-CplLog 'KEEP via ENTER'
                $clicked = $true
                Start-Sleep -Milliseconds 300
            }
        } catch { }
        Start-Sleep -Milliseconds 280
    }
    return $clicked
}

function Click-ApplyAndKeep {
    param($Root, [switch]$Force)
    Focus-Nvcpl
    $apply = Find-ByName $Root 'Apply'
    if (-not $apply) {
        Focus-Nvcpl
        [System.Windows.Forms.SendKeys]::SendWait('%a')
        Start-Sleep -Milliseconds 200
    } else {
        if (-not $apply.Current.IsEnabled -and -not $Force) {
            Write-CplLog 'Apply disabled — page already correct, skip keep wait'
            return $false
        }
        Write-CplLog 'Apply...'
        [void](Click-El $apply)
    }
    Start-Sleep -Milliseconds 350
    [void](Confirm-KeepChanges -Seconds 12)
    return $true
}

function Ensure-NvcplRunning {
    $exe = $null
    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)^NVIDIACorp\.NVIDIAControlPanel$'
    } | Select-Object -First 1
    if ($pkg) {
        $hit = Get-ChildItem -LiteralPath $pkg.InstallLocation -Recurse -Filter 'nvcplui.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { $exe = $hit.FullName }
    }
    if (-not $exe) {
        foreach ($c in @(
            (Join-Path $env:ProgramFiles 'NVIDIA Corporation\Control Panel Client\nvcplui.exe'),
            (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe')
        )) { if (Test-Path $c) { $exe = $c; break } }
    }
    if (-not (Get-NvcplProcess)) {
        if ($exe) { Start-Process -FilePath $exe | Out-Null }
        else {
            Start-Process 'shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel' -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 3
    }
    $p = Get-NvcplProcess
    if ($p) {
        [void][OptiHubWin]::Maximize($p.MainWindowHandle)
        Focus-Nvcpl
        Start-Sleep -Milliseconds 400
        [void][OptiHubWin]::Maximize($p.MainWindowHandle)
    }
    return [bool]$p
}

function Navigate-Page([string]$PageName) {
    $root = Get-NvcplRoot
    if (-not $root) { return $false }
    Focus-Nvcpl
    $nav = Find-ByName $root $PageName
    if (-not $nav) { Write-CplLog "Missing page: $PageName"; return $false }
    $ok = Click-El $nav
    Write-CplLog "Page -> $PageName ($ok)"
    Start-Sleep -Milliseconds 1000
    Focus-Nvcpl
    return $ok
}

function Get-MonitorClickPoints {
    # Display icons live in pane AutomationId=528 under Change resolution / size pages
    $root = Get-NvcplRoot
    $strip = Find-ById $root '528'
    $screenCount = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
    $points = New-Object System.Collections.Generic.List[object]

    if ($strip) {
        $r = $strip.Current.BoundingRectangle
        if ($r.Width -gt 20 -and $r.Height -gt 20) {
            for ($i = 0; $i -lt $screenCount; $i++) {
                # evenly spaced clicks across the strip
                $x = [int]($r.X + ($r.Width * ($i + 0.5) / $screenCount))
                $y = [int]($r.Y + $r.Height / 2)
                [void]$points.Add([pscustomobject]@{ Index = $i; X = $x; Y = $y; Label = "strip[$i]" })
            }
            Write-CplLog "Display strip 528: $screenCount slot(s) across ${([int]$r.Width)}px"
            return @($points)
        }
    }

    # Fallback: single current display
    [void]$points.Add([pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' })
    Write-CplLog 'No display strip — using current display only'
    return @($points)
}

function Select-MonitorSlot($Point) {
    if ($Point.X -le 0) { return }
    Focus-Nvcpl
    [OptiHubWin]::Click([int]$Point.X, [int]$Point.Y)
    Write-CplLog "Selected monitor $($Point.Label) @ $($Point.X),$($Point.Y)"
    Start-Sleep -Milliseconds 800
}

function Invoke-Safe([string]$Label, [scriptblock]$Body) {
    try {
        Write-CplLog "BEGIN $Label"
        & $Body
        Write-CplLog "END $Label OK"
    } catch {
        Write-CplLog "END $Label FAIL: $($_.Exception.Message)"
    }
}

# -------- pages --------

function Apply-3DImageSettings {
    if (-not (Navigate-Page 'Adjust image settings with preview')) { return }
    $root = Get-NvcplRoot
    # Already advanced?
    # If "Let the 3D application decide" is selected we need advanced. Hard to know selection state —
    # click advanced 3D always (id 321) unless already the active path; clicking again is fine.
    $need = $true
    # Prefer advanced 3D (uses Manage 3D / our profile) = "use my 3D settings"
    if ($need) {
        if (-not (Click-NameOrId -Root $root -Names @('Use the advanced 3D image settings') -Ids @('321'))) {
            Write-CplLog 'Advanced 3D control missing'
            return
        }
    }
    $root = Get-NvcplRoot
    Click-ApplyAndKeep -Root $root
}

function Apply-ChangeResolution {
    if (-not (Navigate-Page 'Change resolution')) { return }
    $slots = @(Get-MonitorClickPoints)
    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot

        # Unlock: Use NVIDIA color settings (skip if already implied by enabled Full/RGB controls and nvidia radio)
        $nvidia = Find-ById $root '34606'
        if (-not $nvidia) { $nvidia = Find-ByName $root 'Use NVIDIA color settings' }
        $default = Find-ById $root '34605'
        if (-not $default) { $default = Find-ByName $root 'Use default color settings' }

        # Always click NVIDIA color to unlock dropdowns (required before Full/bpc work)
        if ($nvidia) {
            [void](Click-El $nvidia)
            Write-CplLog 'Use NVIDIA color settings'
            Start-Sleep -Milliseconds 600
        }

        $root = Get-NvcplRoot
        # RGB
        [void](Select-FromCombo -Root $root -CurrentLabels @('RGB', 'YCbCr420', 'YCbCr422', 'YCbCr444') -WantExact 'RGB')
        # Full
        [void](Select-FromCombo -Root $root -CurrentLabels @('Full', 'Limited') -WantExact 'Full')
        # Highest BPC available
        [void](Select-FromCombo -Root $root -CurrentLabels @('8 bpc', '10 bpc', '12 bpc', '6 bpc', '16 bpc') -PickBest {
            param($opts, $cur)
            $bpc = @()
            foreach ($o in $opts) {
                if ($o -match '^\s*(\d+)\s*bpc\s*$') { $bpc += [int]$Matches[1] }
            }
            if ($bpc.Count -eq 0) {
                # list might only show current; try known descending
                foreach ($try in @(16, 12, 10, 8, 6)) {
                    $label = "$try bpc"
                    if ($opts -contains $label -or $cur -eq $label) { return $label }
                }
                return $null
            }
            $best = ($bpc | Measure-Object -Maximum).Maximum
            return "$best bpc"
        })

        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-DesktopSizePosition {
    if (-not (Navigate-Page 'Adjust desktop size and position')) { return }
    $slots = @(Get-MonitorClickPoints)
    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot

        # No scaling (skip if already selected - click anyway if Apply enables)
        $noScale = Find-ById $root '327'
        if (-not $noScale) { $noScale = Find-ByName $root 'No scaling' }
        if ($noScale) { [void](Click-El $noScale); Write-CplLog 'No scaling' }
        Start-Sleep -Milliseconds 250

        # Perform scaling on: GPU (combo 9329)
        $scaleCombo = Find-ById $root '9329'
        if ($scaleCombo) {
            $curName = [string]$scaleCombo.Current.Name
            if ($curName -match '(?i)GPU') {
                Write-CplLog "Perform scaling on already: $curName"
            }
            [void](Click-El $scaleCombo)
            Start-Sleep -Milliseconds 400
            $gpu = Find-ByName (Get-NvcplRoot) 'GPU'
            if (-not $gpu) { $gpu = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) 'GPU' }
            if ($gpu) { [void](Click-El $gpu); Write-CplLog 'Perform scaling on: GPU' }
            else {
                Focus-Nvcpl
                [System.Windows.Forms.SendKeys]::SendWait('g')
                Start-Sleep -Milliseconds 80
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                Write-CplLog 'Perform scaling on: GPU (keys)'
            }
        }

        # Override checkbox 9330
        $ovr = Find-ById $root '9330'
        if (-not $ovr) { $ovr = Find-ByName $root 'Override the scaling mode set by games and programs' }
        if ($ovr) {
            $alreadyOn = $false
            try {
                $tp = $ovr.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $alreadyOn = $true }
                else { $tp.Toggle() }
            } catch {
                [void](Click-El $ovr)
            }
            if ($alreadyOn) { Write-CplLog 'Override already On — skip toggle' }
            else { Write-CplLog 'Override = On' }
        }

        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-VideoColor {
    if (-not (Navigate-Page 'Adjust video color settings')) { return }
    $slots = @(Get-MonitorClickPoints)
    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot
        # Skip if already NVIDIA
        # Click NVIDIA settings (id 1302)
        if (Click-NameOrId -Root $root -Names @('With the NVIDIA settings') -Ids @('1302')) {
            Write-CplLog 'Video color -> NVIDIA'
        }
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-VideoImage {
    if (-not (Navigate-Page 'Adjust video image settings')) { return }
    $slots = @(Get-MonitorClickPoints)
    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot
        # Edge enhancement + noise reduction NVIDIA (ids 1402, 1409)
        [void](Click-NameOrId -Root $root -Ids @('1402') -Names @())
        Start-Sleep -Milliseconds 150
        [void](Click-NameOrId -Root $root -Ids @('1409') -Names @())
        # click all "Use the NVIDIA setting" radios
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            if ($all.Item($i).Current.Name -eq 'Use the NVIDIA setting') {
                [void](Click-El $all.Item($i))
                Start-Sleep -Milliseconds 100
            }
        }
        Write-CplLog 'Video image -> NVIDIA settings'
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

# ---------------- main ----------------
Write-CplLog 'FULL CPL pass (max window, multi-monitor, highest BPC, Apply+Keep, skip PhysX)...'
if (-not (Ensure-NvcplRunning)) {
    Write-CplLog 'FATAL: cannot start Control Panel'
    exit 1
}

# EULA
$desk = [System.Windows.Automation.AutomationElement]::RootElement
foreach ($n in @('Agree and continue', 'Agree', 'Continue', 'I Agree', 'Accept')) {
    $el = Find-ByName $desk $n
    if ($el) { [void](Click-El $el); Write-CplLog "EULA $n"; Start-Sleep 1 }
}

# Re-max after EULA
$p = Get-NvcplProcess
if ($p) { [void][OptiHubWin]::Maximize($p.MainWindowHandle) }

Invoke-Safe '3D image settings' { Apply-3DImageSettings }
# PhysX / Surround intentionally skipped
Invoke-Safe 'Change resolution / color' { Apply-ChangeResolution }
Invoke-Safe 'Desktop size and position' { Apply-DesktopSizePosition }
Invoke-Safe 'Video color' { Apply-VideoColor }
Invoke-Safe 'Video image' { Apply-VideoImage }

Write-CplLog 'FULL CPL pass complete'
exit 0
