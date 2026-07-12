# OptiHub Steam Optimizer runner - progress markers for WinUI host.
param(
    [switch]$Quick,
    [switch]$NonInteractive,
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
# Hosted by OptiHub via PowerShell 7 Preview (+ Terminal Preview on the machine).
if ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
    throw 'OptiHub-Steam-Run requires PowerShell 7 Preview. Install Microsoft.PowerShell.Preview.'
}
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

# Reapply is intentionally a full maximum-performance pass. Quick mode remains
# available only when explicitly requested by a script caller.
if (-not $Quick -and -not $Repair) {
    Write-Output '[*] Full aggressive apply mode'
    Write-HubProgress 10 'Full performance pass'
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
        # Elevated OptiHub runs poll OPTIHUB_LOG; non-elevated reads stdout.
        # Always emit progress on both channels so the UI bar keeps updating.
        Write-Output $line
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
