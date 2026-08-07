# Exo non-interactive Steam repair - restore backups and clear marker.
param([switch]$NonInteractive)

$ErrorActionPreference = 'Stop'
$env:EXO = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    # Progress must never move backwards: the orb reads the last value it saw, so a
    # single low number mid-run reads as the bar jumping back and the module stalling.
    # An explicit 0 still resets, matching PowerShellRunnerService.ParseLine.
    if ($p -ne 0 -and (Test-Path Variable:\Global:ExoProgressHigh)) { $p = [Math]::Max($p, [int]$Global:ExoProgressHigh) }
    $Global:ExoProgressHigh = $p
    $line = "EXO_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Steam-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) {
    Write-Output '[-] Steam-Optimizer.ps1 missing'
    Write-HubProgress 100 'Missing script'
    exit 1
}

Write-HubProgress 10 'Starting Steam repair...'
try {
    & $Optimizer -Repair -NonInteractive 2>&1 | ForEach-Object { Write-Output "$_" }
    $code = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    if ($code -eq 0) {
        Write-HubProgress 100 'Repair complete'
        exit 0
    }
    Write-HubProgress 100 'Repair failed'
    exit $code
} catch {
    Write-Output "[-] $($_.Exception.Message)"
    Write-HubProgress 100 'Repair failed'
    exit 1
}
