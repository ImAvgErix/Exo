# 40-DebloatWindows.ps1 - Debloat, cache, Windows tweaks, OpenASAR, profile flags
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Get-DiscordOptStatePath {
    $dir = Get-DiscOptEnvPath 'LOCALAPPDATA' 'OptiHub'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Join-Path $dir 'discord-optimizer.json')
}

function Read-DiscordOptState {
    $path = Get-DiscordOptStatePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Save-DiscordOptState([hashtable]$State) {
    $path = Get-DiscordOptStatePath
    $temp = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $State | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $path -Force
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Test-StableDiscordText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    try {
        $rootPrefix = [IO.Path]::GetFullPath($DiscordRoot).TrimEnd('\') + '\'
        $expanded = [Environment]::ExpandEnvironmentVariables($Text).Replace('/', '\')
        return $expanded.IndexOf($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
    } catch { return $false }
}

function Get-RegistryValueSnapshot([string]$Key, [string]$Name) {
    $item = Get-Item -Path $Key -ErrorAction Stop
    if ($item.GetValueNames() -notcontains $Name) { return $null }
    return @{
        Key   = $Key
        Name  = $Name
        Value = $item.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        Kind  = $item.GetValueKind($Name).ToString()
    }
}

function Get-StableDiscordRunSnapshot {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $entries = [Collections.Generic.List[hashtable]]::new()
    if (-not (Test-Path $runKey)) { return @($entries) }
    $item = Get-Item -Path $runKey -ErrorAction Stop
    foreach ($name in @($item.GetValueNames())) {
        $value = $item.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if (Test-StableDiscordText ([string]$value)) {
            $entries.Add(@{ Key = $runKey; Name = $name; Value = $value; Kind = $item.GetValueKind($name).ToString() })
        }
    }
    return @($entries)
}

function Get-StableDiscordTasks {
    $tasks = [Collections.Generic.List[object]]::new()
    foreach ($task in @(Get-ScheduledTask -ErrorAction Stop)) {
        $stable = $false
        foreach ($action in @($task.Actions)) {
            if ((Test-StableDiscordText ([string]$action.Execute)) -or
                (Test-StableDiscordText ([string]$action.Arguments)) -or
                (Test-StableDiscordText ([string]$action.WorkingDirectory))) {
                $stable = $true
                break
            }
        }
        if ($stable) { $tasks.Add($task) }
    }
    return @($tasks)
}

function Get-StableDiscordTrayEntries {
    $entries = [Collections.Generic.List[hashtable]]::new()
    $trayRoot = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $trayRoot)) { return @($entries) }
    foreach ($key in @(Get-ChildItem -Path $trayRoot -ErrorAction Stop)) {
        $item = Get-Item -Path $key.PSPath -ErrorAction Stop
        $exe = [string]$item.GetValue('ExecutablePath')
        if (-not (Test-StableDiscordText $exe)) { continue }
        $hasPromoted = $item.GetValueNames() -contains 'IsPromoted'
        $entries.Add(@{
            Key              = $key.PSPath
            Name             = $key.PSChildName
            ExecutablePath   = $exe
            IsPromotedExisted = $hasPromoted
            IsPromotedValue  = if ($hasPromoted) { $item.GetValue('IsPromoted') } else { $null }
            IsPromotedKind   = if ($hasPromoted) { $item.GetValueKind('IsPromoted').ToString() } else { 'DWord' }
        })
    }
    return @($entries)
}

function Get-DiscordWindowsSnapshot {
    $runEntries = @(Get-StableDiscordRunSnapshot)
    $startupApproved = [Collections.Generic.List[hashtable]]::new()
    $approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if (Test-Path $approvedKey) {
        foreach ($entry in $runEntries) {
            $snapshot = Get-RegistryValueSnapshot $approvedKey ([string]$entry.Name)
            if ($snapshot) { $startupApproved.Add($snapshot) }
        }
    }

    $notifications = [Collections.Generic.List[hashtable]]::new()
    $notificationRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
        $path = Join-Path $notificationRoot $id
        $keyExisted = Test-Path $path
        $enabled = if ($keyExisted) { Get-RegistryValueSnapshot $path 'Enabled' } else { $null }
        $notifications.Add(@{
            Id             = $id
            KeyExisted     = $keyExisted
            EnabledExisted = [bool]$enabled
            EnabledValue   = if ($enabled) { $enabled.Value } else { $null }
            EnabledKind    = if ($enabled) { $enabled.Kind } else { 'DWord' }
        })
    }

    $scheduledTasks = [Collections.Generic.List[hashtable]]::new()
    foreach ($task in @(Get-StableDiscordTasks)) {
        $scheduledTasks.Add(@{
            TaskName = [string]$task.TaskName
            TaskPath = [string]$task.TaskPath
            Enabled  = [bool]$task.Settings.Enabled
            Xml      = [string](Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop)
        })
    }

    return @{
        RunEntries       = $runEntries
        StartupApproved  = @($startupApproved)
        Notifications    = @($notifications)
        ScheduledTasks   = @($scheduledTasks)
        TrayEntries      = @(Get-StableDiscordTrayEntries)
        Compatibility    = @()
    }
}

function Merge-DiscordRecoveryItems($Prior, $Current, [string[]]$IdentityFields) {
    $result = [Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($set in @($Prior, $Current)) {
        foreach ($item in @($set | Where-Object { $_ })) {
            $parts = foreach ($field in $IdentityFields) { [string]$item.$field }
            $id = ($parts -join "`0").ToLowerInvariant()
            if ($seen.ContainsKey($id)) { continue }
            $seen[$id] = $true
            $result.Add($item)
        }
    }
    return @($result)
}

function Merge-DiscordWindowsRecovery($Prior, [hashtable]$Current) {
    if (-not $Prior) { return $Current }
    return @{
        RunEntries      = @(Merge-DiscordRecoveryItems $Prior.RunEntries $Current.RunEntries @('Key', 'Name'))
        StartupApproved = @(Merge-DiscordRecoveryItems $Prior.StartupApproved $Current.StartupApproved @('Key', 'Name'))
        Notifications   = @(Merge-DiscordRecoveryItems $Prior.Notifications $Current.Notifications @('Id'))
        ScheduledTasks  = @(Merge-DiscordRecoveryItems $Prior.ScheduledTasks $Current.ScheduledTasks @('TaskPath', 'TaskName'))
        TrayEntries     = @(Merge-DiscordRecoveryItems $Prior.TrayEntries $Current.TrayEntries @('Key'))
        Compatibility   = @(Merge-DiscordRecoveryItems $Prior.Compatibility $Current.Compatibility @('Key', 'Name'))
    }
}

function Initialize-DiscordApplyState {
    $prior = Read-DiscordOptState
    $priorRecovery = if ($prior -and ($prior.PSObject.Properties.Name -contains 'recovery')) { $prior.recovery } else { $null }
    $current = Get-DiscordWindowsSnapshot
    $recovery = Merge-DiscordWindowsRecovery $priorRecovery $current
    Save-DiscordOptState @{
        version         = $Script:DiscOptVersion
        applyStatus     = 'applying'
        applied         = $false
        applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        recovery        = $recovery
    }
    $Script:DiscordWindowsRecovery = $recovery
    return $recovery
}

function Refresh-DiscordWindowsRecovery {
    $current = Get-DiscordWindowsSnapshot
    $Script:DiscordWindowsRecovery = Merge-DiscordWindowsRecovery $Script:DiscordWindowsRecovery $current
    Save-DiscordOptState @{
        version         = $Script:DiscOptVersion
        applyStatus     = 'applying'
        applied         = $false
        applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        recovery        = $Script:DiscordWindowsRecovery
    }
}

function Complete-DiscordApplyState([string]$AppDir) {
    $state = Read-DiscordOptState
    $recovery = if ($state -and ($state.PSObject.Properties.Name -contains 'recovery')) { $state.recovery } else { $Script:DiscordWindowsRecovery }
    Save-DiscordOptState @{
        version           = $Script:DiscOptVersion
        applyStatus       = 'applied'
        applied           = $true
        fullApply         = $true
        windowsVerified   = $true
        debloatVerified   = $true
        appDir            = $AppDir
        appliedUtc        = (Get-Date).ToUniversalTime().ToString('o')
        recovery          = $recovery
    }
}

function Ensure-DiscordCompatibilityRecovery([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    $key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
    $existing = @($Script:DiscordWindowsRecovery.Compatibility)
    if ($existing | Where-Object { [string]$_.Name -ieq $exe }) { return }

    $snapshot = if (Test-Path $key) { Get-RegistryValueSnapshot $key $exe } else { $null }
    $record = @{
        Key     = $key
        Name    = $exe
        Existed = [bool]$snapshot
        Value   = if ($snapshot) { $snapshot.Value } else { $null }
        Kind    = if ($snapshot) { $snapshot.Kind } else { 'String' }
    }
    $Script:DiscordWindowsRecovery.Compatibility = @($existing) + @($record)
    Save-DiscordOptState @{
        version         = $Script:DiscOptVersion
        applyStatus     = 'applying'
        applied         = $false
        applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        recovery        = $Script:DiscordWindowsRecovery
    }
}

function Invoke-Debloat([string]$AppDir, [ref]$Freed) {
    Write-Step 'Debloating Discord...'

    # Old app-* hosts only (safe). Never wipe the active build.
    Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' |
        Where-Object { $_.FullName -ne $AppDir } |
        ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" } }

    # Strip the small allowlist of known-nonessential feature modules and game
    # SDK binaries. Unknown modules stay intact to avoid breaking future builds.
    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path -LiteralPath $modPath) {
        foreach ($name in $OptionalModules) {
            $folder = Join-Path $modPath $name
            if (Test-Path -LiteralPath $folder) {
                if (Remove-Safe $folder $freed) { Write-Ok "Removed optional module $name" }
            }
        }
        Get-ChildItem -LiteralPath $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok 'Removed game SDK' } }
    }

    # English is the optimizer's lean baseline. Removing the other locale packs
    # trades multilingual UI assets for the smallest client footprint.
    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path -LiteralPath $localePath) {
        Get-ChildItem -LiteralPath $localePath -Filter '*.pak' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'en-US.pak' } |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed locale $($_.Name)" } }
    }

    # NOTE: never remove d3dcompiler_47.dll, vulkan-1.dll, vk_swiftshader*, or
    # chrome_*_percent.pak - Chromium needs them for rendering and removing them
    # causes blank/black windows on many GPUs.
    foreach ($pattern in @(
        '.first-run', 'Discord.exe.sig', 'discord_wer.*',
        'Microsoft.Gaming.XboxApp.XboxNetwork.winmd', '*.log'
    )) {
        Get-ChildItem (Join-Path $AppDir $pattern) -ErrorAction SilentlyContinue | ForEach-Object {
            if ($Protected -contains $_.Name) { return }
            if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed $($_.Name)" }
        }
    }

    Write-Ok "Debloat saved ~$([math]::Round($freed.Value / 1MB, 1)) MB"
}

function Clear-DiscordConflictLeftovers {
    # Remove stale OptiHub / crash / GPU caches that can fight a fresh apply.
    Write-Step 'Clearing conflicting Discord leftovers (login preserved)...'
    $n = 0
    $desk = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Discord (OptiHub).lnk')) {
        $p = Join-Path $desk $name
        if (Test-Path -LiteralPath $p) {
            Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
            $n++; Write-Ok "Removed $name"
        }
    }
    $local = Join-Path $env:LOCALAPPDATA 'Discord'
    if (Test-Path $local) {
        foreach ($rel in @('GPUCache', 'Code Cache', 'ShaderCache', 'Crashpad', 'DawnCache', 'GrShaderCache')) {
            $p = Join-Path $local $rel
            if (Test-Path $p) {
                try {
                    Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
                    $n++
                    Write-Ok "Cleared LocalAppData\Discord\$rel"
                } catch { }
            }
        }
    }
    Write-Ok "Discord conflict cleanup items: $n"
    return $n
}

function Clear-DiscordSafeCache([ref]$Freed) {
    if ($SkipCacheClean) {
        Write-Ok 'Cache clean skipped (-SkipCacheClean)'
        return
    }

    Write-Step 'Deep-cleaning Discord caches (login/session preserved)...'
    $before = $Freed.Value
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (Remove-Safe $path $Freed) {
            Write-Ok "Cleaned $relative"
        }
    }

    # Squirrel state (packages\RELEASES, installer.db) is never touched -
    # Update.exe silently refuses to launch Discord without it.
    $saved = $Freed.Value - $before
    if ($saved -gt 0) {
        Write-Ok "Deep cache purge saved ~$([math]::Round($saved / 1MB, 1)) MB"
    } else {
        Write-Ok 'Deep cache purge found nothing to remove'
    }
}

function Get-DiscordManifestCached {
    if ($Script:DiscordManifest) { return $Script:DiscordManifest }
    $Script:DiscordManifest = Invoke-RestMethod -Uri 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64' -Headers @{ 'User-Agent' = 'OptiHub-Discord/1.0' } -TimeoutSec 60
    return $Script:DiscordManifest
}

function Install-DiscordModuleFromManifest([string]$AppDir, [string]$ModuleName) {
    $folder = Join-Path $AppDir "modules\$ModuleName-1"
    if (Test-Path $folder) { return $true }

    $manifest = Get-DiscordManifestCached
    $mod = $manifest.modules.$ModuleName
    if (-not $mod -or -not $mod.full.url) { throw "$ModuleName missing from Discord manifest" }

    $work = Get-DiscOptTempPath "discopt-$ModuleName"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    $distro = Join-Path $work 'pkg.distro'
    $tar = Join-Path $work 'pkg.tar'
    $extract = Join-Path $work 'extract'
    try {
        Invoke-WebRequest -Uri $mod.full.url -OutFile $distro -UseBasicParsing -TimeoutSec 120

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
        & tar -xf $tar -C $extract 2>$null
        if ($LASTEXITCODE -ne 0) { throw "tar failed while extracting $ModuleName" }

        $files = Join-Path $extract 'files'
        if (-not (Test-Path $files)) { throw "$ModuleName package had no files/" }

        $modRoot = Join-Path $AppDir 'modules'
        if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot -Force | Out-Null }
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Copy-Item -Path (Join-Path $files '*') -Destination $folder -Recurse -Force
        return $true
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-RuntimeModules([string]$AppDir) {
    foreach ($name in $RuntimeModules) {
        $folder = Join-Path $AppDir "modules\$name-1"
        if (Test-Path $folder) {
            Write-Ok "$name module present"
            continue
        }
        Write-Step "Installing $name module (required by Discord core)..."
        Install-DiscordModuleFromManifest $AppDir $name | Out-Null
        Write-Ok "$name module installed"
    }
}

function Ensure-KrispModule([string]$AppDir) {
    $krisp = Join-Path $AppDir 'modules\discord_krisp-1'
    if (Test-Path $krisp) {
        Write-Ok 'Krisp module present (noise suppression UI)'
        return
    }

    Write-Step 'Installing Krisp module (noise suppression dropdown)...'
    Install-DiscordModuleFromManifest $AppDir 'discord_krisp' | Out-Null
    if (-not (Test-Path $krisp)) { throw 'Krisp module missing after CDN install' }
    Write-Ok 'Krisp module installed'
}

function Test-DebloatNeeded([string]$AppDir) {
    $reasons = @()

    $oldApps = @(Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $AppDir })
    if ($oldApps.Count -gt 0) { $reasons += "$($oldApps.Count) old app-* folder(s)" }

    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path -LiteralPath $modPath) {
        $optionalPresent = @($OptionalModules | Where-Object { Test-Path -LiteralPath (Join-Path $modPath $_) })
        if ($optionalPresent.Count -gt 0) { $reasons += "$($optionalPresent.Count) optional module(s)" }
        if (Get-ChildItem -LiteralPath $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 1) { $reasons += 'game SDK' }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path -LiteralPath $localePath) {
        $extraLocales = @(Get-ChildItem -LiteralPath $localePath -Filter '*.pak' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'en-US.pak' })
        if ($extraLocales.Count -gt 0) { $reasons += "$($extraLocales.Count) extra locale(s)" }
    }

    return @{
        Needed  = ($reasons.Count -gt 0)
        Reasons = $reasons
    }
}

function Disable-DiscordWindowsAutostart {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $stableEntries = @(Get-StableDiscordRunSnapshot)
    foreach ($entry in $stableEntries) {
        Remove-ItemProperty -Path $runKey -Name $entry.Name -Force -ErrorAction Stop
        if ((Get-Item -Path $runKey -ErrorAction Stop).GetValueNames() -contains [string]$entry.Name) {
            throw "Stable Discord startup entry still present: $($entry.Name)"
        }
        Write-Ok "Removed stable Discord startup entry: $($entry.Name)"
    }
    $startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if (Test-Path $startupApproved) {
        $approvedItem = Get-Item -Path $startupApproved -ErrorAction Stop
        foreach ($entry in $stableEntries) {
            if ($approvedItem.GetValueNames() -contains [string]$entry.Name) {
                Remove-ItemProperty -Path $startupApproved -Name $entry.Name -Force -ErrorAction Stop
                if ((Get-Item -Path $startupApproved -ErrorAction Stop).GetValueNames() -contains [string]$entry.Name) {
                    throw "Stable Discord startup approval still present: $($entry.Name)"
                }
                Write-Ok "Removed stable Discord startup approval: $($entry.Name)"
            }
        }
    }
}

function Disable-DiscordScheduledTasks {
    foreach ($task in @(Get-StableDiscordTasks)) {
        Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop | Out-Null
        $verified = Get-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop
        if ([bool]$verified.Settings.Enabled) { throw "Scheduled task remained enabled: $($task.TaskPath)$($task.TaskName)" }
        Write-Ok "Disabled stable Discord task: $($task.TaskPath)$($task.TaskName)"
    }
}

function Set-DiscordWindowsNotificationsOff {
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }
    foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
        $path = Join-Path $base $Id
        if (-not (Test-Path $path)) { New-Item -Path $path -Force -ErrorAction Stop | Out-Null }
        New-ItemProperty -Path $path -Name 'Enabled' -Value 0 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
        if ([int](Get-ItemPropertyValue -Path $path -Name 'Enabled' -ErrorAction Stop) -ne 0) {
            throw "Notification suppression verification failed: $id"
        }
        Write-Ok "Windows toasts off: $id"
    }
}

function Set-DiscordTrayIconHidden([string]$AppDir) {
    $hidden = 0
    foreach ($entry in @(Get-StableDiscordTrayEntries)) {
        New-ItemProperty -Path $entry.Key -Name 'IsPromoted' -Value 0 -PropertyType DWord -Force -ErrorAction Stop | Out-Null
        if ([int](Get-ItemPropertyValue -Path $entry.Key -Name 'IsPromoted' -ErrorAction Stop) -ne 0) {
            throw "Tray suppression verification failed: $($entry.ExecutablePath)"
        }
        $hidden++
    }
    if ($hidden -gt 0) { Write-Ok "Tray icon hidden ($hidden entries)" }
    else { Write-Warn 'Tray icon registry entry not found yet - launch once, then re-run' }
}

function Test-DiscordWindowsSuppression {
    try {
        if (@(Get-StableDiscordRunSnapshot).Count -ne 0) { return $false }

        $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
        foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
            $path = Join-Path $base $id
            if ([int](Get-ItemPropertyValue -Path $path -Name 'Enabled' -ErrorAction Stop) -ne 0) { return $false }
        }

        foreach ($task in @(Get-StableDiscordTasks)) {
            if ([bool]$task.Settings.Enabled) { return $false }
        }
        foreach ($entry in @(Get-StableDiscordTrayEntries)) {
            if (-not $entry.IsPromotedExisted -or [int]$entry.IsPromotedValue -ne 0) { return $false }
        }
        return $true
    } catch { return $false }
}

function Apply-WindowsTweaks([string]$AppDir) {
    Write-Step 'Applying aggressive Windows tweaks (notifications, tray, startup)...'
    Refresh-DiscordWindowsRecovery
    Disable-DiscordWindowsAutostart
    Disable-DiscordScheduledTasks
    Set-DiscordWindowsNotificationsOff
    Set-DiscordTrayIconHidden $AppDir
    if (-not (Test-DiscordWindowsSuppression)) { throw 'Stable Discord Windows suppression could not be fully verified' }
    Write-Ok 'Windows background noise, toasts, tray promotion, and startup disabled'
}

function Test-OpenAsarInstalled([string]$ResourcesDir) {
    $target = Join-Path $ResourcesDir '_app.asar'
    if (-not (Test-Path $target)) { return $false }
    $size = (Get-Item $target).Length
    return ($size -gt 10000 -and $size -lt 500000)
}

function Ensure-AsarStockBackup([string]$AppDir) {
    $resources = Join-Path $AppDir 'resources'
    $stockBackup = Join-Path $resources '_app.asar.stock'
    if (Test-Path $stockBackup) { return }

    $candidates = @(
        (Join-Path $resources 'app.asar.backup'),
        (Join-Path $resources '_app.asar'),
        (Join-Path $resources 'app.asar')
    )
    foreach ($src in $candidates) {
        if ((Test-Path $src) -and (Get-Item $src).Length -gt 1000000) {
            Copy-Item $src $stockBackup -Force
            Write-Ok 'Backed up stock bootstrap -> _app.asar.stock'
            return
        }
    }
    Write-Warn 'No _app.asar.stock backup yet'
}

function Install-OpenAsar([string]$AppDir) {
    Write-Step 'Installing OpenASAR (Equicord-compatible)...'
    $resources = Join-Path $AppDir 'resources'
    $target = Join-Path $resources '_app.asar'
    $stockBackup = Join-Path $resources '_app.asar.stock'

    if ($Quick -and (Test-OpenAsarInstalled $resources)) {
        Write-Ok 'OpenASAR already active on _app.asar (-Quick)'
        return
    }

    Ensure-AsarStockBackup $AppDir

    if (-not (Test-Path $target)) {
        throw 'Missing _app.asar - install Equicord loader first'
    }

    if (-not (Test-Path $stockBackup)) {
        if ((Get-Item $target).Length -gt 1000000) {
            Copy-Item $target $stockBackup -Force
            Write-Ok 'Backed up stock Discord bootstrap -> _app.asar.stock'
        }
    }

    $temp = Get-DiscOptTempPath 'discopt-openasar-app.asar'
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    $bundled = Get-BundledOpenAsar
    if ($bundled) {
        Copy-Item $bundled $temp -Force
        Write-Ok "Using bundled OpenASAR from tools/ ($([math]::Round((Get-Item $bundled).Length / 1KB, 1)) KB)"
    } else {
        Invoke-WebRequest -Uri $OpenAsarUrl -OutFile $temp -UseBasicParsing -TimeoutSec 90
    }
    if ((Get-Item $temp).Length -lt 10000) {
        throw 'Downloaded OpenASAR app.asar looks invalid'
    }

    if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null }
    Copy-Item $temp (Join-Path $ToolsDir 'openasar.asar') -Force

    Write-DiscordResourceBytes -Path $target -Bytes ([IO.File]::ReadAllBytes($temp))
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    Write-Ok "OpenASAR nightly installed ($([math]::Round((Get-Item $target).Length / 1KB, 1)) KB on _app.asar)"
}

function Unlock-DiscordSettings([string]$DestPath = '') {
    if (-not $DestPath) { $DestPath = Join-Path $AppData 'settings.json' }
    if (Test-Path $DestPath) { attrib -R $DestPath 2>$null }
}

function Get-DiscOptPowerShellExe {
    $found = Get-DiscOptPwsh7
    if ($found) { return $found.Exe }
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pwsh) { return $pwsh.Source }
    return (Get-DiscOptEnvPath 'SystemRoot' 'System32\WindowsPowerShell\v1.0\powershell.exe')
}

function Apply-DiscordProfile([string]$DestPath) {
    Write-Step 'Applying boot/optimizer flags (preserving your in-app settings)...'
    $profilePath = Join-Path $Profiles 'discord.json'
    if (-not (Test-Path $profilePath)) { throw 'Missing profiles/discord.json' }

    $kit = ConvertTo-HashtableDeep (Get-Content $profilePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $merged = @{}
    if (Test-Path $DestPath) {
        try {
            $merged = ConvertTo-HashtableDeep (Get-Content $DestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
        } catch {}
    }

    # Strip noisy/debug keys Discord or older kits may leave behind.
    foreach ($drop in @(
        'DANGEROUS_ENABLE_DEVTOOLS_ONLY_ENABLE_IF_YOU_KNOW_WHAT_YOURE_DOING',
        'devTools',
        'OPENASAR_HARDCODED'
    )) {
        if ($merged.ContainsKey($drop)) { $merged.Remove($drop) }
    }

    # If hardware acceleration was turned off on this PC (GPU-driver black
    # screens, repair fallback, or the user's own choice), never force it back on.
    $hwAccelOff = ($merged.Keys -contains 'enableHardwareAcceleration') -and
        ($merged['enableHardwareAcceleration'] -eq $false)

    $allowed = @(
        'SKIP_HOST_UPDATE', 'OPEN_ON_STARTUP', 'MINIMIZE_TO_TRAY', 'START_MINIMIZED',
        'IS_MAXIMIZED', 'IS_MINIMIZED', 'enableHardwareAcceleration', 'debugLogging', 'offloadAdmControls',
        'asyncVideoInputDeviceInit', 'DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR',
        'DESKTOP_TTI_DNSTCP_WARMUP', 'DESKTOP_TTI_EARLY_UPDATE_CHECK',
        'DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS', 'BACKGROUND_COLOR',
        'audioSubsystem', 'useLegacyAudioDevice'
    )
    foreach ($key in $allowed) {
        if ($kit.Keys -contains $key) { $merged[$key] = $kit[$key] }
    }
    if ($hwAccelOff) {
        $merged['enableHardwareAcceleration'] = $false
        Write-LogLine 'OK' 'Hardware acceleration kept OFF (was disabled on this PC)'
    }

    # Always re-stamp chromium + OpenASAR from kit so Discord updates cannot wipe them to {}.
    if ($kit.chromiumSwitches) {
        $merged.chromiumSwitches = ConvertTo-HashtableDeep $kit.chromiumSwitches
    } else {
        $merged.chromiumSwitches = @{
            'disable-breakpad'          = 1
            'disable-crash-reporter'    = 1
            'disable-domain-reliability' = 1
            'disable-logging'           = 1
        }
    }
    if ($kit.openasar) {
        $merged.openasar = ConvertTo-HashtableDeep $kit.openasar
    } else {
        $merged.openasar = @{}
    }
    $merged.openasar.setup = $true
    $merged.openasar.cmdPreset = 'perf'
    $merged.openasar.quickstart = $false
    $merged.openasar.domOptimizer = $false
    $merged.openasar.themeSync = $false
    $merged.openasar.autoupdate = $false
    $merged.openasar.noTrack = $true
    $merged.openasar.noTyping = $true
    $merged.openasar.disableMediaKeys = $true
    if (-not $merged.openasar.css) {
        $merged.openasar.css = 'body { --background-primary: #000000; --background-secondary: #000000; }'
    }

    # Hard overrides every run (Discord sometimes rewrites these mid-session).
    $merged['DESKTOP_TTI_EARLY_UPDATE_CHECK'] = $false
    $merged['DESKTOP_TTI_DNSTCP_WARMUP'] = $false
    $merged['DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR'] = $true
    $merged['audioSubsystem'] = 'standard'
    $merged['useLegacyAudioDevice'] = $false
    $merged['asyncVideoInputDeviceInit'] = $false
    $merged['debugLogging'] = $false
    $merged['BACKGROUND_COLOR'] = '#000000'
    $merged['OPEN_ON_STARTUP'] = $false

    # Never force host-update skip until modules are healthy - SKIP_HOST_UPDATE=true
    # with a broken installer.db freezes Discord on "Starting...".
    $activeForSkip = Get-ActiveApp
    if (-not $activeForSkip -or -not (Test-DiscordModulesReady $activeForSkip.FullName)) {
        $merged['SKIP_HOST_UPDATE'] = $false
        Write-LogLine 'OK' 'SKIP_HOST_UPDATE left false until modules are healthy'
    }

    $dir = Split-Path $DestPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Unlock-DiscordSettings $DestPath

    Write-JsonFile $DestPath $merged 20
    Write-Ok 'Boot/optimizer flags applied (OpenASAR perf + chromium + standard audio)'
}

