using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Reads NVIDIA's GeForce Hotfix support article.
///
/// Hotfix drivers are <b>not in the driver feed at all</b> — confirmed empirically: a
/// <c>beta=1&amp;isWHQL=0</c> lookup returns <c>Success:"0"</c> with
/// <c>DriverDownloadIDNotFound</c>. NVIDIA says so in the article itself: "we only provide
/// NVIDIA Hotfix drivers through our NVIDIA Customer Care support site." So this parses a page
/// rather than an API, and is written to fail closed when the page changes shape.
///
/// Three facts from the real article drive the design:
///
/// <list type="bullet">
/// <item><b>They are beta.</b> NVIDIA's own words: "beta, optional and provided as-is… run
/// through a much abbreviated QA process. The safest option is to wait for the next WHQL
/// certified driver." Exo never prefers a hotfix by default.</item>
/// <item><b>They are withdrawn.</b> "at which time the Hotfix driver will be taken down" — a
/// cached hotfix URL goes dead, so it is re-read rather than remembered.</item>
/// <item><b>Their fixes are narrow and often hardware-specific.</b> 610.82 fixes an RTX 50
/// series stability bug and one game's DX12 stutter. On a 30-series card neither applies, and
/// offering it as an upgrade would trade a WHQL driver for a beta one in exchange for nothing.
/// That is why <see cref="HotfixRelease.RelevantTo"/> exists.</item>
/// </list>
/// </summary>
internal static class NvidiaHotfixLookup
{
    /// <summary>The support article. The ID rotates when a hotfix supersedes the last one.</summary>
    public const string ArticleUrl = "https://nvidia.custhelp.com/app/answers/detail/a_id/5870";

    internal sealed record HotfixFix(string Title, string Detail)
    {
        /// <summary>
        /// Series this fix names, e.g. "50" from "GeForce RTX 50 series". Empty when the fix is
        /// not hardware-specific, which means it may apply to anyone.
        /// </summary>
        public IReadOnlyList<string> Series
        {
            get
            {
                var found = new List<string>();
                foreach (Match m in Regex.Matches(Detail + " " + Title,
                             @"RTX\s*(\d0)\s*series", RegexOptions.IgnoreCase))
                    found.Add(m.Groups[1].Value);
                return found;
            }
        }
    }

    internal sealed record HotfixRelease(
        string Version,
        string BasedOnVersion,
        string DownloadUrl,
        IReadOnlyList<HotfixFix> Fixes)
    {
        /// <summary>
        /// Whether this hotfix is worth taking on a given GPU series.
        ///
        /// A hotfix whose every listed fix names a different series is not an upgrade for this
        /// machine — it is a beta driver in exchange for nothing. Fixes that name no series are
        /// treated as potentially relevant, because "fixed a DX12 stutter" with no hardware
        /// qualifier could apply to anyone.
        /// </summary>
        public bool RelevantTo(string? gpuSeries)
        {
            if (Fixes.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(gpuSeries)) return true;   // unknown GPU: do not filter
            return Fixes.Any(f => f.Series.Count == 0 || f.Series.Contains(gpuSeries));
        }

        /// <summary>
        /// Whether any fix names this GPU series explicitly. Stricter than
        /// <see cref="RelevantTo"/>: a fix with no hardware qualifier ("fixed a stutter in
        /// game X") *might* apply to anyone, which is enough to mention but not enough to move
        /// someone off a WHQL driver onto a beta one. Recommending a hotfix requires a fix that
        /// names the hardware in front of us.
        /// </summary>
        public bool NamesSeries(string? gpuSeries) =>
            !string.IsNullOrWhiteSpace(gpuSeries) && Fixes.Any(f => f.Series.Contains(gpuSeries));

        /// <summary>Fixes that actually apply here, for showing the user why (or why not).</summary>
        public IReadOnlyList<HotfixFix> FixesFor(string? gpuSeries) =>
            string.IsNullOrWhiteSpace(gpuSeries)
                ? Fixes
                : Fixes.Where(f => f.Series.Count == 0 || f.Series.Contains(gpuSeries)).ToList();
    }

    /// <summary>
    /// Parses the article body. Returns null when the download link or version cannot be found —
    /// a shape change must produce "no hotfix known", never a half-populated release that sends
    /// someone at a URL built from a guess.
    /// </summary>
    public static HotfixRelease? Parse(string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText)) return null;

        // The download link is the anchor of the whole parse. Its shape is distinctive and
        // differs from the WHQL feed's in three ways worth noting: a different host, an "hf"
        // suffix on the version directory, and ".hf.exe" on the filename.
        var url = Regex.Match(pageText,
            @"https://[^\s""'<>\)]*download\.nvidia\.com/Windows/[\d.]+hf/[^\s""'<>\)]*\.hf\.exe",
            RegexOptions.IgnoreCase);
        if (!url.Success) return null;

        // Version from the URL's own directory rather than the prose. The page repeats the
        // version in several places and translations reword the surrounding text; the path is
        // structural.
        var ver = Regex.Match(url.Value, @"/Windows/([\d.]+)hf/", RegexOptions.IgnoreCase);
        if (!ver.Success) return null;

        var basedOn = Regex.Match(pageText,
            @"based on .{0,40}?Game Ready Driver\s+([\d.]+)", RegexOptions.IgnoreCase);

        var fixes = new List<HotfixFix>();
        foreach (Match line in Regex.Matches(pageText, @"^\s*[\*\-]\s+(.+)$", RegexOptions.Multiline))
        {
            var text = line.Groups[1].Value.Trim();
            if (text.Length == 0) continue;
            // Entries read "[Game Name] description [bugid]".
            var title = Regex.Match(text, @"^\[([^\]]+)\]");
            fixes.Add(new HotfixFix(
                title.Success ? title.Groups[1].Value.Trim() : "General",
                text));
        }

        return new HotfixRelease(
            Version: ver.Groups[1].Value,
            BasedOnVersion: basedOn.Success ? basedOn.Groups[1].Value : "",
            DownloadUrl: url.Value,
            Fixes: fixes);
    }

    /// <summary>
    /// "NVIDIA GeForce RTX 3070" -> "30". Matches how the DRS profile packs are keyed, so the
    /// same series string drives both profile selection and hotfix relevance.
    /// </summary>
    public static string? SeriesOf(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return null;
        var m = Regex.Match(adapterName, @"\b(?:RTX|GTX)\s*([1-9])0\d{2}\b", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value + "0";
        // GTX 16xx has no RT cores and rides the 20-series branch, matching the pack layout.
        return Regex.IsMatch(adapterName, @"\b16\d{2}\b") ? "10" : null;
    }
}
