# DEPRECATED — Control Panel mouse automation removed.
# Display color/scaling is applied by OptiHub.NvDisplay.exe via NVAPI (driver API).
# This stub remains so older OptiHub builds that still call this script do not crash.
$ErrorActionPreference = 'Continue'

function Write-CplLog([string]$Msg) {
    $line = "[CPL] $Msg"
    Write-Host $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

Write-CplLog 'Control Panel UI automation is retired. Forwarding to NVAPI helper...'

$candidates = @(
    (Join-Path $PSScriptRoot 'tools\OptiHub.NvDisplay.exe'),
    (Join-Path $env:LOCALAPPDATA 'OptiHub\scripts\Nvidia\tools\OptiHub.NvDisplay.exe'),
    (Join-Path $env:LOCALAPPDATA 'OptiHub\app\Scripts\Nvidia\tools\OptiHub.NvDisplay.exe'),
    (Join-Path $env:LOCALAPPDATA 'OptiHub\tools\OptiHub.NvDisplay.exe')
)
$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    Write-CplLog 'FATAL: OptiHub.NvDisplay.exe not found'
    exit 1
}

Write-CplLog "Running $exe --apply"
$p = Start-Process -FilePath $exe -ArgumentList '--apply' -Wait -PassThru -NoNewWindow
exit $(if ($null -ne $p.ExitCode) { [int]$p.ExitCode } else { 0 })
