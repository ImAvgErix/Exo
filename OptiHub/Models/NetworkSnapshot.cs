namespace OptiHub.Models;

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
    public string PolicyLine { get; init; } = string.Empty;
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
