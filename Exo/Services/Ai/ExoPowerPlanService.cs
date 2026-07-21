using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>
/// Deep per-CPU Exo Competitive power plan catalog (Intel/AMD/hybrid knobs).
/// Live writes: ExoAiHands → WindowsNativeApply.ApplyPowerPlanOnly / ExoPowerPlanNative.
/// </summary>
public sealed class ExoPowerPlanService
{
    public const string SchemeNameIntel = "Exo Competitive Intel";
    public const string SchemeNameAmd = "Exo Competitive AMD";
    public const string SchemeNameHybrid = "Exo Competitive Hybrid";

    public static readonly IReadOnlyList<PowerKnob> Catalog =
    [
        new("PROCESSOR_IDLEDISABLE", "Idle disable (desktop gaming)", "intel,amd,hybrid"),
        new("PERFBOOSTMODE", "Processor boost mode", "intel,amd,hybrid"),
        new("PERFEPP", "Energy performance preference", "intel,amd,hybrid"),
        new("CPMINCORES", "Min cores unparked", "intel,amd,hybrid"),
        new("CPMAXCORES", "Max cores unparked", "intel,amd,hybrid"),
        new("DISTRIBUTEUTIL", "Distribution utility", "intel,amd,hybrid"),
        new("SCHEDPOLICY", "Hetero scheduling policy", "intel,hybrid"),
        new("SHORTSCHEDPOLICY", "Short-running hetero policy", "intel,hybrid"),
        new("HETEROPOLICY", "Heterogeneous thread policy", "intel,hybrid"),
        new("CPCLASS0FLOOR", "Class0 (E-core) floor", "intel,hybrid"),
        new("CPCLASS1INITIAL", "Class1 (P-core) initial", "intel,hybrid"),
        new("CPCONCURRENCY", "Concurrency threshold", "intel,hybrid"),
        new("CPPCAUTONOMOUS", "CPPC autonomous mode", "amd"),
        new("CPPCAUTONOMOUSWINDOW", "CPPC autonomous window", "amd"),
        new("CPPPEPP", "CPPC energy performance preference", "amd"),
        new("PERFCHECK", "Perf check interval", "amd,hybrid"),
        new("SYSRESP", "System responsiveness (MMCSS peer)", "intel,amd,hybrid"),
        new("USBSELECTIVESUSPEND", "USB selective suspend off AC", "intel,amd,hybrid"),
        new("PCIEXPRESSASPM", "PCIe ASPM off AC", "intel,amd,hybrid"),
        new("WIFI_POWER", "Wi-Fi max performance when used", "intel,amd,hybrid"),
        new("MULTIMEDIA", "Multimedia power GUID align", "intel,amd,hybrid"),
        new("AHCI_LINK", "AHCI/NVMe link power", "intel,amd,hybrid")
    ];

    public sealed record PowerKnob(string Id, string Title, string Vendors);

    public string DetectVendorHint(ExoSystemState? state = null)
    {
        var cpu = state?.Hardware.GetValueOrDefault("cpuName") ?? "";
        if (cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            return "amd";
        if (Environment.ProcessorCount >= 12)
            return "hybrid";
        return OperatingSystem.IsWindows() ? "intel" : "linux";
    }

    public string SchemeNameFor(string vendor) => vendor.ToLowerInvariant() switch
    {
        "amd" => SchemeNameAmd,
        "hybrid" => SchemeNameHybrid,
        _ => SchemeNameIntel
    };

    public IReadOnlyList<PowerKnob> KnobsFor(string vendor) =>
        Catalog.Where(k => k.Vendors.Split(',').Any(v =>
            v.Trim().Equals(vendor, StringComparison.OrdinalIgnoreCase) ||
            (vendor.Equals("hybrid", StringComparison.OrdinalIgnoreCase) &&
             v.Trim().Equals("intel", StringComparison.OrdinalIgnoreCase)))).ToList();

    public Task<ExoToolResult> ApplyAsync(
        ExoSystemState? state = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var vendor = DetectVendorHint(state);
        var scheme = SchemeNameFor(vendor);
        var knobs = KnobsFor(vendor);
        progress?.Report($"power: {scheme} ({knobs.Count} knobs)");
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "power.exoCompetitive",
            Success = true,
            Status = OperatingSystem.IsWindows() ? "ok" : "skip",
            Message = OperatingSystem.IsWindows()
                ? $"Route via ExoAiHands → ExoPowerPlanNative ({scheme}, {knobs.Count} knobs)"
                : $"catalog ready for {scheme}; apply requires Windows ({knobs.Count} knobs)"
        });
    }
}
