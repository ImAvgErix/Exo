#Requires -Version 5.1
<#
.SYNOPSIS
  Exo dependency doctor - install/update path for machine prerequisites.

.DESCRIPTION
  Idempotent and elevated-safe. Runs under Windows PowerShell 5.1 or PowerShell 7
  so it can bootstrap machines where pwsh is missing entirely.

  Called by the Exo.exe SFX after every install/update so community PCs get:
    * .NET 10 Desktop Runtime (FDD helpers e.g. Exo.NvDisplay)
    * WebView2 Evergreen Runtime (SPA shell)
    * PowerShell 7 (Preview preferred; stable accepted)
    * VC++ 2015-2022 x64 redistributable (native bits)
  Also prunes stale Exo update leftovers under %LocalAppData%\Exo.

  Never uninstalls PowerShell Preview or Windows Terminal Preview - beta hosts
  are preferred for optimizer scripts when present.

  Steps (each emits EXO_REPORT:<step>|ok|fail|skip):
    dotnet-detect / dotnet-install
    webview2-detect / webview2-install
    vcredist-detect / vcredist-install
    pwsh-detect / pwsh-install / pwsh-upgrade
    preview-pwsh-uninstall / preview-terminal-uninstall  (always skip - never remove)
    cache-prune

  NEVER deletes optimizer state (*-optimizer.json), snapshots, settings.json, or logs\.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Exo-DependencyDoctor.ps1 -Reason install
#>
[CmdletBinding()]
param(
    [string]$Reason = 'manual',
    [string]$LogPath = '',
    # Skip the winget/MSI install machinery (used by tests; detection still runs).
    [switch]$NoInstall,
    # Kept for CLI compatibility. Preview uninstall is never performed.
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
            Where-Object { $_.Name -notmatch '(?i)Preview' } |
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

function Get-PreviewPwsh {
    # PowerShell 7 Preview - preferred host when the user keeps betas installed.
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($root in @($env:ProgramFiles, ${env:ProgramW6432}, ${env:ProgramFiles(x86)})) {
        if ($root) {
            [void]$candidates.Add((Join-Path $root 'PowerShell\7-preview\pwsh.exe'))
            [void]$candidates.Add((Join-Path $root 'PowerShell\7-preview\pwsh-preview.exe'))
        }
    }
    $cmdPreview = Get-Command pwsh-preview -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPreview -and $cmdPreview.Source) { [void]$candidates.Add([string]$cmdPreview.Source) }
    $cmdPwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmdPwsh -and $cmdPwsh.Source -and ($cmdPwsh.Source -match '(?i)preview')) {
        [void]$candidates.Add([string]$cmdPwsh.Source)
    }
    $appsRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $appsRoot) {
        Get-ChildItem -LiteralPath $appsRoot -Directory -Filter 'Microsoft.PowerShellPreview*' -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object {
                [void]$candidates.Add((Join-Path $_.FullName 'pwsh.exe'))
                [void]$candidates.Add((Join-Path $_.FullName 'pwsh-preview.exe'))
            }
    }
    [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh-preview.exe'))
    [void]$candidates.Add((Join-Path $Script:ExoDataDir 'runtime\PowerShellPreview\pwsh.exe'))

    foreach ($p in ($candidates | Select-Object -Unique)) {
        if (-not $p -or $p -match 'WindowsPowerShell') { continue }
        if (-not (Test-Path -LiteralPath $p)) { continue }
        return $p
    }
    return $null
}

function Get-AnyPwsh7 {
    # Prefer Preview, then stable. Any Core 7.x host is enough for optimizers.
    $preview = Get-PreviewPwsh
    if ($preview) { return $preview }
    return Get-StablePwsh
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

# --- .NET 10 Desktop Runtime ------------------------------------------------

function Test-DotNet10DesktopRuntime {
    # Shared framework folder is the ground truth (works without `dotnet` on PATH).
    $roots = @(
        (Join-Path ${env:ProgramFiles} 'dotnet\shared\Microsoft.WindowsDesktop.App'),
        (Join-Path ${env:ProgramW6432} 'dotnet\shared\Microsoft.WindowsDesktop.App')
    ) | Where-Object { $_ } | Select-Object -Unique
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $hit = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.' } |
            Select-Object -First 1
        if ($hit) { return $true }
    }
    return $false
}

function Install-DotNet10DesktopRuntime {
    # Prefer winget; fall back to official quiet installer (Desktop Runtime includes base Runtime).
    if ($winget) {
        Write-DoctorLine '[*] Installing .NET 10 Desktop Runtime via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'install', '--id', 'Microsoft.DotNet.DesktopRuntime.10', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity') 1200
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) {
            if (Test-DotNet10DesktopRuntime) { return $true }
        } else {
            Write-DoctorLine "[!] winget DesktopRuntime.10 exit: $code - trying direct installer..."
        }
    } else {
        Write-DoctorLine '[!] winget not found - downloading .NET 10 Desktop Runtime installer...'
    }

    # Always-latest x64 Desktop Runtime channel (includes Microsoft.NETCore.App 10).
    $url = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
    $setup = Join-Path $env:TEMP ('windowsdesktop-runtime-10-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
        Write-DoctorLine "[*] Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing `
            -Headers @{ 'User-Agent' = 'Exo-DependencyDoctor/2.0' } -TimeoutSec 900
        if (-not (Test-Path -LiteralPath $setup) -or (Get-Item -LiteralPath $setup).Length -lt 1MB) {
            Write-DoctorLine '[!] .NET Desktop Runtime download looks invalid.'
            return $false
        }
        Write-DoctorLine '[*] Running .NET Desktop Runtime installer (quiet)...'
        $code = Invoke-DoctorProcess $setup @('/install', '/quiet', '/norestart') 1200
        # 0 ok, 1638 already installed, 3010 reboot pending
        if ($code -eq 0 -or $code -eq 1638 -or $code -eq 3010) {
            return (Test-DotNet10DesktopRuntime)
        }
        Write-DoctorLine "[!] .NET Desktop Runtime installer exit $code"
        return $false
    } catch {
        Write-DoctorLine "[!] .NET Desktop Runtime install failed: $($_.Exception.Message)"
        return $false
    } finally {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
    }
}

# --- WebView2 Evergreen Runtime ---------------------------------------------

function Test-WebView2Runtime {
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($k in $keys) {
        try {
            $pv = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).pv
            $loc = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).location
            if ($pv -and $loc) {
                $exe = Join-Path $loc (Join-Path $pv 'msedgewebview2.exe')
                if (Test-Path -LiteralPath $exe) { return $true }
            }
        } catch { }
    }
    $root = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\EdgeWebView\Application'
    if (Test-Path -LiteralPath $root) {
        $hit = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'msedgewebview2.exe') } |
            Select-Object -First 1
        if ($hit) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    The WebView2 runtime Exo ships with itself.
.DESCRIPTION
    Exo bundles a runtime at <app>\Runtime\WebView2 and pins it via
    WEBVIEW2_BROWSER_EXECUTABLE_FOLDER, so a machine with no system WebView2 still runs the
    UI perfectly - a real startup log shows webview2-bundled-runtime followed by
    web-host-ready and web-nav-ok on exactly such a machine.

    The doctor did not know that. It called WebView2 critical, tried winget, downloaded a
    bootstrapper, failed, printed "Exo UI may not load until it is installed" and returned
    doctor|fail - all for a dependency the app carries. Alarming, slow, and wrong.
#>
function Test-ExoBundledWebView2 {
    foreach ($base in @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot), $env:LOCALAPPDATA)) {
        if (-not $base) { continue }
        foreach ($candidate in @(
            (Join-Path $base 'Runtime\WebView2\msedgewebview2.exe'),
            (Join-Path $base 'Exo\app\Runtime\WebView2\msedgewebview2.exe'))) {
            try { if (Test-Path -LiteralPath $candidate) { return $true } } catch { }
        }
    }
    return $false
}

function Install-WebView2Runtime {
    if ($winget) {
        Write-DoctorLine '[*] Installing WebView2 Runtime via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'install', '--id', 'Microsoft.EdgeWebView2Runtime', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity') 1200
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) {
            if (Test-WebView2Runtime) { return $true }
        } else {
            Write-DoctorLine "[!] winget WebView2 exit: $code - trying Evergreen bootstrapper..."
        }
    }
    $url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    $setup = Join-Path $env:TEMP ('MicrosoftEdgeWebview2Setup-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
        Write-DoctorLine '[*] Downloading WebView2 Evergreen bootstrapper...'
        Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing `
            -Headers @{ 'User-Agent' = 'Exo-DependencyDoctor/2.0' } -TimeoutSec 600
        if (-not (Test-Path -LiteralPath $setup) -or (Get-Item -LiteralPath $setup).Length -lt 10KB) {
            Write-DoctorLine '[!] WebView2 bootstrapper looks invalid.'
            return $false
        }
        $code = Invoke-DoctorProcess $setup @('/silent', '/install') 600
        if ($null -eq $code) { return $false }
        for ($i = 0; $i -lt 30 -and -not (Test-WebView2Runtime); $i++) { Start-Sleep -Seconds 1 }
        return (Test-WebView2Runtime)
    } catch {
        Write-DoctorLine "[!] WebView2 install failed: $($_.Exception.Message)"
        return $false
    } finally {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
    }
}

# --- VC++ 2015-2022 x64 redistributable -------------------------------------

function Test-VCRedistX64 {
    # VS 2015-2022 unified runtime (x64)
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )
    foreach ($k in $keys) {
        try {
            $installed = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).Installed
            if ($installed -eq 1) { return $true }
        } catch { }
    }
    return $false
}

function Install-VCRedistX64 {
    if ($winget) {
        Write-DoctorLine '[*] Installing VC++ 2015-2022 x64 redistributable via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'install', '--id', 'Microsoft.VCRedist.2015+.x64', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity') 900
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) {
            return $true
        }
        Write-DoctorLine "[!] winget VCRedist exit: $code"
    } else {
        Write-DoctorLine '[!] winget not found - skipping VC++ redist (usually already present on Win11).'
    }
    return (Test-VCRedistX64)
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
$criticalOk = $true

# -- .NET 10 Desktop Runtime (NvDisplay + any FDD helpers) ------------------
if (Test-DotNet10DesktopRuntime) {
    Write-DoctorLine '[+] .NET 10 Desktop Runtime is installed.'
    Write-Report 'dotnet-detect' 'ok'
    Write-Report 'dotnet-install' 'skip'
} else {
    Write-DoctorLine '[!] .NET 10 Desktop Runtime not found.'
    Write-Report 'dotnet-detect' 'fail'
    if ($NoInstall) {
        Write-Report 'dotnet-install' 'skip'
        $criticalOk = $false
    } else {
        if (Install-DotNet10DesktopRuntime) {
            Write-DoctorLine '[+] .NET 10 Desktop Runtime ready.'
            Write-Report 'dotnet-install' 'ok'
        } else {
            Write-DoctorLine '[-] Could not install .NET 10 Desktop Runtime. NVIDIA display tools may fail until it is installed.'
            Write-Report 'dotnet-install' 'fail'
            $criticalOk = $false
        }
    }
}

# -- WebView2 (SPA shell) ---------------------------------------------------
if (Test-WebView2Runtime) {
    Write-DoctorLine '[+] WebView2 Runtime is installed.'
    Write-Report 'webview2-detect' 'ok'
    Write-Report 'webview2-install' 'skip'
} elseif (Test-ExoBundledWebView2) {
    # Exo carries its own. Nothing to install, and nothing to warn about.
    Write-DoctorLine '[+] WebView2: using the runtime bundled with Exo.'
    Write-Report 'webview2-detect' 'ok'
    Write-Report 'webview2-install' 'skip'
} else {
    Write-DoctorLine '[!] WebView2 Runtime not found.'
    Write-Report 'webview2-detect' 'fail'
    if ($NoInstall) {
        Write-Report 'webview2-install' 'skip'
        $criticalOk = $false
    } else {
        if (Install-WebView2Runtime) {
            Write-DoctorLine '[+] WebView2 Runtime ready.'
            Write-Report 'webview2-install' 'ok'
        } else {
            Write-DoctorLine '[-] Could not install WebView2. Exo UI may not load until it is installed.'
            Write-Report 'webview2-install' 'fail'
            $criticalOk = $false
        }
    }
}

# -- VC++ redistributable (best-effort; common on Win11 already) ------------
if (Test-VCRedistX64) {
    Write-DoctorLine '[+] VC++ 2015-2022 x64 redistributable present.'
    Write-Report 'vcredist-detect' 'ok'
    Write-Report 'vcredist-install' 'skip'
} else {
    Write-DoctorLine '[!] VC++ redistributable not detected.'
    Write-Report 'vcredist-detect' 'fail'
    if ($NoInstall) {
        Write-Report 'vcredist-install' 'skip'
    } else {
        if (Install-VCRedistX64) {
            Write-DoctorLine '[+] VC++ redistributable ready (or already present).'
            Write-Report 'vcredist-install' 'ok'
        } else {
            Write-DoctorLine '[!] VC++ redistributable install skipped/failed (non-fatal).'
            Write-Report 'vcredist-install' 'fail'
        }
    }
}

# -- PowerShell 7 (Preview preferred, stable accepted) ----------------------
$preview = Get-PreviewPwsh
$stable = Get-StablePwsh
$anyPwsh = if ($preview) { $preview } elseif ($stable) { $stable } else { $null }

if ($preview) {
    Write-DoctorLine "[+] PowerShell 7 Preview: $preview"
    Write-Report 'pwsh-detect' 'ok'
} elseif ($stable) {
    Write-DoctorLine "[+] Stable PowerShell 7: $stable"
    Write-Report 'pwsh-detect' 'ok'
} else {
    Write-DoctorLine '[!] PowerShell 7 not found (Preview or stable).'
    Write-Report 'pwsh-detect' 'fail'
}

# Install only when no pwsh 7 host exists. Prefer Preview winget id first.
if ($NoInstall) {
    Write-Report 'pwsh-install' 'skip'
    Write-Report 'pwsh-upgrade' 'skip'
} elseif ($anyPwsh) {
    Write-Report 'pwsh-install' 'skip'
    # Optional upgrade of stable only when stable is the active host (never touch Preview).
    if ($stable -and -not $preview -and $winget) {
        Write-DoctorLine '[*] Checking for a newer stable PowerShell 7 via winget...'
        $code = Invoke-DoctorProcess $winget @(
            'upgrade', '--id', 'Microsoft.PowerShell', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity')
        if ($code -eq 0) { Write-Report 'pwsh-upgrade' 'ok' }
        elseif ($code -eq -1978335189 -or $code -eq -1978335212) {
            Write-DoctorLine '[+] Stable PowerShell 7 is already current.'
            Write-Report 'pwsh-upgrade' 'skip'
        } else {
            Write-DoctorLine "[!] winget upgrade exit: $code"
            Write-Report 'pwsh-upgrade' 'skip'
        }
    } else {
        Write-Report 'pwsh-upgrade' 'skip'
    }
} else {
    $installed = $false
    if ($winget) {
        Write-DoctorLine '[*] Installing PowerShell 7 Preview via winget (preferred)...'
        $code = Invoke-DoctorProcess $winget @(
            'install', '--id', 'Microsoft.PowerShell.Preview', '-e', '--silent',
            '--accept-package-agreements', '--accept-source-agreements',
            '--disable-interactivity')
        if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) { $installed = $true }
        else { Write-DoctorLine "[!] winget Preview install exit: $code - trying stable..." }
        if (-not $installed) {
            Write-DoctorLine '[*] Installing stable PowerShell 7 via winget...'
            $code = Invoke-DoctorProcess $winget @(
                'install', '--id', 'Microsoft.PowerShell', '-e', '--silent',
                '--accept-package-agreements', '--accept-source-agreements',
                '--disable-interactivity')
            if ($code -eq 0 -or $code -eq -1978335189 -or $code -eq -1978335212) { $installed = $true }
            else { Write-DoctorLine "[!] winget install exit: $code" }
        }
    } else {
        Write-DoctorLine '[!] winget not found - using the official MSI fallback (stable)...'
    }
    if (-not $installed) {
        $latest = Get-LatestStablePwshRelease
        $installed = Install-StablePwshViaMsi $latest
    }
    $preview = Get-PreviewPwsh
    $stable = Get-StablePwsh
    $anyPwsh = if ($preview) { $preview } elseif ($stable) { $stable } else { $null }
    if ($anyPwsh) {
        Write-DoctorLine "[+] PowerShell 7 ready: $anyPwsh"
        Write-Report 'pwsh-install' 'ok'
    } else {
        Write-DoctorLine "[-] Could not install PowerShell 7. Install Preview with 'winget install Microsoft.PowerShell.Preview' or stable with 'winget install Microsoft.PowerShell'."
        Write-Report 'pwsh-install' 'fail'
        $criticalOk = $false
    }
    Write-Report 'pwsh-upgrade' 'skip'
}

# Step 3: NEVER remove PowerShell Preview or Windows Terminal Preview.
# Exo prefers Preview hosts when present; betas are intentional for this product.
# (KeepPreview switch retained for CLI compatibility - uninstall is always skipped.)
Write-DoctorLine '[*] Keeping PowerShell Preview / Windows Terminal Preview (never uninstalled).'
Write-Report 'preview-pwsh-uninstall' 'skip'
Write-Report 'preview-terminal-uninstall' 'skip'
# KeepPreview switch is accepted for CLI compatibility; uninstall is always skipped.

# Cache hygiene under %LocalAppData%\Exo.
# StablePwshPresent only gates pruning of the *legacy Exo portable* Preview folder
# under runtime\ - it does not uninstall Store/winget Preview apps.
$anyPwshNow = [bool](Get-AnyPwsh7)
Invoke-CachePrune ([bool]$stable)

if (-not $anyPwshNow) { $criticalOk = $false }

if ($criticalOk) {
    Write-DoctorLine '[+] Dependency doctor finished - .NET 10, WebView2, PowerShell 7 ready (Preview preferred).'
    Write-Report 'doctor' 'ok'
    exit 0
}

Write-DoctorLine '[-] Dependency doctor finished with missing critical deps. See EXO_REPORT lines above.'
Write-Report 'doctor' 'fail'
exit 1
