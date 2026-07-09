# OptiHub Steam Optimizer runner - progress markers for WinUI host.
param(
    [switch]$Quick,
    [switch]$NonInteractive,
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
$env:OPTIHUB = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "OPTIHUB_PROGRESS:$p|$Status"
    Write-Output $line
    if ($env:OPTIHUB_LOG) {
        try { Add-Content -LiteralPath $env:OPTIHUB_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Optimizer = Join-Path $Root 'Steam-Optimizer.ps1'
if (-not (Test-Path $Optimizer)) {
    Write-Output "[-] Steam-Optimizer.ps1 missing: $Root"
    Write-HubProgress 100 'Missing optimizer script'
    exit 1
}

Write-HubProgress 4 'Starting Steam Optimizer...'

# Auto-quick if already applied
$statePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OptiHub\steam-optimizer.json'
if (-not $Quick -and -not $Repair -and (Test-Path $statePath)) {
    $Quick = $true
    Write-Output '[*] Already optimized - Quick reapply'
    Write-HubProgress 10 'Quick reapply mode'
}

$runArgs = @()
if ($Quick) { $runArgs += '-Quick' }
if ($Repair) { $runArgs += '-Repair' }
$runArgs += '-NonInteractive'
$runArgs += '-NoLaunch'

try {
    & $Optimizer @runArgs 2>&1 | ForEach-Object {
        $line = "$_"
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        if ($line -notmatch '^OPTIHUB_PROGRESS:') { Write-Output $line }
    }
    $code = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
    if ($code -eq 0) {
        Write-Output '[+] Steam Optimizer finished successfully.'
        Write-HubProgress 100 'Completed successfully'
        exit 0
    }
    Write-Output "[-] Steam Optimizer exited with code $code"
    Write-HubProgress 100 'Finished with errors'
    exit $code
} catch {
    Write-Output "[-] $($_.Exception.Message)"
    Write-HubProgress 100 'Failed'
    exit 1
}
