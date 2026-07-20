#Requires -Version 7.0
[CmdletBinding()]
param([switch]$NonInteractive)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Windows-Optimizer.ps1') -Repair -NonInteractive:$NonInteractive
exit $LASTEXITCODE
