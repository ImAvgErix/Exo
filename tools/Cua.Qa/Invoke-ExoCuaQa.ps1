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

function Invoke-CuaCall {
    param(
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Args = $null
    )
    # Empty-object tools (list_windows / list_apps): pass {} literally.
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

Write-Host '[cua-qa] doctor'
& cua-driver doctor
if ($LASTEXITCODE -ne 0) { throw 'cua-driver doctor failed' }

$status = & cua-driver status 2>&1 | Out-String
if ($status -notmatch 'running') {
    Write-Host '[cua-qa] starting cua-driver serve'
    Start-Process -FilePath 'cua-driver.exe' -ArgumentList 'serve' -WindowStyle Hidden
    Start-Sleep -Seconds 2
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

function Capture-Module([string]$Name, [int]$ButtonIndex) {
    Write-Host "[cua-qa] module $Name (element_index=$ButtonIndex)"
    if ($ButtonIndex -ge 0) {
        $null = Invoke-CuaCall -Tool 'click' -Args @{
            pid           = $pidExo
            window_id     = $hwnd
            element_index = $ButtonIndex
            delivery_mode = 'background'
        }
        # Internet/NVIDIA detect scripts are slower than 2s; wait for status text to settle.
        Start-Sleep -Seconds 2
        if ($Name -in @('Internet', 'NVIDIA', 'Discord', 'Steam')) {
            for ($w = 0; $w -lt 8; $w++) {
                $probeRaw = Invoke-CuaCall -Tool 'get_window_state' -Args @{
                    pid                = $pidExo
                    window_id          = $hwnd
                    include_screenshot = $false
                    max_elements       = 40
                }
                try {
                    $probe = $probeRaw | ConvertFrom-Json
                    $statusBlob = (@($probe.elements | ForEach-Object { [string]$_.label }) -join ' ')
                    if ($statusBlob -notmatch 'Checking(\.\.\.| status)|Detecting this PC') { break }
                } catch { }
                Start-Sleep -Seconds 1
            }
        }
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
    try {
        $o = $raw | ConvertFrom-Json
        $labels = @($o.elements | ForEach-Object { "[{0}] {1}: {2}" -f $_.element_index, $_.role, $_.label })
    } catch { }

    $summary.Add("## $Name")
    $summary.Add("")
    $summary.Add("- screenshot: ``docs/cua-qa/$([IO.Path]::GetFileName($png))``")
    $summary.Add("- elements: $($labels.Count)")
    $summary.Add('```')
    $labels | Select-Object -First 40 | ForEach-Object { $summary.Add($_) }
    $summary.Add('```')
    $summary.Add("")

    # Honesty heuristics (agent-readable)
    $blob = ($labels -join ' ').ToLowerInvariant()
    if ($Name -eq 'Discord' -and $blob -match 'already optimized' -and $blob -match 'not installed') {
        $summary.Add('**FAIL honesty:** Discord shows Already optimized while not-installed banner is visible.')
        $summary.Add('')
    }
    if ($Name -eq 'NVIDIA' -and $blob -notmatch 'g-sync|gsync|vrr') {
        $summary.Add('**WARN:** NVIDIA page missing G-SYNC/VRR control labels in UIA tree.')
        $summary.Add('')
    }
}

# Fresh snapshot for nav indices (TitleBar EXO + 6 modules + Home typical).
# NOTE: never use $home — PowerShell aliases it to read-only $HOME.
$navSnapRaw = Invoke-CuaCall -Tool 'get_window_state' -Args @{
    pid                = $pidExo
    window_id          = $hwnd
    include_screenshot = $false
    max_elements       = 40
}
$navSnap = $navSnapRaw | ConvertFrom-Json
$byLabel = @{}
foreach ($el in $navSnap.elements) {
    if ($el.role -eq 'Button' -and $el.label) { $byLabel[$el.label] = [int]$el.element_index }
}

foreach ($m in $Modules) {
    if ($m -eq 'ShellHome') {
        # 3.6.1 shell: overview is "Open system overview" (not "Home")
        $navHomeKey = $null
        foreach ($k in @('Open system overview', 'Home', 'Settings')) {
            if ($byLabel.ContainsKey($k)) { $navHomeKey = $k; break }
        }
        $idx = if ($navHomeKey) { [int]$byLabel[$navHomeKey] } else { -1 }
        Capture-Module 'ShellHome' $idx
        continue
    }
    $idx = if ($byLabel.ContainsKey($m)) { [int]$byLabel[$m] } else { -1 }
    if ($idx -lt 0) {
        $summary.Add("## $m")
        $summary.Add("")
        $summary.Add("**SKIP:** nav button not found in UIA tree.")
        $summary.Add("")
        continue
    }
    Capture-Module $m $idx
    # Refresh index map after nav (tree is stable for shell buttons usually)
    $navSnapRaw = Invoke-CuaCall -Tool 'get_window_state' -Args @{
        pid                = $pidExo
        window_id          = $hwnd
        include_screenshot = $false
        max_elements       = 40
    }
    try {
        $navSnap = $navSnapRaw | ConvertFrom-Json
        $byLabel = @{}
        foreach ($el in $navSnap.elements) {
            if ($el.role -eq 'Button' -and $el.label) { $byLabel[$el.label] = [int]$el.element_index }
        }
    } catch { }
}

$report = Join-Path $OutDir 'REPORT.md'
$summary | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host "[cua-qa] wrote $report"
Write-Host '[cua-qa] done'
exit 0

