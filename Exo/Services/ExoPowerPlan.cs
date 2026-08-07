using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// A named "Exo" Windows power plan, built from what the CPU in front of us actually is.
///
/// The system module edits whichever plan the user is already on. This is the other half: a
/// plan of our own, so the tuning survives the user switching back and forth and is visibly
/// labelled rather than silently mutating "Balanced".
///
/// <b>Most settings are the same on every CPU, and this file says so rather than inventing
/// vendor differences to look thorough.</b> There are exactly two places where the silicon
/// genuinely changes the answer:
///
/// <list type="number">
/// <item><b>Intel hybrid (12th gen and later).</b> <c>SCHEDPOLICY</c> and
/// <c>SHORTSCHEDPOLICY</c> decide whether a thread lands on a P-core or an E-core. They only
/// exist on heterogeneous systems, and getting them wrong puts game threads on efficiency
/// cores. Detected from <c>EfficiencyClass</c> in the real processor topology, not by parsing
/// a marketing name — an i5-14400 and an i9-14900K are both hybrid and neither string tells
/// you the core split.</item>
/// <item><b>AMD multi-CCD.</b> Cross-CCD latency is real and powercfg cannot fix it. Detected
/// by counting L3 domains, and reported as advice pointing at the chipset driver rather than
/// pretended away with a power setting that does not address it.</item>
/// </list>
///
/// Every setting is probed for existence before it is written. A machine that does not expose
/// a setting is not a machine to write it to anyway, and probing is what makes this work on
/// hardware nobody here has seen.
/// </summary>
internal static class ExoPowerPlan
{
    /// <summary>
    /// Fixed so a second Apply reuses the same plan instead of stacking duplicates, and so
    /// Detect can find it without matching on a display name the user may have renamed.
    /// </summary>
    internal const string ExoSchemeGuid = "7ae4b8a5-2c19-4d6f-9f3e-1b0c5d8e4a72";

    /// <summary>
    /// Ultimate Performance is Microsoft's own maximum-performance plan: no disk timeout, no
    /// PCIe link power saving, no core parking. It ships hidden on most SKUs, but
    /// <c>duplicatescheme</c> against its well-known GUID unhides it on Windows 10 1803+ and
    /// Windows 11, which is exactly what Exo does. High Performance is the fallback, Balanced
    /// the last resort — the base only sets the starting point, since every value that matters
    /// is written explicitly afterwards.
    /// </summary>
    private const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PowerSettingsRoot = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";
    private const string SchemesRoot = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    // Verified against the published alias/GUID list rather than recalled — one of these
    // (DISTRIBUTEUTIL) did not match what it is commonly misquoted as.
    private const string ProcThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string ProcThrottleMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string PerfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string CpMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string DistributeUtil = "e0007330-f589-42ed-a401-5ddb10e785d3";
    private const string SchedPolicy = "93b8b6dc-0698-4d1c-9ee4-0644e900c85d";
    private const string ShortSchedPolicy = "bae08b81-2d5e-4688-ad6a-13243356654b";
    private const string CpMaxCores = "ea062031-0e34-4ff1-9b6d-eb1059334028";
    private const string PerfEpp = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string PerfIncPolicy = "465e1f50-b610-473a-ab58-00d1077dc418";
    private const string PerfDecPolicy = "40fbefc7-2e9d-4d25-a185-0cfd8574bac6";
    private const string LatencyHintPerf = "619b7505-003b-4e82-b7a6-4dd29c300971";

    // Processor idle disable (5d76a2ca-e8c0-402f-a133-2158492d58ad) is deliberately NOT here,
    // and it is the most-recommended "max performance" setting on the internet. Disabling
    // C-states stops cores idling, and modern boost algorithms spend the thermal and power
    // headroom that idle cores free up. Turning it off measurably LOWERS peak boost clocks on
    // both Zen and recent Core parts. It is a performance setting that costs performance.

    // ── Topology ──────────────────────────────────────────────────────────────────────────

    internal sealed record CpuTopology(
        string Vendor,
        string Name,
        int LogicalProcessors,
        int PhysicalCores,
        bool IsHybrid,
        int PerformanceCores,
        int EfficiencyCores,
        int L3Domains,
        bool IsDesktop)
    {
        public bool IsIntel => Vendor == "Intel";
        public bool IsAmd => Vendor == "AMD";
        /// <summary>More than one L3 domain on AMD means more than one core complex die.</summary>
        public bool IsMultiCcd => IsAmd && L3Domains > 1;

        public string Summary =>
            IsHybrid
                ? $"{Name} - {PerformanceCores}P + {EfficiencyCores}E cores, {LogicalProcessors} threads"
                : $"{Name} - {PhysicalCores} cores, {LogicalProcessors} threads" +
                  (IsMultiCcd ? $", {L3Domains} core complexes" : "");
    }

    public static CpuTopology DetectTopology()
    {
        var vendorId = "";
        var name = "Unknown CPU";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            vendorId = key?.GetValue("VendorIdentifier")?.ToString() ?? "";
            name = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? name;
        }
        catch { }

        var vendor = vendorId.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                   : vendorId.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                   : "Unknown";

        var (cores, hybrid, pCores, eCores, l3) = ReadProcessorTopology();
        if (cores == 0) cores = Environment.ProcessorCount; // last resort, better than zero

        return new CpuTopology(vendor, name, Environment.ProcessorCount,
            cores, hybrid, pCores, eCores, l3, HasNoSystemBattery());
    }

    /// <summary>
    /// Walks the real processor relationship table. EfficiencyClass is the only reliable way to
    /// tell a P-core from an E-core; CPU name strings do not carry the split, and core counts
    /// alone cannot distinguish 8 cores from 6P+2E.
    /// </summary>
    private static (int Cores, bool Hybrid, int PCores, int ECores, int L3Domains)
        ReadProcessorTopology()
    {
        try
        {
            var cores = CollectEfficiencyClasses();
            if (cores.Count == 0) return (0, false, 0, 0, 1);

            var classes = cores.Distinct().ToList();
            var hybrid = classes.Count > 1;
            var top = classes.Max();
            var pCores = hybrid ? cores.Count(c => c == top) : cores.Count;
            var eCores = hybrid ? cores.Count - pCores : 0;

            return (cores.Count, hybrid, pCores, eCores, Math.Max(1, CountL3Domains()));
        }
        catch
        {
            // Detection failing must not stop the plan being created; it only costs the
            // hybrid-specific settings, which are skipped rather than guessed.
            return (0, false, 0, 0, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>
    /// True on a machine with no battery, i.e. a desktop. BatteryFlag 128 is the documented
    /// "no system battery" value. This is the one thing that changes the right answer for the
    /// minimum processor state, so it is measured rather than assumed.
    /// </summary>
    private static bool HasNoSystemBattery()
    {
        try { return GetSystemPowerStatus(out var st) && st.BatteryFlag == 128; }
        catch { return false; }  // unknown -> treat as laptop, the safer of the two
    }

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref uint returnedLength);

    /// <summary>One EfficiencyClass byte per physical core.</summary>
    private static List<byte> CollectEfficiencyClasses()
    {
        var result = new List<byte>();
        foreach (var (ptr, _) in EnumerateRelations(RelationProcessorCore))
        {
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (4) + Size (4), then
            // PROCESSOR_RELATIONSHIP { BYTE Flags; BYTE EfficiencyClass; ... }
            result.Add(Marshal.ReadByte(ptr, 9));
        }
        return result;
    }

    /// <summary>
    /// Distinct level-3 caches. On AMD this is the core-complex count, which is what makes a
    /// 7950X (two) behave differently from a 7800X3D (one) for game thread placement.
    /// </summary>
    private static int CountL3Domains()
    {
        var count = 0;
        foreach (var (ptr, _) in EnumerateRelations(RelationCache))
        {
            // CACHE_RELATIONSHIP { BYTE Level; ... } at offset 8.
            if (Marshal.ReadByte(ptr, 8) == 3) count++;
        }
        return count;
    }

    private static IEnumerable<(IntPtr Ptr, int Size)> EnumerateRelations(int relation)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(relation, IntPtr.Zero, ref len);
        if (len == 0) yield break;

        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(relation, buffer, ref len)) yield break;

            var offset = 0;
            while (offset < len)
            {
                var entry = IntPtr.Add(buffer, offset);
                var size = Marshal.ReadInt32(entry, 4);
                // A zero or negative size would spin forever; treat it as a corrupt table.
                if (size <= 0) yield break;
                yield return (entry, size);
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ── Plan settings, chosen from topology ───────────────────────────────────────────────

    // Non-processor subgroups. These live in the plan too, so Exo never edits a plan the user
    // chose — everything it sets is inside the plan it created and can delete again.
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubDisk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string DiskIdle = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
    // AHCI/NVMe link power management. Hidden by default. HIPM/DIPM let the SATA or NVMe link
    // drop into a low-power state between commands; coming back out is measured in
    // milliseconds and shows up as a stutter on the first access after an idle gap.
    private const string AhciLinkPower = "0b2d69d7-a2a1-449c-9680-f91c70521c60";
    // Wi-Fi radio power saving. Hidden by default. Anything other than maximum performance
    // parks the radio between beacons, which is added latency on every packet.
    private const string SubWireless = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WirelessPowerSave = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
    // Adaptive dimming. Pointless once the display never sleeps, and it dims mid-game.
    private const string VideoDim = "17aaa29b-8b43-4b94-aafe-35f64daaf1ee";
    // Processor performance boost POLICY (not mode). Hidden. How aggressively the scheduler
    // asks for boost above the guaranteed frequency.
    private const string PerfBoostPolicy = "45bcc044-d885-43e2-8605-ee0ec6e96b59";

    // Display and sleep. The plan tuned the CPU, the PCIe links, USB and the disks, and then
    // left Windows to blank the screen and suspend the machine on its own timer - so a long
    // shader compile, a download, or a benchmark being watched finished behind a black screen
    // and, on default timings, a sleeping PC. On MAINS, inside a plan the user opted into,
    // being idle is not a reason to stop.
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string StandbyIdle = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    private const string HibernateIdle = "9d7815a6-7ee4-497e-8888-515a05f02364";

    internal sealed record PlanSetting(string Subgroup, string Setting, int Value, string Why);

    /// <summary>
    /// The values the Exo plan gets on this specific machine. Only settings the machine
    /// actually exposes are returned, so nothing here can be written into a void.
    /// </summary>
    internal static List<PlanSetting> SettingsFor(CpuTopology cpu)
    {
        var list = new List<PlanSetting>
        {
            new(SubProcessor, ProcThrottleMax, 100, "Maximum processor state 100% on mains."),

            // The minimum state is the one value where "max performance" genuinely differs by
            // machine, so it is decided from the chassis rather than picked once.
            //
            // Desktop: 100. The clocks never drop, so there is no ramp latency at all. It costs
            // idle watts and heat, which a desktop cooler has to spare.
            //
            // Laptop: 5, even on mains. Thin chassis are power- and thermally-limited, and
            // holding every core at its base multiplier while idle spends the exact thermal
            // budget the boost algorithm needs when a game actually loads. Pinning it there is
            // slower in practice, not faster - so on a laptop the fast setting IS the low one.
            new(SubProcessor, ProcThrottleMin, cpu.IsDesktop ? 100 : 5,
                cpu.IsDesktop
                    ? "Minimum processor state 100% - clocks never drop, no ramp-up delay."
                    : "Minimum processor state low - protects the thermal budget boost needs on a laptop."),

            new(SubProcessor, PerfBoostMode, 2, "Turbo boost set to Aggressive."),
            new(SubProcessor, CpMinCores, 100, "All cores unparked."),
            new(SubProcessor, CpMaxCores, 100, "Every core available to the scheduler."),

            // Energy Performance Preference. 0 is maximum performance; Windows ships this
            // mid-scale even on High Performance, and on modern Intel parts it is the single
            // biggest lever on how eagerly the chip clocks up.
            new(SubProcessor, PerfEpp, 0, "Energy preference set fully to performance."),

            // Rocket: go straight to maximum on a load increase instead of stepping up through
            // intermediate P-states. Paired with a slow step-down so a brief idle between
            // frames does not drop the clock.
            new(SubProcessor, PerfIncPolicy, 2, "Clocks jump straight to maximum under load."),
            new(SubProcessor, PerfDecPolicy, 1, "Clocks step down slowly, not after every frame gap."),

            // When an application flags itself latency-sensitive, run flat out.
            new(SubProcessor, LatencyHintPerf, 100, "Latency-sensitive work runs at full performance."),

            // Utility distribution spreads load thinly to let cores park. With parking already
            // off it is redundant, but leaving it on works against the line above.
            new(SubProcessor, DistributeUtil, 0, "Utility distribution off - work is not spread thin to allow parking."),

            new(SubPciExpress, Aspm, 0, "PCIe link power management off - GPU and NVMe links stop sleeping."),
            new(SubUsb, UsbSelectiveSuspend, 0, "USB selective suspend off - no wake-up delay on mouse and keyboard."),
            new(SubDisk, DiskIdle, 0, "Drives never spun down on mains."),

            // 0 means never. Only ever written on AC, and only inside Exo's own plan - switch
            // back to Balanced and Windows' timers return untouched, because Exo never edits a
            // plan it did not create.
            new(SubVideo, VideoIdle, 0, "Screen never turns itself off on mains."),
            new(SubVideo, VideoDim, 0, "Screen never dims itself on mains."),
            new(SubSleep, StandbyIdle, 0, "PC never sleeps on mains."),
            new(SubSleep, HibernateIdle, 0, "PC never hibernates on mains."),

            // Hidden by default, and each one is a real latency source rather than folklore.
            new(SubProcessor, PerfBoostPolicy, 100, "Boost requested as aggressively as the silicon allows."),
            new(SubDisk, AhciLinkPower, 0, "Storage link power management off - no wake-up stall on the first access after idle."),
            new(SubWireless, WirelessPowerSave, 0, "Wi-Fi radio at maximum performance - the radio stops parking between beacons."),
        };

        // The only genuinely CPU-specific settings. They exist solely on heterogeneous parts,
        // which is why they are gated on measured topology rather than on a name or a
        // generation number.
        if (cpu.IsHybrid)
        {
            // Short, bursty threads are the ones a game's frame loop is made of. Confining
            // them to performance cores (1) keeps them off E-cores entirely.
            list.Add(new(SubProcessor, ShortSchedPolicy, 1,
                "Short-running threads pinned to performance cores."));
            // Longer-running work prefers P-cores but may spill to E-cores rather than queue.
            list.Add(new(SubProcessor, SchedPolicy, 2,
                "Longer threads prefer performance cores, can still use efficiency cores."));
        }

        return list.Where(s => SettingExists(s.Subgroup, s.Setting)).ToList();
    }

    /// <summary>
    /// True when Windows on this machine knows the setting. Power settings vary by CPU driver,
    /// Windows edition and OEM policy, so writing one blind is how a plan ends up with values
    /// that quietly do nothing.
    /// </summary>
    internal static bool SettingExists(string subgroup, string setting)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"{PowerSettingsRoot}\{subgroup}\{setting}");
        return key is not null;
    }

    public static bool PlanExists()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{SchemesRoot}\{ExoSchemeGuid}");
        return key is not null;
    }

    /// <summary>Is the Exo plan the one currently running?</summary>
    public static bool PlanIsActive()
    {
        var active = NativeReg.GetValue("HKLM", SchemesRoot, "ActivePowerScheme")?.ToString();
        return string.Equals(active, ExoSchemeGuid, StringComparison.OrdinalIgnoreCase);
    }

    // ── Op building ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stages the whole plan as elevated ops: duplicate Balanced into our fixed GUID, name it,
    /// write each supported setting's AC value, then activate. DC is never written — the
    /// battery profile of the duplicated plan is left exactly as Balanced had it.
    /// </summary>
    public static List<string> BuildApplyOps(CpuTopology cpu)
    {
        var ops = new List<string>();
        if (!PlanExists())
        {
            // Ultimate Performance first. The script tries each base in turn and stops at the
            // first that works, because duplicatescheme fails on a SKU that does not carry the
            // scheme. The base only sets the starting point - every value that matters is
            // written explicitly below - so falling back costs nothing.
            ops.Add($"planduplicate:{UltimatePerformanceGuid}|{ExoSchemeGuid}");
            ops.Add($"planduplicate:{HighPerformanceGuid}|{ExoSchemeGuid}");
            ops.Add($"planduplicate:{BalancedGuid}|{ExoSchemeGuid}");
        }

        ops.Add($"planname:{ExoSchemeGuid}|{PlanName(cpu)}");

        // Unhide every setting Exo writes, before writing it.
        //
        // Windows ships most of these with Attributes=1, which hides them from Power Options
        // entirely. powercfg sets them by GUID regardless, so the values did land - but the
        // user could not see, verify or undo a single one of them in Windows' own UI, and
        // "core parking off" is exactly the claim someone will want to check for themselves.
        // Attributes=2 makes a setting visible; it changes nothing about its value.
        foreach (var s in SettingsFor(cpu).Select(x => (x.Subgroup, x.Setting)).Distinct())
            ops.Add($"dword:HKLM\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerSettings\\{s.Subgroup}\\{s.Setting}|Attributes|2");

        foreach (var s in SettingsFor(cpu))
            ops.Add($"planac:{ExoSchemeGuid}|{s.Subgroup}|{s.Setting}|{s.Value}");
        ops.Add($"planactive:{ExoSchemeGuid}");
        return ops;
    }

    /// <summary>
    /// Restore: put the recorded plan back, then delete ours. Order matters — Windows refuses
    /// to delete the active scheme, so deleting first would leave the plan behind and the
    /// machine still on it.
    /// </summary>
    public static List<string> BuildRestoreOps(string? previousScheme)
    {
        var ops = new List<string>();
        if (!string.IsNullOrWhiteSpace(previousScheme) && Guid.TryParse(previousScheme, out _))
            ops.Add($"planactive:{previousScheme}");
        if (PlanExists())
            ops.Add($"plandelete:{ExoSchemeGuid}");
        return ops;
    }

    /// <summary>
    /// Plan name, restricted to characters the elevated script can carry safely. The topology
    /// goes in the name so the user can see what it was built for in Windows' own UI.
    /// </summary>
    internal static string PlanName(CpuTopology cpu)
    {
        var chassis = cpu.IsDesktop ? "" : " Laptop";
        var suffix = cpu.IsHybrid ? $"{cpu.PerformanceCores}P{cpu.EfficiencyCores}E"
                   : cpu.IsMultiCcd ? $"{cpu.PhysicalCores}C {cpu.L3Domains}CCD"
                   : $"{cpu.PhysicalCores}C";
        return Sanitize($"Exo - {cpu.Vendor} {suffix}{chassis}");
    }

    /// <summary>
    /// Letters, digits, spaces and hyphens only. The name is interpolated into a powercfg
    /// command line, so anything that could terminate a quoted argument is dropped rather
    /// than escaped — a plan name is not worth a quoting bug.
    /// </summary>
    internal static string Sanitize(string s)
    {
        var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray()).Trim();
        return clean.Length == 0 ? "Exo" : (clean.Length > 60 ? clean[..60].Trim() : clean);
    }

    // ── Detect ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rows for the system module. The plan counts as applied only when it exists, is active,
    /// AND every setting reads back correctly — an Exo plan sitting inactive is not applied,
    /// and neither is one the user has since edited.
    /// </summary>
    public static (bool Applied, List<(string Title, string Detail, bool Active)> Rows) Detect()
    {
        var rows = new List<(string, string, bool)>();
        var cpu = DetectTopology();

        rows.Add(("Processor", cpu.Summary, true));

        var exists = PlanExists();
        var active = exists && PlanIsActive();

        var drifted = new List<string>();
        if (exists)
        {
            foreach (var s in SettingsFor(cpu))
            {
                var current = NativeReg.GetDword("HKLM",
                    $@"{SchemesRoot}\{ExoSchemeGuid}\{s.Subgroup}\{s.Setting}", "ACSettingIndex");
                if (current != s.Value) drifted.Add(s.Setting);
            }
        }

        var ok = active && drifted.Count == 0;
        rows.Add(("Exo power plan",
            !exists ? "Not created yet."
            : !active ? "Created, but Windows is running a different plan."
            : drifted.Count > 0 ? $"Active, but {drifted.Count} setting(s) have been changed since."
            : "Active and matching what Exo set.",
            ok));

        if (cpu.IsHybrid)
        {
            var sched = NativeReg.GetDword("HKLM",
                $@"{SchemesRoot}\{ExoSchemeGuid}\{SubProcessor}\{ShortSchedPolicy}", "ACSettingIndex");
            rows.Add(("Performance-core scheduling",
                sched == 1
                    ? "Short game threads kept on performance cores."
                    : "Not set - short threads can still land on efficiency cores.",
                sched == 1));
        }

        // AMD cross-CCD placement. Advisory, because no power setting addresses it: it is the
        // chipset driver and Game Bar that tell Windows which die a game belongs on. Marked as
        // a firmware-class row so it can never block the module from reading applied.
        if (cpu.IsMultiCcd)
        {
            rows.Add(("Multi-die scheduling (firmware)",
                $"This CPU has {cpu.L3Domains} core complexes. Games that spill across both lose " +
                "frametime consistency to cross-die latency. Windows decides placement from the " +
                "AMD chipset driver, so keep that current - no power setting can substitute for it.",
                true));
        }

        return (ok, rows);
    }
}
