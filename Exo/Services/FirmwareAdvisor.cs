using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Reads the machine-level settings Exo can measure but cannot change, because they live in
/// UEFI rather than Windows.
///
/// These matter more than most of what Exo *can* set: RAM running at JEDEC base speed instead
/// of its rated XMP/EXPO profile, and Resizable BAR left off, are the two largest and most
/// common losses on a gaming PC. A tool that silently skips them because it cannot fix them
/// is hiding the two things that would help most.
///
/// Everything here is read-only. Exo never writes firmware.
/// </summary>
internal static class FirmwareAdvisor
{
    public sealed record Finding(
        string Id,
        string Title,
        /// <summary>true = already in the good state, false = a real miss, null = could not read.</summary>
        bool? Ok,
        string Detail,
        /// <summary>Exact thing to change, and where. Empty when Ok or unknown.</summary>
        string FixWhere);

    public static List<Finding> Scan()
    {
        var findings = new List<Finding>();
        findings.Add(ScanMemoryProfile());
        findings.Add(ScanResizableBar());
        findings.Add(ScanVirtualizationSecurity());
        findings.Add(ScanBiosVersion());
        return findings;
    }

    /// <summary>
    /// XMP / EXPO. Compares the speed the modules are actually clocked at against the highest
    /// speed they advertise. A gap means the profile was never enabled in firmware.
    /// </summary>
    private static Finding ScanMemoryProfile()
    {
        try
        {
            var (configured, rated) = ReadMemorySpeeds();
            if (configured <= 0 || rated <= 0)
            {
                return new Finding("mem-profile", "RAM speed profile", null,
                    "Could not read the memory profile on this machine.", "");
            }

            // Treat within ~5% as "running its profile" — vendors report rounded values and a
            // 3600 kit legitimately reads back as 3593.
            if (configured >= rated * 0.95)
            {
                return new Finding("mem-profile", "RAM speed profile", true,
                    $"Running at {configured} MT/s, its rated speed. XMP/EXPO is on.", "");
            }

            var lost = (int)Math.Round((1.0 - (double)configured / rated) * 100);
            return new Finding("mem-profile", "RAM speed profile", false,
                $"Your RAM is running at {configured} MT/s but is rated for {rated} MT/s — " +
                $"about {lost}% below what you paid for. This costs real frames in CPU-bound games.",
                "Enable XMP (Intel) or EXPO/DOCP (AMD) in your UEFI. It is usually one dropdown " +
                "on the first page or under an overclocking/tweaker menu. Exo cannot set this — " +
                "it is firmware, not Windows.");
        }
        catch (Exception ex)
        {
            return new Finding("mem-profile", "RAM speed profile", null, ex.Message, "");
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint bufferSize);

    /// <summary>
    /// Rated vs actual memory speed, straight out of SMBIOS.
    ///
    /// Deliberately not System.Management: the rest of this codebase avoids the WMI
    /// dependency (see SteamNativeApply's adapter enumeration), and GetSystemFirmwareTable
    /// gives the same numbers with no extra package and no WMI service dependency.
    /// SMBIOS Type 17 "Memory Device": Speed at 0x15 is the module's rated maximum,
    /// ConfiguredMemorySpeed at 0x20 is what it is actually clocked at.
    /// </summary>
    private static (int Configured, int Rated) ReadMemorySpeeds()
    {
        const uint RSMB = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return (0, 0);

        var buf = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0) return (0, 0);

        // RawSMBIOSData: 4 header bytes, DWORD length, then the table itself.
        if (buf.Length < 8) return (0, 0);
        var tableLen = BitConverter.ToUInt32(buf, 4);
        var start = 8;
        var end = (int)Math.Min((long)start + tableLen, buf.Length);

        var configured = 0;
        var rated = 0;
        var p = start;
        while (p + 4 <= end)
        {
            var type = buf[p];
            int len = buf[p + 1];
            if (len < 4) break;
            if (p + len > end) break;

            if (type == 17)
            {
                // Guard every read: older SMBIOS revisions have shorter Type 17 records.
                if (len >= 0x17)
                {
                    int spd = BitConverter.ToUInt16(buf, p + 0x15);
                    if (spd > rated) rated = spd;
                }
                if (len >= 0x22)
                {
                    int cfg = BitConverter.ToUInt16(buf, p + 0x20);
                    if (cfg > configured) configured = cfg;
                }
            }
            if (type == 127) break; // end-of-table

            // Skip the formatted area, then the string set (double NUL terminated).
            p += len;
            while (p + 1 < end && !(buf[p] == 0 && buf[p + 1] == 0)) p++;
            p += 2;
        }

        // Some firmware reports only Speed. Treating that as "configured == rated" avoids
        // inventing a failure out of a field the board simply does not publish.
        if (configured == 0) configured = rated;
        return (configured, Math.Max(rated, configured));
    }

    /// <summary>
    /// Resizable BAR. The driver exposes its state per adapter; enabling it needs UEFI
    /// (Above 4G Decoding first). Real gains in several engines, and commonly left off.
    /// </summary>
    private static Finding ScanResizableBar()
    {
        try
        {
            var enabled = ReadResizableBarEnabled();
            if (enabled is null)
            {
                return new Finding("rebar", "Resizable BAR", null,
                    "Could not read Resizable BAR state on this GPU.", "");
            }
            if (enabled.Value)
            {
                return new Finding("rebar", "Resizable BAR", true,
                    "On — the CPU can address the whole framebuffer.", "");
            }
            return new Finding("rebar", "Resizable BAR", false,
                "Off. The CPU can only see the GPU's memory 256 MB at a time, which costs " +
                "frames in engines that stream large textures.",
                "In UEFI: enable Above 4G Decoding first, then Resizable BAR (sometimes called " +
                "Smart Access Memory on AMD boards). Both are firmware settings — Exo cannot " +
                "set them. Your GPU and CPU both have to support it; most from 2020 on do.");
        }
        catch (Exception ex)
        {
            return new Finding("rebar", "Resizable BAR", null, ex.Message, "");
        }
    }

    private static bool? ReadResizableBarEnabled()
    {
        // NVIDIA driver publishes rBAR state under the display class key.
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return null;
            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var adapter = classKey.OpenSubKey(sub);
                if (adapter is null) continue;
                var desc = adapter.GetValue("DriverDesc")?.ToString() ?? "";
                if (desc.Length == 0) continue;
                if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;

                // "Enabled" is the fact this probe answers. The Supported flags only bound it:
                // reading Supported as the answer reported rBAR "On" on exactly the machines
                // this advisor exists to catch — GPU supports it, UEFI has it switched off.
                var e = adapter.GetValue("RMResizableBarEnabled");
                if (e is int ei) return ei != 0;
                foreach (var name in new[] { "RmResizableBarSupported", "ResizableBarSupported" })
                {
                    var v = adapter.GetValue(name);
                    // Unsupported → certainly not enabled. Supported alone says nothing
                    // about the switch, so that case stays unknown.
                    if (v is int i && i == 0) return false;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Virtualization-based security. Exo does not turn this off on its own — it is a real
    /// security feature and the trade-off is the user's call, so this is reported as
    /// information with the cost stated, not as a failing check.
    /// </summary>
    private static Finding ScanVirtualizationSecurity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            var enabled = key?.GetValue("Enabled");
            var on = enabled is int i && i != 0;
            if (!on)
            {
                return new Finding("vbs", "Memory Integrity (VBS)", true,
                    "Off — no virtualization overhead on your game's CPU time.", "");
            }
            return new Finding("vbs", "Memory Integrity (VBS)", null,
                "On. It costs measurable frames in some titles, and it is also a genuine " +
                "security feature that blocks a real class of driver attack. Exo will not " +
                "turn it off behind your back.",
                "If you want the frames back: Windows Security → Device security → Core " +
                "isolation → Memory integrity. Understand you are trading away a security " +
                "boundary to do it.");
        }
        catch (Exception ex)
        {
            return new Finding("vbs", "Memory Integrity (VBS)", null, ex.Message, "");
        }
    }

    private static Finding ScanBiosVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var vendor = key?.GetValue("BaseBoardManufacturer")?.ToString() ?? "";
            var board = key?.GetValue("BaseBoardProduct")?.ToString() ?? "";
            var ver = key?.GetValue("BIOSVersion")?.ToString() ?? "";
            var date = key?.GetValue("BIOSReleaseDate")?.ToString() ?? "";
            if (board.Length == 0 && ver.Length == 0)
                return new Finding("bios", "Motherboard firmware", null, "Could not read board details.", "");

            var label = string.Join(" ", new[] { vendor, board }.Where(s => s.Length > 0));
            var detail = $"{label} — BIOS {ver}".Trim();
            if (date.Length > 0) detail += $" ({date})";
            // Reported, never acted on. Flashing firmware from an application is how machines die.
            return new Finding("bios", "Motherboard firmware", null, detail,
                "Exo reads this and never writes it. If your board is several BIOS revisions " +
                "behind, updating it from the vendor's own tool can bring memory-profile and " +
                "CPU-boost fixes — but that is a decision to make deliberately, not from here.");
        }
        catch (Exception ex)
        {
            return new Finding("bios", "Motherboard firmware", null, ex.Message, "");
        }
    }
}
