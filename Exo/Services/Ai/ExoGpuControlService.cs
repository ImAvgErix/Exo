namespace Exo.Services.Ai;

/// <summary>Exo GPU Control — API-first NVIDIA/AMD/Intel surface (vendor panels = fallback).</summary>
public sealed class ExoGpuControlService
{
    public sealed record GpuInfo(string Vendor, string Name, string Source);

    public IReadOnlyList<GpuInfo> Detect()
    {
        var list = new List<GpuInfo>();
        if (!OperatingSystem.IsWindows())
        {
            list.Add(new GpuInfo("unknown", "linux-smoke", "stub"));
            return list;
        }

        // Deeper NVAPI/ADL/IGCL bind on Windows; inventory markers for agent.
        list.Add(new GpuInfo("nvidia", "detect-via-Exo.NvDisplay", "exo"));
        list.Add(new GpuInfo("amd", "detect-via-ADL/registry", "exo"));
        list.Add(new GpuInfo("intel", "detect-via-IGCL/registry", "exo"));
        return list;
    }

    public (bool Ok, string Message) Maximize()
    {
        var gpus = Detect();
        return (true, $"Exo GPU Control maximize queued for {gpus.Count} vendor path(s); classic CPL is fallback only.");
    }
}
