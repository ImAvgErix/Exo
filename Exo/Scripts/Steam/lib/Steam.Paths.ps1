# Steam.Paths.ps1 - thin path helpers extracted for maintainability (Wave 3+).
# Dot-sourced by Steam.Bootstrap.ps1. Safe to call multiple times. ASCII only.

Set-StrictMode -Version Latest

function Get-ExoSteamInstallPathFromRegistry {
    $candidates = @(
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'
        'HKLM:\SOFTWARE\Valve\Steam'
        'HKCU:\SOFTWARE\Valve\Steam'
    )
    foreach ($key in $candidates) {
        try {
            if (-not (Test-Path -LiteralPath $key)) { continue }
            $p = [string](Get-ItemProperty -LiteralPath $key -ErrorAction Stop).InstallPath
            if (-not [string]::IsNullOrWhiteSpace($p) -and (Test-Path -LiteralPath (Join-Path $p 'steam.exe'))) {
                return $p
            }
        } catch { }
    }
    return $null
}

function Get-ExoSteamDefaultInstallPaths {
    return @(
        (Join-Path ${env:ProgramFiles(x86)} 'Steam')
        (Join-Path $env:ProgramFiles 'Steam')
        (Join-Path $env:LOCALAPPDATA 'Steam')
    )
}
