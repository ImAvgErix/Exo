#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Exo and create a GitHub Release with ONLY Exo.exe (double-click install).
#>
param(
    [string]$Configuration = 'Release',
    [string]$Repo = 'ImAvgErix/Exo',
    [string]$NotesFile = '',
    [string]$Notes = ''
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
    throw "Release $Tag already exists. Releases are immutable; bump VERSION instead of replacing it."
}

$ErrorActionPreference = 'Continue'
git ls-remote --exit-code --tags origin "refs/tags/$Tag" 1>$null 2>$null
$remoteTagExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap
if ($remoteTagExists) {
    throw "Remote tag $Tag already exists. Release tags are immutable; bump VERSION."
}

$ChecksumPath = Join-Path $ReleaseDir 'Exo.exe.sha256'
"$SfxSha256  Exo.exe" | Set-Content -LiteralPath $ChecksumPath -Encoding Ascii -NoNewline

Write-Host "[*] Creating immutable GitHub Release $Tag..." -ForegroundColor Cyan
gh release create $Tag $SfxPath $ChecksumPath `
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


Write-Host ''
Write-Host "[+] VERIFIED Latest: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "    Download: https://github.com/$Repo/releases/latest/download/Exo.exe" -ForegroundColor Green
Write-Host ''
