# Poll origin/main every 2 min; exit 0 when tip changes
param([string]$Repo='C:\Users\Erix\Exo',[string]$Baseline='')
$ErrorActionPreference='Stop'
$env:PATH = "$env:LOCALAPPDATA\Programs\Cua\cua-driver\bin;" + $env:PATH
Set-Location $Repo
if (-not $Baseline) { $Baseline = (git rev-parse origin/main).Trim() }
Write-Host "Watching origin/main starting from $Baseline"
for ($i=0; $i -lt 90; $i++) {
  git fetch origin --quiet 2>$null
  $tip = (git rev-parse origin/main).Trim()
  $msg = (git log -1 --oneline origin/main)
  Write-Host ("[{0}] origin/main={1} {2}" -f (Get-Date -Format 'HH:mm:ss'), $tip.Substring(0,7), $msg)
  if ($tip -ne $Baseline) {
    Write-Host "NEW VERSION DETECTED: $tip"
    git log --oneline $Baseline..origin/main
    exit 0
  }
  Start-Sleep -Seconds 120
}
Write-Host "No new version within watch window"
exit 2
