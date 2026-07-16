#Requires -Version 5.1
<#
.SYNOPSIS
  Runs Exo's fast repository integrity checks.

.DESCRIPTION
  Validates PowerShell syntax/encoding, version markers, JSON manifests, and
  NVIDIA Profile Inspector XML. The same check runs in GitHub Actions.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    [void]$script:failures.Add($Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Assert-ContainsText([string]$Text, [string]$Needle, [string]$Context) {
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        Add-Failure "$Context is missing required marker: $Needle"
    }
}

function Test-SemanticVersionFile([string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        Add-Failure "Missing version file: $RelativePath"
        return ''
    }

    $value = (Get-Content -LiteralPath $path -Raw).Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        Add-Failure ("Invalid semantic version in {0}: '{1}'" -f $RelativePath, $value)
    }
    return $value
}

$appVersion = Test-SemanticVersionFile 'VERSION'
$discordVersion = Test-SemanticVersionFile 'Exo\Scripts\Discord\VERSION'
$steamVersion = Test-SemanticVersionFile 'Exo\Scripts\Steam\VERSION'
$nvidiaVersion = Test-SemanticVersionFile 'Exo\Scripts\Nvidia\VERSION'
$null = Test-SemanticVersionFile 'Exo\Scripts\Nvidia\profiles\PROFILE_VERSION'

[xml]$project = Get-Content -LiteralPath (Join-Path $Root 'Exo\Exo.csproj') -Raw
$projectVersion = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($projectVersion -ne $appVersion) {
    Add-Failure "VERSION mismatch: VERSION=$appVersion, Exo.csproj=$projectVersion"
}

$discordOptimizerPath = Join-Path $Root 'Exo\Scripts\Discord\Disc-Optimizer.ps1'
$discordOptimizer = Get-Content -LiteralPath $discordOptimizerPath -Raw
$discordMatch = [regex]::Match($discordOptimizer, '\$Script:DiscOptVersion\s*=\s*''([^'']+)''')
if (-not $discordMatch.Success -or $discordMatch.Groups[1].Value -ne $discordVersion) {
    Add-Failure "Discord version mismatch: VERSION=$discordVersion, script=$($discordMatch.Groups[1].Value)"
}

foreach ($marker in @(
    @{
        Path = 'Exo\Models\AppSettings.cs'
        Pattern = 'DiscordKitVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*"([^"]+)"'
    },
    @{
        Path = 'Exo\Services\SettingsService.cs'
        Pattern = 'settings\.DiscordKitVersion\s*=\s*"([^"]+)"'
    }
)) {
    $markerText = Get-Content -LiteralPath (Join-Path $Root $marker.Path) -Raw
    $markerMatch = [regex]::Match($markerText, $marker.Pattern)
    if (-not $markerMatch.Success -or $markerMatch.Groups[1].Value -ne $discordVersion) {
        Add-Failure "Discord version mismatch: VERSION=$discordVersion, $($marker.Path)=$($markerMatch.Groups[1].Value)"
    }
}

$steamOptimizer = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Steam\Steam-Optimizer.ps1') -Raw
$steamMatch = [regex]::Match($steamOptimizer, '\$Script:SteamOptVersion\s*=\s*''([^'']+)''')
if (-not $steamMatch.Success -or $steamMatch.Groups[1].Value -ne $steamVersion) {
    Add-Failure "Steam version mismatch: VERSION=$steamVersion, script=$($steamMatch.Groups[1].Value)"
}

$nvidiaOptimizer = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Nvidia\Nvidia-Optimizer.ps1') -Raw
$nvidiaDetect = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Nvidia\Exo-Nvidia-Detect.ps1') -Raw
$nvDisplaySource = Get-Content -LiteralPath (Join-Path $Root 'tools\Exo.NvDisplay\Program.cs') -Raw
$nvidiaMatch = [regex]::Match($nvidiaOptimizer, '\$Script:NvidiaOptVersion\s*=\s*''([^'']+)''')
if (-not $nvidiaMatch.Success -or $nvidiaMatch.Groups[1].Value -ne $nvidiaVersion) {
    Add-Failure "NVIDIA version mismatch: VERSION=$nvidiaVersion, script=$($nvidiaMatch.Groups[1].Value)"
}

# Match Windows + Linux path separators (cloud agents run on Linux).
$excludedDirectories = '[\\/](bin|obj|publish|release|node_modules|dist|playwright-report|test-results)[\\/]'
$scripts = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter *.ps1 -File |
    Where-Object { $_.FullName -notmatch $excludedDirectories })

foreach ($file in $scripts) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($text.Length -gt 0 -and [int][char]$text[0] -eq 0xFEFF) {
        $text = $text.Substring(1)
    }
    if ($text -match '[^\x00-\x7F]') {
        Add-Failure "PowerShell source contains non-ASCII text: $($file.FullName.Substring($Root.Length + 1))"
    }

    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName,
        [ref]$tokens,
        [ref]$parseErrors)
    # Only iterate real errors - @($null) is a one-element array in Windows PowerShell.
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($parseError in $parseErrors) {
            if ($null -eq $parseError) { continue }
            Add-Failure "PowerShell parse error in $($file.Name): $($parseError.Message)"
        }
    }
}

# The Steam optimizer emits a helper script from a single-quoted here-string.
# Parse that generated script too; parsing only the outer file cannot catch a
# typo inside the embedded helper.
$embeddedHelperMatch = [regex]::Match(
    $steamOptimizer,
    '(?ms)\$body\s*=\s*@''\r?\n(?<body>.*?)\r?\n''@')
if (-not $embeddedHelperMatch.Success) {
    Add-Failure 'Could not locate the embedded Steam webhelper script.'
}
else {
    $embeddedHelper = $embeddedHelperMatch.Groups['body'].Value
    $helperTokens = $null
    $helperErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput(
        $embeddedHelper,
        [ref]$helperTokens,
        [ref]$helperErrors)
    foreach ($parseError in @($helperErrors)) {
        Add-Failure "Embedded Steam helper parse error: $($parseError.Message)"
    }

    foreach ($marker in @(
        'EmptyWorkingSet',
        'Start-Sleep -Seconds 6',
        'ProcessPriorityClass]::High',
        'ProcessPriorityClass]::BelowNormal'
    )) {
        Assert-ContainsText $embeddedHelper $marker 'Steam webhelper helper'
    }
}

$discordConfig = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\kit\config.ini') -Raw
foreach ($marker in @('TrimIntervalMs=4000', 'PriorityClass=3')) {
    Assert-ContainsText $discordConfig $marker 'Discord aggressive kernel config'
}

$steamDetect = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Steam\Exo-Steam-Detect.ps1') -Raw
$steamDetectCore = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Steam\SteamDetectCore.ps1') -Raw
$discordDetect = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\Exo-Discord-Detect.ps1') -Raw
$discordDetectCore = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\DiscordDetectCore.ps1') -Raw
$discordWindows = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\kit\lib\40-DebloatWindows.ps1') -Raw
$discordRepair = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\Exo-Discord-Repair.ps1') -Raw
$stateService = Get-Content -LiteralPath (Join-Path $Root 'Exo\Services\OptimizerStateService.cs') -Raw

foreach ($marker in @(
    "applyStatus     = 'applying'",
    'Merge-SteamStartupRecovery',
    "applyStatus      = 'repair-pending'",
    'shaderInventoryVerified',
    'installed-game manifest inventory was unreadable or ambiguous'
)) {
    Assert-ContainsText $steamOptimizer $marker 'Steam durable state/fail-closed contract'
}
Assert-ContainsText $steamDetectCore "[string]`$State.applyStatus -ne 'applied'" 'Steam live applied-state contract'
foreach ($marker in @(
    'Test-SteamStartupQuiet',
    'Test-SteamDownloadConfig',
    'Test-SteamClientTweaks',
    'Complete client debloat',
    'Windows quiet shell',
    'Test-SteamCompleteClientDebloat',
    'Test-SteamWindowsQuiet',
    'Reinstate-SteamQuiet'
)) {
    Assert-ContainsText $steamDetect $marker 'Steam live applied-state contract'
}
Assert-ContainsText $steamOptimizer 'Reinstate-SteamQuiet' 'Steam durable quiet helper'
$steamInvalidation = $steamOptimizer.IndexOf("applyStatus     = 'applying'", [StringComparison]::Ordinal)
$steamMutation = $steamOptimizer.IndexOf('$startupResult = Disable-SteamWindowsStartup', [StringComparison]::Ordinal)
if ($steamInvalidation -lt 0 -or $steamMutation -lt 0 -or $steamInvalidation -gt $steamMutation) {
    Add-Failure 'Steam applied state is not invalidated before startup mutation.'
}

$steamTokens = $null
$steamErrors = $null
$steamAst = [Management.Automation.Language.Parser]::ParseInput($steamOptimizer, [ref]$steamTokens, [ref]$steamErrors)
foreach ($helperName in @('Get-SteamObjectProperty', 'Merge-SteamRecoveryItems', 'Merge-SteamStartupRecovery')) {
    $def = @($steamAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $helperName
    }, $true)) | Select-Object -First 1
    if (-not $def) {
        Add-Failure ("Steam recovery helper missing: " + $helperName)
    } else {
        Invoke-Expression $def.Extent.Text
    }
}
if (Get-Command Merge-SteamStartupRecovery -ErrorAction SilentlyContinue) {
    $priorRecovery = @{
        StartupEntries = @(@{ Key = 'HKCU:\Run'; Name = 'Steam'; Value = 'original'; Kind = 'String' })
        StartupModeCaptured = $true; HadStartupMode = $true; PreviousStartupMode = 2; PreviousStartupModeKind = 'DWord'
        ScheduledTasks = @(); Notifications = @(); TrayEntries = @(); AppPath = $null
    }
    $currentRecovery = @{
        StartupEntries = @(
            @{ Key = 'HKCU:\Run'; Name = 'Steam'; Value = 'changed'; Kind = 'String' },
            @{ Key = 'HKLM:\Run'; Name = 'SteamNew'; Value = 'new'; Kind = 'String' }
        )
        StartupModeCaptured = $true; HadStartupMode = $true; PreviousStartupMode = 0; PreviousStartupModeKind = 'DWord'
        ScheduledTasks = @(); Notifications = @(); TrayEntries = @(); AppPath = $null
    }
    $mergedRecovery = Merge-SteamStartupRecovery $priorRecovery $currentRecovery
    if (@($mergedRecovery.StartupEntries).Count -ne 2 -or
        [string]$mergedRecovery.StartupEntries[0].Value -ne 'original' -or
        [int]$mergedRecovery.PreviousStartupMode -ne 2) {
        Add-Failure 'Steam reapply recovery merge no longer preserves original values.'
    }
}
foreach ($helperName in @('Merge-SteamStartupRecovery', 'Merge-SteamRecoveryItems', 'Get-SteamObjectProperty')) {
    Remove-Item ("Function:\" + $helperName) -ErrorAction SilentlyContinue
}

foreach ($marker in @(
    'Initialize-DiscordApplyState',
    'Refresh-DiscordWindowsRecovery',
    'Get-StableDiscordRunSnapshot',
    'Get-StableDiscordTasks',
    'Get-StableDiscordTrayEntries',
    "applyStatus     = 'applying'"
)) {
    Assert-ContainsText $discordWindows $marker 'Discord scoped recovery contract'
}
Assert-ContainsText $discordDetectCore "[string]`$State.applyStatus -ne 'applied'" 'Discord live applied-state contract'
foreach ($marker in @(
    'Test-StableDiscordWindowsQuiet',
    '$markerOk -and $equicordOk',
    '$launchOk'
)) {
    Assert-ContainsText $discordDetect $marker 'Discord live applied-state contract'
}
foreach ($marker in @('repair-pending', 'Restore-RepairRegistryValue', 'ScheduledTasks', 'TrayEntries')) {
    Assert-ContainsText $discordRepair $marker 'Discord exact repair contract'
}
if ($discordWindows -match "TaskName\s+-match\s+'\(\?i\)Discord'" -or
    $discordWindows -match "PSChildName\s+-match\s+'Discord'" -or
    $discordRepair -match "TaskName\s+-match\s+'\(\?i\)Discord'") {
    Add-Failure 'Discord Windows apply/repair regressed to broad name-based matching.'
}
foreach ($marker in @(
    'AreStableDiscordScheduledTasksDisabled',
    'AreStableDiscordTrayEntriesHidden',
    'IsSteamDownloadConfigOptimized',
    'AreSteamClientTweaksOptimized'
)) {
    Assert-ContainsText $stateService $marker 'Fast applied-state contract'
}

$nvidiaScriptRoot = Join-Path $Root 'Exo\Scripts\Nvidia'
$nvidiaPowerShell = (Get-ChildItem -LiteralPath $nvidiaScriptRoot -Filter *.ps1 -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
if ($nvidiaPowerShell -match '(?i)SendKeys|mouse_event|SetCursorPos') {
    Add-Failure 'NVIDIA scripts must not regress to mouse/keyboard UI automation.'
}
if ($nvidiaPowerShell -match 'ExoPrefer(?:GpuScaling|NoScaling|ScalingOverride|FullRgb)\s*=') {
    Add-Failure 'NVIDIA scripts contain obsolete Exo-only Control Panel registry markers.'
}
$optimizerWritesNvapiMethod = $nvidiaOptimizer -match "displayMethod\s*=\s*'nvapi'" -or
    $nvidiaOptimizer -match '\$displayMethod\s*=\s*if\s*\(\$displayNvApiOk\)\s*\{\s*''nvapi''\s*\}'
if (-not $optimizerWritesNvapiMethod -or
    $nvidiaPowerShell -notmatch "displayMethod.*-eq\s*'nvapi'") {
    Add-Failure 'NVIDIA apply/detect scripts do not require the verified NVAPI display marker.'
}
foreach ($marker in @(
    'driverTweaksVerified',
    'driverTweaksVersion',
    'profileSha256',
    'profileDriverVersion',
    'applyInProgress',
    'debloatApplied',
    'overlayDisabled'
)) {
    Assert-ContainsText $nvidiaOptimizer $marker 'NVIDIA verified state writer'
    Assert-ContainsText $nvidiaPowerShell $marker 'NVIDIA apply/detect contract'
}

$invalidationIndex = $nvidiaOptimizer.IndexOf('applyInProgress       = $true', [StringComparison]::Ordinal)
$driverStageIndex = $nvidiaOptimizer.IndexOf('$driverInfo = Coerce-Hashtable (Start-DriverUpdateIfNeeded', [StringComparison]::Ordinal)
if ($invalidationIndex -lt 0 -or $driverStageIndex -lt 0 -or $invalidationIndex -gt $driverStageIndex) {
    Add-Failure 'NVIDIA success marker is not invalidated before the driver/profile mutation pipeline.'
}
if ($nvidiaOptimizer -match 'overlayDisabled\s*=\s*\[bool\]\$debloatResult\.Ok') {
    Add-Failure 'NVIDIA overlay state is still derived from the generic debloat result.'
}
foreach ($source in @($nvidiaOptimizer, $nvidiaDetect)) {
    foreach ($marker in @('Test-NvidiaOverlayDisabled', 'OverlayEnabled', 'NVSPCAPS')) {
        Assert-ContainsText $source $marker 'NVIDIA independent overlay verification'
    }
}
if ($nvidiaDetect -match '\(-not\s+\$displayLive\.Available\s+-or') {
    Add-Failure 'NVIDIA live detection still accepts a stale display marker when the helper is unavailable.'
}
foreach ($marker in @(
    'DisplayEnumerationResult',
    'active-display-enumeration-failed',
    'Complete mode coverage required',
    'targets.Count != allowedDevices.Count'
)) {
    Assert-ContainsText $nvDisplaySource $marker 'NVDisplay fail-closed coverage'
}
foreach ($source in @($nvidiaOptimizer, $nvidiaDetect)) {
    Assert-ContainsText $source 'Test-IsNotebookGpuName' 'NVIDIA notebook guard'
    Assert-ContainsText $source '$gpus = @(Get-NvidiaGpus)' 'NVIDIA PowerShell 5.1 single-GPU handling'

    $mapTokens = $null
    $mapErrors = $null
    $mapAst = [System.Management.Automation.Language.Parser]::ParseInput(
        $source,
        [ref]$mapTokens,
        [ref]$mapErrors)
    foreach ($functionName in @('Get-GpuSeriesFromName', 'Test-IsNotebookGpuName')) {
        $definition = @($mapAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
        }, $true)) | Select-Object -First 1
        if (-not $definition) {
            Add-Failure "NVIDIA mapping function missing: $functionName"
            continue
        }
        Invoke-Expression $definition.Extent.Text
    }

    if ((Get-GpuSeriesFromName 'NVIDIA GeForce GTX 1660 SUPER') -ne '10' -or
        (Get-GpuSeriesFromName 'NVIDIA GeForce RTX 4090') -ne '40' -or
        (Get-GpuSeriesFromName 'NVIDIA GeForce RTX 5090') -ne '50') {
        Add-Failure 'NVIDIA GPU-series mapping regression detected.'
    }
    if (-not (Test-IsNotebookGpuName 'NVIDIA GeForce RTX 4090 Laptop GPU') -or
        -not (Test-IsNotebookGpuName 'NVIDIA GeForce RTX 2080 with Max-Q Design') -or
        (Test-IsNotebookGpuName 'NVIDIA GeForce RTX 4090')) {
        Add-Failure 'NVIDIA notebook/desktop classification regression detected.'
    }
    Remove-Item Function:\Get-GpuSeriesFromName -ErrorAction SilentlyContinue
    Remove-Item Function:\Test-IsNotebookGpuName -ErrorAction SilentlyContinue
}
Assert-ContainsText $nvidiaOptimizer 'will not use desktop driver metadata or packages' 'NVIDIA notebook driver guard'

$statusStart = $nvidiaDetect.IndexOf('$statusText =', [StringComparison]::Ordinal)
$statusEnd = $nvidiaDetect.IndexOf('$detail =', $statusStart, [StringComparison]::Ordinal)
if ($statusStart -lt 0 -or $statusEnd -le $statusStart) {
    Add-Failure 'NVIDIA live status priority block was not found.'
} else {
    $statusBlock = $nvidiaDetect.Substring($statusStart, $statusEnd - $statusStart)
    $priorityMarkers = @(
        '$pendingAfterDriver',
        '$needsRetweak',
        '-not $profileOk',
        '-not $displayOk',
        '-not $backgroundOk',
        '$isApplied'
    )
    $previous = -1
    foreach ($marker in $priorityMarkers) {
        $index = $statusBlock.IndexOf($marker, [StringComparison]::Ordinal)
        if ($index -lt 0 -or $index -le $previous) {
            Add-Failure "NVIDIA live status priority is missing or out of order at: $marker"
            break
        }
        $previous = $index
    }
}

foreach ($marker in @(
    'driverTweaksVerified',
    'driverTweaksVersion',
    'profileSha256',
    'profileDriverVersion',
    'applyInProgress',
    'debloatApplied',
    'overlayDisabled',
    'displayMethod'
)) {
    Assert-ContainsText $stateService $marker 'NVIDIA fast-state contract'
}

$releaseScript = Get-Content -LiteralPath (Join-Path $Root 'Release-Exo.ps1') -Raw
foreach ($marker in @(
    'status --porcelain=v1 --untracked-files=all',
    "branch -ne 'main'",
    'rev-parse origin/main',
    '--target $HeadSha'
)) {
    Assert-ContainsText $releaseScript $marker 'Release source-integrity guard'
}

$bundleService = Get-Content -LiteralPath (Join-Path $Root 'Exo\Services\ScriptBundleService.cs') -Raw
foreach ($marker in @(
    'FilesMatch(bundledHelper, workingHelper)',
    'The NVIDIA display helper did not synchronize correctly.'
)) {
    Assert-ContainsText $bundleService $marker 'NVIDIA helper cache-integrity guard'
}

$vbsFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter *.vbs -File |
    Where-Object { $_.FullName -notmatch $excludedDirectories })
foreach ($file in $vbsFiles) {
    if ([IO.File]::ReadAllText($file.FullName) -match '[^\x00-\x7F]') {
        Add-Failure "VBScript source contains non-ASCII text: $($file.FullName.Substring($Root.Length + 1))"
    }
}

$jsonFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter *.json -File |
    Where-Object {
        $_.FullName -notmatch $excludedDirectories -and
        $_.Name -notin @('package-lock.json', 'package.json')
    })
foreach ($file in $jsonFiles) {
    try {
        $null = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Add-Failure "Invalid JSON in $($file.FullName.Substring($Root.Length + 1)): $($_.Exception.Message)"
    }
}

$profiles = @(Get-ChildItem -LiteralPath (Join-Path $Root 'Exo\Scripts\Nvidia\profiles') -Filter *.nip -File)
foreach ($profile in $profiles) {
    try {
        [xml]$xml = Get-Content -LiteralPath $profile.FullName -Raw
        $profileNodes = @($xml.ArrayOfProfile.Profile)
        if ($profileNodes.Count -ne 1 -or [string]$profileNodes[0].ProfileName -ne 'Base Profile') {
            throw 'Expected exactly one Base Profile element.'
        }
        $settings = @($profileNodes[0].Settings.ProfileSetting)
        if ($settings.Count -lt 60) {
            throw "Profile contains only $($settings.Count) settings."
        }
        $duplicates = @($settings | Group-Object SettingID | Where-Object { $_.Count -gt 1 })
        if ($duplicates.Count -gt 0) {
            throw "Duplicate setting IDs: $($duplicates.Name -join ', ')"
        }
        $actual = @{}
        foreach ($setting in $settings) {
            $actual[[string]$setting.SettingID] = [string]$setting.SettingValue
        }
        $gsyncProfile = $profile.Name -match '(?i)G-SYNC'
        $expected = @{
            '274197361' = '1'
            '6600001'   = '1'
            '549528094' = '1'
            '11306135'  = '4294967295'
            '277041154' = '0'
            '553505273' = '0'
            '390467'    = $(if ($gsyncProfile) { '0' } else { '2' })
            '277041152' = $(if ($gsyncProfile) { '0' } else { '1' })
            '294973784' = $(if ($gsyncProfile) { '1' } else { '0' })
        }
        foreach ($id in $expected.Keys) {
            if (-not $actual.ContainsKey($id) -or $actual[$id] -ne $expected[$id]) {
                throw "Performance invariant failed for setting $id."
            }
        }
    }
    catch {
        Add-Failure "Invalid NVIDIA profile $($profile.Name): $($_.Exception.Message)"
    }
}
if ($profiles.Count -ne 10) {
    Add-Failure "Expected 10 NVIDIA series profiles; found $($profiles.Count)."
}

$profileRoot = Join-Path $Root 'Exo\Scripts\Discord\kit\profiles'
$equicordManifest = Join-Path $profileRoot 'equicordplugins.json'
foreach ($name in @('equicordplugins.json', 'vencordplugins.json', 'equicord-overrides.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $profileRoot $name))) {
        Add-Failure "Missing Discord plugin manifest: $name"
    }
}
if (Test-Path -LiteralPath $equicordManifest) {
    $equicordData = Get-Content -LiteralPath $equicordManifest -Raw | ConvertFrom-Json
    $equicordCount = if ($equicordData -is [Array]) {
        $equicordData.Length
    }
    else {
        @($equicordData).Count
    }
    if ($equicordCount -lt 100) {
        Add-Failure "Equicord plugin manifest is unexpectedly small ($equicordCount entries)."
    }
}

# Wave-1 trust: product Scripts must never CREATE Exo-* scheduled tasks (Unregister/Delete OK).
# Scan only Exo\Scripts (not tools/ which may mention patterns in comments).
$exoScriptRoot = Join-Path $Root 'Exo\Scripts'
$exoTaskCreateHits = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $exoScriptRoot) {
    Get-ChildItem -LiteralPath $exoScriptRoot -Recurse -Filter *.ps1 -File | ForEach-Object {
        $rel = $_.FullName.Substring($Root.Length).TrimStart('\', '/')
        $raw = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrEmpty($raw)) { return }
        # Match Register-ScheduledTask but not Unregister-ScheduledTask
        if ($raw -match '(?i)(?<!Un)Register-ScheduledTask[^\r\n]{0,200}Exo-') {
            [void]$exoTaskCreateHits.Add("$rel : Register-ScheduledTask Exo-*")
        }
        if ($raw -match '(?i)schtasks\s+/Create[^\r\n]{0,200}Exo-') {
            [void]$exoTaskCreateHits.Add("$rel : schtasks /Create Exo-*")
        }
    }
}
if ($exoTaskCreateHits.Count -gt 0) {
    Add-Failure ("Exo must not create scheduled tasks (found {0}): {1}" -f $exoTaskCreateHits.Count, ($exoTaskCreateHits -join '; '))
}

if ($failures.Count -gt 0) {
    throw "Repository integrity checks failed ($($failures.Count) issue(s))."
}

Write-Host "Repository checks passed: $($scripts.Count) PowerShell scripts, $($jsonFiles.Count) JSON files, and $($profiles.Count) NVIDIA profiles." -ForegroundColor Green
