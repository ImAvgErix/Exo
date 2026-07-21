using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// One Exo Competitive power plan (Intel/AMD/generic), no Ultimate spam.
/// Cleans duplicate "Ultimate Performance" clones left by bad Apply paths.
/// </summary>
public static class ExoPowerPlanNative
{
    public const string UltimateTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PowerSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static string TargetNameForCpu()
    {
        var (vendor, _, _) = GetCpuVendor();
        return vendor switch
        {
            "intel" => "Exo Competitive Intel",
            "amd" => "Exo Competitive AMD",
            _ => "Exo Competitive"
        };
    }

    public static (string Vendor, string Name, bool Hybrid) GetCpuVendor()
    {
        var name = "";
        try
        {
            name = NativeReg.GetValue("HKLM", @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")
                       ?.ToString() ?? "";
        }
        catch { }

        if (Regex.IsMatch(name, @"(?i)Intel|Core\(TM\)|Core Ultra|Xeon"))
        {
            var hybrid = Regex.IsMatch(name, @"(?i)Ultra|i[3579]-1[2-9]|i[3579]-2[0-9]");
            return ("intel", name, hybrid);
        }
        if (Regex.IsMatch(name, @"(?i)AMD|Ryzen|Threadripper|EPYC|Athlon"))
            return ("amd", name, false);
        return ("generic", name, false);
    }

    public static IReadOnlyList<(string Guid, string Name, bool Active)> ListSchemes()
    {
        var list = new List<(string, string, bool)>();
        var text = Run("powercfg", "/l");
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+)\)(\s+\*)?");
            if (!m.Success) continue;
            list.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(),
                line.Contains('*') || m.Groups[3].Success));
        }
        return list;
    }

    public static string? GetActiveGuid()
    {
        var line = Run("powercfg", "/getactivescheme");
        var m = Regex.Match(line, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static string? FindGuidByName(string name)
    {
        foreach (var s in ListSchemes())
        {
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return s.Guid;
        }
        return null;
    }

    public static bool IsExoPlanActive()
    {
        var active = GetActiveGuid();
        if (active is null) return false;
        foreach (var n in new[] { "Exo Competitive Intel", "Exo Competitive AMD", "Exo Competitive" })
        {
            var g = FindGuidByName(n);
            if (g is not null && g.Equals(active, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var line = Run("powercfg", "/getactivescheme");
        return line.Contains("Exo Competitive", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensure exactly one Exo Competitive plan exists, apply competitive knobs, activate it.
    /// Deletes duplicate Ultimate Performance clones (keeps stock Balanced/High/Saver/Nexus/Exo).
    /// </summary>
    public static (bool Ok, string Name, string? Guid, int SettingsWritten, int DeletedDuplicates) EnsureAndActivate()
    {
        var (vendor, _, hybrid) = GetCpuVendor();
        var targetName = TargetNameForCpu();
        var deleted = PurgeDuplicateUltimatePlans(keepExoName: targetName);

        var guid = FindGuidByName(targetName);
        if (guid is null)
        {
            // Prefer reusing an existing "Exo Competitive*" leftover
            foreach (var s in ListSchemes())
            {
                if (s.Name.StartsWith("Exo Competitive", StringComparison.OrdinalIgnoreCase))
                {
                    guid = s.Guid;
                    Run("powercfg", $"/changename {guid} \"{targetName}\" \"Exo competitive host plan ({vendor})\"");
                    break;
                }
            }
        }

        if (guid is null)
        {
            // Duplicate Ultimate template ONCE and rename immediately (never leave bare Ultimate clones).
            var dup = Run("powercfg", $"-duplicatescheme {UltimateTemplateGuid}");
            var m = Regex.Match(dup, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
                RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                dup = Run("powercfg", $"-duplicatescheme {HighPerfGuid}");
                m = Regex.Match(dup, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
                    RegexOptions.IgnoreCase);
            }
            if (m.Success)
            {
                guid = m.Groups[1].Value.ToLowerInvariant();
                Run("powercfg", $"/changename {guid} \"{targetName}\" \"Exo competitive host plan ({vendor})\"");
            }
        }

        if (guid is null)
        {
            // Last resort: activate High performance
            Run("powercfg", "/S SCHEME_MIN");
            return (false, targetName, GetActiveGuid(), 0, deleted);
        }

        var written = ApplyCompetitiveSettings(guid, vendor, hybrid);
        Run("powercfg", $"/setactive {guid}");
        Run("powercfg", $"/S {guid}");

        // Second cleanup pass after we own the active plan
        deleted += PurgeDuplicateUltimatePlans(keepExoName: targetName);

        var ok = IsExoPlanActive();
        return (ok, targetName, guid, written, deleted);
    }

    /// <summary>
    /// Delete every "Ultimate Performance" scheme that is not the active Exo plan.
    /// Never deletes Balanced, High performance, Power saver, Nexus, or Exo Competitive*.
    /// </summary>
    public static int PurgeDuplicateUltimatePlans(string? keepExoName = null)
    {
        keepExoName ??= TargetNameForCpu();
        var active = GetActiveGuid();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BalancedGuid, HighPerfGuid, PowerSaverGuid
        };
        var exoGuid = FindGuidByName(keepExoName);
        if (exoGuid is not null) keep.Add(exoGuid);
        foreach (var s in ListSchemes())
        {
            if (s.Name.StartsWith("Exo Competitive", StringComparison.OrdinalIgnoreCase))
                keep.Add(s.Guid);
            if (s.Name.Contains("Nexus", StringComparison.OrdinalIgnoreCase))
                keep.Add(s.Guid);
        }

        var deleted = 0;
        foreach (var s in ListSchemes())
        {
            if (!s.Name.Equals("Ultimate Performance", StringComparison.OrdinalIgnoreCase))
                continue;
            if (keep.Contains(s.Guid)) continue;
            if (active is not null && s.Guid.Equals(active, StringComparison.OrdinalIgnoreCase))
            {
                // Switch off Ultimate before delete
                if (exoGuid is not null)
                    Run("powercfg", $"/setactive {exoGuid}");
                else
                    Run("powercfg", "/S SCHEME_MIN");
            }
            var outText = Run("powercfg", $"-delete {s.Guid}");
            // powercfg returns empty on success
            deleted++;
        }

        // Also collapse extra Exo Competitive* clones — keep one target name only
        var exoPlans = ListSchemes()
            .Where(s => s.Name.StartsWith("Exo Competitive", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exoPlans.Count > 1)
        {
            var primary = exoPlans.FirstOrDefault(s => s.Name.Equals(keepExoName, StringComparison.OrdinalIgnoreCase));
            if (primary.Guid is null) primary = exoPlans[0];
            foreach (var s in exoPlans)
            {
                if (s.Guid.Equals(primary.Guid, StringComparison.OrdinalIgnoreCase)) continue;
                Run("powercfg", $"-delete {s.Guid}");
                deleted++;
            }
        }

        return deleted;
    }

    public static int ApplyCompetitiveSettings(string schemeGuid, string vendor, bool hybrid)
    {
        var n = 0;
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PROCTHROTTLEMIN", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PROCTHROTTLEMAX", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "CPMINCORES", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "CPMAXCORES", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFEPP", 0);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFBOOSTMODE", 2);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "SYSCOOLPOL", 1);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "LATENCYHINTPERF", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "LATENCYHINTUNPARK", 100);
        n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFDUTYCYCLING", 0);

        if (vendor == "intel")
        {
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFAUTONOMOUS", 0);
            if (hybrid)
            {
                n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "SCHEDPOLICY", 2);
                n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "SHORTSCHEDPOLICY", 2);
                n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "HETEROCLASS1INITIALPERF", 100);
                n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "HETEROCLASS0FLOORPERF", 100);
            }
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFBOOSTPOL", 100);
        }
        else if (vendor == "amd")
        {
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFAUTONOMOUS", 1);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFAUTONOMOUSWINDOW", 30);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFBOOSTMODE", 2);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFBOOSTPOL", 100);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "SCHEDPOLICY", 2);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "SHORTSCHEDPOLICY", 2);
        }
        else
        {
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFAUTONOMOUS", 0);
            n += SetAcDc(schemeGuid, "SUB_PROCESSOR", "PERFBOOSTMODE", 2);
        }

        n += SetAcDc(schemeGuid, "SUB_PCIEXPRESS", "ASPM", 0);
        n += SetAcDc(schemeGuid, "SUB_DISK", "DISKIDLE", 0);
        n += SetAcDc(schemeGuid, "SUB_SLEEP", "STANDBYIDLE", 0);
        n += SetAcDc(schemeGuid, "SUB_SLEEP", "HYBRIDSLEEP", 0);
        n += SetAcDc(schemeGuid, "SUB_SLEEP", "HIBERNATEIDLE", 0);
        n += SetAcDc(schemeGuid, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0);
        n += SetAcDc(schemeGuid, "SUB_VIDEO", "VIDEOIDLE", 0);

        return n;
    }

    private static int SetAcDc(string scheme, string sub, string setting, int value)
    {
        var ok = 0;
        foreach (var mode in new[] { "setacvalueindex", "setdcvalueindex" })
        {
            Run("powercfg", $"/{mode} {scheme} {sub} {setting} {value}");
            ok++;
        }
        return ok;
    }

    private static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return "";
            var o = p.StandardOutput.ReadToEnd();
            var e = p.StandardError.ReadToEnd();
            p.WaitForExit(12000);
            return o + e;
        }
        catch
        {
            return "";
        }
    }
}
