using System.Text.Json.Serialization;

namespace Exo.Models;

/// <summary>
/// Exo-owned NVIDIA panel (replaces Control Panel / App for relevant settings).
/// Applied at the driver via NVAPI + DRS — not the Store Control Panel UI.
/// </summary>
public sealed class NvidiaPanelSettings
{
    public const string FileName = "nvidia-panel-settings.json";

    /// <summary>Primary monitor refresh: max | keep | 60</summary>
    public string PrimaryRefresh { get; set; } = "max";

    /// <summary>Secondary monitors: 60 | max | keep</summary>
    public string SecondaryRefresh { get; set; } = "60";

    public bool FullRgb { get; set; } = true;
    /// <summary>GPU + No scaling + Override (user peak default).</summary>
    public bool GpuNoScaling { get; set; } = true;
    public bool ScalingOverride { get; set; } = true;
    public bool VideoNvidiaColor { get; set; } = true;
    public bool VideoNvidiaImage { get; set; } = true;
    public bool DeveloperCounters { get; set; } = true;

    /// <summary>Force driver-level 3D packs (Profile Inspector). Always on for full Apply.</summary>
    public bool Force3dProfiles { get; set; } = true;

    /// <summary>Remove NVIDIA App + Control Panel clients; Exo is the only UI.</summary>
    public bool StripAppAndControlPanel { get; set; } = true;

    public static NvidiaPanelSettings CreateDefaults() => new();

    [JsonIgnore]
    public string Summary =>
        $"Primary={PrimaryRefresh}, Secondary={SecondaryRefresh}, FullRGB={FullRgb}, " +
        $"GPUScale={GpuNoScaling}, Override={ScalingOverride}, VideoNVIDIA={VideoNvidiaColor && VideoNvidiaImage}";
}
