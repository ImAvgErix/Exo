using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptiHub.Models;

namespace OptiHub.Services;

/// <summary>
/// Full-stack Windows network optimizer — SG TCP Optimizer–class and beyond
/// (TCP/IP, AFD, DNS, QoS, multimedia throttle, NIC advanced, power, Wi‑Fi, DO).
/// Presets: LowestLatency (gaming) vs HighestThroughput (downloads) vs Balanced.
/// Safe across Ethernet / Wi‑Fi / multi-NIC; missing properties are skipped.
/// </summary>
public sealed class NetworkOptimizerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OptiHub", "network-optimizer.json");

    public NetworkPreset LoadSavedPreset()
    {
        try
        {
            if (!File.Exists(StatePath)) return NetworkPreset.Balanced;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (doc.RootElement.TryGetProperty("preset", out var p) &&
                Enum.TryParse<NetworkPreset>(p.GetString(), true, out var preset))
                return preset;
        }
        catch { }
        return NetworkPreset.Balanced;
    }

    public void SavePreset(NetworkPreset preset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new
            {
                preset = preset.ToString(),
                appliedUtc = DateTime.UtcNow.ToString("o")
            }));
        }
        catch { }
    }

    public async Task<NetworkSnapshot> ProbeAsync(CancellationToken ct = default)
    {
        var features = new List<NetworkFeatureRow>();
        string adapterName = "—", adapterDesc = "—", linkSpeed = "—", connType = "Unknown";
        string ipv4 = "—", gateway = "—", dns = "—", mtu = "—";
        bool? taskOffloadDisabled = null, lso = null, rsc = null;
        string autoTuning = "—", congestion = "—";
        int? gwPing = null, netPing = null;
        string publicIp = "—", provider = "—", area = "—";
        var detail = string.Empty;
        var probeOk = true;

        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenByDescending(n => n.Speed)
                .FirstOrDefault();

            if (up is not null)
            {
                adapterName = up.Name;
                adapterDesc = up.Description;
                linkSpeed = FormatSpeed(up.Speed);
                connType = up.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    NetworkInterfaceType.Wireless80211 => "Wi‑Fi",
                    _ => up.NetworkInterfaceType.ToString()
                };

                var ipProps = up.GetIPProperties();
                var v4 = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (v4 is not null) ipv4 = v4.Address.ToString();

                var gw = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw is not null) gateway = gw.Address.ToString();

                dns = string.Join(", ", ipProps.DnsAddresses
                    .Where(d => d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(d => d.ToString()));
                if (string.IsNullOrWhiteSpace(dns)) dns = "—";

                try { mtu = ipProps.GetIPv4Properties()?.Mtu.ToString() ?? "—"; }
                catch { mtu = "—"; }
            }
            else
            {
                probeOk = false;
                detail = "No active network adapter.";
            }

            var activePreset = LoadSavedPreset();
            var latency = activePreset == NetworkPreset.LowestLatency;
            var throughput = activePreset == NetworkPreset.HighestThroughput;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
                var dto = key?.GetValue("DisableTaskOffload");
                taskOffloadDisabled = dto is int i && i != 0;
            }
            catch { }

            var tcpGlobal = await RunCaptureAsync("netsh", "int tcp show global", ct).ConfigureAwait(false);
            autoTuning = Match(tcpGlobal, @"Receive Window Auto-Tuning Level\s*:\s*(\S+)") ?? "—";
            var rscStr = Match(tcpGlobal, @"Receive Segment Coalescing State\s*:\s*(\w+)");
            if (rscStr is not null)
                rsc = rscStr.Equals("enabled", StringComparison.OrdinalIgnoreCase);

            var supp = await RunCaptureAsync("netsh", "int tcp show supplemental", ct).ConfigureAwait(false);
            congestion = Match(supp, @"Congestion Control Provider\s*:\s*(\w+)") ?? "—";

            try
            {
                var lsoOut = await RunCaptureAsync(
                    "powershell",
                    "-NoProfile -Command \"$n=(Get-NetAdapter|? Status -eq 'Up'|select -First 1 -Expand Name); if($n){(Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*LsoV2IPv4' -EA 0).DisplayValue}\"",
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(lsoOut))
                    lso = lsoOut.Contains("Enabled", StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            // Gaming Nagle/ACK keys (per-interface) — expected ON for latency, OFF for throughput
            bool? nagleOff = null;
            try
            {
                using var ifRoot = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                if (ifRoot is not null)
                {
                    foreach (var name in ifRoot.GetSubKeyNames())
                    {
                        using var ik = ifRoot.OpenSubKey(name);
                        if (ik is null) continue;
                        var ack = ik.GetValue("TcpAckFrequency");
                        var nd = ik.GetValue("TCPNoDelay");
                        if (ack is int a || nd is int)
                        {
                            nagleOff = (ack is int aa && aa == 1) || (nd is int nn && nn == 1);
                            if (nagleOff == true) break;
                        }
                    }
                    nagleOff ??= false;
                }
            }
            catch { }

            // MMCSS targets: SystemResponsiveness=10, NetworkThrottlingIndex=10 (MS defaults / valid gaming)
            var mmOk = false;
            try
            {
                using var mm = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                var resp = mm?.GetValue("SystemResponsiveness");
                var idx = mm?.GetValue("NetworkThrottlingIndex");
                var respOk = resp is int r && r == 10;
                // Accept default missing NetworkThrottlingIndex as OK (OS default 10), or explicit 10
                var thrOk = idx is null || (idx is int ti && ti == 10);
                mmOk = respOk && thrOk;
            }
            catch { }

            // Light pings only for status (not feature cards)
            if (gateway is not "—" && System.Net.IPAddress.TryParse(gateway, out _))
                gwPing = await PingMsAsync(gateway, ct).ConfigureAwait(false);
            netPing = await PingMsAsync("1.1.1.1", ct).ConfigureAwait(false)
                      ?? await PingMsAsync("8.8.8.8", ct).ConfigureAwait(false);

            // Feature cards = optimizer knobs only (same idea as Discord/Steam/NVIDIA tiles)
            var lsoOk = lso is null || (latency ? lso == false : lso == true);
            var rscOk = rsc is null || (latency ? rsc == false : rsc == true);
            var autoOk = !autoTuning.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                         && (latency
                             ? autoTuning.Equals("normal", StringComparison.OrdinalIgnoreCase)
                             : !autoTuning.Equals("disabled", StringComparison.OrdinalIgnoreCase));
            var nagleOk = !latency || nagleOff != false;
            if (throughput) nagleOk = nagleOff != true;

            features.Add(Row("Task offload", taskOffloadDisabled == true ? "Off (bad)" : "On", taskOffloadDisabled != true));
            features.Add(Row("LSO v2",
                lso == true ? "On" : lso == false ? (latency ? "Off · latency" : "Off") : "—",
                lsoOk));
            features.Add(Row("RSC",
                rsc == true ? "On" : rsc == false ? (latency ? "Off · latency" : "Off") : "—",
                rscOk));
            features.Add(Row("Auto-tuning", autoTuning, autoOk));
            features.Add(Row("Congestion", congestion, true));
            features.Add(Row("Nagle / ACK",
                nagleOff == true ? "Off (latency)" : nagleOff == false ? "Default" : "—",
                nagleOk));
            features.Add(Row("MMCSS", mmOk ? "Responsiveness 10" : "Not set", mmOk || activePreset == NetworkPreset.Balanced));
            features.Add(Row("QoS reserve", ReadQosReserve(), ReadQosReserve() is "0%" or "—"));
        }
        catch (Exception ex)
        {
            probeOk = false;
            detail = ex.Message;
        }

        var mediaProfile = new NetworkMediaProfile();
        try
        {
            mediaProfile = await DetectMediaProfileAsync(ct).ConfigureAwait(false);
            features.Add(Row("Path policy", mediaProfile.PolicyLine, true));
            if (mediaProfile.WifiAvailable)
            {
                var bandDetail = mediaProfile.ClientSupports6Ghz
                    ? "6 GHz capable"
                    : mediaProfile.ClientSupports5Ghz ? "5 GHz capable" : "Legacy";
                if (mediaProfile.ConnectedRadioHint is not "—")
                    bandDetail += $" · {mediaProfile.ConnectedRadioHint}";
                features.Add(Row("Wi‑Fi capability", bandDetail, true));
            }
        }
        catch { }

        return new NetworkSnapshot
        {
            AdapterName = adapterName,
            AdapterDescription = adapterDesc,
            LinkSpeed = linkSpeed,
            ConnectionType = connType,
            Ipv4Address = ipv4,
            Gateway = gateway,
            DnsServers = dns,
            PublicIp = publicIp,
            Provider = provider,
            Area = area,
            Mtu = mtu,
            TaskOffloadDisabled = taskOffloadDisabled,
            LsoEnabled = lso,
            RscEnabled = rsc,
            AutoTuning = autoTuning,
            CongestionProvider = congestion,
            GatewayPingMs = gwPing,
            InternetPingMs = netPing,
            Detail = detail,
            ProbeOk = probeOk,
            ActivePreset = LoadSavedPreset(),
            Media = mediaProfile,
            Features = features
        };
    }

    /// <summary>
    /// Detect Ethernet/Wi‑Fi availability, client band support (5/6 GHz), and apply policy text.
    /// Uses OS/driver facts only — not a cloud model.
    /// </summary>
    public async Task<NetworkMediaProfile> DetectMediaProfileAsync(CancellationToken ct = default)
    {
        var ethAvail = false;
        var ethUp = false;
        var ethInUse = false;
        var wifiAvail = false;
        var wifiUp = false;
        var supports6 = false;
        var supports5 = true; // almost all modern Wi‑Fi
        var radioHint = "—";

        try
        {
            var probePs = Path.Combine(Path.GetTempPath(), $"optihub-media-{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(probePs, """
$ErrorActionPreference = 'SilentlyContinue'
function IsWifi($a) {
  $m=[string]$a.MediaType; $d=[string]$a.InterfaceDescription; $n=[string]$a.Name
  return ($m -match '802\.11|Native 802|Wireless|Wi-?Fi' -or $d -match 'Wi-?Fi|Wireless|802\.11|WLAN' -or $n -match '^Wi-?Fi|Wireless')
}
$phys = @(Get-NetAdapter -Physical -EA SilentlyContinue)
$eth = @($phys | Where-Object { -not (IsWifi $_) })
$wifi = @($phys | Where-Object { IsWifi $_ })
$eUp = @($eth | Where-Object Status -eq 'Up').Count -gt 0
$wUp = @($wifi | Where-Object Status -eq 'Up').Count -gt 0
# "In use" for gaming = Ethernet is linked AND has a real IPv4 (usable).
# Prefer Ethernet 100% when that is true — do not wait for default-route ownership.
$eInUse = $false
foreach ($e in @($eth | Where-Object Status -eq 'Up')) {
  $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '169.254.*' })
  if ($ip.Count -gt 0) { $eInUse = $true; break }
}
$band6 = $false; $band5 = $false
foreach ($w in $wifi) {
  foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $w.Name -EA SilentlyContinue)) {
    $blob = "$($p.DisplayName) $($p.DisplayValue) $(($p.ValidDisplayValues) -join ' ')"
    if ($blob -match '(?i)6\s*GHz|6GHz|Wi-?Fi\s*6E|802\.11be|Prefer 6') { $band6 = $true }
    if ($blob -match '(?i)5\s*GHz|5GHz|Prefer 5') { $band5 = $true }
  }
}
$drv = (netsh wlan show drivers 2>$null | Out-String)
if ($drv -match '(?i)6\s*GHz|802\.11be|Wi-?Fi\s*6E') { $band6 = $true }
if ($drv -match '(?i)5\s*GHz|802\.11ac|802\.11ax|802\.11n') { $band5 = $true }
$iface = (netsh wlan show interfaces 2>$null | Out-String)
$hint = '-'
if ($iface -match '(?i)Radio type\s*:\s*(.+)') { $hint = $Matches[1].Trim() }
elseif ($iface -match '(?i)Channel\s*:\s*(\d+)') { $hint = 'ch ' + $Matches[1] }
if ($hint -match '(?i)6\s*GHz|be|6E') { $band6 = $true }
Write-Output "ETH=$($eth.Count -gt 0);ETHUP=$eUp;ETHUSE=$eInUse;WIFI=$($wifi.Count -gt 0);WIFIUP=$wUp;B6=$band6;B5=$band5;HINT=$hint"
""", ct).ConfigureAwait(false);
            try
            {
                var ps = await RunCaptureAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{probePs}\"", ct)
                    .ConfigureAwait(false);
                foreach (var part in (ps ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var k = kv[0].Trim().ToUpperInvariant();
                    var v = kv[1].Trim();
                    switch (k)
                    {
                        case "ETH": ethAvail = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "ETHUP": ethUp = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "ETHUSE": ethInUse = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "WIFI": wifiAvail = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "WIFIUP": wifiUp = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "B6": supports6 = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "B5": supports5 = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "HINT" when v is not ("-" or ""): radioHint = v; break;
                    }
                }
            }
            finally
            {
                try { File.Delete(probePs); } catch { }
            }
        }
        catch { }

        // Fallback from managed APIs if PS failed
        if (!ethAvail && !wifiAvail)
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    ethAvail = true;
                    if (n.OperationalStatus == OperationalStatus.Up) ethUp = true;
                }
                else if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    wifiAvail = true;
                    if (n.OperationalStatus == OperationalStatus.Up) wifiUp = true;
                }
            }
        }

        // Managed fallback: Ethernet Up + real IPv4 = prefer Ethernet for gaming
        if (!ethInUse && ethUp)
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    var hasIp = n.GetIPProperties().UnicastAddresses.Any(u =>
                        u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                    if (hasIp) { ethInUse = true; break; }
                }
            }
            catch { }
        }

        var bandTarget = supports6 ? "6GHz" : supports5 ? "5GHz" : "Auto";
        string policy;
        if (ethInUse)
            policy = wifiAvail
                ? "Ethernet ready → prefer Ethernet, disable Wi‑Fi (lowest latency)"
                : "Ethernet ready → Ethernet only";
        else if (ethUp && !ethInUse)
            policy = "Ethernet linked (no IP yet) → leave Wi‑Fi until Ethernet has an address";
        else if (wifiUp)
            policy = $"Wi‑Fi only → prefer {bandTarget}";
        else if (ethAvail)
            policy = "Ethernet present (unplugged/down) → use when linked";
        else if (wifiAvail)
            policy = $"Wi‑Fi only → prefer {bandTarget}";
        else
            policy = "No physical adapter detected";

        return new NetworkMediaProfile
        {
            EthernetAvailable = ethAvail,
            EthernetUp = ethUp,
            EthernetInUse = ethInUse,
            WifiAvailable = wifiAvail,
            WifiUp = wifiUp,
            ClientSupports6Ghz = supports6,
            ClientSupports5Ghz = supports5,
            PreferredBandTarget = bandTarget,
            ConnectedRadioHint = radioHint,
            PolicyLine = policy
        };
    }

    /// <summary>True when live settings match the saved preset (no false “fail” for intentional offs).</summary>
    public bool MatchesPreset(NetworkSnapshot snap, NetworkPreset preset)
    {
        if (!snap.ProbeOk) return false;
        if (snap.TaskOffloadDisabled == true) return false;
        var latency = preset == NetworkPreset.LowestLatency;
        if (latency)
        {
            if (snap.LsoEnabled == true) return false;
            if (snap.RscEnabled == true) return false;
            if (snap.AutoTuning.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if (preset == NetworkPreset.HighestThroughput)
        {
            if (snap.LsoEnabled == false) return false;
            if (snap.RscEnabled == false) return false;
            if (snap.AutoTuning.Equals("disabled", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public Task<(bool Ok, string Message)> ApplyPresetAsync(
        NetworkPreset preset,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => ApplyPresetAsync(preset, new NetworkApplyOptions(), progress, ct);

    public async Task<(bool Ok, string Message)> ApplyPresetAsync(
        NetworkPreset preset,
        NetworkApplyOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new NetworkApplyOptions();
        progress?.Report("Detecting adapters & radio capabilities...");
        var media = await DetectMediaProfileAsync(ct).ConfigureAwait(false);

        progress?.Report("Preparing stack (Ethernet-first when available)...");
        var script = BuildFullApplyScript(preset, options, media);
        var path = Path.Combine(Path.GetTempPath(), $"optihub-net-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, script, ct).ConfigureAwait(false);

        progress?.Report(preset switch
        {
            NetworkPreset.LowestLatency => "Applying lowest-latency stack (elevated)...",
            NetworkPreset.HighestThroughput => "Applying highest-throughput stack (elevated)...",
            _ => "Applying balanced stack (elevated)..."
        });

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Could not start elevated PowerShell.");
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
                return (false, $"Apply exit {p.ExitCode}. Try again as Administrator.");

            progress?.Report("Verifying settings...");
            await Task.Delay(options.RestartEthernet ? 2000 : 800, ct).ConfigureAwait(false);
            SavePreset(preset);
            var snap = await ProbeAsync(ct).ConfigureAwait(false);
            var matched = MatchesPreset(snap, preset);
            var policy = media.EthernetInUse
                ? "Ethernet preferred (Wi‑Fi disabled when Ethernet has a real IP)."
                : media.WifiUp
                    ? $"Wi‑Fi path; prefer {media.PreferredBandTarget}."
                    : media.PolicyLine;

            if (matched)
            {
                var baseMsg = preset switch
                {
                    NetworkPreset.LowestLatency => "Lowest latency applied and verified.",
                    NetworkPreset.HighestThroughput => "Highest download applied and verified.",
                    _ => "Stack applied and verified."
                };
                var restartNote = options.RestartEthernet
                    ? " Ethernet was restarted."
                    : " Adapter restart skipped (toggle link or re-apply with restart if a prop looks stale).";
                return (true, $"{baseMsg} {policy}{restartNote}");
            }

            var fails = snap.Features.Where(f => !f.IsOk).Select(f => f.Title).Take(4).ToList();
            var hint = fails.Count > 0 ? string.Join(", ", fails) : "some NIC properties";
            return (true, $"Applied ({policy}). Verify incomplete ({hint}). Refresh after a moment.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Administrator approval cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Documented / still-effective knobs only (Win10/11).
    /// Removed folklore: MaxUserPort/MaxFreeTcbs/TcpNumConnections, chimney/NetDMA/DCA,
    /// LargeSystemCache, AFD backlog, DNS priority, WinINET connection limits, TTL, etc.
    /// Sources: MS MMCSS docs, MS NIC performance tuning, netsh/Set-NetTCPSetting.
    /// </summary>
    private static string BuildFullApplyScript(
        NetworkPreset preset,
        NetworkApplyOptions options,
        NetworkMediaProfile media)
    {
        var latency = preset == NetworkPreset.LowestLatency;
        // MS: autotune normal for typical; experimental for high BDP bulk only
        var autotune = latency ? "normal" : "experimental";
        var autoTuningPs = latency ? "Normal" : "Experimental";
        // RSC/LSO: real coalescing tradeoffs — off for latency, on for bulk
        var rsc = latency ? "disabled" : "enabled";
        var lso = latency ? "0" : "1";
        var im = latency ? "0" : "1";
        var restartEth = options.RestartEthernet ? "1" : "0";
        var preferEth = options.PreferEthernetDisableWifi ? "1" : "0";
        // Smart band: 6GHz if client supports, else 5GHz prefer (never force-only)
        var prefer6 = media.ClientSupports6Ghz ? "1" : "0";
        var prefer5 = media.ClientSupports5Ghz || !media.ClientSupports6Ghz ? "1" : "0";
        // Nagle keys only for latency (TCP games); throughput clears them
        var ackBlock = latency
            ? """
  Set-Dword $p 'TcpAckFrequency' 1
  Set-Dword $p 'TCPNoDelay' 1
  Set-Dword $p 'TcpDelAckTicks' 0
"""
            : """
  Remove-Prop $p 'TcpAckFrequency'
  Remove-Prop $p 'TCPNoDelay'
  Remove-Prop $p 'TcpDelAckTicks'
""";

        var sb = new StringBuilder(14_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("Write-Host '[OptiHub-NET] Preset=" + preset + " ethFirst=" + preferEth + " restartEth=" + restartEth + " band6=" + prefer6 + "'");
        sb.AppendLine("""
function Set-Dword([string]$Path, [string]$Name, [int]$Value) {
  if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
  New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force -EA SilentlyContinue | Out-Null
  Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -Type DWord -Force -EA SilentlyContinue
}
function Remove-Prop([string]$Path, [string]$Name) {
  if (Test-Path -LiteralPath $Path) { Remove-ItemProperty -LiteralPath $Path -Name $Name -Force -EA SilentlyContinue }
}
function Set-Adv($Name, $Kw, $Val) {
  try { Set-NetAdapterAdvancedProperty -Name $Name -RegistryKeyword $Kw -RegistryValue $Val -NoRestart -EA SilentlyContinue } catch {}
}
""");

        // --- Registry: only keys that still matter ---
        // DisableTaskOffload=1 is a real footgun (kills checksum/LSO at stack level)
        sb.AppendLine("$tcp = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters'");
        sb.AppendLine("Set-Dword $tcp 'DisableTaskOffload' 0");
        sb.AppendLine("Set-Dword $tcp 'EnablePMTUDiscovery' 1");
        // Clear obsolete static RWIN if present (auto-tuning owns window size)
        sb.AppendLine("Remove-Prop $tcp 'GlobalMaxTcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'TcpWindowSize'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPChimney'");
        sb.AppendLine("Remove-Prop $tcp 'EnableTCPA'");
        sb.AppendLine("Remove-Prop $tcp 'EnableDCA'");
        sb.AppendLine("Remove-Prop $tcp 'TcpNumConnections'");
        sb.AppendLine("Remove-Prop $tcp 'LargeSystemCache'");

        // MMCSS — Microsoft docs:
        // SystemResponsiveness: % for low-priority; default 20; <10 or >100 clamp to 20 → use 10
        // NetworkThrottlingIndex: default 10. ffffffff can raise DPC latency / audio issues → keep 10
        sb.AppendLine("$mm = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile'");
        sb.AppendLine("Set-Dword $mm 'SystemResponsiveness' 10");
        sb.AppendLine("Set-Dword $mm 'NetworkThrottlingIndex' 10");
        sb.AppendLine("""
$mmGames = Join-Path $mm 'Tasks\Games'
if (-not (Test-Path $mmGames)) { New-Item $mmGames -Force | Out-Null }
Set-Dword $mmGames 'GPU Priority' 8
Set-Dword $mmGames 'Priority' 6
try { Set-ItemProperty $mmGames -Name 'Scheduling Category' -Value 'High' -Force -EA SilentlyContinue } catch {}
try { Set-ItemProperty $mmGames -Name 'SFIO Priority' -Value 'High' -Force -EA SilentlyContinue } catch {}
# QoS "Limit reservable bandwidth" — GPO NonBestEffortLimit 0 removes the old 20% reserve
New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Force | Out-Null
Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit' 0
""");

        // --- netsh / Set-NetTCPSetting (supported modern path) ---
        sb.AppendLine("netsh int tcp set global rss=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global autotuninglevel=" + autotune + " | Out-Null");
        sb.AppendLine("netsh int tcp set global rsc=" + rsc + " | Out-Null");
        sb.AppendLine("try { netsh int tcp set heuristics disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internetcustom congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ip set global taskoffload=enabled | Out-Null } catch {}");
        // Ephemeral ports — modern API (MaxUserPort is legacy)
        sb.AppendLine("try { netsh int ipv4 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("foreach ($pr in @('Internet','InternetCustom')) {");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -CongestionProvider CUBIC -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal " + autoTuningPs + " -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Disabled -EA SilentlyContinue } catch {}");
        sb.AppendLine("}");

        // Nagle (per-interface) — only latency preset
        sb.AppendLine("Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces' -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("  $p = $_.PSPath");
        sb.AppendLine(ackBlock);
        sb.AppendLine("}");

        // --- Per-adapter: branch Ethernet vs Wi‑Fi (MS: wireless often has no RSS/LSO) ---
        // Apply to all physical NICs so dual-homed PCs are ready on either media.
        sb.AppendLine("function Test-IsWifiAdapter($a) {");
        sb.AppendLine("  $media = [string]$a.MediaType");
        sb.AppendLine("  $desc  = [string]$a.InterfaceDescription");
        sb.AppendLine("  $name  = [string]$a.Name");
        sb.AppendLine("  if ($media -match '(?i)802\\.11|Native 802|Wireless|Wi-?Fi') { return $true }");
        sb.AppendLine("  if ($desc  -match '(?i)Wi-?Fi|Wireless|802\\.11|WLAN|MediaTek.*Wi|Intel.*Wi-?Fi|Realtek.*802\\.11|Killer.*Wireless') { return $true }");
        sb.AppendLine("  if ($name  -match '(?i)^Wi-?Fi|Wireless') { return $true }");
        sb.AppendLine("  return $false");
        sb.AppendLine("}");
        sb.AppendLine("$adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue | Where-Object { $_.Status -eq 'Up' -or $_.Status -eq 'Disconnected' })");
        sb.AppendLine("if ($adapters.Count -eq 0) { $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue) }");
        sb.AppendLine("foreach ($a in $adapters) {");
        sb.AppendLine("  $n = $a.Name");
        sb.AppendLine("  $isWifi = Test-IsWifiAdapter $a");
        sb.AppendLine("  $kind = $(if ($isWifi) { 'Wi-Fi' } else { 'Ethernet' })");
        sb.AppendLine("  Write-Host \"[NIC] $n ($kind) $($a.InterfaceDescription)\"");
        sb.AppendLine("  foreach ($kw in @('*IPChecksumOffloadIPv4','*TCPChecksumOffloadIPv4','*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv4','*UDPChecksumOffloadIPv6')) { Set-Adv $n $kw 3 }");
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv4' " + lso);
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv6' " + lso);
        sb.AppendLine("  try { if (" + lso + " -eq 1) { Enable-NetAdapterLso -Name $n -NoRestart -EA SilentlyContinue } else { Disable-NetAdapterLso -Name $n -NoRestart -EA SilentlyContinue } } catch {}");
        sb.AppendLine("  try { if ('" + rsc + "' -eq 'enabled') { Enable-NetAdapterRsc -Name $n -EA SilentlyContinue } else { Disable-NetAdapterRsc -Name $n -EA SilentlyContinue } } catch {}");
        sb.AppendLine("  Set-Adv $n '*InterruptModeration' " + im);
        sb.AppendLine("  if (" + im + " -eq 0) {");
        sb.AppendLine("    try { Set-Adv $n 'ITR' 0 } catch {}");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Off' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  } else {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $vals = @((Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword ITR -EA SilentlyContinue).ValidDisplayValues)");
        sb.AppendLine("      if ($vals -contains 'Adaptive') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Adaptive' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("      elseif ($vals -contains 'Medium') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Medium' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($kw in @('*EEE','*EnergyEfficientEthernet','*GreenEthernet','*SelectiveSuspend','*IdleRestriction','*ReduceSpeedOnPowerDown')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("  try { Set-NetAdapterPowerManagement -Name $n -SelectiveSuspend Disabled -NoRestart -EA SilentlyContinue } catch {}");
        // RSS: Microsoft — many wireless NICs do not support RSS
        sb.AppendLine("  if (-not $isWifi) {");
        sb.AppendLine("    Set-Adv $n '*RSS' 1");
        sb.AppendLine("    try { Set-NetAdapterRss -Name $n -Enabled $true -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { $q = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*NumRssQueues' -EA SilentlyContinue; if ($q -and $q.ValidRegistryValues) { $max = ($q.ValidRegistryValues | Measure-Object -Maximum).Maximum; if ($max -gt 0) { Set-Adv $n '*NumRssQueues' ([int]$max) } } } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($kw in @('*ReceiveBuffers','*TransmitBuffers','ReceiveBuffers','TransmitBuffers')) {");
        sb.AppendLine("    try { $prop = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -EA SilentlyContinue; if ($prop -and $prop.ValidRegistryValues) { $max = ($prop.ValidRegistryValues | Measure-Object -Maximum).Maximum; if ($max -gt 0) { Set-Adv $n $kw ([int]$max) } } } catch {}");
        sb.AppendLine("  }");
        // Wi-Fi: power-save + smart band (6 GHz prefer if client supports; never force-only)
        sb.AppendLine("  if ($isWifi) {");
        sb.AppendLine("    foreach ($dn in @('MIMO Power Save Mode','MIMO Power Save','uAPSD support','Power Saving Mode')) {");
        sb.AppendLine("      try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $dn -DisplayValue 'Disabled' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("      try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $dn -DisplayValue 'Off' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    $bandVals = @()");
        sb.AppendLine("    try { $bp = Get-NetAdapterAdvancedProperty -Name $n -DisplayName 'Preferred Band' -EA SilentlyContinue; if ($bp) { $bandVals = @($bp.ValidDisplayValues) } } catch {}");
        if (prefer6 == "1")
        {
            sb.AppendLine("    foreach ($bv in @('Prefer 6GHz band','Prefer 6GHz','6GHz band','Prefer 6 GHz band')) {");
            sb.AppendLine("      if ($bandVals.Count -eq 0 -or $bandVals -contains $bv) { try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Preferred Band' -DisplayValue $bv -NoRestart -EA SilentlyContinue } catch {} }");
            sb.AppendLine("    }");
        }
        sb.AppendLine("    foreach ($bv in @('Prefer 5GHz band','Prefer 5GHz','Prefer 5 GHz band')) {");
        sb.AppendLine("      if ($bandVals.Count -eq 0 -or $bandVals -contains $bv) { try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Preferred Band' -DisplayValue $bv -NoRestart -EA SilentlyContinue } catch {} }");
        sb.AppendLine("    }");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Roaming Aggressiveness' -DisplayValue 'Medium' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    $class = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}'");
        sb.AppendLine("    Get-ChildItem $class -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("      $props = Get-ItemProperty $_.PSPath -EA SilentlyContinue");
        sb.AppendLine("      if ($props.DriverDesc -eq $a.InterfaceDescription) { Set-ItemProperty $_.PSPath -Name PnPCapabilities -Value 24 -Type DWord -Force -EA SilentlyContinue }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("}");

        // Prefer Ethernet 100% when linked with a real IPv4 (gaming lowest-latency path).
        // Not "cable only / no IP" — that is not usable Ethernet yet.
        sb.AppendLine("if (" + preferEth + " -eq 1) {");
        sb.AppendLine("  $ethReady = @()");
        sb.AppendLine("  foreach ($e in @($adapters | Where-Object { -not (Test-IsWifiAdapter $_) -and $_.Status -eq 'Up' })) {");
        sb.AppendLine("    $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' })");
        sb.AppendLine("    if ($ip.Count -gt 0) { $ethReady += $e }");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($ethReady.Count -gt 0) {");
        sb.AppendLine("    Write-Host '[OptiHub-NET] Ethernet ready — preferring Ethernet (lowest latency)'");
        sb.AppendLine("    foreach ($e in $ethReady) {");
        sb.AppendLine("      try { Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -InterfaceMetric 1 -EA SilentlyContinue } catch {}");
        sb.AppendLine("      try { Set-NetIPInterface -InterfaceIndex $e.ifIndex -AddressFamily IPv6 -InterfaceMetric 1 -EA SilentlyContinue } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    foreach ($w in @($adapters | Where-Object { Test-IsWifiAdapter $_ })) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        if ($w.Status -ne 'Disabled') {");
        sb.AppendLine("          Disable-NetAdapter -Name $w.Name -Confirm:$false -EA SilentlyContinue");
        sb.AppendLine("          Write-Host \"[NIC] Wi-Fi disabled: $($w.Name)\"");
        sb.AppendLine("        }");
        sb.AppendLine("      } catch { Write-Host \"[NIC] could not disable $($w.Name)\" }");
        sb.AppendLine("    }");
        sb.AppendLine("  } else {");
        sb.AppendLine("    Write-Host '[OptiHub-NET] No usable Ethernet (up+IPv4) — keeping Wi-Fi'");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        sb.AppendLine("try { Clear-DnsClientCache -EA SilentlyContinue } catch {}");

        // Restart Ethernet only if user confirmed (never auto Wi-Fi restart)
        sb.AppendLine("if (" + restartEth + " -eq 1) {");
        sb.AppendLine("  foreach ($a in @($adapters | Where-Object { $_.Status -eq 'Up' -and -not (Test-IsWifiAdapter $_) })) {");
        sb.AppendLine("    try { Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue; Write-Host \"[NIC] restarted (Ethernet) $($a.Name)\" } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  Start-Sleep -Seconds 2");
        sb.AppendLine("} else {");
        sb.AppendLine("  Write-Host '[OptiHub-NET] Ethernet restart skipped (user declined)'");
        sb.AppendLine("}");
        sb.AppendLine("Write-Host '[OptiHub-NET] DONE preset=" + preset + "'");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    private static NetworkFeatureRow Row(string title, string status, bool ok, string? note = null) => new()
    {
        Title = title,
        Status = string.IsNullOrWhiteSpace(note) ? status : $"{status} · {note}",
        IsOk = ok
    };

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length <= max) return s;
        return s[..(max - 1)].TrimEnd() + "…";
    }

    private static string FmtMs(int? ms) => ms is int v ? $"{v} ms" : "—";

    private static string ReadQosReserve()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Psched");
            var v = k?.GetValue("NonBestEffortLimit");
            if (v is int i) return i == 0 ? "0%" : $"{i}%";
        }
        catch { }
        return "—";
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text ?? "", pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        double v = bitsPerSecond;
        string[] units = { "bps", "Kbps", "Mbps", "Gbps" };
        var i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private static async Task<int?> PingMsAsync(string host, CancellationToken ct)
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(host, 2000).WaitAsync(ct).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return (int)reply.RoundtripTime;
        }
        catch { }
        return null;
    }

    private static async Task<string> RunCaptureAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return string.Empty;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return stdout.Trim();
        }
        catch { return string.Empty; }
    }
}
