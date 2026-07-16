# Nvidia.Bootstrap.ps1 - thin stage entry points (Wave 3).
# Dot-sourced by Exo-Nvidia-Run.ps1. Does not replace Nvidia-Optimizer.ps1;
# defines stage IDs shared by detect contracts + Contracts.Smoke.
# ASCII only. Safe to call multiple times.

Set-StrictMode -Version Latest

# Stage IDs must stay aligned with NvidiaDetectLogic + Exo-Nvidia-Detect.ps1.
$script:ExoNvidiaStageIds = @(
    'driver-tweaks'
    'profile-import'
    'game-profile-deltas'
    'display-apply'
    'overlay-disable'
    'tray-clear'
    'debloat'
)

function Get-ExoNvidiaStageIds {
    return @($script:ExoNvidiaStageIds)
}

function Test-ExoNvidiaStageId {
    param([Parameter(Mandatory)][string]$StageId)
    return $script:ExoNvidiaStageIds -contains $StageId
}

function Write-ExoNvidiaStageReport {
    param(
        [Parameter(Mandatory)][string]$StageId,
        [Parameter(Mandatory)][ValidateSet('ok', 'fail', 'skip')][string]$Status,
        [string]$Reason = ''
    )
    if (-not (Test-ExoNvidiaStageId -StageId $StageId)) {
        $StageId = "unknown:$StageId"
    }
    if (Get-Command Write-ExoReport -ErrorAction SilentlyContinue) {
        Write-ExoReport -Step $StageId -Status $Status -Reason $Reason
        return
    }
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$StageId|$Status" } else { "$StageId|$Status`:$Reason" }
    Write-Output "EXO_REPORT:$entry"
}
