# Legacy redirect -> Install-OptiHub.ps1
$ErrorActionPreference = "Stop"
Write-Host ""
Write-Host "  Redirecting to OptiHub installer..." -ForegroundColor Cyan
Write-Host ""
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Install-OptiHub.ps1" | iex