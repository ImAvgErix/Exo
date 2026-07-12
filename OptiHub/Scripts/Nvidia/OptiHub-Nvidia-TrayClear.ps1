# OptiHub — clear NVIDIA taskbar overflow / tray ghosts (App + display container icons)
# Safe: does not stop NVDisplay.ContainerLocalSystem (display driver).
$ErrorActionPreference = 'Continue'

function Write-TLog([string]$Msg) {
    $line = "[TRAY] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -EA 0 } catch { }
    }
}

Write-TLog 'Clearing NVIDIA tray / overflow icons...'

# Stop App container only
try {
    $svc = Get-Service -Name 'NvContainerLocalSystem' -EA 0
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service NvContainerLocalSystem -Force -EA 0 }
        Set-Service NvContainerLocalSystem -StartupType Disabled -EA 0
        Write-TLog 'NvContainerLocalSystem Stopped/Disabled'
    }
} catch { }

# Kill leftover App processes (not NVDisplay.Container)
foreach ($im in @(
    'NVIDIA App.exe', 'NVIDIA Overlay.exe', 'NVIDIA Share.exe', 'nvsphelper64.exe',
    'nvcontainer.exe'
)) {
    try { & taskkill.exe /F /IM $im /T 2>$null | Out-Null } catch { }
}

$removed = 0
$notify = 'HKCU:\Control Panel\NotifyIconSettings'
if (Test-Path $notify) {
    Get-ChildItem $notify -EA 0 | ForEach-Object {
        $exe = $null
        try { $exe = [string](Get-ItemProperty $_.PSPath -EA 0).ExecutablePath } catch { }
        if ([string]::IsNullOrWhiteSpace($exe)) { return }
        if ($exe -match '(?i)NVIDIA|nvcontainer|NVDisplay|GeForce|ShadowPlay|nvsphelper|nvapp') {
            try {
                Remove-Item -LiteralPath $_.PSPath -Recurse -Force -EA Stop
                $removed++
                Write-TLog "Removed tray key: $exe"
            } catch {
                Write-TLog "Could not remove $($_.PSChildName): $($_.Exception.Message)"
            }
        }
    }
}
Write-TLog "Removed $removed NotifyIconSettings entr(y/ies)"

# ProgramData App leftovers
$pd = Join-Path $env:ProgramData 'NVIDIA Corporation\NVIDIA App'
if (Test-Path $pd) {
    try {
        Remove-Item -LiteralPath $pd -Recurse -Force -EA Stop
        Write-TLog "Removed $pd"
    } catch {
        try {
            & takeown.exe /F $pd /R /D Y 2>$null | Out-Null
            & icacls.exe $pd /grant Administrators:F /T /C /Q 2>$null | Out-Null
            Remove-Item -LiteralPath $pd -Recurse -Force -EA 0
            Write-TLog "Removed $pd (after takeown)"
        } catch {
            Write-TLog "Could not remove ProgramData NVIDIA App: $($_.Exception.Message)"
        }
    }
}

# Also wipe known App paths if empty leftovers
foreach ($p in @(
    (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVIDIA App'),
    (Join-Path $env:LOCALAPPDATA 'NVIDIA Corporation\NVIDIA App')
)) {
    if (Test-Path $p) {
        try { Remove-Item $p -Recurse -Force -EA Stop; Write-TLog "Removed $p" } catch { }
    }
}

Write-TLog 'Done. If Windows still shows a name in overflow, open the ^ menu once or sign out/in.'
Write-Output 'OPTIHUB_PROGRESS:100|Tray cleared'
exit 0
