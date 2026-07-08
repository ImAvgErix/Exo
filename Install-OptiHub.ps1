# OptiHub installer
$ErrorActionPreference = "Stop"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw "Windows only." }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = "BarcusEric/OptiHub"
$InstallRoot = Join-Path $env:LOCALAPPDATA "OptiHub"
$InstallDir = Join-Path $InstallRoot "app"

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

function Stop-OptiHubProcesses {
  Get-Process -Name "OptiHub" -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      Write-Host "[*] Closing running OptiHub..." -ForegroundColor DarkGray
      $_.CloseMainWindow() | Out-Null
      Start-Sleep -Milliseconds 400
      if (-not $_.HasExited) { $_.Kill() }
      $_.WaitForExit(5000) | Out-Null
    } catch { }
  }
}

function Clear-InstallDir([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }

  # Prefer rename-out so we never fail mid-delete on locked/missing MUI files
  $trash = Join-Path (Split-Path -Parent $Path) ("app.old-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
  try {
    Move-Item -LiteralPath $Path -Destination $trash -Force -ErrorAction Stop
  } catch {
    # Fallback: best-effort recursive delete without aborting the install
    Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue |
      Sort-Object { $_.FullName.Length } -Descending |
      ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Force -Recurse -ErrorAction SilentlyContinue } catch { }
      }
    try { Remove-Item -LiteralPath $Path -Force -Recurse -ErrorAction SilentlyContinue } catch { }
    if (Test-Path -LiteralPath $Path) {
      throw "Could not clear previous install at $Path. Close OptiHub and try again."
    }
    return
  }

  # Delete trash in background-ish best effort; ignore missing-file races
  try {
    Remove-Item -LiteralPath $trash -Recurse -Force -ErrorAction SilentlyContinue
  } catch { }
  if (Test-Path -LiteralPath $trash) {
    Start-Job -ScriptBlock {
      param($p)
      Start-Sleep -Seconds 2
      try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue } catch { }
    } -ArgumentList $trash | Out-Null
  }
}

try {
  Write-Host "[*] Downloading OptiHub $($release.tag_name)..." -ForegroundColor DarkGray
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ "User-Agent" = "OptiHub-Installer/1.0" }

  Stop-OptiHubProcesses
  Start-Sleep -Milliseconds 300

  if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "[*] Replacing previous install..." -ForegroundColor DarkGray
    Clear-InstallDir $InstallDir
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
