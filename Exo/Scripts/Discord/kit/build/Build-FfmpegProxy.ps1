# Builds ffmpeg.dll proxy for Disc Optimizer (loads version.dll + early priority).
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$KitRoot = Split-Path $Root -Parent

function Get-DiscOptEnvPath([string]$Name, [string]$Child = '') {
    $base = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($base)) { return $null }
    if ([string]::IsNullOrWhiteSpace($Child)) { return $base }
    return (Join-Path $base $Child)
}

$LocalAppData = Get-DiscOptEnvPath 'LOCALAPPDATA'
if (-not $LocalAppData) { throw 'LOCALAPPDATA is not set; this build helper must run on Windows.' }

$Bin = Get-ChildItem (Join-Path $LocalAppData 'Microsoft\WinGet\Packages\BrechtSanders.WinLibs*\mingw64\bin') -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $Bin) {
    throw 'MinGW not found. Run: winget install BrechtSanders.WinLibs.POSIX.UCRT.LLVM'
}

$Gcc = Join-Path $Bin.FullName 'gcc.exe'
$Gendef = Join-Path $Bin.FullName 'gendef.exe'

$discordApp = Get-ChildItem (Join-Path $LocalAppData 'Discord') -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
    Select-Object -First 1

if (-not $discordApp) { throw 'Install Discord first, then run Discord-Optimizer.ps1' }

$appDir = $discordApp.FullName
$real = Join-Path $appDir 'ffmpeg_real.dll'
$current = Join-Path $appDir 'ffmpeg.dll'

if (Test-Path $real) { $sourceFfmpeg = $real }
elseif ((Test-Path $current) -and (Get-Item $current).Length -gt 500000) { $sourceFfmpeg = $current }
else { throw 'Cannot find stock ffmpeg.dll' }

Push-Location $Root
try {
    $null = cmd /c "`"$Gendef`" `"$sourceFfmpeg`" 2>nul"
    if (-not (Test-Path 'ffmpeg.def')) { throw 'gendef failed' }

    $forward = Get-Content 'ffmpeg.def' | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*$' -and $_ -notmatch '^(LIBRARY|EXPORTS|;|\s*$)') {
            "$($Matches[1])=ffmpeg_real.$($Matches[1])"
        } else { $_ }
    }
    $forward | Set-Content 'ffmpeg_proxy.def' -Encoding ASCII

    & $Gcc -shared -O2 -s `
        -o (Join-Path $KitRoot 'ffmpeg.dll') `
        'ffmpeg_proxy.c' 'ffmpeg_proxy.def' `
        '-Wl,--enable-auto-import' '-static-libgcc'

    Write-Host "Built $(Join-Path $KitRoot 'ffmpeg.dll')"
} finally {
    Pop-Location
}
