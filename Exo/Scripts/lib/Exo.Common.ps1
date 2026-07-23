# Exo.Common.ps1 - shared helpers for optimizer Run wrappers (Wave 2 quality).
# Dot-source from Exo-*-Run.ps1. ASCII only. Safe to call multiple times.

Set-StrictMode -Version Latest

function Assert-ExoPwsh7 {
    if ($PSVersionTable.PSEdition -ne 'Core' -or [int]$PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Exo optimizers require PowerShell 7. Install: winget install Microsoft.PowerShell'
    }
}

function Get-ExoAppDataDir {
    $dir = Join-Path $env:LOCALAPPDATA 'Exo'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-ExoLogsDir {
    $dir = Join-Path (Get-ExoAppDataDir) 'logs'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Initialize-ExoRunLog {
    param([Parameter(Mandatory)][string]$Module)
    $safe = ($Module -replace '[^\w\-]', '_')
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $path = Join-Path (Get-ExoLogsDir) ("{0}-{1}.log" -f $safe, $stamp)
    $env:EXO_LOG = $path
    try {
        Set-Content -LiteralPath $path -Value ("# Exo {0} run {1:o}" -f $Module, (Get-Date).ToUniversalTime()) -Encoding UTF8
    } catch { }
    return $path
}

function Write-ExoReport {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][ValidateSet('ok', 'fail', 'skip')][string]$Status,
        [string]$Reason = ''
    )
    $entry = if ([string]::IsNullOrWhiteSpace($Reason)) { "$Step|$Status" } else { "$Step|$Status`:$Reason" }
    $line = "EXO_REPORT:$entry"
    Write-Output $line
    if ($env:EXO_LOG) {
        try { Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    }
}

function Get-ExoLibDir {
    # Resolve Scripts/lib whether we run from:
    #   %LocalAppData%\Exo\scripts\Steam  (working kit)
    #   %LocalAppData%\Exo\app\Scripts\Steam (bundled)
    #   repo Exo\Scripts\Steam
    param([string]$From = '')
    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($From)) {
        [void]$candidates.Add((Join-Path $From 'lib'))
        [void]$candidates.Add((Join-Path (Split-Path -Parent $From) 'lib'))
        [void]$candidates.Add((Join-Path $From '..\lib'))
    }
    $here = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($here) -and $MyInvocation.MyCommand.Path) {
        $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if (-not [string]::IsNullOrWhiteSpace($here)) {
        [void]$candidates.Add((Join-Path $here 'lib'))
        [void]$candidates.Add((Join-Path (Split-Path -Parent $here) 'lib'))
        [void]$candidates.Add((Join-Path $here '..\lib'))
    }
    if ($env:LOCALAPPDATA) {
        [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Exo\scripts\lib'))
        [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\lib'))
    }
    # Exo.exe base (elevated hosts still set this when launched from the app folder)
    try {
        $proc = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        if ($proc) {
            $appDir = Split-Path -Parent $proc
            [void]$candidates.Add((Join-Path $appDir 'Scripts\lib'))
        }
    } catch { }

    foreach ($c in $candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        try {
            $full = [IO.Path]::GetFullPath($c)
        } catch { continue }
        if (Test-Path -LiteralPath (Join-Path $full 'Exo.GameBar.ps1')) { return $full }
        if (Test-Path -LiteralPath (Join-Path $full 'Exo.Common.ps1')) { return $full }
    }
    return $null
}

function Import-ExoSharedLibs {
    <#
    .SYNOPSIS
      Resolve Scripts/lib and return the directory path.
      Callers MUST dot-source the return value ps1 files at script scope
      (dot-source inside this function would not export to the caller).
    #>
    param([string]$From = '')
    return (Get-ExoLibDir -From $From)
}

function Import-ExoSharedLibFiles {
    # Returns ordered list of lib script paths that exist (caller dotsources).
    param([string]$From = '')
    $libDir = Get-ExoLibDir -From $From
    if (-not $libDir) { return @() }
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @(
        'Exo.NoBackground.ps1', 'Exo.GameBar.ps1'
    )) {
        $path = Join-Path $libDir $name
        if (Test-Path -LiteralPath $path) { [void]$paths.Add($path) }
    }
    return @($paths)
}
