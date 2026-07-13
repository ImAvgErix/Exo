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

            bool? throttleOff = null;
            try
            {
                using var mm = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                var idx = mm?.GetValue("NetworkThrottlingIndex");
                // 0xFFFFFFFF = throttling disabled (our target)
                throttleOff = idx is int ti && unchecked((uint)ti) == 0xFFFFFFFFu
                              || idx is long tl && unchecked((ulong)tl) == 0xFFFFFFFFul;
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
                nagleOff == true ? "Gaming keys on" : nagleOff == false ? "Default" : "—",
                nagleOk));
            features.Add(Row("Net throttle",
                throttleOff == true ? "Off" : throttleOff == false ? "On" : "—",
                throttleOff != false));
            features.Add(Row("QoS reserve", ReadQosReserve(), ReadQosReserve() is "0%" or "—"));
        }
        catch (Exception ex)
        {
            probeOk = false;
            detail = ex.Message;
        }

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
            Features = features
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

    public async Task<(bool Ok, string Message)> ApplyPresetAsync(
        NetworkPreset preset,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Preparing full TCP / NIC stack...");
        var script = BuildFullApplyScript(preset);
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
            // Let NIC restart settle before probe
            await Task.Delay(1500, ct).ConfigureAwait(false);
            SavePreset(preset);
            var snap = await ProbeAsync(ct).ConfigureAwait(false);
            var matched = MatchesPreset(snap, preset);
            if (matched)
            {
                return (true, preset switch
                {
                    NetworkPreset.LowestLatency => "Lowest latency applied and verified. Reboot helps power/PnP settle.",
                    NetworkPreset.HighestThroughput => "Highest download applied and verified. Reboot helps power/PnP settle.",
                    _ => "Stack applied and verified."
                });
            }

            // Still saved — partial apply is better than nothing; surface what looks off
            var fails = snap.Features.Where(f => !f.IsOk).Select(f => f.Title).Take(4).ToList();
            var hint = fails.Count > 0 ? string.Join(", ", fails) : "some NIC properties";
            return (true, $"Applied, but verify incomplete ({hint}). Reboot, then Refresh.");
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

    private static string BuildFullApplyScript(NetworkPreset preset)
    {
        var latency = preset == NetworkPreset.LowestLatency;
        var throughput = preset == NetworkPreset.HighestThroughput;
        // Gaming latency: normal auto-tune (not disabled — disabled kills throughput on modern stacks)
        var autotune = latency ? "normal" : "experimental";
        var autoTuningPs = latency ? "Normal" : "Experimental";
        var rsc = latency ? "disabled" : "enabled";
        var lso = latency ? "0" : "1";
        var im = latency ? "0" : "1";
        var packetCoal = latency ? "0" : "1";
        // Slightly higher initial window even for latency — helps first paint without bulk penalty
        var initialCwnd = latency ? "10" : "16";
        var largeCache = throughput ? "1" : "0";
        // Gaming multimedia: kill network throttle hard
        var systemResponsiveness = latency ? "0" : "10";
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

        var sb = new StringBuilder(24_000);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("Write-Host '[OptiHub-NET] Preset=" + preset + "'");
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

        // TCP/IP core
        sb.AppendLine("$tcp = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters'");
        sb.AppendLine("Set-Dword $tcp 'DisableTaskOffload' 0");
        sb.AppendLine("Set-Dword $tcp 'EnablePMTUDiscovery' 1");
        sb.AppendLine("Set-Dword $tcp 'EnablePMTUBHDetect' 0");
        sb.AppendLine("Set-Dword $tcp 'DefaultTTL' 64");
        sb.AppendLine("Set-Dword $tcp 'TcpMaxDupAcks' 2");
        sb.AppendLine("Set-Dword $tcp 'SackOpts' 1");
        sb.AppendLine("Set-Dword $tcp 'Tcp1323Opts' 1");
        sb.AppendLine("Set-Dword $tcp 'TcpTimedWaitDelay' 30");
        sb.AppendLine("Set-Dword $tcp 'MaxUserPort' 65534");
        sb.AppendLine("Set-Dword $tcp 'MaxFreeTcbs' 65536");
        sb.AppendLine("Set-Dword $tcp 'MaxHashTableSize' 65536");
        sb.AppendLine("Set-Dword $tcp 'TcpNumConnections' 16777214");
        sb.AppendLine("Set-Dword $tcp 'TcpFinWait2Delay' 30");
        sb.AppendLine("Set-Dword $tcp 'SynAttackProtect' 1");
        sb.AppendLine("Set-Dword $tcp 'DisableMediaSenseEventLog' 1");
        sb.AppendLine("Set-Dword $tcp 'EnableICMPRedirect' 0");
        sb.AppendLine("Set-Dword $tcp 'EnableWsd' 0");
        sb.AppendLine("Set-Dword $tcp 'TcpMaxDataRetransmissions' 5");
        sb.AppendLine("Set-Dword $tcp 'KeepAliveTime' 300000");
        sb.AppendLine("Set-Dword $tcp 'KeepAliveInterval' 1000");
        sb.AppendLine("Set-Dword $tcp 'TcpMaxConnectRetransmissions' 2");
        sb.AppendLine("Set-Dword $tcp 'DisableIPSourceRouting' 2");
        sb.AppendLine("Set-Dword $tcp 'EnableDeadGWDetect' 1");
        sb.AppendLine("Set-Dword $tcp 'GlobalMaxTcpWindowSize' 0");
        sb.AppendLine("Set-Dword $tcp 'TcpWindowSize' 0");
        sb.AppendLine("Set-Dword $tcp 'EnableConnectionRateLimiting' 0");
        sb.AppendLine("Set-Dword $tcp 'EnableDCA' 1");
        sb.AppendLine("Set-Dword $tcp 'EnableRSS' 1");
        sb.AppendLine("Set-Dword $tcp 'EnableTCPA' 0");
        sb.AppendLine("Set-Dword $tcp 'EnableTCPChimney' 0");
        sb.AppendLine("Set-Dword $tcp 'QualifyingDestinationThreshold' 3");
        sb.AppendLine("Set-Dword $tcp 'TcpCreateAndConnectTcbRateLimitDepth' 0");
        sb.AppendLine("Set-Dword $tcp 'LargeSystemCache' " + largeCache);
        sb.AppendLine("$tcp6 = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters'");
        sb.AppendLine("Set-Dword $tcp6 'EnablePMTUDiscovery' 1");
        sb.AppendLine("Set-Dword $tcp6 'TcpTimedWaitDelay' 30");
        sb.AppendLine("Set-Dword $tcp6 'DisabledComponents' 0");
        sb.AppendLine("Set-Dword $tcp6 'EnableICMPRedirect' 0");
        sb.AppendLine("Set-Dword $tcp6 'DisableIPSourceRouting' 2");

        // AFD
        sb.AppendLine("""
$afd = 'HKLM:\SYSTEM\CurrentControlSet\Services\AFD\Parameters'
Set-Dword $afd 'FastSendDatagramThreshold' 16384
Set-Dword $afd 'FastCopyReceiveThreshold' 16384
Set-Dword $afd 'DefaultReceiveWindow' 65535
Set-Dword $afd 'DefaultSendWindow' 65535
Set-Dword $afd 'DynamicSendBufferDisable' 0
Set-Dword $afd 'DoNotHoldNicBuffers' 1
Set-Dword $afd 'IgnorePushBitOnReceives' 0
Set-Dword $afd 'NonBlockingSendSpecialBuffering' 1
Set-Dword $afd 'EnableDynamicBacklog' 1
Set-Dword $afd 'MinimumDynamicBacklog' 20
Set-Dword $afd 'MaximumDynamicBacklog' 20000
Set-Dword $afd 'DynamicBacklogGrowthDelta' 10
""");

        // SMB / DNS / NetBT / service provider
        sb.AppendLine("""
$lanman = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'
Set-Dword $lanman 'DisableBandwidthThrottling' 1
Set-Dword $lanman 'MaxCmds' 100
Set-Dword $lanman 'FileInfoCacheLifetime' 1024
Set-Dword $lanman 'DirectoryCacheLifetime' 1024
Set-Dword $lanman 'FileNotFoundCacheLifetime' 1024
Set-Dword $lanman 'MaxCollectionCount' 16
$dnscache = 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters'
Set-Dword $dnscache 'MaxCacheTtl' 86400
Set-Dword $dnscache 'MaxNegativeCacheTtl' 0
Set-Dword $dnscache 'NetFailureCacheTime' 0
Set-Dword $dnscache 'NegativeSOACacheTime' 0
Set-Dword $dnscache 'MaxCacheEntryTtlLimit' 86400
Set-Dword $dnscache 'MaxSOACacheEntryTtlLimit' 300
$sp = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider'
Set-Dword $sp 'LocalPriority' 4
Set-Dword $sp 'HostsPriority' 5
Set-Dword $sp 'DnsPriority' 6
Set-Dword $sp 'NetbtPriority' 7
Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters' 'EnableLMHOSTS' 0
Set-Dword 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters' 'NodeType' 2
""");

        // Multimedia + QoS (SystemResponsiveness 0 = prioritize games for latency preset)
        sb.AppendLine("$mm = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile'");
        sb.AppendLine("Set-Dword $mm 'NetworkThrottlingIndex' ([int]0xFFFFFFFF)");
        sb.AppendLine("Set-Dword $mm 'SystemResponsiveness' " + systemResponsiveness);
        sb.AppendLine("""
$mmGames = Join-Path $mm 'Tasks\Games'
if (-not (Test-Path $mmGames)) { New-Item $mmGames -Force | Out-Null }
Set-Dword $mmGames 'GPU Priority' 8
Set-Dword $mmGames 'Priority' 6
try { Set-ItemProperty $mmGames -Name 'Scheduling Category' -Value 'High' -Force -EA SilentlyContinue } catch {}
try { Set-ItemProperty $mmGames -Name 'SFIO Priority' -Value 'High' -Force -EA SilentlyContinue } catch {}
New-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' -Force | Out-Null
Set-Dword 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Psched' 'NonBestEffortLimit' 0
""");

        // WinINET / DO / BITS
        sb.AppendLine("""
$ie = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings'
Set-Dword $ie 'MaxConnectionsPerServer' 16
Set-Dword $ie 'MaxConnectionsPer1_0Server' 16
$ie64 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Internet Settings'
if (Test-Path $ie64) {
  Set-Dword $ie64 'MaxConnectionsPerServer' 16
  Set-Dword $ie64 'MaxConnectionsPer1_0Server' 16
}
try { Set-Dword 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config' 'DODownloadMode' 1 } catch {}
try {
  $bits = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\BITS'
  New-Item $bits -Force | Out-Null
  Set-Dword $bits 'EnableBITSMaxBandwidth' 0
} catch {}
""");

        // netsh TCP global
        sb.AppendLine("netsh int tcp set global rss=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global autotuninglevel=" + autotune + " | Out-Null");
        sb.AppendLine("netsh int tcp set global ecncapability=disabled | Out-Null");
        sb.AppendLine("netsh int tcp set global timestamps=disabled | Out-Null");
        sb.AppendLine("netsh int tcp set global initialRto=2000 | Out-Null");
        sb.AppendLine("netsh int tcp set global rsc=" + rsc + " | Out-Null");
        sb.AppendLine("netsh int tcp set global nonsackrttresiliency=disabled | Out-Null");
        sb.AppendLine("netsh int tcp set global maxsynretransmissions=2 | Out-Null");
        sb.AppendLine("netsh int tcp set global fastopen=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global fastopenfallback=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global hystart=enabled | Out-Null");
        sb.AppendLine("netsh int tcp set global prr=enabled | Out-Null");
        sb.AppendLine("try { netsh int tcp set global pacingprofile=off | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internetcustom congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=datacenter congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=compat congestionprovider=cubic | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet congestionprovider=bbr2 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set heuristics disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set global chimney=disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set global dca=enabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set global netdma=disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int tcp set supplemental template=internet initialCwnd=" + initialCwnd + " | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ip set global taskoffload=enabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set global icmpredirects=disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set global icmpredirects=disabled | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv4 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport tcp start=1025 num=64511 | Out-Null } catch {}");
        sb.AppendLine("try { netsh int ipv6 set dynamicport udp start=1025 num=64511 | Out-Null } catch {}");

        // Set-NetTCPSetting
        sb.AppendLine("$profs = @('Internet','InternetCustom','Datacenter','DatacenterCustom','Compat')");
        sb.AppendLine("foreach ($pr in $profs) {");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -CongestionProvider CUBIC -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -InitialCongestionWindow " + initialCwnd + " -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -AutoTuningLevelLocal " + autoTuningPs + " -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -ScalingHeuristics Disabled -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -EcnCapability Disabled -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetTCPSetting -SettingName $pr -Timestamps Disabled -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("}");

        // Per-interface Nagle/ACK
        sb.AppendLine("Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces' -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("  $p = $_.PSPath");
        sb.AppendLine(ackBlock);
        sb.AppendLine("  Set-Dword $p 'TcpInitialRTT' 3");
        sb.AppendLine("  Set-Dword $p 'TcpWindowSize' 0");
        sb.AppendLine("}");

        // NIC loop
        sb.AppendLine("$adapters = @(Get-NetAdapter -EA SilentlyContinue | Where-Object { $_.Status -eq 'Up' -or $_.Status -eq 'Disconnected' })");
        sb.AppendLine("if ($adapters.Count -eq 0) { $adapters = @(Get-NetAdapter -Physical -EA SilentlyContinue) }");
        sb.AppendLine("foreach ($a in $adapters) {");
        sb.AppendLine("  $n = $a.Name");
        sb.AppendLine("  Write-Host \"[NIC] $n ($($a.InterfaceDescription))\"");
        sb.AppendLine("  foreach ($kw in @('*IPChecksumOffloadIPv4','*TCPChecksumOffloadIPv4','*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv4','*UDPChecksumOffloadIPv6')) { Set-Adv $n $kw 3 }");
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv4' " + lso);
        sb.AppendLine("  Set-Adv $n '*LsoV2IPv6' " + lso);
        sb.AppendLine("  Set-Adv $n '*LsoV1IPv4' " + lso);
        sb.AppendLine("  Set-Adv $n '*IPsecOffloadV1IPv4' 0");
        sb.AppendLine("  Set-Adv $n '*IPsecOffloadV2' 0");
        sb.AppendLine("  Set-Adv $n '*IPsecOffloadV2IPv4' 0");
        sb.AppendLine("  Set-Adv $n '*PMARPOffload' 0");
        sb.AppendLine("  Set-Adv $n '*PMNSOffload' 0");
        sb.AppendLine("  Set-Adv $n '*InterruptModeration' " + im);
        sb.AppendLine("  try {");
        sb.AppendLine("    if (" + im + " -eq 1) {");
        sb.AppendLine("      $itr = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword ITR -EA SilentlyContinue");
        sb.AppendLine("      if ($itr) {");
        sb.AppendLine("        $vals = @($itr.ValidDisplayValues)");
        sb.AppendLine("        if ($vals -contains 'Medium') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Medium' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("        elseif ($vals -contains 'Adaptive') { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Adaptive' -NoRestart -EA SilentlyContinue }");
        sb.AppendLine("        else { Set-Adv $n 'ITR' 200 }");
        sb.AppendLine("      }");
        sb.AppendLine("    } else {");
        sb.AppendLine("      Set-Adv $n 'ITR' 0");
        sb.AppendLine("      try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Interrupt Moderation Rate' -DisplayValue 'Off' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  foreach ($kw in @('*EEE','*EnergyEfficientEthernet','*GreenEthernet','*SelectiveSuspend','*IdleRestriction','*ReduceSpeedOnPowerDown','*UltraLowPowerMode','EnablePME')) { Set-Adv $n $kw 0 }");
        sb.AppendLine("  try { Disable-NetAdapterPowerManagement -Name $n -SelectiveSuspend -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetAdapterPowerManagement -Name $n -SelectiveSuspend Disabled -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetAdapterPowerManagement -Name $n -WakeOnMagicPacket Disabled -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Set-NetAdapterPowerManagement -Name $n -WakeOnPattern Disabled -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Disable-NetAdapterLso -Name $n -IPv4 -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try { Disable-NetAdapterLso -Name $n -IPv6 -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  if (" + lso + " -eq 1) {");
        sb.AppendLine("    try { Enable-NetAdapterLso -Name $n -IPv4 -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { Enable-NetAdapterLso -Name $n -IPv6 -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        if (rsc == "enabled")
            sb.AppendLine("    Enable-NetAdapterRsc -Name $n -EA SilentlyContinue");
        else
            sb.AppendLine("    Disable-NetAdapterRsc -Name $n -EA SilentlyContinue");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  try {");
        sb.AppendLine("    $fc = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*FlowControl' -EA SilentlyContinue");
        sb.AppendLine("    if ($fc -and $fc.ValidRegistryValues -contains 3) { Set-Adv $n '*FlowControl' 3 }");
        sb.AppendLine("    elseif ($fc -and $fc.ValidRegistryValues -contains 0) { Set-Adv $n '*FlowControl' 0 }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  Set-Adv $n '*RSS' 1");
        sb.AppendLine("  try { Set-NetAdapterRss -Name $n -Enabled $true -EA SilentlyContinue } catch {}");
        sb.AppendLine("  try {");
        sb.AppendLine("    $q = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*NumRssQueues' -EA SilentlyContinue");
        sb.AppendLine("    if ($q -and $q.ValidRegistryValues) {");
        sb.AppendLine("      $max = ($q.ValidRegistryValues | Measure-Object -Maximum).Maximum");
        sb.AppendLine("      if ($max -gt 0) { Set-Adv $n '*NumRssQueues' ([int]$max) }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  foreach ($kw in @('*ReceiveBuffers','*TransmitBuffers','ReceiveBuffers','TransmitBuffers')) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $prop = Get-NetAdapterAdvancedProperty -Name $n -RegistryKeyword $kw -EA SilentlyContinue");
        sb.AppendLine("      if ($prop -and $prop.ValidRegistryValues) {");
        sb.AppendLine("        $max = ($prop.ValidRegistryValues | Measure-Object -Maximum).Maximum");
        sb.AppendLine("        if ($max -gt 0) { Set-Adv $n $kw ([int]$max) }");
        sb.AppendLine("      }");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($kw in @('*PacketCoalescing','*RscIPv4','*RscIPv6')) { Set-Adv $n $kw " + packetCoal + " }");
        sb.AppendLine("  try { Set-Adv $n '*AdaptiveIFS' 0 } catch {}");
        sb.AppendLine("  try { Set-Adv $n '*PriorityVLANTag' 3 } catch {}");
        sb.AppendLine("  if ($a.InterfaceDescription -match 'Wi-?Fi|Wireless|802\\.11|WLAN|MediaTek|Realtek.*802|Intel.*Wi') {");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Preferred Band' -DisplayValue 'Prefer 5GHz band' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Preferred Band' -DisplayValue '5GHz only' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Roaming Aggressiveness' -DisplayValue 'Medium' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    foreach ($dn in @('MIMO Power Save Mode','MIMO Power Save','uAPSD support','Power Saving Mode')) {");
        sb.AppendLine("      try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $dn -DisplayValue 'Disabled' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("      try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $dn -DisplayValue 'Off' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("    }");
        sb.AppendLine("    try { Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Throughput Booster' -DisplayValue 'Enabled' -NoRestart -EA SilentlyContinue } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("  try {");
        sb.AppendLine("    $class = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}'");
        sb.AppendLine("    Get-ChildItem $class -EA SilentlyContinue | ForEach-Object {");
        sb.AppendLine("      $props = Get-ItemProperty $_.PSPath -EA SilentlyContinue");
        sb.AppendLine("      if ($props.DriverDesc -eq $a.InterfaceDescription) {");
        sb.AppendLine("        Set-ItemProperty $_.PSPath -Name PnPCapabilities -Value 24 -Type DWord -Force -EA SilentlyContinue");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {}");
        sb.AppendLine("  if ($a.InterfaceDescription -notmatch 'PPPoE|VPN|TAP|TUN|Virtual|Hyper-V|VMware|VirtualBox|WSL|Bluetooth') {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $ifIndex = $a.ifIndex");
        sb.AppendLine("      netsh interface ipv4 set subinterface $ifIndex mtu=1500 store=persistent | Out-Null");
        sb.AppendLine("    } catch {}");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        sb.AppendLine("try { Clear-DnsClientCache -EA SilentlyContinue } catch {}");
        sb.AppendLine("try { ipconfig /flushdns | Out-Null } catch {}");
        sb.AppendLine("try {");
        sb.AppendLine("  powercfg /SETACVALUEINDEX SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null");
        sb.AppendLine("  powercfg /SETDCVALUEINDEX SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0 | Out-Null");
        sb.AppendLine("  powercfg /SETACTIVE SCHEME_CURRENT | Out-Null");
        sb.AppendLine("} catch {}");
        sb.AppendLine("foreach ($a in @($adapters | Where-Object Status -eq 'Up')) {");
        sb.AppendLine("  try { Restart-NetAdapter -Name $a.Name -Confirm:$false -EA SilentlyContinue; Write-Host \"[NIC] restarted $($a.Name)\" } catch {}");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Seconds 3");
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
