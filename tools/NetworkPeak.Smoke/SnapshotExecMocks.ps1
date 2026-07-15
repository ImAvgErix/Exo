# Windows-shaped cmdlet/native mocks for executing the generated network
# apply-snapshot and repair scripts on any OS (dot-source before the script
# under test). Registry mocks return MIXED value types on purpose:
# Int32 DWord (incl. 0xffffffff -> -1), Int64 QWord, String, ExpandString,
# String[] MultiString and Byte[] Binary - the shapes that broke snapshot
# serialization on real Windows ('Argument types do not match').
# Mutating cmdlets append structured lines to $env:EXO_TEST_CAPTURE so the
# harness can assert the repair path writes back correctly-typed values.

function Add-ExoCapture([string]$line) {
  if ($env:EXO_TEST_CAPTURE) { Add-Content -LiteralPath $env:EXO_TEST_CAPTURE -Value $line }
}

function New-MockRegKey {
  param([string]$PsPath, [hashtable]$Values, [hashtable]$Kinds)
  $o = [pscustomobject]@{ PSPath = $PsPath; MockValues = $Values; MockKinds = $Kinds }
  $o | Add-Member -MemberType ScriptMethod -Name GetValueNames -Value { @($this.MockValues.Keys) } -Force
  $o | Add-Member -MemberType ScriptMethod -Name GetValueKind -Value { param($n) $this.MockKinds[$n] } -Force
  $o | Add-Member -MemberType ScriptMethod -Name GetValue -Value {
    param($n, $default = $null, $options = $null)
    if ($this.MockValues.Contains($n)) { return $this.MockValues[$n] } else { return $default }
  } -Force
  return $o
}

$script:MockRegistry = @{}

$script:MockRegistry['HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters'] = New-MockRegKey `
  -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters' `
  -Values @{
    'DisableTaskOffload'     = [int]0
    'GlobalMaxTcpWindowSize' = [long]65535
    'TcpWindowSize'          = [string[]]@('64240', '131072')
    'EnableTCPA'             = [byte[]](1, 2, 3, 4)
    'EnableDCA'              = '%SystemRoot%\dca'
  } `
  -Kinds @{
    'DisableTaskOffload'     = [Microsoft.Win32.RegistryValueKind]::DWord
    'GlobalMaxTcpWindowSize' = [Microsoft.Win32.RegistryValueKind]::QWord
    'TcpWindowSize'          = [Microsoft.Win32.RegistryValueKind]::MultiString
    'EnableTCPA'             = [Microsoft.Win32.RegistryValueKind]::Binary
    'EnableDCA'              = [Microsoft.Win32.RegistryValueKind]::ExpandString
  }

# Key exists but the priority values do not -> snapshot records kind 'absent'
# and repair must REMOVE them (the removal branch of the restore).
$script:MockRegistry['HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'] = New-MockRegKey `
  -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider' `
  -Values @{ 'Class' = [int]8 } -Kinds @{ 'Class' = [Microsoft.Win32.RegistryValueKind]::DWord }

# 0xffffffff round-trips as Int32 -1 on real Windows
$script:MockRegistry['HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'] = New-MockRegKey `
  -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' `
  -Values @{ 'SystemResponsiveness' = [int]20; 'NetworkThrottlingIndex' = [int]-1 } `
  -Kinds  @{ 'SystemResponsiveness' = [Microsoft.Win32.RegistryValueKind]::DWord; 'NetworkThrottlingIndex' = [Microsoft.Win32.RegistryValueKind]::DWord }

$script:MockRegistry['HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'] = New-MockRegKey `
  -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games' `
  -Values @{ 'GPU Priority' = [int]2; 'Scheduling Category' = 'Medium'; 'SFIO Priority' = 'Normal' } `
  -Kinds  @{ 'GPU Priority' = [Microsoft.Win32.RegistryValueKind]::DWord; 'Scheduling Category' = [Microsoft.Win32.RegistryValueKind]::String; 'SFIO Priority' = [Microsoft.Win32.RegistryValueKind]::String }

$script:MockNagleKeys = @(
  (New-MockRegKey -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{mock-guid-1}' `
    -Values @{ 'TcpAckFrequency' = [int]1 } -Kinds @{ 'TcpAckFrequency' = [Microsoft.Win32.RegistryValueKind]::DWord }),
  (New-MockRegKey -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{mock-guid-2}' `
    -Values @{} -Kinds @{})
)
$script:MockNetbtKeys = @(
  (New-MockRegKey -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_{mock-guid-1}' `
    -Values @{ 'NetbiosOptions' = [int]0 } -Kinds @{ 'NetbiosOptions' = [Microsoft.Win32.RegistryValueKind]::DWord })
)
$script:MockClassKeys = @(
  (New-MockRegKey -PsPath 'Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001' `
    -Values @{ 'PnPCapabilities' = [int]24; 'DriverDesc' = 'Mock Ethernet Adapter' } `
    -Kinds  @{ 'PnPCapabilities' = [Microsoft.Win32.RegistryValueKind]::DWord; 'DriverDesc' = [Microsoft.Win32.RegistryValueKind]::String })
)

function Test-Path {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  if ($p -match '^(HKLM|HKCU):') { return [bool]$script:MockRegistry.ContainsKey($p) }
  if ($LiteralPath) { return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath }
  return Microsoft.PowerShell.Management\Test-Path -Path $Path
}
function Get-Item {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  if ($p -match '^(HKLM|HKCU):') { return $script:MockRegistry[$p] }
  if ($p -match 'Registry::') {
    foreach ($k in ($script:MockClassKeys + $script:MockNagleKeys + $script:MockNetbtKeys)) {
      if ($k.PSPath -eq $p) { return $k }
    }
    return $null
  }
  if ($LiteralPath) { return Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath }
  return Microsoft.PowerShell.Management\Get-Item -Path $Path
}
function Get-ChildItem {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [switch]$Recurse, [string]$Filter)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  if ($p -like '*Tcpip\Parameters\Interfaces*') { return $script:MockNagleKeys }
  if ($p -like '*NetBT\Parameters\Interfaces*') { return $script:MockNetbtKeys }
  if ($p -like '*4d36e972*') { return $script:MockClassKeys }
  if ($p -match '^(HKLM|HKCU):') { return @() }
  return Microsoft.PowerShell.Management\Get-ChildItem @PSBoundParameters
}
function Get-ItemProperty {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [string]$Name)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  foreach ($k in $script:MockClassKeys) {
    if ($k.PSPath -eq $p) { return [pscustomobject]@{ DriverDesc = $k.MockValues['DriverDesc']; PnPCapabilities = $k.MockValues['PnPCapabilities'] } }
  }
  if ($p -match '^(HKLM|HKCU):|Registry::') { return $null }
  return Microsoft.PowerShell.Management\Get-ItemProperty @PSBoundParameters
}
function New-Item {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$ItemType, [switch]$Force, [string]$Name)
  if ($Path -match '^(HKLM|HKCU):') { Add-ExoCapture ("NEWKEY|" + $Path); return $null }
  return Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
function Remove-Item {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [switch]$Recurse, [switch]$Force)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  if ($p -match '^(HKLM|HKCU):') { Add-ExoCapture ("DELKEY|" + $p); return }
  return Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Set-ItemProperty {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [string]$Name, $Value, [string]$Type, [switch]$Force)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  $vt = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName }
  $vr = if ($Value -is [System.Array]) { (@($Value) -join ',') } else { [string]$Value }
  Add-ExoCapture ("SET|" + $p + "|" + $Name + "|" + $Type + "|" + $vt + "|" + $vr)
}
function New-ItemProperty {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [string]$Name, $Value, [string]$PropertyType, [switch]$Force)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  $vt = if ($null -eq $Value) { 'null' } else { $Value.GetType().FullName }
  $vr = if ($Value -is [System.Array]) { (@($Value) -join ',') } else { [string]$Value }
  Add-ExoCapture ("SET|" + $p + "|" + $Name + "|" + $PropertyType + "|" + $vt + "|" + $vr)
}
function Remove-ItemProperty {
  [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$LiteralPath, [string]$Name, [switch]$Force)
  $p = if ($LiteralPath) { $LiteralPath } else { $Path }
  Add-ExoCapture ("DEL|" + $p + "|" + $Name)
}

function netsh {
  $joined = ($args -join ' ')
  if ($joined -match 'tcp show global') {
    return @'
TCP Global Parameters
----------------------------------------------
Receive-Side Scaling State          : enabled
Receive Window Auto-Tuning Level    : normal
Add-On Congestion Control Provider  : default
ECN Capability                      : disabled
RFC 1323 Timestamps                 : allowed
Initial RTO                         : 1000
Receive Segment Coalescing State    : enabled
Non Sack Rtt Resiliency             : disabled
Max SYN Retransmissions             : 4
Fast Open                           : enabled
Fast Open Fallback                  : enabled
HyStart                             : enabled
Pacing Profile                      : off
'@ -split "`n"
  }
  if ($joined -match 'tcp show heuristics') { return @('TCP Window Scaling heuristics Parameters', '----------', 'Window Scaling heuristics         : disabled') }
  if ($joined -match 'udp show global') { return @('UDP Global Parameters', '----------', 'Receive Offload State    : enabled') }
  if ($joined -match 'show dynamicport') { return @('Protocol tcp Dynamic Port Range', '---------------------------------', 'Start Port      : 49152', 'Number of Ports : 16384') }
  if ($joined -match 'show prefixpolicies') {
    return @'
Precedence  Label  Prefix
----------  -----  --------------------------------
        50      0  ::1/128
        40      1  ::/0
        35      4  ::ffff:0:0/96
        30      2  2002::/16
         5      5  2001::/32
         3     13  fc00::/7
         1     11  fec0::/10
         1     12  3ffe::/16
         1      3  ::/96
'@ -split "`n"
  }
  if ($joined -match '\bset\b') { Add-ExoCapture ("NETSH|" + $joined) }
  return @('Ok.')
}
function powercfg {
  $joined = ($args -join ' ')
  if ($joined -like '*getactivescheme*') { return 'Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)' }
  if ($joined -match 'setacvalueindex|setdcvalueindex|setactive') { Add-ExoCapture ("POWERCFG|" + $joined); return @('') }
  return @(
    'Power Setting GUID: 12bbebe6-58d6-4636-95bb-3217ef867c1a  (Power Saving Mode)',
    '  Current AC Power Setting Index: 0x00000000',
    '  Current DC Power Setting Index: 0x00000002'
  )
}

function Get-NetOffloadGlobalSetting {
  [CmdletBinding()] param()
  [pscustomobject]@{ ReceiveSegmentCoalescing = 'Enabled'; ReceiveSideScaling = 'Enabled'; TaskOffload = 'Enabled' }
}
function Get-NetTCPSetting {
  [CmdletBinding()] param([string]$SettingName)
  @(
    # 'Automatic' template on real Windows carries null-valued fields
    [pscustomobject]@{ SettingName = 'Automatic'; CongestionProvider = $null; AutoTuningLevelLocal = $null; EcnCapability = $null; Timestamps = $null; InitialRtoMs = $null; MinRtoMs = $null; MaxSynRetransmissions = $null; NonSackRttResiliency = $null },
    [pscustomobject]@{ SettingName = 'InternetCustom'; CongestionProvider = 'CUBIC'; AutoTuningLevelLocal = 'Normal'; EcnCapability = 'Disabled'; Timestamps = 'Disabled'; InitialRtoMs = [uint32]1000; MinRtoMs = [uint32]300; MaxSynRetransmissions = [byte]4; NonSackRttResiliency = 'Disabled' },
    [pscustomobject]@{ SettingName = 'Internet'; CongestionProvider = 'CUBIC'; AutoTuningLevelLocal = 'Normal'; EcnCapability = 'Disabled'; Timestamps = 'Disabled'; InitialRtoMs = [uint32]1000; MinRtoMs = [uint32]300; MaxSynRetransmissions = [byte]4; NonSackRttResiliency = 'Disabled' }
  )
}
function Set-NetTCPSetting { [CmdletBinding()] param([string]$SettingName, $CongestionProvider, $AutoTuningLevelLocal, $ScalingHeuristics, $InitialRtoMs, $MinRtoMs, $MaxSynRetransmissions, $NonSackRttResiliency, $Timestamps, $EcnCapability)
  Add-ExoCapture ("TCPSET|" + $SettingName) }
function Get-NetAdapter {
  [CmdletBinding()] param([string]$Name, [switch]$Physical)
  @(
    [pscustomobject]@{ Name = 'Ethernet'; InterfaceDescription = 'Mock Ethernet Adapter'; Status = 'Up'; Virtual = $false; PhysicalMediaType = '802.3'; MediaType = '802.3'; ifIndex = 5 },
    [pscustomobject]@{ Name = 'Wi-Fi'; InterfaceDescription = 'Mock Wireless AX211 802.11ax'; Status = 'Disabled'; Virtual = $false; PhysicalMediaType = 'Native 802.11'; MediaType = 'Native 802.11'; ifIndex = 12 }
  ) | Where-Object { -not $Name -or $_.Name -eq $Name }
}
function Get-NetAdapterAdvancedProperty {
  [CmdletBinding()] param([string]$Name)
  @(
    # RegistryValue is String[] on real Windows (often single-element)
    [pscustomobject]@{ RegistryKeyword = '*FlowControl'; RegistryValue = [string[]]@('3') },
    [pscustomobject]@{ RegistryKeyword = '*InterruptModeration'; RegistryValue = [string[]]@('1') },
    [pscustomobject]@{ RegistryKeyword = '*ReceiveBuffers'; RegistryValue = [string[]]@('256', '512') },
    [pscustomobject]@{ RegistryKeyword = $null; RegistryValue = [string[]]@('ignored') },
    [pscustomobject]@{ RegistryKeyword = 'NetworkAddress'; RegistryValue = [string[]]@() }
  )
}
function Set-NetAdapterAdvancedProperty { [CmdletBinding()] param([string]$Name, [string]$RegistryKeyword, $RegistryValue, [string]$DisplayName, $DisplayValue, [switch]$NoRestart)
  Add-ExoCapture ("ADV|" + $Name + "|" + $RegistryKeyword + "|" + (@($RegistryValue) -join ';')) }
function Get-NetAdapterBinding {
  [CmdletBinding()] param([string]$Name)
  @(
    [pscustomobject]@{ ComponentID = 'ms_tcpip'; Enabled = $true },
    [pscustomobject]@{ ComponentID = 'ms_lldp'; Enabled = $false }
  )
}
function Enable-NetAdapterBinding { [CmdletBinding()] param([string]$Name, [string]$ComponentID) Add-ExoCapture ("BINDON|" + $Name + "|" + $ComponentID) }
function Disable-NetAdapterBinding { [CmdletBinding()] param([string]$Name, [string]$ComponentID) Add-ExoCapture ("BINDOFF|" + $Name + "|" + $ComponentID) }
function Enable-NetAdapter { [CmdletBinding()] param([string]$Name, [switch]$Confirm) Add-ExoCapture ("NICON|" + $Name) }
function Disable-NetAdapter { [CmdletBinding()] param([string]$Name, [switch]$Confirm) Add-ExoCapture ("NICOFF|" + $Name) }
function Get-NetIPInterface {
  [CmdletBinding()] param()
  @(
    [pscustomobject]@{ ifIndex = 5; InterfaceAlias = 'Ethernet'; AddressFamily = 'IPv4'; InterfaceMetric = [uint32]25; AutomaticMetric = 'Enabled' },
    [pscustomobject]@{ ifIndex = 5; InterfaceAlias = 'Ethernet'; AddressFamily = 'IPv6'; InterfaceMetric = [uint32]25; AutomaticMetric = 'Enabled' },
    [pscustomobject]@{ ifIndex = 12; InterfaceAlias = 'Wi-Fi'; AddressFamily = 'IPv4'; InterfaceMetric = [uint32]40; AutomaticMetric = 'Disabled' }
  )
}
function Set-NetIPInterface { [CmdletBinding()] param($InterfaceIndex, [string]$InterfaceAlias, [string]$AddressFamily, [string]$AutomaticMetric, $InterfaceMetric)
  Add-ExoCapture ("METRIC|" + $InterfaceIndex + "|" + $InterfaceAlias + "|" + $AddressFamily + "|" + $AutomaticMetric + "|" + $InterfaceMetric) }
function Get-NetAdapterRss {
  [CmdletBinding()] param([string]$Name)
  [pscustomobject]@{ Enabled = $true; BaseProcessorNumber = [uint16]0; Profile = 'NUMAStatic' }
}
function Set-NetAdapterRss { [CmdletBinding()] param([string]$Name, $Enabled, $BaseProcessorNumber, [string]$Profile)
  Add-ExoCapture ("RSS|" + $Name + "|" + $Enabled + "|" + $BaseProcessorNumber + "|" + $Profile) }
function Get-Service {
  [CmdletBinding()] param([string]$Name)
  if ($Name -eq 'DoSvc') { return [pscustomobject]@{ Name = 'DoSvc'; StartType = 'Automatic'; Status = 'Running' } }
  return $null
}
function Set-Service { [CmdletBinding()] param([string]$Name, [string]$StartupType) Add-ExoCapture ("SVC|" + $Name + "|" + $StartupType) }
function Clear-DnsClientCache { [CmdletBinding()] param() }
function Restart-NetAdapter { [CmdletBinding()] param([string]$Name, [switch]$Confirm) Add-ExoCapture ("NICRESTART|" + $Name) }
