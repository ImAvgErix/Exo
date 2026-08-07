using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Microsoft Update Catalog client — the fully automatic download path for AMD/Intel
/// platform (chipset) drivers when vendor CDNs block non-browser clients.
///
/// Flow: search → update IDs → DownloadDialog → https://catalog.s.download.windowsupdate.com/…
/// Only that Microsoft host is accepted.
/// </summary>
internal static class ChipsetMucClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        // Catalog search/DownloadDialog calls are small metadata requests. A
        // dead catalog endpoint must not hold a foreground optimizer for half
        // an hour; package downloads still inherit the caller's overall
        // prepare budget through its cancellation token.
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return c;
    }

    private static readonly string[] AllowedHosts =
    {
        "catalog.s.download.windowsupdate.com",
        "catalog.sf.dl.delivery.mp.microsoft.com",
        "download.windowsupdate.com",
        "dl.delivery.mp.microsoft.com",
    };

    internal sealed record MucUpdate(
        string UpdateId,
        string Title,
        string? Version);

    internal sealed record MucDownload(
        string UpdateId,
        string Title,
        string Url,
        string? FileName);

    public static bool IsAcceptableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        return AllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                     || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)
                                     || uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase)
                                     || uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Search the catalog and return ranked chipset/platform updates for a vendor.</summary>
    public static async Task<IReadOnlyList<MucUpdate>> SearchAsync(
        string vendor, CancellationToken ct = default)
    {
        var queries = vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                "AMD Chipset Software",
                "AMD System Driver",
                "AMD GPIO",
            }
            : new[]
            {
                "Intel Chipset Device Software",
                "Intel - System - Chipset",
                "Intel INF Utility",
                "Intel Serial IO",
            };

        var byId = new Dictionary<string, MucUpdate>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var u in await SearchOnceAsync(q, ct).ConfigureAwait(false))
                {
                    if (!IsRelevant(vendor, u.Title)) continue;
                    if (IsGpuDisplayDriver(u.Title)) continue;
                    if (!byId.ContainsKey(u.UpdateId))
                        byId[u.UpdateId] = u;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* next query */ }
        }

        // Prefer higher embedded versions, then shorter titles (less junk).
        return byId.Values
            .OrderByDescending(u => ChipsetDriverLogic.CompareVersions(u.Version, "0"))
            .ThenBy(u => u.Title.Length)
            .Take(12)
            .ToList();
    }

    private static bool IsGpuDisplayDriver(string title) =>
        Regex.IsMatch(title, @"(?i)Display Driver|Radeon|GeForce|Graphics Driver|Adrenalin");

    private static bool IsRelevant(string vendor, string title)
    {
        if (vendor.Equals("amd", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(title, @"(?i)AMD|Advanced Micro Devices")
                   && Regex.IsMatch(title, @"(?i)System Driver|GPIO|SMBus|SMBus|PCI|Chipset|PPM|PSP|USB|I2C|SFH");
        return Regex.IsMatch(title, @"(?i)Intel")
               && Regex.IsMatch(title, @"(?i)Chipset|INF|Serial IO|Management Engine|MEI|GPIO|SMBus|System");
    }

    private static async Task<IReadOnlyList<MucUpdate>> SearchOnceAsync(string query, CancellationToken ct)
    {
        var url = "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(query);
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var list = new List<MucUpdate>();

        // goToDetails("guid") near a title in the results table
        foreach (Match m in Regex.Matches(html,
                     @"goToDetails\(""([a-f0-9\-]{36})""\)",
                     RegexOptions.IgnoreCase))
        {
            var id = m.Groups[1].Value;
            // Title: look backward/forward in a window for a driver-looking string
            var start = Math.Max(0, m.Index - 800);
            var window = html.Substring(start, Math.Min(1600, html.Length - start));
            var titleMatch = Regex.Match(window,
                @"(?is)(?:<a[^>]*>\s*)?((?:AMD|Advanced Micro Devices|Intel)[^<]{8,160}?)(?:\s*</a>|<)",
                RegexOptions.IgnoreCase);
            var title = titleMatch.Success
                ? WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim()
                : id;
            title = Regex.Replace(title, @"\s+", " ");
            var ver = ExtractVersion(title);
            list.Add(new MucUpdate(id, title, ver));
        }

        // Alternate: id='guid_link' with title text
        foreach (Match m in Regex.Matches(html,
                     @"id=['""]([a-f0-9\-]{36})_link['""][^>]*>\s*([^<]+)</a>",
                     RegexOptions.IgnoreCase))
        {
            var id = m.Groups[1].Value;
            var title = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (list.Any(x => x.UpdateId.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new MucUpdate(id, title, ExtractVersion(title)));
        }

        return list;
    }

    private static string? ExtractVersion(string title)
    {
        var m = Regex.Match(title, @"(\d+\.\d+(?:\.\d+){1,3})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Resolve direct download URLs for a set of update IDs.</summary>
    public static async Task<IReadOnlyList<MucDownload>> ResolveDownloadsAsync(
        IEnumerable<MucUpdate> updates, CancellationToken ct = default)
    {
        var results = new List<MucDownload>();
        foreach (var u in updates.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var urls = await ResolveOneAsync(u.UpdateId, ct).ConfigureAwait(false);
                foreach (var url in urls)
                {
                    if (!IsAcceptableUrl(url)) continue;
                    var file = GuessFileName(url, u);
                    results.Add(new MucDownload(u.UpdateId, u.Title, url, file));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* next */ }
        }
        return results;
    }

    private static string GuessFileName(string url, MucUpdate u)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name) && name.Contains('.')) return name;
        }
        catch { }
        var ext = url.Contains(".cab", StringComparison.OrdinalIgnoreCase) ? ".cab"
            : url.Contains(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe"
            : url.Contains(".msu", StringComparison.OrdinalIgnoreCase) ? ".msu"
            : ".bin";
        return (u.UpdateId[..8]) + ext;
    }

    private static async Task<IReadOnlyList<string>> ResolveOneAsync(string updateId, CancellationToken ct)
    {
        // Classic DownloadDialog POST used by the catalog website.
        var payload =
            "updateIDs=[{%22size%22:0,%22languages%22:%22%22,%22uidInfo%22:%22" + updateId +
            "%22,%22updateID%22:%22" + updateId +
            "%22}]&updateIDsBlockedForImport=&wsusApiPresent=&contentImport=&sku=&size=&version=&uidInfo=" +
            updateId + "&updateID=" + updateId +
            "&updateInformationIsFullURl=&contentFileNames=";

        using var content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var resp = await Http.PostAsync(
            "https://www.catalog.update.microsoft.com/DownloadDialog.aspx", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var urls = new List<string>();
        foreach (Match m in Regex.Matches(body, @"https://[a-z0-9\.\-]+(?:\.windowsupdate\.com|\.microsoft\.com)/[^'""\s]+",
                     RegexOptions.IgnoreCase))
        {
            var u = m.Value.TrimEnd('\\', ';', ',', ')');
            if (!urls.Contains(u, StringComparer.OrdinalIgnoreCase))
                urls.Add(u);
        }
        // JS-escaped variants
        foreach (Match m in Regex.Matches(body, @"downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*'([^']+)'",
                     RegexOptions.IgnoreCase))
        {
            var u = m.Groups[1].Value.Replace("\\u0026", "&").Replace("\\/", "/");
            if (!urls.Contains(u, StringComparer.OrdinalIgnoreCase))
                urls.Add(u);
        }
        return urls;
    }

    public static async Task<(string? Path, string Message)> DownloadAsync(
        MucDownload item, string destDir, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var name = item.FileName ?? (item.UpdateId + ".cab");
        var dest = Path.Combine(destDir, name);
        if (File.Exists(dest) && new FileInfo(dest).Length > 10_000)
            return (dest, "Already cached.");

        progress?.Report($"Downloading {item.Title}…");
        using var resp = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (null, $"HTTP {(int)resp.StatusCode} for {name}");
        var partial = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(partial))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        var len = new FileInfo(partial).Length;
        if (len < 5_000)
        {
            try { File.Delete(partial); } catch { }
            return (null, $"Download too small ({len} bytes).");
        }
        File.Move(partial, dest, overwrite: true);
        return (dest, $"Downloaded {len / 1024} KB.");
    }
}
