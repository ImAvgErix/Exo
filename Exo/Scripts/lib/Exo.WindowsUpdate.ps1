# Exo.WindowsUpdate.ps1 - pause / defer Windows Update as far as policy allows.
# Does not delete the Update stack (needed for optional manual installs / Store).
# Repair restores snapped keys when present.

Set-StrictMode -Version Latest

function Get-ExoWindowsUpdateSnapshot {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($t in @(
        @{ Path = 'SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'; Hive = 'HKLM'; Names = @('PauseUpdatesExpiryTime','PauseFeatureUpdatesStartTime','PauseFeatureUpdatesEndTime','PauseQualityUpdatesStartTime','PauseQualityUpdatesEndTime','FlightSettingsMaxPauseDays') },
        @{ Path = 'SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'; Hive = 'HKLM'; Names = @('DeferFeatureUpdates','DeferFeatureUpdatesPeriodInDays','DeferQualityUpdates','DeferQualityUpdatesPeriodInDays','SetDisableUXWUAccess','ExcludeWUDriversInQualityUpdate','DoNotConnectToWindowsUpdateInternetLocations','DisableWindowsUpdateAccess') },
        @{ Path = 'SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'; Hive = 'HKLM'; Names = @('NoAutoUpdate','AUOptions','NoAutoRebootWithLoggedOnUsers','ScheduledInstallDay','ScheduledInstallTime') },
        @{ Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update'; Hive = 'HKLM'; Names = @('AUOptions','IncludeRecommendedUpdates') }
    )) {
        foreach ($name in $t.Names) {
            $entry = [ordered]@{ hive = $t.Hive; path = $t.Path; name = $name; existed = $false; value = $null; kind = $null }
            try {
                $root = [Microsoft.Win32.Registry]::LocalMachine
                $key = $root.OpenSubKey($t.Path)
                if ($key) {
                    try {
                        if ($name -in @($key.GetValueNames())) {
                            $entry.existed = $true
                            $entry.value = $key.GetValue($name)
                            $entry.kind = [string]$key.GetValueKind($name)
                        }
                    } finally { $key.Dispose() }
                }
            } catch { }
            [void]$list.Add([pscustomobject]$entry)
        }
    }
    return @($list)
}

function Set-ExoWindowsUpdateMaxPause {
    # Push pause/defer as far as practical. Not permanent forever (OS re-enables),
    # but max FlightSettings + policy defer + UX pause expiry far out.
    param([switch]$Force)
    $n = 0
    $now = [DateTime]::UtcNow
    # 10 years out (ISO-like local string Windows UX accepts)
    $expiryLocal = $now.AddYears(10).ToLocalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $startLocal = $now.ToLocalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $endLocal = $now.AddYears(10).ToLocalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

    try {
        $ux = 'SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($ux, $true)
        try {
            # Max pause days knob used by Settings UI
            $key.SetValue('FlightSettingsMaxPauseDays', 0x2710, [Microsoft.Win32.RegistryValueKind]::DWord) # 10000
            $key.SetValue('PauseUpdatesExpiryTime', $expiryLocal, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('PauseFeatureUpdatesStartTime', $startLocal, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('PauseFeatureUpdatesEndTime', $endLocal, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('PauseQualityUpdatesStartTime', $startLocal, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('PauseQualityUpdatesEndTime', $endLocal, [Microsoft.Win32.RegistryValueKind]::String)
            $n += 6
        } finally { $key.Dispose() }
    } catch { }

    try {
        $pol = 'SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($pol, $true)
        try {
            $key.SetValue('DeferFeatureUpdates', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('DeferFeatureUpdatesPeriodInDays', 365, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('DeferQualityUpdates', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('DeferQualityUpdatesPeriodInDays', 30, [Microsoft.Win32.RegistryValueKind]::DWord)
            # Hide Windows Update page UX somewhat
            $key.SetValue('SetDisableUXWUAccess', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('ExcludeWUDriversInQualityUpdate', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n += 6
        } finally { $key.Dispose() }
    } catch { }

    try {
        $au = 'SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($au, $true)
        try {
            # 1 = never check (strongest policy); some builds ignore  -  also set NoAutoUpdate
            $key.SetValue('NoAutoUpdate', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('AUOptions', 2, [Microsoft.Win32.RegistryValueKind]::DWord) # notify for download/install
            $key.SetValue('NoAutoRebootWithLoggedOnUsers', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n += 3
        } finally { $key.Dispose() }
    } catch { }

    # Soft-stop update services (do not delete binaries). Medic/WU may fight back until reboot policies stick.
    foreach ($svcName in @('wuauserv', 'UsoSvc', 'DoSvc', 'WaaSMedicSvc')) {
        try {
            $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
            if (-not $svc) { continue }
            try { Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue } catch { }
            try {
                Set-Service -Name $svcName -StartupType Disabled -ErrorAction SilentlyContinue
                $n++
            } catch {
                # Fallback sc.exe config
                try { & sc.exe config $svcName start= disabled | Out-Null; $n++ } catch { }
            }
        } catch { }
    }

    # Scheduled update orchestrator noise (protected list skips critical medic if matched)
    foreach ($tn in @(
        '\Microsoft\Windows\WindowsUpdate\Scheduled Start',
        '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan',
        '\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static Task',
        '\Microsoft\Windows\UpdateOrchestrator\USO_UxBroker',
        '\Microsoft\Windows\UpdateOrchestrator\Reboot',
        '\Microsoft\Windows\UpdateOrchestrator\Reboot_AC',
        '\Microsoft\Windows\UpdateOrchestrator\Reboot_Battery',
        '\Microsoft\Windows\UpdateOrchestrator\Report policies',
        '\Microsoft\Windows\UpdateOrchestrator\UpdateModelTask',
        '\Microsoft\Windows\UpdateOrchestrator\USO_Broker_Display',
        '\Microsoft\Windows\WaaSMedic\PerformRemediation'
    )) {
        try {
            $name = Split-Path $tn -Leaf
            $path = (Split-Path $tn -Parent) + '\'
            if (-not $path.StartsWith('\')) { $path = '\' + $path }
            Disable-ScheduledTask -TaskName $name -TaskPath $path -ErrorAction SilentlyContinue | Out-Null
            $n++
        } catch { }
    }

    return $n
}

function Test-ExoWindowsUpdatePaused {
    try {
        $noAuto = Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -Name 'NoAutoUpdate' -ErrorAction SilentlyContinue
        if ($null -ne $noAuto -and [int]$noAuto -eq 1) { return $true }
    } catch { }
    try {
        $exp = [string](Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -Name 'PauseUpdatesExpiryTime' -ErrorAction Stop)
        if ($exp.Length -gt 8) {
            # Accept any far-future-looking pause string
            return $true
        }
    } catch { }
    return $false
}

function Restore-ExoWindowsUpdateFromSnapshot {
    param($Entries)
    if (-not $Entries) { return 0 }
    $n = 0
    foreach ($e in @($Entries)) {
        try {
            $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey([string]$e.path, $true)
            try {
                if ([bool]$e.existed) {
                    $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                    if ($e.kind) { [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$e.kind, $true, [ref]$kind) }
                    $key.SetValue([string]$e.name, $e.value, $kind)
                } else {
                    try { $key.DeleteValue([string]$e.name, $false) } catch { }
                }
                $n++
            } finally { $key.Dispose() }
        } catch { }
    }
    foreach ($svcName in @('wuauserv', 'UsoSvc', 'DoSvc', 'WaaSMedicSvc')) {
        try {
            Set-Service -Name $svcName -StartupType Manual -ErrorAction SilentlyContinue
            Start-Service -Name $svcName -ErrorAction SilentlyContinue
        } catch { }
    }
    return $n
}
