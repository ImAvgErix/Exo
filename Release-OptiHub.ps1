#Requires -Version 5.1
<#
.SYNOPSIS
  Publish OptiHub and create a single GitHub Release that is the installer Latest.
  Verifies GET /repos/{repo}/releases/latest — tags alone are NOT enough.
  Deletes every older release + orphan tags after Latest is confirmed.

.EXAMPLE
  .\Release-OptiHub.ps1
#>
param(
    [string]$Configuration = 'Release',
    [string]$Repo = 'BarcusEric/OptiHub',
    [string]$NotesFile = '',
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$env:Path = "C:\Program Files\GitHub CLI;C:\Program Files\Git\cmd;C:\Program Files\dotnet;" + $env:Path

$VersionFile = Join-Path $Root 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '1.0.0' }
$Tag = "v$Version"
$ReleaseDir = Join-Path $Root 'release'
$ZipPath = Join-Path $ReleaseDir "OptiHub-$Version-win-x64.zip"
$PayloadZip = Join-Path $ReleaseDir 'optihub-build.zip'
$SfxPath = Join-Path $ReleaseDir 'OptiHub.exe'

function Get-LatestReleaseInfo {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{
        'User-Agent' = 'OptiHub-Release/1.0'
        'Accept'     = 'application/vnd.github+json'
    }
}

function Test-LatestIsTag([string]$ExpectedTag) {
    $latest = Get-LatestReleaseInfo
    $assetNames = @($latest.assets | ForEach-Object { $_.name })
    # Prefer double-click OptiHub.exe; zip kept for the PowerShell installer.
    $hasExe = ($assetNames -contains 'OptiHub.exe')
    $hasZip = ($assetNames -contains 'optihub-build.zip')
    [pscustomobject]@{
        Tag    = $latest.tag_name
        Assets = $assetNames
        Ok     = ($latest.tag_name -eq $ExpectedTag -and $hasExe -and $hasZip)
    }
}

function Get-AllReleaseTags {
    # Prefer API JSON over `gh release list` text parsing (Latest column is empty for non-latest).
    $json = gh api "repos/$Repo/releases?per_page=100" 2>$null
    if (-not $json) { return @() }
    $rels = $json | ConvertFrom-Json
    return @($rels | ForEach-Object { $_.tag_name } | Where-Object { $_ })
}

function Remove-OldReleasesAndTags([string]$KeepTag) {
    Write-Host "[*] Deleting older releases (keeping $KeepTag)..." -ForegroundColor Cyan

    $tags = Get-AllReleaseTags
    foreach ($old in $tags) {
        if ($old -eq $KeepTag) { continue }
        Write-Host "    delete release $old" -ForegroundColor DarkGray
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        gh release delete $old --repo $Repo --yes --cleanup-tag 2>&1 | Out-Null
        $ErrorActionPreference = $prev
    }

    # Prune leftover tags that are not the keep tag (orphans from failed creates)
    $prev2 = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $remoteTags = @(git ls-remote --tags origin 2>$null | ForEach-Object {
        if ($_ -match 'refs/tags/(v[\w\.\-]+)$') { $Matches[1] }
    } | Sort-Object -Unique)
    foreach ($t in $remoteTags) {
        if ($t -eq $KeepTag) { continue }
        # Only prune OptiHub version tags (v1.x.x), not unrelated tags
        if ($t -notmatch '^v\d+\.\d+\.\d+') { continue }
        Write-Host "    delete orphan tag $t" -ForegroundColor DarkGray
        git push origin ":refs/tags/$t" 1>$null 2>$null
        git tag -d $t 1>$null 2>$null
    }
    $ErrorActionPreference = $prev2
}

Write-Host ''
Write-Host "  OptiHub release  ·  $Tag" -ForegroundColor Cyan
Write-Host ''

& (Join-Path $Root 'Publish-OptiHub.ps1') -Configuration $Configuration
if (-not (Test-Path $ZipPath)) {
    throw "Missing publish zip: $ZipPath"
}
if (-not (Test-Path $SfxPath)) {
    throw "Missing self-extracting OptiHub.exe: $SfxPath"
}
Copy-Item -LiteralPath $ZipPath -Destination $PayloadZip -Force

if ($NotesFile -and (Test-Path $NotesFile)) {
    $body = (Get-Content $NotesFile -Raw).Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($Notes)) {
    $body = $Notes.Trim()
} else {
    $body = @"
## What's new in $Version

Self-extracting **OptiHub.exe** on the release (double-click install).

## Download

- **OptiHub.exe** — double-click installer (recommended)
- ``optihub-build.zip`` — portable folder (used by the PowerShell one-liner)

## Install / update

**Option A — exe:** download ``OptiHub.exe`` from this release and run it.

**Option B — PowerShell:**

``````powershell
irm "https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1" | iex
``````

Installs into ``%LocalAppData%\OptiHub\app`` and launches OptiHub.
"@
}

if ($body -notmatch 'Install-OptiHub\.ps1') {
    $body += @"

## Install / update

Download **OptiHub.exe** from this release and double-click it, or:

``````powershell
irm "https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1" | iex
``````
"@
}

# Recreate this tag's release if it already exists. Do NOT delete other releases first —
# that can leave /releases/latest stuck on an older published release.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $Tag --repo $Repo 1>$null 2>$null
$tagReleaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap

if ($tagReleaseExists) {
    Write-Host "[*] Deleting existing release $Tag before recreate" -ForegroundColor DarkGray
    $ErrorActionPreference = 'Continue'
    gh release delete $Tag --repo $Repo --yes 2>$null
    $ErrorActionPreference = $prevEap
}

$ErrorActionPreference = 'Continue'
git tag -d $Tag 1>$null 2>$null
git push origin ":refs/tags/$Tag" 1>$null 2>$null
$ErrorActionPreference = $prevEap

Write-Host "[*] Creating GitHub Release $Tag with OptiHub.exe + optihub-build.zip (--latest)..." -ForegroundColor Cyan
# OptiHub.exe first so it appears as the primary download on GitHub.
gh release create $Tag $SfxPath $PayloadZip `
    --repo $Repo `
    --title "OptiHub $Version" `
    --notes $body `
    --latest `
    --target main
if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Tag" }

Write-Host "[*] Verifying API /releases/latest == $Tag + OptiHub.exe + zip ..." -ForegroundColor Cyan
$ok = $false
$last = $null
for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Seconds 2
    try {
        $last = Test-LatestIsTag $Tag
        Write-Host ("    attempt $i : tag=$($last.Tag) assets=$($last.Assets -join ',')" ) -ForegroundColor DarkGray
        if ($last.Ok) { $ok = $true; break }
    } catch {
        Write-Host ("    attempt $i : $($_.Exception.Message)") -ForegroundColor DarkGray
    }
}

if (-not $ok) {
    throw "RELEASE VERIFY FAILED: /releases/latest is '$($last.Tag)' not '$Tag' with OptiHub.exe + optihub-build.zip. A tag alone is NOT a release."
}

# Only after Latest is confirmed, remove older releases + orphan tags
Remove-OldReleasesAndTags -KeepTag $Tag

# Re-verify after cleanup (deletes must not demote Latest)
$last = $null
$ok = $false
for ($i = 1; $i -le 8; $i++) {
    Start-Sleep -Seconds 2
    try {
        $last = Test-LatestIsTag $Tag
        Write-Host ("    post-cleanup $i : tag=$($last.Tag) assets=$($last.Assets -join ',')" ) -ForegroundColor DarkGray
        if ($last.Ok) { $ok = $true; break }
    } catch {
        Write-Host ("    post-cleanup $i : $($_.Exception.Message)") -ForegroundColor DarkGray
    }
}
if (-not $ok) {
    throw "RELEASE VERIFY FAILED after cleanup: /releases/latest is '$($last.Tag)' not '$Tag'."
}

$remaining = Get-AllReleaseTags
$extra = @($remaining | Where-Object { $_ -ne $Tag })
if ($extra.Count -gt 0) {
    throw "CLEANUP FAILED: still have old releases: $($extra -join ', ')"
}

Write-Host ''
Write-Host "[+] VERIFIED Latest release: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "    API /releases/latest = $Tag + OptiHub.exe + optihub-build.zip" -ForegroundColor Green
Write-Host "    Old releases deleted (only $Tag remains)" -ForegroundColor Green
Write-Host "    Download: https://github.com/$Repo/releases/latest/download/OptiHub.exe" -ForegroundColor DarkGray
Write-Host "    Or: irm `"https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1`" | iex" -ForegroundColor DarkGray
Write-Host ''
