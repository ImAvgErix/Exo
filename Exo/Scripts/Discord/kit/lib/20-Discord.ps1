# 20-Discord.ps1 - Discord install, modules, kit integrity
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Get-ActiveApp {
    if (-not $DiscordRoot -or -not (Test-Path -LiteralPath $DiscordRoot)) { return $null }
    Get-ChildItem -LiteralPath $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object {
            $parsed = [version]'0.0.0.0'
            [void][version]::TryParse(($_.Name -replace '^app-', ''), [ref]$parsed)
            $parsed
        } -Descending |
        Select-Object -First 1
}

function Get-FolderSize([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    try {
        (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
    } catch { 0 }
}

function Remove-Safe([string]$Path, [ref]$Freed) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $item) { return $false }
    $size = if ($item.PSIsContainer) { Get-FolderSize $Path } else { $item.Length }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Path) {
        Write-Warn "Could not fully remove $Path"
        return $false
    }
    $Freed.Value += [long]$size
    return $true
}

function Stop-Discord {
    # Hard-kill Discord/Update so resources\app.asar is not locked during Equicord/OpenASAR writes.
    # Update.exe is a common process name, so path-scope every candidate to this
    # Discord install before terminating it. Never use image-name taskkill here.
    $names = @('Discord', 'Discord.bin', 'Update')
    $rootPrefix = $null
    try { $rootPrefix = [IO.Path]::GetFullPath($DiscordRoot).TrimEnd('\') + '\' } catch { }
    for ($round = 1; $round -le 4; $round++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {
            try {
                $path = $_.Path
                if ($path -and $rootPrefix) {
                    return [IO.Path]::GetFullPath($path).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
                }
            } catch { }
            # Discord/Discord.bin are product-specific; do not fall back for the
            # generic Update name when its executable path cannot be confirmed.
            return $_.ProcessName -in @('Discord', 'Discord.bin')
        })
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            try { & taskkill.exe /F /T /PID $p.Id 2>$null | Out-Null } catch { }
        }
        Start-Sleep -Milliseconds (175 * $round)
    }
}

function Write-DiscordResourceBytes {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes,
        [int]$Attempts = 12
    )

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    for ($i = 1; $i -le $Attempts; $i++) {
        Stop-Discord
        Write-HubProgress 57 ("Rewriting Discord resources ($i/$Attempts)...")
        try {
            if (Test-Path -LiteralPath $Path) {
                attrib -R -S -H "$Path" 2>$null
                try { & takeown.exe /F "$Path" /A 2>$null | Out-Null } catch { }
                try { & icacls.exe "$Path" /grant "*S-1-5-32-544:F" /C 2>$null | Out-Null } catch { }
                try {
                    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
                    if ($sid) { & icacls.exe "$Path" /grant "*$sid`:F" /C 2>$null | Out-Null }
                } catch { }

                $bak = "$Path.lockbak"
                try {
                    if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue }
                    Move-Item -LiteralPath $Path -Destination $bak -Force -ErrorAction Stop
                    Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
                } catch {
                    try { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop } catch { }
                }
            }

            $tmp = "$Path.tmpwrite"
            if (Test-Path -LiteralPath $tmp) {
                Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }

            # Prefer FileStream with share-none so we fail fast if still locked.
            $fs = [IO.File]::Open($tmp, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $fs.Write($Bytes, 0, $Bytes.Length)
                $fs.Flush($true)
            } finally {
                $fs.Dispose()
            }

            if (Test-Path -LiteralPath $Path) {
                attrib -R -S -H "$Path" 2>$null
                Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            }
            Move-Item -LiteralPath $tmp -Destination $Path -Force
            if ((Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path).Length -eq $Bytes.Length)) {
                return
            }
            throw "Write verify failed for $Path"
        } catch {
            try { Remove-Item -LiteralPath "$Path.tmpwrite" -Force -ErrorAction SilentlyContinue } catch { }
            if ($i -ge $Attempts) { throw }
            Write-Warn ("Waiting for Discord to release file lock ($i/$Attempts): " + $_.Exception.Message)
            Start-Sleep -Milliseconds (400 * $i)
        }
    }
}

function Test-DiscordReady {
    if (-not (Test-Path $DiscordRoot)) { return $false }
    if (-not (Test-Path (Join-Path $DiscordRoot 'Update.exe'))) { return $false }
    $app = Get-ActiveApp
    if (-not $app) { return $false }
    if (-not (Test-Path (Join-Path $app.FullName 'Discord.exe'))) { return $false }
    # Broken installs often keep Discord.exe but wipe/corrupt resources/app.asar
    $resources = Join-Path $app.FullName 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    if (-not ((Test-Path $resources) -and (Test-Path $appAsar))) { return $false }
    # 1-byte stubs are corrupt - treat as not ready so repair can restore stock
    return ((Get-Item $appAsar).Length -ge 64)
}

function Confirm-WindowsDiscordTarget {
    $os64 = [Environment]::Is64BitOperatingSystem
    if (-not $os64) {
        throw 'Disc Optimizer requires 64-bit Windows. Discord desktop is x64-only.'
    }
    Write-Ok 'Target: Discord stable x64 for Windows'
}

function Test-ValidDiscordSetup([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -le 50000000) { return $false }
    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
        return $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
            $signature.SignerCertificate.Subject -match '(?i)\bDiscord\b'
    } catch {
        return $false
    }
}

function Test-ValidEquicordAsar([string]$Path) {
    return (Test-Path $Path) -and (Get-Item $Path).Length -gt 1000000
}

function Get-BundledDiscordSetup {
    foreach ($name in @('DiscordSetup.exe', 'DiscordSetup-x64.exe')) {
        $path = Join-Path $ToolsDir $name
        if (Test-ValidDiscordSetup $path) { return $path }
    }
    return $null
}

function Test-WingetAvailable {
    try {
        $cmd = Get-Command winget -ErrorAction SilentlyContinue
        return [bool]$cmd
    } catch { return $false }
}

function Install-DiscordViaWinget {
    if (-not (Test-WingetAvailable)) { return $false }
    Write-Step 'Installing Discord via winget (no bulky local installer)...'
    Write-HubProgress 22 'Installing Discord (winget)...'
    $prev = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & winget install --id Discord.Discord -e --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Out-Null
        $code = $LASTEXITCODE
        # 0 = installed, -1978335189 / other codes can mean already installed
        if (Test-DiscordReady) {
            Write-Ok 'Discord ready via winget'
            return $true
        }
        if ($code -eq 0 -and (Wait-DiscordReady 120)) {
            Write-Ok 'Discord installed via winget'
            return $true
        }
    } catch {
        Write-Warn "winget Discord install failed: $($_.Exception.Message)"
    } finally {
        $ErrorActionPreference = $prev
    }
    return $false
}

function Get-DiscordSetup {
    if (-not (Test-Path $DownloadDir)) {
        New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
    }

    $bundled = Get-BundledDiscordSetup
    if ($bundled) {
        Write-Ok "Using bundled Discord installer ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB from tools/)"
        Write-LogLine 'DIAG' "DiscordSetup path=$bundled size=$((Get-Item $bundled).Length)"
        return ,([string]$bundled)
    }

    $cached = Join-Path $DownloadDir 'DiscordSetup-x64.exe'
    if (Test-ValidDiscordSetup $cached) {
        $age = (Get-Date) - (Get-Item $cached).LastWriteTime
        if ($age.TotalDays -lt 7) {
            Write-Ok "Using cached x64 installer ($([math]::Round((Get-Item $cached).Length / 1MB, 1)) MB)"
            Write-LogLine 'DIAG' "DiscordSetup cached path=$cached size=$((Get-Item $cached).Length)"
            return ,([string]$cached)
        }
    }

    Write-Step 'Downloading latest Discord stable x64...'
    $ua = @{ 'User-Agent' = 'Exo/1.0 (Windows; PowerShell)' }
    $partial = "$cached.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    try {
        Invoke-WebRequest -Uri $DiscordSetupUrl -OutFile $partial -UseBasicParsing -Headers $ua -TimeoutSec 180
        if (-not (Test-ValidDiscordSetup $partial)) {
            throw 'downloaded installer is incomplete or its Discord signature is invalid'
        }
        Move-Item -LiteralPath $partial -Destination $cached -Force
    } catch {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        throw "Discord x64 installer download failed: $($_.Exception.Message)"
    }
    Write-Ok "Downloaded x64 installer ($([math]::Round((Get-Item $cached).Length / 1MB, 1)) MB temp cache)"
    Write-LogLine 'DIAG' "DiscordSetup downloaded path=$cached size=$((Get-Item $cached).Length)"
    return ,([string]$cached)
}

function Get-ModulesBundleDir {
    return Join-Path $ToolsDir 'discord-modules'
}

function Get-ModulesBundleVersion {
    $versionFile = Join-Path (Get-ModulesBundleDir) 'version.txt'
    if (-not (Test-Path $versionFile)) { return $null }
    return (Get-Content $versionFile -Raw).Trim()
}

function Test-ModulesBundleReady {
    $bundleDir = Get-ModulesBundleDir
    if (-not (Test-Path $bundleDir)) { return $false }
    foreach ($name in $RequiredModules) {
        if (-not (Test-Path (Join-Path $bundleDir $name))) { return $false }
    }
    return $true
}

function Export-DiscordModulesBundle([string]$AppDir) {
    # Intentionally no-op: shipping multi-MB module trees bloats Exo.
    # Modules come from Discord's own updater / CDN on demand.
    return
}

function Restore-DiscordModulesBundle([string]$AppDir) {
    # Legacy kits may still have tools/discord-modules - never restore stale ones.
    return $false
}

function Get-BundledEquicordAsar {
    foreach ($name in @('desktop.asar', 'equicord.asar')) {
        $path = Join-Path $ToolsDir $name
        if (Test-ValidEquicordAsar $path) { return $path }
    }
    return $null
}

function Get-BundledOpenAsar {
    foreach ($name in @('openasar.asar', 'OpenAsar.asar')) {
        $path = Join-Path $ToolsDir $name
        if ((Test-Path $path) -and (Get-Item $path).Length -gt 10000 -and (Get-Item $path).Length -lt 500000) {
            return $path
        }
    }
    return $null
}

function Test-EquicordLoaderPatched([string]$AppDir) {
    $appAsar = Join-Path $AppDir 'resources\app.asar'
    if (-not (Test-Path $appAsar)) { return $false }
    $len = (Get-Item $appAsar).Length
    # Real Equicord stub is small but not empty/corrupt (1-byte stubs were skipping repair).
    return ($len -ge 64 -and $len -lt 4096)
}

function Resolve-EquicordDesktopAsar([string]$DestPath) {
    # Prefer already-installed/cached Equicord under %APPDATA% (no kit bloat).
    if (Test-ValidEquicordAsar $DestPath) {
        $ageHours = ((Get-Date) - (Get-Item $DestPath).LastWriteTime).TotalHours
        if ($ageHours -lt 168) {
            $size = (Get-Item $DestPath).Length
            Write-Ok "Using cached Equicord ($([math]::Round($size / 1MB, 1)) MB)"
            return @{ Tag = 'cached'; Size = $size; Source = 'cache' }
        }
    }

    $bundled = Get-BundledEquicordAsar
    if ($bundled) {
        Copy-Item $bundled $DestPath -Force
        Write-Ok "Using bundled Equicord ($([math]::Round((Get-Item $bundled).Length / 1MB, 1)) MB from tools/)"
        return @{ Tag = 'bundled'; Size = (Get-Item $bundled).Length; Source = 'tools' }
    }

    try {
        Write-Step 'Downloading latest Equicord desktop.asar...'
        Write-HubProgress 56 'Downloading Equicord...'
        $result = Get-EquicordReleaseFile -FileName 'desktop.asar' -OutFile $DestPath
        return $result
    } catch {
        if (Test-ValidEquicordAsar $DestPath) {
            $cached = (Get-Item $DestPath).Length
            Write-Warn "Download failed - using cached equicord.asar ($([math]::Round($cached / 1MB, 1)) MB)"
            Write-LogLine 'WARN' "Equicord download failed, using cache: $($_.Exception.Message)"
            return @{ Tag = 'cached'; Size = $cached; Source = 'cache' }
        }
        throw
    }
}

function Wait-DiscordReady {
    param([int]$TimeoutSec = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-DiscordReady) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Test-DiscordModulesReady([string]$AppDir) {
    $modPath = Join-Path $AppDir 'modules'
    if (-not (Test-Path $modPath)) { return $false }
    foreach ($name in $RequiredModules) {
        if (-not (Test-Path (Join-Path $modPath $name))) { return $false }
    }
    return $true
}

function Use-StockDiscordRuntime([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    $innerAsar = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'
    $equilotBackup = Join-Path $resources 'app.asar.backup'

    if ((Test-Path $appAsar) -and (Get-Item $appAsar).Length -lt 4096) {
        if (Test-Path $stockBackup) {
            Copy-Item $stockBackup $appAsar -Force
        } elseif (Test-Path $equilotBackup) {
            Copy-Item $equilotBackup $appAsar -Force
        } elseif (Test-Path $innerAsar) {
            $inner = Get-Item $innerAsar
            if ($inner.Length -gt 1000000) { Copy-Item $inner.FullName $appAsar -Force }
        }
    }

    if ((Test-Path $innerAsar) -and (Get-Item $innerAsar).Length -lt 500000) {
        if (Test-Path $stockBackup) {
            Copy-Item $stockBackup $innerAsar -Force
        } elseif (Test-Path $equilotBackup) {
            Copy-Item $equilotBackup $innerAsar -Force
        }
    }

    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) {
        Copy-Item $ffmpegReal (Join-Path $AppDir 'ffmpeg.dll') -Force
    }

    foreach ($name in @('version.dll', 'config.ini')) {
        $path = Join-Path $AppDir $name
        if (Test-Path $path) {
            $disabled = "$path.disabled"
            if (Test-Path $disabled) { Remove-Item $disabled -Force }
            Rename-Item $path $disabled -Force -ErrorAction SilentlyContinue
        }
    }
}

function Restore-StockDiscordBase {
    if (-not (Test-DiscordReady)) { return }
    $app = Get-ActiveApp
    Write-Step 'Restoring stock Discord base (default, before updates/mods)...'
    Use-StockDiscordRuntime $app.FullName
    Write-Ok 'Stock Discord base restored'
}

function Update-DiscordSilent {
    Repair-DiscordInstallerState
    $setup = Resolve-DiscordSetupPath
    $before = if (Test-DiscordReady) { (Get-ActiveApp).Name } else { $null }

    Write-Step 'Updating Discord to latest stable x64 (silent, keeps your install)...'
    Write-LogLine 'DIAG' "Update DiscordSetup FilePath=$setup"
    $proc = Start-Process -FilePath $setup -ArgumentList '-s' -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit(300000)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'Discord update installer timed out after 5 minutes'
    }
    if ($null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
        Write-Warn "DiscordSetup exited with code $($proc.ExitCode)"
    }

    if (-not (Wait-DiscordReady 120)) {
        throw 'Discord update timed out - check internet or put DiscordSetup-x64.exe in tools/'
    }

    $after = (Get-ActiveApp).Name
    if ($before -and $before -ne $after) {
        Write-Ok "Discord updated: $before -> $after"
    } else {
        Write-Ok "Discord up to date ($after)"
    }
}

function Invoke-SquirrelFirstRun([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path $exe)) { return }
    Write-Step 'Discord first-run init (-squirrel-firstrun)...'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '-squirrel-firstrun'
    $psi.WorkingDirectory = $AppDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $null = $proc.StandardError.ReadToEndAsync()
    $null = $proc.StandardOutput.ReadToEndAsync()
    $deadline = (Get-Date).AddSeconds(60)
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-Discord
    Write-Ok 'Discord first-run init done'
}

function Resolve-DiscordSetupPath {
    $raw = @(Get-DiscordSetup)
    $setup = [string]($raw | Where-Object { $_ -is [string] -and $_ -like '*.exe' } | Select-Object -Last 1)
    if ([string]::IsNullOrWhiteSpace($setup)) {
        $setup = [string]($raw | Select-Object -Last 1)
    }
    if ([string]::IsNullOrWhiteSpace($setup) -or -not (Test-Path -LiteralPath $setup)) {
        $dump = ($raw | ForEach-Object { "$_ ($($_.GetType().FullName))" }) -join '; '
        throw "DiscordSetup path invalid. Get-DiscordSetup returned: $dump"
    }
    Write-LogLine 'DIAG' "Resolved DiscordSetup FilePath=$setup"
    return $setup
}

function Invoke-DiscordSetupSilent {
    # Prefer winget (no multi-MB installer stored in the kit). Fall back to CDN setup.
    if (-not (Test-DiscordReady)) {
        if (Install-DiscordViaWinget) {
            $app = Get-ActiveApp
            if ($app) {
                Invoke-SquirrelFirstRun $app.FullName
                $Script:DiscordInstalledThisRun = $true
                return
            }
        }
    }

    $setup = Resolve-DiscordSetupPath
    Write-Step 'Installing Discord (stock, silent)...'
    Write-LogLine 'DIAG' "Start-Process DiscordSetup FilePath=$setup Args=-s"
    $proc = Start-Process -FilePath $setup -ArgumentList '-s' -PassThru -WindowStyle Hidden
    if (-not $proc.WaitForExit(300000)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw 'Discord installer timed out after 5 minutes'
    }
    if ($null -ne $proc.ExitCode -and $proc.ExitCode -ne 0) {
        Write-Warn "DiscordSetup exited with code $($proc.ExitCode)"
    }
    if (-not (Wait-DiscordReady 120)) {
        throw 'Discord install timed out - check internet, install Discord from winget, or retry.'
    }
    $app = Get-ActiveApp
    Invoke-SquirrelFirstRun $app.FullName
    $Script:DiscordInstalledThisRun = $true
}

function Backup-DiscordInstallerDb {
    $src = Join-Path $DiscordRoot 'installer.db'
    if (-not (Test-Path $src)) { return }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item $src (Join-Path $DiscordRoot "installer.db.bak-$stamp") -Force -ErrorAction SilentlyContinue
}

function Test-DiscordInstallerHealthy {
    $db = Join-Path $DiscordRoot 'installer.db'
    if (-not (Test-Path $db)) { return $false }
    if ((Get-Item $db).Length -lt 4096) { return $false }
    $log = Join-Path $AppData 'logs\Discord_updater_rCURRENT.log'
    if (Test-Path $log) {
        $last = Select-String -Path $log -Pattern 'hosts_req_modules_installed: true' -ErrorAction SilentlyContinue |
            Select-Object -Last 1
        if ($last) { return $true }
    }
    $app = Get-ActiveApp
    return $null -ne $app -and (Test-DiscordModulesReady $app.FullName)
}

function Repair-DiscordInstallerState {
    Stop-Discord
    Start-Sleep -Milliseconds 500
    Backup-DiscordInstallerDb
    $log = Join-Path $DiscordRoot 'SquirrelSetup.log'
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
}

function Wait-DiscordMainWindow {
    param([int]$TimeoutSec = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $win = Get-Process Discord -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowTitle -like '*Discord*' } |
            Select-Object -First 1
        if ($win) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-DiscordWindowState {
    $win = Get-Process Discord -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle } |
        Sort-Object WorkingSet64 -Descending |
        Select-Object -First 1
    if (-not $win) { return 'none' }

    $title = $win.MainWindowTitle
    # Fully loaded client: "Friends - Discord", "#channel - Server - Discord", etc.
    if ($title -match '^(Friends|Inbox|Library|Nitro|Shop|Discover|Activity) - Discord$') { return 'logged_in' }
    if ($title -match ' - Discord$' -and $title -notmatch 'discord(app)?\.com' -and $title -ne 'Discord') {
        return 'logged_in'
    }
    # Early boot / webview bridge titles
    if ($title -match 'discord(app)?\.com') { return 'loading' }
    if ($title -eq 'Discord' -or $title -eq 'discord' -or $title -match 'Updater') { return 'login_or_loading' }
    return 'unknown'
}

function Test-DiscordSessionStorage {
    param([string]$DbDir, [int]$MinBytes = 2048)
    if (-not (Test-Path $DbDir)) { return $false }
    $files = Get-ChildItem $DbDir -File -ErrorAction SilentlyContinue |
        Where-Object { ($_.Extension -in @('.ldb', '.log')) -and $_.Name -ne 'LOCK' }
    if (-not $files) { return $false }
    return (($files | Measure-Object -Property Length -Sum).Sum -ge $MinBytes)
}

function Test-DiscordLoggedIn {
    $indexedDb = Join-Path $AppData 'IndexedDB\https_discord.com_0.indexeddb.leveldb'
    $localDb = Join-Path $AppData 'Local Storage\leveldb'
    if ((Test-DiscordSessionStorage $indexedDb) -and (Test-DiscordSessionStorage $localDb)) {
        return $true
    }

    if ((Get-DiscordWindowState) -eq 'logged_in') {
        $rendererLog = Join-Path $AppData 'logs\renderer_js.log'
        if (Test-Path $rendererLog) {
            $tail = @(Get-Content $rendererLog -Tail 100 -ErrorAction SilentlyContinue) -join "`n"
            if ($tail -match 'Dispatching CONNECTION_OPEN|Dispatching LOGIN_SUCCESS|\[GatewaySocket\] \[READY\]') {
                return $true
            }
        }
    }
    return $false
}

function Ensure-DiscordLoggedIn([string]$AppDir) {
    if (Test-DiscordLoggedIn) {
        Write-Ok 'Discord session found - already logged in (session will not be touched)'
        return
    }

    if ($env:DISCOPT_NONINTERACTIVE -eq '1') {
        throw 'Discord is not logged in. Open Discord, log in once, then rerun Exo.'
    }

    Write-Host ''
    Write-Host '  >>> Log in to Discord in the window that opens.' -ForegroundColor Yellow
    Write-Host '  >>> The optimizer waits until you are logged in, then applies mods.' -ForegroundColor Yellow
    Write-Host '  >>> Your login is saved before any optimization runs.' -ForegroundColor Yellow
    Write-Host ''

    if (-not (Get-Process Discord -ErrorAction SilentlyContinue)) {
        [void](Invoke-DiscordLaunch -AppDir $AppDir)
    }

    $deadline = (Get-Date).AddMinutes(15)
    $lastHint = [DateTime]::MinValue
    while ((Get-Date) -lt $deadline) {
        if (Test-DiscordLoggedIn) { break }

        $state = Get-DiscordWindowState
        if ($state -eq 'logged_in') {
            Start-Sleep -Seconds 5
            if (Test-DiscordLoggedIn) { break }
        }

        if (((Get-Date) - $lastHint).TotalSeconds -ge 30) {
            Write-Host "  ... waiting for login (window: $state)" -ForegroundColor DarkGray
            $lastHint = Get-Date
        }
        Start-Sleep -Seconds 2
    }

    if (-not (Test-DiscordLoggedIn)) {
        Stop-Discord
        throw 'Login not detected within 15 minutes. Log in to Discord, then rerun Disc-Optimizer.ps1'
    }

    Write-Ok 'Login verified - session saved; applying optimizations now'
    Start-Sleep -Seconds 4
    Stop-Discord
    Start-Sleep -Seconds 2
}

function Ensure-DiscordBootReady([string]$AppDir) {
    if (Test-DiscordInstallerHealthy) {
        Write-Ok 'Discord installer state healthy'
        return
    }

    $ffmpegReal = Join-Path $AppDir 'ffmpeg_real.dll'
    if (Test-Path $ffmpegReal) {
        Copy-Item $ffmpegReal (Join-Path $AppDir 'ffmpeg.dll') -Force
    }

    Write-Step 'Repairing Discord boot (installer DB + module handshake)...'
    Repair-DiscordInstallerState
    Invoke-SquirrelFirstRun $AppDir
    $updateExe = Join-Path $DiscordRoot 'Update.exe'

    if (-not (Test-DiscordModulesReady $AppDir)) {
        Write-Step 'Waiting for Discord updater to install required modules...'
        [void](Invoke-DiscordLaunch -AppDir $AppDir)
        $deadline = (Get-Date).AddMinutes(6)
        while ((Get-Date) -lt $deadline) {
            if (Test-DiscordModulesReady $AppDir) { break }
            Start-Sleep -Seconds 2
        }
        Stop-Discord
    }

    if (-not (Test-DiscordModulesReady $AppDir)) {
        throw 'Discord modules not ready - quit Discord from tray, rerun Disc-Optimizer.ps1'
    }

    [void](Invoke-DiscordLaunch -AppDir $AppDir)
    if (-not (Wait-DiscordMainWindow 90)) {
        Stop-Discord
        throw 'Discord did not reach the main window - boot repair failed'
    }
    Stop-Discord
    if (-not $SkipKernel) { Install-DiscOptKernel $AppDir }
    Write-Ok 'Discord boot verified (main window reached)'
}

function Install-DiscordModulesFromManifest([string]$AppDir) {
    $helper = Join-Path $ToolsDir 'Install-DiscordModules.ps1'
    if (-not (Test-Path $helper)) { return $false }

    $version = (Split-Path $AppDir -Leaf) -replace '^app-', ''
    Write-Step 'Downloading Discord modules from CDN (fast)...'

    $helperPwsh = Get-DiscOptPowerShellExe
    if (-not $helperPwsh -or -not (Test-Path -LiteralPath $helperPwsh)) {
        Write-Warn 'PowerShell 7 (pwsh) required for fast module download - using stock first-run'
        return $false
    }

    $global:LASTEXITCODE = 0
    & $helperPwsh -NoProfile -File $helper -AppDir $AppDir -Version $version
    $helperExit = $LASTEXITCODE
    if ($null -ne $helperExit -and $helperExit -ne 0) {
        Write-Warn 'CDN module install failed - falling back to stock first-run'
        return $false
    }

    if (Test-DiscordModulesReady $AppDir) {
        Write-Ok 'Discord modules installed from CDN'
        Export-DiscordModulesBundle $AppDir
        return $true
    }

    Write-Warn 'CDN modules incomplete - falling back to stock first-run'
    return $false
}

function Initialize-DiscordModules([string]$AppDir) {
    if (Test-DiscordModulesReady $AppDir) {
        Write-Ok 'Discord modules already installed'
        return
    }

    if (Restore-DiscordModulesBundle $AppDir) {
        return
    }

    if (Test-DiscordInstallerHealthy) {
        if (Install-DiscordModulesFromManifest $AppDir) {
            return
        }
    } else {
        Write-Warn 'Installer DB not healthy - skipping CDN module drop (use updater first)'
    }

    Repair-DiscordInstallerState

    Write-Step 'Installing Discord modules (stock first-run - needs internet)...'
    Use-StockDiscordRuntime $AppDir

    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    $packagesDir = Join-Path $DiscordRoot 'packages'
    if (Test-Path $packagesDir) {
        $nupkg = Get-ChildItem $packagesDir -Filter '*.nupkg' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($nupkg) {
            Write-LogLine 'STEP' "Applying pending package: $($nupkg.Name)"
            Start-Process -FilePath $updateExe -ArgumentList (Join-DiscOptProcessArguments @('-update', $nupkg.FullName)) -Wait -ErrorAction SilentlyContinue | Out-Null
            if (Test-DiscordModulesReady $AppDir) {
                Write-Ok 'Discord modules installed via update package'
                Stop-Discord
                Export-DiscordModulesBundle $AppDir
                return
            }
        }
    }

    for ($attempt = 1; $attempt -le 2; $attempt++) {
        if ($attempt -gt 1) {
            Write-Warn 'Retrying module install (one more stock launch)...'
        }
        [void](Invoke-DiscordLaunch -AppDir $AppDir)

        $deadline = (Get-Date).AddMinutes(6)
        $lastMsg = Get-Date
        while ((Get-Date) -lt $deadline) {
        if (Test-DiscordModulesReady $AppDir) {
            Write-Ok 'Discord modules installed'
            Stop-Discord
            Export-DiscordModulesBundle $AppDir
            return
        }
            if (((Get-Date) - $lastMsg).TotalSeconds -ge 10) {
                $missing = @($RequiredModules | Where-Object { -not (Test-Path (Join-Path $AppDir "modules\$_")) })
                Write-LogLine 'STEP' "Waiting for modules: $($missing -join ', ')"
                $lastMsg = Get-Date
            }
            Start-Sleep -Seconds 1
        }
        Stop-Discord
        Start-Sleep -Seconds 1
    }

    throw 'Discord module install timed out - open stock Discord once manually, then run -Quick'
}

function Remove-DiscordInstall {
    Write-Step 'Removing existing Discord install...'
    Stop-Discord

    if (Test-Path $DiscordRoot) {
        Remove-Item $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if (Test-Path $DiscordRoot) {
            Get-ChildItem $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue |
                Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
            Remove-Item $DiscordRoot -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($shortcut in @(
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord Inc'),
        (Get-DiscOptEnvPath 'APPDATA' 'Microsoft\Windows\Start Menu\Programs\Discord.lnk')
    )) {
        if ($shortcut -and (Test-Path $shortcut)) {
            Remove-Item $shortcut -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $DiscordRoot) {
        throw 'Could not remove Discord folder - close Discord completely and run again'
    }
    Write-Ok 'Old Discord install removed'
}

function Prepare-Discord {
    if ($SkipDiscordInstall) {
        Write-Ok 'Discord prep skipped (-SkipDiscordInstall / -Quick)'
        return
    }

    if ($FreshInstall) {
        Remove-DiscordInstall
        Invoke-DiscordSetupSilent
        $app = Get-ActiveApp
        Invoke-SquirrelFirstRun $app.FullName
        if (-not (Test-DiscordModulesReady $app.FullName)) {
            Initialize-DiscordModules $app.FullName
        }
        Stop-Discord
        Sync-DiscordModulesBundle $app.FullName
        Write-Ok "Discord $($app.Name) x64 fresh install ready"
        return
    }

    # Half-broken Discord: exe present, resources/app.asar gone
    $active = Get-ActiveApp
    if ($active) {
        $resources = Join-Path $active.FullName 'resources'
        $appAsar = Join-Path $resources 'app.asar'
        if (-not (Test-Path $resources) -or -not (Test-Path $appAsar)) {
            Write-Warn "Discord resources missing under $($active.FullName) - wiping and reinstalling..."
            Write-HubProgress 18 'Reinstalling Discord (resources missing)...'
            Remove-DiscordInstall
            Invoke-DiscordSetupSilent
            $app = Get-ActiveApp
            Invoke-SquirrelFirstRun $app.FullName
            if (-not (Test-DiscordModulesReady $app.FullName)) {
                Initialize-DiscordModules $app.FullName
            }
            Stop-Discord
            Sync-DiscordModulesBundle $app.FullName
            Write-Ok "Discord $($app.Name) x64 reinstalled (resources restored)"
            return
        }
    }

    if (Test-DiscordReady) {
        $app = Get-ActiveApp
        if (Test-DiscordModulesReady $app.FullName) {
            Sync-DiscordModulesBundle $app.FullName
            Write-Ok "Discord $($app.Name) ready (modules OK - skipping host update)"
            return
        }
        Write-Warn 'Modules missing - repairing without wiping install...'
        Repair-DiscordInstallerState
        Invoke-SquirrelFirstRun $app.FullName
        Initialize-DiscordModules $app.FullName
        Stop-Discord
        Sync-DiscordModulesBundle $app.FullName
        return
    }

    Invoke-DiscordSetupSilent
    $app = Get-ActiveApp
    if (-not (Test-DiscordModulesReady $app.FullName)) {
        Initialize-DiscordModules $app.FullName
    }
    Stop-Discord
    Sync-DiscordModulesBundle $app.FullName
    Write-Ok "Discord $($app.Name) x64 installed"
}

function Sync-DiscordModulesBundle([string]$AppDir) {
    if (Test-DiscordModulesReady $AppDir) {
        $bundleVersion = Get-ModulesBundleVersion
        $appVersion = (Split-Path $AppDir -Leaf) -replace '^app-', ''
        if (-not (Test-ModulesBundleReady) -or $bundleVersion -ne $appVersion) {
            Export-DiscordModulesBundle $AppDir
        }
    }
}

function Assert-DiscordInstall {
    if (-not (Test-Path $DiscordRoot)) {
        throw 'Discord not installed. Run without -SkipDiscordInstall to download fresh x64 Discord.'
    }
    if (-not (Test-Path (Join-Path $DiscordRoot 'Update.exe'))) {
        throw 'Update.exe missing. Run without -SkipDiscordInstall to reinstall Discord.'
    }
    $app = Get-ActiveApp
    if (-not $app) { throw 'No app-* folder found. Finish the Discord installer first.' }
    $resources = Join-Path $app.FullName 'resources'
    $appAsar = Join-Path $resources 'app.asar'
    if (-not (Test-Path $resources) -or -not (Test-Path $appAsar)) {
        throw "Discord resources/app.asar missing under $($app.FullName). Re-run without -SkipDiscordInstall."
    }
    return $app
}

function Test-KitIntegrity {
    Write-Step 'Checking kit (portable / new-PC ready)...'
    $required = @(
        (Join-Path $KitDir 'version.dll'),
        (Join-Path $KitDir 'ffmpeg.dll'),
        (Join-Path $KitDir 'config.ini'),
        (Join-Path $Profiles 'equicord-overrides.json'),
        (Join-Path $Profiles 'equicordplugins.json'),
        (Join-Path $Profiles 'vencordplugins.json'),
        (Join-Path $Profiles 'discord.json')
    )
    foreach ($file in $required) {
        if (-not (Test-Path $file)) { throw "Kit incomplete - missing $file" }
    }
    if ((Get-Item (Join-Path $KitDir 'ffmpeg.dll')).Length -lt 10000) {
        throw 'Bundled ffmpeg.dll proxy looks invalid'
    }
    if ((Get-Item (Join-Path $KitDir 'version.dll')).Length -lt 50000) {
        throw 'Bundled version.dll looks invalid'
    }
    Write-Ok 'Kit OK (kernel: ffmpeg proxy + version.dll + config.ini)'
    if (Test-Path (Join-Path $Themes $EnabledTheme)) {
        Write-Ok "Theme: $EnabledTheme"
    } else {
        Write-Warn "Missing theme: $EnabledTheme"
    }
    $hasDiscordSetup = $null -ne (Get-BundledDiscordSetup)
    $hasModulesBundle = Test-ModulesBundleReady
    $hasEquicord = $null -ne (Get-BundledEquicordAsar)
    if ($hasDiscordSetup -and $hasEquicord -and $hasModulesBundle) {
        Write-Ok "tools/ fully ready: Discord + Equicord + modules ($(Get-ModulesBundleVersion))"
    } elseif ($hasDiscordSetup -and $hasEquicord) {
        Write-Ok 'tools/ has Discord + Equicord (modules bundle will cache on first run)'
    } elseif ($hasDiscordSetup) {
        Write-Ok 'tools/ has Discord installer'
    } else {
        Write-Ok 'Tip: put DiscordSetup.exe + desktop.asar in tools/ for fast setup'
    }
}

