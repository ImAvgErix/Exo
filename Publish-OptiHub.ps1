#Requires -Version 5.1
<#
.SYNOPSIS
  Publish a self-contained OptiHub win-x64 build and zip it for GitHub Releases.

.EXAMPLE
  .\Publish-OptiHub.ps1
  .\Publish-OptiHub.ps1 -SkipZip
#>
param(
    [switch]$SkipZip,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'OptiHub\OptiHub.csproj'
$VersionFile = Join-Path $Root 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '1.0.0' }

$ReleaseDir = Join-Path $Root 'release'
$ZipPath = Join-Path $ReleaseDir "OptiHub-$Version-win-x64.zip"
# Prefer a clean versioned folder; fall back if an older publish dir is file-locked (e.g. core.asar).
$OutDir = Join-Path $Root "publish\OptiHub-win-x64-v$Version"
$LegacyOutDir = Join-Path $Root 'publish\OptiHub-win-x64'

Write-Host ''
Write-Host "  OptiHub publish  ·  v$Version  ·  self-contained win-x64" -ForegroundColor Cyan
Write-Host ''

function Clear-PublishDir([string]$Path) {
    if (-not (Test-Path $Path)) { return $true }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return $true
    } catch {
        Write-Host "[!] Could not clear $Path (file lock). Using a fresh folder." -ForegroundColor DarkYellow
        return $false
    }
}

if (-not (Clear-PublishDir $OutDir)) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $OutDir = Join-Path $Root "publish\OptiHub-win-x64-v$Version-$stamp"
}
Clear-PublishDir $LegacyOutDir | Out-Null
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null

Write-Host '[*] dotnet publish...' -ForegroundColor DarkGray
& dotnet publish $Project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=true `
    -o $OutDir

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed (exit $LASTEXITCODE)"
}

$exe = Join-Path $OutDir 'OptiHub.exe'
if (-not (Test-Path $exe)) {
    throw "OptiHub.exe not found in $OutDir"
}

Write-Host "[+] Published: $exe" -ForegroundColor Green

if (-not $SkipZip) {
    if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Write-Host "[*] Zipping → $ZipPath" -ForegroundColor DarkGray
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    $sizeMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "[+] Release zip: $ZipPath ($sizeMb MB)" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Done. Smoke-test with:' -ForegroundColor Cyan
Write-Host "  Start-Process '$exe'"
Write-Host ''
