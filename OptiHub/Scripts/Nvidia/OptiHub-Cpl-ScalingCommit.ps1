# OptiHub — ONE careful Control Panel commit for scaling prefs.
# Pixel-detect Override (blue = ON). Never click when already ON (prevents uncheck).
# Full CPL restart between monitors so each display is configured alone.
# Does NOT use GPU dropdown (owner-drawn / breaks tree). GPU via NVTweak registry.
$ErrorActionPreference = 'Continue'

function Write-SLog([string]$Msg) {
    $line = "[SCALE] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

if (-not ('OptiHubScaleWin' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OptiHubScaleWin {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(int f, int a, int b, int c, int d);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr d);
  [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr hdc, int x, int y);
  public static void Max(IntPtr h) { ShowWindow(h, 3); }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(45);
    mouse_event(0x02, 0, 0, 0, 0);
    System.Threading.Thread.Sleep(30);
    mouse_event(0x04, 0, 0, 0, 0);
  }
  public static int[] Sample(int x, int y) {
    IntPtr dc = GetDC(IntPtr.Zero);
    uint p = GetPixel(dc, x, y);
    ReleaseDC(IntPtr.Zero, dc);
    return new int[] { (int)(p & 0xFF), (int)((p >> 8) & 0xFF), (int)((p >> 16) & 0xFF) };
  }
  // Checked Override box paints blue-ish check (e.g. 25,110,191). Unchecked is light gray (~234-243).
  public static bool IsBlueCheck(int r, int g, int b) {
    return (b >= 140 && r <= 100 && b > r + 30);
  }
}
"@
}

function Write-NvtweakAll {
    $root = 'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices'
    if (-not (Test-Path $root)) { New-Item $root -Force | Out-Null }
    Get-ChildItem $root -EA 0 | ForEach-Object {
        $p = $_.PSPath
        Set-ItemProperty $p -Name PerformScalingOn -Value 0 -Type DWord -Force
        Set-ItemProperty $p -Name ScalingOverride -Value 1 -Type DWord -Force
        Set-ItemProperty $p -Name AppControlledScaling -Value 0 -Type DWord -Force
        Set-ItemProperty $p -Name ScalingMode -Value 2 -Type DWord -Force
        Set-ItemProperty $p -Name Scaling -Value 2 -Type DWord -Force
    }
}

function Stop-Cpl {
    Get-Process nvcplui -EA 0 | Stop-Process -Force -EA 0
    Start-Sleep -Milliseconds 600
}

function Start-Cpl {
    Stop-Cpl
    $exe = $null
    $pkg = Get-AppxPackage -EA 0 | Where-Object { $_.Name -match 'NVIDIAControlPanel' } | Select-Object -First 1
    if ($pkg) {
        $hit = Get-ChildItem $pkg.InstallLocation -Recurse -Filter nvcplui.exe -EA 0 | Select-Object -First 1
        if ($hit) { $exe = $hit.FullName }
    }
    if (-not $exe) {
        foreach ($c in @(
            "$env:ProgramFiles\NVIDIA Corporation\Control Panel Client\nvcplui.exe",
            "$env:ProgramFiles\NVIDIA Corporation\NVIDIA Control Panel\nvcplui.exe"
        )) { if (Test-Path $c) { $exe = $c; break } }
    }
    if ($exe) { Start-Process $exe | Out-Null }
    else { Start-Process 'shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel' -EA 0 }
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while ([datetime]::UtcNow -lt $deadline) {
        $p = Get-Process nvcplui -EA 0 | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
        if ($p) {
            [void][OptiHubScaleWin]::BringWindowToTop($p.MainWindowHandle)
            [void][OptiHubScaleWin]::SetForegroundWindow($p.MainWindowHandle)
            [void][OptiHubScaleWin]::Max($p.MainWindowHandle)
            Start-Sleep -Milliseconds 500
            return $p
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Get-Root($p) {
    [void][OptiHubScaleWin]::SetForegroundWindow($p.MainWindowHandle)
    [void][OptiHubScaleWin]::Max($p.MainWindowHandle)
    Start-Sleep -Milliseconds 100
    try { return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
    catch { return $null }
}

function Find-Id($root, $id) {
    if (-not $root) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-Name($root, $n) {
    if (-not $root) { return $null }
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $n)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Click-ElCenter($el) {
    if (-not $el) { return $false }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -lt 2) { return $false }
    [OptiHubScaleWin]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    return $true
}

function Click-ElLeft($el, [int]$ox = 10) {
    if (-not $el) { return $false }
    $r = $el.Current.BoundingRectangle
    if ($r.Width -lt 2) { return $false }
    [OptiHubScaleWin]::Click([int]($r.X + $ox), [int]($r.Y + $r.Height / 2))
    return $true
}

function Test-OverrideOn($ovrEl) {
    if (-not $ovrEl) { return $false }
    $r = $ovrEl.Current.BoundingRectangle
    # Sample several x offsets in the checkbox glyph
    $hits = 0
    foreach ($ox in @(8, 10, 12)) {
        $rgb = [OptiHubScaleWin]::Sample([int]($r.X + $ox), [int]($r.Y + $r.Height / 2))
        if ([OptiHubScaleWin]::IsBlueCheck($rgb[0], $rgb[1], $rgb[2])) { $hits++ }
        Write-SLog ("  pixel ox={0} rgb={1},{2},{3} blue={4}" -f $ox, $rgb[0], $rgb[1], $rgb[2], ([OptiHubScaleWin]::IsBlueCheck($rgb[0], $rgb[1], $rgb[2])))
    }
    return ($hits -ge 1)
}

function Go-DesktopSize($p) {
    $root = Get-Root $p
    $nav = Find-Name $root 'Adjust desktop size and position'
    if (-not $nav) { Write-SLog 'Nav missing'; return $false }
    [void](Click-ElCenter $nav)
    Start-Sleep -Milliseconds 1400
    $root = Get-Root $p
    if (-not (Find-Id $root '327') -and -not (Find-Name $root 'No scaling')) {
        Write-SLog 'Desktop size page controls missing'
        return $false
    }
    return $true
}

function Get-StripPoints($p) {
    $root = Get-Root $p
    $strip = Find-Id $root '528'
    $count = [Math]::Max(1, [System.Windows.Forms.Screen]::AllScreens.Count)
    if (-not $strip) {
        return ,@([pscustomobject]@{ I = 0; X = 0; Y = 0 })
    }
    $r = $strip.Current.BoundingRectangle
    $arr = New-Object object[] $count
    for ($i = 0; $i -lt $count; $i++) {
        $arr[$i] = [pscustomobject]@{
            I = $i
            X = [int]($r.X + ($r.Width * ($i + 0.5) / $count))
            Y = [int]($r.Y + $r.Height * 0.50)
        }
    }
    Write-SLog "Strip slots=$count width=$([int]$r.Width)"
    return $arr
}

function Apply-OneMonitor([int]$Index, [int]$X, [int]$Y) {
    Write-SLog "======== MONITOR $Index (fresh CPL session) ========"
    Write-NvtweakAll

    $p = Start-Cpl
    if (-not $p) { Write-SLog 'FATAL: CPL start failed'; return $false }
    if (-not (Go-DesktopSize $p)) { Stop-Cpl; return $false }

    # Select ONLY this monitor
    if ($X -gt 0) {
        [void][OptiHubScaleWin]::SetForegroundWindow($p.MainWindowHandle)
        [OptiHubScaleWin]::Click($X, $Y)
        Write-SLog "Selected monitor $Index @ $X,$Y"
        Start-Sleep -Milliseconds 1500
    }

    $root = Get-Root $p

    # No scaling (left radio glyph)
    $no = Find-Id $root '327'
    if (-not $no) { $no = Find-Name $root 'No scaling' }
    if ($no) {
        [void](Click-ElLeft $no 12)
        Write-SLog 'No scaling clicked'
        Start-Sleep -Milliseconds 400
    } else {
        Write-SLog 'No scaling control missing'
    }

    # Override: ONLY click if pixel says OFF
    $root = Get-Root $p
    $ovr = Find-Id $root '9330'
    if (-not $ovr) {
        $ovr = Find-Name $root 'Override the scaling mode set by games and programs'
    }
    if (-not $ovr) {
        Write-SLog 'Override control missing'
    } else {
        $on = Test-OverrideOn $ovr
        if ($on) {
            Write-SLog 'Override already ON (blue pixels) — NOT clicking'
        } else {
            Write-SLog 'Override OFF — single left-glyph click to enable'
            [void](Click-ElLeft $ovr 10)
            Start-Sleep -Milliseconds 500
            $root = Get-Root $p
            $ovr = Find-Id $root '9330'
            if (-not $ovr) {
                $ovr = Find-Name $root 'Override the scaling mode set by games and programs'
            }
            $on2 = Test-OverrideOn $ovr
            if ($on2) {
                Write-SLog 'Override now ON'
            } else {
                Write-SLog 'Override still OFF after one click — one more attempt'
                [void](Click-ElLeft $ovr 12)
                Start-Sleep -Milliseconds 400
                $on3 = Test-OverrideOn (Find-Id (Get-Root $p) '9330')
                Write-SLog "Override after retry: $on3"
            }
        }
    }

    # Apply once
    $root = Get-Root $p
    $apply = Find-Name $root 'Apply'
    if ($apply) {
        $en = $true
        try { $en = [bool]$apply.Current.IsEnabled } catch { }
        if ($en) {
            [void](Click-ElCenter $apply)
            Write-SLog 'Apply'
            Start-Sleep -Milliseconds 500
            # Keep/Yes
            $end = [datetime]::UtcNow.AddSeconds(8)
            while ([datetime]::UtcNow -lt $end) {
                $desk = [System.Windows.Automation.AutomationElement]::RootElement
                foreach ($n in @('Yes', 'Keep changes', 'Keep Changes', 'OK')) {
                    $el = Find-Name $desk $n
                    if ($el -and $el.Current.IsEnabled -and $el.Current.Name -notmatch '(?i)^no|revert') {
                        [void](Click-ElCenter $el)
                        Write-SLog "Keep: $n"
                        Start-Sleep -Milliseconds 400
                        break
                    }
                }
                Start-Sleep -Milliseconds 250
            }
        } else {
            Write-SLog 'Apply disabled (already matched UI)'
        }
    } else {
        Write-SLog 'Apply button missing'
    }

    Write-NvtweakAll
    Stop-Cpl
    Write-SLog "Monitor $Index session done"
    return $true
}

# ---- main ----
Write-SLog 'Scaling commit: pixel-safe Override, one monitor per CPL restart'
Write-NvtweakAll

# Fresh CPL once just to read strip geometry, then kill
$p0 = Start-Cpl
if (-not $p0) { Write-SLog 'FATAL cannot start CPL'; exit 1 }
if (-not (Go-DesktopSize $p0)) { Stop-Cpl; exit 1 }
$pts = Get-StripPoints $p0
Stop-Cpl
if ($pts -isnot [System.Array]) { $pts = @($pts) }

Write-SLog "Will configure $($pts.Count) monitor(s)"
$ok = 0
foreach ($pt in $pts) {
    if (Apply-OneMonitor -Index ([int]$pt.I) -X ([int]$pt.X) -Y ([int]$pt.Y)) { $ok++ }
    Start-Sleep -Milliseconds 800
}

Write-NvtweakAll
Write-SLog "Finished $ok / $($pts.Count) monitors"
exit $(if ($ok -gt 0) { 0 } else { 1 })
