namespace Exo.Models;

public enum NetworkPreset
{
    Balanced,
    LowestLatency,
    HighestThroughput
}

/// <summary>Local media/capability detection for Ethernet-first + band policy (see docs/INTERNET-GOLDEN-PATH.md).</summary>
public sealed class NetworkMediaProfile
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
    /// <summary>Live NIC peak status (flow control, selective suspend, etc.).</summary>
    public string NicPeakHints { get; init; } = "—";
    public bool NicPeakOk { get; init; } = true;
    /// <summary>
    /// Ethernet Properties → Networking checkboxes match Exo peak
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

public sealed class NetworkSnapshot
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
    public NetworkPreset ActivePreset { get; init; } = NetworkPreset.Balanced;
    public NetworkMediaProfile Media { get; init; } = new();
    public IReadOnlyList<NetworkFeatureRow> Features { get; init; } = Array.Empty<NetworkFeatureRow>();
}

public sealed class NetworkFeatureRow
{
    public required string Title { get; init; }
    public required string Status { get; init; }
    public bool IsOk { get; init; }
}

public sealed class NetworkApplyOptions
{
    /// <summary>When true, Restart-NetAdapter on Ethernet after props (user-confirmed).</summary>
    public bool RestartEthernet { get; init; }

    /// <summary>When Ethernet is up, disable Wi‑Fi adapters (default true).</summary>
    public bool PreferEthernetDisableWifi { get; init; } = true;
}

/// <summary>
/// One structured step emitted by the generated apply/repair scripts as
/// <c>EXO_REPORT:&lt;name&gt;|ok</c> / <c>|fail:&lt;reason&gt;</c> / <c>|skip:&lt;reason&gt;</c>.
/// </summary>
public sealed class NetworkApplyReportStep
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
public sealed class NetworkBenchmarkResult
{
    public bool Ok { get; init; }
    public double PingP50Ms { get; init; }
    public double PingP95Ms { get; init; }
    public double JitterMs { get; init; }
    /// <summary>Average DNS resolve time in ms (-1 when resolution failed).</summary>
    public double DnsMs { get; init; }
    public int Samples { get; init; }
    public string TimestampUtc { get; init; } = string.Empty;
}

/// <summary>
/// Honest post-apply outcome written by the elevated apply script into
/// %LocalAppData%\Exo\network-apply-state.json (rollback marker + Wi‑Fi record).
/// </summary>
public sealed class NetworkRollbackStatus
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
