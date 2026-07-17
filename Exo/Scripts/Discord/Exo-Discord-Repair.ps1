# Exo non-interactive Discord repair
# Restores stock, bootable Discord while preserving login by default.
# ASCII-only source so Windows PowerShell and pwsh never mis-parse punctuation.

param(
    [switch]$NonInteractive,
    [switch]$FullReset
)

$ErrorActionPreference = 'Stop'
$env:EXO = '1'
$env:DISCOPT_NONINTERACTIVE = '1'

function Write-HubProgress([int]$Percent, [string]$Status) {
    $p = [Math]::Max(0, [Math]::Min(100, $Percent))
    $line = "EXO_PROGRESS:$p|$Status"
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
    if ($env:EXO_LOG) {
        try {
            $dir = Split-Path -Parent $env:EXO_LOG
            if ($dir -and (Test-Path -LiteralPath $dir)) {
                Add-Content -LiteralPath $env:EXO_LOG -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}
function Write-RepStep([string]$Msg) { Write-Host "[*] $Msg" -ForegroundColor Cyan }
function Write-RepOk([string]$Msg)   { Write-Host "[+] $Msg" -ForegroundColor Green }
function Write-RepWarn([string]$Msg) { Write-Host "[!] $Msg" -ForegroundColor Yellow }
function Write-RepErr([string]$Msg)  { Write-Host "[-] $Msg" -ForegroundColor Red }

function Stop-RepairDiscord([string]$DiscordRoot) {
    $names = @('Discord', 'Discord.bin', 'Update')
    $rootPrefix = $null
    try { $rootPrefix = [IO.Path]::GetFullPath($DiscordRoot).TrimEnd('\') + '\' } catch { }
    for ($round = 1; $round -le 4; $round++) {
        $procs = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {
            try {
                $path = $_.Path
                if ($path -and $rootPrefix) {
                    return [IO.Path]::GetFullPath($path).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
                }
            } catch { }
            return $_.ProcessName -in @('Discord', 'Discord.bin')
        })
        if ($procs.Count -eq 0) { break }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            try { & taskkill.exe /F /T /PID $p.Id 2>$null | Out-Null } catch { }
        }
        Start-Sleep -Milliseconds (175 * $round)
    }
}

function Get-RepairActiveApp([string]$DiscordRoot) {
    Get-ChildItem -LiteralPath $DiscordRoot -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object {
            $parsed = [version]'0.0.0.0'
            [void][version]::TryParse(($_.Name -replace '^app-', ''), [ref]$parsed)
            $parsed
        } -Descending |
        Select-Object -First 1
}

function Test-RepairStockExecutableOnly([string]$DiscordRoot) {
    # Windows can briefly keep a terminated executable image mapped in PID 4.
    # In that state every mutable Discord file is already gone, but deleting the
    # final official Discord.exe fails until the system releases the image. It is
    # safe to let the signed installer repair around that one immutable stock file.
    $remainingFiles = @(Get-ChildItem -LiteralPath $DiscordRoot -File -Recurse -Force -ErrorAction SilentlyContinue)
    if ($remainingFiles.Count -ne 1) { return $false }

    $exe = $remainingFiles[0]
    if ($exe.Name -ine 'Discord.exe' -or $exe.Directory.Name -notmatch '^app-[0-9]+(?:\.[0-9]+)+$') {
        return $false
    }

    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $exe.FullName -ErrorAction Stop
        return $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
            $signature.SignerCertificate -and
            $signature.SignerCertificate.Subject -match '(?:^|,\s*)O=Discord Inc\.(?:,|$)'
    } catch {
        return $false
    }
}

function Remove-RepairProgramFiles([string]$DiscordRoot) {
    if (-not (Test-Path -LiteralPath $DiscordRoot)) {
        Write-RepOk 'No old program files to remove'
        return $false
    }
    $expectedRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Discord'
    if ([IO.Path]::GetFullPath($DiscordRoot).TrimEnd('\') -ine [IO.Path]::GetFullPath($expectedRoot).TrimEnd('\')) {
        throw "Refusing to remove unexpected Discord path: $DiscordRoot"
    }
    Write-RepStep 'Removing Discord program files...'
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Stop-RepairDiscord $DiscordRoot
        Remove-Item -LiteralPath $DiscordRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $DiscordRoot)) { break }
        Start-Sleep -Seconds 2
    }
    if (Test-Path -LiteralPath $DiscordRoot) {
        if (Test-RepairStockExecutableOnly $DiscordRoot) {
            Write-RepWarn 'Windows is still releasing the signed Discord executable; reusing it after cleaning every mutable file'
            return $true
        }
        throw "Could not delete $DiscordRoot - close Discord in Task Manager and retry"
    }
    Write-RepOk 'Old program files removed'
    return $false
}

function Clear-RepairRendererState([string]$AppDataDiscord, [bool]$DoFullReset) {
    if (-not (Test-Path -LiteralPath $AppDataDiscord)) {
        Write-RepOk 'No cached app data to clean'
        return
    }
    if ($DoFullReset) {
        Write-RepStep 'FULL reset - clearing app data including login...'
        Remove-Item -LiteralPath $AppDataDiscord -Recurse -Force -ErrorAction SilentlyContinue
        Write-RepOk 'App data fully cleared'
        return
    }
    # Keep login/session folders only.
    $keep = @('Local Storage', 'IndexedDB', 'Cookies', 'Cookies-journal', 'databases', 'Network')
    Write-RepStep 'Purging renderer caches (login kept)...'
    $removed = 0
    Get-ChildItem -LiteralPath $AppDataDiscord -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($keep -contains $_.Name) { return }
        try { attrib -R $_.FullName 2>$null } catch { }
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    Write-RepOk "Renderer state purged ($removed item(s))"
}

function Get-RepairVerifiedDiscordSetup {
    Write-RepStep 'Downloading official Discord installer...'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiscordSetup-exo-repair.exe'
    if (Test-Path -LiteralPath $setup) {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
    }
    $url = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    $headers = @{ 'User-Agent' = 'Exo-Repair/1.0' }
    try {
        try {
            Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing -Headers $headers -TimeoutSec 180
        } catch {
            throw "Discord installer download failed - check your internet connection ($($_.Exception.Message))"
        }
        if (-not (Test-Path -LiteralPath $setup) -or ((Get-Item -LiteralPath $setup).Length -lt 50000000)) {
            throw 'Discord installer download failed - file missing or too small'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $setup -ErrorAction Stop
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignerCertificate.Subject -notmatch '(?i)\bDiscord\b') {
            throw "Discord installer signature is invalid ($($signature.Status))"
        }

        return $setup
    } catch {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Expand-RepairDiscordFromSignedSetup([string]$DiscordRoot, [string]$Setup) {
    # DiscordSetup is an Authenticode-verified PE with a ZIP payload resource.
    # Extract the official full nupkg directly when Windows keeps Discord.exe
    # mapped in PID 4 and Squirrel cannot replace that one file in-place.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $work = Join-Path ([IO.Path]::GetTempPath()) ('exo-discord-repair-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $payloadZip = Join-Path $work 'payload.zip'
        $input = [IO.File]::OpenRead($Setup)
        try {
            $tailSize = [int][Math]::Min($input.Length, 4MB)
            $tail = New-Object byte[] $tailSize
            [void]$input.Seek(-$tailSize, [IO.SeekOrigin]::End)
            $read = 0
            while ($read -lt $tailSize) {
                $n = $input.Read($tail, $read, $tailSize - $read)
                if ($n -le 0) { throw 'Unexpected end of Discord installer while locating its package' }
                $read += $n
            }

            $eocdIndex = -1
            for ($i = $tailSize - 22; $i -ge 0; $i--) {
                if ($tail[$i] -eq 0x50 -and $tail[$i + 1] -eq 0x4b -and
                    $tail[$i + 2] -eq 0x05 -and $tail[$i + 3] -eq 0x06) {
                    $eocdIndex = $i
                    break
                }
            }
            if ($eocdIndex -lt 0) { throw 'Official Discord installer package was not found' }

            $eocd = $input.Length - $tailSize + $eocdIndex
            $centralSize = [BitConverter]::ToUInt32($tail, $eocdIndex + 12)
            $centralOffset = [BitConverter]::ToUInt32($tail, $eocdIndex + 16)
            $commentLength = [BitConverter]::ToUInt16($tail, $eocdIndex + 20)
            $zipStart = $eocd - [long]$centralSize - [long]$centralOffset
            $zipEnd = $eocd + 22 + $commentLength
            if ($zipStart -lt 0 -or $zipEnd -gt $input.Length -or $zipEnd -le $zipStart) {
                throw 'Official Discord installer package bounds are invalid'
            }

            [void]$input.Seek($zipStart, [IO.SeekOrigin]::Begin)
            $header = New-Object byte[] 4
            if ($input.Read($header, 0, 4) -ne 4 -or $header[0] -ne 0x50 -or
                $header[1] -ne 0x4b -or $header[2] -ne 0x03 -or $header[3] -ne 0x04) {
                throw 'Official Discord installer package header is invalid'
            }

            [void]$input.Seek($zipStart, [IO.SeekOrigin]::Begin)
            $output = [IO.File]::Create($payloadZip)
            try {
                $remaining = $zipEnd - $zipStart
                $buffer = New-Object byte[] 1048576
                while ($remaining -gt 0) {
                    $want = [int][Math]::Min($buffer.Length, $remaining)
                    $n = $input.Read($buffer, 0, $want)
                    if ($n -le 0) { throw 'Unexpected end of official Discord package' }
                    $output.Write($buffer, 0, $n)
                    $remaining -= $n
                }
            } finally {
                $output.Dispose()
            }
        } finally {
            $input.Dispose()
        }

        $outer = [IO.Compression.ZipFile]::OpenRead($payloadZip)
        try {
            $packageEntry = $outer.Entries | Where-Object {
                $_.FullName -match '^Discord-(?<version>[0-9]+(?:\.[0-9]+)+)-full\.nupkg$'
            } | Select-Object -First 1
            $updateEntry = $outer.GetEntry('Update.exe')
            $releasesEntry = $outer.GetEntry('RELEASES')
            if (-not $packageEntry -or -not $updateEntry -or -not $releasesEntry) {
                throw 'Official Discord installer is missing required package entries'
            }
            if ($packageEntry.FullName -notmatch '^Discord-(?<version>[0-9]+(?:\.[0-9]+)+)-full\.nupkg$') {
                throw 'Official Discord package version is invalid'
            }
            $version = $Matches.version
            $packagePath = Join-Path $work $packageEntry.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($packageEntry, $packagePath, $true)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($updateEntry, (Join-Path $DiscordRoot 'Update.exe'), $true)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($releasesEntry, (Join-Path $DiscordRoot 'RELEASES'), $true)
        } finally {
            $outer.Dispose()
        }

        $appDir = Join-Path $DiscordRoot "app-$version"
        New-Item -ItemType Directory -Path $appDir -Force | Out-Null
        $appPrefix = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'
        $package = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            foreach ($entry in $package.Entries) {
                if (-not $entry.FullName.StartsWith('lib/net45/', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $relative = $entry.FullName.Substring(10)
                if ([string]::IsNullOrWhiteSpace($relative) -or $relative.EndsWith('/')) { continue }
                $target = [IO.Path]::GetFullPath((Join-Path $appDir ($relative.Replace('/', '\'))))
                if (-not $target.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Unsafe path in official Discord package: $relative"
                }
                $parent = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $parent)) {
                    New-Item -ItemType Directory -Path $parent -Force | Out-Null
                }
                if ($relative -ieq 'Discord.exe' -and (Test-Path -LiteralPath $target)) {
                    $existingSignature = Get-AuthenticodeSignature -LiteralPath $target -ErrorAction Stop
                    if ($existingSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
                        -not $existingSignature.SignerCertificate -or
                        $existingSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Discord Inc\.(?:,|$)') {
                        throw 'Locked Discord executable is not signed by Discord Inc.'
                    }
                    continue
                }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally {
            $package.Dispose()
        }

        $packagesDir = Join-Path $DiscordRoot 'packages'
        New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
        Copy-Item -LiteralPath $packagePath -Destination (Join-Path $packagesDir $packageEntry.Name) -Force
        Copy-Item -LiteralPath (Join-Path $DiscordRoot 'RELEASES') -Destination (Join-Path $packagesDir 'RELEASES') -Force
        Copy-Item -LiteralPath (Join-Path $appDir 'installer.db') -Destination (Join-Path $DiscordRoot 'installer.db') -Force

        foreach ($required in @('Discord.exe', 'icudtl.dat', 'resources.pak', 'installer.db', 'resources\app.asar')) {
            $requiredPath = Join-Path $appDir $required
            if (-not (Test-Path -LiteralPath $requiredPath) -or (Get-Item -LiteralPath $requiredPath).Length -le 0) {
                throw "Staged Discord repair is missing $required"
            }
        }
        $restoredSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $appDir 'Discord.exe') -ErrorAction Stop
        if ($restoredSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            -not $restoredSignature.SignerCertificate -or
            $restoredSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Discord Inc\.(?:,|$)') {
            throw 'Restored Discord executable signature is invalid'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $packagesDir 'RELEASES')) -or
            (Get-Item -LiteralPath (Join-Path $DiscordRoot 'installer.db')).Length -lt 4096) {
            throw 'Staged Discord repair did not restore Squirrel package state'
        }
        return (Get-Item -LiteralPath $appDir)
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-RepairFreshDiscord([string]$DiscordRoot, [string]$Setup) {
    if (-not (Test-Path -LiteralPath $Setup)) {
        throw 'Discord installer is missing - download it from discord.com manually'
    }
    try {
        Write-RepStep 'Installing Discord (silent)...'
        $p = Start-Process -FilePath $Setup -ArgumentList '-s' -PassThru -WindowStyle Hidden
        if ($null -eq $p) { throw 'Failed to start Discord installer' }
        if (-not $p.WaitForExit(300000)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            throw 'Discord installer timed out after 5 minutes'
        }
        $deadline = (Get-Date).AddSeconds(180)
        while ((Get-Date) -lt $deadline) {
            $app = Get-RepairActiveApp $DiscordRoot
            if ($app -and (Test-Path -LiteralPath (Join-Path $app.FullName 'Discord.exe'))) {
                return $app
            }
            Start-Sleep -Seconds 3
        }
        throw 'Discord install did not complete - run the installer from discord.com manually'
    } finally {
        Remove-Item -LiteralPath $Setup -Force -ErrorAction SilentlyContinue
    }
}

function Start-RepairDiscord([string]$DiscordRoot, [string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (Test-Path -LiteralPath $exe) {
        # Launch via explorer so Discord does not inherit an elevated token.
        try {
            Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" | Out-Null
            return
        } catch { }
        Start-Process -FilePath $exe -WorkingDirectory $AppDir | Out-Null
        return
    }
    $updateExe = Join-Path $DiscordRoot 'Update.exe'
    if (Test-Path -LiteralPath $updateExe) {
        Start-Process -FilePath $updateExe -ArgumentList '--processStart', 'Discord.exe' -WorkingDirectory $DiscordRoot | Out-Null
    }
}

function Confirm-RepairDiscordBoot([string]$DiscordRoot) {
    $rootPrefix = [IO.Path]::GetFullPath($DiscordRoot).TrimEnd('\') + '\'
    $deadline = (Get-Date).AddSeconds(30)
    $stableSeconds = 0
    while ((Get-Date) -lt $deadline) {
        $running = @(Get-Process -Name @('Discord', 'Discord.bin') -ErrorAction SilentlyContinue | Where-Object {
            try {
                $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
            } catch { $false }
        })
        if ($running.Count -gt 0) {
            $stableSeconds++
            if ($stableSeconds -ge 3) { return $true }
        } else {
            $stableSeconds = 0
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Restore-RepairDiscordShortcuts([string]$AppDir) {
    $exe = Join-Path $AppDir 'Discord.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return }
    $roots = @(
        [Environment]::GetFolderPath('Programs'),
        [Environment]::GetFolderPath('CommonPrograms'),
        [Environment]::GetFolderPath('Desktop'),
        [Environment]::GetFolderPath('CommonDesktopDirectory'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    $wsh = New-Object -ComObject WScript.Shell
    $restored = 0
    foreach ($root in $roots) {
        foreach ($link in @(Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -Force -ErrorAction SilentlyContinue)) {
            try {
                $shortcut = $wsh.CreateShortcut($link.FullName)
                if ([string]$shortcut.TargetPath -notmatch '(?i)wscript\.exe$' -or
                    [string]$shortcut.Arguments -notmatch '(?i)Discord\.vbs') { continue }
                $shortcut.TargetPath = $exe
                $shortcut.Arguments = ''
                $shortcut.WorkingDirectory = $AppDir
                $shortcut.IconLocation = "$exe,0"
                $shortcut.Description = 'Discord'
                $shortcut.Save()
                $restored++
            } catch { }
        }
    }
    if ($restored -gt 0) { Write-RepOk "Restored $restored Discord shortcut(s) to the stock client" }
}

function Get-RepairDiscordStatePath {
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Exo'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Join-Path $dir 'discord-optimizer.json')
}

function Read-RepairDiscordState {
    $path = Get-RepairDiscordStatePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Save-RepairDiscordState([hashtable]$State) {
    $path = Get-RepairDiscordStatePath
    $temp = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $State | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($temp, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $path -Force
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Restore-RepairRegistryValue($Entry) {
    if (-not (Test-Path $Entry.Key)) { New-Item -Path $Entry.Key -Force -ErrorAction Stop | Out-Null }
    $kind = if ([string]$Entry.Kind -in @('String', 'ExpandString', 'Binary', 'DWord', 'MultiString', 'QWord')) {
        [string]$Entry.Kind
    } else { 'String' }
    $value = switch ($kind) {
        'Binary' { [byte[]]$Entry.Value; break }
        'DWord' { [int]$Entry.Value; break }
        'QWord' { [long]$Entry.Value; break }
        'MultiString' { [string[]]$Entry.Value; break }
        default { [string]$Entry.Value }
    }
    New-ItemProperty -Path $Entry.Key -Name ([string]$Entry.Name) -Value $value -PropertyType $kind -Force -ErrorAction Stop | Out-Null
    $item = Get-Item -Path $Entry.Key -ErrorAction Stop
    if ($item.GetValueNames() -notcontains [string]$Entry.Name) { throw 'registry value missing after restore' }
    $actual = $item.GetValue([string]$Entry.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($item.GetValueKind([string]$Entry.Name).ToString() -ne $kind -or
        (($actual | ConvertTo-Json -Compress -Depth 4) -ne ($value | ConvertTo-Json -Compress -Depth 4))) {
        throw 'registry value verification failed'
    }
}

function Restore-RepairWindowsTweaks([string]$DiscordRoot, $Recovery, [ref]$Failures) {
    Write-RepStep 'Restoring captured stable Discord Windows integration...'
    if (-not $Recovery) {
        Write-RepWarn 'No Windows recovery snapshot exists (older optimizer state); unrelated integration was left untouched'
        return
    }

    foreach ($entry in @($Recovery.RunEntries) + @($Recovery.StartupApproved)) {
        try {
            Restore-RepairRegistryValue $entry
            Write-RepOk "Restored registry value: $($entry.Name)"
        } catch { $Failures.Value.Add("Registry $($entry.Name): $($_.Exception.Message)") }
    }

    $notificationRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    foreach ($entry in @($Recovery.Notifications)) {
        $path = Join-Path $notificationRoot ([string]$entry.Id)
        try {
            if ([bool]$entry.EnabledExisted) {
                Restore-RepairRegistryValue @{
                    Key = $path; Name = 'Enabled'; Value = $entry.EnabledValue; Kind = $entry.EnabledKind
                }
            } elseif (Test-Path $path) {
                Remove-ItemProperty -Path $path -Name 'Enabled' -Force -ErrorAction SilentlyContinue
                if ((Get-Item -Path $path -ErrorAction Stop).GetValueNames() -contains 'Enabled') {
                    throw 'Enabled value is still present'
                }
            }
            Write-RepOk "Restored notification state: $($entry.Id)"
        } catch { $Failures.Value.Add("Notification $($entry.Id): $($_.Exception.Message)") }
    }

    foreach ($entry in @($Recovery.ScheduledTasks)) {
        try {
            $task = Get-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction SilentlyContinue
            if (-not $task) {
                if ([string]::IsNullOrWhiteSpace([string]$entry.Xml)) { throw 'captured task XML is missing' }
                Register-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -Xml ([string]$entry.Xml) -Force -ErrorAction Stop | Out-Null
            }
            if ([bool]$entry.Enabled) {
                Enable-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop | Out-Null
            } else {
                Disable-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop | Out-Null
            }
            $verified = Get-ScheduledTask -TaskName $entry.TaskName -TaskPath $entry.TaskPath -ErrorAction Stop
            if ([bool]$verified.Settings.Enabled -ne [bool]$entry.Enabled) { throw 'task enabled state verification failed' }
            Write-RepOk "Restored task state: $($entry.TaskPath)$($entry.TaskName)"
        } catch { $Failures.Value.Add("Task $($entry.TaskPath)$($entry.TaskName): $($_.Exception.Message)") }
    }

    foreach ($entry in @($Recovery.TrayEntries)) {
        try {
            if (-not (Test-Path $entry.Key)) { throw 'captured tray key no longer exists' }
            if ([bool]$entry.IsPromotedExisted) {
                Restore-RepairRegistryValue @{
                    Key = $entry.Key; Name = 'IsPromoted'; Value = $entry.IsPromotedValue; Kind = $entry.IsPromotedKind
                }
            } else {
                Remove-ItemProperty -Path $entry.Key -Name 'IsPromoted' -Force -ErrorAction SilentlyContinue
                if ((Get-Item -Path $entry.Key -ErrorAction Stop).GetValueNames() -contains 'IsPromoted') {
                    throw 'IsPromoted value is still present'
                }
            }
            Write-RepOk "Restored tray state: $($entry.ExecutablePath)"
        } catch { $Failures.Value.Add("Tray $($entry.ExecutablePath): $($_.Exception.Message)") }
    }

    foreach ($entry in @($Recovery.Compatibility)) {
        try {
            if ([bool]$entry.Existed) {
                Restore-RepairRegistryValue $entry
            } elseif (Test-Path $entry.Key) {
                Remove-ItemProperty -Path $entry.Key -Name $entry.Name -Force -ErrorAction SilentlyContinue
                if ((Get-Item -Path $entry.Key -ErrorAction Stop).GetValueNames() -contains [string]$entry.Name) {
                    throw 'compatibility value is still present'
                }
            }
            Write-RepOk "Restored compatibility state: $($entry.Name)"
        } catch { $Failures.Value.Add("Compatibility $($entry.Name): $($_.Exception.Message)") }
    }
}

function Remove-ExoDiscordQosPolicies {
    # Exo-created voice QoS policies use fixed documented names - always safe to
    # remove on repair (they never exist unless Exo created them). The recovery
    # snapshot (recovery.QosPolicies) records the same names.
    $root = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS'
    $removed = 0
    foreach ($name in @('Exo Discord Voice', 'Exo Discord PTB Voice', 'Exo Discord Canary Voice')) {
        $path = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $path) { throw 'policy key is still present' }
            $removed++
            Write-RepOk "Removed QoS policy: $name"
        } catch {
            Write-RepWarn "QoS policy $name`: $($_.Exception.Message)"
        }
    }
    if ($removed -eq 0) { Write-RepOk 'No Exo QoS policies to remove' }
    return $removed
}

function Restore-ExoDiscordVariantSettings([string]$AppDataRoot) {
    # PTB / Canary: strip Exo-written boot flags so test channels return to stock.
    # Full program reinstall for variants stays a manual step (test-channel data
    # safety); removing our flags restores stock behavior deterministically.
    foreach ($dir in @('discordptb', 'discordcanary')) {
        $settings = Join-Path $AppDataRoot (Join-Path $dir 'settings.json')
        if (-not (Test-Path -LiteralPath $settings)) { continue }
        try {
            attrib -R $settings 2>$null
            $sj = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
            $names = @($sj.PSObject.Properties.Name)
            $dropped = 0
            foreach ($key in @(
                'chromiumSwitches',
                'DESKTOP_TTI_EARLY_UPDATE_CHECK', 'DESKTOP_TTI_DNSTCP_WARMUP',
                'DESKTOP_TTI_REMOVE_V8_CACHE_CLEAR', 'DESKTOP_TTI_UPDATE_BACKOFF_MAX_MS'
            )) {
                if ($names -contains $key) {
                    $sj.PSObject.Properties.Remove($key)
                    $dropped++
                }
            }
            if ($dropped -gt 0) {
                [IO.File]::WriteAllText($settings, ($sj | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
                Write-RepOk "Restored stock boot flags: $dir ($dropped key(s) removed)"
            }
        } catch {
            Write-RepWarn "Variant settings $dir`: $($_.Exception.Message)"
        }
    }
}

function Remove-BrokenThemes([string]$AppDataRoot) {
    $equicordThemes = Join-Path $AppDataRoot 'Equicord\themes'
    if (-not (Test-Path -LiteralPath $equicordThemes)) { return }
    Get-ChildItem -LiteralPath $equicordThemes -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'discopt-amoled*' -or $_.Name -like 'amoled-cord*' } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            Write-RepOk "Removed theme: $($_.Name)"
        }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Repair must run on Windows.'
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    $appData = [Environment]::GetEnvironmentVariable('APPDATA')
    if (-not $localAppData -or -not $appData) {
        throw 'LOCALAPPDATA/APPDATA environment variables are not set.'
    }

    $discordRoot = Join-Path $localAppData 'Discord'
    $appDataDiscord = Join-Path $appData 'discord'
    $optimizerState = Read-RepairDiscordState
    $recovery = if ($optimizerState -and ($optimizerState.PSObject.Properties.Name -contains 'recovery')) {
        $optimizerState.recovery
    } else { $null }
    $repairFailures = [Collections.Generic.List[string]]::new()
    if ($optimizerState) {
        Save-RepairDiscordState @{
            version          = '1.3.0'
            applyStatus      = 'repairing'
            applied          = $false
            recovery         = $recovery
            repairFailures   = @()
            repairStartedUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    $doFull = $FullReset -or
        ([Environment]::GetEnvironmentVariable('EXO_REPAIR_FULL') -eq '1') -or
        ([Environment]::GetEnvironmentVariable('DISCOPT_REPAIR_FULL') -eq '1')
    $repairSetup = $null

    Write-Host ''
    Write-Host '  Exo - Discord Clean Reset' -ForegroundColor Cyan
    Write-Host '  Stock reinstall + cache purge. Login preserved by default.' -ForegroundColor DarkGray
    Write-Host ''

    Write-HubProgress 5 'Closing Discord...'
    Write-RepStep 'Closing Discord...'
    Stop-RepairDiscord $discordRoot

    Write-HubProgress 20 'Downloading Discord installer...'
    $repairSetup = Get-RepairVerifiedDiscordSetup

    Write-HubProgress 35 'Removing program files...'
    $reuseSignedStockExecutable = [bool](Remove-RepairProgramFiles $discordRoot)

    Write-HubProgress 45 'Clearing renderer state...'
    Clear-RepairRendererState $appDataDiscord $doFull
    Remove-BrokenThemes $appData

    Write-HubProgress 55 'Restoring stock Discord...'
    if ($reuseSignedStockExecutable) {
        $app = Expand-RepairDiscordFromSignedSetup $discordRoot $repairSetup
        Remove-Item -LiteralPath $repairSetup -Force -ErrorAction SilentlyContinue
        $repairSetup = $null
        Write-RepOk "Discord $($app.Name) stock runtime restored from the signed installer"
    } else {
        $app = Install-RepairFreshDiscord $discordRoot $repairSetup
        $repairSetup = $null
        Write-RepOk "Discord $($app.Name) installed clean"
    }

    Write-HubProgress 82 'Restoring stock shortcuts...'
    Restore-RepairDiscordShortcuts $app.FullName
    Write-HubProgress 83 'Restoring Windows integration...'
    Restore-RepairWindowsTweaks $discordRoot $recovery ([ref]$repairFailures)
    Write-HubProgress 84 'Removing Exo QoS policies / variant flags...'
    [void](Remove-ExoDiscordQosPolicies)
    Restore-ExoDiscordVariantSettings $appData
    if ($repairFailures.Count -gt 0) {
        Save-RepairDiscordState @{
            version        = '1.3.0'
            applyStatus    = 'repair-pending'
            applied        = $false
            recovery       = $recovery
            repairFailures = @($repairFailures)
            lastRepairUtc  = (Get-Date).ToUniversalTime().ToString('o')
        }
        throw "Windows integration restore incomplete ($($repairFailures.Count) item(s)); recovery state was kept for retry"
    }

    Write-HubProgress 85 'Starting Discord...'
    Write-RepStep 'Starting Discord...'
    Start-RepairDiscord $discordRoot $app.FullName
    if ($reuseSignedStockExecutable -and -not (Confirm-RepairDiscordBoot $discordRoot)) {
        throw 'Discord did not stay running after the staged stock repair'
    }

    $statePath = Get-RepairDiscordStatePath
    if (Test-Path -LiteralPath $statePath) {
        Remove-Item -LiteralPath $statePath -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $statePath) { throw 'Could not clear Discord recovery state' }
        Write-RepOk 'Cleared Exo Discord recovery state'
    }

    Write-RepOk 'Repair complete. Wait for Discord to finish loading.'
    Write-HubProgress 100 'Repair complete'
    exit 0
} catch {
    $failureRecord = $_
    if ($repairSetup) {
        try { Remove-Item -LiteralPath $repairSetup -Force -ErrorAction SilentlyContinue } catch { }
    }
    try {
        $failedState = Read-RepairDiscordState
        if ($failedState) {
            $failedRecovery = if ($failedState.PSObject.Properties.Name -contains 'recovery') { $failedState.recovery } else { $recovery }
            $recordedFailures = if ($failedState.PSObject.Properties.Name -contains 'repairFailures') {
                @($failedState.repairFailures)
            } else { @() }
            Save-RepairDiscordState @{
                version        = '1.3.0'
                applyStatus    = 'repair-pending'
                applied        = $false
                recovery       = $failedRecovery
                repairFailures = @($recordedFailures) + @([string]$failureRecord.Exception.Message)
                lastRepairUtc  = (Get-Date).ToUniversalTime().ToString('o')
            }
        }
    } catch { }
    Write-RepErr 'Repair failed.'
    Write-RepErr $failureRecord.Exception.Message
    Write-HubProgress 100 'Repair failed'
    Write-Host 'Manual fallback: https://discord.com/download' -ForegroundColor Yellow
    exit 1
}
