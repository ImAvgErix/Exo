# DEPRECATED - full CPL automation retired. Forward to Exo-Display-Apply.ps1
$ErrorActionPreference = 'Continue'
$apply = Join-Path $PSScriptRoot 'Exo-Display-Apply.ps1'
Write-Host '[CPL] UI automation retired - using Exo-Display-Apply.ps1'
if (-not (Test-Path $apply)) { Write-Host '[CPL] FATAL: missing Exo-Display-Apply.ps1'; exit 1 }
& $apply
exit $LASTEXITCODE
