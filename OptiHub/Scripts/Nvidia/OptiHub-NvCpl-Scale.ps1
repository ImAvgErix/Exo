# OptiHub - Display scaling / Override via NVAPI + NVTweak + NvCpl registry contract
# No mouse. No Control Panel UI automation.
# New path: clear HDTV TVFormat (PC mode), GPUScanOutToNative, stamp Override for every
# device key including App-shown IDs (e.g. 100002487), then re-read live path.
param(
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Continue'
function Write-SLog([string]$Msg) { Write-Host "[SCALE] $Msg" }

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-NvDisplayExe {
    foreach ($c in @(
        (Join-Path $Root 'tools\OptiHub.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'OptiHub\scripts\Nvidia\tools\OptiHub.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'OptiHub\app\Scripts\Nvidia\tools\OptiHub.NvDisplay.exe'),
        'C:\Users\Erix\OptiHub\tools\OptiHub.NvDisplay\bin\Release\net8.0-windows\OptiHub.NvDisplay.exe'
    )) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

function Stamp-ScalingOverrideAll {
    $roots = @(
        'HKCU:\Software\NVIDIA Corporation\Global\NVTweak\Devices',
        'HKLM:\SOFTWARE\NVIDIA Corporation\Global\NVTweak\Devices'
    )
    # App UI sometimes keys by short display property id (e.g. HDMI mon2 "100002487")
    $extraIds = @('100002487', '100002487-0', '100002487-1')
    $n = 0
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) {
            try { New-Item -Path $root -Force | Out-Null } catch { continue }
        }
        $names = New-Object System.Collections.Generic.HashSet[string]
        Get-ChildItem -LiteralPath $root -EA 0 | ForEach-Object { [void]$names.Add($_.PSChildName) }
        foreach ($e in $extraIds) { [void]$names.Add($e) }
        foreach ($name in $names) {
            $p = Join-Path $root $name
            if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
            # Contract the NVIDIA App GetScalingSettings path reads when refreshed:
            #   scalingMethod NoScale=3, scalingMode Gpu=1, isOverrideScalingEnabled=true
            # Registry mirrors used by classic CPL + App:
            Set-ItemProperty -LiteralPath $p -Name 'PerformScalingOn' -Value 0 -Type DWord -Force   # GPU
            Set-ItemProperty -LiteralPath $p -Name 'ScalingOverride' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'OverrideScalingMode' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'bOverrideScaling' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'ScalingModeOverride' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'Scaling' -Value 2 -Type DWord -Force             # No scaling
            Set-ItemProperty -LiteralPath $p -Name 'ScalingMode' -Value 2 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'FlatPanelScaling' -Value 2 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'GpuScaling' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'DisplayScaling' -Value 0 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'AppControlledScaling' -Value 0 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'ScalingDevice' -Value 0 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'PreferGpuScaling' -Value 1 -Type DWord -Force
            Set-ItemProperty -LiteralPath $p -Name 'ForceGpuScaling' -Value 1 -Type DWord -Force
            # NOTE: Windows registry value names are case-insensitive.
            # Do NOT write App JSON names like "scalingMode"=1 (Gpu) - that overwrites
            # classic "ScalingMode"=2 (No scaling) and breaks verification.
            Set-ItemProperty -LiteralPath $p -Name 'isOverrideScalingEnabled' -Value 1 -Type DWord -Force
            $n++
        }
    }
    $client = 'HKCU:\Software\NVIDIA Corporation\NVControlPanel2\Client'
    if (-not (Test-Path $client)) { New-Item $client -Force | Out-Null }
    Set-ItemProperty $client -Name 'OptiHubPreferScalingOverride' -Value 1 -Type DWord -Force
    Set-ItemProperty $client -Name 'OptiHubPreferGpuScaling' -Value 1 -Type DWord -Force
    Set-ItemProperty $client -Name 'OptiHubPreferNoScaling' -Value 1 -Type DWord -Force
    return $n
}

Write-SLog '=== OptiHub NvCpl-scale path (no mouse) ==='
$stamped = Stamp-ScalingOverrideAll
Write-SLog "Stamped Override/GPU/NoScale on $stamped device key(s)"

$exe = Get-NvDisplayExe
if (-not $exe) {
    Write-SLog 'FAIL: OptiHub.NvDisplay.exe missing'
    exit 2
}

if ($VerifyOnly) {
    Write-SLog "Running: $exe --status"
    & $exe --status
    exit $LASTEXITCODE
}

Write-SLog "Running: $exe --apply (clears HDTV TVFormat, forces GPUScanOutToNative)"
& $exe --apply
$code = $LASTEXITCODE
# Re-stamp after apply (helper rewrites NVTweak during apply)
[void](Stamp-ScalingOverrideAll)
Write-SLog "NVAPI apply exit=$code; registry re-stamped"
Write-SLog 'NOTE: App UI Override checkbox is isOverrideScalingEnabled from NvCpl GetScalingSettings.'
Write-SLog 'Driver path + registry are forced. Re-open System > Displays > mon2 to refresh App UI.'
exit $code
