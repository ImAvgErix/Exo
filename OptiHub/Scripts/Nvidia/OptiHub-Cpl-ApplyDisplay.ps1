# OptiHub — full NVIDIA Control Panel automation.
# Every page: change settings -> Apply -> confirm Keep/Yes (don't let them revert).
#
# Pages:
#  1) Adjust image settings -> Use the advanced 3D image settings
#  2) Configure Surround, PhysX -> Processor = NVIDIA GPU
#  3) Change resolution -> Use NVIDIA color settings + RGB + Full + 10 bpc (per display)
#  4) Adjust desktop size and position -> No scaling + GPU + Override (per display)
#  5) Adjust video color settings -> With the NVIDIA settings
#  6) Adjust video image settings -> Use the NVIDIA setting
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

if (-not ('OptiHubMouse' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubMouse {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);
  public const int MOUSEEVENTF_LEFTDOWN = 0x02;
  public const int MOUSEEVENTF_LEFTUP = 0x04;
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

function Get-NvcplRoot {
    $p = Get-NvcplProcess
    if (-not $p) { return $null }
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
    catch { return $null }
}

function Focus-Nvcpl {
    $p = Get-NvcplProcess
    if ($p) { [void][OptiHubMouse]::SetForegroundWindow($p.MainWindowHandle) }
}

function Find-ElByName($Root, [string]$Name) {
    if (-not $Root -or [string]::IsNullOrWhiteSpace($Name)) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ElById($Root, [string]$Id) {
    if (-not $Root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-El($El) {
    if (-not $El) { return $false }
    Focus-Nvcpl
    Start-Sleep -Milliseconds 60
    try {
        $inv = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        return $true
    } catch { }
    try {
        $pt = $El.GetClickablePoint()
        [OptiHubMouse]::Click([int]$pt.X, [int]$pt.Y)
        return $true
    } catch { }
    try {
        $rect = $El.Current.BoundingRectangle
        if ($rect.Width -gt 1 -and $rect.Height -gt 1) {
            [OptiHubMouse]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
            return $true
        }
    } catch { }
    return $false
}

function Click-ByNameOrId {
    param($Root, [string[]]$Names = @(), [string[]]$Ids = @())
    foreach ($id in $Ids) {
        $el = Find-ElById $Root $id
        if ($el -and (Click-El $el)) { Write-CplLog "Clicked id=$id ($($el.Current.Name))"; return $true }
    }
    foreach ($n in $Names) {
        $el = Find-ElByName $Root $n
        if ($el -and (Click-El $el)) { Write-CplLog "Clicked name='$n'"; return $true }
    }
    return $false
}

function Select-ComboOption {
    param($Root, [string[]]$CurrentCandidates, [string]$WantName)
    $combo = $null
    $current = $null
    foreach ($c in $CurrentCandidates) {
        $el = Find-ElByName $Root $c
        if ($el) { $combo = $el; $current = $c; break }
    }
    if (-not $combo) {
        # already want?
        if (Find-ElByName $Root $WantName) { Write-CplLog "Already '$WantName'"; return $true }
        Write-CplLog "Combo not found for want='$WantName' (cands: $($CurrentCandidates -join ','))"
        return $false
    }
    if ($current -eq $WantName) { Write-CplLog "Already '$WantName'"; return $true }

    if (-not (Click-El $combo)) { Write-CplLog "Open combo '$current' failed"; return $false }
    Start-Sleep -Milliseconds 400

    $root2 = Get-NvcplRoot
    $item = Find-ElByName $root2 $WantName
    if (-not $item) {
        try {
            $desk = [System.Windows.Automation.AutomationElement]::RootElement
            $item = Find-ElByName $desk $WantName
        } catch { }
    }
    if ($item -and (Click-El $item)) {
        Write-CplLog "Selected '$WantName'"
        Start-Sleep -Milliseconds 250
        return $true
    }

    # keyboard
    try {
        Focus-Nvcpl
        $combo.SetFocus()
        Start-Sleep -Milliseconds 80
        [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
        foreach ($ch in $WantName.ToCharArray()) {
            if ($ch -match '\s') { continue }
            [System.Windows.Forms.SendKeys]::SendWait([string]$ch)
            Start-Sleep -Milliseconds 35
        }
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Write-CplLog "Selected '$WantName' via keyboard"
        Start-Sleep -Milliseconds 200
        return $true
    } catch {
        Write-CplLog "Failed to select '$WantName'"
        return $false
    }
}

function Confirm-KeepChanges {
    param([int]$Seconds = 14)
    # Must click Keep/Yes or Windows/NVIDIA reverts the change.
    $deadline = [datetime]::UtcNow.AddSeconds($Seconds)
    $clicked = $false
    Write-CplLog "Waiting for Keep/Yes (do not revert) up to ${Seconds}s..."
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $desk = [System.Windows.Automation.AutomationElement]::RootElement
            $winCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)
            $windows = $desk.FindAll([System.Windows.Automation.TreeScope]::Children, $winCond)
            for ($wi = 0; $wi -lt $windows.Count; $wi++) {
                $win = $windows.Item($wi)
                $wname = [string]$win.Current.Name
                $blob = $wname
                try {
                    $nodes = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition)
                    $lim = [Math]::Min(60, $nodes.Count)
                    for ($ti = 0; $ti -lt $lim; $ti++) {
                        $tn = [string]$nodes.Item($ti).Current.Name
                        if ($tn) { $blob += ' ' + $tn }
                    }
                } catch { }

                $isConfirm = $blob -match '(?i)keep these|do you want to keep|revert|display settings|seconds remaining|keep changes'
                if (-not $isConfirm -and $wname -notmatch '(?i)nvidia|display') { continue }

                foreach ($n in @('Yes', 'Keep changes', 'Keep Changes', 'Keep these settings', 'Keep', 'OK', '&Yes')) {
                    $el = Find-ElByName $win $n
                    if (-not $el) { $el = Find-ElByName $desk $n }
                    if ($el -and $el.Current.IsEnabled -and $el.Current.Name -notmatch '(?i)^no$|revert') {
                        if (Click-El $el) {
                            Write-CplLog "KEEP confirmed via '$($el.Current.Name)' ($wname)"
                            $clicked = $true
                            Start-Sleep -Milliseconds 450
                        }
                    }
                }
                if ($isConfirm) {
                    try { $win.SetFocus() } catch { }
                    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                    Write-CplLog "KEEP via ENTER on confirm dialog ($wname)"
                    $clicked = $true
                    Start-Sleep -Milliseconds 350
                }
            }
        } catch { }
        Start-Sleep -Milliseconds 280
    }
    if (-not $clicked) { Write-CplLog 'No Keep dialog appeared (Apply may have been disabled)' }
    return $clicked
}

function Click-ApplyAndKeep {
    param($Root)
    Focus-Nvcpl
    $apply = Find-ElByName $Root 'Apply'
    if (-not $apply) {
        $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            $e = $all.Item($i)
            if ($e.Current.Name -eq 'Apply') { $apply = $e; break }
        }
    }
    if ($apply) {
        if ($apply.Current.IsEnabled) {
            Write-CplLog 'Clicking Apply...'
            [void](Click-El $apply)
        } else {
            Write-CplLog 'Apply disabled (no change pending on this page)'
            return $false
        }
    } else {
        Write-CplLog 'Apply not found — Alt+A then Enter'
        Focus-Nvcpl
        [System.Windows.Forms.SendKeys]::SendWait('%a')
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    Start-Sleep -Milliseconds 400
    [void](Confirm-KeepChanges -Seconds 14)
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
    Focus-Nvcpl
    return [bool](Get-NvcplProcess)
}

function Navigate-Page([string]$PageName) {
    $root = Get-NvcplRoot
    if (-not $root) { return $false }
    $nav = Find-ElByName $root $PageName
    if (-not $nav) { Write-CplLog "Page missing: $PageName"; return $false }
    $ok = Click-El $nav
    Write-CplLog "Navigate -> $PageName ($ok)"
    Start-Sleep -Milliseconds 900
    return $ok
}

function Get-DisplaySelectors($Root) {
    $found = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        $n = [string]$e.Current.Name
        if ([string]::IsNullOrWhiteSpace($n)) { continue }
        if ($n -match '(?i)select the display|connector:|resolution:|refresh rate:|apply the following|how do you') { continue }
        # list-style displays: "1. ...", model names, or connector lines
        $isDisp = $n -match '^\d+\.\s' -or
            $n -match '(?i)\(NVIDIA|GeForce|RTX|GTX|DisplayPort|HDMI' -or
            ($n -match '(?i)monitor|display' -and $n.Length -lt 80)
        if (-not $isDisp) { continue }
        if ($seen.ContainsKey($n)) { continue }
        $seen[$n] = $true
        [void]$found.Add($e)
    }
    return @($found)
}

function For-EachDisplay {
    param([scriptblock]$Action)
    $root = Get-NvcplRoot
    $displays = @(Get-DisplaySelectors $root)
    if ($displays.Count -eq 0) {
        Write-CplLog 'Single/current display only'
        & $Action
        return
    }
    $i = 0
    foreach ($d in $displays) {
        $i++
        Write-CplLog "--- Display $i : $($d.Current.Name) ---"
        [void](Click-El $d)
        Start-Sleep -Milliseconds 750
        & $Action
    }
}

# ---------------- pages ----------------

function Apply-Page-3DImageSettings {
    Write-CplLog '=== Adjust image settings with preview ==='
    if (-not (Navigate-Page 'Adjust image settings with preview')) { return }
    $root = Get-NvcplRoot
    # "Use my 3D settings" path = advanced 3D image settings (our NIP / Manage 3D)
    $ok = Click-ByNameOrId -Root $root `
        -Names @('Use the advanced 3D image settings') `
        -Ids @('321')
    if (-not $ok) {
        # fallback preference emphasizing Performance
        [void](Click-ByNameOrId -Root $root -Names @('Use my preference emphasizing:') -Ids @('322'))
        [void](Click-ByNameOrId -Root $root -Names @('Performance') -Ids @('468', '298'))
    }
    $root = Get-NvcplRoot
    Click-ApplyAndKeep -Root $root
}

function Apply-Page-PhysX {
    Write-CplLog '=== Configure Surround, PhysX ==='
    if (-not (Navigate-Page 'Configure Surround, PhysX')) { return }
    $root = Get-NvcplRoot
    # Processor combo is often AutomationId 1515 (garbled name)
    $combo = Find-ElById $root '1515'
    if (-not $combo) {
        # find near "Processor:"
        $procLabel = Find-ElByName $root 'Processor:'
        if ($procLabel) {
            # try next siblings via search of combos on page
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for ($i = 0; $i -lt $all.Count; $i++) {
                $e = $all.Item($i)
                if ($e.Current.ControlType.ProgrammaticName -match 'ComboBox' -or $e.Current.AutomationId -eq '1515') {
                    $combo = $e; break
                }
                # pane that acts as combo
                if ($e.Current.AutomationId -eq '1515') { $combo = $e; break }
            }
        }
    }
    if ($combo) {
        [void](Click-El $combo)
        Start-Sleep -Milliseconds 400
        $root2 = Get-NvcplRoot
        $desk = [System.Windows.Automation.AutomationElement]::RootElement
        $picked = $false
        foreach ($scope in @($root2, $desk)) {
            $all = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for ($i = 0; $i -lt [Math]::Min($all.Count, 200); $i++) {
                $n = [string]$all.Item($i).Current.Name
                if ($n -match '(?i)GeForce|RTX|GTX|NVIDIA' -and $n -notmatch '(?i)auto-select|CPU|Intel|AMD') {
                    if (Click-El $all.Item($i)) {
                        Write-CplLog "PhysX processor set to GPU: $n"
                        $picked = $true
                        break
                    }
                }
            }
            if ($picked) { break }
        }
        if (-not $picked) {
            # Auto-select is OK if only one GPU
            $auto = Find-ElByName $root2 'Auto-select'
            if (-not $auto) { $auto = Find-ElByName $desk 'Auto-select' }
            if ($auto) { [void](Click-El $auto); Write-CplLog 'PhysX Auto-select' }
            else { Write-CplLog 'Could not set PhysX GPU explicitly' }
        }
    } else {
        Write-CplLog 'PhysX processor control not found'
    }
    Start-Sleep -Milliseconds 300
    $root = Get-NvcplRoot
    Click-ApplyAndKeep -Root $root
}

function Apply-Page-ChangeResolution {
    Write-CplLog '=== Change resolution (NVIDIA color / Full / 10 bpc) ==='
    if (-not (Navigate-Page 'Change resolution')) { return }
    For-EachDisplay {
        $root = Get-NvcplRoot
        [void](Click-ByNameOrId -Root $root -Names @('Use NVIDIA color settings') -Ids @('34606'))
        Start-Sleep -Milliseconds 500
        $root = Get-NvcplRoot
        [void](Select-ComboOption -Root $root -CurrentCandidates @('RGB', 'YCbCr420', 'YCbCr422', 'YCbCr444') -WantName 'RGB')
        [void](Select-ComboOption -Root $root -CurrentCandidates @('Full', 'Limited') -WantName 'Full')
        $ok10 = Select-ComboOption -Root $root -CurrentCandidates @('8 bpc', '10 bpc', '12 bpc', '6 bpc') -WantName '10 bpc'
        if (-not $ok10) { Write-CplLog '10 bpc unavailable for this mode — left depth as-is' }
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-Page-DesktopSizePosition {
    Write-CplLog '=== Adjust desktop size and position (GPU / No scaling / Override) ==='
    if (-not (Navigate-Page 'Adjust desktop size and position')) { return }
    For-EachDisplay {
        $root = Get-NvcplRoot
        # Scaling mode
        [void](Click-ByNameOrId -Root $root -Names @('No scaling', 'No Scaling') -Ids @('327'))
        Start-Sleep -Milliseconds 250
        # Perform scaling on: GPU (combo 9329)
        $scaleOn = Find-ElById $root '9329'
        if ($scaleOn) {
            [void](Click-El $scaleOn)
            Start-Sleep -Milliseconds 350
            $desk = [System.Windows.Automation.AutomationElement]::RootElement
            $gpuItem = Find-ElByName (Get-NvcplRoot) 'GPU'
            if (-not $gpuItem) { $gpuItem = Find-ElByName $desk 'GPU' }
            if ($gpuItem) { [void](Click-El $gpuItem); Write-CplLog 'Perform scaling on: GPU' }
            else {
                # keyboard G
                Focus-Nvcpl
                [System.Windows.Forms.SendKeys]::SendWait('g')
                Start-Sleep -Milliseconds 100
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
                Write-CplLog 'Perform scaling on: GPU (keyboard)'
            }
        } else {
            [void](Click-ByNameOrId -Root $root -Names @('GPU') -Ids @())
        }
        Start-Sleep -Milliseconds 250
        # Override checkbox 9330
        $ovr = Find-ElById $root '9330'
        if (-not $ovr) { $ovr = Find-ElByName $root 'Override the scaling mode set by games and programs' }
        if ($ovr) {
            try {
                $tp = $ovr.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
                Write-CplLog 'Override scaling = On (toggle)'
            } catch {
                [void](Click-El $ovr)
                Write-CplLog 'Override scaling clicked'
            }
        }
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-Page-VideoColor {
    Write-CplLog '=== Adjust video color settings (NVIDIA) ==='
    if (-not (Navigate-Page 'Adjust video color settings')) { return }
    For-EachDisplay {
        $root = Get-NvcplRoot
        [void](Click-ByNameOrId -Root $root -Names @('With the NVIDIA settings') -Ids @('1302'))
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

function Apply-Page-VideoImage {
    Write-CplLog '=== Adjust video image settings (NVIDIA) ==='
    if (-not (Navigate-Page 'Adjust video image settings')) { return }
    For-EachDisplay {
        $root = Get-NvcplRoot
        # Two separate "Use the NVIDIA setting" radios (edge + noise) — click both by id
        [void](Click-ByNameOrId -Root $root -Names @() -Ids @('1402'))  # edge enhancement NVIDIA
        Start-Sleep -Milliseconds 200
        [void](Click-ByNameOrId -Root $root -Names @() -Ids @('1409'))  # noise reduction NVIDIA
        # Also try by name if multiple
        $root = Get-NvcplRoot
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            $e = $all.Item($i)
            if ($e.Current.Name -eq 'Use the NVIDIA setting') { [void](Click-El $e); Start-Sleep -Milliseconds 150 }
        }
        $root = Get-NvcplRoot
        Click-ApplyAndKeep -Root $root
    }
}

# ---------------- main ----------------
Write-CplLog 'FULL Control Panel pass starting (Apply + Keep after every page)...'
if (-not (Ensure-NvcplRunning)) {
    Write-CplLog 'FATAL: NVIDIA Control Panel failed to start'
    exit 1
}
Start-Sleep -Seconds 2

# EULA if present
$desk = [System.Windows.Automation.AutomationElement]::RootElement
foreach ($n in @('Agree and continue', 'Agree', 'Continue', 'I Agree', 'Accept')) {
    $el = Find-ElByName $desk $n
    if ($el) { [void](Click-El $el); Write-CplLog "EULA '$n'"; Start-Sleep 1 }
}

Apply-Page-3DImageSettings
Apply-Page-PhysX
Apply-Page-ChangeResolution
Apply-Page-DesktopSizePosition
Apply-Page-VideoColor
Apply-Page-VideoImage

Write-CplLog 'FULL Control Panel pass finished'
exit 0
