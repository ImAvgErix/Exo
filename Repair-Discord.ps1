# Repair-Discord.ps1 - public one-liner entry for stock Discord reset.
# Prefer the OptiHub in-app Repair button when available.
#
#   irm "https://raw.githubusercontent.com/UhhErix/OptiHub/main/Repair-Discord.ps1" | iex
#
# Full logout reset:
#   $env:OPTIHUB_REPAIR_FULL = '1'
#   irm "https://raw.githubusercontent.com/UhhErix/OptiHub/main/Repair-Discord.ps1" | iex

$ErrorActionPreference = 'Stop'
$env:OPTIHUB = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

$localScripts = if ($env:LOCALAPPDATA) {
    Join-Path $env:LOCALAPPDATA 'OptiHub\scripts\Discord\OptiHub-Discord-Repair.ps1'
} else { $null }

$bundledCandidates = @()
# $PSScriptRoot is empty when this bootstrap is piped to Invoke-Expression.
if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $bundledCandidates += Join-Path $PSScriptRoot 'OptiHub\Scripts\Discord\OptiHub-Discord-Repair.ps1'
}
if ($localScripts) { $bundledCandidates += $localScripts }

$tmp = $null
$exitCode = 1
try {
    $repair = $bundledCandidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        Select-Object -First 1

    if (-not $repair) {
        # Download the canonical non-interactive repair script from the repo.
        $url = 'https://raw.githubusercontent.com/UhhErix/OptiHub/main/OptiHub/Scripts/Discord/OptiHub-Discord-Repair.ps1'
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('OptiHub-Discord-Repair-' + [guid]::NewGuid().ToString('N') + '.ps1')
        Write-Host '[*] Downloading OptiHub Discord repair script...' -ForegroundColor Cyan
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -TimeoutSec 60 -Headers @{ 'User-Agent' = 'OptiHub-Repair/1.0' }
        $downloaded = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8
        if ($downloaded.Length -lt 3000 -or
            $downloaded -notmatch 'function\s+Stop-RepairDiscord' -or
            $downloaded -notmatch 'function\s+Write-HubProgress') {
            throw 'Downloaded repair script failed validation.'
        }
        $repair = $tmp
    }

    $argsList = @('-NonInteractive')
    if ($env:OPTIHUB_REPAIR_FULL -eq '1' -or $env:DISCOPT_REPAIR_FULL -eq '1') {
        $argsList += '-FullReset'
    }

    & $repair @argsList
    $exitCode = if ($null -ne $LASTEXITCODE) { [int]$LASTEXITCODE } else { 0 }
} catch {
    Write-Host "[-] Discord repair could not start: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

exit $exitCode
