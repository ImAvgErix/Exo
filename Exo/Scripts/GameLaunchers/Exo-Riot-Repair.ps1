[CmdletBinding()] param([switch]$NonInteractive)
& (Join-Path $PSScriptRoot 'GameLauncher-Optimizer.ps1') -Module Riot -Repair -NonInteractive:$NonInteractive
exit $LASTEXITCODE
