#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Exo and create a GitHub Release with ONLY Exo.exe (double-click install).
#>
param(
    [string]$Configuration = 'Release',
    [string]$Repo = 'ImAvgErix/Exo',
    [string]$NotesFile = '',
    [string]$Notes = '',
    [switch]$ReplaceExisting,
    [switch]$PruneOldReleases
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$env:Path = "C:\Program Files\GitHub CLI;C:\Program Files\Git\cmd;C:\Program Files\dotnet;" + $env:Path

$insideWorkTree = git -C $Root rev-parse --is-inside-work-tree 2>$null
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
    throw 'Releases must be created from a Git worktree.'
}
$dirty = @(git -C $Root status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
    throw "Release refused: commit or remove every modified/untracked file first.`n$($dirty -join "`n")"
}
$branch = (git -C $Root branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') {
    throw "Release refused: expected branch 'main', current branch is '$branch'."
}
git -C $Root fetch origin main --quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not refresh origin/main before release.' }
$HeadSha = (git -C $Root rev-parse HEAD).Trim()
$RemoteMainSha = (git -C $Root rev-parse origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or $HeadSha -ne $RemoteMainSha) {
    throw "Release refused: local main ($HeadSha) does not match origin/main ($RemoteMainSha)."
}

$VersionFile = Join-Path $Root 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { '1.0.0' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must contain an exact semantic version (x.y.z); got '$Version'."
}
$Tag = "v$Version"
$ReleaseDir = Join-Path $Root 'release'
$SfxPath = Join-Path $ReleaseDir 'Exo.exe'

function Get-LatestReleaseInfo {
    $headers = @{
        'User-Agent' = 'Exo-Release/1.0'
        'Accept'     = 'application/vnd.github+json'
    }
    # CI runners share anonymous API rate limits; authenticate when a token is available.
    if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}

function Test-LatestIsTag([string]$ExpectedTag, [string]$ExpectedSha256) {
    $latest = Get-LatestReleaseInfo
    $asset = @($latest.assets) | Where-Object { $_.name -eq 'Exo.exe' } | Select-Object -First 1
    $assetNames = @($latest.assets | ForEach-Object { $_.name })
    $remoteSha256 = if ($asset -and ([string]$asset.digest) -match '^sha256:([0-9a-fA-F]{64})$') {
        $Matches[1].ToLowerInvariant()
    } else { '' }
    [pscustomobject]@{
        Tag       = $latest.tag_name
        Assets    = $assetNames
        Sha256    = $remoteSha256
        Ok        = ($latest.tag_name -eq $ExpectedTag -and $asset -and
            $remoteSha256 -eq $ExpectedSha256.ToLowerInvariant())
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
Write-Host "  Exo release  -  $Tag  -  Exo.exe only" -ForegroundColor Cyan
Write-Host ''

& (Join-Path $Root 'Publish-Exo.ps1') -Configuration $Configuration
if (-not (Test-Path $SfxPath)) {
    throw "Missing Exo.exe: $SfxPath"
}
$SfxSha256 = (Get-FileHash -LiteralPath $SfxPath -Algorithm SHA256).Hash.ToLowerInvariant()

if ($NotesFile -and (Test-Path $NotesFile)) {
    $body = (Get-Content $NotesFile -Raw).Trim()
} elseif (-not [string]::IsNullOrWhiteSpace($Notes)) {
    $body = $Notes.Trim()
} else {
    $body = @"
## Exo $Version

### Download
**[Exo.exe](https://github.com/$Repo/releases/latest/download/Exo.exe)** - double-click to install and launch.

Installs to ``%LocalAppData%\Exo\app``.

Windows 10 1809+ / Windows 11, 64-bit.

If Windows SmartScreen appears: **More info** -> **Run anyway** (unsigned local build).
"@
}

$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $Tag --repo $Repo 1>$null 2>$null
$tagReleaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap

if ($tagReleaseExists) {
    if (-not $ReplaceExisting) {
        throw "Release $Tag already exists. Bump VERSION, or pass -ReplaceExisting to intentionally recreate it."
    }
    Write-Host "[*] Deleting existing release $Tag before recreate" -ForegroundColor DarkGray
    $ErrorActionPreference = 'Continue'
    gh release delete $Tag --repo $Repo --yes 2>$null
    $ErrorActionPreference = $prevEap
}

$ErrorActionPreference = 'Continue'
git ls-remote --exit-code --tags origin "refs/tags/$Tag" 1>$null 2>$null
$remoteTagExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap
if ($remoteTagExists -and -not $ReplaceExisting) {
    throw "Remote tag $Tag already exists without a replaceable release. Bump VERSION, or pass -ReplaceExisting intentionally."
}

if ($ReplaceExisting) {
    $ErrorActionPreference = 'Continue'
    git tag -d $Tag 1>$null 2>$null
    git push origin ":refs/tags/$Tag" 1>$null 2>$null
    $ErrorActionPreference = $prevEap
}

Write-Host "[*] Creating GitHub Release $Tag with Exo.exe only..." -ForegroundColor Cyan
gh release create $Tag $SfxPath `
    --repo $Repo `
    --title "Exo $Version" `
    --notes $body `
    --latest `
    --target $HeadSha
if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Tag" }

Write-Host "[*] Verifying API /releases/latest == $Tag + Exo.exe ..." -ForegroundColor Cyan
$ok = $false
$last = $null
for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Seconds 2
    try {
        $last = Test-LatestIsTag $Tag $SfxSha256
        Write-Host ("    attempt $i : tag=$($last.Tag) assets=$($last.Assets -join ',') sha256=$($last.Sha256)" ) -ForegroundColor DarkGray
        if ($last.Ok) { $ok = $true; break }
    } catch {
        Write-Host ("    attempt $i : $($_.Exception.Message)") -ForegroundColor DarkGray
    }
}
if (-not $ok) {
    throw "RELEASE VERIFY FAILED: /releases/latest is '$($last.Tag)' without Exo.exe."
}

if ($PruneOldReleases) {
    Remove-OldReleasesAndTags -KeepTag $Tag
} else {
    Write-Host '[*] Preserving historical releases and tags (use -PruneOldReleases to remove them).' -ForegroundColor DarkGray
}

$last = $null
$ok = $false
for ($i = 1; $i -le 8; $i++) {
    Start-Sleep -Seconds 2
    try {
        $last = Test-LatestIsTag $Tag $SfxSha256
        Write-Host ("    post-cleanup $i : tag=$($last.Tag) assets=$($last.Assets -join ',') sha256=$($last.Sha256)" ) -ForegroundColor DarkGray
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
Write-Host "    Download: https://github.com/$Repo/releases/latest/download/Exo.exe" -ForegroundColor Green
Write-Host ''
