#Requires -Version 5.1
<#
.SYNOPSIS
  Exo dependency doctor - keeps the machine on stable PowerShell 7 and prunes
  stale Exo update leftovers.

.DESCRIPTION
  Idempotent and elevated-safe. Runs under Windows PowerShell 5.1 or PowerShell 7
  so it can bootstrap machines where pwsh is missing entirely.

  Steps (each emits EXO_REPORT:<step>|ok|fail|skip):
    pwsh-detect              locate stable PowerShell 7
    pwsh-install             install stable PowerShell 7 via winget, MSI fallback
    pwsh-upgrade             upgrade an outdated stable PowerShell 7
    preview-pwsh-uninstall   remove Microsoft.PowerShell.Preview (best effort)
    preview-terminal-uninstall  remove Microsoft.WindowsTerminal.Preview (best effort)
    cache-prune              prune %LocalAppData%\Exo update/staging leftovers

  The doctor NEVER deletes optimizer state (*-optimizer.json), snapshots
  (network-snapshot.json), settings.json, or anything under logs\.

.EXAMPLE
  pwsh -NoProfile -ExecutionPolicy Bypass -File .\Exo-DependencyDoctor.ps1 -Reason install
#>
[CmdletBinding()]
param(
    [string]$Reason = 'manual',
    [string]$LogPath = '',
    # Skip the winget/MSI install machinery (used by tests; detection still runs).
    [switch]$NoInstall,
    # Skip preview uninstalls (cache pruning and detection still run).
    [switch]$KeepPreview
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Off

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Write-Host 'EXO_REPORT:doctor|skip'
    Write-Host '[!] Windows only - nothing to do.'
    exit 0
}

$Script:LocalAppData = $env:LOCALAPPDATA
if ([string]::IsNullOrWhiteSpace($Script:LocalAppData)) {
    $Script:LocalAppData = [Environment]::GetFolderPath('LocalApplicationData')
}
$Script:ExoDataDir = Join-Path $Script:LocalAppData 'Exo'

function Write-DoctorLine([string]$Line) {
    Write-Host $Line
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        try {
            $dir = Split-Path -Parent $LogPath
            if ($dir -and -not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Add-Content -LiteralPath $LogPath -Value $Line -Encoding UTF8 -ErrorAction SilentlyContinue
        } catch { }
    }
}

function Write-Report([string]$Step, [string]$Result) {
    Write-DoctorLine "EXO_REPORT:${Step}|${Result}"
}

# --- Stable PowerShell 7 detection -----------------------------------------

function Get-StablePwsh {
    # Stable only: never Windows PowerShell 5.1, never a preview channel.
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($root in @($env:ProgramFiles, ${env:ProgramW6432}, ${env:ProgramFiles(x86)})) {
        if ($root) { [void]$candidates.Add((Join-Path $root 'PowerShell\7\pwsh.exe')) }
    }
    $cmd = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd -and $cmd.Source) { [void]$candidates.Add([string]$cmd.Source) }
    $appsRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $appsRoot) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShell_*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { [void]$candidates.Add((Join-Path $_.FullName 'pwsh.exe')) }
    }
    [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe'))
    [void]$candidates.Add((Join-Path $Script:ExoDataDir 'runtime\PowerShell\pwsh.exe'))

    foreach ($p in ($candidates | Select-Object -Unique)) {
        if (-not $p -or $p -match 'WindowsPowerShell') { continue }
        if ($p -match '(?i)preview') { continue }
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $info = $null
        try { $info = (Get-Item -LiteralPath $p -ErrorAction Stop).VersionInfo } catch { }
        if ($info -and ("$($info.ProductVersion) $($info.FileVersion)" -match '(?i)preview')) { continue }
        return $p
    }
    return $null
}

function Get-PwshVersion([string]$ExePath) {
    try {
        $raw = (Get-Item -LiteralPath $ExePath -ErrorAction Stop).VersionInfo.ProductVersion
        if (-not $raw) { return $null }
        $clean = ($raw -split '[-+ ]')[0]
        $parsed = $null
        if ([version]::TryParse($clean, [ref]$parsed)) { return $parsed }
    } catch { }
    return $null
}

function Get-Winget {
    $cmd = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd -and $cmd.Source) { return [string]$cmd.Source }
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (Test-Path -LiteralPath $alias) { return $alias }
    return $null
}

function ConvertTo-DoctorArgString([string[]]$Arguments) {
    # ProcessStartInfo.ArgumentList does not exist on .NET Framework (PS 5.1),
    # so build a conservatively quoted argument string instead.
    $parts = foreach ($a in $Arguments) {
        if ($null -eq $a) { continue }
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    return ($parts -join ' ')
}

function Invoke-DoctorProcess([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSec = 900) {
    # Returns exit code, or $null when the process could not run / timed out.
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $FilePath
        $psi.Arguments = ConvertTo-DoctorArgString $Arguments
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) { return $null }
        # Drain output so a chatty child (winget) cannot deadlock on full pipes.
        $stdout = $proc.StandardOutput.ReadToEndAsync()
        $stderr = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill() } catch { }
            Write-DoctorLine "[!] Timed out after ${TimeoutSec}s: $FilePath"
            return $null
        }
        $proc.WaitForExit()
        foreach ($chunk in @($stdout.Result, $stderr.Result)) {
            if ([string]::IsNullOrWhiteSpace($chunk)) { continue }
            $flat = ($chunk -replace '[\r\n]+', ' ').Trim()
            if ($flat.Length -gt 220) { $flat = $flat.Substring(0, 220) + '...' }
            Write-DoctorLine "    $flat"
        }
        return $proc.ExitCode
    } catch {
        Write-DoctorLine "[!] Could not run ${FilePath}: $($_.Exception.Message)"
        return $null
    }
}

function Get-LatestStablePwshRelease {
    # Latest stable (non-prerelease) PowerShell release with a win-x64 MSI asset.
    # Returns @{ Version = [version]; MsiUrl; MsiSize; MsiSha256 } or $null.
    try {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
        $headers = @{ 'User-Agent' = 'Exo-DependencyDoctor/1.0'; 'Accept' = 'application/vnd.github+json' }
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/PowerShell/PowerShell/releases?per_page=15' `
            -Headers $headers -TimeoutSec 60
        foreach ($rel in @($releases)) {
            if ($rel.prerelease -or $rel.draft) { continue }
            $tag = ([string]$rel.tag_name).Trim().TrimStart('v', 'V')
            if ($tag -match '(?i)preview|rc') { continue }
            $parsed = $null
            if (-not [version]::TryParse(($tag -split '[-+]')[0], [ref]$parsed)) { continue }
            $msi = @($rel.assets) |
                Where-Object { $_.name -match '(?i)^PowerShell-7.*-win-x64\.msi$' } |
                Select-Object -First 1
            if (-not $msi) { continue }
            $sha = $null
            if ($msi.digest -and ([string]$msi.digest) -match '^sha256:([0-9a-fA-F]{64})$') {
                $sha = $Matches[1].ToLowerInvariant()
            }
            return @{
                Version   = $parsed
                MsiUrl    = [string]$msi.browser_download_url
                MsiSize   = [long]$msi.size
                MsiSha256 = $sha
            }
        }
    } catch {
        Write-DoctorLine "[!] Could not query PowerShell releases: $($_.Exception.Message)"
    }
    return $null
}

function Install-StablePwshViaMsi($Release) {
    if (-not $Release -or -not $Release.MsiUrl) {
        Write-DoctorLine '[!] No stable PowerShell MSI release available.'
        return $false
    }
    $msiPath = Join-Path $env:TEMP ('PowerShell-stable-' + [guid]::NewGuid().ToString('N') + '.msi')
    try {
        Write-DoctorLine "[*] Downloading $($Release.MsiUrl)"
        Invoke-WebRequest -Uri $Release.MsiUrl -OutFile $msiPath -UseBasicParsing `
            -Headers @{ 'User-Agent' = 'Exo-DependencyDoctor/1.0' } -TimeoutSec 600
        $item = Get-Item -LiteralPath $msiPath
        if ($Release.MsiSize -gt 0 -and $item.Length -ne $Release.MsiSize) {
            Write-DoctorLine "[!] MSI size mismatch ($($item.Length) vs $($Release.MsiSize)) - not installing."
            return $false
        }
        if ($Release.MsiSha256) {
            $actual = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $Release.MsiSha256) {
                Write-DoctorLine '[!] MSI failed SHA-256 verification - not installing.'
                return $false
            }
            Write-DoctorLine '[*] MSI SHA-256 verified.'
        }
        $msiexec = Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe'
        $code = Invoke-DoctorProcess $msiexec @('/i', $msiPath, '/qn', '/norestart') 1200
        # 0 = ok, 3010 = ok + reboot pending, 1603/others = failure (often no elevation).
        if ($code -eq 0 -or $code -eq 3010) { return $true }
        Write-DoctorLine "[!] msiexec exit $code - stable PowerShell MSI install failed (elevation declined?)."
        return $false
    } catch {
        Write-DoctorLine "[!] MSI install failed: $($_.Exception.Message)"
        return $false
    } finally {
        Remove-Item -LiteralPath $msiPath -Force -ErrorAction SilentlyContinue
    }
}

# --- Cache pruning ----------------------------------------------------------

function Test-ProtectedPath([string]$FullPath) {
    $leaf = Split-Path -Leaf $FullPath
    if ($leaf -match '(?i)-optimizer\.json$') { return $true }
    if ($leaf -ieq 'network-snapshot.json') { return $true }
    if ($leaf -ieq 'settings.json') { return $true }
    if ($FullPath -match '(?i)[\\/]logs([\\/]|$)') { return $true }
    return $false
}

function Remove-DoctorItem([string]$FullPath, [ref]$Removed, [ref]$Failed) {
    if (Test-ProtectedPath $FullPath) {
        Write-DoctorLine "[!] Refusing to prune protected path: $FullPath"
        return
    }
    try {
        if (Test-Path -LiteralPath $FullPath -PathType Container) {
            Remove-Item -LiteralPath $FullPath -Recurse -Force -ErrorAction Stop
        } else {
            Remove-Item -LiteralPath $FullPath -Force -ErrorAction Stop
        }
        $Removed.Value++
        Write-DoctorLine "[+] Pruned: $FullPath"
    } catch {
        $Failed.Value++
        Write-DoctorLine "[!] Could not prune (in use?): $FullPath"
    }
}

function Invoke-CachePrune([bool]$StablePwshPresent) {
    $removed = 0
    $failed = 0
    $root = $Script:ExoDataDir
    if (-not (Test-Path -LiteralPath $root)) {
        Write-Report 'cache-prune' 'skip'
        return
    }
    # Anything younger than this may belong to an in-flight update - leave it.
    $cutoff = (Get-Date).AddHours(-24)

    # 1) Old app stage-swap folders from the SFX installer (app.old-*, app.incoming-*,
    #    app.broken-*, legacy app-update). The live app lives in 'app' - never touched.
    Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $name = $_.Name
        if (($name -like 'app.old-*' -or $name -like 'app.incoming-*' -or
             $name -like 'app.broken-*' -or $name -ieq 'app-update') -and
            $_.LastWriteTime -lt $cutoff) {
            Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed)
        }
    }

    # 2) Stale downloaded installers + leftover script-update work dirs under updates\.
    $updates = Join-Path $root 'updates'
    if (Test-Path -LiteralPath $updates) {
        Get-ChildItem -LiteralPath $updates -File -Filter 'Exo*.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object { Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed) }
        Get-ChildItem -LiteralPath $updates -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^[0-9a-fA-F]{32}$' -and $_.LastWriteTime -lt $cutoff } |
            ForEach-Object { Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed) }
    }

    # 3) Script-kit staging leftovers under scripts\ (<kit>.fresh-*/.prev-*/.update-*/.backup-*).
    $scripts = Join-Path $root 'scripts'
    if (Test-Path -LiteralPath $scripts) {
        Get-ChildItem -LiteralPath $scripts -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '\.(fresh|prev|update|backup)-[0-9a-fA-F]{32}$' -and
                $_.LastWriteTime -lt $cutoff) {
                Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed)
            }
        }
    }

    # 4) Runtime staging leftovers + the legacy Exo-managed portable PREVIEW copy.
    #    The preview copy is deleted only when stable pwsh is confirmed available.
    $runtime = Join-Path $root 'runtime'
    if (Test-Path -LiteralPath $runtime) {
        Get-ChildItem -LiteralPath $runtime -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Name -match '\.staging-[0-9a-fA-F]{32}$' -and $_.LastWriteTime -lt $cutoff) {
                Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed)
            } elseif ($_.Name -match '(?i)-download\.zip$' -and $_.LastWriteTime -lt $cutoff) {
                Remove-DoctorItem $_.FullName ([ref]$removed) ([ref]$failed)
            }
        }
        $legacyPreview = Join-Path $runtime 'PowerShellPreview'
        if ($StablePwshPresent -and (Test-Path -LiteralPath $legacyPreview)) {
            Remove-DoctorItem $legacyPreview ([ref]$removed) ([ref]$failed)
        }
    }

    # 5) Versioned tool caches: in tools\<tool>\, each versioned copy is a child
    #    directory carrying a version.txt marker (first line = version string).
    #    Keep the newest marked copy, prune older marked copies. Directories
    #    without a marker are never touched.
    $tools = Join-Path $root 'tools'
    if (Test-Path -LiteralPath $tools) {
        Get-ChildItem -LiteralPath $tools -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $versioned = @()
            Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                $marker = Join-Path $_.FullName 'version.txt'
                if (Test-Path -LiteralPath $marker) {
                    $text = ''
                    try { $text = ([string](Get-Content -LiteralPath $marker -First 1 -ErrorAction Stop)).Trim() } catch { }
                    $parsed = $null
                    if ([version]::TryParse(($text.TrimStart('v', 'V') -split '[-+]')[0], [ref]$parsed)) {
                        $versioned += [pscustomobject]@{ Dir = $_.FullName; Version = $parsed }
                    }
                }
            }
            if ($versioned.Count -gt 1) {
                $ordered = $versioned | Sort-Object Version -Descending
                Write-DoctorLine "[*] $($_.Name): keeping v$($ordered[0].Version), pruning $($versioned.Count - 1) older cache(s)."
                $ordered | Select-Object -Skip 1 | ForEach-Object {
                    Remove-DoctorItem $_.Dir ([ref]$removed) ([ref]$failed)
                }
            }
        }
    }

    Write-DoctorLine "[*] Cache prune: removed $removed item(s), $failed failure(s)."
    if ($failed -gt 0 -and $removed -eq 0) { Write-Report 'cache-prune' 'fail' }
    else { Write-Report 'cache-prune' 'ok' }
}

# --- Main -------------------------------------------------------------------

Write-DoctorLine ''
Write-DoctorLine "  Exo dependency doctor (reason: $Reason)"
Write-DoctorLine ''

$winget = Get-Winget
$latest = $null

# Step 1: stable PowerShell 7 present?
$stable = Get-StablePwsh
if ($stable) {
    Write-DoctorLine "[+] Stable PowerShell 7: $stable"
    Write-Report 'pwsh-detect' 'ok'
} else {
    Write-DoctorLine '[!] Stable PowerShell 7 not found.'
    Write-Report 'pwsh-detect' 'fail'
}

# Step 2: install when missing / upgrade when outdated.
if ($NoInstall) {
    Write-Report 'pwsh-install' 'skip'
    Write-Report 'pwsh-upgrade' 'skip'
} elseif (-not $stable) {
    $installed = $false
    if ($winget) {
        Write-DoctorLine '[*] Installing stable PowerShell 7 via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'install', '--id', 'Microsoft.PowerShell', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity')
        # 0 = ok; -1978335189 = no applicable update; -1978335212 = already installed.
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) { $installed = $true }
        else { Write-DoctorLine "[!] winget install exit: $code" }
    } else {
        Write-DoctorLine '[!] winget not found - using the official MSI fallback...'
    }
    if (-not $installed) {
        $latest = Get-LatestStablePwshRelease
        $installed = Install-StablePwshViaMsi $latest
    }
    $stable = Get-StablePwsh
    if ($stable) {
        Write-DoctorLine "[+] Stable PowerShell 7 ready: $stable"
        Write-Report 'pwsh-install' 'ok'
    } else {
        Write-DoctorLine "[-] Could not install stable PowerShell 7. Install it with 'winget install Microsoft.PowerShell' or from the Microsoft Store ('PowerShell')."
        Write-Report 'pwsh-install' 'fail'
    }
    Write-Report 'pwsh-upgrade' 'skip'
} else {
    Write-Report 'pwsh-install' 'skip'
    $upgraded = $null
    if ($winget) {
        Write-DoctorLine '[*] Checking for a stable PowerShell 7 upgrade via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'upgrade', '--id', 'Microsoft.PowerShell', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity')
        if ($code -eq 0) { $upgraded = 'ok' }
        elseif ($code -eq -1978335189 -or $code -eq -1978335212) {
            Write-DoctorLine '[+] Stable PowerShell 7 is already current.'
            $upgraded = 'skip'
        }
        else { Write-DoctorLine "[!] winget upgrade exit: $code" }
    }
    if ($null -eq $upgraded) {
        # No winget (or it failed): compare against the latest GitHub release.
        $current = Get-PwshVersion $stable
        $latest = Get-LatestStablePwshRelease
        if ($current -and $latest -and $latest.Version -gt $current) {
            Write-DoctorLine "[*] Stable PowerShell $current is outdated (latest $($latest.Version)) - MSI upgrade..."
            if (Install-StablePwshViaMsi $latest) { $upgraded = 'ok' } else { $upgraded = 'fail' }
        } else {
            $upgraded = 'skip'
        }
    }
    Write-Report 'pwsh-upgrade' $upgraded
}

# Step 3: retire the preview channels Exo used to require. Only when a stable
# pwsh exists (never strip the only working host) and always tolerated -
# a declined UAC prompt or missing package must not fail the doctor.
if ($KeepPreview) {
    Write-Report 'preview-pwsh-uninstall' 'skip'
    Write-Report 'preview-terminal-uninstall' 'skip'
} elseif (-not $stable) {
    Write-DoctorLine '[!] Keeping preview installs - no stable PowerShell 7 to replace them yet.'
    Write-Report 'preview-pwsh-uninstall' 'skip'
    Write-Report 'preview-terminal-uninstall' 'skip'
} elseif (-not $winget) {
    Write-DoctorLine '[!] winget not found - leaving any preview installs in place.'
    Write-Report 'preview-pwsh-uninstall' 'skip'
    Write-Report 'preview-terminal-uninstall' 'skip'
} else {
    $previewTargets = @(
        @{ Id = 'Microsoft.PowerShell.Preview'; Step = 'preview-pwsh-uninstall'; Name = 'PowerShell 7 Preview' },
        @{ Id = 'Microsoft.WindowsTerminal.Preview'; Step = 'preview-terminal-uninstall'; Name = 'Windows Terminal Preview' }
    )
    foreach ($target in $previewTargets) {
        $listCode = Invoke-DoctorProcess $winget @(
            'list', '--id', $target.Id, '-e', '--disable-interactivity',
            '--accept-source-agreements') 120
        if ($listCode -ne 0) {
            Write-DoctorLine "[*] $($target.Name) is not installed - nothing to remove."
            Write-Report $target.Step 'skip'
            continue
        }
        Write-DoctorLine "[*] Uninstalling $($target.Name)..."
        $code = Invoke-DoctorProcess $winget @(
            'uninstall', '--id', $target.Id, '-e', '--silent',
            '--disable-interactivity', '--accept-source-agreements')
        if ($code -eq 0) {
            Write-DoctorLine "[+] $($target.Name) removed."
            Write-Report $target.Step 'ok'
        } else {
            Write-DoctorLine "[!] Could not remove $($target.Name) (exit $code) - it was declined, in use, or already gone. Continuing."
            Write-Report $target.Step 'fail'
        }
    }
}

# Step 4: cache hygiene under %LocalAppData%\Exo.
Invoke-CachePrune ([bool]$stable)

if ($stable) {
    Write-DoctorLine '[+] Dependency doctor finished.'
    Write-Report 'doctor' 'ok'
    exit 0
}

Write-DoctorLine '[-] Dependency doctor finished, but stable PowerShell 7 is still missing.'
Write-Report 'doctor' 'fail'
exit 1
