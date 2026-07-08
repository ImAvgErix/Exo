#Requires -Version 5.1
<#
.SYNOPSIS
  Publish OptiHub and create a single GitHub Release (changelog + PowerShell install paste).
  Deletes older releases. Uploads optihub-build.zip for Install-OptiHub.ps1 only.

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

- (add changelog bullets here)

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

$existing = gh release list --repo $Repo --limit 50 2>$null
if ($existing) {
    foreach ($line in ($existing -split "`n")) {
        if ($line -match '\t(v[\w\.\-]+)\t') {
            $old = $Matches[1]
            if ($old -ne $Tag) {
                Write-Host "[*] Deleting old release $old" -ForegroundColor DarkGray
                gh release delete $old --repo $Repo --yes --cleanup-tag 2>$null
            }
        }
    }
}

# gh writes "release not found" to stderr; don't let Stop kill the script
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh release view $Tag --repo $Repo 1>$null 2>$null
$tagExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap

if ($tagExists) {
    Write-Host "[*] Replacing existing $Tag" -ForegroundColor DarkGray
    gh release delete $Tag --repo $Repo --yes --cleanup-tag 2>$null
}

git tag -d $Tag 2>$null | Out-Null
git tag -a $Tag -m "OptiHub $Version"
git push origin $Tag --force 2>&1 | Out-Null

gh release create $Tag $PayloadZip `
    --repo $Repo `
    --title "OptiHub $Version" `
    --notes $body

Write-Host ''
Write-Host "[+] Release: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host '    Users install via PowerShell paste.' -ForegroundColor DarkGray
Write-Host ''
