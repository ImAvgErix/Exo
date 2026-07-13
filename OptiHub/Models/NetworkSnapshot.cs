namespace OptiHub.Models;

public enum NetworkPreset
{
    Balanced,
    LowestLatency,
    HighestThroughput
}

/// <summary>Smart media detection used to pick Ethernet-first / band policy.</summary>
public sealed class NetworkMediaProfile
{
    public bool EthernetAvailable { get; init; }
    public bool EthernetUp { get; init; }
    public bool WifiAvailable { get; init; }
    public bool WifiUp { get; init; }
    /// <summary>Client radio can do 6 GHz (Wi‑Fi 6E/7) if property/driver says so.</summary>
    public bool ClientSupports6Ghz { get; init; }
    public bool ClientSupports5Ghz { get; init; }
    /// <summary>Best preferred-band target: 6GHz | 5GHz | Auto</summary>
    public string PreferredBandTarget { get; init; } = "Auto";
    /// <summary>Connected BSS hint from netsh (radio type / channel) when on Wi‑Fi.</summary>
    public string ConnectedRadioHint { get; init; } = "—";
    /// <summary>Human policy line for UI.</summary>
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
