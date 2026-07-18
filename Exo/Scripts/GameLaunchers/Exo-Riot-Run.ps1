[CmdletBinding()] param([switch]$NonInteractive)
& (Join-Path $PSScriptRoot 'GameLauncher-Optimizer.ps1') -Module Riot -NonInteractive:$NonInteractive
exit $LASTEXITCODE
