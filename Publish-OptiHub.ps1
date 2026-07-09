#Requires -Version 5.1
<#
.SYNOPSIS
  Publish OptiHub and build a single double-click OptiHub.exe self-extractor.
  Zip is only used as an intermediate payload (not required as a release asset).

.EXAMPLE
  .\Publish-OptiHub.ps1
#>
param(
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
$OutDir = Join-Path $Root "publish\OptiHub-win-x64-v$Version"
$LegacyOutDir = Join-Path $Root 'publish\OptiHub-win-x64'
$SfxSource = Join-Path $Root 'tools\OptiHubSfx.cs'

Write-Host ''
Write-Host "  OptiHub publish  -  v$Version  -  self-contained win-x64 -> OptiHub.exe" -ForegroundColor Cyan
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
    foreach ($c in @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function New-OptiHubSfx {
    param(
        [Parameter(Mandatory)][string]$PayloadZipPath,
        [Parameter(Mandatory)][string]$OutputExe,
        [Parameter(Mandatory)][string]$SourceCs
    )

    $csc = Get-CscPath
    if (-not $csc) { throw 'csc.exe not found (.NET Framework 4.x).' }
    if (-not (Test-Path -LiteralPath $SourceCs)) { throw "SFX source missing: $SourceCs" }
    if (-not (Test-Path -LiteralPath $PayloadZipPath)) { throw "Payload zip missing: $PayloadZipPath" }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('optihub-sfx-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $payloadCopy = Join-Path $work 'payload.zip'
        $srcCopy = Join-Path $work 'OptiHubSfx.cs'
        $outCopy = Join-Path $work 'OptiHub.exe'
        $rsp = Join-Path $work 'build.rsp'

        Copy-Item -LiteralPath $PayloadZipPath -Destination $payloadCopy -Force
        Copy-Item -LiteralPath $SourceCs -Destination $srcCopy -Force

        @(
            '/nologo'
            '/target:winexe'
            '/optimize+'
            '/platform:anycpu'
            "/out:$outCopy"
            "/resource:$payloadCopy,payload.zip"
            '/r:System.IO.Compression.dll'
            '/r:System.IO.Compression.FileSystem.dll'
            $srcCopy
        ) | Set-Content -LiteralPath $rsp -Encoding ASCII

        Write-Host '[*] Building self-extracting OptiHub.exe...' -ForegroundColor DarkGray
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = & $csc ("@" + $rsp) 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = $prev
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $outCopy)) {
            $output | ForEach-Object { Write-Host $_ }
            throw "csc failed building SFX (exit $code)"
        }

        Copy-Item -LiteralPath $outCopy -Destination $OutputExe -Force
        $sizeMb = [math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
        Write-Host "[+] OptiHub.exe (double-click installer): $OutputExe ($sizeMb MB)" -ForegroundColor Green
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

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

$publishedExe = Join-Path $OutDir 'OptiHub.exe'
if (-not (Test-Path $publishedExe)) { throw "OptiHub.exe not found in $OutDir" }
Write-Host "[+] Published app folder: $publishedExe" -ForegroundColor Green

if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Write-Host '[*] Packing payload zip (internal only)...' -ForegroundColor DarkGray
Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

if (Test-Path $SfxPath) { Remove-Item -LiteralPath $SfxPath -Force }
New-OptiHubSfx -PayloadZipPath $ZipPath -OutputExe $SfxPath -SourceCs $SfxSource

# Keep intermediate zip on disk for rebuilds; release script ships EXE only.
Write-Host ''
Write-Host 'Done. Double-click install test:' -ForegroundColor Cyan
Write-Host "  Start-Process '$SfxPath'"
Write-Host ''
