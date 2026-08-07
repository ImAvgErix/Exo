using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// AMD / Intel CPU chipset software — check, prepare (download or local drop + strip), install.
///
/// Same three-stage contract as <see cref="NvidiaDriverInstaller"/>:
/// check (no side effects) → prepare (extract + strip, install nothing) → execute (token + confirm).
///
/// Pure rules live in <see cref="ChipsetDriverLogic"/> so Contracts.Smoke can gate them.
/// </summary>
internal static class ChipsetDriverInstaller
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        AllowAutoRedirect = true,
    })
    {
        // A vendor landing page or stalled CDN must not hold the Driver Center
        // forever. The bounded prepare token below is the overall guard; this
        // is the per-request fallback guard.
        Timeout = TimeSpan.FromMinutes(2),
    };

    private static readonly TimeSpan CheckWindowsUpdateBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PrepareBudget = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClassicInstallBudget = TimeSpan.FromMinutes(20);

    static ChipsetDriverInstaller()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
    }

    internal enum Recommendation
    {
        UpToDate,
        UpgradeAvailable,
        PackageReady,
        Unknown,
        NotApplicable,
    }

    internal enum Source
    {
        None,
        CatalogDownload,
        LocalDrop,
        WindowsUpdate,
    }

    internal sealed record LocalState(
        HardwareInventory.CpuVendor CpuVendor,
        string CpuName,
        string? Socket,
        string? InstalledVersion,
        string? InstalledName,
        ChipsetDriverLogic.PackageSpec? Spec);

    internal sealed record InstallPlan(
        Recommendation Kind,
        Source PreferredSource,
        string Vendor,
        string Title,
        string? CurrentVersion,
        string? TargetVersion,
        string Headline,
        IReadOnlyList<string> Reasons,
        string? SupportUrl,
        string? DropFolder,
        string? LocalPackagePath,
        bool CanPrepare,
        bool CanStrip);

    internal sealed record PreparedInstall(
        string Vendor,
        string Version,
        string ExtractDir,
        string SetupPath,
        string Args,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> Kept,
        string Token,
        Source Source);

    private static string CatalogPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "chipset-catalog.json");
            if (File.Exists(path)) return path;
            var alt = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", "chipset-catalog.json"));
            return File.Exists(alt) ? alt : path;
        }
    }

    internal static string DropFolder =>
        Path.Combine(PathHelper.AppDataDir, "chipset-packages");

    internal static string WorkDir =>
        Path.Combine(PathHelper.AppDataDir, "chipset-work");

    public static int CompareVersions(string? a, string? b) => ChipsetDriverLogic.CompareVersions(a, b);

    public static string? InferSocket(string cpuName, HardwareInventory.CpuVendor vendor) =>
        ChipsetDriverLogic.InferSocket(cpuName, vendor.ToString());

    public static IReadOnlyList<ChipsetDriverLogic.PackageSpec> LoadCatalog() =>
        ChipsetDriverLogic.LoadCatalog(CatalogPath);

    public static ChipsetDriverLogic.StripResult StripPackage(string extractDir, ChipsetDriverLogic.PackageSpec spec) =>
        ChipsetDriverLogic.StripPackage(extractDir, spec);

    // ── Detect ────────────────────────────────────────────────────────────────────────────

    public static LocalState ReadLocal()
    {
        var inv = HardwareInventory.Read();
        var cpu = inv.Cpu;
        var vendor = cpu?.Vendor ?? HardwareInventory.CpuVendor.Unknown;
        var name = cpu?.Name ?? "";
        var socket = InferSocket(name, vendor);
        var (installedName, installedVer) = FindInstalledChipset(vendor);
        var catalog = LoadCatalog();
        var spec = catalog.FirstOrDefault(p =>
            string.Equals(p.Vendor, vendor.ToString(), StringComparison.OrdinalIgnoreCase));
        return new LocalState(vendor, name, socket, installedVer, installedName, spec);
    }

    public static (string? Name, string? Version) FindInstalledChipset(HardwareInventory.CpuVendor vendor)
    {
        // Prefer stamped ProductVersion from a successful/partial package install.
        if (vendor == HardwareInventory.CpuVendor.Amd)
        {
            var regVer = ReadAmdProductVersion();
            if (!string.IsNullOrWhiteSpace(regVer))
                return ("AMD Chipset Software", regVer);
        }

        string[] nameMatch = vendor switch
        {
            HardwareInventory.CpuVendor.Amd => new[]
            {
                "AMD Chipset", "AMD PPM", "Chipset Software", "AMD GPIO", "Ryzen Chipset",
                "Chipset IO Drivers", "AMD_Chipset", "Chipset Drivers"
            },
            HardwareInventory.CpuVendor.Intel => new[]
            {
                "Intel(R) Chipset", "Intel Chipset", "Chipset Device Software", "INF Utility"
            },
            _ => Array.Empty<string>()
        };
        if (nameMatch.Length == 0) return (null, null);

        string? bestName = null;
        string? bestVer = null;
        foreach (var root in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(sub);
                        if (app is null) continue;
                        var name = app.GetValue("DisplayName")?.ToString() ?? "";
                        if (name.Length == 0) continue;
                        if (!nameMatch.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var ver = app.GetValue("DisplayVersion")?.ToString()?.Trim();
                        var score = name.Contains("Chipset Software", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("Chipset Device", StringComparison.OrdinalIgnoreCase)
                            ? 2
                            : 1;
                        var prevScore = bestName is not null &&
                                        (bestName.Contains("Chipset Software", StringComparison.OrdinalIgnoreCase)
                                         || bestName.Contains("Chipset Device", StringComparison.OrdinalIgnoreCase))
                            ? 2
                            : bestName is null ? 0 : 1;
                        if (score > prevScore || (score == prevScore && CompareVersions(ver, bestVer) > 0))
                        {
                            bestName = name;
                            bestVer = ver;
                        }
                    }
                    catch { /* next */ }
                }
            }
            catch { /* next hive */ }
        }

        if (vendor == HardwareInventory.CpuVendor.Amd)
        {
            // Prefer the real package revision (8.07.x) from install artifacts / MSI when
            // Add/Remove has no "AMD Chipset Software" entry (common after quiet installs).
            if (bestName is null || string.IsNullOrWhiteSpace(bestVer))
            {
                var pkg = TryReadAmdChipsetPackageVersion();
                if (!string.IsNullOrWhiteSpace(pkg))
                    return ("AMD Chipset Software", pkg);
            }

            // Fallback: IO drivers registry means something installed, but without a package
            // revision we only report presence via driver hint — currency is decided later.
            if (bestName is null)
            {
                foreach (var path in new[]
                         {
                             @"SOFTWARE\WOW6432Node\AMD\AMD_Chipset_IODrivers",
                             @"SOFTWARE\AMD\AMD_Chipset_IODrivers",
                         })
                {
                    try
                    {
                        using var io = Registry.LocalMachine.OpenSubKey(path);
                        if (io is null) continue;
                        var deployed = io.GetValue("DeployedFeatures")?.ToString()
                                       ?? io.GetValue("ADDLOCAL")?.ToString()
                                       ?? "";
                        if (deployed.Length == 0) continue;
                        if (!deployed.Contains("Drivers", StringComparison.OrdinalIgnoreCase)
                            && !deployed.Contains("PSP", StringComparison.OrdinalIgnoreCase)
                            && !deployed.Contains("SMBUS", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var ver = ReadAmdPlatformDriverVersionHint();
                        return ("AMD Chipset IO Drivers", ver);
                    }
                    catch { /* next */ }
                }
            }
        }

        return (bestName, bestVer);
    }

    /// <summary>
    /// Package revision like 8.07.16.1035 from MSI product props or the last install log.
    /// Never confuses INF driver versions (5.x PSP) with the chipset software package number.
    /// </summary>
    private static string? TryReadAmdChipsetPackageVersion()
    {
        // 1) MSI product registration (when install completed cleanly).
        foreach (var root in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(sub);
                        if (app is null) continue;
                        var name = app.GetValue("DisplayName")?.ToString() ?? "";
                        var pub = app.GetValue("Publisher")?.ToString() ?? "";
                        var ver = app.GetValue("DisplayVersion")?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(ver)) continue;
                        var looksAmd = pub.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase)
                                       || pub.Contains("AMD", StringComparison.OrdinalIgnoreCase);
                        var looksChip = name.Contains("Chipset", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains("AMD_Chipset", StringComparison.OrdinalIgnoreCase);
                        // Package revisions are 7.x / 8.x with 3–4 segments, not 5.12 driver style.
                        if (looksAmd && looksChip && IsChipsetPackageVersion(ver))
                            return ver;
                    }
                    catch { /* next */ }
                }
            }
            catch { /* next hive */ }
        }

        // 2) Install log left by AMD / Exo chipset installer (even when Add/Remove is empty).
        foreach (var log in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "AMD", "Chipset_Software", "Logs", "AMD_Chipset_Software_Install.log"),
                     @"C:\AMD\Chipset_Software\Logs\AMD_Chipset_Software_Install.log",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Exo", "chipset-work"),
                 })
        {
            try
            {
                if (Directory.Exists(log))
                {
                    foreach (var f in Directory.EnumerateFiles(log, "*Install*.log", SearchOption.AllDirectories)
                                 .OrderByDescending(File.GetLastWriteTimeUtc)
                                 .Take(5))
                    {
                        var v = ParseAmdChipsetVersionFromLog(f);
                        if (v is not null) return v;
                    }
                    continue;
                }
                if (File.Exists(log))
                {
                    var v = ParseAmdChipsetVersionFromLog(log);
                    if (v is not null) return v;
                }
            }
            catch { /* next */ }
        }

        // 3) Extracted package folder name under LocalAppData\Exo\chipset-work or Roaming\AMD.
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "chipset-work"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMD", "Chipset_Software"),
                 })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "AMD_Chipset*.exe", SearchOption.AllDirectories)
                             .Concat(Directory.EnumerateFiles(root, "*Chipset*Software*.exe", SearchOption.AllDirectories)))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        Path.GetFileNameWithoutExtension(f),
                        @"(\d+\.\d+\.\d+\.\d+)");
                    if (m.Success && IsChipsetPackageVersion(m.Groups[1].Value))
                        return m.Groups[1].Value;
                }
            }
            catch { /* next */ }
        }

        return null;
    }

    private static string? ParseAmdChipsetVersionFromLog(string path)
    {
        try
        {
            // Prefer ProductVersion= / MaxVersion: 8.07.16.1035 from InstallShield upgrade table.
            using var sr = new StreamReader(path);
            string? line;
            string? found = null;
            var n = 0;
            while ((line = sr.ReadLine()) is not null && n++ < 4000)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"MaxVersion:\s*(\d+\.\d+\.\d+\.\d+)");
                if (m.Success && IsChipsetPackageVersion(m.Groups[1].Value))
                    found = m.Groups[1].Value;
                m = System.Text.RegularExpressions.Regex.Match(
                    line, @"ProductVersion\s*[:=]\s*(\d+\.\d+\.\d+\.\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && IsChipsetPackageVersion(m.Groups[1].Value))
                    return m.Groups[1].Value;
                m = System.Text.RegularExpressions.Regex.Match(
                    line, @"AMD_Chipset_Software[_\s-]*(\d+\.\d+\.\d+\.\d+)");
                if (m.Success && IsChipsetPackageVersion(m.Groups[1].Value))
                    return m.Groups[1].Value;
            }
            return found;
        }
        catch { return null; }
    }

    /// <summary>Chipset *package* revisions are 7.x / 8.x (and similar), not 5.x INF drivers.</summary>
    private static bool IsChipsetPackageVersion(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return false;
        var parts = ver.Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var major)) return false;
        // Package line is currently 7.x / 8.x. Driver INF revs are often 1.x–5.x.
        return major is >= 6 and <= 20;
    }

    /// <summary>
    /// Best-effort platform driver version from live AMD devices (PSP / SMBus / PCI).
    /// Package DisplayVersion (e.g. 7.01.x) is a different number space — do not mix them.
    /// </summary>
    private static string? ReadAmdPlatformDriverVersionHint()
    {
        try
        {
            // Prefer PSP / SMBus / PCI under Enum\PCI VEN_1022.
            using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci is null) return null;
            string? best = null;
            foreach (var id in pci.GetSubKeyNames())
            {
                if (!id.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Contains("CC_03", StringComparison.OrdinalIgnoreCase)) continue; // GPU
                try
                {
                    using var devClass = pci.OpenSubKey(id);
                    if (devClass is null) continue;
                    foreach (var inst in devClass.GetSubKeyNames())
                    {
                        using var instKey = devClass.OpenSubKey(inst);
                        var desc = instKey?.GetValue("DeviceDesc")?.ToString() ?? "";
                        using var driver = instKey?.OpenSubKey("Device Parameters")
                                           ?? instKey;
                        // Driver version lives under the class driver key via Driver value → Control\Class.
                        var driverKey = instKey?.GetValue("Driver")?.ToString();
                        if (string.IsNullOrWhiteSpace(driverKey)) continue;
                        using var classDriver = Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                        var ver = classDriver?.GetValue("DriverVersion")?.ToString()?.Trim();
                        var provider = classDriver?.GetValue("ProviderName")?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(ver)) continue;
                        if (!provider.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                            && !provider.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Prefer PSP-like or higher version string.
                        if (best is null || CompareVersions(ver, best) > 0)
                            best = ver;
                        _ = desc;
                    }
                }
                catch { /* next device */ }
            }
            return best;
        }
        catch { return null; }
    }

    // ── Plan / Check ──────────────────────────────────────────────────────────────────────

    public static InstallPlan Plan(LocalState local, string? localPackagePath)
    {
        var reasons = new List<string>();
        if (local.CpuVendor is HardwareInventory.CpuVendor.Unknown)
        {
            return new InstallPlan(Recommendation.NotApplicable, Source.None, "unknown", "Chipset",
                null, null, "No AMD or Intel CPU detected — chipset updates are not offered.",
                reasons, null, DropFolder, null, false, false);
        }

        var spec = local.Spec;
        if (spec is null)
        {
            reasons.Add("No catalog entry for this CPU vendor.");
            return new InstallPlan(Recommendation.Unknown, Source.None, local.CpuVendor.ToString().ToLowerInvariant(),
                "Chipset software", local.InstalledVersion, null,
                "Could not map this CPU to a chipset package catalog.",
                reasons, null, DropFolder, null, false, false);
        }

        var vendor = spec.Vendor;
        var title = spec.Title;
        var current = local.InstalledVersion;
        // Live newest (MUC / AMD pages) wins over the offline catalog floor.
        var target = ChipsetLatestLookup.ResolveLatest(vendor, spec.TargetVersion) ?? spec.TargetVersion;
        var drop = DropFolder;
        Directory.CreateDirectory(drop);

        // Always fully automatic: Prepare pulls Microsoft Update Catalog + Windows Update.
        // Local drop is only a silent bonus if a file happens to already be there.
        var foundDrop = localPackagePath ?? FindDropPackage(spec);
        if (!string.IsNullOrWhiteSpace(foundDrop) && File.Exists(foundDrop))
        {
            reasons.Add($"Cached package: {Path.GetFileName(foundDrop)}.");
            reasons.Add("Prepare will strip optional junk, then install on your yes — no manual download.");
            return new InstallPlan(Recommendation.PackageReady, Source.LocalDrop, vendor, title,
                current, target,
                $"{title} is ready to prepare automatically.",
                reasons, spec.SupportUrl, drop, foundDrop, true, true);
        }

        if (!string.IsNullOrWhiteSpace(current) && CompareVersions(current, target) >= 0)
        {
            // Floor met — do not nag every session. CheckAsync may still promote to
            // UpgradeAvailable when Windows Update actually has pending packages.
            reasons.Add($"{current} meets catalog target {target}.");
            return new InstallPlan(Recommendation.UpToDate, Source.None, vendor, title,
                current, target,
                $"{title} looks current ({current}).",
                reasons, spec.SupportUrl, drop, null, false, true);
        }

        if (string.IsNullOrWhiteSpace(current))
            reasons.Add($"No {title} entry detected — inbox drivers only, or not registered.");
        else
            reasons.Add($"Installed {current}; catalog target {target}.");
        reasons.Add("Prepare downloads automatically from Microsoft Update Catalog (and Windows Update), strips junk, then waits for install consent. No manual download.");
        return new InstallPlan(Recommendation.UpgradeAvailable, Source.WindowsUpdate, vendor, title,
            current, target,
            string.IsNullOrWhiteSpace(current)
                ? $"{title} can be installed automatically (catalog target {target})."
                : $"{title} {target} available — you are on {current}. I will download and strip it myself.",
            reasons, spec.SupportUrl, drop, null, true, true);
    }

    public static async Task<InstallPlan> CheckAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var local = ReadLocal();
        var drop = local.Spec is null ? null : FindDropPackage(local.Spec);
        var plan = Plan(local, drop);

        // Always probe WU: even "up to date" vs catalog may still have pending WU packages.
        try
        {
            using var wuBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wuBudget.CancelAfter(CheckWindowsUpdateBudget);
            var wuTask = ChipsetWindowsUpdate.SearchAsync(plan.Vendor, wuBudget.Token);
            var timeoutTask = Task.Delay(CheckWindowsUpdateBudget, ct);
            var completed = await Task.WhenAny(wuTask, timeoutTask).ConfigureAwait(false);
            if (completed != wuTask)
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                wuBudget.Cancel();
                _ = wuTask.ContinueWith(t => _ = t.Exception,
                    TaskScheduler.Default);
                var timedOutReasons = plan.Reasons.ToList();
                timedOutReasons.Add("Windows Update check timed out; the catalog plan is being retained. You can still prepare it.");
                plan = plan with { Reasons = timedOutReasons, CanPrepare = plan.CanPrepare };
                return plan;
            }

            var (wu, wuMsg) = await wuTask.ConfigureAwait(false);
            if (wu.Count > 0)
            {
                var reasons = plan.Reasons.ToList();
                reasons.Insert(0, $"Windows Update has {wu.Count} platform driver package(s) ready to install automatically.");
                plan = plan with
                {
                    Kind = Recommendation.UpgradeAvailable,
                    PreferredSource = Source.WindowsUpdate,
                    Reasons = reasons,
                    Headline = $"{plan.Title}: {wu.Count} update(s) ready via Windows Update — fully automatic.",
                    CanPrepare = true,
                };
            }
            else if (plan.Kind is Recommendation.UpgradeAvailable or Recommendation.PackageReady)
            {
                var reasons = plan.Reasons.ToList();
                reasons.Add(wuMsg);
                // MUC still auto-downloads even when WU is empty — keep CanPrepare true.
                plan = plan with { Reasons = reasons, CanPrepare = true };
            }
        }
        catch { /* headline stays catalog-based */ }

        return plan;
    }

    public static string? FindDropPackage(ChipsetDriverLogic.PackageSpec spec)
    {
        try
        {
            Directory.CreateDirectory(DropFolder);
            foreach (var file in Directory.EnumerateFiles(DropFolder, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                if (spec.FileNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    return file;
            }
            var exes = Directory.EnumerateFiles(DropFolder, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            if (exes.Count == 1) return exes[0];
        }
        catch { }
        return null;
    }

    // ── Prepare ───────────────────────────────────────────────────────────────────────────

    public static async Task<(PreparedInstall? Prepared, string Message)> PrepareAsync(
        InstallPlan plan,
        bool allowSevenZipInstall,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (plan.Kind is Recommendation.NotApplicable or Recommendation.Unknown)
            return (null, plan.Headline);

        var local = ReadLocal();
        var spec = local.Spec;
        if (spec is null) return (null, "No catalog package for this CPU.");

        Directory.CreateDirectory(WorkDir);
        using var prepareBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prepareBudget.CancelAfter(PrepareBudget);
        var runCt = prepareBudget.Token;
        var extractDir = Path.Combine(WorkDir, $"{spec.Vendor}-auto-{DateTime.UtcNow:yyyyMMdd}");
        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(extractDir);

        var removed = new List<string>();
        var kept = new List<string>();
        var source = Source.WindowsUpdate;
        string? setupPath = null;
        var installArgs = "pnputil-inf"; // special marker: install via pnputil on INFs
        var downloaded = 0;

        try
        {
            // ── 1) Microsoft Update Catalog (automatic, no vendor CDN) ─────────────────
            progress?.Report("Searching Microsoft Update Catalog (up to 2 minutes)…");
            var mucUpdates = await ChipsetMucClient.SearchAsync(spec.Vendor, runCt).ConfigureAwait(false);
            progress?.Report(mucUpdates.Count > 0
                ? $"Catalog found {mucUpdates.Count} platform package(s). Resolving downloads…"
                : "Catalog search returned no packages — trying other sources…");

            if (mucUpdates.Count > 0)
            {
                var dls = await ChipsetMucClient.ResolveDownloadsAsync(mucUpdates.Take(3), runCt).ConfigureAwait(false);
                var cacheDir = Path.Combine(WorkDir, "muc-cache");
                Directory.CreateDirectory(cacheDir);
                foreach (var d in dls)
                {
                    runCt.ThrowIfCancellationRequested();
                    var (path, dlMsg) = await ChipsetMucClient.DownloadAsync(d, cacheDir, progress, runCt).ConfigureAwait(false);
                    progress?.Report(dlMsg);
                    if (path is null) continue;
                    downloaded++;
                    await ExpandPackageAsync(path, extractDir, allowSevenZipInstall, progress, runCt).ConfigureAwait(false);
                }
                if (downloaded > 0) source = Source.CatalogDownload;
            }

            // ── 2) Local/cache package if present ──────────────────────────────────────
            var packagePath = plan.LocalPackagePath ?? FindDropPackage(spec);
            if (packagePath is not null && File.Exists(packagePath))
            {
                progress?.Report($"Also unpacking cached {Path.GetFileName(packagePath)}…");
                await ExpandPackageAsync(packagePath, extractDir, allowSevenZipInstall, progress, runCt).ConfigureAwait(false);
                downloaded++;
                source = Source.LocalDrop;
            }

            // ── 3) Vendor URL only when not a landing page ─────────────────────────────
            if (downloaded == 0 && !string.IsNullOrWhiteSpace(spec.DownloadUrl) && !spec.DownloadIsLandingPage)
            {
                progress?.Report($"Trying vendor download for {spec.Title}…");
                var dl = await TryDownloadAsync(spec, progress, runCt).ConfigureAwait(false);
                if (dl is not null)
                {
                    await ExpandPackageAsync(dl, extractDir, allowSevenZipInstall, progress, runCt).ConfigureAwait(false);
                    downloaded++;
                    source = Source.CatalogDownload;
                }
            }

            // Strip optional apps if any full package tree landed
            progress?.Report("Stripping optional junk…");
            var strip = StripPackage(extractDir, spec);
            removed.AddRange(strip.Removed);
            kept.AddRange(strip.Kept);

            var setup = FindSetup(extractDir, spec);
            var infCount = 0;
            try
            {
                infCount = Directory.EnumerateFiles(extractDir, "*.inf", SearchOption.AllDirectories).Count();
            }
            catch { }

            if (setup is not null)
            {
                setupPath = setup;
                installArgs = string.Join(" ", spec.InstallSilentArgs);
                source = Source.LocalDrop;
            }
            else if (infCount > 0)
            {
                // INF-only tree from MUC cabs — install with pnputil
                setupPath = extractDir; // directory marker
                installArgs = "pnputil-inf";
            }
            else
            {
                // Still allow pure WU install with no extracted files — token covers WU path.
                progress?.Report("No extracted package — install will use Windows Update online automatically.");
                setupPath = "windows-update";
                installArgs = "windows-update";
                source = Source.WindowsUpdate;
            }

            var token = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(setupPath + spec.TargetVersion + installArgs + spec.Vendor + downloaded)))[..16];

            var msg = setupPath == "windows-update"
                ? $"Ready to auto-install {spec.Title} via Windows Update. Nothing installed yet."
                : $"Ready to auto-install {spec.Title} ({downloaded} package(s), {infCount} INF(s)). Nothing installed yet.";

            return (new PreparedInstall(
                    spec.Vendor, spec.TargetVersion, extractDir, setupPath, installArgs,
                    removed, kept, token, source),
                msg);
        }
        catch (OperationCanceledException)
        {
            return (null, ct.IsCancellationRequested
                ? "Stopped — nothing was installed."
                : "Chipset preparation timed out after 2 minutes. Nothing was installed; try again or use a cached package.");
        }
        catch (Exception ex) { return (null, $"Preparation failed: {ex.Message}"); }
    }

    private static async Task ExpandPackageAsync(
        string packagePath,
        string extractDir,
        bool allowSevenZipInstall,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(extractDir);
        var ext = Path.GetExtension(packagePath).ToLowerInvariant();

        if (ext is ".cab" or ".msu")
        {
            // expand.exe is built into Windows
            var dest = Path.Combine(extractDir, Path.GetFileNameWithoutExtension(packagePath));
            Directory.CreateDirectory(dest);
            progress?.Report($"Expanding {Path.GetFileName(packagePath)}…");
            var exp = await RunProcessAsync(
                "expand.exe",
                $"\"{packagePath}\" -F:* \"{dest}\"",
                TimeSpan.FromSeconds(90),
                ct).ConfigureAwait(false);
            if (!exp.Ok)
                progress?.Report($"expand: {exp.Message}");
            return;
        }

        var sevenZip = NvidiaDriverInstaller.FindSevenZip();
        if (sevenZip is null)
        {
            var decide = NvidiaDriverInstaller.DecideUnpacker(null, NvidiaDriverInstaller.FindWinget(), allowSevenZipInstall);
            if (decide.NeedsInstall)
            {
                progress?.Report("Installing 7-Zip through winget…");
                var (path, _) = await NvidiaDriverInstaller.InstallSevenZipAsync(progress, ct).ConfigureAwait(false);
                sevenZip = path;
            }
            else
                sevenZip = NvidiaDriverInstaller.FindSevenZip();
        }
        if (sevenZip is null)
        {
            // Try expand anyway
            var dest = Path.Combine(extractDir, "pkg");
            Directory.CreateDirectory(dest);
            await RunProcessAsync(
                "expand.exe",
                $"\"{packagePath}\" -F:* \"{dest}\"",
                TimeSpan.FromSeconds(90),
                ct).ConfigureAwait(false);
            return;
        }

        progress?.Report($"Unpacking {Path.GetFileName(packagePath)}…");
        await RunProcessAsync(
            sevenZip,
            $"x \"{packagePath}\" -o\"{extractDir}\" -y",
            TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);
    }

    private static async Task<string?> TryDownloadAsync(
        ChipsetDriverLogic.PackageSpec spec, IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.DownloadUrl) || spec.DownloadIsLandingPage) return null;
        try
        {
            Directory.CreateDirectory(DropFolder);
            var dest = Path.Combine(DropFolder, $"{spec.Id}-{spec.TargetVersion}.exe");
            if (File.Exists(dest) && new FileInfo(dest).Length > 1_000_000) return dest;

            using var req = new HttpRequestMessage(HttpMethod.Get, spec.DownloadUrl);
            req.Headers.Referrer = new Uri(spec.SupportUrl.Length > 0 ? spec.SupportUrl : "https://www.amd.com/");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                progress?.Report($"Vendor download returned {(int)resp.StatusCode}.");
                return null;
            }
            var partial = dest + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(partial))
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            var len = new FileInfo(partial).Length;
            if (len < 500_000)
            {
                try { File.Delete(partial); } catch { }
                return null;
            }
            File.Move(partial, dest, overwrite: true);
            return dest;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report($"Download failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindSetup(string extractDir, ChipsetDriverLogic.PackageSpec spec)
    {
        string[] preferred = spec.Vendor.Equals("intel", StringComparison.OrdinalIgnoreCase)
            ? new[] { "SetupChipset.exe", "setup.exe", "Setup.exe" }
            : new[] { "Setup.exe", "setup.exe", "AMD_Chipset_Software.exe", "InstallManager.exe" };

        foreach (var name in preferred)
        {
            var hit = Directory.EnumerateFiles(extractDir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit;
        }
        return Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                var n = Path.GetFileName(f);
                return n.Contains("setup", StringComparison.OrdinalIgnoreCase)
                       && !n.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
            });
    }

    // ── Execute ───────────────────────────────────────────────────────────────────────────

    public static async Task<(bool Ok, string Message)> ExecuteAsync(
        PreparedInstall prepared,
        string token,
        bool confirmed,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!confirmed) return (false, "Install was not confirmed.");
        if (!string.Equals(token, prepared.Token, StringComparison.OrdinalIgnoreCase))
            return (false, "Install token does not match the prepared package — run Prepare again.");

        // ── Windows Update full-auto path ─────────────────────────────────────────────
        if (prepared.Args == "windows-update" || prepared.SetupPath == "windows-update")
        {
            var (ok, msg, n) = await ChipsetWindowsUpdate
                .InstallPendingAsync(prepared.Vendor, progress, ct).ConfigureAwait(false);
            return (ok, n > 0 ? msg : msg);
        }

        // ── INF tree via pnputil (MUC cabs) ────────────────────────────────────────────
        if (prepared.Args == "pnputil-inf" || Directory.Exists(prepared.SetupPath))
        {
            var root = Directory.Exists(prepared.SetupPath) ? prepared.SetupPath : prepared.ExtractDir;
            if (!Directory.Exists(root))
                return (false, "Prepared driver folder is missing — run Prepare again.");

            progress?.Report("Installing platform INFs with pnputil (elevated)…");
            var rootEsc = root.Replace("'", "''");
            var ps = $@"
$ErrorActionPreference = 'Continue'
$root = '{rootEsc}'
$infs = Get-ChildItem -LiteralPath $root -Filter *.inf -Recurse -ErrorAction SilentlyContinue
if (-not $infs -or $infs.Count -eq 0) {{ Write-Output 'NO_INF'; exit 2 }}
$ok = 0; $fail = 0
foreach ($inf in $infs) {{
  $p = Start-Process -FilePath pnputil.exe -ArgumentList @('/add-driver', $inf.FullName, '/install') -Wait -PassThru -WindowStyle Hidden
  if ($p.ExitCode -eq 0) {{ $ok++ }} else {{ $fail++ }}
}}
Write-Output (""INF_OK=$ok INF_FAIL=$fail"")
exit $(if ($ok -gt 0) {{ 0 }} else {{ 1 }})
";
            var temp = Path.Combine(Path.GetTempPath(), $"exo-chipset-pnputil-{Guid.NewGuid():N}.ps1");
            try
            {
                await File.WriteAllTextAsync(temp, ps, ct).ConfigureAwait(false);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using var p = Process.Start(psi);
                if (p is null) return (false, "Could not elevate pnputil install.");
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ClassicInstallBudget);
                try
                {
                    await p.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                    return (false, "Stopped — no further chipset packages were installed.");
                }
                catch (OperationCanceledException)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                    return (false, $"Chipset INF installation timed out after {ClassicInstallBudget.TotalMinutes:0} minutes.");
                }
                if (p.ExitCode == 0)
                    return (true, "Platform drivers installed automatically via pnputil. Reboot if devices still need rebinding.");
                // Fallback: WU install
                progress?.Report("pnputil had issues — falling back to Windows Update install…");
                var wuFallback = await ChipsetWindowsUpdate
                    .InstallPendingAsync(prepared.Vendor, progress, ct).ConfigureAwait(false);
                return (wuFallback.Ok, wuFallback.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (false, "Stopped — no further chipset packages were installed.");
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                    return (false, "Administrator approval was declined — nothing was installed.");
                // Last resort WU
                progress?.Report("Falling back to Windows Update…");
                var wu = await ChipsetWindowsUpdate.InstallPendingAsync(prepared.Vendor, progress, ct).ConfigureAwait(false);
                if (wu.Ok) return (true, wu.Message);
                return (false, "Install failed: " + ex.Message);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        // ── Classic Setup.exe silent ──────────────────────────────────────────────────
        if (!File.Exists(prepared.SetupPath))
            return (false, "Prepared setup.exe is missing — run Prepare again.");

        progress?.Report($"Installing {prepared.Vendor} chipset {prepared.Version} (elevated, silent)…");
        var args = prepared.Args;
        var setupPs = $@"
$p = Start-Process -FilePath '{prepared.SetupPath.Replace("'", "''")}' -ArgumentList '{args.Replace("'", "''")}' -Verb RunAs -Wait -PassThru
exit $p.ExitCode
";
        var setupTemp = Path.Combine(Path.GetTempPath(), $"exo-chipset-install-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(setupTemp, setupPs, ct).ConfigureAwait(false);
            var run = await RunProcessAsync(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{setupTemp}\"",
                ClassicInstallBudget,
                ct).ConfigureAwait(false);

            var after = FindInstalledChipset(
                prepared.Vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
                    ? HardwareInventory.CpuVendor.Amd
                    : HardwareInventory.CpuVendor.Intel);
            var verOk = after.Version is not null &&
                        CompareVersions(after.Version, prepared.Version) >= 0;

            if (verOk)
                return (true,
                    $"Chipset software installed ({after.Name} {after.Version}). A reboot is often required before power/USB devices rebind.");
            if (run.Ok)
                return (false,
                    "Installer exited successfully, but Exo could not verify the chipset version. Nothing is marked complete; check again before retrying.");
            // Still try WU
            progress?.Report("Setup reported failure — trying Windows Update…");
            var wu = await ChipsetWindowsUpdate.InstallPendingAsync(prepared.Vendor, progress, ct).ConfigureAwait(false);
            if (wu.Ok) return (true, wu.Message);
            return (false, $"Installer failed: {run.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "Stopped — chipset installer cancelled; no further packages were installed.");
        }
        catch (Exception ex)
        {
            return (false, $"Install failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(setupTemp); } catch { }
        }
    }

    private static async Task<(bool Ok, string Message)> RunProcessAsync(
        string exe,
        string args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process = Process.Start(psi);
            if (process is null) return (false, "Could not start process.");

            // Read both pipes concurrently. Reading one to completion before the
            // other can deadlock a vendor setup that fills the untouched pipe.
            stdoutTask = process.StandardOutput.ReadToEndAsync();
            stderrTask = process.StandardError.ReadToEndAsync();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillProcessTree(process);
                return (false, "Stopped — chipset installer cancelled; no further packages were installed.");
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                return (false, $"Chipset installer timed out after {timeout.TotalMinutes:0} minutes.");
            }

            var output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var stdout = output[0];
            var stderr = output[1];
            var ok = process.ExitCode is 0 or 3010;
            var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (msg.Length > 400) msg = msg[..400];
            return (ok, string.IsNullOrWhiteSpace(msg) ? $"exit {process.ExitCode}" : msg.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (process is not null) KillProcessTree(process);
            return (false, "Stopped — chipset installer cancelled; no further packages were installed.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { process?.Dispose(); } catch { }
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public static IReadOnlyList<string> DescribePlan(PreparedInstall p) =>
        new[]
        {
            $"Vendor: {p.Vendor}",
            $"Version: {p.Version}",
            $"Setup: {p.SetupPath}",
            $"Args: {p.Args}",
            $"Stripped: {(p.Removed.Count == 0 ? "(nothing)" : string.Join(", ", p.Removed.Take(12)))}",
        };

    // ── NVCleanstall-style one-shot AMD chipset install (silent, no UI clicks) ─────────

    /// <summary>
    /// Fully automatic AMD chipset clean install used by the AMD module Apply path:
    /// resolve package (cache / drop / MUC) → unpack → strip junk → install core W11 INFs
    /// with pnputil + silent MSI → re-enable disabled AMD devices → verify health.
    /// No interactive installer UI. Requires the process (or elevates once) as admin.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RunCleanAmdInstallAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        void Report(string m) => progress?.Report(m);

        var local = ReadLocal();
        if (local.CpuVendor != HardwareInventory.CpuVendor.Amd)
            return (false, "No AMD CPU — chipset clean install is not applicable.");

        var floor = local.Spec?.TargetVersion ?? "8.07.16.1035";
        var target = ChipsetLatestLookup.ResolveLatest("amd", floor) ?? floor;
        Report($"Target package: AMD Chipset Software {target}");

        // Prefer already-downloaded package matching target, else any drop package.
        string? package = null;
        try
        {
            Directory.CreateDirectory(DropFolder);
            var prefer = Path.Combine(DropFolder, $"amd-chipset-{target}.exe");
            if (File.Exists(prefer) && new FileInfo(prefer).Length > 1_000_000)
                package = prefer;
            else
                package = FindDropPackage(local.Spec!)
                          ?? Directory.EnumerateFiles(DropFolder, "amd-chipset*.exe")
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();
        }
        catch { }

        if (package is null || !File.Exists(package))
        {
            Report("No cached package — preparing from Microsoft Update Catalog…");
            var plan = await CheckAsync(ct).ConfigureAwait(false);
            var (prepared, prepMsg) = await PrepareAsync(plan, allowSevenZipInstall: true, progress, ct)
                .ConfigureAwait(false);
            if (prepared is null)
                return (false, "Could not obtain an AMD chipset package: " + prepMsg);

            // Prefer INF tree from prepare
            return await InstallCoreFromExtractAsync(prepared.ExtractDir, prepared.Version, progress, ct)
                .ConfigureAwait(false);
        }

        Report($"Using package {Path.GetFileName(package)} ({new FileInfo(package).Length / 1_000_000} MB)");
        var extractRoot = Path.Combine(WorkDir, "amd-nvclean-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        try
        {
            if (Directory.Exists(extractRoot))
                try { Directory.Delete(extractRoot, true); } catch { }
            Directory.CreateDirectory(extractRoot);

            Report("Unpacking (strip path — no UI)…");
            await ExpandPackageAsync(package, extractRoot, allowSevenZipInstall: true, progress, ct)
                .ConfigureAwait(false);

            // Nested InstallShield launcher often ships as AMD_Chipset_Drivers.exe — admin-extract MSI.
            var nested = Directory.EnumerateFiles(extractRoot, "AMD_Chipset_Drivers.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            var msi = Directory.EnumerateFiles(extractRoot, "AMD_Chipset_Drivers.msi", SearchOption.AllDirectories)
                .FirstOrDefault();
            var msiTree = Path.Combine(extractRoot, "msi-admin");
            if (msi is null && nested is not null)
            {
                // Run silent -INSTALL once only if we already have MSI from a prior stage under Roaming.
                var roamingMsi = Directory.Exists(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "AMD", "Chipset_Software"))
                    ? Directory.EnumerateFiles(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "AMD", "Chipset_Software"),
                            "AMD_Chipset_Drivers.msi",
                            SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                    : null;
                msi = roamingMsi;
            }

            if (msi is not null && File.Exists(msi))
            {
                Report("Administrative MSI extract (drivers only)…");
                Directory.CreateDirectory(msiTree);
                var adm = await RunProcessAsync(
                    "msiexec.exe",
                    $"/a \"{msi}\" /qn TARGETDIR=\"{msiTree}\"",
                    TimeSpan.FromMinutes(5),
                    ct).ConfigureAwait(false);
                if (!adm.Ok)
                    Report("MSI admin extract: " + adm.Message);
            }

            // Strip junk trees (RyzenMaster, AI PMF series, etc.)
            if (local.Spec is not null)
            {
                Report("Stripping optional junk…");
                StripPackage(extractRoot, local.Spec);
                if (Directory.Exists(msiTree))
                    StripPackage(msiTree, local.Spec);
            }

            var searchRoot = Directory.Exists(Path.Combine(msiTree, "Chipset_Software"))
                ? Path.Combine(msiTree, "Chipset_Software")
                : Directory.Exists(msiTree) ? msiTree : extractRoot;

            return await InstallCoreFromExtractAsync(searchRoot, target, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "Stopped — chipset clean install cancelled.");
        }
        catch (Exception ex)
        {
            return (false, "Chipset clean install failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Install only the core AM4/desktop platform drivers (NVCleanstall-style subset):
    /// W11x64 INFs via pnputil /force + silent msiexec of matching MSIs, then enable
    /// any disabled AMD devices (Code 22).
    /// </summary>
    private static async Task<(bool Ok, string Message)> InstallCoreFromExtractAsync(
        string searchRoot,
        string packageVersion,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        void Report(string m) => progress?.Report(m);

        // Prefer Chipset_Software folder if present.
        var chipsetDir = Directory.Exists(Path.Combine(searchRoot, "Chipset_Software"))
            ? Path.Combine(searchRoot, "Chipset_Software")
            : searchRoot;

        // Core folders only — skip AI/PMF/laptop noise for a desktop B550-class board.
        string[] coreNameHints =
        {
            "PSP", "SMBus", "SBxxxSMBus", "PCI", "I2C", "GPIO2", "GPIO Promontory", "Promontory_GPIO",
            "RyzenPPKG", "PPM Provisioning",
        };
        bool IsCore(string path)
        {
            var n = path.Replace('\\', '/');
            if (n.Contains("PMF", StringComparison.OrdinalIgnoreCase) &&
                !n.Contains("PPM Provisioning", StringComparison.OrdinalIgnoreCase))
                return false;
            if (n.Contains("3D_V-Cache", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Contains("Wireless", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Contains("USB4", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Contains("NULL Driver", StringComparison.OrdinalIgnoreCase)) return false;
            return coreNameHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        var infs = Directory.EnumerateFiles(chipsetDir, "*.inf", SearchOption.AllDirectories)
            .Where(p => p.Contains("W11x64", StringComparison.OrdinalIgnoreCase) ||
                        p.Contains("WTx64", StringComparison.OrdinalIgnoreCase))
            .Where(IsCore)
            // Prefer W11x64 over WTx64
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Contains("W11x64", StringComparison.OrdinalIgnoreCase)).First())
            .ToList();

        var msis = Directory.EnumerateFiles(chipsetDir, "*.msi", SearchOption.AllDirectories)
            .Where(IsCore)
            .Where(p => !p.Contains("PMF", StringComparison.OrdinalIgnoreCase) ||
                        p.Contains("PPM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (infs.Count == 0 && msis.Count == 0)
            return (false, "No core chipset drivers found in the package after unpack/strip.");

        Report($"Installing {infs.Count} core INF(s) + {msis.Count} MSI(s) silently…");

        var infOk = 0;
        var infFail = 0;
        foreach (var inf in infs)
        {
            ct.ThrowIfCancellationRequested();
            var r = await RunProcessAsync(
                "pnputil.exe",
                $"/add-driver \"{inf}\" /install /force",
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            // exit 0 = installed; exit 1 often = already present / reboot pending — count as soft ok if device later healthy
            if (r.Ok || r.Message.Contains("reboot", StringComparison.OrdinalIgnoreCase))
                infOk++;
            else
                infFail++;
            Report($"{Path.GetFileName(inf)}: {(r.Ok ? "ok" : r.Message)}");
        }

        var msiOk = 0;
        foreach (var msi in msis)
        {
            ct.ThrowIfCancellationRequested();
            var r = await RunProcessAsync(
                "msiexec.exe",
                $"/i \"{msi}\" /qn /norestart REBOOT=ReallySuppress",
                TimeSpan.FromMinutes(3),
                ct).ConfigureAwait(false);
            if (r.Ok) msiOk++;
            Report($"{Path.GetFileName(msi)}: {(r.Ok ? "ok" : r.Message)}");
        }

        Report("Re-enabling disabled AMD platform devices…");
        EnableDisabledAmdDevices(progress);

        // Stamp package version if missing
        try
        {
            foreach (var path in new[]
                     {
                         @"SOFTWARE\WOW6432Node\AMD\AMD_Chipset_IODrivers",
                         @"SOFTWARE\AMD\AMD_Chipset_IODrivers",
                     })
            {
                using var k = Registry.LocalMachine.CreateSubKey(path);
                k?.SetValue("ProductVersion", packageVersion, RegistryValueKind.String);
            }
        }
        catch { /* non-fatal */ }

        var health = AssessAmdChipsetHealth();
        if (health.Healthy)
        {
            return (true,
                $"AMD chipset clean install complete ({packageVersion}). " +
                $"{health.Detail} Reboot if Windows still asks for one.");
        }

        // Soft success if we installed something and only reboot remains
        if (infOk + msiOk > 0)
        {
            return (true,
                $"Chipset drivers applied ({infOk} INF, {msiOk} MSI). {health.Detail} " +
                "A reboot may finish binding. Re-Verify after reboot.");
        }

        return (false, "Clean install ran but no core drivers could be applied. " + health.Detail);
    }

    /// <summary>Live device health for AMD platform (not package log text).</summary>
    public static (bool Healthy, string Detail, IReadOnlyList<(string Title, string Detail, bool Ok)> Rows)
        AssessAmdChipsetHealth()
    {
        var rows = new List<(string, string, bool)>();
        var (name, ver) = FindInstalledChipset(HardwareInventory.CpuVendor.Amd);
        var regVer = ReadAmdProductVersion();
        var packageVer = regVer ?? ver;
        var floor = LoadCatalog().FirstOrDefault(p =>
            p.Vendor.Equals("amd", StringComparison.OrdinalIgnoreCase))?.TargetVersion;
        var target = ChipsetLatestLookup.ResolveLatest("amd", floor) ?? floor;

        rows.Add(("Package",
            packageVer is null
                ? "No ProductVersion registered"
                : $"{name ?? "AMD Chipset"} {packageVer}" +
                  (target is null ? "" : $" (newest known {target})"),
            packageVer is not null &&
            (target is null || CompareVersions(packageVer, target) >= 0)));

        // Probe key devices
        foreach (var (label, match) in new[]
                 {
                     ("PSP", "PSP"),
                     ("SMBus", "SMBus"),
                     ("PCI", "AMD PCI"),
                     ("I2C", "I2C"),
                     ("GPIO", "GPIO"),
                 })
        {
            var (status, driver, problem) = ReadAmdDevice(match);
            var ok = status is "OK" && problem is 0 or null;
            rows.Add((label,
                status is null
                    ? "Not present"
                    : $"{status}" + (driver is null ? "" : $" · {driver}") +
                      (problem is > 0 ? $" (code {problem})" : ""),
                ok || status is null && label is "I2C" or "GPIO")); // optional on some boards
        }

        // Required: package + PSP + SMBus healthy (SMBus was the real failure on this machine)
        var pkgOk = rows[0].Item3;
        var pspOk = rows.First(r => r.Item1 == "PSP").Item3;
        var smbus = rows.First(r => r.Item1 == "SMBus");
        var smbusOk = smbus.Item3 || smbus.Item2 == "Not present";
        var healthy = pkgOk && pspOk && smbusOk;
        var detail = healthy
            ? $"Package {packageVer}; platform devices OK."
            : string.Join("; ", rows.Where(r => !r.Item3).Select(r => $"{r.Item1}: {r.Item2}"));
        return (healthy, detail, rows);
    }

    private static string? ReadAmdProductVersion()
    {
        foreach (var path in new[]
                 {
                     @"SOFTWARE\WOW6432Node\AMD\AMD_Chipset_IODrivers",
                     @"SOFTWARE\AMD\AMD_Chipset_IODrivers",
                 })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                var v = k?.GetValue("ProductVersion")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
        }
        return null;
    }

    private static (string? Status, string? Driver, int? Problem) ReadAmdDevice(string nameContains)
    {
        try
        {
            // Use pnputil-friendly registry Enum under PCI VEN_1022 is heavy; PowerShell-less:
            // scan Class System + display via SetupAPI not available — use CIM via process? Keep registry.
            using var enumPci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (enumPci is null) return (null, null, null);
            foreach (var id in enumPci.GetSubKeyNames())
            {
                if (!id.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) continue;
                using var dev = enumPci.OpenSubKey(id);
                if (dev is null) continue;
                foreach (var inst in dev.GetSubKeyNames())
                {
                    using var key = dev.OpenSubKey(inst);
                    if (key is null) continue;
                    var desc = key.GetValue("DeviceDesc")?.ToString() ?? "";
                    // DeviceDesc is often "@oemxx.inf,%desc%;Friendly Name"
                    var friendly = desc.Contains(';') ? desc[(desc.LastIndexOf(';') + 1)..] : desc;
                    if (!friendly.Contains(nameContains, StringComparison.OrdinalIgnoreCase) &&
                        !desc.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var config = key.GetValue("ConfigFlags");
                    var problem = key.GetValue("Problem"); // may not exist
                    int? prob = problem is int pi ? pi : null;
                    var driverKey = key.GetValue("Driver")?.ToString();
                    string? ver = null;
                    if (!string.IsNullOrWhiteSpace(driverKey))
                    {
                        using var cls = Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                        ver = cls?.GetValue("DriverVersion")?.ToString();
                    }
                    // ConfigFlags 0x1 = disabled sometimes
                    var disabled = config is int cf && (cf & 1) != 0;
                    var status = disabled ? "Error" : "OK";
                    if (disabled) prob ??= 22;
                    return (status, ver, prob);
                }
            }
        }
        catch { }
        return (null, null, null);
    }

    private static void EnableDisabledAmdDevices(IProgress<string>? progress)
    {
        try
        {
            // PowerShell one-liner elevated not required if already admin
            var ps = @"
Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
  Where-Object { ($_.FriendlyName -match '^AMD ' -or $_.Manufacturer -match 'Advanced Micro|AMD') -and $_.Status -ne 'OK' } |
  ForEach-Object {
    try { Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction Stop; 'OK ' + $_.FriendlyName }
    catch { 'FAIL ' + $_.FriendlyName + ': ' + $_.Exception.Message }
  }
";
            var temp = Path.Combine(Path.GetTempPath(), $"exo-amd-enable-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(temp, ps);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60_000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    progress?.Report(line);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
        catch (Exception ex)
        {
            progress?.Report("Enable devices: " + ex.Message);
        }
    }
}
