# OptiHub - detect whether Discord Optimizer is already applied.
# Prints a single JSON object to stdout for the WinUI host.

$ErrorActionPreference = 'SilentlyContinue'

$local = [Environment]::GetFolderPath('LocalApplicationData')
$appData = [Environment]::GetFolderPath('ApplicationData')
$discordRoot = Join-Path $local 'Discord'
$equicord = Join-Path $appData 'Equicord'

$features = New-Object System.Collections.Generic.List[hashtable]
$isApplied = $false
$statusText = 'Ready to optimize'
$detail = 'Run the optimizer to cut Discord memory use, speed startup, and quiet background noise.'

function Add-Feature([string]$Title, [string]$Detail, [bool]$Active) {
    $script:features.Add(@{
        title  = $Title
        detail = $Detail
        active = $Active
    })
}

if (-not (Test-Path $discordRoot)) {
    $statusText = 'Discord not installed'
    $detail = 'Install Discord stable first, or let the optimizer install it for you.'
    Add-Feature 'Discord install' 'Stable Discord is required before optimizations can apply.' $false
} else {
    $app = Get-ChildItem $discordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
        Select-Object -First 1

    if (-not $app) {
        $statusText = 'Discord incomplete'
        $detail = 'No active Discord build folder was found.'
        Add-Feature 'Discord build' 'No app-* folder under LocalAppData\Discord.' $false
    } else {
        $resources = Join-Path $app.FullName 'resources'
        $equicordAsar = Join-Path $equicord 'equicord.asar'
        $appAsar = Join-Path $resources 'app.asar'
        $openAsarTarget = Join-Path $resources '_app.asar'
        $versionDll = Join-Path $app.FullName 'version.dll'
        $ffmpeg = Join-Path $app.FullName 'ffmpeg.dll'
        $configIni = Join-Path $app.FullName 'config.ini'

        $equicordOk = (Test-Path $equicordAsar) -and (Test-Path $appAsar) -and ((Get-Item $appAsar).Length -lt 4096)
        Add-Feature 'Client mods & privacy' 'Equicord loads privacy plugins and strips noisy telemetry.' $equicordOk

        $openAsarOk = $false
        if (Test-Path $openAsarTarget) {
            $sz = (Get-Item $openAsarTarget).Length
            if ($sz -gt 10000 -and $sz -lt 500000) { $openAsarOk = $true }
        }
        Add-Feature 'Faster Discord startup' 'OpenASAR replaces the heavy launcher path so Discord opens quicker.' $openAsarOk

        $kernelOk = $false
        if ((Test-Path $versionDll) -and (Test-Path $ffmpeg) -and (Test-Path $configIni)) {
            $ffSize = (Get-Item $ffmpeg).Length
            if ($ffSize -lt 500000) { $kernelOk = $true }
        }
        Add-Feature 'Lower memory use' 'DiscOpt kernel trims idle RAM and keeps Discord on a higher process priority.' $kernelOk

        $amoledOk = $false
        $startupOk = $false
        $settingsPath = Join-Path $appData 'discord\settings.json'
        if (Test-Path $settingsPath) {
            try {
                $sj = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($sj.BACKGROUND_COLOR -eq '#000000') { $amoledOk = $true }
                if ($sj.OPEN_ON_STARTUP -eq $false) { $startupOk = $true }
            } catch {}
        }
        Add-Feature 'True black AMOLED theme' 'Pure black UI saves OLED power and cuts eye strain at night.' $amoledOk
        Add-Feature 'Quieter Windows startup' 'Discord stays closed on boot so it is not sitting in the tray.' $startupOk

        $isApplied = [bool]($equicordOk -and ($kernelOk -or $openAsarOk))
        if ($isApplied) {
            $statusText = 'Already optimized'
            $detail = 'These savings are active. Reapply after Discord updates itself.'
        } else {
            $statusText = 'Ready to optimize'
            $detail = 'Some pieces are missing. Run to finish setup and unlock the savings below.'
        }
    }
}

$payload = [ordered]@{
    isApplied  = $isApplied
    statusText = $statusText
    detail     = $detail
    features   = @($features)
}

$payload | ConvertTo-Json -Compress -Depth 5
