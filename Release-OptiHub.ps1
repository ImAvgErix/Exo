#Requires -Version 5.1
<#
.SYNOPSIS
  Publish OptiHub and create a GitHub Release that is the installer Latest.
  Verifies GET /repos/{repo}/releases/latest — tags alone are NOT enough.

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

function Get-LatestReleaseInfo {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{
        'User-Agent' = 'OptiHub-Release/1.0'
        'Accept'     = 'application/vnd.github+json'
    }
}

function Test-LatestIsTag([string]$ExpectedTag) {
    $latest = Get-LatestReleaseInfo
    $assetNames = @($latest.assets | ForEach-Object { $_.name })
    [pscustomobject]@{
        Tag    = $latest.tag_name
        Assets = $assetNames
        Ok     = ($latest.tag_name -eq $ExpectedTag -and ($assetNames -contains 'optihub-build.zip'))
    }
}

Write-Host ''
Write-Host "  OptiHub release  ·  $Tag" -ForegroundColor Cyan
Write-Host ''

& (Join-Path $Root 'Publish-OptiHub.ps1') -Configuration $Configuration
if (-not (Test-Path $ZipPath)) {
    throw "Missing publish zip: $ZipPath"
}
Copy-Item -LiteralPath $ZipPath -Destination $PayloadZip -Force

if ($NotesFile -and (Test-Path $NotesFile)) {
    $body = (Get-Content $NotesFile -Raw).Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($Notes)) {
    $body = $Notes.Trim()
} else {
    $body = @"
## What's new in $Version

- Repair Discord installs missing resources/app.asar (reinstall instead of failing)
- Direct Equicord/OpenASAR path (no Equilot hang)
- Release script verifies GitHub /releases/latest, not just tags

## Install / update

Paste this into PowerShell:

``````powershell
irm "https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1" | iex
``````

That downloads the latest build into ``%LocalAppData%\OptiHub\app`` and launches OptiHub.
"@
}

if ($body -notmatch 'Install-OptiHub\.ps1') {
    $body += @"

## Install / update

Paste this into PowerShell:

``````powershell
irm "https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1" | iex
``````
"@
}

# Replace this tag's release if it already exists (do NOT delete other releases first —
# that can leave /releases/latest stuck on an older published release).
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $Tag --repo $Repo 1>$null 2>$null
$tagReleaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap

if ($tagReleaseExists) {
    Write-Host "[*] Deleting existing release $Tag before recreate" -ForegroundColor DarkGray
    gh release delete $Tag --repo $Repo --yes 2>$null
}

$ErrorActionPreference = 'Continue'
git tag -d $Tag 1>$null 2>$null
git push origin ":refs/tags/$Tag" 1>$null 2>$null
$ErrorActionPreference = $prevEap

Write-Host "[*] Creating GitHub Release $Tag with optihub-build.zip (--latest)..." -ForegroundColor Cyan
gh release create $Tag $PayloadZip `
    --repo $Repo `
    --title "OptiHub $Version" `
    --notes $body `
    --latest `
    --target main
if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Tag" }

Write-Host "[*] Verifying API /releases/latest == $Tag + optihub-build.zip ..." -ForegroundColor Cyan
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
    throw "RELEASE VERIFY FAILED: /releases/latest is '$($last.Tag)' not '$Tag' with optihub-build.zip. A tag alone is NOT a release."
}

# Only after Latest is confirmed, remove older releases so Latest stays unambiguous
$existing = gh release list --repo $Repo --limit 50 2>$null
if ($existing) {
    foreach ($line in ($existing -split "`n")) {
        if ($line -match '\t(v[\w\.\-]+)\t') {
            $old = $Matches[1]
            if ($old -eq $Tag) { continue }
            Write-Host "[*] Deleting old release $old" -ForegroundColor DarkGray
            gh release delete $old --repo $Repo --yes --cleanup-tag 2>$null
        }
    }
}

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

Write-Host ''
Write-Host "[+] VERIFIED Latest release: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "    API /releases/latest = $Tag + optihub-build.zip" -ForegroundColor Green
Write-Host "    Install: irm `"https://raw.githubusercontent.com/$Repo/main/Install-OptiHub.ps1`" | iex" -ForegroundColor DarkGray
Write-Host ''
