using System.Text.RegularExpressions;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Pure path decisions for Internet optimizer (no elevation, no I/O).
/// Used by detect, apply-script generation, and smoke tests — single source of truth.
/// </summary>
public static partial class NetworkLogic
{
    [GeneratedRegex(@"(?i)\bonly\b|\bexclusive\b")]
    private static partial Regex BandOnlyRegex();

    [GeneratedRegex(@"(?i)prefer|preferred|preferable|priority|favou?r")]
    private static partial Regex BandPreferRegex();

    [GeneratedRegex(@"(?i)2\.4|2,4|2400|2GHz|2\s*GHz")]
    private static partial Regex Band24Regex();

    [GeneratedRegex(@"(?i)no\s*pref|no\s*preference|auto|default|disabled|not\s*set|any\s*band|best\s*performance|\b802\.11\s*auto\b")]
    private static partial Regex BandNeutralRegex();

    [GeneratedRegex(@"(?i)6\s*GHz|6GHz|6,?0\s*GHz|Wi-?Fi\s*6E|802\.11be.*6|band\s*6")]
    private static partial Regex Band6Regex();

    [GeneratedRegex(@"(?i)5\s*GHz|5GHz|5\.2|5,0|5\.0|5800|band\s*5|802\.11a(?!x)|802\.11ac|802\.11n.*5")]
    private static partial Regex Band5Regex();

    [GeneratedRegex(@"(?i)6")]
    private static partial Regex AnySixRegex();

    [GeneratedRegex(@"(?i)2\.4|only")]
    private static partial Regex TwoFourOrOnlyRegex();

    [GeneratedRegex(@"(?i)5")]
    private static partial Regex AnyFiveRegex();

    [GeneratedRegex(@"(?i)2\.4|only|6")]
    private static partial Regex TwoFourOnlyOrSixRegex();

    [GeneratedRegex(@"(?i)Native 802\.11|802\.11|Wireless")]
    private static partial Regex WifiPhysicalMediaRegex();

    [GeneratedRegex(@"(?i)^802\.3$")]
    private static partial Regex EthernetPhysicalMediaRegex();

    [GeneratedRegex(@"(?i)Native 802|802\.11|Wireless|Wi-?Fi")]
    private static partial Regex WifiMediaRegex();

    [GeneratedRegex(@"(?i)Wi-?Fi|Wireless|802\.11|WLAN|MediaTek.*Wi|Intel.*Wi-?Fi|Realtek.*802\.11|Killer.*Wireless|Qualcomm.*Wi|Broadcom.*802|AX\d{3,4}|BE\d{3,4}|Wi-Fi\s*\d")]
    private static partial Regex WifiDescriptionRegex();

    [GeneratedRegex(@"(?i)^Wi-?Fi|Wireless|WLAN")]
    private static partial Regex WifiNameRegex();

    [GeneratedRegex(@"(?i)Bluetooth|Virtual|Hyper-V|vEthernet|TAP-|TUN-|WireGuard|OpenVPN|Wintun|Meta\s*Tunnel")]
    private static partial Regex NonWifiVirtualRegex();

    [GeneratedRegex(@"(?i)6\s*GHz|6GHz|Wi-?Fi\s*6E|Prefer\s*6|6\s*GHz\s*prefer|band\s*6")]
    private static partial Regex Supports6Regex();

    [GeneratedRegex(@"(?i)5\s*GHz|5GHz|Prefer\s*5|5\s*GHz\s*prefer|5\.2\s*GHz|band\s*5")]
    private static partial Regex Supports5Regex();

    [GeneratedRegex(@"(?i)802\.11be|Wi-?Fi\s*7")]
    private static partial Regex WifiBeRegex();

    [GeneratedRegex(@"(?i)802\.11ax|Wi-?Fi\s*6")]
    private static partial Regex WifiAxRegex();

    [GeneratedRegex(@"(?i)802\.11a|802\.11n|802\.11ac")]
    private static partial Regex Legacy5GhzRegex();

    public sealed record PresetKnobs(
        string AutotuneNetsh,
        string AutotunePs,
        string Rsc,
        string Lso,           // "0" | "1"
        string InterruptMod,  // "0" | "1"
        string FlowControl,   // "0" | "3"
        string IdleRestrict,  // "1" = prevent NIC idle (latency)
        bool NagleOff)
    { }

    public sealed record PathDecision(
        string PolicyLine,
        string PreferredBandTarget,
        bool DisableWifiWhenPreferEth,
        bool KeepWifiBecauseEthNoIp);

    /// <summary>Documented tradeoffs only — latency vs highest download.</summary>
    public static PresetKnobs KnobsFor(NetworkPreset preset)
    {
        var latency = preset == NetworkPreset.LowestLatency;
        // Balanced uses latency-leaning host knobs but without aggressive nagle (treat as latency stack lite)
        var bulk = preset == NetworkPreset.HighestThroughput;
        return new PresetKnobs(
            // Competitive stack (Nexus/Paragon/Evolve-class): low latency disables
            // RSC/LSO/IM/flow; throughput enables them. Nagle off on both gaming presets.
            AutotuneNetsh: "normal",
            AutotunePs: "Normal",
            Rsc: bulk ? "enabled" : "disabled",
            Lso: bulk ? "1" : "0",
            InterruptMod: bulk ? "1" : "0",
            FlowControl: bulk ? "3" : "0",
            IdleRestrict: bulk ? "0" : "1",
            // Nagle left adaptive — never force ACK/NoDelay pins (smoke + community safety).
            NagleOff: false);
    }

    /// <summary>
    /// Classify NIC vendor from InterfaceDescription (Intel I225/I226, Realtek, Killer, …).
    /// Used to pick vendor-specific advanced properties without inventing keys.
    /// </summary>
    public static string ClassifyNicVendor(string? interfaceDescription)
    {
        if (string.IsNullOrWhiteSpace(interfaceDescription)) return "Unknown";
        var d = interfaceDescription;
        if (d.Contains("Killer", StringComparison.OrdinalIgnoreCase)) return "Killer";
        if (d.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (d.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) return "Realtek";
        if (d.Contains("MediaTek", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("MT7", StringComparison.OrdinalIgnoreCase)) return "MediaTek";
        if (d.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("QCA", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("Atheros", StringComparison.OrdinalIgnoreCase)) return "Qualcomm";
        if (d.Contains("Broadcom", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("BCM", StringComparison.OrdinalIgnoreCase)) return "Broadcom";
        return "Other";
    }

    /// <summary>
    /// Buffer strategy: latency uses mid-high (not always absolute max — large rings can add jitter);
    /// throughput uses max. Returns "max" | "mid".
    /// </summary>
    public static string BufferStrategy(NetworkPreset preset) =>
        preset == NetworkPreset.HighestThroughput ? "max" : "mid";

    /// <summary>
    /// RSS queue budget from physical core count (not HT threads).
    /// Latency: up to physical cores; throughput: same ceiling (driver max still wins).
    /// </summary>
    public static int RssQueueBudget(NetworkPreset preset, int physicalCores)
    {
        var n = Math.Max(1, physicalCores);
        if (preset == NetworkPreset.HighestThroughput) return n;
        // Latency: prefer fewer queues on high-core counts (less DPC scatter)
        return Math.Max(2, Math.Min(n, Math.Max(2, n / 2 + n % 2)));
    }

    /// <summary>Exo leaves Windows' RFC-aware IPv4/IPv6 prefix precedence unchanged.</summary>
    public static bool PreferIpv4First(NetworkPreset preset, bool ethernetInUse) => false;

    /// <summary>Raw NIC advanced-property facts (null = not exposed by driver).</summary>
    public sealed record NicFacts(
        bool? FlowControlOn,
        bool? InterruptModerationOn,
        bool? IdleRestrictOn,
        bool? SelectiveSuspendOn);

    /// <summary>
    /// Preset-aware NIC status: latency wants flow/IM off + idle-restrict on;
    /// highest download wants flow/IM on + idle-restrict off. Missing props do not fail.
    /// </summary>
    public static (bool Ok, string Hints) EvaluateNic(NetworkPreset preset, NicFacts f)
    {
        var bulk = preset == NetworkPreset.HighestThroughput;
        var bits = new List<string>();
        var ok = true;

        if (f.FlowControlOn is bool fcOn)
        {
            // bulk: on is intentional; latency: on is a gap
            if (bulk)
            {
                bits.Add(fcOn ? "FlowCtrl on" : "FlowCtrl off");
                if (!fcOn) ok = false;
            }
            else
            {
                bits.Add(fcOn ? "FlowCtrl on" : "FlowCtrl off");
                if (fcOn) ok = false;
            }
        }

        if (f.InterruptModerationOn is bool imOn)
        {
            if (bulk)
            {
                bits.Add(imOn ? "IM on" : "IM off");
                if (!imOn) ok = false;
            }
            else
            {
                bits.Add(imOn ? "IM on" : "IM off");
                if (imOn) ok = false;
            }
        }

        if (f.IdleRestrictOn is bool idleOn)
        {
            // IdleRestriction Enabled = prevent low-power idle (latency good)
            if (bulk)
            {
                bits.Add(idleOn ? "IdleRestrict on" : "IdleRestrict off");
                if (idleOn) ok = false;
            }
            else
            {
                bits.Add(idleOn ? "IdleRestrict on" : "IdleRestrict off");
                if (!idleOn) ok = false;
            }
        }

        // Selective suspend: always prefer off when exposed (both presets)
        if (f.SelectiveSuspendOn is bool ssOn)
        {
            bits.Add(ssOn ? "SelSuspend on" : "SelSuspend off");
            if (ssOn) ok = false;
        }

        var hints = bits.Count > 0 ? string.Join(", ", bits) : "—";
        return (ok, hints);
    }

    /// <summary>
    /// Autotune level must match preset knobs (normal vs experimental).
    /// Unknown / unread ("—", empty) skips — same as null LSO/RSC — so a probe gap
    /// never marks the row "not checked" after a successful apply.
    /// </summary>
    public static bool AutotuneMatches(NetworkPreset preset, string? autoTuning)
    {
        if (string.IsNullOrWhiteSpace(autoTuning) || autoTuning is "—") return true;
        var want = KnobsFor(preset).AutotuneNetsh;
        var got = autoTuning.Trim();
        // netsh may report "normal", "Normal", or rare multi-token; compare first token
        var token = got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Congestion provider must read back CUBIC (the only provider Apply ever sets,
    /// via `netsh ... congestionprovider=cubic` / `Set-NetTCPSetting -CongestionProvider
    /// CUBIC`, on every preset). Unknown/unread ("-", empty) skips, same rationale as
    /// <see cref="AutotuneMatches"/> — a probe gap never marks the row "not checked".
    /// </summary>
    public static bool CongestionMatches(string? congestionProvider)
    {
        if (string.IsNullOrWhiteSpace(congestionProvider) || congestionProvider is "-" or "—") return true;
        return congestionProvider.Trim().Equals("cubic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RSC enabled state matches preset (null unknown = skip).</summary>
    public static bool RscMatches(NetworkPreset preset, bool? rscEnabled)
    {
        if (rscEnabled is null) return true;
        var wantOn = KnobsFor(preset).Rsc.Equals("enabled", StringComparison.OrdinalIgnoreCase);
        return rscEnabled == wantOn;
    }

    /// <summary>LSO enabled state matches preset (null unknown = skip).</summary>
    public static bool LsoMatches(NetworkPreset preset, bool? lsoEnabled)
    {
        if (lsoEnabled is null) return true;
        var wantOn = KnobsFor(preset).Lso == "1";
        return lsoEnabled == wantOn;
    }

    /// <summary>
    /// Score a single Preferred Band display value. Higher wins.
    /// Prefer-* beats Only-*; 2.4 never chosen when higher exists.
    /// </summary>
    public static int ScoreBandDisplayValue(string? value, bool want6)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var v = value.Trim();
        var isOnly = BandOnlyRegex().IsMatch(v);
        var isPref = BandPreferRegex().IsMatch(v);

        if (Band24Regex().IsMatch(v))
            return isOnly ? -200 : isPref ? -100 : -50;

        if (BandNeutralRegex().IsMatch(v))
            return 1;

        if (Band6Regex().IsMatch(v))
        {
            if (want6) return isOnly ? 45 : isPref ? 100 : 90;
            return isOnly ? 5 : 25;
        }

        if (Band5Regex().IsMatch(v))
            return isOnly ? 35 : isPref ? 80 : 70;

        return 0;
    }

    /// <summary>Pick best Prefer band display value from vendor ValidDisplayValues.</summary>
    public static string? SelectBandDisplayValue(IEnumerable<string?> values, bool want6)
    {
        var list = values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return null;

        string? best = null;
        var bestScore = int.MinValue;
        foreach (var v in list)
        {
            var s = ScoreBandDisplayValue(v, want6);
            if (s > bestScore || (s == bestScore && best is not null && string.CompareOrdinal(v, best) < 0))
            {
                bestScore = s;
                best = v;
            }
        }

        if (best is not null && bestScore > 1)
            return best;

        // Last resort: any 6/5 string that is not only/2.4
        if (want6)
        {
            var fb6 = list.FirstOrDefault(x =>
                AnySixRegex().IsMatch(x) && !TwoFourOrOnlyRegex().IsMatch(x));
            if (fb6 is not null) return fb6;
        }

        return list.FirstOrDefault(x =>
            AnyFiveRegex().IsMatch(x) && !TwoFourOnlyOrSixRegex().IsMatch(x));
    }

    /// <summary>True when adapter facts indicate Wi‑Fi (not Ethernet 802.3).</summary>
    public static bool IsWifiAdapter(
        string? physicalMediaType,
        string? mediaType,
        string? interfaceDescription,
        string? name)
    {
        var pm = physicalMediaType ?? "";
        var m = mediaType ?? "";
        var d = interfaceDescription ?? "";
        var n = name ?? "";

        if (WifiPhysicalMediaRegex().IsMatch(pm)) return true;
        if (EthernetPhysicalMediaRegex().IsMatch(pm)) return false;
        if (WifiMediaRegex().IsMatch(m)) return true;
        // USB/PCIe Wi‑Fi vendors + common names (not Bluetooth PAN as primary signal)
        if (WifiDescriptionRegex().IsMatch(d))
            return true;
        if (WifiNameRegex().IsMatch(n)) return true;
        // Explicit non-wifi virtual/tunnel
        if (NonWifiVirtualRegex().IsMatch(d))
            return false;
        return false;
    }

    /// <summary>
    /// Path policy: usable Ethernet (Up + real IPv4) → prefer eth 100% / disable Wi‑Fi.
    /// Link without IP → keep Wi‑Fi. Wi‑Fi only → prefer 6 then 5.
    /// </summary>
    public static PathDecision DecidePath(
        bool ethAvailable,
        bool ethUp,
        bool ethInUse,
        bool wifiAvailable,
        bool wifiUp,
        bool supports6Ghz,
        bool supports5Ghz,
        bool wifi6,
        bool wifi7)
    {
        var bandTarget = supports6Ghz ? "6GHz" : supports5Ghz ? "5GHz" : "Auto";
        string policy;
        var disableWifi = false;
        var keepWifiNoIp = false;

        if (ethInUse)
        {
            // Never disable Wi-Fi adapters — metrics-only prefer Ethernet.
            disableWifi = false;
            policy = wifiAvailable
                ? "Ethernet ready → prefer Ethernet (Wi-Fi stays enabled, higher metric)"
                : "Ethernet ready → Ethernet only";
        }
        else if (ethUp && !ethInUse)
        {
            keepWifiNoIp = true;
            policy = "Ethernet linked (no IP yet) → keep Wi‑Fi until Ethernet has address";
        }
        else if (wifiUp)
        {
            policy = $"Wi‑Fi only → prefer {bandTarget}" +
                     (wifi7 ? " (Wi‑Fi 7 client)" : wifi6 ? " (Wi‑Fi 6/6E client)" : "");
        }
        else if (ethAvailable)
            policy = "Ethernet present (down) → prefer when linked + IP";
        else if (wifiAvailable)
            policy = $"Wi‑Fi only → prefer {bandTarget}";
        else
            policy = "No physical adapter detected";

        return new PathDecision(policy, bandTarget, disableWifi, keepWifiNoIp);
    }

    /// <summary>
    /// Wi-Fi adapters are never disabled — Ethernet preference is metrics-only.
    /// Kept for API compatibility; always returns false.
    /// </summary>
    public static bool ShouldDisableWifi(bool preferEthernetOption, bool ethInUse, bool wifiAvailable) =>
        false;

    /// <summary>True when IPv4 is real (not empty, not APIPA).</summary>
    public static bool IsUsableIpv4(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip is "—" or "-") return false;
        if (ip.StartsWith("169.254.", StringComparison.Ordinal)) return false;
        return System.Net.IPAddress.TryParse(ip, out var a) &&
               a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>Infer client 5/6 GHz support from free-text driver/property blobs.</summary>
    public static void InferBandSupport(string blob, ref bool band5, ref bool band6, ref bool ax, ref bool be)
    {
        if (string.IsNullOrEmpty(blob)) return;
        if (Supports6Regex().IsMatch(blob))
            band6 = true;
        if (Supports5Regex().IsMatch(blob))
            band5 = true;
        if (WifiBeRegex().IsMatch(blob))
        {
            be = true;
            band6 = true;
        }
        if (WifiAxRegex().IsMatch(blob))
            ax = true;
        if (Legacy5GhzRegex().IsMatch(blob))
            band5 = true;
    }

    /// <summary>Folklore / brick-risk assignments that must never appear as applied values.</summary>
    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Set-Dword $tcp 'MaxUserPort'",
        "Set-Dword $mm 'SystemResponsiveness' 0",
        "Set-Dword $mm 'NetworkThrottlingIndex' 4294967295",
        "Set-Dword $mm 'NetworkThrottlingIndex' -1",
        "Set-Dword $tcp 'LargeSystemCache' 1",
        "Set-Dword $tcp 'TcpWindowSize'",
        "Set-Dword $tcp 'GlobalMaxTcpWindowSize'",
        "Set-Dword $tcp 'EnableTCPChimney' 1",
        // Retired crude IPv4-first hack (replaced by documented prefix-policy precedence)
        "$want6 = $base + 20",
        // Retired DNS-priority folklore (v3.0.11): measured DNS 100ms -> 1s+.
        // 6/7 are substring-safe (cannot match the 2000/2001 defaults); 4/5 are not
        // (they prefix 499/500), so they stay covered by the required markers only.
        "Set-Dword $sp 'DnsPriority' 6",
        "Set-Dword $sp 'NetbtPriority' 7",
        "set prefixpolicy ::ffff:0:0/96",
        "Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol",
        "Set-Net6to4Configuration",
        "Set-NetIsatapConfiguration",
        "Set-NetTeredoConfiguration",
    };

    /// <summary>Required host-stack markers in every apply script (network-only).</summary>
    public static readonly string[] RequiredHostMarkers =
    {
        "DisableTaskOffload' 0",
        "congestionprovider=cubic",
        "wantBand6Live",
        "Select-BandDisplayValue",
        "BufferStrategy",
        "RssQueueBudget",
        // Safety layer: pristine pre-apply snapshot + connectivity probe + rollback + report.
        // Wi-Fi adapters must never be disabled (metrics-only prefer-ethernet).
        "never disable wifi adapters",
        "network-snapshot.json",
        "Save-ExoNetworkSnapshot",
        "Test-ExoConnectivity",
        "EXO_REPORT:",
        "network-apply-state.json",
        // Global TCP algorithm choices remain on Windows adaptive defaults.
        "tcp-algorithms' 'skip'",
        // Background download quiet
        "DoSvc",
        "EnableBITSMaxBandwidth",
        // RSS off core 0 + keyword-first adapter writes
        // (*SpeedDuplex intentionally omitted — never force link speed)
        "BaseProcessorNumber 2",
        "'*FlowControl'",
        "'*JumboPacket'",
        "'*PriorityVLANTag'",
        // Per-interface ACK registry folklore is actively retired.
        "legacy-ack-pins' 'ok'",
    };

    /// <summary>Audit a generated apply script for host markers and folklore absence.</summary>
    public static (bool Ok, List<string> Issues) AuditApplyScript(string script, NetworkPreset preset)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(script))
        {
            issues.Add("empty script");
            return (false, issues);
        }

        foreach (var m in RequiredHostMarkers)
        {
            if (script.IndexOf(m, StringComparison.OrdinalIgnoreCase) < 0)
                issues.Add("missing required: " + m);
        }

        foreach (var f in ForbiddenApplyPatterns)
        {
            if (script.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                issues.Add("forbidden present: " + f);
        }

        var knobs = KnobsFor(preset);
        if (script.IndexOf("autotuninglevel=" + knobs.AutotuneNetsh, StringComparison.OrdinalIgnoreCase) < 0)
            issues.Add("autotune mismatch for " + preset + " expect " + knobs.AutotuneNetsh);
        if (script.IndexOf("rsc=" + knobs.Rsc, StringComparison.OrdinalIgnoreCase) < 0)
            issues.Add("rsc mismatch for " + preset + " expect " + knobs.Rsc);
        // LSO registry value appears as Set-Adv ... '*LsoV2IPv4' 0|1
        if (script.IndexOf("*LsoV2IPv4' " + knobs.Lso, StringComparison.Ordinal) < 0 &&
            script.IndexOf("*LsoV2IPv4' " + knobs.Lso, StringComparison.OrdinalIgnoreCase) < 0)
        {
            // builder emits: Set-Adv $n '*LsoV2IPv4' 0
            if (script.IndexOf("'*LsoV2IPv4' " + knobs.Lso, StringComparison.Ordinal) < 0)
                issues.Add("LSO mismatch for " + preset + " expect " + knobs.Lso);
        }

        if (script.IndexOf("'*FlowControl' " + knobs.FlowControl, StringComparison.Ordinal) < 0)
            issues.Add("FlowControl mismatch for " + preset + " expect " + knobs.FlowControl);

        foreach (var forbidden in new[]
                 {
                     "timestamps=disabled", "pacingprofile=off", "hystart=disabled",
                     "ecncapability=enabled", "ecncapability=disabled", "uro=disabled",
                     "dynamicport tcp start=1025", "-MaxSynRetransmissions 2",
                     "-NonSackRttResiliency Disabled", "-InitialRtoMs 1000"
                 })
        {
            if (script.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0)
                issues.Add("global TCP policy must stay adaptive: " + forbidden);
        }

        return (issues.Count == 0, issues);
    }

    [GeneratedRegex(@"EXO_REPORT:([A-Za-z0-9_\-\. ]+)\|(ok|fail|skip)(?::([^\r\n]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex ApplyReportRegex();

    /// <summary>
    /// Parse EXO_REPORT structured lines from the elevated apply/repair run log
    /// (%TEMP%\exo-net-last.log). Order preserved; duplicates kept (honest trace).
    /// </summary>
    public static IReadOnlyList<NetworkApplyReportStep> ParseApplyReport(string? logText)
    {
        var list = new List<NetworkApplyReportStep>();
        if (string.IsNullOrEmpty(logText)) return list;
        foreach (System.Text.RegularExpressions.Match m in ApplyReportRegex().Matches(logText))
        {
            list.Add(new NetworkApplyReportStep
            {
                Name = m.Groups[1].Value.Trim(),
                Status = m.Groups[2].Value.ToLowerInvariant(),
                Reason = m.Groups[3].Success ? m.Groups[3].Value.Trim() : string.Empty
            });
        }
        return list;
    }

    /// <summary>
    /// Parse the single EXO_BENCH JSON line printed by the BuildBenchmark script.
    /// Returns null when no valid benchmark line is present.
    /// </summary>
    public static NetworkBenchmarkResult? TryParseBenchmark(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        const string marker = "EXO_BENCH:";
        var idx = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var line = output[(idx + marker.Length)..];
        var nl = line.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) line = line[..nl];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            double D(string n) =>
                root.TryGetProperty(n, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? e.GetDouble() : 0;
            string S(string n) =>
                root.TryGetProperty(n, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                    ? e.GetString() ?? string.Empty : string.Empty;
            return new NetworkBenchmarkResult
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True,
                PingP50Ms = D("pingP50Ms"),
                PingP95Ms = D("pingP95Ms"),
                JitterMs = D("jitterMs"),
                DnsMs = D("dnsMs"),
                Samples = (int)D("samples"),
                IsQualityTest = root.TryGetProperty("isQualityTest", out var qt) && qt.ValueKind == System.Text.Json.JsonValueKind.True,
                DownloadMbps = D("downloadMbps"),
                UploadMbps = D("uploadMbps"),
                DownloadLoadedMs = D("downloadLoadedMs"),
                UploadLoadedMs = D("uploadLoadedMs"),
                DownloadLoadedJitterMs = D("downloadLoadedJitterMs"),
                UploadLoadedJitterMs = D("uploadLoadedJitterMs"),
                PacketLossPercent = D("packetLossPercent"),
                DataUsedMb = D("dataUsedMb"),
                Endpoint = S("endpoint"),
                ParallelStreams = (int)D("parallelStreams"),
                TransferSeconds = D("transferSeconds"),
                LinkSpeedMbps = D("linkSpeedMbps"),
                DownloadEndpointLimited = root.TryGetProperty("downloadEndpointLimited", out var dl) && dl.ValueKind == System.Text.Json.JsonValueKind.True,
                UploadEndpointLimited = root.TryGetProperty("uploadEndpointLimited", out var ul) && ul.ValueKind == System.Text.Json.JsonValueKind.True,
                DnsProvider = S("dnsProvider"),
                DnsPrimary = S("dnsPrimary"),
                DnsSecondary = S("dnsSecondary"),
                DnsPrimaryV6 = S("dnsPrimaryV6"),
                DnsSecondaryV6 = S("dnsSecondaryV6"),
                DnsOverHttpsTemplate = S("dnsOverHttpsTemplate"),
                DnsMedianMs = D("dnsMedianMs"),
                RecommendedPreset = S("recommendedPreset"),
                RecommendationReason = S("recommendationReason"),
                TimestampUtc = root.TryGetProperty("timestampUtc", out var ts) && ts.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ts.GetString() ?? string.Empty : string.Empty
            };
        }
        catch { return null; }
    }

    public static NetworkPreset RecommendPreset(NetworkBenchmarkResult result, NetworkMediaProfile media)
    {
        if (!result.Ok || !result.IsQualityTest)
            return NetworkPreset.LowestLatency;

        var downPenalty = Math.Max(0, result.DownloadLoadedMs - result.PingP50Ms);
        var upPenalty = Math.Max(0, result.UploadLoadedMs - result.PingP50Ms);
        var unstable = result.PacketLossPercent >= 0.5 || result.JitterMs >= 8 ||
                       result.DownloadLoadedJitterMs >= 15 || result.UploadLoadedJitterMs >= 15 ||
                       downPenalty >= 25 || upPenalty >= 35;
        // Host-side offload disabling cannot fix queueing in a router/ONT. On a
        // 1+ GbE link it can only cut throughput, so keep RSS/RSC/LSO and report
        // loaded latency honestly. Wi-Fi and slower paths may use latency-safe
        // host knobs when the measured path is unstable.
        var fastEthernet = media.EthernetInUse && media.PrimaryLinkSpeedBps >= 1_000_000_000;
        if (!fastEthernet && (unstable || media.WifiUp && !media.EthernetInUse))
            return NetworkPreset.LowestLatency;

        // A short-lived public endpoint must never downgrade a verified multi-gig
        // Ethernet link. Endpoint saturation is recorded separately in the UI.
        return fastEthernet ||
               result.DownloadMbps >= 300 && media.PrimaryLinkSpeedBps >= 1_000_000_000
            ? NetworkPreset.HighestThroughput
            : NetworkPreset.LowestLatency;
    }

    /// <summary>
    /// Baseline packet loss from the idle series only. Loaded ICMP misses are not
    /// connection loss: the quality test deliberately saturates the path and many
    /// routers/targets deprioritize ping replies under that load.
    /// </summary>
    public static double CalculateIdlePacketLossPercent(int attempts, int successful)
    {
        if (attempts <= 0) return 100d;
        var boundedSuccessful = Math.Clamp(successful, 0, attempts);
        return (attempts - boundedSuccessful) * 100d / attempts;
    }
}
