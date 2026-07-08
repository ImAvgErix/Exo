#Requires -Version 5.1
<#
.SYNOPSIS
  Legacy Documents-folder installer for Discord scripts only.
  Prefer OptiHub (Install-OptiHub.ps1) for the full app experience.
#>
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

function Find-DiscordScriptsRoot([string]$extractRoot) {
    $modern = Get-ChildItem $extractRoot -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -eq 'Discord' -and
            (Test-Path (Join-Path $_.FullName 'Disc-Optimizer.ps1')) -and
            $_.FullName -match '[\\/]Scripts[\\/]'
        } |
        Select-Object -First 1
    if ($modern) { return $modern.FullName }

    $legacy = Get-ChildItem $extractRoot -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Disc Optimizer' -and (Test-Path (Join-Path $_.FullName 'Disc-Optimizer.ps1')) } |
        Select-Object -First 1
    if ($legacy) { return $legacy.FullName }

    $any = Get-ChildItem $extractRoot -Filter 'Disc-Optimizer.ps1' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($any) { return $any.DirectoryName }
    return $null
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Install-DiscOptimizer.ps1 must be run on Windows.'
    }

    Write-Host '[!] Prefer OptiHub: irm .../Install-OptiHub.ps1 | iex' -ForegroundColor Yellow

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
    Write-Host "[*] Downloading Discord kit from $Repo ($Branch)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-DiscordKit/1.0' }

    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $sourcePath = Find-DiscordScriptsRoot $extract
    if (-not $sourcePath) {
        throw 'Downloaded source did not contain Disc-Optimizer.ps1.'
    }

    $parent = Split-Path $InstallDir -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    Write-Host "[*] Installing Discord kit to $InstallDir..." -ForegroundColor Cyan
    robocopy $sourcePath $InstallDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null

    $optimizer = Join-Path $InstallDir 'Disc-Optimizer.ps1'
    if (-not (Test-Path $optimizer)) {
        throw "Disc-Optimizer.ps1 missing after install into $InstallDir"
    }

    $args = @()
    if ($Quick) { $args += '-Quick' }
    if ($FreshInstall) { $args += '-FreshInstall' }
    if ($NoLaunch) { $args += '-NoLaunch' }
    if ($OptimizerArgs) { $args += $OptimizerArgs }

    Write-Host '[*] Running Disc-Optimizer.ps1...' -ForegroundColor Cyan
    & $optimizer @args
}
finally {
    if ($work -and (Test-Path $work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
