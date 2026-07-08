# OptiHub installer
$ErrorActionPreference = "Stop"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw "Windows only." }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = "BarcusEric/OptiHub"
$InstallDir = Join-Path $env:LOCALAPPDATA "OptiHub\app"

Write-Host ""
Write-Host "  OptiHub installer" -ForegroundColor Cyan
Write-Host ""

$headers = @{
  "User-Agent" = "OptiHub-Installer/1.0"
  "Accept" = "application/vnd.github+json"
}

try {
  $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
} catch {
  throw "Could not fetch latest release from $Repo. $_"
}

$asset = @($release.assets) | Where-Object { $_.name -eq "optihub-build.zip" } | Select-Object -First 1
if (-not $asset) {
  $asset = @($release.assets) | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
}
if (-not $asset) { throw "Latest release has no build asset." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("optihub-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$zip = Join-Path $work "OptiHub.zip"

try {
  Write-Host "[*] Downloading OptiHub $($release.tag_name)..." -ForegroundColor DarkGray
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ "User-Agent" = "OptiHub-Installer/1.0" }

  if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "[*] Replacing previous install..." -ForegroundColor DarkGray
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
  }
  New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

  Write-Host "[*] Installing..." -ForegroundColor DarkGray
  Expand-Archive -Path $zip -DestinationPath $InstallDir -Force

  $exe = Get-ChildItem -LiteralPath $InstallDir -Filter "OptiHub.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $exe) { throw "OptiHub.exe not found in $InstallDir" }

  Write-Host "[+] Installed to $($exe.DirectoryName)" -ForegroundColor Green
  Write-Host "[+] Launching OptiHub..." -ForegroundColor Green
  Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}