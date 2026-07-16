using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Exo.Models;
using Microsoft.Win32;

namespace Exo.Services;

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
        "Exo", "network-optimizer.json");

    /// <summary>Pristine pre-apply baseline written by the elevated apply script (never overwritten).</summary>
    private static readonly string SnapshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Exo", "network-snapshot.json");

    /// <summary>Honest apply outcome (rollback marker + disabled Wi‑Fi record) from the elevated script.</summary>
    private static readonly string ApplyStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Exo", "network-apply-state.json");

    /// <summary>Elevated apply run log (EXO_REPORT structured lines).</summary>
    private static string ApplyLogPath => Path.Combine(Path.GetTempPath(), "exo-net-last.log");

    /// <summary>True when a pristine pre-apply snapshot exists (true restore is possible).</summary>
    public static bool HasRestoreSnapshot()
    {
        try { return File.Exists(SnapshotPath); }
        catch { return false; }
    }

    private JsonObject LoadStateObject()
    {
        try
        {
            if (File.Exists(StatePath) &&
                JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject o)
                return o;
        }
        catch { }
        return new JsonObject();
    }

    private void SaveStateObject(JsonObject state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

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

    public void SavePreset(NetworkPreset preset, NetworkApplyOptions? options = null)
    {
        try
        {
            // Merge-write: keep benchmark / report / rollback keys intact across preset saves.
            var state = LoadStateObject();
            state["preset"] = preset.ToString();
            state["appliedUtc"] = DateTime.UtcNow.ToString("o");
            // Used by probe so "Wi‑Fi while Ethernet" only fails when we actually preferred eth-first
            state["preferEthernetDisableWifi"] = options?.PreferEthernetDisableWifi ?? true;
            SaveStateObject(state);
        }
        catch { }
    }

    /// <summary>Last apply chose Ethernet-first (Wi‑Fi off when Ethernet has IP). Default true.</summary>
    public bool LoadPreferEthernetDisableWifi()
    {
        try
        {
            if (!File.Exists(StatePath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            if (doc.RootElement.TryGetProperty("preferEthernetDisableWifi", out var p))
            {
                if (p.ValueKind == JsonValueKind.False) return false;
                if (p.ValueKind == JsonValueKind.True) return true;
            }
        }
        catch { }
        return true;
    }

    public void ClearSavedPreset()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch { }
        try
        {
            if (File.Exists(ApplyStatePath)) File.Delete(ApplyStatePath);
        }
        catch { }
    }

    /// <summary>
    /// Undo Exo network apply (elevated). TRUE RESTORE from the pristine pre-apply
    /// snapshot when %LocalAppData%\Exo\network-snapshot.json exists; otherwise an
    /// approximate stock reset (fallback). Wi‑Fi Exo disabled is always re-enabled.
    /// </summary>
    public async Task<(bool Ok, string Message)> RepairAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var hadSnapshot = HasRestoreSnapshot();
        progress?.Report(hadSnapshot
            ? "Preparing repair (exact restore from pre-apply snapshot)..."
            : "Preparing repair (stock network stack — no snapshot found)...");
        var script = NetworkApplyScriptBuilder.BuildRepair();
        var path = Path.Combine(Path.GetTempPath(), $"exo-net-repair-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, script, ct).ConfigureAwait(false);
        try
        {
            progress?.Report("Repairing network stack (elevated)...");
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
            // Exit 2 = hard winsock/ip reset applied; connectivity needs a reboot.
            if (p.ExitCode == 2)
            {
                progress?.Report("Clearing Exo network preset...");
                ClearSavedPreset();
                return (false,
                    "Repair applied a hard Winsock/IP reset because connectivity was still down. Reboot Windows now, then retry — if it is still broken, run Repair-Internet.ps1 -Hard from an admin PowerShell (or use a phone hotspot temporarily).");
            }
            if (p.ExitCode != 0)
                return (false, $"Repair exit {p.ExitCode}. Try again as Administrator, or run Repair-Internet.ps1 -Hard from an elevated PowerShell.");

            progress?.Report("Clearing Exo network preset...");
            ClearSavedPreset();
            await Task.Delay(800, ct).ConfigureAwait(false);
            if (hadSnapshot && HasRestoreSnapshot())
            {
                // Snapshot kept = script recorded restore failures; be honest, allow retry.
                return (true, "Repair ran, but some values could not be restored exactly — the snapshot was kept so you can retry Repair. Adapters were re-enabled; reboot if the link stays down.");
            }
            return (true, hadSnapshot
                ? "Network restored to the exact pre-Exo state from the snapshot. Adapters re-enabled; snapshot cleared."
                : "Network stack repaired to stock-like defaults (no snapshot was available). Critical bindings restored; adapters re-enabled.");
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
    /// Proof layer: run the non-elevated ping/DNS benchmark script and parse its
    /// EXO_BENCH JSON line. Returns null when the benchmark could not run/parse.
    /// </summary>
    public async Task<NetworkBenchmarkResult?> RunBenchmarkAsync(CancellationToken ct = default)
    {
        var script = NetworkApplyScriptBuilder.BuildBenchmark();
        var path = Path.Combine(Path.GetTempPath(), $"exo-net-bench-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(path, script, ct).ConfigureAwait(false);
            var stdout = await RunCaptureAsync(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                ct).ConfigureAwait(false);
            return NetworkLogic.TryParseBenchmark(stdout);
        }
        catch { return null; }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Persisted before/after benchmark pair (state JSON: benchmark.before / benchmark.after).</summary>
    public (NetworkBenchmarkResult? Before, NetworkBenchmarkResult? After) LoadBenchmark()
    {
        try
        {
            var state = LoadStateObject();
            if (state["benchmark"] is not JsonObject bench) return (null, null);
            NetworkBenchmarkResult? Read(string key) =>
                bench[key] is JsonObject o
                    ? JsonSerializer.Deserialize<NetworkBenchmarkResult>(o.ToJsonString(), BenchJson)
                    : null;
            return (Read("before"), Read("after"));
        }
        catch { return (null, null); }
    }

    /// <summary>Structured step list parsed from the last elevated apply run (EXO_REPORT lines).</summary>
    public IReadOnlyList<NetworkApplyReportStep> LoadLastApplyReport()
    {
        try
        {
            var state = LoadStateObject();
            if (state["lastApplyReport"] is not JsonArray arr) return Array.Empty<NetworkApplyReportStep>();
            var list = new List<NetworkApplyReportStep>();
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var name = o["name"]?.GetValue<string>();
                var status = o["status"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(status)) continue;
                list.Add(new NetworkApplyReportStep
                {
                    Name = name,
                    Status = status,
                    Reason = o["reason"]?.GetValue<string>() ?? string.Empty
                });
            }
            return list;
        }
        catch { return Array.Empty<NetworkApplyReportStep>(); }
    }

    /// <summary>
    /// Honest rollback status recorded by the elevated apply script
    /// (%LocalAppData%\Exo\network-apply-state.json). Null when no apply ran yet.
    /// </summary>
    public NetworkRollbackStatus? LoadRollbackStatus()
    {
        try
        {
            if (!File.Exists(ApplyStatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ApplyStatePath));
            var root = doc.RootElement;
            var wifi = new List<string>();
            if (root.TryGetProperty("wifiDisabled", out var wd) && wd.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wd.EnumerateArray())
                    if (w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } s)
                        wifi.Add(s);
            }
            return new NetworkRollbackStatus
            {
                RolledBack = root.TryGetProperty("rollback", out var rb) && rb.ValueKind == JsonValueKind.True,
                Reason = root.TryGetProperty("rollbackReason", out var rr) && rr.ValueKind == JsonValueKind.String
                    ? rr.GetString() ?? string.Empty : string.Empty,
                ConnectivityAfterApply = !root.TryGetProperty("connectivityAfterApply", out var ca) ||
                    ca.ValueKind != JsonValueKind.False,
                WifiDisabled = wifi,
                AppliedUtc = root.TryGetProperty("appliedUtc", out var au) && au.ValueKind == JsonValueKind.String
                    ? au.GetString() ?? string.Empty : string.Empty
            };
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions BenchJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private void PersistBenchmark(NetworkBenchmarkResult? before, NetworkBenchmarkResult? after)
    {
        try
        {
            var state = LoadStateObject();
            var bench = state["benchmark"] as JsonObject ?? new JsonObject();
            if (before is not null)
                bench["before"] = JsonSerializer.SerializeToNode(before, BenchJson);
            if (after is not null)
                bench["after"] = JsonSerializer.SerializeToNode(after, BenchJson);
            state["benchmark"] = bench;
            SaveStateObject(state);
        }
        catch { }
    }

    private void PersistApplyOutcome(IReadOnlyList<NetworkApplyReportStep> report, NetworkRollbackStatus? rollback)
    {
        try
        {
            var state = LoadStateObject();
            var arr = new JsonArray();
            foreach (var step in report)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = step.Name,
                    ["status"] = step.Status,
                    ["reason"] = step.Reason
                });
            }
            state["lastApplyReport"] = arr;
            if (rollback is not null)
            {
                var wifi = new JsonArray();
                foreach (var w in rollback.WifiDisabled) wifi.Add(w);
                state["rollback"] = new JsonObject
                {
                    ["rolledBack"] = rollback.RolledBack,
                    ["reason"] = rollback.Reason,
                    ["connectivityAfterApply"] = rollback.ConnectivityAfterApply,
                    ["wifiDisabled"] = wifi,
                    ["appliedUtc"] = rollback.AppliedUtc
                };
            }
            SaveStateObject(state);
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

            // MMCSS targets: SystemResponsiveness=10, NetworkThrottlingIndex=10 (never 0 / never ffffffff)
            // Registry DWORD may surface as int/long/uint/string depending on writer — parse flexibly.
            var mmOk = false;
            var thrStatus = "—";
            try
            {
                using var mm = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                var resp = ReadRegistryDword(mm?.GetValue("SystemResponsiveness"));
                var thr = ReadRegistryDword(mm?.GetValue("NetworkThrottlingIndex"));
                // Missing responsiveness → OS default 20 (not our apply); after apply must be 10.
                // For Balanced (no apply), treat missing as soft OK. For latency/throughput, need 10.
                var respOk = resp is null
                    ? activePreset == NetworkPreset.Balanced
                    : resp == 10;
                // Missing throttle = OS default 10 (OK). Explicit 10 = OK. 0 / ffffffff(-1) / other = not OK.
                var thrOk = thr is null or 10;
                thrStatus = thr is null ? "default" : thr == 10 ? "10" : thr is -1 ? "ffffffff (bad)" : thr.ToString()!;
                mmOk = respOk && thrOk;
            }
            catch { }

            // Light pings only for status (not feature cards)
            if (gateway is not "—" && System.Net.IPAddress.TryParse(gateway, out _))
                gwPing = await PingMsAsync(gateway, ct).ConfigureAwait(false);
            netPing = await PingMsAsync("1.1.1.1", ct).ConfigureAwait(false)
                      ?? await PingMsAsync("8.8.8.8", ct).ConfigureAwait(false);

            // Feature cards = optimizer knobs only (aligned with NetworkLogic.KnobsFor)
            var lsoOk = NetworkLogic.LsoMatches(activePreset, lso);
            var rscOk = NetworkLogic.RscMatches(activePreset, rsc);
            var autoOk = NetworkLogic.AutotuneMatches(activePreset, autoTuning);
            var nagleOk = !latency || nagleOff != false;
            if (throughput) nagleOk = nagleOff != true;

            features.Add(Row("Task offload", taskOffloadDisabled == true ? "Off (bad)" : "On", taskOffloadDisabled != true));
            features.Add(Row("LSO v2",
                lso == true ? (throughput ? "On · download" : "On")
                    : lso == false ? (latency ? "Off · latency" : "Off") : "—",
                lsoOk));
            features.Add(Row("RSC",
                rsc == true ? (throughput ? "On · download" : "On")
                    : rsc == false ? (latency ? "Off · latency" : "Off") : "—",
                rscOk));
            features.Add(Row("Auto-tuning",
                autoOk ? autoTuning : $"{autoTuning} (want {NetworkLogic.KnobsFor(activePreset).AutotuneNetsh})",
                autoOk));
            features.Add(Row("Congestion", congestion, true));
            features.Add(Row("Nagle / ACK",
                nagleOff == true ? "Off (latency)" : nagleOff == false ? "Default" : "—",
                nagleOk));
            features.Add(Row("MMCSS",
                mmOk ? "Responsiveness 10 · throttle 10" : $"Needs apply (throttle {thrStatus})",
                mmOk || activePreset == NetworkPreset.Balanced));
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
            // Adapter Properties checkboxes (Ethernet Properties → Networking)
            if (mediaProfile.EthernetAvailable || mediaProfile.WifiAvailable)
            {
                var presetApplied = LoadSavedPreset() is NetworkPreset.LowestLatency
                    or NetworkPreset.HighestThroughput;
                var bindStatus = mediaProfile.AdapterBindingsOk
                    ? "Applied (QoS+IP on · Client/LLDP off)"
                    : mediaProfile.AdapterBindingsHint is ("—" or "")
                        ? "Needs apply"
                        : mediaProfile.AdapterBindingsHint;
                // Stock Windows bindings are fine until user applies a preset.
                features.Add(Row("Adapter bindings", bindStatus,
                    mediaProfile.AdapterBindingsOk || !presetApplied));
            }

            if (mediaProfile.EthernetInUse)
            {
                // Applied path sets primary Ethernet to 1 (secondaries 5+).
                // AutomaticMetric Enabled with ~20–25 means apply did not stick (common after restart race).
                var metricOk = mediaProfile.EthernetMetric is null or <= 5;
                var metricStatus = mediaProfile.EthernetMetric is int m
                    ? (metricOk
                        ? m.ToString()
                        : $"{m} (want 1 · re-apply)")
                    : "—";
                features.Add(Row("Ethernet metric", metricStatus, metricOk));
                if (mediaProfile.WifiAvailable)
                {
                    // Only hard-fail when last apply chose Ethernet-first. If user kept Wi‑Fi, show info OK.
                    var preferEth = LoadPreferEthernetDisableWifi();
                    if (preferEth)
                    {
                        features.Add(Row("Wi‑Fi while Ethernet",
                            mediaProfile.WifiUp ? "Still up" : "Disabled / down",
                            !mediaProfile.WifiUp));
                    }
                    else
                    {
                        features.Add(Row("Wi‑Fi while Ethernet",
                            mediaProfile.WifiUp ? "Up (kept)" : "Down",
                            true));
                    }
                }
            }
            if (mediaProfile.WifiAvailable)
            {
                var gen = mediaProfile.ClientSupportsWifi7 ? "Wi‑Fi 7"
                    : mediaProfile.ClientSupports6Ghz ? "Wi‑Fi 6E/6 GHz"
                    : mediaProfile.ClientSupportsWifi6 ? "Wi‑Fi 6"
                    : mediaProfile.ClientSupports5Ghz ? "5 GHz class" : "Legacy";
                var bandDetail = $"Prefer {mediaProfile.PreferredBandTarget} · {gen}";
                if (mediaProfile.ConnectedRadioHint is not "—")
                    bandDetail += $" · {mediaProfile.ConnectedRadioHint}";
                if (mediaProfile.CurrentBandSetting is not ("—" or ""))
                    bandDetail += $" · set: {mediaProfile.CurrentBandSetting}";
                features.Add(Row("Wi‑Fi capability", bandDetail, true));
            }
            if (!string.IsNullOrWhiteSpace(mediaProfile.NicHints) && mediaProfile.NicHints is not "—")
                features.Add(Row("NIC status", mediaProfile.NicHints, mediaProfile.NicOk));
            // Path / media only (no Windows Game Mode / CPU plan rows — those are not Internet)
            if (!string.IsNullOrWhiteSpace(mediaProfile.NicVendor) &&
                mediaProfile.NicVendor is not ("Unknown" or "Other" or ""))
            {
                var link = mediaProfile.PrimaryLinkSpeedBps >= 2_500_000_000 ? "2.5G+"
                    : mediaProfile.PrimaryLinkSpeedBps >= 1_000_000_000 ? "1G"
                    : mediaProfile.PrimaryLinkSpeedBps >= 100_000_000 ? "100M" : "—";
                features.Add(Row("Adapter",
                    $"{mediaProfile.PrimaryMediaKind} · {mediaProfile.NicVendor} · {link}",
                    true));
            }
        }
        catch { }

        // Honest last-apply surface: rollback marker written by the elevated apply script.
        try
        {
            var rollback = LoadRollbackStatus();
            if (rollback is { RolledBack: true })
            {
                features.Add(Row("Last apply",
                    $"Auto-rollback ({rollback.Reason}) — Wi‑Fi restored",
                    false));
            }
            else if (rollback is { ConnectivityAfterApply: false })
            {
                features.Add(Row("Last apply", "Connectivity check failed after apply", false));
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
    /// Deep local detection: PhysicalMediaType, usable Ethernet, Wi‑Fi 5/6/6E/7 radios, connected band.
    /// See docs/INTERNET-GOLDEN-PATH.md. No cloud model.
    /// </summary>
    public async Task<NetworkMediaProfile> DetectMediaProfileAsync(CancellationToken ct = default)
    {
        var ethAvail = false;
        var ethUp = false;
        var ethInUse = false;
        var wifiAvail = false;
        var wifiUp = false;
        var supports6 = false;
        var supports5 = false;
        var wifi6 = false;
        var wifi7 = false;
        var radioHint = "—";
        var driverRadios = "—";
        var currentBand = "—";
        int? ethMetric = null;
        var nicHints = "—";
        var nicOk = true;
        int fcR = -1, imR = -1, idleR = -1, ssR = -1;
        var bindOk = true;
        var bindHint = "—";
        var nicVendor = "Unknown";
        var primaryMedia = "Unknown";
        long linkBps = 0;
        var isLaptop = false;
        var logicals = Environment.ProcessorCount;
        var physicalCores = 0;
        var activePreset = LoadSavedPreset();

        try
        {
            var probePs = Path.Combine(Path.GetTempPath(), $"exo-media-{Guid.NewGuid():N}.ps1");
            // Detection matrix:
            // - PhysicalMediaType: Native 802.11 = Wi-Fi, 802.3 = Ethernet (MS Get-NetAdapter)
            // - Usable Ethernet: Up + IPv4 not APIPA + InterfaceMetric
            // - Bands: Preferred Band valid values + netsh wlan show drivers (Radio types)
            // - Connected: netsh wlan show interfaces (Band / Radio type / Channel)
            // - NIC status: Flow Control, SelectiveSuspend, InterruptModeration, IdleRestriction
            await File.WriteAllTextAsync(probePs, """
$ErrorActionPreference = 'SilentlyContinue'
function IsWifi($a) {
  # Mirrors NetworkLogic.IsWifiAdapter
  $pm = [string]$a.PhysicalMediaType
  $m  = [string]$a.MediaType
  $d  = [string]$a.InterfaceDescription
  $n  = [string]$a.Name
  if ($pm -match '(?i)Native 802\.11|802\.11|Wireless') { return $true }
  if ($pm -match '(?i)^802\.3$') { return $false }
  if ($m -match '(?i)Native 802|802\.11|Wireless|Wi-?Fi') { return $true }
  if ($d -match '(?i)Bluetooth|Virtual|Hyper-V|vEthernet|TAP-|TUN-|WireGuard|OpenVPN|Wintun|Meta\s*Tunnel') { return $false }
  if ($d -match '(?i)Wi-?Fi|Wireless|802\.11|WLAN|MediaTek.*Wi|Intel.*Wi-?Fi|Realtek.*802\.11|Killer.*Wireless|Qualcomm.*Wi|Broadcom.*802|AX\d{3,4}|BE\d{3,4}|Wi-Fi\s*\d') { return $true }
  if ($n -match '(?i)^Wi-?Fi|Wireless|WLAN') { return $true }
  return $false
}
$phys = @(Get-NetAdapter -Physical -EA SilentlyContinue)
$eth = @($phys | Where-Object { -not (IsWifi $_) })
$wifi = @($phys | Where-Object { IsWifi $_ })
$eUp = @($eth | Where-Object Status -eq 'Up').Count -gt 0
$wUp = @($wifi | Where-Object Status -eq 'Up').Count -gt 0
$eInUse = $false
$eMetric = -1
$bestEth = $null
foreach ($e in @($eth | Where-Object Status -eq 'Up')) {
  $ip = @(Get-NetIPAddress -InterfaceIndex $e.ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '169.254.*' })
  if ($ip.Count -gt 0) {
    $eInUse = $true
    # Prefer ReceiveLinkSpeed (bps int) — LinkSpeed is a display string and sorts wrong
    $spd = 0L
    try { $spd = [int64]$e.ReceiveLinkSpeed } catch { $spd = 0 }
    $bestSpd = 0L
    if ($bestEth) { try { $bestSpd = [int64]$bestEth.ReceiveLinkSpeed } catch { $bestSpd = 0 } }
    if (-not $bestEth -or $spd -gt $bestSpd) { $bestEth = $e }
  }
}
if ($bestEth) {
  $mi = Get-NetIPInterface -InterfaceIndex $bestEth.ifIndex -AddressFamily IPv4 -EA SilentlyContinue
  if ($mi) {
    $eMetric = [int]$mi.InterfaceMetric
    # When AutomaticMetric is still on, Windows shows speed-based defaults (~20–25) — not our apply
    if ($mi.AutomaticMetric -eq 'Enabled' -and $eMetric -gt 5) {
      # Keep real live metric for UI; feature row will fail until apply sticks
    }
  }
}
$band6 = $false; $band5 = $false; $ax = $false; $be = $false
$curBand = '-'
foreach ($w in $wifi) {
  foreach ($p in @(Get-NetAdapterAdvancedProperty -Name $w.Name -EA SilentlyContinue)) {
    $blob = "$($p.DisplayName) $($p.DisplayValue) $(($p.ValidDisplayValues) -join ' ')"
    if ($blob -match '(?i)6\s*GHz|6GHz|Wi-?Fi\s*6E|Prefer\s*6|6\s*GHz\s*prefer|band\s*6') { $band6 = $true }
    if ($blob -match '(?i)5\s*GHz|5GHz|Prefer\s*5|5\s*GHz\s*prefer|5\.2\s*GHz|band\s*5') { $band5 = $true }
    if ($blob -match '(?i)802\.11be|Wi-?Fi\s*7') { $be = $true; $band6 = $true }
    if ($blob -match '(?i)802\.11ax|Wi-?Fi\s*6') { $ax = $true }
    if ([string]$p.DisplayName -match '(?i)preferred\s*band|preferable\s*band|band\s*pref') {
      if ($p.DisplayValue) { $curBand = [string]$p.DisplayValue }
    }
  }
}
$drv = (netsh wlan show drivers 2>$null | Out-String)
$radios = '-'
if ($drv -match '(?i)Radio types supported\s*:\s*(.+)') { $radios = $Matches[1].Trim() -replace '\s+',' ' }
if ($drv -match '(?i)802\.11be') { $be = $true; $band6 = $true }
if ($drv -match '(?i)802\.11ax') { $ax = $true }
if ($drv -match '(?i)6\s*GHz|Wi-?Fi\s*6E') { $band6 = $true }
if ($drv -match '(?i)802\.11a|802\.11n|802\.11ac|802\.11ax|5\s*GHz') { $band5 = $true }
if ($wifi.Count -gt 0 -and -not $band5 -and -not $band6) { $band5 = $true }
$iface = (netsh wlan show interfaces 2>$null | Out-String)
$hint = '-'
if ($iface -match '(?i)Band\s*:\s*(.+)') { $hint = $Matches[1].Trim() }
elseif ($iface -match '(?i)Radio type\s*:\s*(.+)') { $hint = $Matches[1].Trim() }
if ($iface -match '(?i)Channel\s*:\s*(\d+)') {
  $ch = [int]$Matches[1]
  if ($hint -eq '-') { $hint = "ch $ch" } else { $hint = "$hint · ch $ch" }
}
if ($hint -match '(?i)6\s*GHz|6GHz') { $band6 = $true }
if ($hint -match '(?i)5\s*GHz|5GHz') { $band5 = $true }
if ($hint -match '(?i)802\.11be') { $be = $true }
if ($hint -match '(?i)802\.11ax') { $ax = $true }
# Raw NIC facts (C# scores preset-aware via NetworkLogic.EvaluateNic)
# FC/IM/IDLE/SS: 1=on, 0=off, -1=not exposed
$fcR = -1; $imR = -1; $idleR = -1; $ssR = -1
$primary = if ($bestEth) { $bestEth } else { @($phys | Where-Object Status -eq 'Up' | Select-Object -First 1) }
if ($primary) {
  $fc = Get-NetAdapterAdvancedProperty -Name $primary.Name -RegistryKeyword '*FlowControl' -EA SilentlyContinue
  if ($fc) {
    $fcR = if ([string]$fc.DisplayValue -match '(?i)^Disabled') { 0 }
           elseif ([string]$fc.DisplayValue -match '(?i)Rx|Tx|Enabled') { 1 } else { -1 }
  }
  $im = Get-NetAdapterAdvancedProperty -Name $primary.Name -RegistryKeyword '*InterruptModeration' -EA SilentlyContinue
  if ($im) {
    $imR = if ([string]$im.DisplayValue -match '(?i)Enabled|On') { 1 }
           elseif ([string]$im.DisplayValue -match '(?i)Disabled|Off') { 0 } else { -1 }
  }
  $ss = Get-NetAdapterAdvancedProperty -Name $primary.Name -RegistryKeyword '*SelectiveSuspend' -EA SilentlyContinue
  if ($ss) {
    $ssR = if ([string]$ss.DisplayValue -match '(?i)Enabled|On') { 1 }
           elseif ([string]$ss.DisplayValue -match '(?i)Disabled|Off') { 0 } else { -1 }
  }
  $idle = Get-NetAdapterAdvancedProperty -Name $primary.Name -RegistryKeyword '*IdleRestriction' -EA SilentlyContinue
  if ($idle) {
    # Enabled = restriction ON (prevent idle) → idleR=1
    $idleR = if ([string]$idle.DisplayValue -match '(?i)^Enabled') { 1 }
             elseif ([string]$idle.DisplayValue -match '(?i)^Disabled') { 0 } else { -1 }
  }
}
# Ethernet Properties checkbox bindings (ComponentIDs)
# Target: pacer+tcpip+tcpip6 ON; client/server/lldp/lltd/implat OFF
$bindOk = 1
$bindHint = '-'
$bindProbe = if ($bestEth) { $bestEth } else { @($eth | Select-Object -First 1) }
if ($bindProbe) {
  $wantOn = @('ms_pacer','ms_tcpip','ms_tcpip6')
  $wantOff = @('ms_msclient','ms_server','ms_implat','ms_lldp','ms_lltdio','ms_rspndr')
  $gaps = @()
  $all = @(Get-NetAdapterBinding -Name $bindProbe.Name -EA SilentlyContinue)
  foreach ($id in $wantOn) {
    $row = $all | Where-Object { $_.ComponentID -eq $id } | Select-Object -First 1
    if ($row -and -not $row.Enabled) { $gaps += "$id off" }
  }
  foreach ($id in $wantOff) {
    $row = $all | Where-Object { $_.ComponentID -eq $id } | Select-Object -First 1
    if ($row -and $row.Enabled) { $gaps += "$id on" }
  }
  if ($gaps.Count -gt 0) { $bindOk = 0; $bindHint = ($gaps -join ', ') } else { $bindHint = 'target bindings' }
}
# Vendor / link / chassis for tailored apply
$vendor = 'Unknown'
$linkBps = 0
$mediaKind = 'Unknown'
$primaryDesc = ''
if ($bestEth) {
  $primaryDesc = [string]$bestEth.InterfaceDescription
  $mediaKind = 'Ethernet'
  try { $linkBps = [int64]$bestEth.ReceiveLinkSpeed } catch { $linkBps = 0 }
} elseif (@($wifi | Where-Object Status -eq 'Up').Count -gt 0) {
  $w0 = @($wifi | Where-Object Status -eq 'Up' | Select-Object -First 1)
  $primaryDesc = [string]$w0.InterfaceDescription
  $mediaKind = 'WiFi'
  try { $linkBps = [int64]$w0.ReceiveLinkSpeed } catch { $linkBps = 0 }
} elseif ($primary) {
  $primaryDesc = [string]$primary.InterfaceDescription
  $mediaKind = if (IsWifi $primary) { 'WiFi' } else { 'Ethernet' }
  try { $linkBps = [int64]$primary.ReceiveLinkSpeed } catch { $linkBps = 0 }
}
$d = $primaryDesc
if ($d -match '(?i)Killer') { $vendor = 'Killer' }
elseif ($d -match '(?i)Intel') { $vendor = 'Intel' }
elseif ($d -match '(?i)Realtek') { $vendor = 'Realtek' }
elseif ($d -match '(?i)MediaTek|MT7') { $vendor = 'MediaTek' }
elseif ($d -match '(?i)Qualcomm|QCA|Atheros') { $vendor = 'Qualcomm' }
elseif ($d -match '(?i)Broadcom|BCM') { $vendor = 'Broadcom' }
elseif ($d) { $vendor = 'Other' }
$laptop = 0
try {
  $bat = Get-CimInstance -ClassName Win32_Battery -EA SilentlyContinue
  if ($bat) { $laptop = 1 }
} catch {}
if ($laptop -eq 0) {
  try {
    $ch = Get-CimInstance -ClassName Win32_SystemEnclosure -EA SilentlyContinue
    $types = @($ch.ChassisTypes)
    # 8-11 portable/laptop/notebook/handheld; 14 subnotebook; 30-32 tablet/convertible
    foreach ($t in $types) {
      if ($t -in 8,9,10,11,14,30,31,32) { $laptop = 1; break }
    }
  } catch {}
}
$cpuN = [Environment]::ProcessorCount
$coreN = 0
try {
  $coreN = [int]((Get-CimInstance Win32_Processor -EA SilentlyContinue | Measure-Object -Property NumberOfCores -Sum).Sum)
} catch { $coreN = 0 }
if ($coreN -le 0) { $coreN = [Math]::Max(1, [int]($cpuN / 2)) }
# Sanitize free-text for ; delimiter parse
$hintS = ($hint -replace '[;=]', ' ').Trim()
$radiosS = ($radios -replace '[;=]', ' ').Trim()
$curBandS = ($curBand -replace '[;=]', ' ').Trim()
$bindHintS = ($bindHint -replace '[;=]', ',').Trim()
$descS = ($primaryDesc -replace '[;=]', ' ').Trim()
Write-Output "ETH=$($eth.Count -gt 0);ETHUP=$eUp;ETHUSE=$eInUse;WIFI=$($wifi.Count -gt 0);WIFIUP=$wUp;B6=$band6;B5=$band5;AX=$ax;BE=$be;HINT=$hintS;RADIOS=$radiosS;CURBAND=$curBandS;EMETRIC=$eMetric;FC=$fcR;IM=$imR;IDLE=$idleR;SS=$ssR;BINDOK=$bindOk;BINDHINT=$bindHintS;VENDOR=$vendor;LINK=$linkBps;LAPTOP=$laptop;CPU=$cpuN;CORES=$coreN;MEDIA=$mediaKind;DESC=$descS"
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
                        case "AX": wifi6 = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "BE": wifi7 = v.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                        case "HINT" when v is not ("-" or ""): radioHint = v; break;
                        case "RADIOS" when v is not ("-" or ""): driverRadios = v; break;
                        case "CURBAND" when v is not ("-" or ""): currentBand = v; break;
                        case "EMETRIC" when int.TryParse(v, out var em) && em >= 0: ethMetric = em; break;
                        case "FC" when int.TryParse(v, out var fc): fcR = fc; break;
                        case "IM" when int.TryParse(v, out var imv): imR = imv; break;
                        case "IDLE" when int.TryParse(v, out var id): idleR = id; break;
                        case "SS" when int.TryParse(v, out var ss): ssR = ss; break;
                        case "BINDOK":
                            bindOk = v is "1" or "True" or "true";
                            break;
                        case "BINDHINT" when v is not ("-" or ""):
                            bindHint = v.Replace(',', '·');
                            break;
                        case "VENDOR" when v is not ("-" or ""):
                            nicVendor = v;
                            break;
                        case "LINK" when long.TryParse(v, out var lb) && lb > 0:
                            linkBps = lb;
                            break;
                        case "LAPTOP":
                            isLaptop = v is "1" or "True" or "true";
                            break;
                        case "CPU" when int.TryParse(v, out var cn) && cn > 0:
                            logicals = cn;
                            break;
                        case "CORES" when int.TryParse(v, out var cores) && cores > 0:
                            physicalCores = cores;
                            break;
                        case "MEDIA" when v is not ("-" or ""):
                            primaryMedia = v;
                            break;
                        case "DESC" when v is not ("-" or "") && nicVendor is ("Unknown" or ""):
                            nicVendor = NetworkLogic.ClassifyNicVendor(v);
                            break;
                    }
                }

                static bool? Tri(int r) => r < 0 ? null : r != 0;
                var nicEval = NetworkLogic.EvaluateNic(
                    activePreset,
                    new NetworkLogic.NicFacts(
                        FlowControlOn: Tri(fcR),
                        InterruptModerationOn: Tri(imR),
                        IdleRestrictOn: Tri(idleR),
                        SelectiveSuspendOn: Tri(ssR)));
                nicOk = nicEval.Ok;
                nicHints = nicEval.Hints;
            }
            finally
            {
                try { File.Delete(probePs); } catch { }
            }
        }
        catch { }

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
                    supports5 = true;
                }
            }
        }

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

        if (wifiAvail && !supports5 && !supports6) supports5 = true;

        var path = NetworkLogic.DecidePath(
            ethAvail, ethUp, ethInUse, wifiAvail, wifiUp,
            supports6, supports5, wifi6 || supports6, wifi7);

        if (primaryMedia is "Unknown" or "")
            primaryMedia = ethInUse || ethUp ? "Ethernet" : wifiUp || wifiAvail ? "WiFi" : "Unknown";
        if (logicals <= 0) logicals = Environment.ProcessorCount;
        // Physical cores: prefer probe; else half of logicals (HT) as floor estimate
        if (physicalCores <= 0)
            physicalCores = Math.Max(1, logicals / 2);

        return new NetworkMediaProfile
        {
            EthernetAvailable = ethAvail,
            EthernetUp = ethUp,
            EthernetInUse = ethInUse,
            WifiAvailable = wifiAvail,
            WifiUp = wifiUp,
            ClientSupports6Ghz = supports6,
            ClientSupports5Ghz = supports5,
            ClientSupportsWifi6 = wifi6 || supports6,
            ClientSupportsWifi7 = wifi7,
            PreferredBandTarget = path.PreferredBandTarget,
            ConnectedRadioHint = radioHint,
            DriverRadios = driverRadios,
            CurrentBandSetting = currentBand,
            EthernetMetric = ethMetric,
            NicHints = nicHints,
            NicOk = nicOk,
            AdapterBindingsOk = bindOk,
            AdapterBindingsHint = bindHint,
            PolicyLine = path.PolicyLine,
            NicVendor = nicVendor,
            PrimaryMediaKind = primaryMedia,
            PrimaryLinkSpeedBps = linkBps,
            IsLikelyLaptop = isLaptop,
            LogicalProcessors = logicals,
            PhysicalCores = physicalCores
        };
    }

    /// <summary>True when live settings match the preset knobs (no false fail for intentional offs).</summary>
    public bool MatchesPreset(NetworkSnapshot snap, NetworkPreset preset)
    {
        if (!snap.ProbeOk) return false;
        if (snap.TaskOffloadDisabled == true) return false;
        if (!NetworkLogic.AutotuneMatches(preset, snap.AutoTuning)) return false;
        if (!NetworkLogic.LsoMatches(preset, snap.LsoEnabled)) return false;
        if (!NetworkLogic.RscMatches(preset, snap.RscEnabled)) return false;
        // NIC status: when probe computed it for this saved preset, require OK
        if (snap.ActivePreset == preset && !snap.Media.NicOk &&
            snap.Media.NicHints is not ("—" or "" or null))
            return false;
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

        // Proof layer: capture the "before" benchmark once (kept across re-applies so the
        // delta always compares against the pre-Exo baseline).
        if (LoadBenchmark().Before is null)
        {
            progress?.Report("Measuring baseline latency (ping/DNS)...");
            var baseline = await RunBenchmarkAsync(ct).ConfigureAwait(false);
            if (baseline is not null) PersistBenchmark(baseline, null);
        }

        progress?.Report("Preparing stack (Ethernet-first when available)...");
        var script = NetworkApplyScriptBuilder.Build(preset, options, media);
        var path = Path.Combine(Path.GetTempPath(), $"exo-net-{Guid.NewGuid():N}.ps1");
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
            // Give netsh / NIC props time to settle (esp. after adapter restart).
            await Task.Delay(options.RestartEthernet ? 3500 : 1600, ct).ConfigureAwait(false);
            SavePreset(preset, options);

            // Honest apply outcome: structured EXO_REPORT steps from the elevated run log
            // + rollback marker written by the script itself.
            IReadOnlyList<NetworkApplyReportStep> report = Array.Empty<NetworkApplyReportStep>();
            try
            {
                if (File.Exists(ApplyLogPath))
                    report = NetworkLogic.ParseApplyReport(await File.ReadAllTextAsync(ApplyLogPath, ct).ConfigureAwait(false));
            }
            catch { }
            var rollback = LoadRollbackStatus();
            PersistApplyOutcome(report, rollback);

            if (rollback is { RolledBack: true })
            {
                // Aggressive but deterministic: the script already re-enabled Wi‑Fi and
                // restored metrics. Report the partial failure honestly instead of "verified".
                return (false,
                    "Connectivity was lost after apply, so Exo automatically rolled back the path changes " +
                    "(Wi‑Fi re-enabled, interface metrics restored). Host-stack tweaks remain applied. " +
                    "Use Repair to restore the exact pre-Exo state from the snapshot.");
            }

            // Proof layer: post-apply benchmark for the before/after delta.
            progress?.Report("Measuring post-apply latency (ping/DNS)...");
            var after = await RunBenchmarkAsync(ct).ConfigureAwait(false);
            if (after is not null) PersistBenchmark(null, after);

            var snap = await ProbeAsync(ct).ConfigureAwait(false);
            var matched = MatchesPreset(snap, preset);
            // One re-probe if first verify is soft-incomplete (stale netsh / adapter props).
            if (!matched)
            {
                progress?.Report("Re-checking after settle...");
                await Task.Delay(1400, ct).ConfigureAwait(false);
                snap = await ProbeAsync(ct).ConfigureAwait(false);
                matched = MatchesPreset(snap, preset);
            }
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

    /// <summary>Expose apply script generation for audit/smokes (same path as elevated apply).</summary>
    public static string BuildApplyScript(
        NetworkPreset preset,
        NetworkApplyOptions options,
        NetworkMediaProfile media) =>
        NetworkApplyScriptBuilder.Build(preset, options, media);

    /// <summary>Expose repair script generation for audit/smokes (same path as elevated repair).</summary>
    public static string BuildRepairScript() => NetworkApplyScriptBuilder.BuildRepair();

    /// <summary>Expose benchmark script generation for audit/smokes (same path as RunBenchmarkAsync).</summary>
    public static string BuildBenchmarkScript() => NetworkApplyScriptBuilder.BuildBenchmark();

    // BuildFullApplyScript removed — see NetworkApplyScriptBuilder
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
            var i = ReadRegistryDword(k?.GetValue("NonBestEffortLimit"));
            if (i is int n) return n == 0 ? "0%" : $"{n}%";
        }
        catch { }
        return "—";
    }

    /// <summary>Registry DWORD can surface as int/long/uint/string depending on how it was written.</summary>
    private static int? ReadRegistryDword(object? value) => value switch
    {
        int i => i,
        long l => unchecked((int)l),
        uint u => unchecked((int)u),
        string s when int.TryParse(s.Trim(), out var n) => n,
        byte[] b when b.Length >= 4 => BitConverter.ToInt32(b, 0),
        _ => null
    };

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
