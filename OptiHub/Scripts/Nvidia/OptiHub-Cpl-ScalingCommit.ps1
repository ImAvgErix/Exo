# DEPRECATED - mouse/keyboard Control Panel automation was unsafe and locale-dependent.
# Keep this entry point for older callers, but use the NVAPI + registry display path.
$ErrorActionPreference = 'Stop'
$apply = Join-Path $PSScriptRoot 'OptiHub-Display-Apply.ps1'
Write-Host '[SCALE] Control Panel automation retired - using the NVAPI display path'
if (-not (Test-Path -LiteralPath $apply)) {
    Write-Error 'OptiHub-Display-Apply.ps1 is missing'
    exit 1
}

& $apply
exit $LASTEXITCODE
