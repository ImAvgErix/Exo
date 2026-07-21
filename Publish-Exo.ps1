#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Exo and build a single double-click Exo.exe self-extractor.
  Zip is only used as an intermediate payload (not required as a release asset).

.EXAMPLE
  .\Publish-Exo.ps1
#>
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Project = Join-Path $Root 'Exo\Exo.csproj'
$VersionFile = Join-Path $Root 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '1.0.0' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must contain an exact semantic version (x.y.z); got '$Version'."
}

$ReleaseDir = Join-Path $Root 'release'
$ZipPath = Join-Path $ReleaseDir "Exo-$Version-win-x64.zip"
$SfxPath = Join-Path $ReleaseDir 'Exo.exe'
$OutDir = Join-Path $Root "publish\Exo-win-x64-v$Version"
$LegacyOutDir = Join-Path $Root 'publish\Exo-win-x64'
$SfxSource = Join-Path $Root 'tools\ExoSfx.cs'

Write-Host ''
Write-Host "  Exo publish  -  v$Version  -  self-contained win-x64 -> Exo.exe" -ForegroundColor Cyan
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

function New-ExoSfx {
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

    $work = Join-Path ([IO.Path]::GetTempPath()) ('exo-sfx-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $payloadCopy = Join-Path $work 'payload.zip'
        $srcCopy = Join-Path $work 'ExoSfx.cs'
        $outCopy = Join-Path $work 'Exo.exe'
        $rsp = Join-Path $work 'build.rsp'
        $assemblyInfo = Join-Path $work 'AssemblyInfo.cs'

        Copy-Item -LiteralPath $PayloadZipPath -Destination $payloadCopy -Force
        Copy-Item -LiteralPath $SourceCs -Destination $srcCopy -Force

        $fourPartVersion = if ($AppVersion -match '^\d+\.\d+\.\d+$') { "$AppVersion.0" } else { $AppVersion }
        @"
using System.Reflection;
[assembly: AssemblyTitle("Exo Installer")]
[assembly: AssemblyProduct("Exo")]
[assembly: AssemblyDescription("Exo self-contained Windows installer")]
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

        Write-Host '[*] Building self-extracting Exo.exe...' -ForegroundColor DarkGray
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
        Write-Host "[+] Exo.exe (double-click installer): $OutputExe ($sizeMb MB)" -ForegroundColor Green
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Clear-PublishDir $OutDir)) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $OutDir = Join-Path $Root "publish\Exo-win-x64-v$Version-$stamp"
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
# Framework-dependent (FDD): ~0.7 MB with NvAPIWrapper vs ~70 MB self-contained single-file.
# Host ships with the app's .NET runtime; NvAPIWrapper is not trim-safe so keep PublishTrimmed=false.
$nvDisplayProj = Join-Path $Root 'tools\Exo.NvDisplay\Exo.NvDisplay.csproj'
$nvDisplayOut = Join-Path $Root 'Exo\Scripts\Nvidia\tools'
if (Test-Path -LiteralPath $nvDisplayProj) {
    Write-Host '[*] Building Exo.NvDisplay FDD (NVAPI + DRS helper)...' -ForegroundColor DarkGray
    New-Item -ItemType Directory -Force -Path $nvDisplayOut | Out-Null
    Get-ChildItem -LiteralPath $nvDisplayOut -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    & dotnet publish $nvDisplayProj `
        -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $nvDisplayOut
    if ($LASTEXITCODE -ne 0) { throw "Exo.NvDisplay publish failed (exit $LASTEXITCODE)" }
    $nvExe = Join-Path $nvDisplayOut 'Exo.NvDisplay.exe'
    $nvApi = Join-Path $nvDisplayOut 'NvAPIWrapper.dll'
    if (-not (Test-Path -LiteralPath $nvExe)) { throw "Missing $nvExe after publish" }
    if (-not (Test-Path -LiteralPath $nvApi)) { throw "Missing $nvApi after FDD publish (NVAPI binding)" }
    $nvLen = (Get-Item -LiteralPath $nvExe).Length
    $nvApiLen = (Get-Item -LiteralPath $nvApi).Length
    $nvTotal = $nvLen + $nvApiLen
    # FDD footprint is hundreds of KB; reject empty stubs and accidental 70MB single-file bloat.
    if ($nvLen -lt 50KB) { throw "Exo.NvDisplay.exe too small ($nvLen bytes) - FDD publish may have failed" }
    if ($nvTotal -gt 8MB) { throw "Exo.NvDisplay FDD payload too large ($nvTotal bytes) - expected framework-dependent, not single-file" }
    Write-Host "[+] NVAPI helper FDD: $nvExe ($([math]::Round($nvTotal/1KB,0)) KB total with NvAPIWrapper)" -ForegroundColor Green
} else {
    throw "Exo.NvDisplay project missing at $nvDisplayProj - display Apply will fail on user PCs"
}

# React UI must be in Exo/wwwroot before publish. CI runners have no ui/node_modules
# unless we install here - without this, users get "Exo UI not built".
$uiDir = Join-Path $Root 'ui'
$wwwIndex = Join-Path $Root 'Exo\wwwroot\index.html'
if (-not (Test-Path -LiteralPath (Join-Path $uiDir 'package.json'))) {
    throw "Missing ui/package.json - cannot build product WebView UI."
}
Write-Host '[*] Building React UI (npm ci + npm run build)...' -ForegroundColor DarkGray
Push-Location $uiDir
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit $LASTEXITCODE)" }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $wwwIndex)) {
    throw "Exo/wwwroot/index.html missing after npm run build"
}
Write-Host '[+] React UI built into Exo/wwwroot' -ForegroundColor Green

# WinUI's incremental XAML compiler can retain an obsolete connection-id map
# after named controls move or change type. A normal build may still succeed while
# the packaged XBF crashes during InitializeComponent. Release builds always clean
# the app's generated XAML artifacts before publishing.
Write-Host '[*] Cleaning generated WinUI/XBF artifacts...' -ForegroundColor DarkGray
& dotnet clean $Project -c $Configuration -r win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed ($LASTEXITCODE)" }

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

$publishedExe = Join-Path $OutDir 'Exo.exe'
if (-not (Test-Path $publishedExe)) { throw "Exo.exe not found in $OutDir" }

$publishedWww = Join-Path $OutDir 'wwwroot\index.html'
if (-not (Test-Path -LiteralPath $publishedWww)) {
    throw "Publish check: wwwroot/index.html missing from $OutDir - UI would show 'Exo UI not built' for users."
}
Write-Host '[+] Publish check: wwwroot/index.html packed' -ForegroundColor Green

$fv = (Get-Item $publishedExe).VersionInfo.FileVersion
$pv = (Get-Item $publishedExe).VersionInfo.ProductVersion
Write-Host "[+] Published app folder: $publishedExe" -ForegroundColor Green
Write-Host "[+] Embedded version FileVersion=$fv ProductVersion=$pv (expected $asmVersion)" -ForegroundColor Green
if ($fv -notlike "$asmVersion*") {
    throw "Publish version stamp failed: FileVersion is '$fv' but VERSION file says '$asmVersion'. Fix csproj/publish props."
}

# Self-contained guard: FDD runtimeconfig asks users to install .NET 10 (broken ship).
$runtimeCfg = Join-Path $OutDir 'Exo.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeCfg)) {
    throw 'Publish check: Exo.runtimeconfig.json missing.'
}
$runtimeText = Get-Content -LiteralPath $runtimeCfg -Raw
if ($runtimeText -notmatch 'includedFrameworks') {
    throw 'Publish check: Exo.runtimeconfig.json is framework-dependent (missing includedFrameworks). Users would be prompted to install .NET 10. Re-run with --self-contained true.'
}
if (-not (Test-Path -LiteralPath (Join-Path $OutDir 'coreclr.dll'))) {
    throw 'Publish check: coreclr.dll missing - payload is not self-contained.'
}
Write-Host '[+] Publish check: self-contained runtime (includedFrameworks + coreclr)' -ForegroundColor Green

# Wave-2: scripts + NvDisplay FDD must be inside the published app tree (or beside SFX payload).
$publishedNv = @(
    (Join-Path $OutDir 'Scripts\Nvidia\tools\Exo.NvDisplay.exe'),
    (Join-Path $Root 'Exo\Scripts\Nvidia\tools\Exo.NvDisplay.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $publishedNv) {
    throw 'Wave-2 publish check: Exo.NvDisplay.exe missing after publish (display Apply would fail).'
}
$publishedNvApi = Join-Path (Split-Path -Parent $publishedNv) 'NvAPIWrapper.dll'
if (-not (Test-Path -LiteralPath $publishedNvApi)) {
    throw 'Wave-2 publish check: NvAPIWrapper.dll missing next to Exo.NvDisplay.exe (FDD binding).'
}
$publishedNvLen = (Get-Item -LiteralPath $publishedNv).Length
if ($publishedNvLen -gt 8MB) {
    throw "Wave-2 publish check: Exo.NvDisplay.exe is $publishedNvLen bytes - expected FDD under 8MB, not single-file."
}
Write-Host "[+] Publish check: NvDisplay FDD present ($publishedNv + NvAPIWrapper)" -ForegroundColor Green
$sharedLib = Join-Path $Root 'Exo\Scripts\lib\Exo.Common.ps1'
$noBgLib = Join-Path $Root 'Exo\Scripts\lib\Exo.NoBackground.ps1'
if (-not (Test-Path -LiteralPath $sharedLib) -or -not (Test-Path -LiteralPath $noBgLib)) {
    throw 'Wave-2 publish check: Exo/Scripts/lib shared helpers missing (Exo.Common / Exo.NoBackground).'
}
Write-Host '[+] Publish check: shared script libs present' -ForegroundColor Green

if (Test-Path $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Write-Host '[*] Packing payload zip (internal only)...' -ForegroundColor DarkGray
Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

if (Test-Path $SfxPath) { Remove-Item -LiteralPath $SfxPath -Force }
New-ExoSfx `
    -PayloadZipPath $ZipPath `
    -OutputExe $SfxPath `
    -SourceCs $SfxSource `
    -AppVersion $Version `
    -IconPath (Join-Path $Root 'Exo\Assets\Exo.ico')

# Keep intermediate zip on disk for rebuilds; release script ships EXE only.
Write-Host ''
Write-Host 'Done. Double-click install test:' -ForegroundColor Cyan
Write-Host "  Start-Process '$SfxPath'"
Write-Host ''
