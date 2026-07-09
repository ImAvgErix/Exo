# Legacy entry point — OptiHub is now a double-click OptiHub.exe download.
# This script only downloads and runs the official release EXE.

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'BarcusEric/OptiHub'
Write-Host ''
Write-Host '  OptiHub — downloading OptiHub.exe...' -ForegroundColor Cyan
Write-Host ''

$headers = @{
    'User-Agent' = 'OptiHub-Installer/2.0'
    'Accept'     = 'application/vnd.github+json'
}

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
$asset = @($release.assets) | Where-Object { $_.name -eq 'OptiHub.exe' } | Select-Object -First 1
if (-not $asset) {
    throw "Latest release has no OptiHub.exe. Open: https://github.com/$Repo/releases/latest"
}

$sfx = Join-Path $env:TEMP ('OptiHub-setup-' + [guid]::NewGuid().ToString('N') + '.exe')
Write-Host "[*] $($release.tag_name) → $sfx" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $sfx -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Installer/2.0' }

Write-Host '[*] Launching installer...' -ForegroundColor DarkGray
Start-Process -FilePath $sfx
Write-Host '[+] Done — complete any SmartScreen prompt, then OptiHub should open.' -ForegroundColor Green
Write-Host ''
