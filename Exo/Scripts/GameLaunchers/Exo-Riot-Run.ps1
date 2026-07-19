[CmdletBinding()] param([switch]$NonInteractive, [switch]$Experimental)
& (Join-Path $PSScriptRoot 'GameLauncher-Optimizer.ps1') -Module Riot -NonInteractive:$NonInteractive -Experimental:$Experimental
exit $LASTEXITCODE
