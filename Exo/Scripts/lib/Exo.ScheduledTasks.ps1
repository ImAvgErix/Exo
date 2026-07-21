# Exo.ScheduledTasks.ps1  -  PC-aware Task Scheduler quiet (community multi-PC).
# Enumerate THIS machine, classify, disable noise only. Empty folders removed.
# Never delete Microsoft task definitions. Never touch AC / recovery / cua-driver.

Set-StrictMode -Version Latest

function Test-ExoTaskProtected {
    param([string]$TaskPath, [string]$TaskName)
    $full = ($TaskPath + $TaskName)
    if ($full -match '(?i)BitLocker|\\TPM\\|CertificateServices|SystemRestore|RecoveryEnvironment|Data Integrity|Chkdsk|SecureBoot|Pluton|BrokerInfrastructure|SystemSounds|Multimedia\\|Plug and Play|Time Synchronization|Time Zone|\.NET Framework|Servicing|SoftwareProtection|cua-driver|CreateExplorerShell|Vanguard|FACEIT|EasyAntiCheat|BattlEye|Ricochet|AppxDeployment|StateRepository|Bluetooth|\\USB\\|\\Setup\\|ApplicationData\\|\\AppID\\') {
        return $true
    }
    return $false
}

function Test-ExoTaskQuiet {
    param([string]$TaskPath, [string]$TaskName)
    $full = ($TaskPath + $TaskName)
    if (Test-ExoTaskProtected -TaskPath $TaskPath -TaskName $TaskName) { return $false }

    # Folder-class noise (any task under these on any PC)
    if ($TaskPath -match '(?i)WindowsAI|Customer Experience|Application Experience|Feedback\\|Flighting|UsageAndQualityInsights|DeviceDirectoryClient|Device Directory Client|UpdateOrchestrator|InstallService|\\EDP\\|\\Maps\\|CloudExperienceHost|CloudRestore|PushToInstall|RetailDemo|Sustainability|SpacePort|Work Folders|WaaSMedic|SettingSync|AppListBackup|PerformanceTrace|\\PI\\|\\Diagnosis\\|DiskFootprint|DiskCleanup|\\Defrag\\|DiskDiagnostic|MemoryDiagnostic|Power Efficiency|\\Maintenance\\|\\Location\\|\\Speech\\|Windows Media Sharing|\\UPnP\\|\\WOF\\|\\DUSM\\|\\DirectX\\|Subscription|\\Clip\\|Device Information|Management\\Provisioning|International|LanguageComponentsInstaller|Windows Error Reporting|Windows Defender|WindowsUpdate|NlaSvc|XblGameSave') {
        return $true
    }
    if ($TaskPath -match '(?i)\\Shell\\' -and $TaskName -match '(?i)FamilySafety|UpdateUserPicture|IndexerAutomaticMaintenance') {
        return $true
    }
    # Root updaters  -  names/GUIDs differ per install
    if ($TaskPath -eq '\' -or $TaskPath -eq '') {
        if ($TaskName -match '(?i)^(MicrosoftEdgeUpdate|BraveSoftwareUpdate|GoogleUpdate|GoogleUpdater|EqualizerAPO|Adobe|CCleaner)') { return $true }
        if ($TaskName -match '(?i)UpdateTaskMachine') { return $true }
    }
    return $false
}

function Disable-ExoBloatScheduledTasks {
    param([switch]$Force)
    $disabled = 0
    $protected = 0
    $left = 0
    $alreadyOff = 0
    $timedOut = 0
    $live = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $budgetMs = 120000
    $report = [System.Collections.Generic.List[string]]::new()

    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        function Walk([string]$Path) {
            if ($script:sw.ElapsedMilliseconds -gt $script:budgetMs) { return }
            $folder = $script:svc.GetFolder($Path)
            foreach ($task in @($folder.GetTasks(0))) {
                if ($script:sw.ElapsedMilliseconds -gt $script:budgetMs) { return }
                $script:live++
                $name = [string]$task.Name
                $enabled = $true
                try { $enabled = [bool]$task.Enabled } catch { }
                $fullPath = if ($Path -eq '\') { "\$name" } else { ($Path.TrimEnd('\') + '\' + $name) }

                if (Test-ExoTaskProtected -TaskPath $Path -TaskName $name) {
                    $script:protected++; continue
                }
                if (-not (Test-ExoTaskQuiet -TaskPath $Path -TaskName $name)) {
                    $script:left++; continue
                }
                if (-not $enabled) { $script:alreadyOff++; continue }

                try {
                    $p = Start-Process -FilePath "$env:SystemRoot\System32\schtasks.exe" `
                        -ArgumentList @('/Change', '/TN', $fullPath, '/DISABLE') `
                        -WindowStyle Hidden -PassThru -ErrorAction Stop
                    if (-not $p.WaitForExit(2500)) {
                        try { $p.Kill($true) } catch { }
                        $script:timedOut++
                        continue
                    }
                    if ($p.ExitCode -eq 0) {
                        $script:disabled++
                        if ($script:report.Count -lt 40) { [void]$script:report.Add("disable:$fullPath") }
                    }
                } catch { }
            }
            foreach ($c in @($folder.GetFolders(0))) {
                Walk ([string]$c.Path)
            }
        }
        $script:svc = $svc
        $script:sw = $sw
        $script:budgetMs = $budgetMs
        $script:live = 0
        $script:disabled = 0
        $script:protected = 0
        $script:left = 0
        $script:alreadyOff = 0
        $script:timedOut = 0
        $script:report = $report
        Walk '\'
        $live = $script:live
        $disabled = $script:disabled
        $protected = $script:protected
        $left = $script:left
        $alreadyOff = $script:alreadyOff
        $timedOut = $script:timedOut
        $report = $script:report
    } catch { }

    $foldersRemoved = 0
    try { $foldersRemoved = Remove-ExoEmptyTaskFolders -BudgetMs $budgetMs -Stopwatch $sw } catch { }

    return [pscustomobject]@{
        Live            = [int]$live
        Disabled        = [int]$disabled
        AlreadyOff      = [int]$alreadyOff
        Protected       = [int]$protected
        LeftAlone       = [int]$left
        TimedOut        = [int]$timedOut
        EmptyFolders    = [int]$foldersRemoved
        Samples         = @($report)
        Ok              = $true
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
        function WalkEmpty([string]$Path) {
            if ($Stopwatch.ElapsedMilliseconds -gt $BudgetMs) { return }
            $folder = $svc.GetFolder($Path)
            $childNames = @()
            foreach ($c in @($folder.GetFolders(0))) { $childNames += [string]$c.Path }
            foreach ($cp in $childNames) { WalkEmpty $cp }
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
        WalkEmpty '\'
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
