# Real-execution regression gate for the generated network scripts.
# Phase 1: extracts Save-ExoNetworkSnapshot from the generated apply script and
#          EXECUTES it against Windows-shaped mocks with mixed-type registry
#          values (Int32/Int64/String/ExpandString/String[]/Byte[]), asserting
#          success + a valid, type-faithful JSON round trip. This is the exact
#          path that failed on real Windows with 'Argument types do not match'.
# Phase 2: executes the FULL generated repair script (child pwsh, same mocks)
#          against the phase-1 snapshot and asserts the registry writes carry
#          correctly-typed values (byte[] Binary, string[] MultiString, DWord
#          incl. 0xffffffff/-1, QWord, ExpandString) and absent values are removed.
# Output: EXOTEST:<name>|pass or EXOTEST:<name>|fail:<detail> lines; exit 1 on any fail.
param(
  [Parameter(Mandatory)] [string]$ApplyScriptPath,
  [Parameter(Mandatory)] [string]$RepairScriptPath,
  [Parameter(Mandatory)] [string]$MocksPath,
  [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('exo-snapexec-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Continue'
$script:failCount = 0
function Assert([string]$name, [bool]$cond, [string]$detail = '') {
  if ($cond) { Write-Output ("EXOTEST:" + $name + "|pass") }
  else {
    $script:failCount++
    $d = ($detail -replace '[\r\n\|]', ' ')
    Write-Output ("EXOTEST:" + $name + "|fail:" + $d)
  }
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
$env:LOCALAPPDATA = $WorkDir
if (-not $env:TEMP) { $env:TEMP = $WorkDir }
$captureFile = Join-Path $WorkDir 'capture.log'
Set-Content -LiteralPath $captureFile -Value '' -Encoding UTF8
$env:EXO_TEST_CAPTURE = $captureFile
$snapshotFile = Join-Path $WorkDir 'Exo/network-snapshot.json'

# ---------------------------------------------------------------------------
# Phase 1 - snapshot capture executes without throwing and round-trips JSON
# ---------------------------------------------------------------------------
$full = Get-Content -LiteralPath $ApplyScriptPath -Raw
$cut = $full.IndexOf('$snapshotOk = Save-ExoNetworkSnapshot')
Assert 'snapshot-exec call marker present' ($cut -ge 0)
if ($cut -lt 0) { exit 1 }

$prefixPath = Join-Path $WorkDir 'apply-prefix.ps1'
Set-Content -LiteralPath $prefixPath -Value $full.Substring(0, $cut) -Encoding UTF8

. $MocksPath
. $prefixPath

$snapOk = $false
$snapErr = ''
try { $snapOk = Save-ExoNetworkSnapshot } catch { $snapErr = $_.Exception.Message }
if (-not $snapOk -and -not $snapErr -and $Error.Count -gt 0) { $snapErr = $Error[0].Exception.Message }
Assert 'snapshot-exec succeeds (mixed-type registry values)' ($snapOk -eq $true) $snapErr
Assert 'snapshot-exec file written' (Microsoft.PowerShell.Management\Test-Path -LiteralPath $snapshotFile)

$parsed = $null
try { $parsed = Get-Content -LiteralPath $snapshotFile -Raw | ConvertFrom-Json } catch {}
Assert 'snapshot-exec JSON parses' ($null -ne $parsed)
if ($null -ne $parsed) {
  Assert 'snapshot-exec version + timestamp' ($parsed.snapshotVersion -eq 1 -and [string]$parsed.timestampUtc)

  function Find-Reg([string]$name) { @($parsed.regValues) | Where-Object { [string]$_.name -eq $name } | Select-Object -First 1 }
  $multi = Find-Reg 'TcpWindowSize'
  Assert 'snapshot-exec MultiString stays array' `
    ($multi -and [string]$multi.kind -eq 'MultiString' -and @($multi.value).Count -eq 2 -and [string]@($multi.value)[0] -eq '64240') `
    ($multi | ConvertTo-Json -Compress -Depth 3)
  $bin = Find-Reg 'EnableTCPA'
  Assert 'snapshot-exec Binary stays int array' `
    ($bin -and [string]$bin.kind -eq 'Binary' -and @($bin.value).Count -eq 4 -and [int]@($bin.value)[3] -eq 4) `
    ($bin | ConvertTo-Json -Compress -Depth 3)
  $qword = Find-Reg 'GlobalMaxTcpWindowSize'
  Assert 'snapshot-exec QWord numeric' ($qword -and [string]$qword.kind -eq 'QWord' -and [long]$qword.value -eq 65535)
  $dwordNeg = Find-Reg 'NetworkThrottlingIndex'
  Assert 'snapshot-exec DWord 0xffffffff round-trips as -1' ($dwordNeg -and [int]$dwordNeg.value -eq -1)
  $expand = Find-Reg 'EnableDCA'
  Assert 'snapshot-exec ExpandString preserved unexpanded' `
    ($expand -and [string]$expand.kind -eq 'ExpandString' -and [string]$expand.value -eq '%SystemRoot%\dca')
  $absent = Find-Reg 'LocalPriority'
  Assert 'snapshot-exec absent value recorded' ($absent -and [string]$absent.kind -eq 'absent')
  $adv = @($parsed.advancedProps) | Where-Object { [string]$_.keyword -eq '*ReceiveBuffers' } | Select-Object -First 1
  Assert 'snapshot-exec String[] advanced prop flattened' ($adv -and [string]$adv.value -eq '256,512')
  Assert 'snapshot-exec adapter states captured' (@($parsed.adapterStates).Count -ge 2)
  Assert 'snapshot-exec prefix policies parsed' (@($parsed.prefixPolicies).Count -ge 5)
}

# Re-apply must keep the pristine baseline byte-for-byte
$before = Get-Content -LiteralPath $snapshotFile -Raw
$second = Save-ExoNetworkSnapshot
$after = Get-Content -LiteralPath $snapshotFile -Raw
Assert 'snapshot-exec pristine baseline kept on re-apply' ($second -eq $true -and $before -eq $after)

# ---------------------------------------------------------------------------
# Phase 2 - full repair script executes and writes back correctly-typed values
# (child pwsh: the repair script ends with exit 0)
# ---------------------------------------------------------------------------
$driverPath = Join-Path $WorkDir 'repair-driver.ps1'
@(
  ("`$env:LOCALAPPDATA = '" + ($WorkDir -replace "'", "''") + "'"),
  ("`$env:EXO_TEST_CAPTURE = '" + ($captureFile -replace "'", "''") + "'"),
  (". '" + ($MocksPath -replace "'", "''") + "'"),
  (". '" + ($RepairScriptPath -replace "'", "''") + "'")
) | Set-Content -LiteralPath $driverPath -Encoding UTF8

$childShell = if (Get-Command pwsh -EA SilentlyContinue) { 'pwsh' } else { 'powershell' }
$repairOut = & $childShell -NoProfile -ExecutionPolicy Bypass -File $driverPath 2>&1 | Out-String
Assert 'repair-exec exits 0' ($LASTEXITCODE -eq 0) ("exit=" + $LASTEXITCODE + " out=" + $repairOut.Substring(0, [Math]::Min(300, $repairOut.Length)))
Assert 'repair-exec snapshot-driven mode' ($repairOut -match 'EXO_REPORT:restore-mode\|ok')
Assert 'repair-exec no registry restore failures' ($repairOut -match 'EXO_REPORT:restore-registry\|ok')

$capture = Get-Content -LiteralPath $captureFile -Raw
Assert 'repair-exec MultiString restored as String[]' `
  ($capture -match [regex]::Escape('|TcpWindowSize|MultiString|System.String[]|64240,131072')) $capture
Assert 'repair-exec Binary restored as Byte[]' `
  ($capture -match [regex]::Escape('|EnableTCPA|Binary|System.Byte[]|1,2,3,4'))
Assert 'repair-exec QWord restored' ($capture -match '\|GlobalMaxTcpWindowSize\|QWord\|System\.Int\d+\|65535')
Assert 'repair-exec DWord -1 restored' ($capture -match '\|NetworkThrottlingIndex\|DWord\|System\.Int\d+\|-1')
Assert 'repair-exec ExpandString restored' `
  ($capture -match [regex]::Escape('|EnableDCA|ExpandString|System.String|%SystemRoot%\dca'))
Assert 'repair-exec absent value removed' ($capture -match [regex]::Escape('DEL|HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider|LocalPriority'))
Assert 'repair-exec advanced prop multi-value restored' ($capture -match [regex]::Escape('ADV|Ethernet|*ReceiveBuffers|256;512'))
Assert 'repair-exec disabled adapter re-enabled' ($capture -match [regex]::Escape('NICON|Wi-Fi'))
Assert 'repair-exec snapshot deleted on full success' (-not (Microsoft.PowerShell.Management\Test-Path -LiteralPath $snapshotFile))

Write-Output ("EXOTEST-SUMMARY:failed=" + $script:failCount)
exit $(if ($script:failCount -eq 0) { 0 } else { 1 })
