param(
    [string]$Version = '1.1.0',
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $RepoRoot 'Disc Optimizer'
$OutFile = Join-Path $OutputDir "Disc-Optimizer-v$Version.zip"

if (-not (Test-Path $Source)) {
    throw "Missing source folder: $Source"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$releaseOnly = @(
    'kit\tools\DiscordSetup.exe',
    'kit\tools\EquilotlCli.exe',
    'kit\downloads\PowerShell-7.7.0-preview.2-win-x64.zip'
)

foreach ($relative in $releaseOnly) {
    $path = Join-Path $Source $relative
    if (-not (Test-Path $path)) {
        Write-Warning "Release package will be online-first; missing optional offline artifact: $relative"
    }
}

if (Test-Path $OutFile) {
    Remove-Item $OutFile -Force
}

$staging = Join-Path ([IO.Path]::GetTempPath()) "discopt-release-$Version"
if (Test-Path $staging) {
    Remove-Item $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    Copy-Item $Source (Join-Path $staging 'Disc Optimizer') -Recurse -Force

    $logs = Join-Path $staging 'Disc Optimizer\kit\logs'
    if (Test-Path $logs) {
        Get-ChildItem $logs -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'README.txt' } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $downloads = Join-Path $staging 'Disc Optimizer\kit\downloads'
    if (Test-Path $downloads) {
        Get-ChildItem $downloads -Directory -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    Compress-Archive -Path (Join-Path $staging 'Disc Optimizer') -DestinationPath $OutFile -Force
    Write-Host "Created $OutFile" -ForegroundColor Green
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
