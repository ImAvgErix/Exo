using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace Exo.Services;

/// <summary>
/// Ties driver lookup, hotfix relevance and component stripping into one flow — and separates
/// deciding from doing.
///
/// Three stages, and nothing crosses a stage boundary on its own:
///
/// <list type="number">
/// <item><see cref="Plan"/> — pure decision from data already fetched. No network, no disk.
/// Answers "is there a better driver for THIS card, and why".</item>
/// <item><c>Prepare</c> — download, verify, extract, strip. Produces a preview of exactly what
/// would be installed and the exact command line. <b>Installs nothing.</b></item>
/// <item><c>Execute</c> — runs the installer. Requires a token produced by Prepare plus an
/// explicit confirmation, so it cannot be reached by a caller that only meant to look.</item>
/// </list>
///
/// The split is the whole design. Every other Exo module can be undone by Repair; a display
/// driver that fails to install cannot, and recovery means Safe Mode and DDU. So the untested
/// step is last, it is opt-in, and the user is looking at a component diff when they take it.
/// </summary>
internal static class NvidiaDriverInstaller
{
    /// <summary>Downloads only ever come from NVIDIA, over TLS. Anything else is refused.</summary>
    private static readonly string[] AllowedHosts =
    {
        "us.download.nvidia.com",
        "international.download.nvidia.com",
        "download.nvidia.com",
    };

    internal enum Recommendation
    {
        /// <summary>Current driver is the newest that supports this card.</summary>
        UpToDate,
        /// <summary>A newer WHQL driver exists and supports this card.</summary>
        UpgradeWhql,
        /// <summary>A hotfix exists and fixes something that applies to this card.</summary>
        UpgradeHotfix,
        /// <summary>Newer drivers exist but none list this GPU — end of support.</summary>
        NoLongerSupported,
        /// <summary>Nothing could be read; say so rather than guess.</summary>
        Unknown,
    }

    internal sealed record InstallPlan(
        Recommendation Kind,
        string CurrentVersion,
        string? TargetVersion,
        string? DownloadUrl,
        bool TargetIsBeta,
        string Headline,
        IReadOnlyList<string> Reasons);

    /// <summary>
    /// Decides what — if anything — is worth installing. Pure: everything it needs is passed in,
    /// so the decision can be tested with made-up machines instead of a live endpoint.
    /// </summary>
    public static InstallPlan Plan(
        string gpuName,
        string currentVersion,
        NvidiaDriverLookup.DriverRelease? whql,
        NvidiaHotfixLookup.HotfixRelease? hotfix)
    {
        var reasons = new List<string>();

        if (whql is null)
            return new InstallPlan(Recommendation.Unknown, currentVersion, null, null, false,
                "Could not read NVIDIA's driver list.", reasons);

        // Support first. A newer driver that does not list this card is not an upgrade, it is
        // the end of the line — and saying "up to date" there would hide that permanently.
        if (!whql.Supports(gpuName))
        {
            reasons.Add($"{whql.Version} does not list {gpuName} among its supported GPUs.");
            return new InstallPlan(Recommendation.NoLongerSupported, currentVersion, whql.Version,
                null, false,
                $"NVIDIA has stopped supporting {gpuName} as of driver {whql.Version}.", reasons);
        }

        var whqlIsNewer = NvidiaDriverLookup.CompareVersions(whql.Version, currentVersion) > 0;

        // A hotfix is an increment on TOP of its base WHQL driver, not a way to skip ahead.
        // Two conditions, and both are load-bearing:
        //
        //   already on the base   Recommending 610.82 to someone on 591.86 would jump them two
        //                         branches onto a beta and skip the stable 610.74 the hotfix is
        //                         literally built from. Catch up first, then consider it.
        //   a fix names the card  A fix with no hardware qualifier might apply to anyone, which
        //                         is enough to mention but not enough to trade WHQL for beta.
        //                         NVIDIA's own guidance is that waiting is the safe option, so
        //                         moving someone needs a fix aimed at their hardware.
        var series = NvidiaHotfixLookup.SeriesOf(gpuName);
        var hotfixIsNewer = hotfix is not null
                            && NvidiaDriverLookup.CompareVersions(hotfix.Version, currentVersion) > 0;
        var onHotfixBase = hotfix is not null
                           && (string.IsNullOrEmpty(hotfix.BasedOnVersion)
                               || NvidiaDriverLookup.CompareVersions(currentVersion, hotfix.BasedOnVersion) >= 0);
        var hotfixRelevant = hotfixIsNewer && onHotfixBase && hotfix!.NamesSeries(series);

        if (hotfixRelevant)
        {
            foreach (var f in hotfix!.FixesFor(series)) reasons.Add($"Fixes: {f.Detail}");
            reasons.Add("This is a beta hotfix. NVIDIA run these through a shortened QA process.");
            return new InstallPlan(Recommendation.UpgradeHotfix, currentVersion, hotfix.Version,
                hotfix.DownloadUrl, true,
                $"Hotfix {hotfix.Version} fixes something that affects your card.", reasons);
        }

        // Explain the hotfix we are NOT offering, so a user who has seen the number elsewhere
        // is not left wondering whether Exo missed it. The two reasons read differently because
        // they mean different things.
        if (hotfix is not null && hotfixIsNewer && !hotfixRelevant)
        {
            reasons.Add(!onHotfixBase
                ? $"Hotfix {hotfix.Version} exists, but it is built on {hotfix.BasedOnVersion} - " +
                  $"it is not worth skipping to a beta from {currentVersion}."
                : $"Hotfix {hotfix.Version} exists but none of its fixes name a " +
                  $"{series ?? "your"}-series card, so it is not worth a beta driver.");
        }

        if (!whqlIsNewer)
        {
            reasons.Add($"{currentVersion} is current for {gpuName}.");
            return new InstallPlan(Recommendation.UpToDate, currentVersion, whql.Version, null, false,
                "Your driver is up to date.", reasons);
        }

        reasons.Add($"{whql.Version} released {whql.Released?.ToString("d MMM yyyy") ?? "recently"}.");
        reasons.Add($"Download is {whql.SizeDisplay}.");
        return new InstallPlan(Recommendation.UpgradeWhql, currentVersion, whql.Version,
            whql.DownloadUrl, false,
            $"Driver {whql.Version} is available — you are on {currentVersion}.", reasons);
    }

    /// <summary>
    /// A download URL is only acceptable if it is HTTPS and on an NVIDIA download host. The URL
    /// comes from a third-party response, so it is validated before it is ever fetched or shown
    /// as something to click.
    /// </summary>
    public static bool IsAcceptableDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────────────────
    // Nothing in this file existed to actually GET anything until now: the parsers were written
    // and tested, and no code path reached them. That is the same defect this release exists to
    // fix, committed by the fix itself.

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromMinutes(30) };

    private static readonly TimeSpan DriverInstallBudget = TimeSpan.FromMinutes(20);

    /// <summary>Product/series ids the driver feed needs, keyed by the series Exo already derives.</summary>
    private static readonly Dictionary<string, (int Psid, int Pfid)> FeedIds = new()
    {
        ["10"] = (101, 815),
        ["20"] = (107, 879),
        ["30"] = (120, 933),
        ["40"] = (127, 995),
        ["50"] = (131, 1066),
    };

    private static string FeedUrl(int psid, int pfid) =>
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&beta=0&isWHQL=1" +
        "&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&windowsVersion=10.0&windowsArchitecture=64bit";

    /// <summary>
    /// Reads the WHQL feed and the hotfix article, then plans. Every network failure degrades to
    /// null and a plan of <see cref="Recommendation.Unknown"/> — a driver check must never be
    /// able to fail a detect pass.
    /// </summary>
    public static async Task<InstallPlan> CheckAsync(
        string gpuName, string currentVersion, CancellationToken ct = default)
    {
        var series = NvidiaHotfixLookup.SeriesOf(gpuName);
        NvidiaDriverLookup.DriverRelease? whql = null;
        NvidiaHotfixLookup.HotfixRelease? hotfix = null;

        if (series is not null && FeedIds.TryGetValue(series, out var ids))
        {
            try
            {
                var body = await Http.GetStringAsync(FeedUrl(ids.Psid, ids.Pfid), ct).ConfigureAwait(false);
                whql = NvidiaDriverLookup.Parse(body);
            }
            catch { /* Unknown, handled by Plan */ }
        }

        try
        {
            var page = await Http.GetStringAsync(NvidiaHotfixLookup.ArticleUrl, ct).ConfigureAwait(false);
            hotfix = NvidiaHotfixLookup.Parse(page);
        }
        catch { /* no hotfix known; Plan says so rather than guessing */ }

        return Plan(gpuName, currentVersion, whql, hotfix);
    }

    internal sealed record PreparedInstall(
        string Version,
        string ExtractedPath,
        string SetupExe,
        string InstallArguments,
        IReadOnlyList<string> RemovedComponents,
        IReadOnlyList<string> KeptComponents,
        IReadOnlyList<string> RefusedRemovals,
        string Token);

    /// <summary>
    /// Downloads the package, extracts it, and rewrites its manifest with the unwanted
    /// components removed. <b>Installs nothing.</b> This is the stage the class comment always
    /// described and that did not exist — ExecuteAsync took a PreparedInstall no code could
    /// produce, so the chain was severed in the middle and every part of it was unreachable.
    /// </summary>
    public static async Task<(PreparedInstall? Prepared, string Message)> PrepareAsync(
        InstallPlan plan,
        bool allowSevenZipInstall = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (plan.DownloadUrl is null || plan.TargetVersion is null)
            return (null, "Nothing to install.");
        if (!IsAcceptableDownloadUrl(plan.DownloadUrl))
            return (null, $"Refusing a download URL that is not HTTPS on an NVIDIA host: {plan.DownloadUrl}");

        // Resolved before the download, not after. Discovering the missing unpacker at the end
        // would mean several hundred MB pulled down to reach an error that was knowable first.
        var sevenZip = FindSevenZip();
        var unpacker = DecideUnpacker(sevenZip, FindWinget(), allowSevenZipInstall);
        if (unpacker.Error is not null) return (null, unpacker.Error);
        if (unpacker.NeedsInstall)
        {
            var (installed, message) = await InstallSevenZipAsync(progress, ct).ConfigureAwait(false);
            if (installed is null) return (null, message);
            sevenZip = installed;
        }
        if (string.IsNullOrWhiteSpace(sevenZip))
            return (null, NoSevenZip);

        Directory.CreateDirectory(WorkDir);
        var exePath = Path.Combine(WorkDir, $"nvidia-{plan.TargetVersion}.exe");
        var extractDir = Path.Combine(WorkDir, plan.TargetVersion);

        try
        {
            if (!File.Exists(exePath))
            {
                progress?.Report($"Downloading NVIDIA {plan.TargetVersion}…");
                // Straight to a temp name and renamed on completion, so an interrupted download
                // is never mistaken for a finished one on the next run.
                var partial = exePath + ".part";
                using (var resp = await Http.GetAsync(plan.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(partial);
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
                File.Move(partial, exePath, overwrite: true);
            }
            else progress?.Report("Using the already-downloaded package.");

            var size = new FileInfo(exePath).Length;
            if (size < 100L * 1024 * 1024)
            {
                // Delete it, and not only because the message said so. Without this the
                // short file stays on disk, the File.Exists shortcut above reuses it on the
                // next attempt, and every retry fails the same check forever - one truncated
                // download would permanently disable driver installs on that machine.
                try { File.Delete(exePath); } catch { }
                return (null, $"Downloaded package is only {size / (1024 * 1024)} MB — that is not a driver. Deleted it; try again.");
            }

            progress?.Report("Unpacking…");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            var unpack = RunProcess(sevenZip, $"x \"{exePath}\" -o\"{extractDir}\" -y", 15 * 60_000);
            if (!unpack.Ok) return (null, $"Unpacking failed: {unpack.Message}");

            var setupCfg = Directory.EnumerateFiles(extractDir, "setup.cfg", SearchOption.AllDirectories).FirstOrDefault();
            var setupExe = Directory.EnumerateFiles(extractDir, "setup.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (setupCfg is null || setupExe is null)
                return (null, "Unpacked package has no setup.cfg/setup.exe — the package layout is not what Exo expects, so nothing was changed.");

            progress?.Report("Removing unwanted components…");
            var original = await File.ReadAllTextAsync(setupCfg, ct).ConfigureAwait(false);
            var strip = NvidiaDriverPackage.Strip(original);

            // Keep the untouched manifest next to the edited one. If an install misbehaves, the
            // first question is whether the edit caused it, and that needs the original.
            await File.WriteAllTextAsync(setupCfg + ".exo-original", original, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(setupCfg, strip.Xml, ct).ConfigureAwait(false);

            var args = NvidiaDriverPackage.BuildInstallArguments(strip.Xml);
            var token = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(setupExe + plan.TargetVersion + args)))[..16];

            return (new PreparedInstall(
                plan.TargetVersion, extractDir, setupExe, args,
                strip.Removed, strip.Kept, strip.RefusedToRemove, token),
                $"Ready to install {plan.TargetVersion}. Nothing has been changed yet.");
        }
        catch (OperationCanceledException) { return (null, "Cancelled — nothing was installed."); }
        catch (Exception ex) { return (null, $"Preparation failed: {ex.Message}"); }
    }

    /// <summary>
    /// A 7-Zip-compatible console extractor. Checked in order of how likely it is to be the
    /// one the user actually has: the official installer's location, then the package managers
    /// that install it, then NanaZip (a maintained 7-Zip fork with an identical command line),
    /// then PATH.
    ///
    /// Hardcoded <c>C:\</c> was wrong on any machine whose Windows is not on C, so the Program
    /// Files paths come from the environment.
    /// </summary>
    internal static string? FindSevenZip()
    {
        string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
        var local = Env("LOCALAPPDATA");
        var home = Env("USERPROFILE");

        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            // winget's shim directory, which is where a winget-installed 7-Zip is reachable
            // from even before a new shell picks up the PATH change.
            Path.Combine(local, "Microsoft", "WinGet", "Links", "7z.exe"),
            Path.Combine(home, "scoop", "shims", "7z.exe"),
            Path.Combine(Env("ProgramData"), "chocolatey", "bin", "7z.exe"),
            // NanaZip ships its console tool under its own name; same arguments as 7z.
            Path.Combine(local, "Microsoft", "WindowsApps", "NanaZipC.exe"),
        })
        {
            try { if (c.Length > 0 && File.Exists(c)) return c; } catch { }
        }

        var path = Env("PATH");
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var exe in new[] { "7z.exe", "7za.exe", "NanaZipC.exe" })
            {
                try
                {
                    var c = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Whether Exo has a way to obtain 7-Zip on this machine. Only winget: it resolves the
    /// package from Microsoft's catalogue, checks the publisher's hash, and installs the real
    /// 7-Zip. Exo writing its own downloader for a third-party binary would mean pinning a
    /// hash — and a hash nobody verified is worse than no hash, because it is trusted.
    /// </summary>
    internal static string? FindWinget()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        var shim = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        try { if (File.Exists(shim)) return shim; } catch { }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    internal const string NoSevenZip =
        "7-Zip is required to unpack the driver and was not found. Install it from 7-zip.org, then try again.";
    internal const string NoWinget =
        "7-Zip is needed to unpack the driver, and winget isn't available here to install it. Install 7-Zip from 7-zip.org and Exo will pick it up.";

    /// <summary>
    /// What to do about the unpacker, decided from what was found rather than by looking.
    ///
    /// Pure, for the same reason <see cref="Plan"/> is: it can then be tested against machines
    /// that do and do not have 7-Zip instead of against whatever the build agent happens to
    /// have installed. The first version of this test did the latter — it passed on Linux,
    /// failed on the Windows agent that ships with 7-Zip, and on that agent got past the
    /// prerequisite and issued a real request to NVIDIA for a driver package.
    /// </summary>
    internal static (bool Ready, bool NeedsInstall, string? Error) DecideUnpacker(
        string? sevenZip, string? winget, bool allowInstall)
    {
        if (!string.IsNullOrEmpty(sevenZip)) return (true, false, null);
        if (!allowInstall) return (false, false, NoSevenZip);
        if (string.IsNullOrEmpty(winget)) return (false, false, NoWinget);
        return (false, true, null);
    }

    /// <summary>
    /// Installs 7-Zip through winget, and only when the caller says it may. This is the one
    /// place Exo puts third-party software on the machine, so it is never a side effect of
    /// asking for a driver — the user is asked first and the answer travels here as an
    /// argument.
    ///
    /// Success is defined as <see cref="FindSevenZip"/> finding it afterwards, not as winget
    /// returning zero. An installer that reports success and leaves nothing runnable is the
    /// failure this codebase keeps finding, and the exit code is the thing that lies.
    /// </summary>
    internal static async Task<(string? Path, string Message)> InstallSevenZipAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var existing = FindSevenZip();
        if (existing is not null) return (existing, "7-Zip was already present.");

        var winget = FindWinget();
        if (winget is null) return (null, NoWinget);

        progress?.Report("Installing 7-Zip through winget…");
        var run = await Task.Run(() => RunProcess(winget,
            "install --id 7zip.7zip --exact --source winget --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            10 * 60_000), ct).ConfigureAwait(false);

        var found = FindSevenZip();
        if (found is not null) return (found, "7-Zip installed.");
        return (null, run.Ok
            ? "winget reported success but 7-Zip still isn't where Exo can run it. Install it from 7-zip.org and try again."
            : $"Installing 7-Zip failed: {run.Message}");
    }

    private static (bool Ok, string Message) RunProcess(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "process did not start");
            // Drain both pipes while waiting. A child that fills an unread pipe buffer
            // blocks forever — winget's progress rendering does exactly that — and the
            // healthy run then gets killed here as "timed out".
            _ = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "timed out"); }
            return p.ExitCode == 0 ? (true, "ok") : (false, $"exit {p.ExitCode}: {stderr.Result.Trim()}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// What Execute will do, as text, for showing before anything runs. Deliberately blunt about
    /// the parts that are not reversible.
    /// </summary>
    public static IReadOnlyList<string> DescribePlan(PreparedInstall p) => new[]
    {
        $"Install NVIDIA driver {p.Version}.",
        $"Leaving out: {(p.RemovedComponents.Count == 0 ? "nothing" : string.Join(", ", p.RemovedComponents))}.",
        $"Keeping: {string.Join(", ", p.KeptComponents)}.",
        $"Command: setup.exe {p.InstallArguments}",
        "The screen will go black several times while the driver loads.",
        "A clean install resets NVIDIA Control Panel settings, so Exo re-applies its profile afterwards.",
        "If this fails, recovery is Safe Mode plus DDU. Have it downloaded before starting.",
    };

    /// <summary>
    /// Runs the installer. Requires the token Prepare issued AND an explicit confirmation from
    /// the caller — two arguments that a caller who only meant to preview cannot supply by
    /// accident. Returns the installer's exit code.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ExecuteAsync(
        PreparedInstall prepared,
        string confirmationToken,
        bool userConfirmed,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!userConfirmed)
            return (false, "Not confirmed — nothing was installed.");
        if (!string.Equals(confirmationToken, prepared.Token, StringComparison.Ordinal))
            return (false, "Confirmation did not match this prepared install — nothing was installed.");
        if (!File.Exists(prepared.SetupExe))
            return (false, $"Installer missing at {prepared.SetupExe} — nothing was installed.");

        progress?.Report("Creating a system restore point…");
        var restore = TryCreateRestorePoint($"Before NVIDIA {prepared.Version} (Exo)");
        progress?.Report(restore
            ? "Restore point created."
            : "Could not create a restore point — System Protection may be off. Continuing.");

        progress?.Report($"Installing NVIDIA {prepared.Version}. The screen will flicker…");
        try
        {
            var psi = new ProcessStartInfo(prepared.SetupExe, prepared.InstallArguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = prepared.ExtractedPath,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Installer did not start.");

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(DriverInstallBudget);
            try
            {
                await proc.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillProcessTree(proc);
                return (false, "Stopped — the NVIDIA installer was terminated. Check the live driver state before retrying.");
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(proc);
                return (false, $"NVIDIA installer timed out after {DriverInstallBudget.TotalMinutes:0} minutes and was terminated.");
            }

            // NVIDIA's installer uses 0 for success and 1 for "installed, reboot required".
            // Treating anything non-zero as failure would report a successful install as broken.
            return proc.ExitCode switch
            {
                0 => (true, $"Driver {prepared.Version} installed."),
                1 => (true, $"Driver {prepared.Version} installed — reboot to finish."),
                _ => (false, $"Installer exited with code {proc.ExitCode}. The previous driver should still be active; reboot and check.")
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator approval was declined — nothing was installed.");
        }
        catch (Exception ex)
        {
            return (false, $"Install failed to launch: {ex.Message}");
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: an elevated vendor child may already have detached. The result stays
            // incomplete so a fresh live check, never this process handle, decides what happened.
        }
    }

    /// <summary>
    /// Best-effort restore point. Not a guarantee — System Protection is off by default on many
    /// installs — so the caller reports whether it worked rather than assuming it did.
    /// </summary>
    private static bool TryCreateRestorePoint(string description)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            return p.WaitForExit(120_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Staging area for the downloaded and extracted package. Resolved from the environment
    /// rather than PathHelper so this file carries no dependency on the app's helper tree and
    /// its decision logic stays testable in isolation.
    /// </summary>
    internal static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Exo", "driver-staging");
}
