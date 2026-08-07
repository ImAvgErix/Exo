using System.Text.Json;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Pure chipset catalog / version / strip rules — no network, no elevation.
/// Split out so Contracts.Smoke can link it without the WinUI host (same pattern as
/// <see cref="NvidiaDetectLogic"/>).
/// </summary>
internal static class ChipsetDriverLogic
{
    internal sealed record PackageSpec(
        string Id,
        string Vendor,
        string Kind,
        string Title,
        string TargetVersion,
        IReadOnlyList<string> Sockets,
        string? DownloadUrl,
        bool DownloadIsLandingPage,
        string SupportUrl,
        IReadOnlyList<string> FileNameHints,
        IReadOnlyList<string> InstallSilentArgs,
        IReadOnlyList<string> StripDirectoryNames,
        IReadOnlyList<string> StripFileNameContains,
        IReadOnlyList<string> NeverStripContains);

    public static int CompareVersions(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;
        static int[] Parts(string v) =>
            Regex.Split(v.Trim(), @"[^\d]+")
                .Where(s => s.Length > 0)
                .Select(s => int.TryParse(s, out var n) ? n : 0)
                .ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <param name="vendor">"amd", "intel", or empty.</param>
    public static string? InferSocket(string cpuName, string vendor)
    {
        if (string.IsNullOrWhiteSpace(cpuName)) return null;
        if (vendor.Equals("amd", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(cpuName, @"(?i)Threadripper\s*PRO")) return "sWRX8";
            if (Regex.IsMatch(cpuName, @"(?i)Threadripper")) return "sTRX4";
            if (Regex.IsMatch(cpuName, @"(?i)EPYC")) return "SP3";
            // 7000/8000/9000 desktop families -> AM5
            if (Regex.IsMatch(cpuName, @"(?i)Ryzen\s*(AI\s*)?(?:\d\s*)?[579]\s*[789]\d{2}"))
                return "AM5";
            if (Regex.IsMatch(cpuName, @"(?i)Ryzen\s*[3579]\s*[1-5]\d{3}|Ryzen\s*[357]\s*G|Athlon"))
                return "AM4";
            return "AM4";
        }
        if (vendor.Equals("intel", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(cpuName, @"(?i)Core\s*Ultra|Series\s*2"))
                return "LGA1851";
            if (Regex.IsMatch(cpuName, @"(?i)i[3579]-1[234]\d{3}|i[3579]-\d{4,5}[KF]?S?"))
                return "LGA1700";
            if (Regex.IsMatch(cpuName, @"(?i)i[3579]-1[01]\d{3}"))
                return "LGA1200";
            return "LGA1700";
        }
        return null;
    }

    public static IReadOnlyList<PackageSpec> LoadCatalog(string? catalogPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
                return Array.Empty<PackageSpec>();
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!doc.RootElement.TryGetProperty("packages", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<PackageSpec>();
            var list = new List<PackageSpec>();
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(new PackageSpec(
                    Str(p, "id") ?? "",
                    Str(p, "vendor") ?? "",
                    Str(p, "kind") ?? "chipset",
                    Str(p, "title") ?? "Chipset software",
                    Str(p, "targetVersion") ?? "",
                    Arr(p, "sockets"),
                    Str(p, "downloadUrl"),
                    p.TryGetProperty("downloadIsLandingPage", out var land) && land.ValueKind == JsonValueKind.True,
                    Str(p, "supportUrl") ?? Str(p, "downloadUrl") ?? "",
                    Arr(p, "fileNameHints"),
                    Arr(p, "installSilentArgs"),
                    Arr(p, "stripDirectoryNames"),
                    Arr(p, "stripFileNameContains"),
                    Arr(p, "neverStripContains")));
            }
            return list;
        }
        catch { return Array.Empty<PackageSpec>(); }
    }

    private static string? Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static IReadOnlyList<string> Arr(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return e.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    public sealed record StripResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept);

    public static StripResult StripPackage(string extractDir, PackageSpec spec)
    {
        var removed = new List<string>();
        var kept = new List<string>();
        if (!Directory.Exists(extractDir)) return new StripResult(removed, kept);

        bool Protected(string name) =>
            spec.NeverStripContains.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

        bool WantStripDir(string name) =>
            !Protected(name) &&
            spec.StripDirectoryNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)
                                              || name.Contains(s, StringComparison.OrdinalIgnoreCase));

        bool WantStripFile(string name) =>
            !Protected(name) &&
            spec.StripFileNameContains.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories).ToList())
            {
                var leaf = Path.GetFileName(dir);
                if (WantStripDir(leaf))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        removed.Add(leaf + "/");
                    }
                    catch { kept.Add(leaf + " (locked)"); }
                }
            }

            foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).ToList())
            {
                var leaf = Path.GetFileName(file);
                if (WantStripFile(leaf))
                {
                    try
                    {
                        File.Delete(file);
                        removed.Add(leaf);
                    }
                    catch { kept.Add(leaf + " (locked)"); }
                }
            }
        }
        catch { }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(extractDir).Take(20))
                kept.Add(Path.GetFileName(dir) + "/");
        }
        catch { }

        return new StripResult(
            removed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            kept.Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList());
    }
}
