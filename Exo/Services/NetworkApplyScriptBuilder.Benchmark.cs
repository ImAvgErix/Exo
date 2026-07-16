using System.Text;

namespace Exo.Services;

public static partial class NetworkApplyScriptBuilder
{
    /// <summary>
    /// Non-elevated proof-layer benchmark: ping p50/p95 + jitter (10 pings each to
    /// 1.1.1.1 and 8.8.8.8) and average DNS resolve time. Prints exactly one
    /// EXO_BENCH:{json} line for <see cref="NetworkLogic.TryParseBenchmark"/>.
    /// </summary>
    public static string BuildBenchmark()
    {
        var sb = new StringBuilder(3_000);
        sb.AppendLine("""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function Get-ExoPingTimes([string]$Target) {
  $raw = (ping.exe -n 10 -w 1500 $Target 2>$null | Out-String)
  return @([regex]::Matches($raw, '[=<](\d+)\s*ms') | ForEach-Object { [double]$_.Groups[1].Value })
}
$samples = @()
$samples += Get-ExoPingTimes '1.1.1.1'
$samples += Get-ExoPingTimes '8.8.8.8'
$ok = ($samples.Count -ge 4)
$p50 = 0.0; $p95 = 0.0; $jitter = 0.0
if ($ok) {
  $sorted = @($samples | Sort-Object)
  $p50 = [double]$sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.5)]
  $p95 = [double]$sorted[[int][Math]::Floor(($sorted.Count - 1) * 0.95)]
  $diffs = @()
  for ($i = 1; $i -lt $samples.Count; $i++) { $diffs += [Math]::Abs($samples[$i] - $samples[$i - 1]) }
  if ($diffs.Count -gt 0) { $jitter = [Math]::Round((($diffs | Measure-Object -Average).Average), 2) }
}
$dnsTimes = @()
foreach ($name in @('www.google.com', 'www.cloudflare.com', 'www.microsoft.com')) {
  try {
    $t = Measure-Command { $null = Resolve-DnsName -Name $name -Type A -DnsOnly -EA Stop }
    $dnsTimes += [double]$t.TotalMilliseconds
  } catch {}
}
$dnsMs = -1.0
if ($dnsTimes.Count -gt 0) { $dnsMs = [Math]::Round((($dnsTimes | Measure-Object -Average).Average), 2) }
$result = [ordered]@{
  ok           = [bool]$ok
  pingP50Ms    = [Math]::Round($p50, 2)
  pingP95Ms    = [Math]::Round($p95, 2)
  jitterMs     = $jitter
  dnsMs        = $dnsMs
  samples      = $samples.Count
  timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
}
Write-Output ('EXO_BENCH:' + ($result | ConvertTo-Json -Compress))
exit 0
""");
        return sb.ToString();
    }
}
