# DEPRECATED — full CPL automation retired. Forward to OptiHub-Display-Apply.ps1
$ErrorActionPreference = 'Continue'
$apply = Join-Path $PSScriptRoot 'OptiHub-Display-Apply.ps1'
Write-Host '[CPL] UI automation retired — using OptiHub-Display-Apply.ps1'
if (-not (Test-Path $apply)) { Write-Host '[CPL] FATAL: missing OptiHub-Display-Apply.ps1'; exit 1 }
$env:OPTIHUB_DISPLAY_HARD_RELOAD = '1'
& $apply
exit $LASTEXITCODE
