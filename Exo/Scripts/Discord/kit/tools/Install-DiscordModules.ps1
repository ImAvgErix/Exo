param(
    [Parameter(Mandatory)][string]$AppDir,
    [string]$Version = '1.0.9244'
)

$ErrorActionPreference = 'Stop'
$modRoot = Join-Path $AppDir 'modules'
if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot -Force | Out-Null }

function Get-DiscOptTempPath([string]$Child = '') {
    $base = [Environment]::GetEnvironmentVariable('TEMP')
    if ([string]::IsNullOrWhiteSpace($base)) { $base = [IO.Path]::GetTempPath() }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

$manifestUrl = 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64'
$manifest = Invoke-RestMethod -Uri $manifestUrl -Headers @{ 'User-Agent' = 'Disc-Optimizer/1.0' }
$exclude = @('discord_hook', 'discord_clips')
$temp = Get-DiscOptTempPath "discopt-modules-$Version"
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
New-Item -ItemType Directory -Path $temp -Force | Out-Null

function Expand-Distro([string]$Url, [string]$WorkDir) {
    $distro = Join-Path $WorkDir 'pkg.distro'
    $tar = Join-Path $WorkDir 'pkg.tar'
    Invoke-WebRequest -Uri $Url -OutFile $distro -UseBasicParsing
    $in = $out = $br = $null
    try {
        $in = [IO.File]::OpenRead($distro)
        $out = [IO.File]::Create($tar)
        $br = [System.IO.Compression.BrotliStream]::new($in, [IO.Compression.CompressionMode]::Decompress)
        $br.CopyTo($out)
    } finally {
        if ($br) { $br.Dispose() }
        if ($out) { $out.Dispose() }
        if ($in) { $in.Dispose() }
    }
    $extract = Join-Path $WorkDir 'extract'
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    $global:LASTEXITCODE = 0
    & tar -xf $tar -C $extract 2>$null
    if ($LASTEXITCODE -ne 0) { throw "tar failed while extracting $Url" }
    return Join-Path $extract 'files'
}

try {
    $count = 0
    foreach ($prop in $manifest.modules.PSObject.Properties) {
        $name = $prop.Name
        if ($exclude -contains $name) { continue }
        $url = $prop.Value.full.url
        if (-not $url) { continue }

        $folder = Join-Path $modRoot "$name-1"
        if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null

        $work = Join-Path $temp $name
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        $files = Expand-Distro $url $work
        if (-not (Test-Path $files)) { throw "No files in module $name" }
        Copy-Item -Path (Join-Path $files '*') -Destination $folder -Recurse -Force
        $count++
        Write-Host "[+] $name"
    }

    Write-Host "[+] Installed $count modules to $modRoot"
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
