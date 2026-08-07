using System.Globalization;
using System.Text.Json;

namespace Exo.Services;

/// <summary>
/// Parses NVIDIA's driver lookup response (AjaxDriverService <c>DriverManualLookup</c>).
///
/// Written against a real captured response rather than assumption, which caught four things
/// that would each have shipped broken:
///
/// <list type="bullet">
/// <item>The download host is <c>us.download.nvidia.com</c> on this response, not the
/// <c>international.download.nvidia.com</c> that most documentation shows. The URL is a field
/// in the response, so Exo uses what it is given and never builds one.</item>
/// <item><c>DownloadURLFileSize</c> is a display string — <c>"979.17 MB"</c> — not a byte
/// count. Treating it as a number gives zero.</item>
/// <item>Every human-readable field is URL-encoded (<c>GeForce%20Game%20Ready%20Driver</c>).</item>
/// <item>Booleans are strings: <c>"1"</c> and <c>"0"</c>, never JSON true/false.</item>
/// </list>
///
/// The <c>series</c>/<c>products</c> list is the useful part nothing else uses: it names every
/// GPU the driver supports, so Exo can confirm a driver covers the card in the machine instead
/// of assuming the newest release still does. That assumption is exactly what breaks on Kepler
/// and Maxwell, where NVIDIA dropped support mid-branch.
/// </summary>
internal static class NvidiaDriverLookup
{
    internal sealed record DriverRelease(
        string Version,
        string Branch,
        string Name,
        string DownloadUrl,
        string DetailsUrl,
        long SizeBytes,
        string SizeDisplay,
        DateTimeOffset? Released,
        bool IsWhql,
        bool IsBeta,
        bool IsStudio,
        IReadOnlyList<string> SupportedProducts)
    {
        /// <summary>
        /// True when this driver lists the given adapter.
        ///
        /// Normalised equality, NOT substring. The normaliser already strips the vendor prefix
        /// and the "Laptop GPU" suffix, which is what made a loose compare seem necessary:
        /// Windows reports "NVIDIA GeForce RTX 4070 Laptop GPU" against a catalogue entry of
        /// "NVIDIA GeForce RTX 4070", and after normalising both are "rtx4070".
        ///
        /// Substring matching was tried first and is wrong in a way that matters: a card the
        /// driver does NOT list would match any longer entry containing its name, so an
        /// unlisted "RTX 3070" would be reported as supported by a driver that only ships
        /// "RTX 3070 Ti". Claiming support that is not there is the failure mode to avoid -
        /// it points a user at a driver that will not install.
        /// </summary>
        public bool Supports(string adapterName)
        {
            if (string.IsNullOrWhiteSpace(adapterName)) return false;
            var norm = Normalize(adapterName);
            return norm.Length > 0 && SupportedProducts.Any(p =>
                string.Equals(Normalize(p), norm, StringComparison.Ordinal));
        }

        private static string Normalize(string s) =>
            new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()
                .Replace("nvidia", "").Replace("geforce", "").Replace("laptopgpu", "");
    }

    /// <summary>
    /// Parses one lookup response. Returns null rather than throwing on anything malformed —
    /// this runs against a live third-party endpoint, and a driver check is never a good reason
    /// to fail a detect pass.
    /// </summary>
    public static DriverRelease? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (Str(root, "Success") != "1") return null;
            if (!root.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
            if (ids.GetArrayLength() == 0) return null;
            if (!ids[0].TryGetProperty("downloadInfo", out var info)) return null;

            var version = Str(info, "Version");
            var url = Str(info, "DownloadURL");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return null;

            var products = new List<string>();
            if (info.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in series.EnumerateArray())
                {
                    if (!s.TryGetProperty("products", out var prods) || prods.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var p in prods.EnumerateArray())
                    {
                        var name = Decode(Str(p, "productName"));
                        if (!string.IsNullOrWhiteSpace(name)) products.Add(name);
                    }
                }
            }

            var sizeDisplay = Str(info, "DownloadURLFileSize");
            return new DriverRelease(
                Version: version,
                Branch: Str(info, "Release"),
                Name: Decode(Str(info, "Name")),
                DownloadUrl: url,
                DetailsUrl: Str(info, "DetailsURL"),
                SizeBytes: ParseSize(sizeDisplay),
                SizeDisplay: sizeDisplay,
                Released: ParseDate(Str(info, "ReleaseDateTime")),
                IsWhql: Str(info, "IsWHQL") == "1",
                IsBeta: Str(info, "IsBeta") == "1",
                // IsCRD marks the Creator/Studio branch. Game Ready is the default for a
                // gaming machine; Studio trades new-game day-one support for longer soak time.
                IsStudio: Str(info, "IsCRD") == "1",
                SupportedProducts: products);
        }
        catch { return null; }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>NVIDIA URL-encodes every display string in this feed.</summary>
    internal static string Decode(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    /// <summary>
    /// "979.17 MB" -> bytes. Returns 0 when the shape is unrecognised, which callers must treat
    /// as "unknown size" rather than "empty download" — a zero here has bitten size checks
    /// before.
    /// </summary>
    internal static long ParseSize(string display)
    {
        if (string.IsNullOrWhiteSpace(display)) return 0;
        var parts = display.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        var mult = parts[1].ToUpperInvariant() switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            _ => 0L
        };
        return mult == 0 ? 0 : (long)(n * mult);
    }

    /// <summary>"Tue Jul 07, 2026" — a US-format display date, not ISO.</summary>
    internal static DateTimeOffset? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        foreach (var fmt in new[] { "ddd MMM dd, yyyy", "ddd MMM d, yyyy", "MMM dd, yyyy" })
        {
            if (DateTimeOffset.TryParseExact(s.Trim(), fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dt))
                return dt;
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var any) ? any : null;
    }

    /// <summary>
    /// Windows reports NVIDIA drivers as "32.0.15.9186"; NVIDIA calls that one "591.86". The
    /// last five digits of the third and fourth parts combined are the NVIDIA version, split
    /// three-then-two. Verified against a real machine: 32.0.15.9186 -> 591.86, and it
    /// round-trips the current release, 32.0.16.1074 -> 610.74.
    /// </summary>
    public static string? ConvertWindowsVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4) return null;
        if (!int.TryParse(parts[2], out var c) || !int.TryParse(parts[3], out var d)) return null;
        var combined = (c * 10000L + d).ToString();
        if (combined.Length < 5) combined = combined.PadLeft(5, '0');
        var last5 = combined[^5..];
        if (!int.TryParse(last5[..3], out var maj) || !int.TryParse(last5[3..], out var min)) return null;
        return $"{maj}.{min:D2}";
    }

    /// <summary>
    /// Compares two NVIDIA driver versions ("610.74"). Returns &gt;0 when <paramref name="a"/>
    /// is newer. String comparison gets this wrong — "610.9" sorts above "610.74" — and version
    /// comparison is the whole point of the check.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static (int Major, int Minor) Split(string v)
        {
            var p = (v ?? "").Split('.');
            int.TryParse(p.ElementAtOrDefault(0), out var maj);
            int.TryParse(p.ElementAtOrDefault(1), out var min);
            return (maj, min);
        }
        var (am, an) = Split(a);
        var (bm, bn) = Split(b);
        return am != bm ? am.CompareTo(bm) : an.CompareTo(bn);
    }
}
