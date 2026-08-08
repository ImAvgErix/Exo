# Exo Hub bootstrap installer.
# Downloads the latest release SFX from GitHub (verified: size + SHA-256 +
# version stamp), then runs it (installs to %LocalAppData%\Exo\app).
# Prefer ExoHub.exe; accept legacy Exo.exe so older mirrors still install.
# The SFX auto-installs machine deps: .NET 10 Desktop Runtime, WebView2,
# PowerShell 7, and VC++ redistributable. Optimizer kits still wait for Apply/Repair.
# Prefer the double-click asset from Releases when you already have it.
# One-liner stays supported: irm <raw Install-Exo.ps1 url> | iex
#
# -Force installs the published release even when it is OLDER than what is already
# installed. Without it this script refuses to go backwards: it fetches whatever
# GitHub calls "latest", so running it on a machine carrying a newer local build
# silently downgraded that machine and rolled back every fix the build carried.
param([switch]$Force)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'ImAvgErix/ExoHub'
Write-Host ''
Write-Host '  Exo Hub - downloading installer...' -ForegroundColor Cyan
Write-Host ''

$headers = @{
    'User-Agent' = 'ExoHub-Installer/2.1'
    'Accept'     = 'application/vnd.github+json'
}

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
$releaseVersion = ([string]$release.tag_name).Trim().TrimStart('v', 'V')
$parsedReleaseVersion = $null
if (-not [version]::TryParse($releaseVersion, [ref]$parsedReleaseVersion) -or
    $parsedReleaseVersion.Build -lt 0) {
    throw "Latest release has invalid version metadata: '$($release.tag_name)'"
}
# Product ships as ExoHub.exe; keep Exo.exe as a legacy alias for mirrors/older tags.
$asset = @($release.assets) |
    Where-Object { $_.name -in @('ExoHub.exe', 'Exo.exe') } |
    Sort-Object { if ($_.name -eq 'ExoHub.exe') { 0 } else { 1 } } |
    Select-Object -First 1
if (-not $asset) {
    throw "Latest release has no ExoHub.exe (or Exo.exe). Open: https://github.com/$Repo/releases/latest"
}
$assetLabel = [string]$asset.name
Write-Host "  Asset: $assetLabel" -ForegroundColor DarkGray


# Never go backwards without being told to. "Latest release" is not the same thing as
# "newer than this machine": a rig running a local build is ahead of every published
# release, and installing over it rolls back every fix that build carried.
$installedExe = Join-Path $env:LOCALAPPDATA 'Exo\app\Exo.exe'
if (-not $Force -and (Test-Path -LiteralPath $installedExe)) {
    $installedRaw = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    $parsedInstalled = $null
    if ([version]::TryParse(([string]$installedRaw).Trim(), [ref]$parsedInstalled) -and
        $parsedInstalled -gt $parsedReleaseVersion) {
        Write-Host ''
        Write-Host "  Installed $parsedInstalled is NEWER than published release $parsedReleaseVersion." -ForegroundColor Yellow
        Write-Host '  Nothing installed. Re-run with -Force to roll back on purpose.' -ForegroundColor Yellow
        Write-Host ''
        return
    }
}

$sfx = Join-Path $env:TEMP ('ExoHub-setup-' + [guid]::NewGuid().ToString('N') + '.exe')
Write-Host "[*] $($release.tag_name) -> $sfx" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $sfx -UseBasicParsing -Headers @{ 'User-Agent' = 'ExoHub-Installer/2.1' } -TimeoutSec 300

$downloaded = Get-Item -LiteralPath $sfx
if ($asset.size -and $downloaded.Length -ne [long]$asset.size) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded $assetLabel has the wrong size ($($downloaded.Length); expected $($asset.size))."
}

# GitHub release assets expose a server-computed SHA-256 digest. Require it so a
# corrupted or substituted installer is never launched.
$expectedDigest = [string]$asset.digest
if ($expectedDigest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "GitHub did not provide a valid SHA-256 digest for $assetLabel."
}
$expectedHash = $expectedDigest.Substring('sha256:'.Length).ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $sfx -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded $assetLabel failed its SHA-256 integrity check."
}

$fileVersionText = (Get-Item -LiteralPath $sfx).VersionInfo.FileVersion
$parsedFileVersion = $null
$versionMismatch = -not [version]::TryParse($fileVersionText, [ref]$parsedFileVersion) -or
    $parsedFileVersion.Major -ne $parsedReleaseVersion.Major -or
    $parsedFileVersion.Minor -ne $parsedReleaseVersion.Minor -or
    $parsedFileVersion.Build -ne $parsedReleaseVersion.Build
# Releases before 1.5.0 used an unstamped 0.0.0.0 SFX. Keep the legacy
# bootstrap usable until 1.5.0 is published; all new installers must match.
if ($parsedReleaseVersion -ge [version]'1.5.0' -and $versionMismatch) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded installer version '$fileVersionText' does not match release '$releaseVersion'."
}

Write-Host '[*] Launching installer...' -ForegroundColor DarkGray
# NSIS release assets honor /S; the managed ExoSfx honors /silent. Pass both so either packaging works.
Start-Process -FilePath $sfx -ArgumentList @('/S', '/silent')
Write-Host '[+] Installer launched - complete any SmartScreen prompt, then Exo Hub should open.' -ForegroundColor Green

Write-Host '[+] Done.' -ForegroundColor Green
Write-Host ''
