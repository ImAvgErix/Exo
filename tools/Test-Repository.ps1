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

function Read-JsonForRepositoryCheck([IO.FileInfo]$File) {
    $text = Get-Content -LiteralPath $File.FullName -Raw
    # TypeScript's tsconfig files are JSONC by design. Strip only comment
    # forms used by those configs, while keeping strict parsing for manifests.
    if ($File.Name -like 'tsconfig*.json') {
        $text = [regex]::Replace($text, '/\*.*?\*/', '', [Text.RegularExpressions.RegexOptions]::Singleline)
        $text = [regex]::Replace($text, '(?m)^\s*//.*$', '')
    }
    return ($text | ConvertFrom-Json -ErrorAction Stop)
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
if (-not $projectVersion) {
    # Single source of truth since 4.8.0: Directory.Build.props reads VERSION for
    # every project, so Exo.csproj must NOT hardcode its own copy.
    $props = Get-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Raw
    if ($props -notmatch 'VersionFile|ExoVersion' -or $props -notmatch 'ReadAllText') {
        Add-Failure 'VERSION is the single version source; Directory.Build.props must read it into Version'
    }
}
elseif ($projectVersion -ne $appVersion) {
    Add-Failure "VERSION mismatch: VERSION=$appVersion, Exo.csproj=$projectVersion"
}

# Product WebView UI must ship; missing wwwroot shows "Exo UI not built" to users.
$wwwIndex = Join-Path $Root 'Exo\wwwroot\index.html'
if (-not (Test-Path -LiteralPath $wwwIndex)) {
    Add-Failure 'Exo/wwwroot/index.html missing - run: cd ui && npm ci && npm run build'
}
$csprojText = Get-Content -LiteralPath (Join-Path $Root 'Exo\Exo.csproj') -Raw
if ($csprojText -notmatch 'Content Include="wwwroot\\') {
    Add-Failure 'Exo.csproj must always Content-Include wwwroot/** (do not pack UI only inside BuildWebUi/node_modules)'
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
        'Exo.SteamMemoryGuard',
        'SetProcessInformation',
        'SetMemoryPriority',
        'ForegroundPid',
        'ProcessPriorityClass]::Normal',
        'ProcessPriorityClass]::BelowNormal',
        '$steamCls = if ($InGame)'
    )) {
        Assert-ContainsText $embeddedHelper $marker 'Steam companion helper'
    }
    # Align with SteamLogic.IsMemoryGuardText: ban EmptyWorkingSet AND
    # SetProcessWorkingSetSize / SoftReclaimWorkingSet thrash of Steam CEF.
    foreach ($rawLine in ($embeddedHelper -split "`n")) {
        $line = $rawLine.TrimStart()
        if ($line.StartsWith('#') -or $line.StartsWith('//')) { continue }
        if ($line.Contains('EmptyWorkingSet(')) {
            Add-Failure 'Steam memory guard contains EmptyWorkingSet (unsafe CEF thrash)'
            break
        }
        if ($line.Contains('SetProcessWorkingSetSize') -or $line.Contains('SoftReclaimWorkingSet')) {
            Add-Failure 'Steam memory guard still thrash-trims CEF working set'
            break
        }
        if ($line -match '(?i)Stop-Process.*steamwebhelper|Suspend-Process') {
            Add-Failure 'Steam memory guard contains an unsafe suspend or kill operation'
            break
        }
    }
}

$discordConfig = Get-Content -LiteralPath (Join-Path $Root 'Exo\Scripts\Discord\kit\config.ini') -Raw
foreach ($marker in @('TrimIntervalMs=2000', 'PriorityClass=3')) {
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
    'Cleaner Steam install',
    'Silent Windows integration',
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
# 3.16.x may leave display prefs CPL-owned (displayMethod='unchanged') while still
# tracking the nvapi marker for live verify when Exo owns display.
$optimizerWritesDisplayMethod = $nvidiaOptimizer -match "displayMethod\s*=\s*'nvapi'" -or
    $nvidiaOptimizer -match '\$displayMethod\s*=\s*if\s*\(\$displayNvApiOk\)\s*\{\s*''nvapi''\s*\}' -or
    $nvidiaOptimizer -match "displayMethod\s*=\s*'unchanged'"
if (-not $optimizerWritesDisplayMethod -or
    ($nvidiaPowerShell -notmatch "displayMethod.*-eq\s*'nvapi'" -and
     $nvidiaPowerShell -notmatch "displayMethod\s*=\s*'unchanged'")) {
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
# Call site only (function definition also mentions Start-DriverUpdateIfNeeded earlier).
$driverStageIndex = $nvidiaOptimizer.IndexOf('Normalize-DriverUpdateInfo (Start-DriverUpdateIfNeeded', [StringComparison]::Ordinal)
if ($driverStageIndex -lt 0) {
    $driverStageIndex = $nvidiaOptimizer.IndexOf('Coerce-Hashtable (Start-DriverUpdateIfNeeded', [StringComparison]::Ordinal)
}
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
    # Display prefs are CPL-owned on 3.16.x - status no longer gates on $displayOk.
    $priorityMarkers = @(
        '$pendingAfterDriver',
        '$needsRetweak',
        '-not $profileOk',
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
        $null = Read-JsonForRepositoryCheck $file
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
            '390467'    = '2'
            '277041152' = '1'
            '294973784' = $(if ($gsyncProfile) { '1' } else { '0' })
            '11041279'  = '0'
            '11041231'  = $(if ($gsyncProfile) { '1199655232' } else { '138504007' })
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

# Mutual recursion between functions in the same script hangs the kit instead of
# failing it: the caller spins until the runner's timeout kills it, after the module
# has already reported progress. Static parse catches it; no gate can catch it at
# runtime because the hang looks identical to slow work.
# Direct self-recursion is allowed - it is deliberate and the author bounds it.
$recursionHits = [System.Collections.Generic.List[string]]::new()
foreach ($file in $scripts) {
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) { continue }  # syntax gate above already reported it

    $defined = @{}
    foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $defined[$fn.Name] = $fn
    }
    if ($defined.Count -lt 2) { continue }

    $edges = @{}
    foreach ($name in $defined.Keys) {
        $callees = [System.Collections.Generic.List[string]]::new()
        foreach ($cmd in $defined[$name].Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            $callee = $cmd.GetCommandName()
            if ($callee -and $defined.ContainsKey($callee) -and $callee -ne $name -and -not $callees.Contains($callee)) {
                [void]$callees.Add($callee)
            }
        }
        $edges[$name] = $callees
    }

    # Transitive closure by repeated relaxation. A function that can reach itself
    # through at least one other function is in a cycle. Function counts per file are
    # small, so the naive closure is cheaper than getting an iterative DFS right.
    $reach = @{}
    foreach ($name in $edges.Keys) { $reach[$name] = [System.Collections.Generic.HashSet[string]]::new([string[]]$edges[$name]) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($name in @($reach.Keys)) {
            foreach ($mid in @($reach[$name])) {
                foreach ($far in $reach[$mid]) {
                    if ($reach[$name].Add($far)) { $changed = $true }
                }
            }
        }
    }

    foreach ($name in ($reach.Keys | Sort-Object)) {
        if (-not $reach[$name].Contains($name)) { continue }
        # BFS back to itself for a readable path, so the message names the whole loop.
        $prev = @{}
        $queue = New-Object System.Collections.Queue
        foreach ($n in $edges[$name]) { $prev[$n] = $name; $queue.Enqueue($n) }
        $path = $null
        while ($queue.Count -gt 0 -and -not $path) {
            $node = $queue.Dequeue()
            if ($node -eq $name) {
                $path = [System.Collections.Generic.List[string]]::new()
                $walk = $node
                while ($walk -and $path.Count -le $edges.Count) {
                    $path.Insert(0, $walk)
                    if ($walk -eq $name -and $path.Count -gt 1) { break }
                    $walk = $prev[$walk]
                }
                break
            }
            foreach ($n in $edges[$node]) {
                if (-not $prev.ContainsKey($n)) { $prev[$n] = $node; $queue.Enqueue($n) }
            }
        }
        $label = if ($path) { $path -join ' -> ' } else { "$name (cycle)" }
        if (-not $recursionHits.Contains("${rel} : $label")) { [void]$recursionHits.Add("${rel} : $label") }
    }
}
if ($recursionHits.Count -gt 0) {
    Add-Failure ("Mutually recursive functions will hang the kit (found {0}): {1}" -f $recursionHits.Count, ($recursionHits -join '; '))
}

# Write-HubProgress drives the orb's bar. Two things have broken it before and neither
# is visible to a parse-only check:
#   1. a backwards percent, which reads to the user as the module stalling;
#   2. an undefined-variable read, which every kit turns into a hard stop because the
#      shipped scripts run under Set-StrictMode.
# So actually EXECUTE each implementation, under StrictMode, and assert the contract.
$progressFiles = $scripts | Where-Object {
    (Get-Content -LiteralPath $_.FullName -Raw) -match '(?m)^\s*function\s+Write-HubProgress'
}
foreach ($file in $progressFiles) {
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors -and $parseErrors.Count -gt 0) { continue }
    $fn = $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Write-HubProgress'
    }, $true) | Select-Object -First 1
    if (-not $fn) { continue }

    $probe = @(
        'Set-StrictMode -Version Latest'
        '$ErrorActionPreference = ''Stop'''
        '$env:EXO = ''1'''
        '$env:EXO_LOG = $null'
        $fn.Extent.Text
        'Write-HubProgress 10 ''a'''
        'Write-HubProgress 55 ''b'''
        'Write-HubProgress 30 ''c'''   # backwards: must be clamped up to 55
        'Write-HubProgress 0 ''d'''    # explicit reset: allowed
        'Write-HubProgress 40 ''e'''
    ) -join [Environment]::NewLine

    $probeFile = Join-Path ([IO.Path]::GetTempPath()) ("exo-progress-{0}.ps1" -f [Guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $probeFile -Value $probe -Encoding UTF8
        $raw = & pwsh -NoProfile -ExecutionPolicy Bypass -File $probeFile 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $raw -match 'cannot be retrieved because it has not been set') {
            $detail = (($raw.Trim() -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' | ')
            Add-Failure ("Write-HubProgress in {0} fails under StrictMode: {1}" -f $rel, $detail)
            continue
        }
        $values = @([regex]::Matches($raw, 'EXO_PROGRESS:(\d+)\|') | ForEach-Object { [int]$_.Groups[1].Value })
        if ($values.Count -eq 0) {
            Add-Failure "Write-HubProgress in $rel emitted no EXO_PROGRESS lines"
            continue
        }
        $high = 0
        foreach ($v in $values) {
            if ($v -eq 0) { $high = 0; continue }
            if ($v -lt $high) {
                Add-Failure "Write-HubProgress in $rel moved backwards: $v after $high"
                break
            }
            $high = $v
        }
    } finally {
        Remove-Item -LiteralPath $probeFile -Force -ErrorAction SilentlyContinue
    }
}

# The shipped kits run under Set-StrictMode, where reading an absent property off a
# registry object THROWS instead of yielding $null. The usual shape
#   (Get-ItemProperty -LiteralPath $p -ErrorAction SilentlyContinue).Something
# looks null-safe because of -ErrorAction, and it is not. Wrapped in the bare
# "catch { }" these reads always seem to carry, the throw silently skips whatever
# else was in that try block. That is how the Steam FSO enforcement counter and the
# NVIDIA MSI check both ended up dead while reporting success. Use a guarded helper
# (Get-NvoRegValue / Get-NvRegValue / Get-ExoRegStringOrNull / Get-SteamObjectProperty).
$strictDerefHits = [System.Collections.Generic.List[string]]::new()
foreach ($file in $scripts) {
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $lineNo++
        if ($line -match '^\s*#') { continue }   # the explanatory comments quote the bad shape
        # -EA is -ErrorAction, and this check used to spell out only the long form. Two live
        # dereferences in Nvidia-Optimizer.ps1 (the PowerMizer and DisableDynamicPstate loops,
        # both reading .DriverDesc off a display class node that need not have one) sat behind
        # the alias and were gated by nothing. A quoted property name -- .'Do not use NLA' --
        # throws identically, so it is matched too.
        #
        # Deliberately NOT widened to bare Get-Item: `(Test-Path $f) -and (Get-Item $f).Length`
        # is idiomatic here and safe, because -and short-circuits before the dereference. The
        # registry cmdlets are the dangerous ones, since a key that EXISTS can still be missing
        # the value being read and no Test-Path guards that.
        if ($line -match 'Get-ItemProperty(?:Value)?[^\r\n]*-(?:ErrorAction|EA)\s+(?:SilentlyContinue|Ignore)\s*\)\s*\.\s*[''"$\w]') {
            [void]$strictDerefHits.Add("${rel}:$lineNo")
        }
    }
}
if ($strictDerefHits.Count -gt 0) {
    Add-Failure ("Registry property dereferenced off a possibly-absent object; throws under StrictMode (found {0}): {1}" -f $strictDerefHits.Count, ($strictDerefHits -join '; '))
}

# The elevated apply path does not run a .ps1 from disk. PowerShellRunnerService builds a
# PowerShell bootstrap out of C# string literals, base64-encodes it and hands it to an
# elevated pwsh, so it is the one script in the product that no PowerShell syntax check ever
# sees - and every elevated Apply and Repair goes through it. A typo there breaks all of them
# at once, on the user's machine, with the error arriving as an exit code.
$runnerPath = Join-Path $Root 'Exo\Services\PowerShellRunnerService.cs'
if (Test-Path -LiteralPath $runnerPath) {
    $runnerLines = [IO.File]::ReadAllLines($runnerPath)
    $bootstrapStart = -1
    for ($i = 0; $i -lt $runnerLines.Count; $i++) {
        if ($runnerLines[$i] -match 'bootstrapBody\s*=\s*string\.Join') { $bootstrapStart = $i + 1; break }
    }
    if ($bootstrapStart -ge 0) {
        $collected = [System.Collections.Generic.List[string]]::new()
        for ($i = $bootstrapStart + 1; $i -lt $runnerLines.Count; $i++) {
            $trimmed = $runnerLines[$i].Trim()
            if ($trimmed -eq '});') { break }
            if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('//')) { continue }
            $literals = [regex]::Matches($trimmed, '"((?:[^"\\]|\\.)*)"')
            if ($literals.Count -eq 0) { continue }
            $joined = ''
            foreach ($m in $literals) {
                $piece = $m.Groups[1].Value
                $piece = $piece -replace '\\"', '"' -replace '\\\\', '\' -replace '\\n', "`n" -replace '\\t', "`t"
                $joined += $piece
            }
            [void]$collected.Add($joined)
        }
        # Reconstruction is heuristic: it drops the C# side of "literal" + variable + "literal"
        # concatenations. That is fine for a syntax check, but if the shape of the array ever
        # changes enough that we recover almost nothing, say so instead of passing silently -
        # "could not verify" and "verified" are not the same answer.
        if ($collected.Count -lt 50) {
            Write-Host "[WARN] Elevated bootstrap check recovered only $($collected.Count) lines; the array shape likely changed - update Test-Repository." -ForegroundColor Yellow
        } else {
            $bootstrapErrors = $null
            $bootstrapTokens = $null
            $null = [System.Management.Automation.Language.Parser]::ParseInput(
                ($collected -join [Environment]::NewLine), [ref]$bootstrapTokens, [ref]$bootstrapErrors)
            if ($bootstrapErrors -and $bootstrapErrors.Count -gt 0) {
                $first = $bootstrapErrors | Select-Object -First 2 | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }
                Add-Failure ("Elevated bootstrap in PowerShellRunnerService.cs is not valid PowerShell ({0} error(s)): {1}" -f $bootstrapErrors.Count, ($first -join '; '))
            }
        }
    }
}

if ($failures.Count -gt 0) {
    throw "Repository integrity checks failed ($($failures.Count) issue(s))."
}

Write-Host "Repository checks passed: $($scripts.Count) PowerShell scripts, $($jsonFiles.Count) JSON files, and $($profiles.Count) NVIDIA profiles." -ForegroundColor Green
