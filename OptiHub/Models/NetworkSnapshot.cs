namespace OptiHub.Models;

public enum NetworkPreset
{
    Balanced,
    LowestLatency,
    HighestThroughput
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
    public IReadOnlyList<NetworkFeatureRow> Features { get; init; } = Array.Empty<NetworkFeatureRow>();
}

public sealed class NetworkFeatureRow
{
    public required string Title { get; init; }
    public required string Status { get; init; }
    public bool IsOk { get; init; }
}
