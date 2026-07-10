#Requires -Version 5.1
<#
.SYNOPSIS
  Bump OptiHub app + Discord kit VERSION files together.

.EXAMPLE
  .\tools\Bump-Version.ps1 -App 1.0.41
  .\tools\Bump-Version.ps1 -App 1.0.41 -Kit 1.1.17
#>
param(
    [Parameter(Mandatory)][string]$App,
    [string]$Kit = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

function Set-TextFile([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, ($Text.Trim() + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Host "[+] $Path -> $Text" -ForegroundColor Green
}

if ($App -notmatch '^\d+\.\d+\.\d+$') { throw "App version must be x.y.z (got $App)" }

Set-TextFile (Join-Path $Root 'VERSION') $App

$csproj = Join-Path $Root 'OptiHub\OptiHub.csproj'
$xml = Get-Content $csproj -Raw
if ($xml -notmatch '<Version>[^<]+</Version>') { throw 'OptiHub.csproj missing <Version>' }
$xml2 = [regex]::Replace($xml, '<Version>[^<]+</Version>', "<Version>$App</Version>")
[IO.File]::WriteAllText($csproj, $xml2, [Text.UTF8Encoding]::new($false))
Write-Host "[+] OptiHub.csproj Version=$App" -ForegroundColor Green

if (-not $Kit) {
    # Default: bump kit patch when app bumps
    $kitPath = Join-Path $Root 'OptiHub\Scripts\Discord\VERSION'
    $cur = if (Test-Path $kitPath) { (Get-Content $kitPath -Raw).Trim() } else { '1.0.0' }
    if ($cur -match '^(\d+)\.(\d+)\.(\d+)') {
        $Kit = "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)"
    } else {
        $Kit = '1.1.0'
    }
}

if ($Kit -notmatch '^\d+\.\d+\.\d+$') { throw "Kit version must be x.y.z (got $Kit)" }

Set-TextFile (Join-Path $Root 'OptiHub\Scripts\Discord\VERSION') $Kit

$opt = Join-Path $Root 'OptiHub\Scripts\Discord\Disc-Optimizer.ps1'
if (Test-Path $opt) {
    $raw = Get-Content $opt -Raw
    $raw2 = [regex]::Replace($raw, "\`$Script:DiscOptVersion = '[^']+'", "`$Script:DiscOptVersion = '$Kit'")
    [IO.File]::WriteAllText($opt, $raw2, [Text.UTF8Encoding]::new($false))
    Write-Host "[+] Disc-Optimizer DiscOptVersion=$Kit" -ForegroundColor Green
}

foreach ($update in @(
    @{
        Path = Join-Path $Root 'OptiHub\Models\AppSettings.cs'
        Pattern = 'DiscordKitVersion\s*\{\s*get;\s*set;\s*\}\s*=\s*"[^"]+"'
        Replacement = "DiscordKitVersion { get; set; } = `"$Kit`""
    },
    @{
        Path = Join-Path $Root 'OptiHub\Services\SettingsService.cs'
        Pattern = 'settings\.DiscordKitVersion\s*=\s*"[^"]+"'
        Replacement = "settings.DiscordKitVersion = `"$Kit`""
    }
)) {
    $raw = Get-Content -LiteralPath $update.Path -Raw
    $updated = [regex]::Replace($raw, $update.Pattern, $update.Replacement, 1)
    if ($updated -eq $raw) { throw "Version marker not found in $($update.Path)" }
    [IO.File]::WriteAllText($update.Path, $updated, [Text.UTF8Encoding]::new($false))
    Write-Host "[+] $($update.Path) DiscordKitVersion=$Kit" -ForegroundColor Green
}

Write-Host ''
Write-Host "App $App - Kit $Kit" -ForegroundColor Cyan
