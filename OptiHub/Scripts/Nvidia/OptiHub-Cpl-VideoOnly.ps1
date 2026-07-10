# DEPRECATED - Control Panel mouse automation for scaling/Override is retired.
# It could not reliably multi-monitor and was toggling Override off.
# Forward to OptiHub-Display-Apply.ps1 (registry + NVAPI, without UI automation).
$ErrorActionPreference = 'Continue'
$apply = Join-Path $PSScriptRoot 'OptiHub-Display-Apply.ps1'
Write-Host '[VIDEO] CPL mouse path retired - using OptiHub-Display-Apply.ps1'
if (-not (Test-Path $apply)) {
    Write-Host '[VIDEO] FATAL: OptiHub-Display-Apply.ps1 missing'
    exit 1
}
& $apply
exit $LASTEXITCODE
