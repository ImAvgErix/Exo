# Repair-Internet.ps1 - public one-liner rescue for the Exo Internet optimizer.
# Prefer the Exo in-app Repair button when available.
#
#   irm "https://raw.githubusercontent.com/ImAvgErix/Exo/main/Repair-Internet.ps1" | iex
#
# What it does (self-elevating):
#   1. TRUE RESTORE: if %LocalAppData%\Exo\network-snapshot.json exists (pristine
#      pre-apply baseline), restores the exact recorded values: registry values
#      (or removes ones that were absent), netsh/TCP globals, Set-NetTCPSetting
#      fields, adapter advanced properties by RegistryKeyword, adapter bindings,
#      interface metrics incl. AutomaticMetric, RSS config, powercfg indexes,
#      dynamic port ranges, IPv6 prefix policies, and service start types.
#   2. FALLBACK: without a snapshot, performs an approximate Windows stock reset.
#   3. ALWAYS re-enables disabled physical network adapters and clears Exo
#      network state files.

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

function Write-RepairStep([string]$Message, [string]$Color = 'Cyan') {
    Write-Host ('[*] ' + $Message) -ForegroundColor $Color
}

function Invoke-ExoInternetRepair {
    $exoDir = Join-Path $env:LOCALAPPDATA 'Exo'
    $snapshotPath = Join-Path $exoDir 'network-snapshot.json'
    $applyStatePath = Join-Path $exoDir 'network-apply-state.json'
    $optimizerStatePath = Join-Path $exoDir 'network-optimizer.json'
    $failures = 0

    function Set-RepairDword([string]$Path, [string]$Name, [int]$Value) {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
        Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -ErrorAction SilentlyContinue
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null
    }
    function Remove-RepairProp([string]$Path, [string]$Name) {
        if (Test-Path -LiteralPath $Path) {
            Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -ErrorAction SilentlyContinue
        }
    }
    function Get-RepairNetshValue([string]$Raw, [string]$Label) {
        $m = [regex]::Match([string]$Raw, ('(?im)^\s*' + [regex]::Escape($Label) + '\s*:\s*(\S+)'))
        if ($m.Success) { return $m.Groups[1].Value.ToLowerInvariant() }
        return $null
    }

    $snap = $null
    if (Test-Path -LiteralPath $snapshotPath) {
        try {
            $snap = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
            if (-not $snap.snapshotVersion) { throw 'missing snapshotVersion' }
        } catch {
            $snap = $null
            Write-RepairStep ('Snapshot unreadable, falling back to stock reset: ' + $_.Exception.Message) 'Yellow'
        }
    }

    if ($snap) {
        Write-RepairStep ('Restoring exact pre-Exo network state from snapshot (captured ' + $snap.timestampUtc + ')')

        Write-RepairStep 'Restoring registry values (recorded value or removal if it was absent)...'
        foreach ($rv in @($snap.regValues)) {
            try {
                $kind = [string]$rv.kind
                if ($kind -eq 'absent') {
                    if (Test-Path -LiteralPath $rv.path) {
                        Remove-ItemProperty -LiteralPath $rv.path -Name $rv.name -Force -ErrorAction SilentlyContinue
                    }
                } else {
                    if (-not (Test-Path -LiteralPath $rv.path)) { New-Item -Path $rv.path -Force -ErrorAction SilentlyContinue | Out-Null }
                    if ($kind -eq 'Binary') {
                        $bytes = [byte[]]@(@($rv.value) | ForEach-Object { [byte]$_ })
                        Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $bytes -Type Binary -Force -ErrorAction Stop
                    } elseif ($kind -eq 'MultiString') {
                        Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value ([string[]]@($rv.value)) -Type MultiString -Force -ErrorAction Stop
                    } else {
                        Set-ItemProperty -LiteralPath $rv.path -Name $rv.name -Value $rv.value -Type $kind -Force -ErrorAction Stop
                    }
                }
            } catch {
                $failures++
                Write-RepairStep ('Registry restore failed: ' + $rv.path + ' \ ' + $rv.name) 'Yellow'
            }
        }

        Write-RepairStep 'Restoring TCP globals (netsh) from snapshot...'
        $inet = @($snap.tcpSettings) | Where-Object { [string]$_.settingName -eq 'Internet' } | Select-Object -First 1
        if ($inet -and $inet.autoTuningLevelLocal) {
            $lvl = ([string]$inet.autoTuningLevelLocal).ToLowerInvariant()
            try { netsh int tcp set global autotuninglevel=$lvl | Out-Null } catch {}
        }
        $og = @($snap.offloadGlobal) | Select-Object -First 1
        if ($og) {
            $rscVal = 'enabled'
            if ([string]$og.receiveSegmentCoalescing -match '(?i)disabled') { $rscVal = 'disabled' }
            $rssVal = 'enabled'
            if ([string]$og.receiveSideScaling -match '(?i)disabled') { $rssVal = 'disabled' }
            try { netsh int tcp set global rsc=$rscVal | Out-Null } catch {}
            try { netsh int tcp set global rss=$rssVal | Out-Null } catch {}
        }
        $tcpRaw = [string]$snap.netshTcpGlobalRaw
        foreach ($pair in @(
                @{ Label = 'RFC 1323 Timestamps'; Opt = 'timestamps'; Default = 'default' },
                @{ Label = 'Fast Open Fallback'; Opt = 'fastopenfallback'; Default = 'enabled' },
                @{ Label = 'Fast Open'; Opt = 'fastopen'; Default = 'enabled' },
                @{ Label = 'HyStart'; Opt = 'hystart'; Default = 'enabled' },
                @{ Label = 'Pacing Profile'; Opt = 'pacingprofile'; Default = 'off' },
                @{ Label = 'ECN Capability'; Opt = 'ecncapability'; Default = 'default' }
        )) {
            $val = Get-RepairNetshValue $tcpRaw $pair.Label
            if (-not $val) { $val = $pair.Default }
            try { $null = (netsh int tcp set global "$($pair.Opt)=$val" 2>&1 | Out-String) } catch {}
        }
        $heur = 'enabled'
        if ([string]$snap.netshTcpHeuristicsRaw -match '(?i)\bdisabled\b') { $heur = 'disabled' }
        try { netsh int tcp set heuristics $heur | Out-Null } catch {}
        if ([System.Environment]::OSVersion.Version.Build -ge 26100) {
            $uroVal = 'enabled'
            if ([string]$snap.netshUdpGlobalRaw -match '(?i)\bdisabled\b') { $uroVal = 'disabled' }
            try { $null = (netsh int udp set global uro=$uroVal 2>&1 | Out-String) } catch {}
        }

        Write-RepairStep 'Restoring per-template TCP settings (Set-NetTCPSetting)...'
        foreach ($ts in @($snap.tcpSettings)) {
            $sn = [string]$ts.settingName
            if (-not $sn) { continue }
            try { if ($ts.congestionProvider) { Set-NetTCPSetting -SettingName $sn -CongestionProvider ([string]$ts.congestionProvider) -ErrorAction SilentlyContinue } } catch {}
            try { if ($ts.autoTuningLevelLocal) { Set-NetTCPSetting -SettingName $sn -AutoTuningLevelLocal ([string]$ts.autoTuningLevelLocal) -ErrorAction SilentlyContinue } } catch {}
            try { if ([int]$ts.initialRtoMs -gt 0) { Set-NetTCPSetting -SettingName $sn -InitialRtoMs ([int]$ts.initialRtoMs) -ErrorAction SilentlyContinue } } catch {}
            try { if ([int]$ts.minRtoMs -gt 0) { Set-NetTCPSetting -SettingName $sn -MinRtoMs ([int]$ts.minRtoMs) -ErrorAction SilentlyContinue } } catch {}
            try { if ([int]$ts.maxSynRetransmissions -gt 0) { Set-NetTCPSetting -SettingName $sn -MaxSynRetransmissions ([int]$ts.maxSynRetransmissions) -ErrorAction SilentlyContinue } } catch {}
            try { if ($ts.nonSackRttResiliency) { Set-NetTCPSetting -SettingName $sn -NonSackRttResiliency ([string]$ts.nonSackRttResiliency) -ErrorAction SilentlyContinue } } catch {}
            try { if ($ts.timestamps) { Set-NetTCPSetting -SettingName $sn -Timestamps ([string]$ts.timestamps) -ErrorAction SilentlyContinue } } catch {}
            try { if ($ts.ecnCapability) { Set-NetTCPSetting -SettingName $sn -EcnCapability ([string]$ts.ecnCapability) -ErrorAction SilentlyContinue } } catch {}
        }

        $liveAdapters = @(Get-NetAdapter -ErrorAction SilentlyContinue)
        function Resolve-RepairAdapter([string]$Name, [string]$IfDesc) {
            $hit = $liveAdapters | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1
            if (-not $hit -and $IfDesc) {
                $hit = $liveAdapters | Where-Object { [string]$_.InterfaceDescription -eq $IfDesc } | Select-Object -First 1
            }
            return $hit
        }

        Write-RepairStep 'Re-enabling adapters recorded as enabled...'
        foreach ($ast in @($snap.adapterStates)) {
            try {
                if ($ast.adminUp) {
                    $cur = Resolve-RepairAdapter ([string]$ast.name) ([string]$ast.ifDesc)
                    if ($cur -and [string]$cur.Status -eq 'Disabled') {
                        Enable-NetAdapter -Name $cur.Name -Confirm:$false -ErrorAction SilentlyContinue
                        Write-RepairStep ('Adapter re-enabled: ' + $cur.Name) 'Green'
                    }
                }
            } catch {}
        }

        Write-RepairStep 'Restoring adapter advanced properties (by RegistryKeyword)...'
        foreach ($ap in @($snap.advancedProps)) {
            $target = Resolve-RepairAdapter ([string]$ap.adapter) ([string]$ap.ifDesc)
            if (-not $target) { continue }
            $vals = @(([string]$ap.value) -split ',' | Where-Object { $_ -ne '' })
            if ($vals.Count -eq 0) { continue }
            try {
                Set-NetAdapterAdvancedProperty -Name $target.Name -RegistryKeyword ([string]$ap.keyword) -RegistryValue $vals -NoRestart -ErrorAction SilentlyContinue
            } catch { $failures++ }
        }

        Write-RepairStep 'Restoring adapter bindings (Properties checkboxes)...'
        foreach ($b in @($snap.bindings)) {
            $target = Resolve-RepairAdapter ([string]$b.adapter) ([string]$b.ifDesc)
            if (-not $target) { continue }
            try {
                if ($b.enabled) { Enable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -ErrorAction SilentlyContinue }
                else { Disable-NetAdapterBinding -Name $target.Name -ComponentID ([string]$b.componentId) -ErrorAction SilentlyContinue }
            } catch {}
        }

        Write-RepairStep 'Restoring interface metrics (incl. AutomaticMetric)...'
        foreach ($mi in @($snap.ipInterfaces)) {
            try {
                if ($mi.automaticMetric) {
                    Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Enabled -ErrorAction SilentlyContinue
                    Set-NetIPInterface -InterfaceAlias ([string]$mi.alias) -AddressFamily ([string]$mi.family) -AutomaticMetric Enabled -ErrorAction SilentlyContinue
                } else {
                    Set-NetIPInterface -InterfaceIndex ([int]$mi.ifIndex) -AddressFamily ([string]$mi.family) -AutomaticMetric Disabled -InterfaceMetric ([int]$mi.metric) -ErrorAction SilentlyContinue
                    Set-NetIPInterface -InterfaceAlias ([string]$mi.alias) -AddressFamily ([string]$mi.family) -AutomaticMetric Disabled -InterfaceMetric ([int]$mi.metric) -ErrorAction SilentlyContinue
                }
            } catch {}
        }

        Write-RepairStep 'Restoring RSS configuration...'
        foreach ($r in @($snap.rss)) {
            try {
                Set-NetAdapterRss -Name ([string]$r.adapter) -Enabled ([bool]$r.enabled) -ErrorAction SilentlyContinue
                if ([bool]$r.enabled) {
                    Set-NetAdapterRss -Name ([string]$r.adapter) -BaseProcessorNumber ([int]$r.baseProcessorNumber) -ErrorAction SilentlyContinue
                    if ($r.profile) { Set-NetAdapterRss -Name ([string]$r.adapter) -Profile ([string]$r.profile) -ErrorAction SilentlyContinue }
                }
            } catch {}
        }

        Write-RepairStep 'Restoring power plan values (powercfg)...'
        foreach ($p in @($snap.powercfg)) {
            try {
                if ($p.ac) {
                    $acVal = [Convert]::ToInt64((([string]$p.ac) -replace '^0x', ''), 16)
                    powercfg /setacvalueindex $p.scheme $p.sub $p.setting $acVal | Out-Null
                }
                if ($p.dc) {
                    $dcVal = [Convert]::ToInt64((([string]$p.dc) -replace '^0x', ''), 16)
                    powercfg /setdcvalueindex $p.scheme $p.sub $p.setting $dcVal | Out-Null
                }
                powercfg /setactive $p.scheme | Out-Null
            } catch {}
        }

        Write-RepairStep 'Restoring dynamic port ranges...'
        foreach ($d in @($snap.dynamicPorts)) {
            try { netsh int $($d.family) set dynamicport $($d.protocol) start=$($d.start) num=$($d.num) | Out-Null } catch {}
        }

        Write-RepairStep 'Restoring IPv6 prefix policies...'
        if (@($snap.prefixPolicies).Count -gt 0) {
            foreach ($pp in @($snap.prefixPolicies)) {
                foreach ($store in @('active', 'persistent')) {
                    try { $null = (netsh int ipv6 set prefixpolicy "$($pp.prefix)" $($pp.precedence) $($pp.label) store=$store 2>&1 | Out-String) } catch {}
                }
            }
        } else {
            foreach ($store in @('active', 'persistent')) {
                try { $null = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 35 4 store=$store 2>&1 | Out-String) } catch {}
            }
        }

        Write-RepairStep 'Restoring service start types...'
        foreach ($svc in @($snap.services)) {
            try { Set-Service -Name ([string]$svc.name) -StartupType ([string]$svc.startType) -ErrorAction SilentlyContinue } catch {}
        }

        try { netsh interface teredo set state default | Out-Null } catch {}
        try { netsh interface isatap set state default | Out-Null } catch {}
        try { netsh interface 6to4 set state default | Out-Null } catch {}

        if ($failures -eq 0) {
            Remove-Item -LiteralPath $snapshotPath -Force -ErrorAction SilentlyContinue
            Write-RepairStep 'Exact restore complete - snapshot cleared.' 'Green'
        } else {
            Write-RepairStep ("$failures value(s) could not be restored - snapshot KEPT so you can retry.") 'Yellow'
        }
    } else {
        Write-RepairStep 'No snapshot found - performing approximate Windows stock reset (fallback).' 'Yellow'

        $tcp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'
        Set-RepairDword $tcp 'DisableTaskOffload' 0
        Remove-RepairProp $tcp 'GlobalMaxTcpWindowSize'
        Remove-RepairProp $tcp 'TcpWindowSize'
        $mm = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        Set-RepairDword $mm 'SystemResponsiveness' 20
        Set-RepairDword $mm 'NetworkThrottlingIndex' 10
        Remove-RepairProp 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit'
        try { Remove-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        # DNS ServiceProvider priorities -> documented Windows defaults
        $sp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'
        Set-RepairDword $sp 'LocalPriority' 499
        Set-RepairDword $sp 'HostsPriority' 500
        Set-RepairDword $sp 'DnsPriority' 2000
        Set-RepairDword $sp 'NetbtPriority' 2001
        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' -ErrorAction SilentlyContinue | ForEach-Object {
            Remove-RepairProp $_.PSPath 'TcpAckFrequency'
            Remove-RepairProp $_.PSPath 'TCPNoDelay'
            Remove-RepairProp $_.PSPath 'TcpDelAckTicks'
        }
        netsh int tcp set global autotuninglevel=normal | Out-Null
        netsh int tcp set global rsc=enabled | Out-Null
        netsh int tcp set global rss=enabled | Out-Null
        try { netsh int tcp set heuristics enabled | Out-Null } catch {}
        try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}
        try { $null = (netsh int tcp set global timestamps=default 2>&1 | Out-String) } catch {}
        try { $null = (netsh int tcp set global fastopen=enabled 2>&1 | Out-String) } catch {}
        try { $null = (netsh int tcp set global fastopenfallback=enabled 2>&1 | Out-String) } catch {}
        try { $null = (netsh int tcp set global hystart=enabled 2>&1 | Out-String) } catch {}
        try { $null = (netsh int tcp set global pacingprofile=off 2>&1 | Out-String) } catch {}
        try { $null = (netsh int tcp set global ecncapability=default 2>&1 | Out-String) } catch {}
        if ([System.Environment]::OSVersion.Version.Build -ge 26100) {
            try { $null = (netsh int udp set global uro=enabled 2>&1 | Out-String) } catch {}
        }
        $isWin11 = ([System.Environment]::OSVersion.Version.Build -ge 22000)
        $rtoDefault = 3000
        if ($isWin11) { $rtoDefault = 1000 }
        $synDefault = 2
        if ($isWin11) { $synDefault = 4 }
        foreach ($pr in @('Internet', 'InternetCustom')) {
            try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal Normal -ErrorAction SilentlyContinue } catch {}
            try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Enabled -ErrorAction SilentlyContinue } catch {}
            try { Set-NetTCPSetting -SettingName $pr -InitialRtoMs $rtoDefault -ErrorAction SilentlyContinue } catch {}
            try { Set-NetTCPSetting -SettingName $pr -MinRtoMs 300 -ErrorAction SilentlyContinue } catch {}
            try { Set-NetTCPSetting -SettingName $pr -MaxSynRetransmissions $synDefault -ErrorAction SilentlyContinue } catch {}
        }
        foreach ($store in @('active', 'persistent')) {
            try { $null = (netsh int ipv6 set prefixpolicy ::ffff:0:0/96 35 4 store=$store 2>&1 | Out-String) } catch {}
        }
        try { Remove-RepairProp 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' 'DODownloadMode' } catch {}
        try { sc.exe config DoSvc start= delayed-auto | Out-Null } catch {}
        try { Remove-RepairProp 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS' 'EnableBITSMaxBandwidth' } catch {}
        try { Remove-RepairProp 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\NetworkConnectivityStatusIndicator' 'NoActiveProbe' } catch {}
        try {
            Remove-RepairProp 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast'
            Remove-RepairProp 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxCacheTtl'
            Remove-RepairProp 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'MaxNegativeCacheTtl'
            Remove-RepairProp 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' 'AutoDetect'
        } catch {}
        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces' -ErrorAction SilentlyContinue | ForEach-Object {
            Remove-RepairProp $_.PSPath 'NetbiosOptions'
        }
        try { netsh interface teredo set state default | Out-Null } catch {}
        try { netsh interface isatap set state default | Out-Null } catch {}
        try { netsh interface 6to4 set state default | Out-Null } catch {}
        $enable = @('ms_msclient', 'ms_server', 'ms_pacer', 'ms_tcpip', 'ms_tcpip6', 'ms_lldp', 'ms_lltdio', 'ms_rspndr')
        $disable = @('ms_implat')
        foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
            foreach ($id in $enable) {
                try { Enable-NetAdapterBinding -Name $a.Name -ComponentID $id -ErrorAction SilentlyContinue } catch {}
            }
            foreach ($id in $disable) {
                try { Disable-NetAdapterBinding -Name $a.Name -ComponentID $id -ErrorAction SilentlyContinue } catch {}
            }
            try { Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword '*LsoV2IPv4' -RegistryValue 1 -NoRestart -ErrorAction SilentlyContinue } catch {}
            try { Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword '*LsoV2IPv6' -RegistryValue 1 -NoRestart -ErrorAction SilentlyContinue } catch {}
            try { Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword '*RscIPv4' -RegistryValue 1 -NoRestart -ErrorAction SilentlyContinue } catch {}
            try { Set-NetAdapterRss -Name $a.Name -BaseProcessorNumber 0 -ErrorAction SilentlyContinue } catch {}
            foreach ($af in @('IPv4', 'IPv6')) {
                try { Set-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily $af -AutomaticMetric Enabled -ErrorAction SilentlyContinue } catch {}
            }
        }
        Write-RepairStep 'Stock reset complete.' 'Green'
    }

    # ALWAYS: re-enable every disabled physical adapter (Wi-Fi Exo disabled and any other NIC).
    Write-RepairStep 'Re-enabling any disabled physical network adapters...'
    foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
        try {
            if ([string]$a.Status -eq 'Disabled') {
                Enable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction SilentlyContinue
                Write-RepairStep ('Adapter re-enabled: ' + $a.Name) 'Green'
            }
        } catch {}
    }

    # Clear Exo network state (apply marker + saved preset). Snapshot handled above.
    Remove-Item -LiteralPath $applyStatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $optimizerStatePath -Force -ErrorAction SilentlyContinue
    try { Clear-DnsClientCache -ErrorAction SilentlyContinue } catch {}

    Write-RepairStep 'Exo Internet repair finished.' 'Green'
    if ($failures -gt 0) { return 1 }
    return 0
}

# ---------------------------------------------------------------------------
# Self-elevating bootstrap (works from a file and from irm | iex).
# ---------------------------------------------------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $identity
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    $code = [int](@(Invoke-ExoInternetRepair) | Select-Object -Last 1)
    exit $code
}

Write-RepairStep 'Administrator rights required - requesting elevation...' 'Yellow'
$selfPath = $PSCommandPath
$tmp = $null
$exitCode = 1
try {
    if ([string]::IsNullOrWhiteSpace($selfPath)) {
        # Piped via Invoke-Expression: persist current definition to a temp file.
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('Exo-Repair-Internet-' + [guid]::NewGuid().ToString('N') + '.ps1')
        $selfText = $null
        try { $selfText = $MyInvocation.MyCommand.ScriptBlock.ToString() } catch { $selfText = $null }
        if ([string]::IsNullOrWhiteSpace($selfText)) {
            # Last resort: download the canonical script from the repo.
            $url = 'https://raw.githubusercontent.com/ImAvgErix/Exo/main/Repair-Internet.ps1'
            Write-RepairStep 'Downloading Exo Internet repair script...'
            Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -TimeoutSec 60 -Headers @{ 'User-Agent' = 'Exo-Repair/1.0' }
            $downloaded = Get-Content -LiteralPath $tmp -Raw -Encoding UTF8
            if ($downloaded.Length -lt 3000 -or $downloaded -notmatch 'function\s+Invoke-ExoInternetRepair') {
                throw 'Downloaded repair script failed validation.'
            }
        } else {
            Set-Content -LiteralPath $tmp -Value $selfText -Encoding ASCII
        }
        $selfPath = $tmp
    }
    $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $selfPath + '"')
    )
    $exitCode = [int]$proc.ExitCode
} catch {
    Write-Host ('[-] Internet repair could not start: ' + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

exit $exitCode
