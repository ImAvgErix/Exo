#Requires -Version 5.1
<#
.SYNOPSIS
  Legacy entry point. DiscOpti is now OptiHub — redirects to Install-OptiHub.ps1.

.EXAMPLE
  irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Install-DiscOptimizer.ps1" | iex
#>
param(
    [string]$Repo = 'BarcusEric/OptiHub',
    [string]$Branch = 'main',
    [Parameter(ValueFromRemainingArguments = $true)]
    $Remaining
)

$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host '  Disc Optimizer is now OptiHub' -ForegroundColor Cyan
Write-Host '  Redirecting to Install-OptiHub.ps1...' -ForegroundColor DarkGray
Write-Host ''

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$url = "https://raw.githubusercontent.com/$Repo/$Branch/Install-OptiHub.ps1"
$script = Invoke-WebRequest -Uri $url -UseBasicParsing -Headers @{ 'User-Agent' = 'OptiHub-LegacyRedirect/1.0' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('Install-OptiHub-' + [guid]::NewGuid().ToString('N') + '.ps1')
Set-Content -LiteralPath $temp -Value $script.Content -Encoding UTF8
try {
    & $temp -Repo $Repo
} finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
