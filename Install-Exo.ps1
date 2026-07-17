# Exo bootstrap installer.
# Downloads the latest release Exo.exe from GitHub (verified: size + SHA-256 +
# version stamp), then runs it (installs to %LocalAppData%\Exo\app).
# Optimizer dependencies are prepared only after an explicit Apply/Repair.
# Prefer the double-click asset from Releases when you already have it.
# One-liner stays supported: irm <raw Install-Exo.ps1 url> | iex

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'ImAvgErix/Exo'
Write-Host ''
Write-Host '  Exo - downloading Exo.exe...' -ForegroundColor Cyan
Write-Host ''

$headers = @{
    'User-Agent' = 'Exo-Installer/2.0'
    'Accept'     = 'application/vnd.github+json'
}

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
$releaseVersion = ([string]$release.tag_name).Trim().TrimStart('v', 'V')
$parsedReleaseVersion = $null
if (-not [version]::TryParse($releaseVersion, [ref]$parsedReleaseVersion) -or
    $parsedReleaseVersion.Build -lt 0) {
    throw "Latest release has invalid version metadata: '$($release.tag_name)'"
}
$asset = @($release.assets) | Where-Object { $_.name -eq 'Exo.exe' } | Select-Object -First 1
if (-not $asset) {
    throw "Latest release has no Exo.exe. Open: https://github.com/$Repo/releases/latest"
}

$sfx = Join-Path $env:TEMP ('Exo-setup-' + [guid]::NewGuid().ToString('N') + '.exe')
Write-Host "[*] $($release.tag_name) -> $sfx" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $sfx -UseBasicParsing -Headers @{ 'User-Agent' = 'Exo-Installer/2.0' } -TimeoutSec 300

$downloaded = Get-Item -LiteralPath $sfx
if ($asset.size -and $downloaded.Length -ne [long]$asset.size) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded Exo.exe has the wrong size ($($downloaded.Length); expected $($asset.size))."
}

# GitHub release assets expose a server-computed SHA-256 digest. Require it so a
# corrupted or substituted installer is never launched.
$expectedDigest = [string]$asset.digest
if ($expectedDigest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'GitHub did not provide a valid SHA-256 digest for Exo.exe.'
}
$expectedHash = $expectedDigest.Substring('sha256:'.Length).ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $sfx -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'Downloaded Exo.exe failed its SHA-256 integrity check.'
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
Start-Process -FilePath $sfx
Write-Host '[+] Installer launched - complete any SmartScreen prompt, then Exo should open.' -ForegroundColor Green

Write-Host '[+] Done.' -ForegroundColor Green
Write-Host ''
