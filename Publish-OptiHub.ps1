#Requires -Version 5.1
<#
.SYNOPSIS
  Publish a self-contained OptiHub win-x64 build, zip it, and wrap it as a
  single double-click OptiHub.exe self-extractor for GitHub Releases.

.EXAMPLE
  .\Publish-OptiHub.ps1
  .\Publish-OptiHub.ps1 -SkipSfx
#>
param(
    [switch]$SkipZip,
    [switch]$SkipSfx,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'OptiHub\OptiHub.csproj'
$VersionFile = Join-Path $Root 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '1.0.0' }

$ReleaseDir = Join-Path $Root 'release'
$ZipPath = Join-Path $ReleaseDir "OptiHub-$Version-win-x64.zip"
$SfxPath = Join-Path $ReleaseDir 'OptiHub.exe'
$PayloadZip = Join-Path $ReleaseDir 'optihub-build.zip'
$OutDir = Join-Path $Root "publish\OptiHub-win-x64-v$Version"
$LegacyOutDir = Join-Path $Root 'publish\OptiHub-win-x64'
$SfxSource = Join-Path $Root 'tools\OptiHubSfx.cs'

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

function Get-CscPath {
    $candidates = @(
        (Join-Path ${env:WINDIR} 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path ${env:WINDIR} 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Net.Component.4.8.SDK -find '**\csc.exe' 2>$null |
            Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    return $null
}

function New-OptiHubSfx {
    param(
        [Parameter(Mandatory)][string]$PayloadZipPath,
        [Parameter(Mandatory)][string]$OutputExe,
        [Parameter(Mandatory)][string]$SourceCs
    )

    if (-not (Test-Path -LiteralPath $SourceCs)) {
        throw "SFX source missing: $SourceCs"
    }
    if (-not (Test-Path -LiteralPath $PayloadZipPath)) {
        throw "Payload zip missing: $PayloadZipPath"
    }

    $csc = Get-CscPath
    if (-not $csc) {
        throw 'csc.exe not found. Install .NET Framework 4.x developer pack / Windows SDK, or Visual Studio.'
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('optihub-sfx-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $payloadCopy = Join-Path $work 'payload.zip'
        Copy-Item -LiteralPath $PayloadZipPath -Destination $payloadCopy -Force
        $srcCopy = Join-Path $work 'OptiHubSfx.cs'
        Copy-Item -LiteralPath $SourceCs -Destination $srcCopy -Force
        $outCopy = Join-Path $work 'OptiHub.exe'

        # Resource name must match Program constant "payload.zip"
        $args = @(
            '/nologo',
            '/target:winexe',
            '/optimize+',
            '/platform:anycpu',
            '/out:' + $outCopy,
            '/resource:' + $payloadCopy + ',payload.zip',
            '/r:System.IO.Compression.dll',
            '/r:System.IO.Compression.FileSystem.dll',
            $srcCopy
        )

        Write-Host "[*] Building self-extracting OptiHub.exe (csc)..." -ForegroundColor DarkGray
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = & $csc @args 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prev
        if ($code -ne 0 -or -not (Test-Path $outCopy)) {
            $output | ForEach-Object { Write-Host $_ }
            throw "csc failed building SFX (exit $code)"
        }

        Copy-Item -LiteralPath $outCopy -Destination $OutputExe -Force
        $sizeMb = [math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
        Write-Host "[+] Self-extracting exe: $OutputExe ($sizeMb MB)" -ForegroundColor Green
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
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

Write-Host "[+] Published folder: $exe" -ForegroundColor Green

if (-not $SkipZip) {
    if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Write-Host "[*] Zipping → $ZipPath" -ForegroundColor DarkGray
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    Copy-Item -LiteralPath $ZipPath -Destination $PayloadZip -Force
    $sizeMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "[+] Release zip: $ZipPath ($sizeMb MB)" -ForegroundColor Green
}

if (-not $SkipSfx) {
    if (-not (Test-Path $ZipPath)) {
        throw 'Need zip payload to build SFX. Run without -SkipZip.'
    }
    if (Test-Path $SfxPath) { Remove-Item -LiteralPath $SfxPath -Force }
    New-OptiHubSfx -PayloadZipPath $ZipPath -OutputExe $SfxPath -SourceCs $SfxSource
}

Write-Host ''
Write-Host 'Done. Smoke-test with:' -ForegroundColor Cyan
if (Test-Path $SfxPath) {
    Write-Host "  Start-Process '$SfxPath'   # self-extracting installer"
}
Write-Host "  Start-Process '$exe'       # from publish folder"
Write-Host ''
