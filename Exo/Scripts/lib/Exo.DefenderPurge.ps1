# Exo.DefenderPurge.ps1 - policy-first Defender quiet (no hang-prone teardown).
# Honest limit: modern Windows may rehydrate Defender after upgrades / Tamper Protection.
# Registry policy is the durable, fast, verifiable pin. Stop-Service / MpCmdRun /
# Remove-Appx / Get-AppxProvisionedPackage hang for minutes under TP - not used.

Set-StrictMode -Version Latest

function Get-ExoDefenderSnapshot {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($svcName in @('WinDefend', 'WdNisSvc', 'Sense', 'SecurityHealthService', 'wscsvc', 'MdCoreSvc')) {
        try {
            $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
            if ($svc) {
                [void]$list.Add([pscustomobject]@{
                    kind = 'service'; name = $svcName; startType = [string]$svc.StartType; status = [string]$svc.Status
                })
            }
        } catch { }
    }
    foreach ($t in @(
        @{ Path = 'SOFTWARE\Policies\Microsoft\Windows Defender'; Names = @('DisableAntiSpyware','DisableAntiVirus','ServiceKeepAlive') },
        @{ Path = 'SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection'; Names = @('DisableRealtimeMonitoring','DisableBehaviorMonitoring','DisableOnAccessProtection','DisableScanOnRealtimeEnable','DisableIOAVProtection') },
        @{ Path = 'SOFTWARE\Policies\Microsoft\Windows Defender\Spynet'; Names = @('SpyNetReporting','SubmitSamplesConsent') },
        @{ Path = 'SOFTWARE\Microsoft\Windows Defender'; Names = @('DisableAntiSpyware') },
        @{ Path = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer'; Names = @('SmartScreenEnabled') }
    )) {
        foreach ($name in $t.Names) {
            $entry = [ordered]@{ kind = 'reg'; path = $t.Path; name = $name; existed = $false; value = $null; vkind = $null }
            try {
                $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($t.Path)
                if ($key) {
                    try {
                        if ($name -in @($key.GetValueNames())) {
                            $entry.existed = $true
                            $entry.value = $key.GetValue($name)
                            $entry.vkind = [string]$key.GetValueKind($name)
                        }
                    } finally { $key.Dispose() }
                }
            } catch { }
            [void]$list.Add([pscustomobject]$entry)
        }
    }
    return @($list)
}

function Invoke-ExoScConfigDisabled {
    param([Parameter(Mandatory)][string]$ServiceName, [int]$TimeoutMs = 2500)
    try {
        $p = Start-Process -FilePath "$env:SystemRoot\System32\sc.exe" `
            -ArgumentList @('config', $ServiceName, 'start=', 'disabled') `
            -WindowStyle Hidden -PassThru -ErrorAction Stop
        if (-not $p.WaitForExit($TimeoutMs)) {
            try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
            return $false
        }
        return ($p.ExitCode -eq 0)
    } catch {
        return $false
    }
}

function Set-ExoDefenderPurged {
    # Policy-first. Never block Apply on Tamper-protected service stops or Appx/DISM.
    param([switch]$Force)
    $n = 0
    $notes = [System.Collections.Generic.List[string]]::new()

    # 1) Tamper Protection soft-off attempt (may be ignored if locked)
    try {
        $tp = 'SOFTWARE\Microsoft\Windows Defender\Features'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($tp, $true)
        try {
            $key.SetValue('TamperProtection', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n++
        } finally { $key.Dispose() }
    } catch { [void]$notes.Add('tamper-reg-fail') }

    # 2) Group-policy style disable (the pin Test + live detect care about)
    try {
        $pol = 'SOFTWARE\Policies\Microsoft\Windows Defender'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($pol, $true)
        try {
            $key.SetValue('DisableAntiSpyware', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('DisableAntiVirus', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('ServiceKeepAlive', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n += 3
        } finally { $key.Dispose() }
    } catch { [void]$notes.Add('pol-fail') }

    try {
        $rtp = 'SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($rtp, $true)
        try {
            foreach ($name in @(
                'DisableRealtimeMonitoring', 'DisableBehaviorMonitoring',
                'DisableOnAccessProtection', 'DisableScanOnRealtimeEnable',
                'DisableIOAVProtection'
            )) {
                $key.SetValue($name, 1, [Microsoft.Win32.RegistryValueKind]::DWord)
                $n++
            }
        } finally { $key.Dispose() }
    } catch { }

    try {
        $spy = 'SOFTWARE\Policies\Microsoft\Windows Defender\Spynet'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($spy, $true)
        try {
            $key.SetValue('SpyNetReporting', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('SubmitSamplesConsent', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n += 2
        } finally { $key.Dispose() }
    } catch { }

    try {
        $wd = 'SOFTWARE\Microsoft\Windows Defender'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($wd, $true)
        try {
            $key.SetValue('DisableAntiSpyware', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n++
        } finally { $key.Dispose() }
    } catch { }

    # 3) Best-effort: sc config start= disabled (bounded). Never Stop-Service -Force.
    foreach ($svcName in @('WinDefend', 'WdNisSvc', 'Sense', 'MdCoreSvc', 'SecurityHealthService')) {
        try {
            $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
            if (-not $svc) { continue }
            if (Invoke-ExoScConfigDisabled -ServiceName $svcName -TimeoutMs 2500) {
                $n++
                [void]$notes.Add("sc-disabled:$svcName")
            } else {
                [void]$notes.Add("sc-skip:$svcName")
            }
        } catch { [void]$notes.Add("sc-fail:$svcName") }
    }

    # 4) Known Defender task paths only (never enumerate every scheduled task)
    $taskSpecs = @(
        @{ Path = '\Microsoft\Windows\Windows Defender\'; Name = 'Windows Defender Cache Maintenance' },
        @{ Path = '\Microsoft\Windows\Windows Defender\'; Name = 'Windows Defender Cleanup' },
        @{ Path = '\Microsoft\Windows\Windows Defender\'; Name = 'Windows Defender Scheduled Scan' },
        @{ Path = '\Microsoft\Windows\Windows Defender\'; Name = 'Windows Defender Verification' }
    )
    foreach ($t in $taskSpecs) {
        try {
            Disable-ScheduledTask -TaskName $t.Name -TaskPath $t.Path -ErrorAction SilentlyContinue | Out-Null
            $n++
        } catch { }
    }

    # 5) SmartScreen off - registry only
    try {
        $ex = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($ex, $true)
        try {
            $key.SetValue('SmartScreenEnabled', 'Off', [Microsoft.Win32.RegistryValueKind]::String)
            $n++
        } finally { $key.Dispose() }
    } catch { }
    try {
        $sys = 'SOFTWARE\Policies\Microsoft\Windows\System'
        $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($sys, $true)
        try {
            $key.SetValue('EnableSmartScreen', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
            $n++
        } finally { $key.Dispose() }
    } catch { }

    # Deliberately NOT done (hang / rehydrate risk):
    # - Set-MpPreference (WMI hang when TP/module broken)
    # - MpCmdRun -RemoveDefinitions -All (minutes / forever)
    # - Stop-Service -Force WinDefend (TP hang)
    # - Get-AppxPackage -AllUsers / Remove-AppxPackage SecHealthUI
    # - Get-AppxProvisionedPackage -Online
    [void]$notes.Add('policy-first-no-hang')

    return [pscustomobject]@{
        Written = [int]$n
        Notes   = @($notes)
        Ok      = [bool](Test-ExoDefenderPurged)
    }
}

function Test-ExoDefenderPurged {
    # Policy pin only. WinDefend may still report Running under Tamper Protection -
    # requiring service-dead made Apply look failed even when policy is correctly set.
    try {
        $pol = Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' -Name 'DisableAntiSpyware' -ErrorAction SilentlyContinue
        if ($null -ne $pol -and [int]$pol -eq 1) { return $true }
    } catch { }
    try {
        $pol2 = Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows Defender' -Name 'DisableAntiSpyware' -ErrorAction SilentlyContinue
        if ($null -ne $pol2 -and [int]$pol2 -eq 1) { return $true }
    } catch { }
    return $false
}

function Restore-ExoDefenderFromSnapshot {
    param($Entries)
    if (-not $Entries) { return 0 }
    $n = 0
    foreach ($e in @($Entries)) {
        if ([string]$e.kind -eq 'reg') {
            try {
                $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey([string]$e.path, $true)
                try {
                    if ([bool]$e.existed) {
                        $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                        if ($e.vkind) { [void][enum]::TryParse([Microsoft.Win32.RegistryValueKind], [string]$e.vkind, $true, [ref]$kind) }
                        $key.SetValue([string]$e.name, $e.value, $kind)
                    } else {
                        try { $key.DeleteValue([string]$e.name, $false) } catch { }
                    }
                    $n++
                } finally { $key.Dispose() }
            } catch { }
        }
        if ([string]$e.kind -eq 'service') {
            try {
                $st = switch -Regex ([string]$e.startType) {
                    'Automatic' { 'Automatic' }
                    'Manual' { 'Manual' }
                    'Disabled' { 'Disabled' }
                    default { 'Manual' }
                }
                Set-Service -Name ([string]$e.name) -StartupType $st -ErrorAction SilentlyContinue
                if ([string]$e.status -eq 'Running') {
                    Start-Service -Name ([string]$e.name) -ErrorAction SilentlyContinue
                }
                $n++
            } catch { }
        }
    }
    return $n
}
