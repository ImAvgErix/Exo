namespace Exo.Services.Ai;

/// <summary>Brave-only browser enforcement — session-safe (never wipe cookies/auth).</summary>
public sealed class ExoBraveOnlyService
{
    private static readonly string[] CompetingProcessNames =
    [
        "chrome", "msedge", "firefox", "opera", "vivaldi", "arc", "iexplore", "waterfox", "librewolf"
    ];

    private readonly ExoAutoInstallService _install;

    public ExoBraveOnlyService(ExoAutoInstallService install) => _install = install;

    public async Task<(bool Ok, string Message, int Terminated)> EnforceAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("brave-only: ensure Brave installed");
        var (ok, msg) = await _install.EnsureInstalledAsync("brave", progress, ct).ConfigureAwait(false);
        if (!ok && !_install.IsPresent("brave"))
            return (false, msg, 0);

        var terminated = 0;
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in CompetingProcessNames)
            {
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        try
                        {
                            p.Kill(entireProcessTree: true);
                            terminated++;
                        }
                        catch
                        {
                            // best-effort
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        // Default browser association + Edge depower happen via Host OS / PcControl on Windows.
        // Never delete WebView2. Never touch Brave Cookies / Login Data / Local Storage.
        return (true,
            $"Brave-only enforced (terminated={terminated}). Edge WebView2 preserved; sessions kept.",
            terminated);
    }

    public static bool IsSessionSafePath(string path) => !ExoActionSafety.TouchesSessionStore(path);
}
