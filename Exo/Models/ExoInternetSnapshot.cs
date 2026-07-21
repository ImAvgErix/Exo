namespace Exo.Models;

public enum ExoInternetPreset
{
    Balanced,
    LowestLatency,
    HighestThroughput
}

/// <summary>Local media/capability detection for Ethernet-first + band policy (see docs/INTERNET-GOLDEN-PATH.md).</summary>
public sealed class ExoInternetMediaProfile
{
    public bool EthernetAvailable { get; init; }
    public bool EthernetUp { get; init; }
    /// <summary>Usable Ethernet: Up + real IPv4 (prefer 100% for gaming when true).</summary>
    public bool EthernetInUse { get; init; }
    public bool WifiAvailable { get; init; }
    public bool WifiUp { get; init; }
    public bool ClientSupports6Ghz { get; init; }
    public bool ClientSupports5Ghz { get; init; }
    public bool ClientSupportsWifi7 { get; init; }
    public bool ClientSupportsWifi6 { get; init; }
    /// <summary>6GHz | 5GHz | Auto</summary>
    public string PreferredBandTarget { get; init; } = "Auto";
    /// <summary>e.g. 802.11be, 802.11ax, Band 6 GHz, ch 36</summary>
    public string ConnectedRadioHint { get; init; } = "—";
    /// <summary>Driver radio types summary from netsh wlan show drivers.</summary>
    public string DriverRadios { get; init; } = "—";
    /// <summary>Current Preferred Band DisplayValue when exposed by driver.</summary>
    public string CurrentBandSetting { get; init; } = "—";
    /// <summary>IPv4 InterfaceMetric of primary usable Ethernet (lower = preferred).</summary>
    public int? EthernetMetric { get; init; }
    /// <summary>Live NIC status (flow control, selective suspend, etc.).</summary>
    public string NicHints { get; init; } = "—";
    public bool NicOk { get; init; } = true;
    /// <summary>
    /// Ethernet Properties → Networking checkboxes match Exo targets
    /// (QoS+IPv4+IPv6 on; Client/File share/LLDP/LLTD off).
    /// </summary>
    public bool AdapterBindingsOk { get; init; } = true;
    public string AdapterBindingsHint { get; init; } = "—";
    public string PolicyLine { get; init; } = string.Empty;

    /// <summary>Intel | Realtek | Killer | MediaTek | Qualcomm | Broadcom | Other | Unknown</summary>
    public string NicVendor { get; init; } = "Unknown";
    /// <summary>Primary active media: Ethernet | WiFi | Unknown</summary>
    public string PrimaryMediaKind { get; init; } = "Unknown";
    /// <summary>Primary link speed in bits/sec (0 = unknown).</summary>
    public long PrimaryLinkSpeedBps { get; init; }
    /// <summary>Chassis looks like laptop/notebook (battery present or chassis type).</summary>
    public bool IsLikelyLaptop { get; init; }
    /// <summary>Logical processors (HT threads) — diagnostics only.</summary>
    public int LogicalProcessors { get; init; }
    /// <summary>Physical cores (for RSS queue budget — not HT).</summary>
    public int PhysicalCores { get; init; }
}

public sealed class ExoInternetSnapshot
{
    public string AdapterName { get; init; } = "—";
    public string AdapterDescription { get; init; } = "—";
    public string LinkSpeed { get; init; } = "—";
    public string ConnectionType { get; init; } = "—"; // Ethernet / Wi‑Fi / Unknown
    public string Ipv4Address { get; init; } = "—";
    public string Gateway { get; init; } = "—";
    public string DnsServers { get; init; } = "—";
    public string PublicIp { get; init; } = "—";
    public string Provider { get; init; } = "—";
    public string Area { get; init; } = "—";
    public string Mtu { get; init; } = "—";
    public bool? TaskOffloadDisabled { get; init; }
    public bool? LsoEnabled { get; init; }
    public bool? RscEnabled { get; init; }
    public string AutoTuning { get; init; } = "—";
    public string CongestionProvider { get; init; } = "—";
    public int? GatewayPingMs { get; init; }
    public int? InternetPingMs { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool ProbeOk { get; init; }
    public ExoInternetPreset ActivePreset { get; init; } = ExoInternetPreset.Balanced;
    public ExoInternetMediaProfile Media { get; init; } = new();
    public IReadOnlyList<ExoInternetFeatureRow> Features { get; init; } = Array.Empty<ExoInternetFeatureRow>();
}

public sealed class ExoInternetFeatureRow
{
    public required string Title { get; init; }
    public required string Status { get; init; }
    public bool IsOk { get; init; }
}

public sealed class ExoInternetApplyOptions
{
    /// <summary>When true, Restart-NetAdapter on Ethernet after props (user-confirmed).</summary>
    public bool RestartEthernet { get; init; }

    /// <summary>
    /// Prefer Ethernet via metrics only. Never disables Wi-Fi adapters
    /// (default false — disabling Wi-Fi stranded users when Ethernet later dropped).
    /// </summary>
    public bool PreferEthernetDisableWifi { get; init; } = false;

    /// <summary>Fastest verified resolver selected by Analyze. Cloudflare is the safe fallback.</summary>
    public string DnsProvider { get; init; } = "Cloudflare";
    public string DnsPrimary { get; init; } = "1.1.1.1";
    public string DnsSecondary { get; init; } = "1.0.0.1";
    public string DnsPrimaryV6 { get; init; } = "2606:4700:4700::1111";
    public string DnsSecondaryV6 { get; init; } = "2606:4700:4700::1001";
    public string DnsOverHttpsTemplate { get; init; } = "https://cloudflare-dns.com/dns-query";

    /// <summary>
    /// Experimental apply: force re-stamp / re-import style paths on top of the full
    /// stable stack. Host MMCSS/Games/Psched knobs ship in Stable (safe values only).
    /// </summary>
    public bool Experimental { get; init; }
}

/// <summary>
/// One structured step emitted by the generated apply/repair scripts as
/// <c>EXO_REPORT:&lt;name&gt;|ok</c> / <c>|fail:&lt;reason&gt;</c> / <c>|skip:&lt;reason&gt;</c>.
/// </summary>
public sealed class ExoInternetApplyReportStep
{
    public required string Name { get; init; }
    /// <summary>ok | fail | skip</summary>
    public required string Status { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Quick ping/DNS benchmark (BuildBenchmark output, one EXO_BENCH JSON line).
/// Persisted as before/after pairs in the optimizer state so the UI can show deltas.
/// </summary>
public sealed class ExoInternetBenchmarkResult
{
    public bool Ok { get; init; }
    public double PingP50Ms { get; init; }
    public double PingP95Ms { get; init; }
    public double JitterMs { get; init; }
    /// <summary>Average DNS resolve time in ms (-1 when resolution failed).</summary>
    public double DnsMs { get; init; }
    public int Samples { get; init; }
    /// <summary>True for the explicit ramped connection-quality test.</summary>
    public bool IsQualityTest { get; init; }
    public double DownloadMbps { get; init; }
    public double UploadMbps { get; init; }
    public double DownloadLoadedMs { get; init; }
    public double UploadLoadedMs { get; init; }
    public double DownloadLoadedJitterMs { get; init; }
    public double UploadLoadedJitterMs { get; init; }
    /// <summary>Idle-path ICMP loss only; loaded ICMP misses are excluded.</summary>
    public double PacketLossPercent { get; init; }
    public double DataUsedMb { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public int ParallelStreams { get; init; }
    public double TransferSeconds { get; init; }
    public double LinkSpeedMbps { get; init; }
    public bool DownloadEndpointLimited { get; init; }
    public bool UploadEndpointLimited { get; init; }
    public string DnsProvider { get; init; } = string.Empty;
    public string DnsPrimary { get; init; } = string.Empty;
    public string DnsSecondary { get; init; } = string.Empty;
    public string DnsPrimaryV6 { get; init; } = string.Empty;
    public string DnsSecondaryV6 { get; init; } = string.Empty;
    public string DnsOverHttpsTemplate { get; init; } = string.Empty;
    public double DnsMedianMs { get; init; }
    /// <summary>lowest-latency | highest-throughput</summary>
    public string RecommendedPreset { get; init; } = string.Empty;
    public string RecommendationReason { get; init; } = string.Empty;
    public string TimestampUtc { get; init; } = string.Empty;
}

/// <summary>
/// Honest post-apply outcome written by the elevated apply script into
/// %LocalAppData%\Exo\network-apply-state.json (rollback marker + Wi‑Fi record).
/// </summary>
public sealed class ExoInternetRollbackStatus
{
    /// <summary>True when the apply script auto-rolled back path changes (Wi‑Fi + metrics).</summary>
    public bool RolledBack { get; init; }
    public string Reason { get; init; } = string.Empty;
    /// <summary>Result of the final TCP-443 connectivity probe at end of apply.</summary>
    public bool ConnectivityAfterApply { get; init; } = true;
    /// <summary>Wi‑Fi adapter names the apply script disabled (after the verified Ethernet probe).</summary>
    public IReadOnlyList<string> WifiDisabled { get; init; } = Array.Empty<string>();
    public string AppliedUtc { get; init; } = string.Empty;
}
