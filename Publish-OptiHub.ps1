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
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must contain an exact semantic version (x.y.z); got '$Version'."
}

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
        [Parameter(Mandatory)][string]$SourceCs,
        [Parameter(Mandatory)][string]$AppVersion,
        [Parameter(Mandatory)][string]$IconPath
    )

    $csc = Get-CscPath
    if (-not $csc) { throw 'csc.exe not found (.NET Framework 4.x).' }
    if (-not (Test-Path -LiteralPath $SourceCs)) { throw "SFX source missing: $SourceCs" }
    if (-not (Test-Path -LiteralPath $PayloadZipPath)) { throw "Payload zip missing: $PayloadZipPath" }
    if (-not (Test-Path -LiteralPath $IconPath)) { throw "App icon missing: $IconPath" }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('optihub-sfx-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $payloadCopy = Join-Path $work 'payload.zip'
        $srcCopy = Join-Path $work 'OptiHubSfx.cs'
        $outCopy = Join-Path $work 'OptiHub.exe'
        $rsp = Join-Path $work 'build.rsp'
        $assemblyInfo = Join-Path $work 'AssemblyInfo.cs'

        Copy-Item -LiteralPath $PayloadZipPath -Destination $payloadCopy -Force
        Copy-Item -LiteralPath $SourceCs -Destination $srcCopy -Force

        $fourPartVersion = if ($AppVersion -match '^\d+\.\d+\.\d+$') { "$AppVersion.0" } else { $AppVersion }
        @"
using System.Reflection;
[assembly: AssemblyTitle("OptiHub Installer")]
[assembly: AssemblyProduct("OptiHub")]
[assembly: AssemblyDescription("OptiHub self-contained Windows installer")]
[assembly: AssemblyVersion("$fourPartVersion")]
[assembly: AssemblyFileVersion("$fourPartVersion")]
[assembly: AssemblyInformationalVersion("$AppVersion")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding ASCII

        # Response-file paths must be quoted: publishing from folders such as
        # "C:\Users\Name\Source Projects" otherwise splits csc arguments.
        @(
            '/nologo'
            '/target:winexe'
            '/optimize+'
            '/platform:anycpu'
            "/out:`"$outCopy`""
            "/win32icon:`"$IconPath`""
            "/resource:`"$payloadCopy`",payload.zip"
            '/r:System.dll'
            '/r:System.Core.dll'
            '/r:System.IO.Compression.dll'
            '/r:System.IO.Compression.FileSystem.dll'
            "`"$srcCopy`""
            "`"$assemblyInfo`""
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

# Stamp assembly version from VERSION file so Settings + auto-update compare correctly.
# Without this, GitHub tag can be v1.3.7 while FileVersion stays stuck at an old csproj value.
$asmVersion = $Version
# AssemblyVersion needs 4 parts for some hosts; FileVersion/Informational use 3.
$asmFour = if ($asmVersion -match '^\d+\.\d+\.\d+$') { "$asmVersion.0" } else { $asmVersion }

# Build NVIDIA NVAPI display helper into Scripts\Nvidia\tools (no Control Panel UI).
$nvDisplayProj = Join-Path $Root 'tools\OptiHub.NvDisplay\OptiHub.NvDisplay.csproj'
$nvDisplayOut = Join-Path $Root 'OptiHub\Scripts\Nvidia\tools'
if (Test-Path -LiteralPath $nvDisplayProj) {
    Write-Host '[*] Building OptiHub.NvDisplay (NVAPI display helper)...' -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $nvDisplayOut | Out-Null
    Get-ChildItem -LiteralPath $nvDisplayOut -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    & dotnet publish $nvDisplayProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $nvDisplayOut
    if ($LASTEXITCODE -ne 0) { throw "OptiHub.NvDisplay publish failed (exit $LASTEXITCODE)" }
    $nvExe = Join-Path $nvDisplayOut 'OptiHub.NvDisplay.exe'
    if (-not (Test-Path -LiteralPath $nvExe)) { throw "Missing $nvExe after publish" }
    Write-Host "[+] NVAPI helper: $nvExe ($([math]::Round((Get-Item $nvExe).Length/1MB,1)) MB)" -ForegroundColor Green
} else {
    Write-Host "[!] OptiHub.NvDisplay project missing at $nvDisplayProj" -ForegroundColor DarkYellow
}

Write-Host "[*] dotnet publish (Version=$asmVersion)..." -ForegroundColor DarkGray
& dotnet publish $Project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=true `
    -p:Version=$asmVersion `
    -p:AssemblyVersion=$asmFour `
    -p:FileVersion=$asmFour `
    -p:InformationalVersion=$asmVersion `
    -o $OutDir

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)" }

$publishedExe = Join-Path $OutDir 'OptiHub.exe'
if (-not (Test-Path $publishedExe)) { throw "OptiHub.exe not found in $OutDir" }

$fv = (Get-Item $publishedExe).VersionInfo.FileVersion
$pv = (Get-Item $publishedExe).VersionInfo.ProductVersion
Write-Host "[+] Published app folder: $publishedExe" -ForegroundColor Green
Write-Host "[+] Embedded version FileVersion=$fv ProductVersion=$pv (expected $asmVersion)" -ForegroundColor Green
if ($fv -notlike "$asmVersion*") {
    throw "Publish version stamp failed: FileVersion is '$fv' but VERSION file says '$asmVersion'. Fix csproj/publish props."
}

if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Write-Host '[*] Packing payload zip (internal only)...' -ForegroundColor DarkGray
Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

if (Test-Path $SfxPath) { Remove-Item -LiteralPath $SfxPath -Force }
New-OptiHubSfx `
    -PayloadZipPath $ZipPath `
    -OutputExe $SfxPath `
    -SourceCs $SfxSource `
    -AppVersion $Version `
    -IconPath (Join-Path $Root 'OptiHub\Assets\OptiHub.ico')

# Keep intermediate zip on disk for rebuilds; release script ships EXE only.
Write-Host ''
Write-Host 'Done. Double-click install test:' -ForegroundColor Cyan
Write-Host "  Start-Process '$SfxPath'"
Write-Host ''
