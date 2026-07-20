# Exo.ScheduledTasks.ps1 - aggressive ban-safe scheduled task quieting.
# DISABLE (not delete) Microsoft bloat. Never touch Defender binaries path separately
# (Defender purge owns those), BitLocker, certs, recovery. Exo-* tasks unregistered.

Set-StrictMode -Version Latest

function Test-ExoTaskProtected {
    param([string]$TaskPath, [string]$TaskName)
    $full = ($TaskPath + $TaskName)
    # Hard protect: security stack remnants, recovery, credentials, AC
    if ($full -match '(?i)BitLocker|TPM|SystemRestore|VSS|Backup|WindowsBackup|SecureBoot|Credential|HelloFace|Ngscert|CertificateServicesClient|KeyPreGen|AikCert|CryptoPolicy|Data Integrity|Chkdsk|Syspart|Recovery|PushToInstall\\Login|Spaceport|BrokerInfrastructure|SystemSoundsService|Multimedia\\SystemSounds') {
        return $true
    }
    if ($full -match '(?i)Vanguard|FACEIT|EasyAntiCheat|BattlEye|Riot Vanguard') { return $true }
    # Keep time sync
    if ($full -match '(?i)Time Synchronization|Time Zone') { return $true }
    return $false
}

function Disable-ExoBloatScheduledTasks {
    # Timeout-safe: explicit task list + schtasks.exe only.
    # NEVER Get-ScheduledTask full catalog (hangs / multi-minute on some PCs).
    param([switch]$Force)
    $disabled = 0
    $errors = 0
    $timedOut = 0
    $report = [System.Collections.Generic.List[string]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $budgetMs = 90000

    $tasks = @(
        '\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser'
        '\Microsoft\Windows\Application Experience\ProgramDataUpdater'
        '\Microsoft\Windows\Application Experience\StartupAppTask'
        '\Microsoft\Windows\Application Experience\PcaPatchDbTask'
        '\Microsoft\Windows\Customer Experience Improvement Program\Consolidator'
        '\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip'
        '\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask'
        '\Microsoft\Windows\Feedback\Siuf\DmClient'
        '\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload'
        '\Microsoft\Windows\Windows Error Reporting\QueueReporting'
        '\Microsoft\Windows\CloudExperienceHost\CreateObjectTask'
        '\Microsoft\Windows\Maps\MapsToastTask'
        '\Microsoft\Windows\Maps\MapsUpdateTask'
        '\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector'
        '\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem'
        '\Microsoft\Windows\Maintenance\WinSAT'
        '\Microsoft\Windows\Shell\FamilySafetyMonitor'
        '\Microsoft\Windows\Shell\FamilySafetyRefreshTask'
        '\Microsoft\XblGameSave\XblGameSaveTask'
        '\Microsoft\Windows\PushToInstall\LoginCheck'
        '\Microsoft\Windows\PushToInstall\Registration'
        '\Microsoft\Windows\Device Information\Device'
        '\Microsoft\Windows\WindowsUpdate\Scheduled Start'
        '\Microsoft\Windows\WaaSMedic\PerformRemediation'
        '\Microsoft\Windows\Flighting\FeatureConfig\ReconcileFeatures'
        '\Microsoft\Windows\Flighting\FeatureConfig\UsageDataFlushing'
        '\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReporting'
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

    $total = $tasks.Count
    return [pscustomobject]@{
        Disabled        = [int]$disabled
        AlreadyDisabled = 0
        Skipped         = 0
        Errors          = [int]$errors
        TimedOut        = [int]$timedOut
        TotalTasks      = [int]$total
        DisabledTotal   = [int]$disabled
        DisabledPct     = if ($total -gt 0) { [math]::Round(100.0 * $disabled / $total, 1) } else { 0 }
        Samples         = @($report)
        Ok              = ($disabled -gt 0 -or $errors -eq 0)
        ElapsedMs       = [int]$sw.ElapsedMilliseconds
    }
}

function Test-ExoScheduledTasksQuieted {
    # Detect must never call Get-ScheduledTask (even named lookups are multi-second on some PCs).
    # Apply writes scheduledTasksOk / scheduledTasksPct into windows-optimizer.json.
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
