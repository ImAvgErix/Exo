# Repair-Discord.ps1 - public one-liner entry for stock Discord reset.
# Prefer the OptiHub in-app Repair button when available.
#
#   irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
#
# Full logout reset:
#   $env:OPTIHUB_REPAIR_FULL = '1'
#   irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex

$ErrorActionPreference = 'Stop'
$env:OPTIHUB = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

$localScripts = Join-Path $env:LOCALAPPDATA 'OptiHub\scripts\Discord\OptiHub-Discord-Repair.ps1'
$bundledCandidates = @(
    (Join-Path $PSScriptRoot 'OptiHub\Scripts\Discord\OptiHub-Discord-Repair.ps1'),
    $localScripts
)

$repair = $bundledCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $repair) {
    # Download the canonical non-interactive repair script from the repo.
    $url = 'https://raw.githubusercontent.com/BarcusEric/OptiHub/main/OptiHub/Scripts/Discord/OptiHub-Discord-Repair.ps1'
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('OptiHub-Discord-Repair-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Write-Host '[*] Downloading OptiHub Discord repair script...' -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Repair/1.0' }
    $repair = $tmp
}

$argsList = @('-NonInteractive')
if ($env:OPTIHUB_REPAIR_FULL -eq '1' -or $env:DISCOPT_REPAIR_FULL -eq '1') {
    $argsList += '-FullReset'
}

& $repair @argsList
exit $LASTEXITCODE
