using System.Diagnostics;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Detects and repairs a WebView2 Evergreen Runtime install that is registered
/// (registry key + msedgewebview2.exe present) but missing its actual browser
/// data files -- the same "incomplete install" class of failure the SFX
/// installer already guards against at install time (see
/// tools/ExoSfx.cs::IsWebView2RuntimeHealthy). That check never re-runs once
/// Exo is installed, so a runtime corrupted later (a bad Edge auto-update is
/// the usual cause) leaves the app stuck on a blank window with no recovery
/// path. This runs the same check + silent repair at launch instead.
/// </summary>
internal static class WebView2Doctor
{
    private const string EvergreenBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static readonly string[] ClientRegistryKeys =
    {
        @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    };

    public static bool IsHealthy() => FindHealthyBrowserFolder() is not null;

    private static string? FindHealthyBrowserFolder()
    {
        var folder = FindBrowserFolder();
        if (folder is null) return null;
        if (!File.Exists(Path.Combine(folder, "msedgewebview2.exe"))) return null;
        if (File.Exists(Path.Combine(folder, "icudtl.dat"))) return folder;
        if (File.Exists(Path.Combine(folder, "resources.pak"))) return folder;
        try
        {
            if (Directory.EnumerateFiles(folder, "icudtl.dat", SearchOption.AllDirectories).Any()) return folder;
            if (Directory.EnumerateFiles(folder, "resources.pak", SearchOption.AllDirectories).Any()) return folder;
        }
        catch { }
        return null;
    }

    private static string? FindBrowserFolder()
    {
        foreach (var key in ClientRegistryKeys)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("location") as string is { Length: > 0 } loc &&
                    k.GetValue("pv") as string is { Length: > 0 } pv)
                {
                    var dir = Path.Combine(loc, pv);
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe"))) return dir;
                }
            }
            catch { }
        }

        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "EdgeWebView", "Application");
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe"))) return dir;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Downloads and silently runs the Evergreen bootstrapper, then polls for a
    /// healthy install. Same flow as ExoSfx.cs's EnsureWebView2Runtime, minus the
    /// install-time logging -- StartupLog carries the trail here instead.
    /// </summary>
    public static async Task<bool> TryRepairAsync(CancellationToken ct = default)
    {
        string? setupPath = null;
        try
        {
            StartupLog.Mark("webview2-repair-start");
            var tempDir = Path.Combine(Path.GetTempPath(), "exo-webview2-repair");
            Directory.CreateDirectory(tempDir);
            setupPath = Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                using var response = await http.GetAsync(EvergreenBootstrapperUrl, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(setupPath);
                await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 10_000)
            {
                StartupLog.Mark("webview2-repair-bad-download");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process is not null)
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

            for (var i = 0; i < 30 && !IsHealthy(); i++)
                await Task.Delay(1000, ct).ConfigureAwait(false);

            var healthy = IsHealthy();
            StartupLog.Mark(healthy ? "webview2-repair-ok" : "webview2-repair-still-unhealthy");
            return healthy;
        }
        catch (Exception ex)
        {
            StartupLog.Mark("webview2-repair-failed:" + ex.GetType().Name);
            return false;
        }
        finally
        {
            if (setupPath is not null)
            {
                try { File.Delete(setupPath); } catch { }
            }
        }
    }
}
