namespace Exo.Services.Ai;

/// <summary>
/// Exo Upscaler Swapper — DLSS/FSR/XeSS with backups + in-app risk ack.
/// AC-tagged game folders are scanned but never swapped.
/// Source resolution: newest matching vendor DLL under Program Files NVIDIA/AMD/Intel
/// (or parameters["source"] / explicit source roots).
/// </summary>
public sealed class ExoUpscalerService
{
    public sealed record GameHit(string Game, string Path, string Kind, bool AntiCheatTagged);

    public sealed record SwapApplyResult(
        int Scanned,
        int Swapped,
        int SkippedAc,
        int BackupsCreated,
        string Message);

    public string BackupRoot
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Exo", "ai", "upscaler-backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static readonly string[] DllPatterns =
    [
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll",
        "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll", "ffx_fsr*",
        "libxess.dll", "libxess_dx11.dll", "libxell.dll"
    ];

    public static readonly string[] AntiCheatMarkers =
    [
        "EasyAntiCheat", "BattlEye", "vgk", "vgc", "FACEIT", "GameGuard", "xigncode"
    ];

    /// <summary>
    /// Vendor roots searched for newest source DLLs (document for agents / UI).
    /// NVIDIA: Program Files\NVIDIA Corporation; AMD: AMD / AMD\CNext; Intel: Intel.
    /// Also Program Files (x86) mirrors and optional extraRoots / parameters["source"].
    /// </summary>
    public static IReadOnlyList<string> DefaultVendorSearchRoots()
    {
        var roots = new List<string>();
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(pf) || !Directory.Exists(pf)) continue;
            foreach (var rel in new[]
                     {
                         "NVIDIA Corporation",
                         "NVIDIA",
                         "AMD",
                         Path.Combine("AMD", "CNext"),
                         "Intel",
                         Path.Combine("Intel", "Intel Arc Control"),
                         Path.Combine("Intel", "Media SDK")
                     })
            {
                var p = Path.Combine(pf, rel);
                if (Directory.Exists(p)) roots.Add(p);
            }
        }

        return roots;
    }

    /// <summary>
    /// Resolve newest vendor source DLL per basename (LastWriteTimeUtc wins).
    /// <paramref name="extraRoots"/> may include a file path (parameters["source"]) or directories.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolveNewestVendorSources(
        IEnumerable<string>? extraRoots = null)
    {
        var newest = new Dictionary<string, (string Path, DateTime Write)>(
            StringComparer.OrdinalIgnoreCase);

        var roots = new List<string>(DefaultVendorSearchRoots());
        if (extraRoots is not null)
        {
            foreach (var r in extraRoots)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                if (File.Exists(r))
                {
                    ConsiderSourceFile(r, newest);
                    continue;
                }

                if (Directory.Exists(r)) roots.Add(r);
            }
        }

        foreach (var root in roots)
            EnumerateVendorDlls(root, newest);

        return newest.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Path,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GameHit> Scan(IEnumerable<string> roots)
    {
        var hits = new List<GameHit>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!MatchesDllPattern(name))
                    continue;

                var dir = Path.GetDirectoryName(file) ?? root;
                var ac = IsAntiCheatTagged(dir, file);

                hits.Add(new GameHit(
                    Path.GetFileName(dir),
                    file,
                    GuessKind(name),
                    ac));
            }
        }

        return hits;
    }

    /// <summary>
    /// Scan game roots, skip AC-tagged hits, swap non-AC targets from newest vendor sources.
    /// </summary>
    public SwapApplyResult ApplySupportedGameSwaps(
        IEnumerable<string> gameRoots,
        IEnumerable<string>? sourceRoots,
        bool riskAcknowledged)
    {
        if (!riskAcknowledged)
        {
            return new SwapApplyResult(
                0, 0, 0, 0,
                "Acknowledge upscaler risk in Settings before swapping.");
        }

        var sources = ResolveNewestVendorSources(sourceRoots);
        var hits = Scan(gameRoots);
        var scanned = hits.Count;
        var skippedAc = 0;
        var swapped = 0;
        var backedUp = 0;

        if (sources.Count == 0)
        {
            return new SwapApplyResult(
                scanned, 0, hits.Count(h => h.AntiCheatTagged), 0,
                scanned == 0
                    ? "No upscaler DLLs found; no vendor source DLLs resolved"
                    : $"Scanned={scanned}; no vendor source DLL resolved under NVIDIA/AMD/Intel Program Files (or source=)");
        }

        foreach (var hit in hits)
        {
            if (hit.AntiCheatTagged)
            {
                skippedAc++;
                continue;
            }

            var name = Path.GetFileName(hit.Path);
            if (!sources.TryGetValue(name, out var source) ||
                string.Equals(Path.GetFullPath(source), Path.GetFullPath(hit.Path), StringComparison.OrdinalIgnoreCase))
                continue;

            var backupPath = BackupPathFor(hit.Path);
            var hadBackup = File.Exists(backupPath);
            var (ok, _) = SwapWithBackup(hit.Path, source, riskAcknowledged: true);
            if (!ok) continue;
            swapped++;
            if (!hadBackup && File.Exists(backupPath))
                backedUp++;
            else if (hadBackup)
                backedUp++; // prior backup still protects this swap
        }

        return new SwapApplyResult(
            scanned,
            swapped,
            skippedAc,
            backedUp,
            $"scanned={scanned} swapped={swapped} skippedAc={skippedAc} backedUp={backedUp} sources={sources.Count}");
    }

    public (bool Ok, string Message) SwapWithBackup(string targetDll, string sourceDll, bool riskAcknowledged)
    {
        if (!riskAcknowledged)
            return (false, "Acknowledge upscaler risk in Settings before swapping.");

        if (!File.Exists(sourceDll) || !File.Exists(targetDll))
            return (false, "source or target DLL missing");

        try
        {
            var backup = BackupPathFor(targetDll);
            if (!File.Exists(backup))
                File.Copy(targetDll, backup, overwrite: false);
            File.Copy(sourceDll, targetDll, overwrite: true);
            return (true, $"swapped {Path.GetFileName(targetDll)} (backup {backup})");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public (bool Ok, string Message) Restore(string targetDll)
    {
        try
        {
            var backup = BackupPathFor(targetDll);
            if (!File.Exists(backup))
                return (false, "no backup");
            File.Copy(backup, targetDll, overwrite: true);
            return (true, "restored from backup");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string BackupPathFor(string targetDll)
    {
        var rel = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(targetDll)))[..16];
        return Path.Combine(BackupRoot, rel + "_" + Path.GetFileName(targetDll));
    }

    private static void EnumerateVendorDlls(
        string root,
        Dictionary<string, (string Path, DateTime Write)> newest)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
            ConsiderSourceFile(file, newest);
    }

    private static void ConsiderSourceFile(
        string file,
        Dictionary<string, (string Path, DateTime Write)> newest)
    {
        var name = Path.GetFileName(file);
        if (!MatchesDllPattern(name)) return;
        DateTime write;
        try { write = File.GetLastWriteTimeUtc(file); }
        catch { return; }

        if (!newest.TryGetValue(name, out var cur) || write > cur.Write)
            newest[name] = (file, write);
    }

    private static bool MatchesDllPattern(string name) =>
        DllPatterns.Any(p =>
            name.Equals(p.Replace("*", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase) ||
            (p.Contains('*', StringComparison.Ordinal) &&
             name.StartsWith(p.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)));

    private static bool IsAntiCheatTagged(string dir, string file)
    {
        if (AntiCheatMarkers.Any(m =>
                file.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(Path.Combine(dir, m))))
            return true;

        try
        {
            return Directory.EnumerateFiles(dir, "*.exe").Any(e =>
                AntiCheatMarkers.Any(m =>
                    Path.GetFileName(e).Contains(m, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static string GuessKind(string name)
    {
        if (name.Contains("dlss", StringComparison.OrdinalIgnoreCase)) return "dlss";
        if (name.Contains("fsr", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("fidelity", StringComparison.OrdinalIgnoreCase)) return "fsr";
        if (name.Contains("xess", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("xell", StringComparison.OrdinalIgnoreCase)) return "xess";
        return "unknown";
    }
}
