# Exo.ScheduledTasks.ps1 - aggressive ban-safe scheduled task quieting.
# DISABLE (not delete) Microsoft bloat + browser updaters.
# Empty Task Scheduler folders are removed. Never touch BitLocker/TPM/certs/AC/cua-driver.

Set-StrictMode -Version Latest

function Test-ExoTaskProtected {
    param([string]$TaskPath, [string]$TaskName)
    $full = ($TaskPath + $TaskName)
    if ($full -match '(?i)BitLocker|TPM|SystemRestore|VSS|Backup|WindowsBackup|SecureBoot|Credential|HelloFace|Ngscert|CertificateServicesClient|KeyPreGen|AikCert|CryptoPolicy|Data Integrity|Chkdsk|Syspart|Recovery|BrokerInfrastructure|SystemSoundsService|Multimedia\\SystemSounds|cua-driver|CreateExplorerShell|Vanguard|FACEIT|EasyAntiCheat|BattlEye|Riot Vanguard') {
        return $true
    }
    if ($full -match '(?i)Time Synchronization|Time Zone') { return $true }
    return $false
}

function Disable-ExoBloatScheduledTasks {
    param([switch]$Force)
    $disabled = 0
    $errors = 0
    $timedOut = 0
    $foldersRemoved = 0
    $report = [System.Collections.Generic.List[string]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $budgetMs = 120000

    $tasks = @(
        '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser'
        '\Microsoft\Windows\Application Experience\ProgramDataUpdater'
        '\Microsoft\Windows\Application Experience\StartupAppTask'
        '\Microsoft\Windows\Application Experience\PcaPatchDbTask'
        '\Microsoft\Windows\Application Experience\MareBackup'
        '\Microsoft\Windows\Application Experience\SdbinstMergeDbTask'
        '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator'
        '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip'
        '\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask'
        '\Microsoft\Windows\Feedback\Siuf\DmClient'
        '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload'
        '\Microsoft\Windows\Windows Error Reporting\QueueReporting'
        '\Microsoft\Windows\UsageAndQualityInsights\UsageAndQualityInsights-MaintenanceTask'
        '\Microsoft\Windows\CloudExperienceHost\CreateObjectTask'
        '\Microsoft\Windows\Maps\MapsToastTask'
        '\Microsoft\Windows\Maps\MapsUpdateTask'
        '\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector'
        '\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem'
        '\Microsoft\Windows\Maintenance\WinSAT'
        '\Microsoft\Windows\Shell\FamilySafetyMonitor'
        '\Microsoft\Windows\Shell\FamilySafetyRefreshTask'
        '\Microsoft\Windows\Shell\UpdateUserPictureTask'
        '\Microsoft\XblGameSave\XblGameSaveTask'
        '\Microsoft\Windows\PushToInstall\LoginCheck'
        '\Microsoft\Windows\PushToInstall\Registration'
        '\Microsoft\Windows\Device Information\Device'
        '\Microsoft\Windows\DeviceDirectoryClient\HandleCommand'
        '\Microsoft\Windows\DeviceDirectoryClient\HandleWnsCommand'
        '\Microsoft\Windows\DeviceDirectoryClient\IntegrityCheck'
        '\Microsoft\Windows\DeviceDirectoryClient\LocateCommandUserSession'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDeviceAccountChange'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDeviceLocationRightsChange'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDevicePeriodic24'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDevicePolicyChange'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDeviceProtectionStateChanged'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterDeviceSettingChange'
        '\Microsoft\Windows\DeviceDirectoryClient\RegisterUserDevice'
        '\Microsoft\Windows\WindowsUpdate\Scheduled Start'
        '\Microsoft\Windows\WindowsUpdate\Refresh Group Policy Cache'
        '\Microsoft\Windows\WaaSMedic\PerformRemediation'
        '\Microsoft\Windows\Flighting\FeatureConfig\ReconcileFeatures'
        '\Microsoft\Windows\Flighting\FeatureConfig\UsageDataFlushing'
        '\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReporting'
        '\Microsoft\Windows\Flighting\OneSettings\RefreshCache'
        '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan'
        '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static Task'
        '\Microsoft\Windows\UpdateOrchestrator\Start Oobe Expedite Work'
        '\Microsoft\Windows\UpdateOrchestrator\StartOobeAppsScan_LicenseAccepted'
        '\Microsoft\Windows\UpdateOrchestrator\StartOobeAppsScanAfterUpdate'
        '\Microsoft\Windows\UpdateOrchestrator\UIEOrchestrator'
        '\Microsoft\Windows\UpdateOrchestrator\UUS Failover Task'
        '\Microsoft\Windows\Speech\SpeechModelDownloadTask'
        '\Microsoft\Windows\Windows Defender\Windows Defender Cache Maintenance'
        '\Microsoft\Windows\Windows Defender\Windows Defender Cleanup'
        '\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan'
        '\Microsoft\Windows\Windows Defender\Windows Defender Verification'
        '\Microsoft\Windows\Windows Media Sharing\UpdateLibrary'
        '\Microsoft\Windows\SettingSync\BackgroundUploadTask'
        '\Microsoft\Windows\Defrag\ScheduledDefrag'
        '\Microsoft\Windows\DiskCleanup\SilentCleanup'
        '\Microsoft\Windows\DirectX\DirectXDatabaseUpdater'
        '\Microsoft\Windows\InstallService\ScanForUpdates'
        '\Microsoft\Windows\InstallService\ScanForUpdatesAsUser'
        '\Microsoft\Windows\Work Folders\Work Folders Logon Synchronization'
        '\Microsoft\Windows\Work Folders\Work Folders Maintenance Work'
        '\Microsoft\Windows\Location\Notifications'
        '\Microsoft\Windows\RetailDemo\CleanupOfflineContent'
        '\Microsoft\Windows\PI\Sqm-Tasks'
        '\Microsoft\Windows\Diagnosis\Scheduled'
        '\Microsoft\Windows\MemoryDiagnostic\ProcessMemoryDiagnosticEvents'
        '\Microsoft\Windows\MemoryDiagnostic\RunFullMemoryDiagnostic'
        '\Microsoft\Windows\CloudRestore\Backup'
        '\Microsoft\Windows\CloudRestore\Restore'
        '\Microsoft\Windows\Subscription\EnableLicenseAcquisition'
        '\Microsoft\Windows\Clip\License Validation'
        '\Microsoft\Windows\SpacePort\SpaceAgentTask'
        '\Microsoft\Windows\SpacePort\SpaceManagerTask'
        '\Microsoft\Windows\Sustainability\PowerGridForecastTask'
        '\Microsoft\Windows\Sustainability\SustainabilityTelemetry'
        '\Microsoft\Windows\UPnP\UPnPHostConfig'
        '\Microsoft\Windows\WOF\WIM-Hash-Management'
        '\Microsoft\Windows\WOF\WIM-Hash-Validation'
        '\Microsoft\Windows\DUSM\dusmtask'
        '\Microsoft\Windows\DiskFootprint\Diagnostics'
        '\Microsoft\Windows\DiskFootprint\StorageSense'
        '\Microsoft\Windows\AppListBackup\Backup'
        '\Microsoft\Windows\AppListBackup\BackupNonMaintenance'
        '\Microsoft\Windows\PerformanceTrace\RequestTrace'
        '\Microsoft\Windows\PerformanceTrace\WhesvcToast'
        '\Microsoft\Windows\WindowsAI\ClickToDo\ModelCachingLimit'
        '\Microsoft\Windows\WindowsAI\ClickToDo\ModelCachingUpdate'
        '\Microsoft\Windows\WindowsAI\Recall\PolicyConfiguration'
        '\Microsoft\Windows\WindowsAI\Settings\InitialConfiguration'
        '\Microsoft\Windows\EDP\EDP App Launch Task'
        '\Microsoft\Windows\EDP\EDP Auth Task'
        '\EqualizerAPOUpdateChecker'
    )

    foreach ($tn in $tasks) {
        if ($sw.ElapsedMilliseconds -gt $budgetMs) { $timedOut++; break }
        try {
            $p = Start-Process -FilePath "$env:SystemRoot\System32\schtasks.exe" `
                -ArgumentList @('/Change', '/TN', $tn, '/DISABLE') `
                -WindowStyle Hidden -PassThru -ErrorAction Stop
            if (-not $p.WaitForExit(2500)) {
                try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
                $timedOut++
                continue
            }
            if ($p.ExitCode -eq 0) {
                $disabled++
                if ($report.Count -lt 40) { [void]$report.Add("disable:$tn") }
            }
        } catch { $errors++ }
    }

    # Root Edge/Brave updaters (GUID suffixes vary)
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $root = $svc.GetFolder('\')
        foreach ($task in @($root.GetTasks(0))) {
            if ($sw.ElapsedMilliseconds -gt $budgetMs) { break }
            $name = [string]$task.Name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            if (Test-ExoTaskProtected -TaskPath '\' -TaskName $name) { continue }
            if ($name -notmatch '(?i)^(MicrosoftEdgeUpdate|BraveSoftwareUpdate|GoogleUpdate|GoogleUpdater|EqualizerAPO)') {
                continue
            }
            $p = Start-Process -FilePath "$env:SystemRoot\System32\schtasks.exe" `
                -ArgumentList @('/Change', '/TN', "\$name", '/DISABLE') `
                -WindowStyle Hidden -PassThru -ErrorAction SilentlyContinue
            if ($p -and $p.WaitForExit(2500) -and $p.ExitCode -eq 0) {
                $disabled++
                if ($report.Count -lt 40) { [void]$report.Add("disable-root:$name") }
            }
        }
    } catch { }

    # Empty folders bottom-up
    try {
        $foldersRemoved = Remove-ExoEmptyTaskFolders -BudgetMs $budgetMs -Stopwatch $sw
    } catch { $foldersRemoved = 0 }

    $total = $tasks.Count
    return [pscustomobject]@{
        Disabled        = [int]$disabled
        AlreadyDisabled = 0
        Skipped         = 0
        Errors          = [int]$errors
        TimedOut        = [int]$timedOut
        EmptyFolders    = [int]$foldersRemoved
        TotalTasks      = [int]$total
        DisabledTotal   = [int]$disabled
        DisabledPct     = if ($total -gt 0) { [math]::Round(100.0 * $disabled / $total, 1) } else { 0 }
        Samples         = @($report)
        Ok              = ($disabled -gt 0 -or $errors -eq 0)
        ElapsedMs       = [int]$sw.ElapsedMilliseconds
    }
}

function Remove-ExoEmptyTaskFolders {
    param([int]$BudgetMs = 30000, $Stopwatch = $null)
    if (-not $Stopwatch) { $Stopwatch = [Diagnostics.Stopwatch]::StartNew() }
    $removed = 0
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        function Walk([string]$Path) {
            if ($Stopwatch.ElapsedMilliseconds -gt $BudgetMs) { return }
            $folder = $svc.GetFolder($Path)
            $childNames = @()
            foreach ($c in @($folder.GetFolders(0))) { $childNames += [string]$c.Path }
            foreach ($cp in $childNames) { Walk $cp }
            if ($Path -eq '\' -or $Path -eq '') { return }
            if ($Path -match '(?i)BitLocker|\\TPM|CertificateServices|SystemRestore|RecoveryEnvironment|Windows Defender|Data Integrity') {
                return
            }
            $tasks = @($folder.GetTasks(0)).Count
            $subs = @($folder.GetFolders(0)).Count
            if ($tasks -ne 0 -or $subs -ne 0) { return }
            $trim = $Path.TrimEnd('\')
            $slash = $trim.LastIndexOf('\')
            if ($slash -lt 0) { return }
            $parent = if ($slash -eq 0) { '\' } else { $trim.Substring(0, $slash) }
            $leaf = $trim.Substring($slash + 1)
            if ([string]::IsNullOrWhiteSpace($leaf)) { return }
            try {
                $svc.GetFolder($parent).DeleteFolder($leaf, $null)
                $script:removed++
            } catch { }
        }
        $script:removed = 0
        Walk '\'
        $removed = [int]$script:removed
    } catch { }
    return $removed
}

function Test-ExoScheduledTasksQuieted {
    try {
        $statePath = Join-Path $env:LOCALAPPDATA 'Exo\windows-optimizer.json'
        if (Test-Path -LiteralPath $statePath) {
            $st = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($st.scheduledTasksOk -eq $true) { return $true }
            if ($null -ne $st.scheduledTasksPct -and [double]$st.scheduledTasksPct -ge 70) { return $true }
            if ($st.applyStatus -eq 'applied' -and $st.applied -eq $true) { return $true }
        }
    } catch { }
    return $false
}
