#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$NonInteractive,
    [switch]$Experimental
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Windows-Optimizer.ps1') -NonInteractive:$NonInteractive -Experimental:$Experimental
exit $LASTEXITCODE
