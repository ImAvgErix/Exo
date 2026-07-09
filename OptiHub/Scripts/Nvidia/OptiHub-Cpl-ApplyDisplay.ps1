# OptiHub — NVIDIA Control Panel automation
# Maximize window, multi-monitor, highest available BPC, NVIDIA color unlock,
# Apply + Keep after each real change. Skip PhysX/Surround.
# Continues on page failure so later pages still run.
#
# PS7 note: NEVER use @($genericList) — throws "Argument types do not match".
# Use .ToArray() / plain Object[] builders instead.
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
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
  public const int SW_MAXIMIZE = 3;
  public const int SW_RESTORE = 9;
  public const int MOUSEEVENTF_LEFTDOWN = 0x02;
  public const int MOUSEEVENTF_LEFTUP = 0x04;
  public static void Maximize(IntPtr h) {
    if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
    ShowWindow(h, SW_MAXIMIZE);
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(30);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
    System.Threading.Thread.Sleep(20);
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
    if (-not $p) { return $null }
    $h = $p.MainWindowHandle
    [void][OptiHubWin]::BringWindowToTop($h)
    [void][OptiHubWin]::SetForegroundWindow($h)
    [void][OptiHubWin]::Maximize($h)
    Start-Sleep -Milliseconds 80
    return $p
}

function Get-NvcplRoot {
    $p = Focus-Nvcpl
    if (-not $p) { return $null }
    try {
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
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Id)) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Click-El($El) {
    if (-not $El) { return $false }
    Focus-Nvcpl | Out-Null
    Start-Sleep -Milliseconds 40

    # 1) SelectionItem (radios)
    try {
        $sel = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        return $true
    } catch { }

    # 2) Toggle (checkboxes)
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        # caller decides On/Off; for click we just toggle once if off-looking
        $tp.Toggle()
        return $true
    } catch { }

    # 3) Invoke (buttons)
    try {
        $inv = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        return $true
    } catch { }

    # 4) Clickable point
    try {
        $pt = $El.GetClickablePoint()
        [OptiHubWin]::Click([int]$pt.X, [int]$pt.Y)
        return $true
    } catch { }

    # 5) Bounding rect center
    try {
        $r = $El.Current.BoundingRectangle
        if ($r.Width -gt 1 -and $r.Height -gt 1) {
            [OptiHubWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            return $true
        }
    } catch { }
    return $false
}

function Click-RadioOrButton {
    # Prefer SelectionItem without accidental toggle flip-flops
    param($El)
    if (-not $El) { return $false }
    Focus-Nvcpl | Out-Null
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
        if ($r.Width -gt 1 -and $r.Height -gt 1) {
            [OptiHubWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            return $true
        }
    } catch { }
    return $false
}

function Set-ToggleOn($El) {
    if (-not $El) { return $false }
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
            return $true  # already on
        }
        $tp.Toggle()
        return $true
    } catch {
        return (Click-RadioOrButton $El)
    }
}

function Click-NameOrId {
    param($Root, [string[]]$Names = @(), [string[]]$Ids = @())
    foreach ($id in $Ids) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        $el = Find-ById $Root $id
        if ($el -and (Click-RadioOrButton $el)) {
            Write-CplLog "Click id=$id ($($el.Current.Name))"
            return $true
        }
    }
    foreach ($n in $Names) {
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        $el = Find-ByName $Root $n
        if ($el -and (Click-RadioOrButton $el)) {
            Write-CplLog "Click '$n'"
            return $true
        }
    }
    Write-CplLog "MISS [$($Names -join '|')] ids=[$($Ids -join '|')]"
    return $false
}

function Get-OpenListOptions {
    $desk = [System.Windows.Automation.AutomationElement]::RootElement
    $all = $desk.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $seen = @{}
    $result = New-Object System.Collections.Generic.List[string]
    $limit = [Math]::Min(500, $all.Count)
    for ($i = 0; $i -lt $limit; $i++) {
        $n = [string]$all.Item($i).Current.Name
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        if ($n.Length -gt 48) { continue }
        if ($seen.ContainsKey($n)) { continue }
        $seen[$n] = $true
        [void]$result.Add($n)
    }
    # Plain string array — never @($List[T])
    return [string[]]$result.ToArray()
}

function Select-FromCombo {
    param(
        $Root,
        [string[]]$CurrentLabels,
        [string]$WantExact = $null,
        [scriptblock]$PickBest = $null
    )
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

    $target = $WantExact
    if ($PickBest) {
        [void](Click-RadioOrButton $combo)
        Start-Sleep -Milliseconds 500
        $opts = Get-OpenListOptions
        Write-CplLog "Combo options sample: $(($opts | Select-Object -First 20) -join ', ')"
        $target = & $PickBest $opts $cur
        if (-not $target) {
            Write-CplLog 'No suitable option; ESC'
            Focus-Nvcpl | Out-Null
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            return $false
        }
        if ($cur -eq $target) {
            Write-CplLog "Already best/current: $target"
            Focus-Nvcpl | Out-Null
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            return $true
        }
        $item = Find-ByName (Get-NvcplRoot) $target
        if (-not $item) {
            $item = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) $target
        }
        if ($item -and (Click-RadioOrButton $item)) {
            Write-CplLog "Selected '$target' (was '$cur')"
            Start-Sleep -Milliseconds 300
            return $true
        }
        Focus-Nvcpl | Out-Null
        try { $combo.SetFocus() } catch { }
        [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
        foreach ($ch in $target.ToCharArray()) {
            if ($ch -match '\s') { continue }
            [System.Windows.Forms.SendKeys]::SendWait([string]$ch)
            Start-Sleep -Milliseconds 40
        }
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Write-CplLog "Selected '$target' via keyboard"
        Start-Sleep -Milliseconds 250
        return $true
    }

    if ($WantExact -and $cur -eq $WantExact) {
        Write-CplLog "Already '$WantExact' — skip"
        return $true
    }

    [void](Click-RadioOrButton $combo)
    Start-Sleep -Milliseconds 450
    $item = Find-ByName (Get-NvcplRoot) $WantExact
    if (-not $item) {
        $item = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) $WantExact
    }
    if ($item -and (Click-RadioOrButton $item)) {
        Write-CplLog "Selected '$WantExact' (was '$cur')"
        Start-Sleep -Milliseconds 250
        return $true
    }
    Focus-Nvcpl | Out-Null
    try { $combo.SetFocus() } catch { }
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    foreach ($ch in $WantExact.ToCharArray()) {
        if ($ch -match '\s') { continue }
        [System.Windows.Forms.SendKeys]::SendWait([string]$ch)
        Start-Sleep -Milliseconds 40
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Write-CplLog "Selected '$WantExact' via keyboard (was '$cur')"
    Start-Sleep -Milliseconds 250
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
                    $lim = [Math]::Min(60, $nodes.Count)
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
                        if (Click-RadioOrButton $el) {
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
        # Also hammer ENTER on CPL in case dialog is focused there
        Focus-Nvcpl | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 280
    }
    return $clicked
}

function Click-ApplyAndKeep {
    param($Root, [switch]$Force)
    Focus-Nvcpl | Out-Null
    $apply = Find-ByName $Root 'Apply'
    if (-not $apply) {
        Focus-Nvcpl | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('%a')
        Write-CplLog 'Apply via Alt+A'
        Start-Sleep -Milliseconds 250
    } else {
        try {
            if (-not $apply.Current.IsEnabled -and -not $Force) {
                Write-CplLog 'Apply disabled — nothing changed on this page'
                return $false
            }
        } catch { }
        Write-CplLog '>>> APPLY'
        [void](Click-RadioOrButton $apply)
    }
    Start-Sleep -Milliseconds 400
    [void](Confirm-KeepChanges -Seconds 12)
    return $true
}

function Ensure-NvcplRunning {
    # Fresh start so UI state is predictable
    Get-Process -Name 'nvcplui' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $exe = $null
    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '(?i)NVIDIAControlPanel'
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
        )) {
            if (Test-Path -LiteralPath $c) { $exe = $c; break }
        }
    }
    if ($exe) {
        Write-CplLog "Starting CPL: $exe"
        Start-Process -FilePath $exe | Out-Null
    } else {
        Write-CplLog 'Starting CPL via shell:AppsFolder'
        Start-Process 'shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel' -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 3
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while ([datetime]::UtcNow -lt $deadline) {
        $p = Get-NvcplProcess
        if ($p) {
            [void][OptiHubWin]::Maximize($p.MainWindowHandle)
            Focus-Nvcpl | Out-Null
            Start-Sleep -Milliseconds 400
            return $true
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Navigate-Page([string]$PageName) {
    $root = Get-NvcplRoot
    if (-not $root) { return $false }
    Focus-Nvcpl | Out-Null
    $nav = Find-ByName $root $PageName
    if (-not $nav) {
        Write-CplLog "Missing page link: $PageName"
        return $false
    }
    $ok = Click-RadioOrButton $nav
    Write-CplLog "Page -> $PageName ($ok)"
    Start-Sleep -Milliseconds 1100
    Focus-Nvcpl | Out-Null
    return $ok
}

function Get-MonitorClickPoints {
    # Returns plain Object[] of pscustomobjects. NEVER return @($List[T]).
    $root = Get-NvcplRoot
    $strip = Find-ById $root '528'
    $screenCount = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
    $arr = New-Object object[] $screenCount

    if ($strip) {
        try {
            $r = $strip.Current.BoundingRectangle
            if ($r.Width -gt 20 -and $r.Height -gt 20) {
                for ($i = 0; $i -lt $screenCount; $i++) {
                    $x = [int]($r.X + ($r.Width * ($i + 0.5) / $screenCount))
                    $y = [int]($r.Y + $r.Height * 0.55)
                    $arr[$i] = [pscustomobject]@{ Index = $i; X = $x; Y = $y; Label = "strip[$i]" }
                }
                Write-CplLog "Display strip 528: $screenCount slot(s), width=$([int]$r.Width)px"
                return $arr
            }
        } catch {
            Write-CplLog "Strip read failed: $($_.Exception.Message)"
        }
    }

    $arr = @(
        [pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' }
    )
    Write-CplLog 'No display strip — single current display'
    return $arr
}

function Select-MonitorSlot($Point) {
    if (-not $Point) { return }
    if ([int]$Point.X -le 0) { return }
    Focus-Nvcpl | Out-Null
    [OptiHubWin]::Click([int]$Point.X, [int]$Point.Y)
    Write-CplLog "Selected monitor $($Point.Label) @ $($Point.X),$($Point.Y)"
    Start-Sleep -Milliseconds 900
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
    if (-not (Click-NameOrId -Root $root -Names @('Use the advanced 3D image settings') -Ids @('321'))) {
        Write-CplLog 'Advanced 3D control missing'
        return
    }
    Start-Sleep -Milliseconds 400
    Click-ApplyAndKeep -Root (Get-NvcplRoot)
}

function Apply-ChangeResolution {
    if (-not (Navigate-Page 'Change resolution')) { return }
    $slots = Get-MonitorClickPoints
    if ($null -eq $slots) { $slots = @([pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' }) }
    # Ensure we can iterate even if one object returned
    if ($slots -isnot [System.Array]) { $slots = @($slots) }

    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot

        # Unlock color dropdowns — always select NVIDIA color
        if (Click-NameOrId -Root $root -Names @('Use NVIDIA color settings') -Ids @('34606')) {
            Write-CplLog 'Use NVIDIA color settings'
            Start-Sleep -Milliseconds 700
        } else {
            Write-CplLog 'WARN: NVIDIA color radio not found — dropdowns may stay locked'
        }

        $root = Get-NvcplRoot
        [void](Select-FromCombo -Root $root -CurrentLabels @('RGB', 'YCbCr420', 'YCbCr422', 'YCbCr444') -WantExact 'RGB')
        [void](Select-FromCombo -Root $root -CurrentLabels @('Full', 'Limited') -WantExact 'Full')
        [void](Select-FromCombo -Root $root -CurrentLabels @('8 bpc', '10 bpc', '12 bpc', '6 bpc', '16 bpc') -PickBest {
            param($opts, $cur)
            $bpc = New-Object System.Collections.Generic.List[int]
            foreach ($o in $opts) {
                if ($o -match '^\s*(\d+)\s*bpc\s*$') { [void]$bpc.Add([int]$Matches[1]) }
            }
            if ($bpc.Count -eq 0) {
                foreach ($try in @(16, 12, 10, 8, 6)) {
                    $label = "$try bpc"
                    if ($cur -eq $label) { return $label }
                }
                return $cur
            }
            $best = ($bpc.ToArray() | Measure-Object -Maximum).Maximum
            return "$best bpc"
        })

        Click-ApplyAndKeep -Root (Get-NvcplRoot)
    }
}

function Apply-DesktopSizePosition {
    if (-not (Navigate-Page 'Adjust desktop size and position')) { return }
    $slots = Get-MonitorClickPoints
    if ($null -eq $slots) { $slots = @([pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' }) }
    if ($slots -isnot [System.Array]) { $slots = @($slots) }

    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot

        if (Click-NameOrId -Root $root -Names @('No scaling', 'No Scaling') -Ids @('327')) {
            Write-CplLog 'No scaling'
        }
        Start-Sleep -Milliseconds 300

        # Perform scaling on: GPU (combo AutomationId 9329)
        $scaleCombo = Find-ById $root '9329'
        if ($scaleCombo) {
            $curName = [string]$scaleCombo.Current.Name
            if ($curName -match '(?i)\bGPU\b') {
                Write-CplLog "Perform scaling on already: $curName"
            } else {
                [void](Click-RadioOrButton $scaleCombo)
                Start-Sleep -Milliseconds 400
                $gpu = Find-ByName (Get-NvcplRoot) 'GPU'
                if (-not $gpu) {
                    $gpu = Find-ByName ([System.Windows.Automation.AutomationElement]::RootElement) 'GPU'
                }
                if ($gpu) {
                    [void](Click-RadioOrButton $gpu)
                    Write-CplLog 'Perform scaling on: GPU'
                } else {
                    Focus-Nvcpl | Out-Null
                    [System.Windows.Forms.SendKeys]::SendWait('g')
                    Start-Sleep -Milliseconds 80
                    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                    Write-CplLog 'Perform scaling on: GPU (keys)'
                }
            }
        } else {
            Write-CplLog 'Scaling combo 9329 not found'
        }

        $ovr = Find-ById $root '9330'
        if (-not $ovr) {
            $ovr = Find-ByName $root 'Override the scaling mode set by games and programs'
        }
        if ($ovr) {
            if (Set-ToggleOn $ovr) { Write-CplLog 'Override = On' }
        }

        Click-ApplyAndKeep -Root (Get-NvcplRoot)
    }
}

function Apply-VideoColor {
    if (-not (Navigate-Page 'Adjust video color settings')) { return }
    $slots = Get-MonitorClickPoints
    if ($null -eq $slots) { $slots = @([pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' }) }
    if ($slots -isnot [System.Array]) { $slots = @($slots) }

    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot
        if (Click-NameOrId -Root $root -Names @('With the NVIDIA settings') -Ids @('1302')) {
            Write-CplLog 'Video color -> NVIDIA'
        }
        Click-ApplyAndKeep -Root (Get-NvcplRoot)
    }
}

function Apply-VideoImage {
    if (-not (Navigate-Page 'Adjust video image settings')) { return }
    $slots = Get-MonitorClickPoints
    if ($null -eq $slots) { $slots = @([pscustomobject]@{ Index = 0; X = 0; Y = 0; Label = 'current' }) }
    if ($slots -isnot [System.Array]) { $slots = @($slots) }

    foreach ($slot in $slots) {
        Select-MonitorSlot $slot
        $root = Get-NvcplRoot
        [void](Click-NameOrId -Root $root -Ids @('1402') -Names @())
        Start-Sleep -Milliseconds 150
        [void](Click-NameOrId -Root $root -Ids @('1409') -Names @())
        # All "Use the NVIDIA setting" radios on this page
        try {
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for ($i = 0; $i -lt $all.Count; $i++) {
                if ($all.Item($i).Current.Name -eq 'Use the NVIDIA setting') {
                    [void](Click-RadioOrButton $all.Item($i))
                    Start-Sleep -Milliseconds 80
                }
            }
        } catch { }
        Write-CplLog 'Video image -> NVIDIA settings'
        Click-ApplyAndKeep -Root (Get-NvcplRoot)
    }
}

# ---------------- main ----------------
Write-CplLog 'CPL start (max window, multi-mon, highest BPC, Apply+Keep, no PhysX)'
if (-not (Ensure-NvcplRunning)) {
    Write-CplLog 'FATAL: cannot start Control Panel'
    exit 1
}

# EULA
$desk = [System.Windows.Automation.AutomationElement]::RootElement
foreach ($n in @('Agree and continue', 'Agree', 'Continue', 'I Agree', 'Accept')) {
    $el = Find-ByName $desk $n
    if ($el) {
        [void](Click-RadioOrButton $el)
        Write-CplLog "EULA $n"
        Start-Sleep 1
    }
}

Focus-Nvcpl | Out-Null

Invoke-Safe '3D image settings' { Apply-3DImageSettings }
# PhysX / Surround intentionally skipped
Invoke-Safe 'Change resolution / color' { Apply-ChangeResolution }
Invoke-Safe 'Desktop size and position' { Apply-DesktopSizePosition }
Invoke-Safe 'Video color' { Apply-VideoColor }
Invoke-Safe 'Video image' { Apply-VideoImage }

Write-CplLog 'CPL pass complete'
exit 0
