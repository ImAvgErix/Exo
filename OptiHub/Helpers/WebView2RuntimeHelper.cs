using System.Diagnostics;
using System.Net.Http;

namespace OptiHub.Helpers;

/// <summary>
/// Detects a broken/incomplete Evergreen WebView2 Runtime and repairs it.
/// Registry + msedgewebview2.exe alone are NOT enough — a healthy install has
/// browser data files (icudtl.dat / resources.pak). Incomplete installs cause
/// EnsureCoreWebView2Async to throw FileNotFoundException.
/// </summary>
public static class WebView2RuntimeHelper
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(4)
    };

    public static bool IsHealthy()
    {
        try
        {
            var folder = FindBrowserFolder();
            if (folder is null) return false;
            if (!File.Exists(Path.Combine(folder, "msedgewebview2.exe"))) return false;

            // Full Evergreen layout always ships these next to the browser.
            if (File.Exists(Path.Combine(folder, "icudtl.dat"))) return true;
            if (File.Exists(Path.Combine(folder, "resources.pak"))) return true;

            // Some builds nest critical bits under EBWebView — still need ICU/pak.
            foreach (var f in Directory.EnumerateFiles(folder, "icudtl.dat", SearchOption.AllDirectories))
                if (File.Exists(f)) return true;
            foreach (var f in Directory.EnumerateFiles(folder, "resources.pak", SearchOption.AllDirectories))
                if (File.Exists(f)) return true;

            // Incomplete: exe present, browser data missing (seen with ~15 files only).
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindBrowserFolder()
    {
        var candidates = new List<string>();

        try
        {
            foreach (var keyPath in new[]
                     {
                         @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                         @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
                     })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
                var loc = key?.GetValue("location") as string;
                var pv = key?.GetValue("pv") as string;
                if (!string.IsNullOrWhiteSpace(loc) && !string.IsNullOrWhiteSpace(pv))
                {
                    var dir = Path.Combine(loc, pv);
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        candidates.Add(dir);
                }
            }
        }
        catch { }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Microsoft", "EdgeWebView", "Application"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Microsoft", "EdgeWebView", "Application")
                 })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        candidates.Add(dir);
                }
            }
            catch { }
        }

        // Prefer a complete install (has icudtl/resources) over a hollow leftover folder.
        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FolderLooksComplete(dir))
                return dir;
        }

        return candidates.FirstOrDefault();
    }

    private static bool FolderLooksComplete(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, "icudtl.dat"))) return true;
            if (File.Exists(Path.Combine(folder, "resources.pak"))) return true;
            return Directory.EnumerateFiles(folder, "icudtl.dat", SearchOption.AllDirectories).Any()
                   || Directory.EnumerateFiles(folder, "resources.pak", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    public static string Describe()
    {
        var folder = FindBrowserFolder();
        if (folder is null) return "WebView2 Runtime not found.";
        var count = 0;
        try { count = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length; }
        catch { }
        var healthy = IsHealthy();
        return $"WebView2 folder={folder} files={count} healthy={healthy}";
    }

    /// <summary>
    /// Downloads the Evergreen bootstrapper and runs a silent install/repair.
    /// Returns true when the runtime is healthy after the attempt.
    /// </summary>
    public static async Task<bool> EnsureHealthyAsync(
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        if (IsHealthy())
        {
            status?.Report("WebView2 Runtime OK.");
            return true;
        }

        status?.Report("WebView2 Runtime incomplete — repairing…");
        Log("EnsureHealthy: " + Describe());

        var prereqDir = Path.Combine(PathHelper.AppDataDir, "prereqs");
        Directory.CreateDirectory(prereqDir);
        var setupPath = Path.Combine(prereqDir, "MicrosoftEdgeWebview2Setup.exe");

        try
        {
            status?.Report("Downloading WebView2 Runtime…");
            // Official Evergreen bootstrapper (small; pulls the full runtime).
            const string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(
                    setupPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 10_000)
            {
                Log("Bootstrapper download invalid.");
                return IsHealthy();
            }

            status?.Report("Installing WebView2 Runtime (may prompt once)…");
            // Prefer silent; if UAC is required the bootstrapper elevates itself.
            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Log("Could not start WebView2 bootstrapper.");
                return IsHealthy();
            }

            // Bootstrapper often finishes in under a minute when online.
            var exited = await Task.Run(() => proc.WaitForExit(180_000), ct).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Log("WebView2 bootstrapper timed out.");
            }
            else
            {
                Log("WebView2 bootstrapper exit=" + proc.ExitCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("EnsureHealthy failed: " + ex);
        }

        // Give EdgeUpdate a moment to finish unpacking.
        for (var i = 0; i < 20 && !IsHealthy(); i++)
            await Task.Delay(500, ct).ConfigureAwait(false);

        var ok = IsHealthy();
        Log("EnsureHealthy result: " + Describe());
        status?.Report(ok
            ? "WebView2 Runtime ready."
            : "WebView2 Runtime still incomplete. Install from Microsoft, then restart OptiHub.");
        return ok;
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(PathHelper.LogsDir, "webview-init.log"),
                $"[{DateTime.UtcNow:O}] runtime: {msg}\n");
        }
        catch { }
    }
}
