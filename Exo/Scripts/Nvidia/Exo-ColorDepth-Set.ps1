# Exo - set NVIDIA display color bit depth via NVAPI helper.
# Usage: Exo-ColorDepth-Set.ps1 -Depth 10 [-DisplayId 12345]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('6', '8', '10', '12', 'BPC6', 'BPC8', 'BPC10', 'BPC12')]
    [string]$Depth,
    [uint32]$DisplayId = 0
)

$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-NvDisplayExe {
    foreach ($c in @(
        (Join-Path $Root 'tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe'),
        (Join-Path $env:LOCALAPPDATA 'Exo\app\Scripts\Nvidia\tools\Exo.NvDisplay.exe')
    )) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

$exe = Get-NvDisplayExe
if (-not $exe) {
    Write-Host 'EXO_NVDISPLAY_JSON:{"ok":false,"error":"helper-missing"}'
    exit 2
}

$argList = @('--set-depth', $Depth)
if ($DisplayId -gt 0) {
    $argList += @('--display-id', "$DisplayId")
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = ($argList -join ' ')
$psi.WorkingDirectory = Split-Path -Parent $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$p = [Diagnostics.Process]::Start($psi)
$stdout = $p.StandardOutput.ReadToEnd()
$stderr = $p.StandardError.ReadToEnd()
$p.WaitForExit()
if ($stdout) { Write-Host $stdout.TrimEnd() }
if ($stderr) { Write-Host $stderr.TrimEnd() }
exit $p.ExitCode
