param(
    [Parameter(Mandatory)][string]$AppDir
)

$ErrorActionPreference = 'Stop'
$krisp = Join-Path $AppDir 'modules\discord_krisp-1'
if (Test-Path $krisp) {
    Write-Host '[+] Krisp module already installed'
    exit 0
}

function Get-DiscOptTempPath([string]$Child = '') {
    $base = [Environment]::GetEnvironmentVariable('TEMP')
    if ([string]::IsNullOrWhiteSpace($base)) { $base = [IO.Path]::GetTempPath() }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

$manifest = Invoke-RestMethod -Uri 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64' -Headers @{ 'User-Agent' = 'Disc-Optimizer/1.0' }
$url = $manifest.modules.discord_krisp.full.url
if (-not $url) { throw 'discord_krisp missing from manifest' }

$work = Get-DiscOptTempPath 'discopt-krisp'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

$distro = Join-Path $work 'pkg.distro'
$tar = Join-Path $work 'pkg.tar'
$extract = Join-Path $work 'extract'

try {
    Invoke-WebRequest -Uri $url -OutFile $distro -UseBasicParsing

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

    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    $global:LASTEXITCODE = 0
    & tar -xf $tar -C $extract
    if ($LASTEXITCODE -ne 0) { throw 'tar failed while extracting Krisp module' }

    $files = Join-Path $extract 'files'
    if (-not (Test-Path $files)) { throw 'Krisp package had no files/' }

    $modRoot = Join-Path $AppDir 'modules'
    if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot -Force | Out-Null }
    New-Item -ItemType Directory -Path $krisp -Force | Out-Null
    Copy-Item -Path (Join-Path $files '*') -Destination $krisp -Recurse -Force
    Write-Host '[+] Krisp module installed'
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}