#Requires -Version 5.1
<#
.SYNOPSIS
  Publish OptiHub and create a GitHub Release with ONLY OptiHub.exe (double-click install).
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
    [pscustomobject]@{
        Tag    = $latest.tag_name
        Assets = $assetNames
        Ok     = ($latest.tag_name -eq $ExpectedTag -and ($assetNames -contains 'OptiHub.exe'))
    }
}

function Get-AllReleaseTags {
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

    $prev2 = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $remoteTags = @(git ls-remote --tags origin 2>$null | ForEach-Object {
        if ($_ -match 'refs/tags/(v[\w\.\-]+)$') { $Matches[1] }
    } | Sort-Object -Unique)
    foreach ($t in $remoteTags) {
        if ($t -eq $KeepTag) { continue }
        if ($t -notmatch '^v\d+\.\d+\.\d+') { continue }
        Write-Host "    delete orphan tag $t" -ForegroundColor DarkGray
        git push origin ":refs/tags/$t" 1>$null 2>$null
        git tag -d $t 1>$null 2>$null
    }
    $ErrorActionPreference = $prev2
}

Write-Host ''
Write-Host "  OptiHub release  ·  $Tag  ·  OptiHub.exe only" -ForegroundColor Cyan
Write-Host ''

& (Join-Path $Root 'Publish-OptiHub.ps1') -Configuration $Configuration
if (-not (Test-Path $SfxPath)) {
    throw "Missing OptiHub.exe: $SfxPath"
}

if ($NotesFile -and (Test-Path $NotesFile)) {
    $body = (Get-Content $NotesFile -Raw).Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($Notes)) {
    $body = $Notes.Trim()
} else {
    $body = @"
## OptiHub $Version

### Download
**[OptiHub.exe](https://github.com/$Repo/releases/latest/download/OptiHub.exe)** — double-click to install and launch.

Installs to ``%LocalAppData%\OptiHub\app``.

Windows 10 1809+ / Windows 11, 64-bit.

If Windows SmartScreen appears: **More info** → **Run anyway** (unsigned local build).
"@
}

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

Write-Host "[*] Creating GitHub Release $Tag with OptiHub.exe only..." -ForegroundColor Cyan
gh release create $Tag $SfxPath `
    --repo $Repo `
    --title "OptiHub $Version" `
    --notes $body `
    --latest `
    --target main
if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Tag" }

Write-Host "[*] Verifying API /releases/latest == $Tag + OptiHub.exe ..." -ForegroundColor Cyan
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
    throw "RELEASE VERIFY FAILED: /releases/latest is '$($last.Tag)' without OptiHub.exe."
}

Remove-OldReleasesAndTags -KeepTag $Tag

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
    throw "RELEASE VERIFY FAILED after cleanup: /releases/latest is '$($last.Tag)'."
}

Write-Host ''
Write-Host "[+] VERIFIED Latest: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "    Download: https://github.com/$Repo/releases/latest/download/OptiHub.exe" -ForegroundColor Green
Write-Host ''
