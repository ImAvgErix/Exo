# OptiHub installer
# Compatible with Windows PowerShell 5.1 and PowerShell 7+
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = 'BarcusEric/OptiHub'
$RootDir = Join-Path $env:LOCALAPPDATA 'OptiHub'
$InstallDir = Join-Path $RootDir 'app'

Write-Host ''
Write-Host '  OptiHub installer' -ForegroundColor Cyan
Write-Host ''

$headers = @{
  'User-Agent' = 'OptiHub-Installer/1.0'
  'Accept' = 'application/vnd.github+json'
}

try {
  $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
} catch {
  throw "Could not fetch latest release from $Repo. $_"
}

$asset = @($release.assets) | Where-Object { $_.name -eq 'optihub-build.zip' } | Select-Object -First 1
if (-not $asset) {
  $asset = @($release.assets) | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
}
if (-not $asset) { throw 'Latest release has no build asset.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('optihub-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$zip = Join-Path $work 'OptiHub.zip'

function Stop-OptiHubProcesses {
  for ($i = 0; $i -lt 20; $i++) {
    $procs = @(Get-Process OptiHub -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { break }
    foreach ($p in $procs) {
      try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 400
    foreach ($p in @(Get-Process OptiHub -ErrorAction SilentlyContinue)) {
      try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Milliseconds 300
  }
  Start-Sleep -Milliseconds 500
}

function Remove-TreeBestEffort([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  # Never let cleanup abort the install (PS7 treats some Remove-Item races as terminating)
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'SilentlyContinue'
  try {
    cmd.exe /c "rmdir /s /q `"$Path`"" | Out-Null
  } catch { }
  if (Test-Path -LiteralPath $Path) {
    try { [IO.Directory]::Delete($Path, $true) } catch { }
  }
  if (Test-Path -LiteralPath $Path) {
    $stamp = Get-Date -Format 'yyyyMMddHHmmss'
    $backup = "$Path.old-$stamp"
    try {
      Move-Item -LiteralPath $Path -Destination $backup -Force -ErrorAction SilentlyContinue
      Start-Job -ScriptBlock {
        param($p)
        Start-Sleep -Seconds 10
        cmd.exe /c "rmdir /s /q `"$p`"" | Out-Null
      } -ArgumentList $backup | Out-Null
    } catch { }
  }
  $ErrorActionPreference = $prev
}

try {
  Write-Host "[*] Downloading OptiHub $($release.tag_name)..." -ForegroundColor DarkGray
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-Installer/1.0' }

  Write-Host '[*] Preparing install...' -ForegroundColor DarkGray
  Stop-OptiHubProcesses

  # Extract to a clean staging folder first. Expand-Archive -Force into an existing
  # app dir calls Remove-Item on locale .mui files and can fail on PS7 races.
  Expand-Archive -Path $zip -DestinationPath $stage -Force

  $exe = Get-ChildItem -LiteralPath $stage -Filter 'OptiHub.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $exe) { throw "OptiHub.exe not found in downloaded package" }

  # Stage contents may be nested (zip root folder) or flat
  $payload = $exe.Directory.FullName

  if (-not (Test-Path -LiteralPath $RootDir)) {
    New-Item -ItemType Directory -Path $RootDir -Force | Out-Null
  }

  Write-Host '[*] Installing...' -ForegroundColor DarkGray
  if (Test-Path -LiteralPath $InstallDir) {
    Remove-TreeBestEffort $InstallDir
  }

  # Prefer atomic-ish move from stage; fall back to copy
  $moved = $false
  try {
    Move-Item -LiteralPath $payload -Destination $InstallDir -Force -ErrorAction Stop
    $moved = $true
  } catch {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $payload '*') -Destination $InstallDir -Recurse -Force
  }

  $finalExe = Join-Path $InstallDir 'OptiHub.exe'
  if (-not (Test-Path -LiteralPath $finalExe)) {
    $found = Get-ChildItem -LiteralPath $InstallDir -Filter 'OptiHub.exe' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw "OptiHub.exe missing after install at $InstallDir" }
    $finalExe = $found.FullName
    $InstallDir = $found.DirectoryName
  }

  Write-Host "[+] Installed to $InstallDir" -ForegroundColor Green
  Write-Host '[+] Launching OptiHub...' -ForegroundColor Green
  Start-Process -FilePath $finalExe -WorkingDirectory $InstallDir
}
finally {
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'SilentlyContinue'
  try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
  $ErrorActionPreference = $prev
}