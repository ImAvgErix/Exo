#Requires -Version 5.1
<#
.SYNOPSIS
  Drive the running Exo window with Cua Driver (https://github.com/trycua/cua)
  and capture per-module screenshots + UIA trees for agent GUI QA.

.DESCRIPTION
  Requires:
    - cua-driver on PATH (default install: %LocalAppData%\Programs\Cua\cua-driver\bin)
    - cua-driver serve running (this script starts it if needed)
    - Exo running (launches %LocalAppData%\Exo\app\Exo.exe when missing)

  Outputs under docs/cua-qa/ (repo-relative when run from repo root).

  Nav contract:
    1. Re-snapshot immediately before every click (element cache is per-snapshot).
    2. Resolve Button by AutomationProperties.Name label.
    3. Background AX click first; if page markers missing, foreground AX then home-card.
    4. Capture only after verify markers (or after exhausting fallbacks).

.EXAMPLE
  pwsh -File tools/Cua.Qa/Invoke-ExoCuaQa.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = "",
    [string]$ExoExe = "$env:LOCALAPPDATA\Exo\app\Exo.exe",
    [string[]]$Modules = @('Discord', 'Steam', 'Internet', 'NVIDIA', 'Riot', 'Epic', 'ShellHome')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'docs\cua-qa' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cuaBin = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin"
if (Test-Path $cuaBin) { $env:PATH = "$cuaBin;$env:PATH" }

# Per-module UIA labels that prove we left SYSTEM overview.
$script:ModuleMarkers = @{
    'Discord'   = @('Apply Discord', 'Already optimized', 'WHAT EXO WILL CHANGE', 'Equicord', 'DISCORD')
    'Steam'     = @('Apply Steam', 'STEAM', 'WebHelper', 'steam.exe', 'Background policy')
    'Internet'  = @('Analyze this connection', 'Repair internet stack', 'INTERNET', 'Analyze & Apply')
    'NVIDIA'    = @('Apply NVIDIA', 'G-SYNC', 'VRR', 'NVIDIA')
    'Riot'      = @('Apply Riot', 'RIOT', 'Riot Client')
    'Epic'      = @('Apply Epic', 'EPIC', 'Epic Games')
    'ShellHome' = @('Optimization status', 'SYSTEM', 'Open Discord optimizer', 'LIVE SYSTEM READ')
}

function Invoke-CuaCall {
    param(
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Args = $null
    )
    if ($null -eq $Args -or $Args.Count -eq 0) {
        $json = '{}'
    } else {
        $json = $Args | ConvertTo-Json -Compress -Depth 6
    }
    $raw = $json | & cua-driver call $Tool 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -and $raw -match 'error') {
        Write-Warning "cua-driver call $Tool failed: $raw"
    }
    return $raw
}

function Get-CuaJson {
    param(
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Args = $null
    )
    $raw = Invoke-CuaCall -Tool $Tool -Args $Args
    try { return ($raw | ConvertFrom-Json) } catch { return $null }
}

function Get-LabelBlob {
    param($Elements)
    return (@($Elements | ForEach-Object { [string]$_.label }) -join ' ')
}

function Test-ModuleLanded {
    param(
        [string]$Name,
        [string]$Blob
    )
    $markers = $script:ModuleMarkers[$Name]
    # Unknown module names must not auto-pass (e.g. "NVIDIA,ShellHome" from bad CLI bind).
    if (-not $markers -or $markers.Count -eq 0) { return $false }
    foreach ($m in $markers) {
        if ($Blob -match [regex]::Escape($m)) { return $true }
    }
    return $false
}

function Get-NavButtonIndex {
    param(
        [int]$PidExo,
        [int64]$Hwnd,
        [string[]]$Candidates
    )
    $snap = Get-CuaJson -Tool 'get_window_state' -Args @{
        pid                = $PidExo
        window_id          = $Hwnd
        include_screenshot = $false
        max_elements       = 80
    }
    if (-not $snap -or -not $snap.elements) { return @{ Index = -1; Blob = '' } }
    $blob = Get-LabelBlob $snap.elements
    foreach ($lab in $Candidates) {
        $el = @($snap.elements | Where-Object { $_.role -eq 'Button' -and [string]$_.label -eq $lab } | Select-Object -First 1)
        if ($el -and $null -ne $el.element_index) {
            return @{ Index = [int]$el.element_index; Blob = $blob; Label = $lab }
        }
    }
    # Home cards as secondary (Open Discord optimizer, etc.)
    foreach ($lab in $Candidates) {
        $el = @($snap.elements | Where-Object { $_.role -eq 'Button' -and [string]$_.label -like "*$lab*" } | Select-Object -First 1)
        if ($el -and $null -ne $el.element_index) {
            return @{ Index = [int]$el.element_index; Blob = $blob; Label = [string]$el.label }
        }
    }
    return @{ Index = -1; Blob = $blob; Label = $null }
}

function Invoke-NavClick {
    param(
        [int]$PidExo,
        [int64]$Hwnd,
        [int]$ElementIndex,
        [string]$DeliveryMode = 'background'
    )
    $raw = Invoke-CuaCall -Tool 'click' -Args @{
        pid           = $PidExo
        window_id     = $Hwnd
        element_index = $ElementIndex
        delivery_mode = $DeliveryMode
    }
    return $raw
}

Write-Host '[cua-qa] doctor'
& cua-driver doctor
if ($LASTEXITCODE -ne 0) { throw 'cua-driver doctor failed' }

$status = & cua-driver status 2>&1 | Out-String
if ($status -notmatch 'running') {
    Write-Host '[cua-qa] starting cua-driver serve'
    Start-Process -FilePath 'cua-driver.exe' -ArgumentList 'serve' -WindowStyle Hidden
    $ready = $false
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $st = & cua-driver status 2>&1 | Out-String
        if ($st -match 'running') { $ready = $true; break }
    }
    if (-not $ready) { throw 'cua-driver serve failed to become ready' }
}

if (-not (Get-Process -Name Exo -ErrorAction SilentlyContinue)) {
    if (-not (Test-Path -LiteralPath $ExoExe)) { throw "Exo not found at $ExoExe" }
    Write-Host "[cua-qa] launching $ExoExe"
    Start-Process -FilePath $ExoExe
    Start-Sleep -Seconds 4
}

$exo = Get-Process -Name Exo -ErrorAction Stop | Select-Object -First 1
$pidExo = [int]$exo.Id
Write-Host "[cua-qa] Exo pid=$pidExo"

# Wait for first paint (cold boot can be a few seconds).
$win = $null
for ($i = 0; $i -lt 15 -and -not $win; $i++) {
    $windows = Get-CuaJson -Tool 'list_windows'
    $candidates = @()
    if ($windows -and $windows.windows) { $candidates += @($windows.windows) }
    if ($windows -and $windows._legacy_windows) { $candidates += @($windows._legacy_windows) }
    $win = $candidates |
        Where-Object {
            [int]$_.pid -eq $pidExo -and (
                $_.title -eq 'Exo' -or
                $_.title -like 'Exo*' -or
                ($_.app_name -and $_.app_name -like 'Exo*')
            ) -and [int]$_.width -gt 400 -and [int]$_.height -gt 300
        } |
        Sort-Object { [int]$_.width * [int]$_.height } -Descending |
        Select-Object -First 1
    if (-not $win) { Start-Sleep -Seconds 1 }
}
if (-not $win) { throw 'Exo window not found via cua-driver list_windows (waited for full chrome size)' }
$hwnd = [int64]$win.window_id
Write-Host "[cua-qa] window_id=$hwnd size=$($win.width)x$($win.height) title=$($win.title)"

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("# Exo Cua QA - $(Get-Date -Format o)")
$summary.Add("")
$summary.Add("- pid: $pidExo")
$summary.Add("- window_id: $hwnd")
$summary.Add("- exe: $($exo.Path)")
$summary.Add("")
$navFails = 0

function Capture-Module {
    param(
        [string]$Name,
        [string[]]$NavLabels
    )
    Write-Host "[cua-qa] module $Name (labels: $($NavLabels -join ', '))"

    $landed = $false
    $clickNote = 'no-nav'
    if ($NavLabels -and $NavLabels.Count -gt 0) {
        # Ladder: background AX → foreground AX → home-card Open <Module> optimizer
        $attempts = @(
            @{ Mode = 'background'; Labels = $NavLabels },
            @{ Mode = 'foreground'; Labels = $NavLabels }
        )
        if ($Name -ne 'ShellHome') {
            $attempts += @{
                Mode   = 'background'
                Labels = @("Open $Name optimizer", "Open $Name")
            }
        }

        foreach ($att in $attempts) {
            $nav = Get-NavButtonIndex -PidExo $pidExo -Hwnd $hwnd -Candidates $att.Labels
            if ($nav.Index -lt 0) {
                Write-Host "[cua-qa]   no button for $($att.Labels -join '|') ($($att.Mode))"
                continue
            }
            Write-Host "[cua-qa]   click idx=$($nav.Index) label='$($nav.Label)' mode=$($att.Mode)"
            $clickRaw = Invoke-NavClick -PidExo $pidExo -Hwnd $hwnd -ElementIndex $nav.Index -DeliveryMode $att.Mode
            $clickNote = "idx=$($nav.Index) label=$($nav.Label) mode=$($att.Mode)"
            try {
                $cj = $clickRaw | ConvertFrom-Json
                if ($cj.effect) { $clickNote += " effect=$($cj.effect)" }
                if ($cj.verified -ne $null) { $clickNote += " verified=$($cj.verified)" }
            } catch { }

            Start-Sleep -Milliseconds 1500
            # Wait for detect settle on heavy modules
            if ($Name -in @('Internet', 'NVIDIA', 'Discord', 'Steam')) {
                for ($w = 0; $w -lt 10; $w++) {
                    $probe = Get-CuaJson -Tool 'get_window_state' -Args @{
                        pid                = $pidExo
                        window_id          = $hwnd
                        include_screenshot = $false
                        max_elements       = 50
                    }
                    if (-not $probe) { Start-Sleep -Seconds 1; continue }
                    $statusBlob = Get-LabelBlob $probe.elements
                    if ($statusBlob -notmatch 'Checking(\.\.\.| status)|Detecting this PC') {
                        if (Test-ModuleLanded -Name $Name -Blob $statusBlob) {
                            $landed = $true
                            break
                        }
                    }
                    Start-Sleep -Seconds 1
                }
            } else {
                $probe = Get-CuaJson -Tool 'get_window_state' -Args @{
                    pid                = $pidExo
                    window_id          = $hwnd
                    include_screenshot = $false
                    max_elements       = 50
                }
                if ($probe) {
                    $statusBlob = Get-LabelBlob $probe.elements
                    $landed = Test-ModuleLanded -Name $Name -Blob $statusBlob
                }
            }

            if ($landed) {
                Write-Host "[cua-qa]   landed on $Name"
                break
            }
            Write-Host "[cua-qa]   not landed after $($att.Mode) — escalate"
        }
    } else {
        $landed = $true
    }

    $png = Join-Path $OutDir ("{0}.png" -f $Name.ToLowerInvariant())
    $jsonPath = Join-Path $OutDir ("{0}-state.json" -f $Name.ToLowerInvariant())
    $raw = Invoke-CuaCall -Tool 'get_window_state' -Args @{
        pid                  = $pidExo
        window_id            = $hwnd
        screenshot_out_file  = $png
        include_screenshot   = $true
        max_elements         = 120
    }
    $raw | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $labels = @()
    $blob = ''
    try {
        $o = $raw | ConvertFrom-Json
        $labels = @($o.elements | ForEach-Object { "[{0}] {1}: {2}" -f $_.element_index, $_.role, $_.label })
        $blob = Get-LabelBlob $o.elements
        if (-not $landed) {
            $landed = Test-ModuleLanded -Name $Name -Blob $blob
        }
    } catch { }

    $summary.Add("## $Name")
    $summary.Add("")
    $summary.Add("- screenshot: ``docs/cua-qa/$([IO.Path]::GetFileName($png))``")
    $summary.Add("- nav: $clickNote")
    $summary.Add("- landed: $landed")
    $summary.Add("- elements: $($labels.Count)")
    $summary.Add('```')
    $labels | Select-Object -First 40 | ForEach-Object { $summary.Add($_) }
    $summary.Add('```')
    $summary.Add("")

    if (-not $landed) {
        $script:navFails++
        $summary.Add("**FAIL nav:** Did not reach $Name page (markers missing; still Home or wrong module).")
        $summary.Add('')
    }

    # Honesty heuristics (agent-readable)
    $blobL = $blob.ToLowerInvariant()
    if ($Name -eq 'Discord' -and $blobL -match 'already optimized' -and $blobL -match 'not installed') {
        $summary.Add('**FAIL honesty:** Discord shows Already optimized while not-installed banner is visible.')
        $summary.Add('')
    }
    if ($Name -eq 'NVIDIA' -and $blobL -notmatch 'g-sync|gsync|vrr') {
        $summary.Add('**WARN:** NVIDIA page missing G-SYNC/VRR control labels in UIA tree.')
        $summary.Add('')
    }
    if ($Name -eq 'Internet' -and $landed) {
        if ($blobL -match 'rss policy|not exposed by this nic|host offloads') {
            $summary.Add('- Internet RSS: soft-ok / N-A surface visible (good).')
            $summary.Add('')
        }
        if ($blobL -match 'optimized - open:.*rss') {
            $summary.Add('**WARN:** Internet status still surfaces RSS as an open failure row.')
            $summary.Add('')
        }
    }
}

# Normalize -Modules: support "NVIDIA,ShellHome" when outer shell splits arrays poorly.
$moduleList = [System.Collections.Generic.List[string]]::new()
foreach ($raw in @($Modules)) {
    foreach ($part in (@($raw -split '[,;]') | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if (-not $moduleList.Contains($part)) { $moduleList.Add($part) }
    }
}
if ($moduleList.Count -eq 0) {
    foreach ($d in @('Discord', 'Steam', 'Internet', 'NVIDIA', 'Riot', 'Epic', 'ShellHome')) {
        $moduleList.Add($d)
    }
}

foreach ($m in $moduleList) {
    if ($m -eq 'ShellHome') {
        Capture-Module -Name 'ShellHome' -NavLabels @('Open system overview', 'Home')
        continue
    }
    Capture-Module -Name $m -NavLabels @($m)
}

$summary.Insert(5, "- nav_fails: $navFails")
$summary.Insert(6, "")

$report = Join-Path $OutDir 'REPORT.md'
$summary | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host "[cua-qa] wrote $report (nav_fails=$navFails)"
Write-Host '[cua-qa] done'
if ($navFails -gt 0) { exit 2 }
exit 0
