# Steam.Bootstrap.ps1 - thin stage entry points (Wave 3).
# Dot-sourced by Exo-Steam-Run.ps1. Does not replace Steam-Optimizer.ps1;
# defines stage IDs + helpers so Detect/Apply/smoke share one vocabulary.
# ASCII only. Safe to call multiple times.

Set-StrictMode -Version Latest

# Path helpers (thin split - god script remains the apply engine).
$__steamPaths = Join-Path $PSScriptRoot 'Steam.Paths.ps1'
if (Test-Path -LiteralPath $__steamPaths) { . $__steamPaths }

# Stage IDs written via EXO_REPORT / durable state - keep aligned with
# SteamDetectCore.ps1 + SteamLogic.RequiredApplyMarkers + Contracts.Smoke.
$script:ExoSteamStageIds = @(
    'cef-launcher'
    'memory-guard'
    'download-config'
    'client-tweaks'
    'windows-quiet'
    'toasts-off'
    'complete-debloat'
    'vdf-inject'
)

function Get-ExoSteamStageIds {
    return @($script:ExoSteamStageIds)
}

function Test-ExoSteamStageId {
    param([Parameter(Mandatory)][string]$StageId)
    return $script:ExoSteamStageIds -contains $StageId
}

function Write-ExoSteamStageReport {
    param(
        [Parameter(Mandatory)][string]$StageId,
        [Parameter(Mandatory)][ValidateSet('ok', 'fail', 'skip')][string]$Status,
        [string]$Reason = ''
    )
    if (-not (Test-ExoSteamStageId -StageId $StageId)) {
        $StageId = "unknown:$StageId"
    }
    if (Get-Command Write-ExoReport -ErrorAction SilentlyContinue) {
        Write-ExoReport -Step $StageId -Status $Status -Reason $Reason
        return
    }
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$StageId|$Status" } else { "$StageId|$Status`:$Reason" }
    Write-Output "EXO_REPORT:$entry"
}
