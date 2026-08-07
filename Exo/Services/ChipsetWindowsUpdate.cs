using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// Fully automatic chipset/platform driver install via the Windows Update Agent COM API.
/// No browser, no drop folder — searches online, downloads, and installs matching drivers.
/// </summary>
internal static class ChipsetWindowsUpdate
{
    private static readonly TimeSpan InstallBudget = TimeSpan.FromMinutes(20);

    internal sealed record WUDriver(
        string Title,
        string Identity);

    /// <summary>
    /// Search online for not-yet-installed drivers matching AMD or Intel platform hardware.
    /// Returns titles only — install goes through a generated elevated PowerShell that uses
    /// the same COM API (WUA must run elevated to install).
    /// </summary>
    public static async Task<(IReadOnlyList<WUDriver> Drivers, string Message)> SearchAsync(
        string vendor, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        var worker = Task.Run(() =>
        {
            try
            {
                dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
                session.ClientApplicationID = "Exo-Chipset";
                dynamic searcher = session.CreateUpdateSearcher();
                // Online search
                dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
                var list = new List<WUDriver>();
                int count = (int)result.Updates.Count;
                for (var i = 0; i < count; i++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    dynamic u = result.Updates.Item(i);
                    string title = (string)(u.Title ?? "");
                    if (!IsRelevant(vendor, title)) continue;
                    if (Regex.IsMatch(title, @"(?i)Display Driver|Radeon Software|GeForce|Graphics"))
                        continue;
                    string id = "";
                    try { id = (string)(u.Identity.UpdateID ?? ""); } catch { id = title; }
                    list.Add(new WUDriver(title, id));
                }
                Marshal.FinalReleaseComObject(result);
                return ((IReadOnlyList<WUDriver>)list,
                    list.Count == 0
                        ? "Windows Update has no pending platform drivers for this PC."
                        : $"Windows Update offered {list.Count} platform driver(s).");
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ((IReadOnlyList<WUDriver>)Array.Empty<WUDriver>(),
                    "Windows Update search failed: " + ex.Message);
            }
        }, CancellationToken.None);

        // WUA's COM Search call is synchronous and may ignore cancellation.
        // Return a useful catalog-backed result to the UI instead of waiting on
        // a driver service that has stopped responding.
        var timeout = Task.Delay(TimeSpan.FromSeconds(12), ct);
        var completed = await Task.WhenAny(worker, timeout).ConfigureAwait(false);
        if (completed != worker)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            budget.Cancel();
            _ = worker.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return (Array.Empty<WUDriver>(), "Windows Update search timed out after 12 seconds; the catalog plan is still available.");
        }
        return await worker.ConfigureAwait(false);
    }

    private static bool IsRelevant(string vendor, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (vendor.Equals("amd", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(title, @"(?i)AMD|Advanced Micro Devices")
                   && !Regex.IsMatch(title, @"(?i)Radeon|Display Driver|Graphics");
        if (vendor.Equals("intel", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(title, @"(?i)Intel")
                   && Regex.IsMatch(title, @"(?i)Chipset|Serial IO|Management Engine|USB|SPI|I2C|GPIO|SMBus|System|Platform|MEI|Thunderbolt")
                   && !Regex.IsMatch(title, @"(?i)Graphics|Arc|UHD|Iris|Display");
        return false;
    }

    /// <summary>
    /// Elevated install of all pending matching drivers via WUA. Fully automatic.
    /// </summary>
    public static async Task<(bool Ok, string Message, int Installed)> InstallPendingAsync(
        string vendor,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Asking Windows Update for platform drivers…");
        var (drivers, searchMsg) = await SearchAsync(vendor, ct).ConfigureAwait(false);
        if (drivers.Count == 0)
            return (false, searchMsg, 0);

        progress?.Report($"Installing {drivers.Count} Windows Update driver package(s)…");

        // PowerShell elevated script — WUA install requires admin.
        var vendorRe = vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
            ? "(?i)AMD|Advanced Micro Devices"
            : "(?i)Intel";
        var excludeRe = vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)
            ? "(?i)Radeon|Display Driver|Graphics|GeForce"
            : "(?i)Graphics|Arc Graphics|UHD|Iris|Display Driver";

        var script = $@"
$ErrorActionPreference = 'Stop'
$vendorRe = '{vendorRe}'
$excludeRe = '{excludeRe}'
$session = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'Exo-Chipset'
$searcher = $session.CreateUpdateSearcher()
$result = $searcher.Search(""IsInstalled=0 and Type='Driver'"")
$toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($u in $result.Updates) {{
  $t = [string]$u.Title
  if ($t -notmatch $vendorRe) {{ continue }}
  if ($t -match $excludeRe) {{ continue }}
  if (-not $u.EulaAccepted) {{ $u.AcceptEula() | Out-Null }}
  [void]$toInstall.Add($u)
  Write-Output (""PICK $t"")
}}
if ($toInstall.Count -eq 0) {{
  Write-Output 'NONE'
  exit 0
}}
$downloader = $session.CreateUpdateDownloader()
$downloader.Updates = $toInstall
$dr = $downloader.Download()
Write-Output (""DOWNLOAD ResultCode=$($dr.ResultCode)"")
$installer = $session.CreateUpdateInstaller()
$installer.Updates = $toInstall
$ir = $installer.Install()
Write-Output (""INSTALL ResultCode=$($ir.ResultCode) RebootRequired=$($ir.RebootRequired)"")
exit $(if ($ir.ResultCode -eq 2 -or $ir.ResultCode -eq 3) {{ 0 }} else {{ 1 }})
";

        var temp = Path.Combine(Path.GetTempPath(), $"exo-wu-chipset-{Guid.NewGuid():N}.ps1");
        Process? p = null;
        try
        {
            await File.WriteAllTextAsync(temp, script, ct).ConfigureAwait(false);
            // Elevate
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            p = Process.Start(psi);
            if (p is null) return (false, "Could not start elevated Windows Update install.", 0);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(InstallBudget);
            try
            {
                await p.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return (false, "Stopped — no further chipset packages were installed.", 0);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return (false, $"Windows Update chipset install timed out after {InstallBudget.TotalMinutes:0} minutes.", 0);
            }
            // Verb=runas cannot redirect stdout easily; re-search to see what remains.
            var (left, _) = await SearchAsync(vendor, ct).ConfigureAwait(false);
            var installed = Math.Max(0, drivers.Count - left.Count);
            if (p.ExitCode == 0 || installed > 0)
            {
                return (true,
                    installed > 0
                        ? $"Installed {installed} platform driver package(s) from Windows Update. Reboot if devices still show warnings."
                        : "Windows Update install finished. Reboot if prompted.",
                    installed > 0 ? installed : drivers.Count);
            }
            return (false, $"Windows Update install exited {p.ExitCode}.", 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return (false, "Stopped — no further chipset packages were installed.", 0);
        }
        catch (Exception ex)
        {
            // User cancelled UAC
            if (ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("740"))
                return (false, "Administrator approval was declined — nothing was installed.", 0);
            return (false, "Windows Update install failed: " + ex.Message, 0);
        }
        finally
        {
            try { p?.Dispose(); } catch { }
            try { File.Delete(temp); } catch { }
        }
    }
}
