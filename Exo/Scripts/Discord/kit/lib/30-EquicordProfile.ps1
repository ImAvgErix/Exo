# 30-EquicordProfile.ps1 - Plugin manifests + hashtable utils
# Dot-sourced by Disc-Optimizer.ps1 (load order = filename sort).
# Universal multi-PC kit - do not assume Equicord/Discord already configured.

function Sync-PluginManifests {
    # Always require local manifests (shipped with Exo) so ANY machine can
    # apply the full plugin list offline. Optionally refresh from GitHub when allowed.
    foreach ($name in @('equicordplugins.json', 'vencordplugins.json')) {
        $dest = Join-Path $Profiles $name
        if (-not (Test-Path $dest)) {
            Write-Warn "Bundled $name missing - downloading for this PC..."
            try {
                Get-EquicordReleaseFile -FileName $name -OutFile $dest | Out-Null
                Write-Ok "Downloaded $name"
            } catch {
                throw "Missing $name and download failed. Reinstall Exo. $($_.Exception.Message)"
            }
            continue
        }

        if ($SkipManifestSync) {
            continue
        }

        if (((Get-Date) - (Get-Item $dest).LastWriteTime).TotalDays -lt 7) {
            Write-LogLine 'OK' "Manifest fresh (<7 days), skipping download: $name"
            continue
        }
        try {
            Get-EquicordReleaseFile -FileName $name -OutFile $dest | Out-Null
            Write-LogLine 'OK' "Refreshed manifest: $name"
        } catch {
            Write-Warn "Could not refresh $name - using bundled copy"
            Write-LogLine 'WARN' "Manifest refresh failed ($name): $($_.Exception.Message)"
        }
    }
    if ($SkipManifestSync) {
        Write-Ok 'Using bundled plugin manifests (universal offline profile)'
    }
}

function Build-FullEquicordSettings {
    # UNIVERSAL profile for any PC: register EVERY known Equicord/Vencord plugin from
    # shipped manifests, then apply Exo overrides (enabled set + options).
    # Stock Discord users have no Equicord settings - this is the source of truth.
    $overridesPath = Join-Path $Profiles 'equicord-overrides.json'
    if (-not (Test-Path $overridesPath)) {
        $overridesPath = Join-Path $Profiles 'equicord.json'
    }
    if (-not (Test-Path $overridesPath)) { throw 'Missing equicord-overrides.json (kit incomplete)' }

    $eqManifest = Join-Path $Profiles 'equicordplugins.json'
    $vcManifest = Join-Path $Profiles 'vencordplugins.json'
    if (-not (Test-Path $eqManifest)) { throw 'Missing equicordplugins.json - required for universal plugin apply' }
    if (-not (Test-Path $vcManifest)) { throw 'Missing vencordplugins.json - required for universal plugin apply' }

    $overrides = ConvertTo-HashtableDeep (Get-Content $overridesPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    $eq = Get-Content $eqManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $vc = Get-Content $vcManifest -Raw -Encoding UTF8 | ConvertFrom-Json

    # Upstream defaults first (required/core plugins often enabledByDefault).
    $defaultOn = @{}
    foreach ($p in (@($eq) + @($vc))) {
        if ($p.enabledByDefault -eq $true) { $defaultOn[$p.name] = $true }
    }

    $allNames = @($eq.name) + @($vc.name) | Select-Object -Unique
    if ($allNames.Count -lt 100) {
        throw "Plugin manifests look incomplete ($($allNames.Count) plugins). Reinstall Exo."
    }

    $plugins = [ordered]@{}
    foreach ($name in ($allNames | Sort-Object)) {
        $plugins[$name] = @{ enabled = [bool]$defaultOn[$name] }
    }

    # Exo curated enable/disable + per-plugin settings (privacy, perf, streamer, etc.).
    if ($overrides.plugins) {
        foreach ($key in @($overrides.plugins.Keys)) {
            $plugins[$key] = ConvertTo-HashtableDeep $overrides.plugins[$key]
        }
    }

    $enabledThemes = @($EnabledTheme)
    if ($overrides.enabledThemes) { $enabledThemes = @($overrides.enabledThemes) }

    # Default false: eagerPatches=true blanks Discord 1.0.9245 + current Equicord
    $eager = $false
    if ($overrides.Keys -contains 'eagerPatches') { $eager = [bool]$overrides.eagerPatches }

    $settings = [ordered]@{
        autoUpdate             = $true
        autoUpdateNotification = $false
        useQuickCss            = $false
        themeLinks             = @()
        eagerPatches           = $eager
        enabledThemes          = $enabledThemes
        enabledThemeLinks      = @()
        enableOnlineThemes     = $false
        enableReactDevtools    = $false
        pinnedThemes           = @()
        themeNames             = @{}
        themeActivationModes   = @{}
        mainWindowFrameless    = $false
        frameless              = $false
        transparent            = $false
        winCtrlQ               = $false
        windowsMaterial        = 'none'
        disableMinSize         = $false
        winNativeTitleBar      = $false
        cloud                  = @{
            authenticated       = $false
            url                 = 'https://cloud.equicord.org/'
            settingsSync        = $false
            settingsSyncVersion = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
        notifications          = @{
            timeout   = 3000
            position  = 'bottom-right'
            useNative = 'never'
            missed    = $false
            logLimit  = 10
        }
        uiElements             = @{
            chatBarButtons        = @{}
            messagePopoverButtons = @{}
        }
        ignoreResetWarning     = $false
        userCssVars            = @{}
        plugins                = $plugins
    }

    if ($overrides.notifications) {
        $settings.notifications = ConvertTo-HashtableDeep $overrides.notifications
    }
    if ($overrides.Keys -contains 'autoUpdate') { $settings.autoUpdate = [bool]$overrides.autoUpdate }
    if ($overrides.Keys -contains 'autoUpdateNotification') {
        $settings.autoUpdateNotification = [bool]$overrides.autoUpdateNotification
    }
    if ($overrides.Keys -contains 'enableOnlineThemes') {
        $settings.enableOnlineThemes = [bool]$overrides.enableOnlineThemes
    }
    if ($overrides.Keys -contains 'useQuickCss') { $settings.useQuickCss = [bool]$overrides.useQuickCss }

    $enabledCount = @($plugins.Values | Where-Object { $_.enabled -eq $true }).Count
    Write-Ok "Universal Equicord profile: $($allNames.Count) plugins registered, $enabledCount enabled (works on stock Discord)"

    return $settings
}

function Get-EquicordLeanPolicy {
    $policyPath = Join-Path $Profiles 'lean-plugin-policy.json'
    if (-not (Test-Path -LiteralPath $policyPath)) { throw 'Missing lean-plugin-policy.json (kit incomplete)' }
    $policy = Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $policy.enabled -or [int]$policy.maximumEnabled -lt 1) { throw 'Lean plugin policy is invalid' }
    return $policy
}

function Get-EquicordLeanAllowedNames {
    param([Parameter(Mandatory)]$Policy)

    $eq = Get-Content (Join-Path $Profiles 'equicordplugins.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $vc = Get-Content (Join-Path $Profiles 'vencordplugins.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifest = @($eq) + @($vc)
    $byName = @{}
    foreach ($plugin in $manifest) { $byName[[string]$plugin.name] = $plugin }

    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @($Policy.enabled)) { [void]$allowed.Add([string]$name) }
    foreach ($plugin in $manifest) {
        if ($plugin.required -eq $true) { [void]$allowed.Add([string]$plugin.name) }
    }

    # Dependencies are executable plugins too. Resolve transitively so a future
    # manifest refresh cannot leave an approved privacy/minimalism plugin half-loaded.
    do {
        $changed = $false
        foreach ($name in @($allowed)) {
            if (-not $byName.ContainsKey($name)) { continue }
            if ($byName[$name].PSObject.Properties.Name -notcontains 'dependencies') { continue }
            foreach ($dependency in @($byName[$name].dependencies)) {
                if ($dependency -and $allowed.Add([string]$dependency)) { $changed = $true }
            }
        }
    } while ($changed)

    return ,$allowed
}

function ConvertTo-HashtableDeep($InputObject) {
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [string]) { return $InputObject }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $table = @{}
        foreach ($key in $InputObject.Keys) { $table[$key] = ConvertTo-HashtableDeep $InputObject[$key] }
        return $table
    }
    if ($InputObject -is [System.Array]) {
        if ($InputObject.Length -eq 0) { return ,@() }
        return [object[]]@($InputObject | ForEach-Object { ConvertTo-HashtableDeep $_ })
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @($InputObject | ForEach-Object { ConvertTo-HashtableDeep $_ })
        if ($items.Count -eq 0) { return ,@() }
        return [object[]]$items
    }
    if ($InputObject -is [pscustomobject]) {
        $table = @{}
        foreach ($prop in $InputObject.PSObject.Properties) {
            $table[$prop.Name] = ConvertTo-HashtableDeep $prop.Value
        }
        return $table
    }
    return $InputObject
}

function Set-DeepValue([hashtable]$Root, [string[]]$Path, $Value) {
    $node = $Root
    for ($i = 0; $i -lt $Path.Length - 1; $i++) {
        $key = $Path[$i]
        if (-not $node.ContainsKey($key) -or -not ($node[$key] -is [hashtable])) {
            $node[$key] = @{}
        }
        $node = $node[$key]
    }
    $node[$Path[-1]] = $Value
}

