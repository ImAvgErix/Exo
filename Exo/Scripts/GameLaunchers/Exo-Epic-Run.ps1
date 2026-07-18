[CmdletBinding()] param([switch]$NonInteractive)
& (Join-Path $PSScriptRoot 'GameLauncher-Optimizer.ps1') -Module Epic -NonInteractive:$NonInteractive
exit $LASTEXITCODE
