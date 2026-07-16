# 40-DebloatWindows.ps1 - Debloat, cache, Windows tweaks, OpenASAR, profile flags
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.
function Get-DiscordOptStatePath {
    $dir = Get-DiscOptEnvPath 'LOCALAPPDATA' 'Exo'
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

function Get-DiscordVariantMap {
    # Universal Discord variant map (stable + PTB + Canary). Keep in sync with
    # Get-DiscOptVariantDefinitions (DiscordDetectCore.ps1) and
    # DiscordLogic.VariantDefinitions (C#).
    return @(
        @{ Name = 'stable'; LocalDir = 'Discord'; AppDataDir = 'discord'; Exe = 'Discord.exe'; QosPolicy = 'Exo Discord Voice' },
        @{ Name = 'ptb'; LocalDir = 'DiscordPTB'; AppDataDir = 'discordptb'; Exe = 'DiscordPTB.exe'; QosPolicy = 'Exo Discord PTB Voice' },
        @{ Name = 'canary'; LocalDir = 'DiscordCanary'; AppDataDir = 'discordcanary'; Exe = 'DiscordCanary.exe'; QosPolicy = 'Exo Discord Canary Voice' }
    )
}

function Get-InstalledDiscordVariants {
    $installed = [Collections.Generic.List[hashtable]]::new()
    foreach ($variant in @(Get-DiscordVariantMap)) {
        $root = Get-DiscOptEnvPath 'LOCALAPPDATA' ([string]$variant.LocalDir)
        if ($root -and (Test-Path -LiteralPath $root)) {
            $app = @(Get-ChildItem -LiteralPath $root -Directory -Filter 'app-*' -ErrorAction SilentlyContinue)
            if ($app.Count -gt 0) { $installed.Add($variant) }
        }
    }
    return @($installed)
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
        QosPolicies      = @(Get-ExoDiscordQosPolicySnapshot)
    }
}

function Get-ExoDiscordQosPolicyRoot {
    return 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS'
}

function Get-ExoDiscordQosPolicySnapshot {
    # Records Exo-created Discord voice QoS policy names so repair knows exactly
    # what to remove. Exo policies are always safe to delete on repair - they
    # never exist unless Exo created them (documented fixed names).
    $names = [Collections.Generic.List[hashtable]]::new()
    $root = Get-ExoDiscordQosPolicyRoot
    foreach ($variant in @(Get-DiscordVariantMap)) {
        $path = Join-Path $root ([string]$variant.QosPolicy)
        if (Test-Path -LiteralPath $path) {
            $names.Add(@{ Name = [string]$variant.QosPolicy; Existed = $true })
        }
    }
    return @($names)
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
    $priorQos = if ($Prior.PSObject -and ($Prior.PSObject.Properties.Name -contains 'QosPolicies')) { $Prior.QosPolicies }
        elseif ($Prior -is [hashtable] -and $Prior.ContainsKey('QosPolicies')) { $Prior.QosPolicies }
        else { @() }
    return @{
        RunEntries      = @(Merge-DiscordRecoveryItems $Prior.RunEntries $Current.RunEntries @('Key', 'Name'))
        StartupApproved = @(Merge-DiscordRecoveryItems $Prior.StartupApproved $Current.StartupApproved @('Key', 'Name'))
        Notifications   = @(Merge-DiscordRecoveryItems $Prior.Notifications $Current.Notifications @('Id'))
        ScheduledTasks  = @(Merge-DiscordRecoveryItems $Prior.ScheduledTasks $Current.ScheduledTasks @('TaskPath', 'TaskName'))
        TrayEntries     = @(Merge-DiscordRecoveryItems $Prior.TrayEntries $Current.TrayEntries @('Key'))
        Compatibility   = @(Merge-DiscordRecoveryItems $Prior.Compatibility $Current.Compatibility @('Key', 'Name'))
        QosPolicies     = @(Merge-DiscordRecoveryItems $priorQos $Current.QosPolicies @('Name'))
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
    # Honesty: never stamp applied=true when Equicord loader or DiscOpt kernel is missing.
    # Launch-safe stock rollbacks must stay incomplete so the UI does not lie about plugins/kernel.
    $loaderOk = $false
    $kernelOk = $false
    try { $loaderOk = [bool](Test-EquicordLoaderPatched $AppDir) } catch { }
    try {
        $ver = Join-Path $AppDir 'version.dll'
        $ff = Join-Path $AppDir 'ffmpeg.dll'
        $real = Join-Path $AppDir 'ffmpeg_real.dll'
        $ini = Join-Path $AppDir 'config.ini'
        $kernelOk = (Test-Path -LiteralPath $ver) -and (Test-Path -LiteralPath $ini) -and
            (Test-Path -LiteralPath $real) -and (Test-Path -LiteralPath $ff) -and
            ((Get-Item -LiteralPath $ff).Length -lt 500000) -and ((Get-Item -LiteralPath $ver).Length -ge 50000)
    } catch { }
    $fullOk = [bool]($loaderOk -and $kernelOk)
    if (-not $fullOk) {
        Write-Warn "Apply record incomplete (equicordLoader=$loaderOk kernel=$kernelOk) - not marking applied"
    }
    Save-DiscordOptState @{
        version           = $Script:DiscOptVersion
        applyStatus       = $(if ($fullOk) { 'applied' } else { 'incomplete' })
        applied           = [bool]$fullOk
        fullApply         = [bool]$fullOk
        windowsVerified   = $true
        debloatVerified   = $true
        equicordLoaderOk  = [bool]$loaderOk
        kernelOk          = [bool]$kernelOk
        appDir            = $AppDir
        appliedUtc        = (Get-Date).ToUniversalTime().ToString('o')
        recovery          = $recovery
        applyReport       = @(Get-ExoReportEntries)
        variants          = @($Script:DiscordVariantResults)
        qosPolicies       = @($Script:DiscordQosResults)
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

    # Only strip known-optional modules. Deleting unknown modules broke Discord
    # 1.0.92xx+ (stuck on Starting / hosts_req_modules_installed=false).
    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        foreach ($name in $OptionalModules) {
            $folder = Join-Path $modPath $name
            if (Test-Path -LiteralPath $folder) {
                if (Remove-Safe $folder $freed) { Write-Ok "Removed optional module $name" }
            }
        }
        Get-ChildItem $modPath -Recurse -Filter 'discord_game_sdk_*.dll' -ErrorAction SilentlyContinue |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok 'Removed game SDK' } }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        Get-ChildItem "$localePath\*.pak" | Where-Object { $_.Name -ne 'en-US.pak' } |
            ForEach-Object { if (Remove-Safe $_.FullName $freed) { Write-Ok "Removed locale $($_.Name)" } }
    }

    # Deeper debloat: unused Chromium spellcheck dictionaries (keep en-US +
    # current system locale). Full repair reinstall restores everything.
    [void](Remove-DiscordExtraSpellcheckDictionaries $freed)

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
    # Remove stale Exo / crash / GPU caches that can fight a fresh apply.
    Write-Step 'Clearing conflicting Discord leftovers (login preserved)...'
    $n = 0
    $desk = [Environment]::GetFolderPath('Desktop')
    foreach ($name in @('Discord (Exo).lnk')) {
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

    Write-Step 'Cleaning safe Discord caches (login/session preserved)...'
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
        Write-Ok "Safe cache clean saved ~$([math]::Round($saved / 1MB, 1)) MB"
    } else {
        Write-Ok 'Safe cache clean found nothing to remove'
    }
}

function Test-CacheCleanNeeded {
    if ($SkipCacheClean) { return $false }
    foreach ($relative in $SafeCacheTargets) {
        $path = Join-Path $AppData $relative
        if (-not (Test-Path $path)) { continue }
        # Sample first files only          enough to decide if a clean is worth it
        $sample = @(Get-ChildItem $path -Recurse -Force -File -ErrorAction SilentlyContinue | Select-Object -First 50)
        if ($sample.Count -eq 0) { continue }
        $sum = ($sample | Measure-Object -Property Length -Sum).Sum
        if ($sum -gt 1MB -or $sample.Count -ge 50) { return $true }
    }
    return $false
}

function Get-DiscordManifestCached {
    if ($Script:DiscordManifest) { return $Script:DiscordManifest }
    $Script:DiscordManifest = Invoke-RestMethod -Uri 'https://updates.discord.com/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64' -Headers @{ 'User-Agent' = 'Exo-Discord/1.0' }
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
        Invoke-WebRequest -Uri $mod.full.url -OutFile $distro -UseBasicParsing

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

function Add-DiscordModuleSkipReport([string]$Step, [string]$Reason) {
    if (Get-Command Add-ExoReport -ErrorAction SilentlyContinue) {
        Add-ExoReport $Step 'skip' $Reason
    }
}

function Ensure-RuntimeModules([string]$AppDir) {
    $skipped = [Collections.Generic.List[string]]::new()
    foreach ($name in $RuntimeModules) {
        $folder = Join-Path $AppDir "modules\$name-1"
        if (Test-Path $folder) {
            Write-Ok "$name module present"
            continue
        }
        Write-Step "Installing $name module (optional runtime module)..."
        try {
            Install-DiscordModuleFromManifest $AppDir $name | Out-Null
            if (-not (Test-Path $folder)) { throw "$name module missing after CDN install" }
            Write-Ok "$name module installed"
        } catch {
            $folderName = "$name-1"
            $isBootCritical = (@($RequiredModules) -contains $folderName) -or (@($RequiredModules) -contains $name)
            if ($isBootCritical) { throw }

            $msg = "$name module skipped: $($_.Exception.Message)"
            Write-Warn $msg
            $skipped.Add($msg)
            continue
        }
    }
    if ($skipped.Count -gt 0) {
        Add-DiscordModuleSkipReport 'runtime-modules' (($skipped | ForEach-Object { [string]$_ }) -join '; ')
    }
}

function Ensure-KrispModule([string]$AppDir) {
    $krisp = Join-Path $AppDir 'modules\discord_krisp-1'
    if (Test-Path $krisp) {
        Write-Ok 'Krisp module present (noise suppression UI)'
        return
    }

    Write-Step 'Installing Krisp module (noise suppression dropdown)...'
    try {
        Install-DiscordModuleFromManifest $AppDir 'discord_krisp' | Out-Null
        if (-not (Test-Path $krisp)) { throw 'Krisp module missing after CDN install' }
        Write-Ok 'Krisp module installed'
    } catch {
        $msg = "Krisp module skipped: $($_.Exception.Message)"
        Write-Warn $msg
        Add-DiscordModuleSkipReport 'krisp' $msg
    }
}

function Test-OptionalModuleDirHasPayload([string]$ModuleDir) {
    # Align with DiscordDetectCore: empty dirs recreated by Discord != not debloated.
    if ([string]::IsNullOrWhiteSpace($ModuleDir)) { return $false }
    if (-not (Test-Path -LiteralPath $ModuleDir)) { return $false }
    try {
        return (@(Get-ChildItem -LiteralPath $ModuleDir -File -Recurse -ErrorAction SilentlyContinue).Count -gt 0)
    } catch {
        return $true
    }
}

function Test-DebloatNeeded([string]$AppDir) {
    $reasons = @()
    $hardReasons = @()
    $softReasons = @()

    $oldApps = @(Get-ChildItem $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $AppDir })
    if ($oldApps.Count -gt 0) {
        $r = "$($oldApps.Count) old app-* folder(s)"
        $reasons += $r
        $hardReasons += $r
    }

    $modPath = Join-Path $AppDir 'modules'
    if (Test-Path $modPath) {
        # Only count optional modules that still have payload files (empty shells are fine).
        $optionalPresent = @($OptionalModules | Where-Object {
            Test-OptionalModuleDirHasPayload (Join-Path $modPath $_)
        })
        if ($optionalPresent.Count -gt 0) {
            $r = "$($optionalPresent.Count) optional module(s)"
            $reasons += $r
            # Optional leftovers are soft: Discord may re-download hook while elevated Apply runs.
            $softReasons += $r
        }
    }

    $localePath = Join-Path $AppDir 'locales'
    if (Test-Path $localePath) {
        $extraLocales = @(Get-ChildItem "$localePath\*.pak" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'en-US.pak' })
        if ($extraLocales.Count -gt 0) {
            $r = "$($extraLocales.Count) extra locale(s)"
            $reasons += $r
            $softReasons += $r
        }
    }

    return @{
        Needed      = ($reasons.Count -gt 0)
        HardNeeded  = ($hardReasons.Count -gt 0)
        SoftNeeded  = ($softReasons.Count -gt 0)
        Reasons     = $reasons
        HardReasons = $hardReasons
        SoftReasons = $softReasons
    }
}

function Disable-DiscordWindowsAutostart {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { return }
    $props = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    if (-not $props) { return }
    foreach ($prop in $props.PSObject.Properties) {
        if ($prop.Name -match '^PS') { continue }
        if ($prop.Value -match 'Discord') {
            Remove-ItemProperty -Path $runKey -Name $prop.Name -Force -ErrorAction SilentlyContinue
            Write-Ok "Removed startup entry: $($prop.Name)"
        }
    }

    $startupApproved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if (Test-Path $startupApproved) {
        $approved = Get-ItemProperty $startupApproved -ErrorAction SilentlyContinue
        if ($approved) {
            foreach ($prop in $approved.PSObject.Properties) {
                if ($prop.Name -match '^PS') { continue }
                if ($prop.Name -match 'Discord') {
                    Remove-ItemProperty -Path $startupApproved -Name $prop.Name -Force -ErrorAction SilentlyContinue
                    Write-Ok "Removed startup approval: $($prop.Name)"
                }
            }
        }
    }
}

function Disable-DiscordScheduledTasks {
    try {
        # Path/exe scoped via Get-StableDiscordTasks - never match bare "Squirrel" or name-only "Discord".
        $tasks = @(Get-StableDiscordTasks)
        foreach ($task in $tasks) {
            Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue | Out-Null
            Write-Ok "Disabled scheduled task: $($task.TaskPath)$($task.TaskName)"
        }
    } catch {
        Write-LogLine 'WARN' "Scheduled task cleanup skipped: $($_.Exception.Message)"
    }
}

function Set-DiscordWindowsNotificationsOff {
    # Quiet Windows: disable Discord toast banners (in-app Discord alerts still work).
    Write-Host '[*] Disabling Windows notifications for Discord...'
    $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }

    $setOff = {
        param([string]$Id)
        $path = Join-Path $base $Id
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        Set-ItemProperty -Path $path -Name 'Enabled' -Value 0 -Type DWord -Force
        Set-ItemProperty -Path $path -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
    }

    # Known stable package IDs only - no broad name match across other apps.
    foreach ($id in @(
        'Discord',
        'Discord.Desktop',
        'DiscordInc.Discord',
        'com.squirrel.Discord.Discord',
        'com.discordapp.Discord'
    )) {
        & $setOff $id
        Write-Ok "Windows toasts off: $id"
    }

    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '(?i)discord' } |
        ForEach-Object {
            Set-ItemProperty -Path $_.PSPath -Name 'Enabled' -Value 0 -Type DWord -Force
            Set-ItemProperty -Path $_.PSPath -Name 'ShowInActionCenter' -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
        }
}

# Back-compat alias name used by older call sites
function Set-DiscordWindowsNotificationsOn {
    Set-DiscordWindowsNotificationsOff
}

function Set-DiscordTrayIconHidden([string]$AppDir) {
    $notifyKey = 'HKCU:\Control Panel\NotifyIconSettings'
    if (-not (Test-Path $notifyKey)) { return }

    $targets = @(
        (Join-Path $AppDir 'Discord.exe'),
        (Get-DiscOptEnvPath 'LOCALAPPDATA' 'Discord\Update.exe'),
        (Join-Path $AppDir 'Discord.bin.exe')
    ) | Where-Object { Test-Path $_ }

    $hidden = 0
    Get-ChildItem $notifyKey -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        $path = $props.ExecutablePath
        if (-not $path) { return }
        foreach ($target in $targets) {
            if ($path -ieq $target -or $path -match [regex]::Escape('Discord')) {
                Set-ItemProperty -Path $_.PSPath -Name 'IsPromoted' -Value 0 -Type DWord -Force
                $hidden++
                break
            }
        }
    }

    if ($hidden -gt 0) { Write-Ok "Tray icon hidden ($hidden entries)" }
    else { Write-Warn 'Tray icon registry entry not found yet - launch once, then re-run' }
}

function Set-DiscordVoiceQosPolicies {
    # Documented Windows QoS policy (DSCP 46 / Expedited Forwarding on UDP) so
    # routers with WMM/DSCP trust prioritize Discord voice. One policy per
    # installed variant, fixed names, recorded in recovery for exact removal.
    Write-Step 'Applying voice QoS policies (DSCP 46, UDP)...'
    $results = [Collections.Generic.List[hashtable]]::new()
    $root = Get-ExoDiscordQosPolicyRoot
    foreach ($variant in @(Get-InstalledDiscordVariants)) {
        $policyName = [string]$variant.QosPolicy
        $path = Join-Path $root $policyName
        $ok = $false
        try {
            if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force -ErrorAction Stop | Out-Null }
            foreach ($pair in @(
                @{ N = 'Version'; V = '1.0' },
                @{ N = 'Application Name'; V = [string]$variant.Exe },
                @{ N = 'Protocol'; V = 'UDP' },
                @{ N = 'Local Port'; V = '*' },
                @{ N = 'Remote Port'; V = '*' },
                @{ N = 'Local IP'; V = '*' },
                @{ N = 'Remote IP'; V = '*' },
                @{ N = 'DSCP Value'; V = '46' },
                @{ N = 'Throttle Rate'; V = '-1' }
            )) {
                New-ItemProperty -LiteralPath $path -Name ([string]$pair.N) -Value ([string]$pair.V) -PropertyType String -Force -ErrorAction Stop | Out-Null
            }
            # Verify readback - honest partial-failure reporting.
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            $ok = ([string]$item.GetValue('DSCP Value') -eq '46') -and
                ([string]$item.GetValue('Application Name') -ieq [string]$variant.Exe) -and
                ([string]$item.GetValue('Protocol') -eq 'UDP')
        } catch {
            Write-Warn "QoS policy $policyName`: $($_.Exception.Message)"
        }
        if ($ok) { Write-Ok "QoS DSCP 46 policy active: $policyName ($($variant.Exe))" }
        else { Write-Warn "QoS policy not verified: $policyName" }
        $results.Add(@{ Variant = [string]$variant.Name; Policy = $policyName; Ok = $ok })
    }
    $Script:DiscordQosResults = @($results)
    # Record created policies for repair removal.
    if ($Script:DiscordWindowsRecovery) {
        $Script:DiscordWindowsRecovery.QosPolicies = @(Get-ExoDiscordQosPolicySnapshot)
        Save-DiscordOptState @{
            version         = $Script:DiscOptVersion
            applyStatus     = 'applying'
            applied         = $false
            applyStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
            recovery        = $Script:DiscordWindowsRecovery
        }
    }
    return @($results)
}

function Test-DiscordVoiceQosApplied {
    $root = Get-ExoDiscordQosPolicyRoot
    foreach ($variant in @(Get-InstalledDiscordVariants)) {
        $path = Join-Path $root ([string]$variant.QosPolicy)
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        try {
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            if ([string]$item.GetValue('DSCP Value') -ne '46') { return $false }
            if ([string]$item.GetValue('Protocol') -ne 'UDP') { return $false }
        } catch { return $false }
    }
    return $true
}

function Remove-DiscordExtraSpellcheckDictionaries([ref]$Freed) {
    # Chromium spellcheck dictionaries (*.bdic) under %AppData%\discord\dictionaries.
    # Keep en-US plus the current system locale; Discord re-downloads a dictionary
    # on demand if the user changes spellcheck language (and full repair reinstall
    # restores everything). Deterministic: exact file-name allow list.
    $dictDir = Join-Path $AppData 'dictionaries'
    if (-not (Test-Path -LiteralPath $dictDir)) { return 0 }
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$keep.Add('en-US')
    try {
        $culture = [Globalization.CultureInfo]::CurrentCulture.Name
        if (-not [string]::IsNullOrWhiteSpace($culture)) { [void]$keep.Add($culture) }
    } catch { }
    $removed = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $dictDir -File -Filter '*.bdic' -ErrorAction SilentlyContinue)) {
        # File names look like en-US-10-1.bdic / de-DE-3-0.bdic - locale prefix.
        $locale = $file.BaseName -replace '-\d+-\d+$', ''
        if ($keep.Contains($locale)) { continue }
        if (Remove-Safe $file.FullName $Freed) {
            $removed++
            Write-Ok "Removed spellcheck dictionary $($file.Name)"
        }
    }
    if ($removed -eq 0) { Write-Ok 'Spellcheck dictionaries already lean' }
    return $removed
}

function Set-DiscordVariantQuiet {
    # PTB / Canary quiet pass: host boot flags + no autostart + safe caches.
    # Equicord / DiscOpt kernel stay stable-only by design (test channels update
    # frequently; module layout is not guaranteed). QoS policies are applied per
    # variant by Set-DiscordVoiceQosPolicies.
    $results = [Collections.Generic.List[hashtable]]::new()
    foreach ($variant in @(Get-InstalledDiscordVariants)) {
        if ([string]$variant.Name -eq 'stable') { continue }
        $name = [string]$variant.Name
        Write-Step "Optimizing Discord $name variant..."
        $flagsOk = $false
        $autostartOk = $false
        try {
            $variantAppData = Get-DiscOptEnvPath 'APPDATA' ([string]$variant.AppDataDir)
            $settingsPath = Join-Path $variantAppData 'settings.json'
            $merged = @{}
            if (Test-Path -LiteralPath $settingsPath) {
                try {
                    $merged = ConvertTo-HashtableDeep (Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json)
                } catch { $merged = @{} }
            }
            $merged['OPEN_ON_STARTUP'] = $false
            $merged['MINIMIZE_TO_TRAY'] = $true
            $merged['SKIP_HOST_UPDATE'] = $false
            $merged['DESKTOP_TTI_EARLY_UPDATE_CHECK'] = $false
            $merged['DESKTOP_TTI_DNSTCP_WARMUP'] = $true
            $merged['DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR'] = $true
            $merged['DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS'] = 2000
            $merged.chromiumSwitches = @{
                'disable-breakpad'                        = 1
                'disable-crash-reporter'                  = 1
                'disable-domain-reliability'              = 1
                'disable-logging'                         = 1
                'disable-component-update'                = 1
                'disable-background-networking'           = 1
                'no-pings'                                = 1
                'disable-renderer-backgrounding'          = 1
                'disable-backgrounding-occluded-windows'  = 1
                'disable-background-timer-throttling'     = 1
                'disable-hang-monitor'                    = 1
            }
            if (-not (Test-Path -LiteralPath $variantAppData)) {
                New-Item -ItemType Directory -Path $variantAppData -Force | Out-Null
            }
            if (Test-Path -LiteralPath $settingsPath) { attrib -R $settingsPath 2>$null }
            Write-JsonFile $settingsPath $merged 20
            $flagsOk = $true
            Write-Ok "$name settings.json flags applied (startup off, chromium lean)"
        } catch {
            Write-Warn "$name settings.json: $($_.Exception.Message)"
        }
        try {
            $variantRoot = Get-DiscOptEnvPath 'LOCALAPPDATA' ([string]$variant.LocalDir)
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            if (Test-Path $runKey) {
                $item = Get-Item -Path $runKey -ErrorAction Stop
                $prefix = [IO.Path]::GetFullPath($variantRoot).TrimEnd('\') + '\'
                foreach ($valueName in @($item.GetValueNames())) {
                    $value = [string]$item.GetValue($valueName)
                    $expanded = [Environment]::ExpandEnvironmentVariables($value).Replace('/', '\')
                    if ($expanded.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        Remove-ItemProperty -Path $runKey -Name $valueName -Force -ErrorAction SilentlyContinue
                        Write-Ok "$name startup entry removed: $valueName"
                    }
                }
            }
            $autostartOk = $true
        } catch {
            Write-Warn "$name autostart: $($_.Exception.Message)"
        }
        $results.Add(@{ Variant = $name; SettingsFlags = $flagsOk; AutostartQuiet = $autostartOk })
    }
    if ($results.Count -eq 0) { Write-Ok 'No PTB/Canary variants installed (stable-only pipeline)' }
    $Script:DiscordVariantResults = @($results)
    return @($results)
}

function Test-DiscordWindowsSuppression {
    try {
        if (@(Get-StableDiscordRunSnapshot).Count -ne 0) { return $false }

        $base = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
        foreach ($id in @('Discord', 'Discord.Desktop', 'DiscordInc.Discord', 'com.squirrel.Discord.Discord')) {
            $path = Join-Path $base $id
            if (-not (Test-Path -LiteralPath $path)) { return $false }
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
    Write-Step 'Applying Windows tweaks (notifications, tray, startup, GPU, QoS)...'
    Disable-DiscordWindowsAutostart
    Disable-DiscordScheduledTasks
    Set-DiscordWindowsNotificationsOff
    Set-DiscordTrayIconHidden $AppDir
    Set-DiscordGpuHighPerformance $AppDir
    Set-DiscordFullscreenOptimizationsOff $AppDir
    [void](Set-DiscordVoiceQosPolicies)
    Write-Ok 'Windows tweaks applied (toasts OFF, tray hidden, no autostart, GPU high-perf, voice QoS)'
}

function Test-ExoHasDiscreteGpu {
    try {
        $gpus = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Name })
        foreach ($n in $gpus) {
            if ($n -match '(?i)NVIDIA|GeForce|RTX|GTX|Quadro|Radeon|RX\s*\d|Arc\s*A') {
                # Skip Microsoft Basic / Hyper-V / remote display
                if ($n -match '(?i)Microsoft Basic|Hyper-V|Remote|Virtual') { continue }
                return $true
            }
        }
    } catch { }
    return $false
}

function Set-DiscordGpuHighPerformance([string]$AppDir) {
    # Windows Graphics Settings -> High performance only when a discrete GPU exists
    # (multi-GPU / laptop dGPU). Single iGPU PCs stay Auto (avoids pointless override).
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return }
    try {
        $key = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        if (Test-ExoHasDiscreteGpu) {
            # GpuPreference=2 = High performance
            New-ItemProperty -LiteralPath $key -Name $exe -Value 'GpuPreference=2;' -PropertyType String -Force -ErrorAction Stop | Out-Null
            Write-Ok 'Discord GPU preference = High performance (discrete GPU detected)'
        } else {
            # Clear stale High preference on iGPU-only machines
            Remove-ItemProperty -LiteralPath $key -Name $exe -Force -ErrorAction SilentlyContinue
            Write-Ok 'Discord GPU preference = Auto (no discrete GPU)'
        }
    } catch {
        Write-Warn "GPU preference: $($_.Exception.Message)"
    }
}

function Set-DiscordFullscreenOptimizationsOff([string]$AppDir) {
    # DISABLEDXMAXIMIZEDWINDOWEDMODE reduces DWM interference when Discord is maximized.
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return }
    try {
        $key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        $flags = '~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE'
        New-ItemProperty -LiteralPath $key -Name $exe -Value $flags -PropertyType String -Force -ErrorAction Stop | Out-Null
        Write-Ok 'Discord fullscreen optimizations off + HighDPI aware'
    } catch {
        Write-Warn "Compat layers: $($_.Exception.Message)"
    }
}

function Test-OpenAsarInstalled([string]$ResourcesDir) {
    # Legacy OpenAsar was a small rewrite on _app.asar. Exo Host no longer uses it.
    # Kept for detect/repair of old installs only.
    $target = Join-Path $ResourcesDir '_app.asar'
    if (-not (Test-Path $target)) { return $false }
    $size = (Get-Item $target).Length
    return ($size -gt 10000 -and $size -lt 500000)
}

function Test-ExoHostInstalled([string]$AppDir) {
    # Modern path: tiny Equicord loader on app.asar + host flags (no OpenAsar).
    $resources = Join-Path $AppDir 'resources'
    $loader = Join-Path $resources 'app.asar'
    if (-not (Test-Path -LiteralPath $loader)) { return $false }
    $len = (Get-Item -LiteralPath $loader).Length
    return ($len -ge 64 -and $len -lt 4096)
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

function Remove-LegacyOpenAsar([string]$AppDir) {
    # OpenAsar (small rewrite on _app.asar) breaks modern Equicord which needs
    # the FULL stock Discord package at _app.asar. Always restore stock shell.
    $resources = Join-Path $AppDir 'resources'
    $bootstrap = Join-Path $resources '_app.asar'
    $stock = Join-Path $resources '_app.asar.stock'
    if (-not (Test-Path -LiteralPath $bootstrap)) { return }
    $len = (Get-Item -LiteralPath $bootstrap).Length
    # OpenAsar is ~30-100KB; stock is multi-MB
    if ($len -gt 500000) { return }
    if (Test-Path -LiteralPath $stock) {
        Copy-Item -LiteralPath $stock -Destination $bootstrap -Force
        Write-Ok 'Replaced outdated OpenAsar with stock Discord shell on _app.asar'
    } else {
        Write-Warn 'OpenAsar-sized _app.asar found but no _app.asar.stock - reinstall Discord if Equicord errors'
    }
}

function Install-ExoHost([string]$AppDir) {
    # Exo Host = modern replacement for OpenAsar:
    #  - Equicord owns the client (actively maintained)
    #  - Tiny Exo loader on app.asar
    #  - Discord settings.json host flags (SKIP_HOST_UPDATE, chromium lean, TTI)
    #  - Stock shell kept as _app.asar.stock for repair (never a third-party asar rewrite)
    Write-Step 'Installing Exo Host (Equicord path - no OpenAsar)...'
    Ensure-AsarStockBackup $AppDir
    Remove-LegacyOpenAsar $AppDir
    Apply-DiscordProfile
    if (-not (Test-ExoHostInstalled $AppDir)) {
        Write-Warn 'Equicord loader not on app.asar yet - Install-Equicord will place it'
    } else {
        Write-Ok 'Exo Host ready (Equicord loader + host flags)'
    }
}

function Install-OpenAsar([string]$AppDir) {
    # Back-compat name: OpenAsar is no longer installed.
    Install-ExoHost $AppDir
}

function Unlock-DiscordSettings([string]$DestPath = '') {
    if (-not $DestPath) { $DestPath = Join-Path $AppData 'settings.json' }
    if (Test-Path $DestPath) { attrib -R $DestPath 2>$null }
}

function Get-DiscOptPowerShellExe {
    # Stable PowerShell 7 host for helpers (any pwsh 7.x; never 5.1).
    $found = Get-DiscOptPwsh7
    if ($found -and $found.Exe) { return $found.Exe }
    $cmd = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd -and $cmd.Source -and ($cmd.Source -notmatch 'WindowsPowerShell')) {
        return $cmd.Source
    }
    throw 'PowerShell 7 is required. Install it with: winget install Microsoft.PowerShell'
}

function Apply-DiscordProfile([string]$DestPath = '') {
    Write-Step 'Applying boot/optimizer flags (preserving your in-app settings)...'
    if ([string]::IsNullOrWhiteSpace($DestPath)) {
        $DestPath = Join-Path $AppData 'settings.json'
    }
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

    # Kit keys we may stamp - do NOT force hardware acceleration or BACKGROUND_COLOR.
    # Equicord themes handle dark/AMOLED (not OpenAsar CSS).
    $allowed = @(
        'SKIP_HOST_UPDATE', 'OPEN_ON_STARTUP', 'MINIMIZE_TO_TRAY', 'START_MINIMIZED',
        'IS_MAXIMIZED', 'IS_MINIMIZED', 'debugLogging', 'offloadAdmControls',
        'asyncVideoInputDeviceInit', 'DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR',
        'DESKTOP_TTI_DNSTCP_WARMUP', 'DESKTOP_TTI_EARLY_UPDATE_CHECK',
        'DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS',
        'audioSubsystem', 'useLegacyAudioDevice'
    )
    foreach ($key in $allowed) {
        if ($kit.Keys -contains $key) { $merged[$key] = $kit[$key] }
    }
    # Leave enableHardwareAcceleration alone (Discord default = on). Remove forced false from old kits.
    if ($merged.Keys -contains 'enableHardwareAcceleration' -and $merged['enableHardwareAcceleration'] -eq $false) {
        if (-not ($kit.Keys -contains 'enableHardwareAcceleration')) {
            $merged.Remove('enableHardwareAcceleration')
            Write-LogLine 'OK' 'Hardware acceleration left at Discord default (not forced off)'
        }
    }

    # Exo Host chromium lean (safe; no single-process / sandbox kills).
    # disable-background-timer-throttling: keep JS timers full-rate when the
    # window is hidden (voice/notification latency; real Chromium switch).
    # disable-hang-monitor: no "page unresponsive" watchdog dialogs (real switch;
    # UI-only watchdog, no renderer behavior change).
    # FORBIDDEN here (documented client blanking): single-process, disable-gpu,
    # disable-software-rasterizer, disable-gpu-compositing, in-process-gpu.
    $merged.chromiumSwitches = @{
        'disable-breakpad'                        = 1
        'disable-crash-reporter'                  = 1
        'disable-domain-reliability'              = 1
        'disable-logging'                         = 1
        'disable-component-update'                = 1
        'disable-background-networking'           = 1
        'no-pings'                                = 1
        'disable-renderer-backgrounding'          = 1
        'disable-backgrounding-occluded-windows'  = 1
        'disable-background-timer-throttling'     = 1
        'disable-hang-monitor'                    = 1
    }
    # Drop legacy OpenAsar settings block - Equicord NoTrack/SilentTyping cover that surface.
    if ($merged.Keys -contains 'openasar') { $merged.Remove('openasar') }

    # Stable boot flags (Equicord AMOLED theme owns look)
    $merged['DESKTOP_TTI_EARLY_UPDATE_CHECK'] = $false
    $merged['DESKTOP_TTI_DNSTCP_WARMUP'] = $true
    $merged['DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR'] = $true
    $merged['DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS'] = 2000
    $merged['audioSubsystem'] = 'standard'
    $merged['useLegacyAudioDevice'] = $false
    $merged['asyncVideoInputDeviceInit'] = $false
    $merged['debugLogging'] = $false
    $merged['OPEN_ON_STARTUP'] = $false
    $merged['SKIP_HOST_UPDATE'] = $true
    $merged['MINIMIZE_TO_TRAY'] = $true
    if ($merged.Keys -contains 'BACKGROUND_COLOR') { $merged.Remove('BACKGROUND_COLOR') }

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
    Write-Ok 'Exo Host flags applied (SKIP_HOST_UPDATE + chromium lean + TTI; no OpenAsar)'
}

