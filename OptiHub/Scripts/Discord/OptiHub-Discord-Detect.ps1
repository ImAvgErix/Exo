# OptiHub — detect whether Discord Optimizer is already applied.
# Prints a single JSON object to stdout for the WinUI host.

$ErrorActionPreference = 'SilentlyContinue'

$local = [Environment]::GetFolderPath('LocalApplicationData')
$appData = [Environment]::GetFolderPath('ApplicationData')
$discordRoot = Join-Path $local 'Discord'
$equicord = Join-Path $appData 'Equicord'

$checks = New-Object System.Collections.Generic.List[string]
$isApplied = $false
$statusText = 'Ready to optimize'
$detail = 'Run the optimizer to apply performance, privacy, and AMOLED tweaks.'

if (-not (Test-Path $discordRoot)) {
    $checks.Add('Discord folder missing under LocalAppData')
    $statusText = 'Discord not installed'
    $detail = 'Install Discord stable first, or let the optimizer install it.'
} else {
    $app = Get-ChildItem $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
        Select-Object -First 1

    if (-not $app) {
        $checks.Add('No active Discord build')
        $statusText = 'Discord incomplete'
        $detail = 'No app-* folder found.'
    } else {
        $resources = Join-Path $app.FullName 'resources'
        $equicordAsar = Join-Path $equicord 'equicord.asar'
        $appAsar = Join-Path $resources 'app.asar'
        $stock = Join-Path $resources '_app.asar.stock'
        $versionDll = Join-Path $app.FullName 'version.dll'
        $ffmpeg = Join-Path $app.FullName 'ffmpeg.dll'
        $configIni = Join-Path $app.FullName 'config.ini'

        $equicordOk = (Test-Path $equicordAsar) -and (Test-Path $appAsar) -and ((Get-Item $appAsar).Length -lt 4096)
        if ($equicordOk) { $checks.Add('Equicord loader present') } else { $checks.Add('Equicord not detected') }

        $openAsarOk = Test-Path $stock
        if ($openAsarOk) { $checks.Add('OpenASAR stock backup present') } else { $checks.Add('OpenASAR backup not found') }

        $kernelOk = $false
        if ((Test-Path $versionDll) -and (Test-Path $ffmpeg) -and (Test-Path $configIni)) {
            $ffSize = (Get-Item $ffmpeg).Length
            if ($ffSize -lt 500000) {
                $kernelOk = $true
                $checks.Add('DiscOpt kernel on disk')
            } else {
                $checks.Add('Stock ffmpeg.dll (kernel not applied)')
            }
        } else {
            $checks.Add('DiscOpt kernel missing')
        }

        $settingsPath = Join-Path $appData 'discord\settings.json'
        if (Test-Path $settingsPath) {
            try {
                $sj = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($sj.BACKGROUND_COLOR -eq '#000000') { $checks.Add('AMOLED profile flags present') }
                if ($sj.OPEN_ON_STARTUP -eq $false) { $checks.Add('Startup disabled in Discord settings') }
            } catch {}
        }

        $isApplied = [bool]($equicordOk -and ($kernelOk -or $openAsarOk))
        if ($isApplied) {
            $statusText = 'Already optimized'
            $detail = 'Optimizations detected. You can reapply after Discord updates.'
        } else {
            $statusText = 'Ready to optimize'
            $detail = 'Some components are missing. Run to apply or complete setup.'
        }
    }
}

$payload = [ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    checks     = @($checks)
}

$payload | ConvertTo-Json -Compress -Depth 4
