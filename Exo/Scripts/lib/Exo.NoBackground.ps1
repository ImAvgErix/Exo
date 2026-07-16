# Exo.NoBackground.ps1 - purge Exo scheduled tasks and Exo Run keys (Wave 2).
# Product law: Exo never installs background tasks/services/startup for itself.

Set-StrictMode -Version Latest

function Unregister-ExoBackground {
    param([switch]$Quiet)

    $removed = 0

    # Known historical names + any task whose name starts with Exo-
    $known = @(
        'Exo-NvidiaTrayHide',
        'Exo-NvidiaDisplayPersist',
        'Exo-NvidiaBackgroundPersist',
        'Exo-NvidiaTray',
        'Exo-Nvidia',
        'Exo-Discord',
        'Exo-Steam',
        'Exo-Internet'
    )
    foreach ($name in $known) {
        try {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
            $removed++
        } catch { }
        try {
            $null = schtasks /Delete /TN $name /F 2>&1
        } catch { }
    }

    try {
        Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
            $_.TaskName -match '(?i)^Exo-'
        } | ForEach-Object {
            try {
                Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false -ErrorAction SilentlyContinue
                $removed++
            } catch { }
        }
    } catch { }

    foreach ($run in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (-not (Test-Path -LiteralPath $run)) { continue }
        try {
            $props = Get-ItemProperty -LiteralPath $run -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            foreach ($p in $props.PSObject.Properties) {
                if ($p.Name -match '^PS') { continue }
                $val = [string]$p.Value
                if ($p.Name -match '(?i)^Exo' -or $val -match '(?i)\\Exo\\|LocalAppData\\Exo') {
                    try {
                        Remove-ItemProperty -LiteralPath $run -Name $p.Name -Force -ErrorAction SilentlyContinue
                        $removed++
                    } catch { }
                }
            }
        } catch { }
    }

    if (-not $Quiet) {
        Write-Output ("EXO_REPORT:no-background|ok:purged={0}" -f $removed)
    }
    return $removed
}
