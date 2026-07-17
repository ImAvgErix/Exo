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

    /// <summary>
    /// Explicit connection-quality test. Uses a Cloudflare-edge ramp similar to modern
    /// speed tests, measuring idle and loaded latency while download/upload traffic is
    /// active. It is never run in the background and emits one EXO_BENCH JSON line.
    /// </summary>
    public static string BuildQualityBenchmark()
    {
        var sb = new StringBuilder(12_000);
        sb.AppendLine("""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-ExoPercentile([double[]]$Values, [double]$P) {
  if (-not $Values -or $Values.Count -eq 0) { return 0.0 }
  $s = @($Values | Sort-Object)
  return [double]$s[[int][Math]::Floor(($s.Count - 1) * $P)]
}
function Get-ExoJitter([double[]]$Values) {
  if (-not $Values -or $Values.Count -lt 2) { return 0.0 }
  $d = @(); for ($i=1; $i -lt $Values.Count; $i++) { $d += [Math]::Abs($Values[$i] - $Values[$i-1]) }
  return [double](($d | Measure-Object -Average).Average)
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AutomaticDecompression = [Net.DecompressionMethods]::None
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(45)
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Exo-Connection-Lab/1.0')
$base = 'https://speed.cloudflare.com'
$script:nonce = 0
$script:dataBytes = [int64]0

function Get-ExoEdgeLatency {
  $script:nonce++
  $url = "$base/__down?bytes=0&exo=$script:nonce"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $response = $client.GetAsync($url).GetAwaiter().GetResult()
  try { $response.EnsureSuccessStatusCode() | Out-Null; $null = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() }
  finally { $response.Dispose() }
  $sw.Stop(); return [double]$sw.Elapsed.TotalMilliseconds
}

function Invoke-ExoDownloadStage([int64]$Bytes, [int]$Count) {
  $tasks = @(); $loaded = @(); $sw = [Diagnostics.Stopwatch]::StartNew()
  for ($i=0; $i -lt $Count; $i++) {
    $script:nonce++; $tasks += $client.GetByteArrayAsync("$base/__down?bytes=$Bytes&exo=$script:nonce")
  }
  while (@($tasks | Where-Object { -not $_.IsCompleted }).Count -gt 0) {
    try { $loaded += Get-ExoEdgeLatency } catch {}
    if (@($tasks | Where-Object { -not $_.IsCompleted }).Count -gt 0) { Start-Sleep -Milliseconds 80 }
  }
  $received = [int64]0
  foreach ($task in $tasks) { $received += [int64]$task.GetAwaiter().GetResult().Length }
  $sw.Stop(); $script:dataBytes += $received
  return @{ Mbps = (($received * 8.0) / [Math]::Max(1.0, $sw.Elapsed.TotalMilliseconds) / 1000.0); Ms = $sw.Elapsed.TotalMilliseconds; Loaded = @($loaded) }
}

function Invoke-ExoUploadStage([int]$Bytes, [int]$Count) {
  $payload = [byte[]]::new($Bytes); (New-Object Random).NextBytes($payload)
  $tasks = @(); $contents = @(); $loaded = @(); $sw = [Diagnostics.Stopwatch]::StartNew()
  for ($i=0; $i -lt $Count; $i++) {
    $content = [Net.Http.ByteArrayContent]::new($payload); $contents += $content
    $script:nonce++; $tasks += $client.PostAsync("$base/__up?exo=$script:nonce", $content)
  }
  while (@($tasks | Where-Object { -not $_.IsCompleted }).Count -gt 0) {
    try { $loaded += Get-ExoEdgeLatency } catch {}
    if (@($tasks | Where-Object { -not $_.IsCompleted }).Count -gt 0) { Start-Sleep -Milliseconds 80 }
  }
  foreach ($task in $tasks) { $r = $task.GetAwaiter().GetResult(); try { $r.EnsureSuccessStatusCode() | Out-Null } finally { $r.Dispose() } }
  foreach ($content in $contents) { $content.Dispose() }
  $sw.Stop(); $sent = [int64]$Bytes * $Count; $script:dataBytes += $sent
  return @{ Mbps = (($sent * 8.0) / [Math]::Max(1.0, $sw.Elapsed.TotalMilliseconds) / 1000.0); Ms = $sw.Elapsed.TotalMilliseconds; Loaded = @($loaded) }
}

try {
  # Warm connection, then collect an idle distribution.
  $null = Get-ExoEdgeLatency
  $idle = @(); 1..14 | ForEach-Object { try { $idle += Get-ExoEdgeLatency } catch {} }
  if ($idle.Count -lt 6) { throw 'Too few edge-latency samples' }

  # Ramp request size until a stage lasts about one second. This avoids a fixed-size
  # test under-driving fast links while keeping slow-link data use bounded.
  $downPoints = @(); $downLoaded = @()
  foreach ($stage in @(@(100000,3), @(1000000,3), @(10000000,3), @(25000000,4))) {
    $r = Invoke-ExoDownloadStage ([int64]$stage[0]) ([int]$stage[1])
    $downPoints += [double]$r.Mbps; $downLoaded += @($r.Loaded)
    if ($r.Ms -ge 1000 -and $stage[0] -ge 1000000) { break }
  }

  $upPoints = @(); $upLoaded = @()
  foreach ($stage in @(@(100000,3), @(1000000,3), @(5000000,3), @(15000000,2), @(25000000,2))) {
    $r = Invoke-ExoUploadStage ([int]$stage[0]) ([int]$stage[1])
    $upPoints += [double]$r.Mbps; $upLoaded += @($r.Loaded)
    if ($r.Ms -ge 1000 -and $stage[0] -ge 1000000) { break }
  }

  # Independent ICMP loss sample. Loss is reported separately from HTTPS latency.
  $received = 0; $expected = 40
  foreach ($target in @('1.1.1.1','8.8.8.8')) {
    $raw = (ping.exe -n 20 -w 1200 $target 2>$null | Out-String)
    $received += [regex]::Matches($raw, '[=<](\d+)\s*ms').Count
  }
  $loss = [Math]::Max(0, (($expected - [Math]::Min($expected,$received)) * 100.0 / $expected))

  $dnsTimes = @()
  foreach ($name in @('www.cloudflare.com','www.microsoft.com','www.google.com')) {
    try { $dnsTimes += [double](Measure-Command { $null = [Net.Dns]::GetHostAddresses($name) }).TotalMilliseconds } catch {}
  }
  $dns = if ($dnsTimes.Count) { [double](($dnsTimes | Measure-Object -Average).Average) } else { -1.0 }
  $p50 = Get-ExoPercentile $idle 0.5; $p95 = Get-ExoPercentile $idle 0.95
  $dl = if ($downLoaded.Count) { Get-ExoPercentile $downLoaded 0.5 } else { $p50 }
  $ul = if ($upLoaded.Count) { Get-ExoPercentile $upLoaded 0.5 } else { $p50 }
  $down = Get-ExoPercentile $downPoints 0.9; $up = Get-ExoPercentile $upPoints 0.9
  $penalty = [Math]::Max($dl-$p50, $ul-$p50)
  $downJitter = Get-ExoJitter $downLoaded; $upJitter = Get-ExoJitter $upLoaded
  $preset = if ($loss -ge 0.5 -or (Get-ExoJitter $idle) -ge 8 -or $downJitter -ge 15 -or $upJitter -ge 15 -or $penalty -ge 25) { 'lowest-latency' } elseif ($down -ge 300) { 'highest-throughput' } else { 'lowest-latency' }
  $reason = if ($loss -ge 0.5) { 'packet loss detected' } elseif ($penalty -ge 25) { 'loaded latency is elevated' } elseif ([Math]::Max($downJitter,$upJitter) -ge 15) { 'latency becomes unstable under load' } elseif ($down -ge 300) { 'fast stable link can benefit from throughput offloads' } else { 'latency-first is the safer fit for this link' }

  $result = [ordered]@{
    ok=$true; isQualityTest=$true; pingP50Ms=[Math]::Round($p50,2); pingP95Ms=[Math]::Round($p95,2)
    jitterMs=[Math]::Round((Get-ExoJitter $idle),2); dnsMs=[Math]::Round($dns,2); samples=$idle.Count
    downloadMbps=[Math]::Round($down,2); uploadMbps=[Math]::Round($up,2)
    downloadLoadedMs=[Math]::Round($dl,2); uploadLoadedMs=[Math]::Round($ul,2)
    downloadLoadedJitterMs=[Math]::Round($downJitter,2)
    uploadLoadedJitterMs=[Math]::Round($upJitter,2)
    packetLossPercent=[Math]::Round($loss,2); dataUsedMb=[Math]::Round(($script:dataBytes/1MB),1)
    endpoint='Cloudflare edge'; recommendedPreset=$preset; recommendationReason=$reason
    timestampUtc=(Get-Date).ToUniversalTime().ToString('o')
  }
  Write-Output ('EXO_BENCH:' + ($result | ConvertTo-Json -Compress))
  exit 0
} catch {
  $result = [ordered]@{ ok=$false; isQualityTest=$true; error=$_.Exception.Message; endpoint='Cloudflare edge'; timestampUtc=(Get-Date).ToUniversalTime().ToString('o') }
  Write-Output ('EXO_BENCH:' + ($result | ConvertTo-Json -Compress)); exit 1
} finally { $client.Dispose(); $handler.Dispose() }
""");
        return sb.ToString();
    }
}
