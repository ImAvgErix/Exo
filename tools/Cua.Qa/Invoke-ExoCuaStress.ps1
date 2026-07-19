#Requires -Version 5.1
<#
.SYNOPSIS
  Full Exo Cua stress pass: every nav, Settings, home cards, Apply/Reapply on
  optimizers, Internet Analyze & Apply + Repair probe, multi-round churn.

.NOTES
  Expects UAC elevation without interactive prompt (user reports UAC disabled /
  auto-elevate). Internet Apply uses fail-closed canary + auto-rollback.
#>
[CmdletBinding()]
param(
    [string]$OutDir = "",
    [string]$ExoExe = "$env:LOCALAPPDATA\Exo\app\Exo.exe",
    [switch]$SkipInternetApply,
    [switch]$SkipNvidiaApply
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'docs\cua-qa\stress' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cuaBin = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin"
if (Test-Path $cuaBin) { $env:PATH = "$cuaBin;$env:PATH" }

function Invoke-CuaCall {
    param([Parameter(Mandatory)][string]$Tool, [hashtable]$Args = $null)
    if ($null -eq $Args -or $Args.Count -eq 0) { $json = '{}' }
    else { $json = $Args | ConvertTo-Json -Compress -Depth 6 }
    $raw = $json | & cua-driver call $Tool 2>&1 | Out-String
    return $raw
}

function Get-CuaJson {
    param([Parameter(Mandatory)][string]$Tool, [hashtable]$Args = $null)
    $raw = Invoke-CuaCall -Tool $Tool -Args $Args
    try { return ($raw | ConvertFrom-Json) } catch { return $null }
}

function Get-ButtonMap {
    param([int]$PidExo, [int64]$Hwnd, [int]$Max = 100)
    $snap = Get-CuaJson -Tool 'get_window_state' -Args @{
        pid = $PidExo; window_id = $Hwnd; include_screenshot = $false; max_elements = $Max
    }
    $map = @{}
    $labels = @()
    if ($snap -and $snap.elements) {
        foreach ($el in @($snap.elements)) {
            $lab = [string]$el.label
            $labels += ("[{0}] {1}: {2}" -f $el.element_index, $el.role, $lab)
            if ($el.role -eq 'Button' -and $lab) {
                if (-not $map.ContainsKey($lab)) { $map[$lab] = [int]$el.element_index }
            }
        }
    }
    return @{ Map = $map; Labels = $labels; Raw = $snap }
}

function Click-ByLabel {
    param(
        [int]$PidExo, [int64]$Hwnd, [string[]]$Candidates,
        [string]$StepName, [System.Collections.Generic.List[string]]$Log
    )
    $info = Get-ButtonMap -PidExo $PidExo -Hwnd $Hwnd
    foreach ($c in $Candidates) {
        if ($info.Map.ContainsKey($c)) {
            $idx = [int]$info.Map[$c]
            Write-Host "[stress] $StepName -> click '$c' (index=$idx)"
            $null = Invoke-CuaCall -Tool 'click' -Args @{
                pid = $PidExo; window_id = $Hwnd; element_index = $idx; delivery_mode = 'background'
            }
            $Log.Add("OK click $StepName : $c")
            return $true
        }
    }
    Write-Host "[stress] $StepName SKIP (no button among: $($Candidates -join ', '))"
    $Log.Add("SKIP $StepName : buttons not found")
    return $false
}

function Wait-NotLoading {
    param([int]$PidExo, [int64]$Hwnd, [int]$MaxSec = 45)
    $deadline = (Get-Date).AddSeconds($MaxSec)
    while ((Get-Date) -lt $deadline) {
        $info = Get-ButtonMap -PidExo $PidExo -Hwnd $Hwnd -Max 60
        $blob = ($info.Labels -join ' ')
        if ($blob -notmatch 'Checking(\.\.\.| status)|Detecting this PC|Reading this PC|Actions unlock when detection') {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Wait-ProgressDone {
    param([int]$PidExo, [int64]$Hwnd, [int]$MaxSec = 180)
    $deadline = (Get-Date).AddSeconds($MaxSec)
    $sawBusy = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $PidExo -ErrorAction SilentlyContinue)) {
            return 'CRASH'
        }
        $info = Get-ButtonMap -PidExo $PidExo -Hwnd $Hwnd -Max 80
        $blob = ($info.Labels -join ' ')
        if ($blob -match 'Applying|Analyz|Installing|Verif|Restoring|Measuring|Import|Progress') {
            $sawBusy = $true
        }
        # Done when Apply/Reapply buttons return enabled-looking primary labels and no busy progress.
        if ($sawBusy -and $blob -notmatch 'Applying\.\.\.|Analyzing|Installing DiscOpt|Importing|Measuring') {
            if ($blob -match 'Reapply|Apply|Analyze') { return 'OK' }
        }
        # Fast path: never saw busy but settled with result / already optimized
        if (-not $sawBusy -and $blob -match 'Already optimized|Verified|DONE|Repair complete|Last apply') {
            Start-Sleep -Seconds 2
            return 'OK-SETTLED'
        }
        Start-Sleep -Seconds 2
    }
    return 'TIMEOUT'
}

function Capture-Shot {
    param([int]$PidExo, [int64]$Hwnd, [string]$Name)
    $png = Join-Path $OutDir ("{0}.png" -f $Name)
    $jsonPath = Join-Path $OutDir ("{0}-state.json" -f $Name)
    $raw = Invoke-CuaCall -Tool 'get_window_state' -Args @{
        pid = $PidExo; window_id = $Hwnd
        screenshot_out_file = $png
        include_screenshot = $true
        max_elements = 140
    }
    $raw | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    return $raw
}

# --- boot ---
Write-Host '[stress] doctor'
& cua-driver doctor
if ($LASTEXITCODE -ne 0) { throw 'cua-driver doctor failed' }

$status = & cua-driver status 2>&1 | Out-String
if ($status -notmatch 'running') {
    Start-Process -FilePath 'cua-driver.exe' -ArgumentList 'serve' -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

Get-Process -Name Exo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
if (-not (Test-Path -LiteralPath $ExoExe)) { throw "Exo not found: $ExoExe" }
Write-Host "[stress] launch $ExoExe"
Start-Process -FilePath $ExoExe
Start-Sleep -Seconds 4

$exo = Get-Process -Name Exo -ErrorAction Stop | Select-Object -First 1
$pidExo = [int]$exo.Id
$win = $null
for ($i = 0; $i -lt 20 -and -not $win; $i++) {
    $windows = Get-CuaJson -Tool 'list_windows'
    $candidates = @()
    if ($windows.windows) { $candidates += @($windows.windows) }
    if ($windows._legacy_windows) { $candidates += @($windows._legacy_windows) }
    $win = $candidates |
        Where-Object {
            [int]$_.pid -eq $pidExo -and (
                $_.title -eq 'Exo' -or $_.title -like 'Exo*' -or ($_.app_name -and $_.app_name -like 'Exo*')
            ) -and [int]$_.width -gt 400 -and [int]$_.height -gt 300
        } |
        Sort-Object { [int]$_.width * [int]$_.height } -Descending |
        Select-Object -First 1
    if (-not $win) { Start-Sleep -Seconds 1 }
}
if (-not $win) { throw 'Exo window not found' }
$hwnd = [int64]$win.window_id
Write-Host "[stress] pid=$pidExo hwnd=$hwnd $($win.width)x$($win.height)"

$log = [System.Collections.Generic.List[string]]::new()
$fails = [System.Collections.Generic.List[string]]::new()
$log.Add("# Exo Cua STRESS - $(Get-Date -Format o)")
$log.Add("- pid: $pidExo")
$log.Add("- exe: $($exo.Path)")
$log.Add("- uac: assumed non-interactive elevate (user disabled UAC)")
$log.Add("")

function Assert-Alive([string]$Phase) {
    if (-not (Get-Process -Id $pidExo -ErrorAction SilentlyContinue)) {
        $fails.Add("CRASH after $Phase")
        throw "Exo process died after $Phase"
    }
    $log.Add("ALIVE $Phase")
}

# --- Round 1: every nav module settle + capture ---
$modules = @('Discord', 'Steam', 'Internet', 'NVIDIA', 'Riot', 'Epic')
foreach ($m in $modules) {
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @($m) -StepName "nav-$m" -Log $log | Out-Null
    Start-Sleep -Seconds 1
    $null = Wait-NotLoading -PidExo $pidExo -Hwnd $hwnd -MaxSec 50
    Assert-Alive "nav-$m"
    $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name ("r1-{0}" -f $m.ToLowerInvariant())
    $info = Get-ButtonMap -PidExo $pidExo -Hwnd $hwnd
    $blob = ($info.Labels -join ' ').ToLowerInvariant()
    if ($m -eq 'Discord' -and $blob -match 'already optimized' -and $blob -match 'not installed') {
        $fails.Add('Discord honesty FAIL: Already optimized + not installed')
    }
    if ($m -eq 'NVIDIA' -and $blob -notmatch 'g-sync|gsync|vrr') {
        $fails.Add('NVIDIA missing G-SYNC/VRR labels')
    }
    if ($m -eq 'Internet' -and $blob -notmatch 'analyze') {
        $fails.Add('Internet missing Analyze control')
    }
    if ($blob -match '┬╖|â€') {
        $fails.Add("$m mojibake in UIA labels")
    }
}

# --- Home + recommended next + settings ---
Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Open system overview', 'Home') -StepName 'nav-home' -Log $log | Out-Null
Start-Sleep -Seconds 2
Assert-Alive 'home'
$null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'r1-home'
$homeInfo = Get-ButtonMap -PidExo $pidExo -Hwnd $hwnd -Max 120
if (($homeInfo.Labels -join ' ') -match 'RECOMMENDED NEXT|Open recommended') {
    $log.Add('OK home recommended-next present')
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @(
        'Open recommended next optimizer', 'Open Internet', 'Open Steam', 'Open Discord', 'Fix Steam', 'Fix Internet'
    ) -StepName 'recommended-next' -Log $log | Out-Null
    Start-Sleep -Seconds 2
    Assert-Alive 'recommended-next'
    $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'r1-recommended-landed'
} else {
    $log.Add('NOTE home recommended-next not visible (maybe all verified)')
}

Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Settings') -StepName 'settings-open' -Log $log | Out-Null
Start-Sleep -Seconds 1
Assert-Alive 'settings'
$null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'r1-settings'
# Dismiss: click home again
Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Open system overview', 'Discord') -StepName 'settings-dismiss' -Log $log | Out-Null
Start-Sleep -Seconds 1

# --- Round 2: Apply stress (UAC disabled) ---
function Invoke-ModuleApply {
    param([string]$Module, [string[]]$ApplyLabels, [int]$TimeoutSec = 240)
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @($Module) -StepName "apply-nav-$Module" -Log $log | Out-Null
    Start-Sleep -Seconds 1
    $null = Wait-NotLoading -PidExo $pidExo -Hwnd $hwnd -MaxSec 60
    Assert-Alive "pre-apply-$Module"
    $clicked = Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates $ApplyLabels -StepName "apply-$Module" -Log $log
    if (-not $clicked) {
        $fails.Add("Could not click Apply on $Module")
        $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name ("apply-miss-{0}" -f $Module.ToLowerInvariant())
        return
    }
    Start-Sleep -Seconds 2
    $result = Wait-ProgressDone -PidExo $pidExo -Hwnd $hwnd -MaxSec $TimeoutSec
    Assert-Alive "post-apply-$Module"
    $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name ("apply-done-{0}" -f $Module.ToLowerInvariant())
    $log.Add("APPLY $Module result=$result")
    if ($result -eq 'CRASH') { $fails.Add("$Module Apply crashed Exo") }
    if ($result -eq 'TIMEOUT') { $fails.Add("$Module Apply timed out after ${TimeoutSec}s") }
    # Toggle last apply report if present
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Toggle last apply report') -StepName "report-$Module" -Log $log | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name ("apply-report-{0}" -f $Module.ToLowerInvariant())
}

# Steam first (contention guard reapply)
Invoke-ModuleApply -Module 'Steam' -ApplyLabels @('Apply Steam', 'Reapply', 'Apply') -TimeoutSec 300

# Discord reapply can be long (Equicord)
Invoke-ModuleApply -Module 'Discord' -ApplyLabels @('Apply Discord', 'Reapply', 'Apply') -TimeoutSec 420

# Riot / Epic
Invoke-ModuleApply -Module 'Riot' -ApplyLabels @('Apply Riot', 'Reapply', 'Apply') -TimeoutSec 180
Invoke-ModuleApply -Module 'Epic' -ApplyLabels @('Apply Epic', 'Reapply', 'Apply') -TimeoutSec 180

if (-not $SkipNvidiaApply) {
    # NVIDIA SafePolicy apply - can be long
    Invoke-ModuleApply -Module 'NVIDIA' -ApplyLabels @('Apply NVIDIA', 'Apply profile', 'Reapply', 'Apply') -TimeoutSec 600
} else {
    $log.Add('SKIP NVIDIA Apply (-SkipNvidiaApply)')
}

if (-not $SkipInternetApply) {
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Internet') -StepName 'net-nav' -Log $log | Out-Null
    Start-Sleep -Seconds 1
    $null = Wait-NotLoading -PidExo $pidExo -Hwnd $hwnd -MaxSec 40
    Assert-Alive 'pre-internet-apply'
    $clicked = Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @(
        'Analyze this connection and apply the best measured settings',
        'Analyze & Apply'
    ) -StepName 'internet-analyze-apply' -Log $log
    if ($clicked) {
        $result = Wait-ProgressDone -PidExo $pidExo -Hwnd $hwnd -MaxSec 300
        Assert-Alive 'post-internet-apply'
        $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'apply-done-internet'
        $log.Add("APPLY Internet result=$result")
        if ($result -eq 'TIMEOUT') { $fails.Add('Internet Analyze&Apply timed out') }
        $netBlob = ((Get-ButtonMap -PidExo $pidExo -Hwnd $hwnd -Max 100).Labels -join ' ')
        if ($netBlob -match 'rollback|rolled back|connectivity failed') {
            $log.Add('NOTE Internet auto-rollback or connectivity fail message present')
        }
        if ($netBlob -match 'Wi-Fi is never disabled|Safety:') {
            $log.Add('OK Internet safety copy present')
        }
    } else {
        $fails.Add('Internet Analyze & Apply button missing')
    }
} else {
    $log.Add('SKIP Internet Apply (-SkipInternetApply)')
}

# --- Round 3: rapid nav churn (crash hunt) ---
Write-Host '[stress] rapid nav churn x3'
for ($round = 1; $round -le 3; $round++) {
    foreach ($m in @('Discord', 'Steam', 'Internet', 'NVIDIA', 'Riot', 'Epic', 'Open system overview')) {
        $cands = if ($m -eq 'Open system overview') { @('Open system overview') } else { @($m) }
        Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates $cands -StepName "churn$round-$m" -Log $log | Out-Null
        Start-Sleep -Milliseconds 400
        if (-not (Get-Process -Id $pidExo -ErrorAction SilentlyContinue)) {
            $fails.Add("CRASH during churn round $round at $m")
            throw "crash churn $round $m"
        }
    }
}
Assert-Alive 'churn-complete'
$null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'r3-churn-final'

# --- Final module honesty pass ---
foreach ($m in $modules) {
    Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @($m) -StepName "final-$m" -Log $log | Out-Null
    Start-Sleep -Seconds 1
    $null = Wait-NotLoading -PidExo $pidExo -Hwnd $hwnd -MaxSec 40
    $null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name ("final-{0}" -f $m.ToLowerInvariant())
    $blob = ((Get-ButtonMap -PidExo $pidExo -Hwnd $hwnd).Labels -join ' ').ToLowerInvariant()
    if ($m -eq 'Discord' -and $blob -match 'already optimized' -and $blob -match 'not installed') {
        $fails.Add('FINAL Discord honesty FAIL')
    }
}

Click-ByLabel -PidExo $pidExo -Hwnd $hwnd -Candidates @('Open system overview') -StepName 'final-home' -Log $log | Out-Null
Start-Sleep -Seconds 2
$null = Capture-Shot -PidExo $pidExo -Hwnd $hwnd -Name 'final-home'

# Process still alive?
if (Get-Process -Id $pidExo -ErrorAction SilentlyContinue) {
    $log.Add('OK Exo still running at end of stress')
} else {
    $fails.Add('Exo not running at end')
}

$log.Add('')
$log.Add('## Failures')
if ($fails.Count -eq 0) { $log.Add('- none') }
else { foreach ($f in $fails) { $log.Add("- FAIL: $f") } }
$log.Add('')
$log.Add("## Summary fails=$($fails.Count)")

$report = Join-Path $OutDir 'STRESS-REPORT.md'
$log | Set-Content -LiteralPath $report -Encoding UTF8
# Also mirror key lines to docs/cua-qa
$mirror = Join-Path $repoRoot 'docs\cua-qa\STRESS-REPORT.md'
$log | Set-Content -LiteralPath $mirror -Encoding UTF8
Write-Host "[stress] wrote $report"
Write-Host "[stress] fails=$($fails.Count)"
if ($fails.Count -gt 0) { exit 2 }
exit 0
