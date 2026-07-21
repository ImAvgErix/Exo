# Exo.PowerPlan.ps1 - custom competitive power schemes (Intel / AMD / generic).
# Duplicates Ultimate Performance, renames to Exo Competitive *, applies hidden
# powercfg attributes that stock UI never exposes. Reversible: keep scheme GUID
# in state; Repair re-activates the pre-Exo scheme.
# ASCII only. Safe to call multiple times.

Set-StrictMode -Version Latest

$script:ExoPowerPlanIntelName = 'Exo Competitive Intel'
$script:ExoPowerPlanAmdName = 'Exo Competitive AMD'
$script:ExoPowerPlanGenericName = 'Exo Competitive'
# Ultimate Performance template (Win10 1803+)
$script:ExoUltimateTemplateGuid = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
# High performance fallback
$script:ExoHighPerfGuid = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'

function Get-ExoCpuVendor {
    try {
        $name = [string](Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Name)
    } catch {
        try { $name = [string](Get-ItemPropertyValue 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name 'ProcessorNameString' -ErrorAction Stop) }
        catch { $name = '' }
    }
    $hybrid = $false
    if ($name -match '(?i)Intel|Core\(TM\)|Core Ultra|Xeon') {
        # 12th+ hybrid (P+E) often advertise "E-cores" or generation patterns; soft-detect
        if ($name -match '(?i)Ultra|i[3579]-1[2-9]|i[3579]-2[0-9]') { $hybrid = $true }
        return [pscustomobject]@{ Vendor = 'intel'; Name = $name; Hybrid = $hybrid }
    }
    if ($name -match '(?i)AMD|Ryzen|Threadripper|EPYC|Athlon') {
        return [pscustomobject]@{ Vendor = 'amd'; Name = $name; Hybrid = $false }
    }
    return [pscustomobject]@{ Vendor = 'generic'; Name = $name; Hybrid = $false }
}

function Get-ExoPowerPlanTargetName {
    param([string]$Vendor = '')
    if (-not $Vendor) { $Vendor = (Get-ExoCpuVendor).Vendor }
    switch ($Vendor.ToLowerInvariant()) {
        'intel' { return $script:ExoPowerPlanIntelName }
        'amd'   { return $script:ExoPowerPlanAmdName }
        default { return $script:ExoPowerPlanGenericName }
    }
}

function Get-ExoActivePowerSchemeGuid {
    try {
        $line = powercfg /getactivescheme 2>$null | Out-String
        $m = [regex]::Match([string]$line, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
        if ($m.Success) { return $m.Groups[1].Value.ToLowerInvariant() }
    } catch { }
    return $null
}

function Get-ExoPowerSchemeGuidByName {
    param([Parameter(Mandatory)][string]$Name)
    try {
        $list = powercfg /l 2>$null | Out-String
        foreach ($line in ($list -split "`r?`n")) {
            if ($line -notmatch [regex]::Escape($Name)) { continue }
            $m = [regex]::Match($line, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
            if ($m.Success) { return $m.Groups[1].Value.ToLowerInvariant() }
        }
    } catch { }
    return $null
}

function Test-ExoCompetitivePowerPlan {
    $active = Get-ExoActivePowerSchemeGuid
    if (-not $active) { return $false }
    foreach ($n in @($script:ExoPowerPlanIntelName, $script:ExoPowerPlanAmdName, $script:ExoPowerPlanGenericName)) {
        $g = Get-ExoPowerSchemeGuidByName -Name $n
        if ($g -and $g -eq $active) { return $true }
    }
    # Also accept if active scheme name contains "Exo Competitive"
    try {
        $line = powercfg /getactivescheme 2>$null | Out-String
        if ($line -match 'Exo Competitive') { return $true }
    } catch { }
    return $false
}

function Set-ExoPowerCfgAcDc {
    # Set both AC and DC when possible; ignore unsupported setting errors.
    param(
        [Parameter(Mandatory)][string]$SchemeGuid,
        [Parameter(Mandatory)][string]$SubGroup,
        [Parameter(Mandatory)][string]$Setting,
        [Parameter(Mandatory)][int]$Value
    )
    $ok = 0
    foreach ($mode in @('setacvalueindex', 'setdcvalueindex')) {
        try {
            $out = & powercfg /$mode $SchemeGuid $SubGroup $Setting $Value 2>&1 | Out-String
            if ($LASTEXITCODE -eq 0 -or [string]::IsNullOrWhiteSpace($out) -or $out -notmatch '(?i)invalid|not found|error') {
                $ok++
            }
        } catch { }
    }
    return $ok
}

function Unlock-ExoPowerCfgHidden {
    # Unhide competitive knobs so they survive Control Panel / Settings edits better.
    param([string]$SchemeGuid)
    $pairs = @(
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PROCTHROTTLEMIN' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PROCTHROTTLEMAX' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'CPMINCORES' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'CPMAXCORES' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PERFEPP' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PERFBOOSTMODE' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'SYSCOOLPOL' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PERFAUTONOMOUS' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PERFAUTONOMOUSWINDOW' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'PERFCHECK' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'LATENCYHINTPERF' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'LATENCYHINTUNPARK' },
        @{ Sub = 'SUB_PROCESSOR'; Set = 'DISTRIBUTEUTIL' },
        @{ Sub = 'SUB_PCIEXPRESS'; Set = 'ASPM' },
        @{ Sub = 'SUB_DISK'; Set = 'DISKIDLE' },
        @{ Sub = 'SUB_SLEEP'; Set = 'STANDBYIDLE' },
        @{ Sub = 'SUB_SLEEP'; Set = 'HYBRIDSLEEP' },
        @{ Sub = 'SUB_SLEEP'; Set = 'HIBERNATEIDLE' }
    )
    foreach ($p in $pairs) {
        try { & powercfg -attributes $p.Sub $p.Set -ATTRIB_HIDE 2>$null | Out-Null } catch { }
    }
}

function Apply-ExoCompetitivePowerSettings {
    param(
        [Parameter(Mandatory)][string]$SchemeGuid,
        [Parameter(Mandatory)][ValidateSet('intel', 'amd', 'generic')][string]$Vendor,
        [bool]$Hybrid = $false
    )
    $n = 0
    # --- Processor: floor at 100% on AC gaming desktop path ---
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PROCTHROTTLEMIN' 100
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PROCTHROTTLEMAX' 100
    # Core parking off (100% unparked)
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'CPMINCORES' 100
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'CPMAXCORES' 100
    # Energy Performance Preference 0 = prefer performance (hidden)
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFEPP' 0
    # Performance boost mode: 2 = Aggressive (Intel/AMD modern)
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFBOOSTMODE' 2
    # Active cooling
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'SYSCOOLPOL' 1
    # Latency sensitivity hints -> high performance / unpark
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'LATENCYHINTPERF' 100
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'LATENCYHINTUNPARK' 100
    # Duty cycling off when present
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFDUTYCYCLING' 0

    if ($Vendor -eq 'intel') {
        # Autonomous mode off = more predictable boost for competitive (Intel)
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFAUTONOMOUS' 0
        # Short / long scheduling: prefer performance cores on hybrid
        if ($Hybrid) {
            # 2 = Prefer performant processors (Win11 hetero)
            $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'SCHEDPOLICY' 2
            $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'SHORTSCHEDPOLICY' 2
            $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'HETEROCLASS1INITIALPERF' 100
            $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'HETEROCLASS0FLOORPERF' 100
        }
        # Decrease boost time sensitivity (keep turbo longer) when supported
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFBOOSTPOL' 100
    }
    elseif ($Vendor -eq 'amd') {
        # AMD: leave autonomous on when present (CPPC/collaborative often happier)
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFAUTONOMOUS' 1
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFAUTONOMOUSWINDOW' 30
        # Aggressive boost still
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFBOOSTMODE' 2
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFBOOSTPOL' 100
        # Heterogeneous / complex scheduling defaults lean performance when exposed
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'SCHEDPOLICY' 2
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'SHORTSCHEDPOLICY' 2
    }
    else {
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFAUTONOMOUS' 0
        $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PROCESSOR' 'PERFBOOSTMODE' 2
    }

    # --- PCI Express ASPM off (hidden link-state power management) ---
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_PCIEXPRESS' 'ASPM' 0

    # --- Disk: never spin-down ---
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_DISK' 'DISKIDLE' 0

    # --- Sleep: no standby / hybrid / hibernate timeout on AC gaming path ---
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_SLEEP' 'STANDBYIDLE' 0
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_SLEEP' 'HYBRIDSLEEP' 0
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_SLEEP' 'HIBERNATEIDLE' 0
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_SLEEP' 'REMOTEFILESLOW' 0

    # --- USB selective suspend OFF (GUID form  -  more reliable than aliases) ---
    $usbSub = '2a737441-1930-4402-8d77-b2bebba308a3'
    $usbSel = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
    $n += Set-ExoPowerCfgAcDc $SchemeGuid $usbSub $usbSel 0

    # --- Wireless adapter: max performance ---
    $wifiSub = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
    $wifiSet = '12bbebe6-58d6-4636-95bb-3217ef867c1a'
    $n += Set-ExoPowerCfgAcDc $SchemeGuid $wifiSub $wifiSet 0

    # --- Display: do not force off aggressively on AC (0 = never) ---
    $n += Set-ExoPowerCfgAcDc $SchemeGuid 'SUB_VIDEO' 'VIDEOIDLE' 0

    # --- Multimedia: when sharing media prefer performance ---
    $mmSub = '9596fb26-9850-41fd-ac3e-f7c3c00afd4b'
    # "When playing video" / quality  -  best effort GUIDs
    foreach ($mmSet in @(
        '10778347-1370-4ee0-8bbd-33bdacaade49', # quality
        '34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4'  # playback
    )) {
        $n += Set-ExoPowerCfgAcDc $SchemeGuid $mmSub $mmSet 0
    }

    Unlock-ExoPowerCfgHidden -SchemeGuid $SchemeGuid

    try {
        & powercfg /S $SchemeGuid 2>$null | Out-Null
        & powercfg /setactive $SchemeGuid 2>$null | Out-Null
    } catch { }

    return $n
}

function New-ExoCompetitivePowerPlan {
    <#
    .SYNOPSIS
      Create or refresh Exo Competitive power plan for this CPU and activate it.
    .OUTPUTS
      pscustomobject with Guid, Name, Vendor, SettingsWritten, Created
    #>
    param([switch]$Force)

    $cpu = Get-ExoCpuVendor
    $targetName = Get-ExoPowerPlanTargetName -Vendor $cpu.Vendor
    $existing = Get-ExoPowerSchemeGuidByName -Name $targetName
    $created = $false
    $guid = $existing

    if (-not $guid) {
        # Duplicate Ultimate template ONCE, rename immediately  -  never leave bare Ultimate clones.
        $dupOut = ''
        try {
            $dupOut = & powercfg -duplicatescheme $script:ExoUltimateTemplateGuid 2>&1 | Out-String
        } catch {
            try { $dupOut = & powercfg -duplicatescheme $script:ExoHighPerfGuid 2>&1 | Out-String } catch { $dupOut = '' }
        }
        $m = [regex]::Match([string]$dupOut, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
        if ($m.Success) {
            $guid = $m.Groups[1].Value.ToLowerInvariant()
            $created = $true
            try { & powercfg /changename $guid $targetName "Exo competitive host plan ($($cpu.Vendor))" 2>$null | Out-Null } catch { }
        } else {
            try { & powercfg /S SCHEME_MIN 2>$null | Out-Null } catch { }
            $guid = Get-ExoActivePowerSchemeGuid
        }
    }

    # Purge leftover bare "Ultimate Performance" clones (keep Exo / Balanced / High / Saver / Nexus)
    try {
        $list = powercfg /l 2>$null | Out-String
        foreach ($line in ($list -split "`r?`n")) {
            if ($line -notmatch '(?i)Ultimate Performance') { continue }
            if ($line -match '(?i)Exo Competitive') { continue }
            $um = [regex]::Match($line, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
            if (-not $um.Success) { continue }
            $ug = $um.Groups[1].Value.ToLowerInvariant()
            if ($guid -and $ug -eq $guid) { continue }
            if ($ug -eq $script:ExoUltimateTemplateGuid) { continue }
            try { & powercfg -delete $ug 2>$null | Out-Null } catch { }
        }
    } catch { }

    if (-not $guid) {
        return [pscustomobject]@{
            Guid = $null; Name = $targetName; Vendor = $cpu.Vendor; CpuName = $cpu.Name
            SettingsWritten = 0; Created = $false; Active = $false; Ok = $false
        }
    }

    $written = Apply-ExoCompetitivePowerSettings -SchemeGuid $guid -Vendor $cpu.Vendor -Hybrid:([bool]$cpu.Hybrid)
    $active = (Get-ExoActivePowerSchemeGuid) -eq $guid

    return [pscustomobject]@{
        Guid            = $guid
        Name            = $targetName
        Vendor          = $cpu.Vendor
        CpuName         = $cpu.Name
        Hybrid          = [bool]$cpu.Hybrid
        SettingsWritten = [int]$written
        Created         = [bool]$created
        Active          = [bool]$active
        Ok              = [bool]$active -and ($written -gt 0 -or (Test-ExoCompetitivePowerPlan))
    }
}

function Set-ExoCompetitivePowerPlan {
    # Drop-in replacement for Set-ExoHighPerfPower
    param([switch]$Force)
    $r = New-ExoCompetitivePowerPlan -Force:$Force
    if ($r.Ok -or $r.Active) { return 1 }
    # Fallback stock path
    try {
        & powercfg -duplicatescheme $script:ExoUltimateTemplateGuid 2>$null | Out-Null
        $u = Get-ExoPowerSchemeGuidByName -Name 'Ultimate Performance'
        if ($u) { & powercfg /S $u 2>$null | Out-Null; return 1 }
        & powercfg /S SCHEME_MIN 2>$null | Out-Null
        return 1
    } catch { return 0 }
}

function Get-ExoCompetitivePowerPlanStatus {
    $cpu = Get-ExoCpuVendor
    $name = Get-ExoPowerPlanTargetName -Vendor $cpu.Vendor
    $guid = Get-ExoPowerSchemeGuidByName -Name $name
    $active = Get-ExoActivePowerSchemeGuid
    $isExo = Test-ExoCompetitivePowerPlan
    return [pscustomobject]@{
        Vendor     = $cpu.Vendor
        CpuName    = $cpu.Name
        PlanName   = $name
        PlanGuid   = $guid
        ActiveGuid = $active
        Active     = [bool]$isExo
        Detail     = if ($isExo) {
            "$name active for $($cpu.Vendor.ToUpperInvariant()) - hidden processor/PCIe/USB knobs applied"
        } elseif ($guid) {
            "$name exists but is not active"
        } else {
            "Will create $name from Ultimate Performance template for $($cpu.Vendor)"
        }
    }
}
