using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// One place that answers "what is this machine made of".
///
/// Before this existed, "is there a discrete GPU" was spelled three different ways in three
/// different files and they disagreed — the Steam client-routing copy had no Quadro, the
/// game-routing copy had no Intel Arc, and GpuTopology had neither — so on a Quadro laptop
/// Apply moved the Steam client to the iGPU and simultaneously treated the same machine as
/// single-GPU for games. Vendor detection is exactly the kind of rule that must have one
/// definition, so this is it, and GpuTopology now defers to it.
///
/// Everything here is read-only WMI. It never installs, changes or removes anything.
/// </summary>
public static partial class HardwareInventory
{
    public enum GpuVendor { Unknown, Nvidia, Amd, Intel }
    public enum CpuVendor { Unknown, Amd, Intel }

    public sealed record GpuInfo(
        string Name,
        GpuVendor Vendor,
        bool Discrete,
        bool Integrated,
        string? DriverVersion,
        string? PnpDeviceId);

    public sealed record CpuInfo(
        string Name,
        CpuVendor Vendor,
        int PhysicalCores,
        int LogicalCores);

    public sealed record Snapshot(
        IReadOnlyList<GpuInfo> Gpus,
        CpuInfo? Cpu)
    {
        /// <summary>Both a discrete and an integrated adapter — the only case where a per-app GPU
        /// preference changes which chip actually runs a process.</summary>
        public bool HybridGpu =>
            Gpus.Count >= 2 && Gpus.Any(g => g.Discrete) && Gpus.Any(g => g.Integrated);

        public GpuInfo? PrimaryGpu =>
            Gpus.FirstOrDefault(g => g.Discrete) ?? Gpus.FirstOrDefault();

        public bool HasNvidia => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);
        public bool HasAmdGpu => Gpus.Any(g => g.Vendor == GpuVendor.Amd);
        public bool HasIntelGpu => Gpus.Any(g => g.Vendor == GpuVendor.Intel);
    }

    // Adapters that are not real display hardware. A machine with Parsec or DisplayLink
    // installed would otherwise look like it had a second GPU.
    [GeneratedRegex(@"(?i)Microsoft Basic|Hyper-V|Remote|Virtual|Parsec|DisplayLink|Citrix|VNC|IddSampleDriver")]
    private static partial Regex NotRealAdapter();

    // Keep these two in step with Test-SteamHybridGpu in SteamDetectCore.ps1. They are the
    // union of every spelling that used to be scattered around the codebase.
    [GeneratedRegex(@"(?i)NVIDIA|GeForce|RTX|GTX|Quadro|Titan|Radeon\s+RX|Radeon\s+Pro|FirePro|Intel.*Arc|Arc\s*A")]
    private static partial Regex DiscreteName();

    [GeneratedRegex(@"(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega|Radeon\(TM\) Vega|AMD Radeon Graphics")]
    private static partial Regex IntegratedName();

    public static GpuVendor ClassifyGpuVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return GpuVendor.Unknown;
        if (Regex.IsMatch(name, @"(?i)NVIDIA|GeForce|RTX|GTX|Quadro|Titan")) return GpuVendor.Nvidia;
        if (Regex.IsMatch(name, @"(?i)\bAMD\b|Radeon|FirePro")) return GpuVendor.Amd;
        if (Regex.IsMatch(name, @"(?i)\bIntel\b")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    public static CpuVendor ClassifyCpuVendor(string? nameOrManufacturer)
    {
        if (string.IsNullOrWhiteSpace(nameOrManufacturer)) return CpuVendor.Unknown;
        if (Regex.IsMatch(nameOrManufacturer, @"(?i)AuthenticAMD|\bAMD\b|Ryzen|Threadripper|EPYC|Athlon")) return CpuVendor.Amd;
        if (Regex.IsMatch(nameOrManufacturer, @"(?i)GenuineIntel|\bIntel\b|Core\(TM\)|Xeon|Pentium|Celeron")) return CpuVendor.Intel;
        return CpuVendor.Unknown;
    }

    /// <summary>
    /// Pure classification, so the rules can be tested without a machine. Integrated wins over
    /// discrete when a name matches both patterns: "Intel Arc Graphics" is the Meteor Lake iGPU
    /// and must not be counted as a discrete Arc card, or a laptop with no dGPU would look hybrid.
    /// </summary>
    public static GpuInfo ClassifyGpu(string name, string? driverVersion = null, string? pnpId = null)
    {
        var integrated = IntegratedName().IsMatch(name);
        var discrete = !integrated && DiscreteName().IsMatch(name);
        return new GpuInfo(name, ClassifyGpuVendor(name), discrete, integrated, driverVersion, pnpId);
    }

    public static bool IsRealAdapter(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !NotRealAdapter().IsMatch(name);

    private const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string CpuPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor";

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex AdapterIndex();

    /// <summary>
    /// Live read, straight from the registry. Deliberately not WMI: this runs on the detect
    /// path, a ManagementObjectSearcher costs an extra dependency and hundreds of milliseconds,
    /// and the display class key holds everything needed. Returns whatever it can read rather
    /// than throwing at the caller.
    /// </summary>
    public static Snapshot Read()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (classKey is not null)
            {
                foreach (var sub in classKey.GetSubKeyNames())
                {
                    if (!AdapterIndex().IsMatch(sub)) continue;
                    using var adapter = classKey.OpenSubKey(sub);
                    if (adapter is null) continue;
                    var name = (adapter.GetValue("DriverDesc")
                                ?? adapter.GetValue("Device Description"))?.ToString()?.Trim();
                    if (!IsRealAdapter(name)) continue;
                    gpus.Add(ClassifyGpu(
                        name!,
                        adapter.GetValue("DriverVersion")?.ToString(),
                        adapter.GetValue("MatchingDeviceId")?.ToString()));
                }
            }
        }
        catch { }

        CpuInfo? cpu = null;
        try
        {
            using var cpuRoot = Registry.LocalMachine.OpenSubKey(CpuPath);
            var logical = cpuRoot?.GetSubKeyNames().Length ?? 0;
            using var cpu0 = cpuRoot?.OpenSubKey("0");
            if (cpu0 is not null)
            {
                var name = (cpu0.GetValue("ProcessorNameString")?.ToString() ?? string.Empty).Trim();
                var ident = cpu0.GetValue("VendorIdentifier")?.ToString();
                var vendor = ClassifyCpuVendor(ident);
                if (vendor == CpuVendor.Unknown) vendor = ClassifyCpuVendor(name);
                // The registry lists one key per LOGICAL processor. Physical core count is not
                // there, so it is reported as 0 rather than guessed - an invented core count is
                // exactly the kind of confident-but-unmeasured number this branch keeps removing.
                cpu = new CpuInfo(name, vendor, 0, logical);
            }
        }
        catch { }

        return new Snapshot(gpus, cpu);
    }
}
