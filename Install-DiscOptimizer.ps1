param(
    [string]$Repo = 'BarcusEric/DiscOpti',
    [string]$Branch = 'main',
    [string]$InstallDir = '',
    [switch]$Quick,
    [switch]$FreshInstall,
    [switch]$NoLaunch,
    [string[]]$OptimizerArgs = @()
)

$ErrorActionPreference = 'Stop'

function Wait-DiscOptInstallerClose {
    try {
        Write-Host 'Press Enter to close...'
        Read-Host | Out-Null
    } catch {
        Start-Sleep -Seconds 8
    }
}

function Get-DiscOptDocumentsPath {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    if ([string]::IsNullOrWhiteSpace($docs)) {
        $profile = [Environment]::GetEnvironmentVariable('USERPROFILE')
        if (-not [string]::IsNullOrWhiteSpace($profile)) {
            $docs = Join-Path $profile 'Documents'
        }
    }
    if ([string]::IsNullOrWhiteSpace($docs)) {
        $docs = (Get-Location).Path
    }
    return $docs
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Install-DiscOptimizer.ps1 must be run on Windows.'
    }

    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        $InstallDir = Join-Path (Get-DiscOptDocumentsPath) 'Disc Optimizer'
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $work = Join-Path ([IO.Path]::GetTempPath()) ('discopt-source-' + [guid]::NewGuid().ToString('N'))
    $zip = Join-Path $work 'source.zip'
    $extract = Join-Path $work 'extract'
    New-Item -ItemType Directory -Path $work, $extract -Force | Out-Null

    $branchRef = [uri]::EscapeDataString($Branch)
    $url = "https://codeload.github.com/$Repo/zip/refs/heads/$branchRef"
    Write-Host "[*] Downloading Disc Optimizer from $Repo ($Branch)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'DiscOpt-Installer/1.1' }

    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $source = Get-ChildItem $extract -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Disc Optimizer' -and (Test-Path (Join-Path $_.FullName 'Disc-Optimizer.ps1')) } |
        Select-Object -First 1
    if (-not $source) {
        throw 'Downloaded source did not contain Disc Optimizer/Disc-Optimizer.ps1.'
    }

    $parent = Split-Path $InstallDir -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    Write-Host "[*] Installing/updating files in $InstallDir..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $source.FullName '*') -Destination $InstallDir -Recurse -Force

    $optimizer = Join-Path $InstallDir 'Disc-Optimizer.ps1'
    if (-not (Test-Path $optimizer)) {
        throw "Optimizer was not copied to $optimizer"
    }

    $runArgs = @()
    if ($Quick) { $runArgs += '-Quick' }
    if ($FreshInstall) { $runArgs += '-FreshInstall' }
    if ($NoLaunch) { $runArgs += '-NoLaunch' }
    if ($OptimizerArgs) { $runArgs += $OptimizerArgs }

    Write-Host '[*] Starting Disc Optimizer...' -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $optimizer @runArgs
    $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
    exit $exitCode
} catch {
    Write-Host ''
    Write-Host 'Disc Optimizer installer failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Wait-DiscOptInstallerClose
    exit 1
} finally {
    if ($work -and (Test-Path $work)) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
