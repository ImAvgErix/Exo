#Requires -Version 5.1
<#
.SYNOPSIS
  Download the latest OptiHub Release zip and launch OptiHub.exe.

.EXAMPLE
  irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-OptiHub.ps1" | iex
#>
param(
    [string]$Repo = 'BarcusEric/DiscOpti',
    [string]$InstallDir = '',
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Install-OptiHub.ps1 must be run on Windows.'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'OptiHub\app'
}

Write-Host ''
Write-Host '  OptiHub installer' -ForegroundColor Cyan
Write-Host ''

$api = "https://api.github.com/repos/$Repo/releases/latest"
$headers = @{
    'User-Agent' = 'OptiHub-Installer/1.0'
    'Accept'     = 'application/vnd.github+json'
}

try {
    $release = Invoke-RestMethod -Uri $api -Headers $headers
} catch {
    throw "Could not fetch latest release from $Repo. Build from source with Run-OptiHub.ps1, or check https://github.com/$Repo/releases. ($_)"
}

$asset = @($release.assets) |
    Where-Object { $_.name -match 'OptiHub.*win-x64.*\.zip$' } |
    Select-Object -First 1

if (-not $asset) {
    $asset = @($release.assets) |
        Where-Object { $_.name -like '*.zip' } |
        Select-Object -First 1
}

if (-not $asset) {
    throw "Latest release ($($release.tag_name)) has no zip asset. Publish with Publish-OptiHub.ps1 first."
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('optihub-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$zip = Join-Path $work 'OptiHub.zip'

try {
    Write-Host "[*] Downloading $($asset.name) ($($release.tag_name))..." -ForegroundColor DarkGray
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Installer/1.0' }

    if (Test-Path -LiteralPath $InstallDir) {
        Write-Host '[*] Replacing previous install...' -ForegroundColor DarkGray
        Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    Write-Host '[*] Extracting...' -ForegroundColor DarkGray
    Expand-Archive -Path $zip -DestinationPath $InstallDir -Force

    # Zip may contain a single top-level folder
    $exe = Get-ChildItem -LiteralPath $InstallDir -Filter 'OptiHub.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $exe) {
        throw "OptiHub.exe not found after extract into $InstallDir"
    }

    Write-Host "[+] Installed to $($exe.DirectoryName)" -ForegroundColor Green

    if (-not $NoLaunch) {
        Write-Host '[+] Launching OptiHub...' -ForegroundColor Green
        Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
    }
}
finally {
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
