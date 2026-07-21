namespace Exo.Services.Ai;

/// <summary>Exo Upscaler Swapper — DLSS/FSR/XeSS with backups + in-app risk ack (no AC hard-skip).</summary>
public sealed class ExoUpscalerService
{
    public sealed record GameHit(string Game, string Path, string Kind, bool AntiCheatTagged);

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
                if (!DllPatterns.Any(p =>
                        name.Equals(p.Replace("*", ""), StringComparison.OrdinalIgnoreCase) ||
                        (p.Contains('*') && name.StartsWith(p.TrimEnd('*'), StringComparison.OrdinalIgnoreCase))))
                    continue;

                var dir = Path.GetDirectoryName(file) ?? root;
                var ac = AntiCheatMarkers.Any(m =>
                    Directory.Exists(Path.Combine(dir, m)) ||
                    file.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                    Directory.EnumerateFiles(dir, "*.exe").Any(e =>
                        Path.GetFileName(e).Contains(m, StringComparison.OrdinalIgnoreCase)));

                hits.Add(new GameHit(
                    Path.GetFileName(dir),
                    file,
                    GuessKind(name),
                    ac));
            }
        }

        return hits;
    }

    public (bool Ok, string Message) SwapWithBackup(string targetDll, string sourceDll, bool riskAcknowledged)
    {
        if (!riskAcknowledged)
            return (false, "Acknowledge upscaler risk in Settings before swapping.");

        if (!File.Exists(sourceDll) || !File.Exists(targetDll))
            return (false, "source or target DLL missing");

        try
        {
            var rel = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(targetDll)))[..16];
            var backup = Path.Combine(BackupRoot, rel + "_" + Path.GetFileName(targetDll));
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
            var rel = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(targetDll)))[..16];
            var backup = Path.Combine(BackupRoot, rel + "_" + Path.GetFileName(targetDll));
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
