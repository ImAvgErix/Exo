# Exo.Common.ps1 - shared helpers for optimizer Run wrappers (Wave 2 quality).
# Dot-source from Exo-*-Run.ps1. ASCII only. Safe to call multiple times.

Set-StrictMode -Version Latest

function Assert-ExoPwsh7 {
    if ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Exo optimizers require PowerShell 7. Install: winget install Microsoft.PowerShell'
    }
}

function Get-ExoAppDataDir {
    $dir = Join-Path $env:LOCALAPPDATA 'Exo'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-ExoLogsDir {
    $dir = Join-Path (Get-ExoAppDataDir) 'logs'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Initialize-ExoRunLog {
    param([Parameter(Mandatory)][string]$Module)
    $safe = ($Module -replace '[^\w\-]', '_')
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path (Get-ExoLogsDir) ("{0}-{1}.log" -f $safe, $stamp)
    $env:EXO_LOG = $path
    try {
        Set-Content -LiteralPath $path -Value ("# Exo {0} run {1:o}" -f $Module, (Get-Date).ToUniversalTime()) -Encoding UTF8
    } catch { }
    return $path
}

function Write-ExoReport {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][ValidateSet('ok', 'fail', 'skip')][string]$Status,
        [string]$Reason = ''
    )
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$Step|$Status" } else { "$Step|$Status`:$Reason" }
    $line = "EXO_REPORT:$entry"
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

function Import-ExoSharedLibs {
    # Resolves Exo/Scripts/lib next to Discord|Steam|Nvidia module folders.
    $here = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($here)) {
        $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $libDir = Join-Path $here 'lib'
    if (-not (Test-Path -LiteralPath (Join-Path $libDir 'Exo.Common.ps1'))) {
        # Called from module folder (Scripts/Discord) -> parent/lib
        $libDir = Join-Path (Split-Path -Parent $here) 'lib'
    }
    $noBg = Join-Path $libDir 'Exo.NoBackground.ps1'
    if (Test-Path -LiteralPath $noBg) {
        . $noBg
    }
    return $libDir
}
