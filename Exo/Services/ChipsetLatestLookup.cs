using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Resolves the newest AMD / Intel chipset *package* revision from the live network
/// (Microsoft Update Catalog + AMD support pages), with a short disk cache so detect
/// stays snappy offline.
///
/// Catalog target in chipset-catalog.json is the floor / offline fallback — not the
/// sole source of "newest" once the network answers.
/// </summary>
internal static class ChipsetLatestLookup
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(5);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = FetchBudget };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/json,*/*");
        return c;
    }

    private static string CachePath(string vendor) =>
        Path.Combine(PathHelper.AppDataDir, $"chipset-latest-{vendor.ToLowerInvariant()}.json");

    private sealed class CacheEntry
    {
        public string? Version { get; set; }
        public string? Source { get; set; }
        public DateTimeOffset FetchedUtc { get; set; }
    }

    /// <summary>
    /// Newest package revision for the vendor, or null if nothing better than the
    /// offline catalog target could be resolved.
    /// </summary>
    public static string? ResolveLatest(string vendor, string? catalogFloor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return catalogFloor;

        var cached = ReadCache(vendor);
        if (cached?.Version is { Length: > 0 } cv &&
            DateTimeOffset.UtcNow - cached.FetchedUtc < CacheTtl)
        {
            return Prefer(cv, catalogFloor);
        }

        string? live = null;
        string? source = null;
        try
        {
            using var cts = new CancellationTokenSource(FetchBudget);
            var (v, src) = FetchLatest(vendor, cts.Token);
            live = v;
            source = src;
        }
        catch
        {
            // Network is optional; catalog floor remains valid offline.
        }

        if (!string.IsNullOrWhiteSpace(live))
        {
            WriteCache(vendor, live!, source ?? "network");
            return Prefer(live, catalogFloor);
        }

        // Stale cache still better than nothing.
        if (cached?.Version is { Length: > 0 })
            return Prefer(cached.Version, catalogFloor);

        return catalogFloor;
    }

    private static string Prefer(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return b ?? "";
        if (string.IsNullOrWhiteSpace(b)) return a!;
        return ChipsetDriverInstaller.CompareVersions(a, b) >= 0 ? a! : b!;
    }

    private static (string? Version, string? Source) FetchLatest(string vendor, CancellationToken ct)
    {
        if (vendor.Equals("amd", StringComparison.OrdinalIgnoreCase))
            return FetchAmd(ct);
        if (vendor.Equals("intel", StringComparison.OrdinalIgnoreCase))
            return FetchIntel(ct);
        return (null, null);
    }

    private static (string? Version, string? Source) FetchAmd(CancellationToken ct)
    {
        string? best = null;

        // 1) Microsoft Update Catalog titles often include the package revision.
        try
        {
            var mucTask = ChipsetMucClient.SearchAsync("amd", ct);
            var list = mucTask.ConfigureAwait(false).GetAwaiter().GetResult();
            foreach (var u in list)
            {
                foreach (var candidate in new[] { u.Version, u.Title })
                {
                    var v = ExtractPackageVersion(candidate);
                    if (v is null) continue;
                    if (best is null || ChipsetDriverInstaller.CompareVersions(v, best) > 0)
                        best = v;
                }
            }
            if (best is not null) return (best, "muc");
        }
        catch { /* fall through */ }

        // 2) AMD / mirror pages that list the current chipset package revision.
        foreach (var url in new[]
                 {
                     "https://www.amd.com/en/support/downloads/drivers.html/chipsets/chipsets-socket-am5/amd-socket-am5-chipset.html",
                     "https://www.amd.com/en/support/downloads/drivers.html/chipsets/chipsets-socket-am4/amd-socket-am4-chipset.html",
                     "https://www.amd.com/en/support/downloads/drivers.html/chipsets/laptop-chipsets/amd-ryzen-and-athlon-mobile-chipset.html",
                     "https://www.techpowerup.com/download/amd-ryzen-chipset-drivers/",
                 })
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var html = Http.GetStringAsync(url, ct).ConfigureAwait(false).GetAwaiter().GetResult();
                foreach (Match m in Regex.Matches(
                             html,
                             @"(?:Revision(?:\s+Number)?|AMD_Chipset_Software[_-]?|amd_software[_-]?|Chipset(?:\s+Software)?(?:\s+Driver)?s?\s+)[^\d]{0,24}(\d+\.\d+\.\d+\.\d+)",
                             RegexOptions.IgnoreCase))
                {
                    var v = m.Groups[1].Value;
                    if (!IsPackageRev(v)) continue;
                    if (best is null || ChipsetDriverInstaller.CompareVersions(v, best) > 0)
                        best = v;
                }
                // Broader scan for 7.x / 8.x package-looking versions on the page.
                foreach (Match m in Regex.Matches(html, @"\b([6789]\.\d{2}\.\d{2}\.\d{3,5})\b"))
                {
                    var v = m.Groups[1].Value;
                    if (!IsPackageRev(v)) continue;
                    if (best is null || ChipsetDriverInstaller.CompareVersions(v, best) > 0)
                        best = v;
                }
                if (best is not null) return (best, url);
            }
            catch { /* next url */ }
        }

        return (best, best is null ? null : "scan");
    }

    private static (string? Version, string? Source) FetchIntel(CancellationToken ct)
    {
        try
        {
            var list = ChipsetMucClient.SearchAsync("intel", ct).ConfigureAwait(false).GetAwaiter().GetResult();
            string? best = null;
            foreach (var u in list)
            {
                var v = ExtractPackageVersion(u.Version) ?? ExtractPackageVersion(u.Title);
                if (v is null) continue;
                if (best is null || ChipsetDriverInstaller.CompareVersions(v, best) > 0)
                    best = v;
            }
            if (best is not null) return (best, "muc");
        }
        catch { }
        return (null, null);
    }

    private static string? ExtractPackageVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\b(\d+\.\d+\.\d+(?:\.\d+)?)\b");
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        return IsPackageRev(v) ? v : null;
    }

    private static bool IsPackageRev(string ver)
    {
        var parts = ver.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var major)) return false;
        // Chipset packages: AMD 6–9.x, Intel 10.x INF utility line.
        return major is >= 6 and <= 20;
    }

    private static CacheEntry? ReadCache(string vendor)
    {
        try
        {
            var path = CachePath(vendor);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteCache(string vendor, string version, string source)
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var json = JsonSerializer.Serialize(new CacheEntry
            {
                Version = version,
                Source = source,
                FetchedUtc = DateTimeOffset.UtcNow,
            });
            File.WriteAllText(CachePath(vendor), json);
        }
        catch { /* cache is best-effort */ }
    }
}
