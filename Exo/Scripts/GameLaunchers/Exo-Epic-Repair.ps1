[CmdletBinding()] param([switch]$NonInteractive)
& (Join-Path $PSScriptRoot 'GameLauncher-Optimizer.ps1') -Module Epic -Repair -NonInteractive:$NonInteractive
exit $LASTEXITCODE
