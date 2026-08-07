using System.Text.Json;

using System.Text.Json.Serialization;

using Exo.Helpers;

using Exo.Models;

using Exo.ViewModels;

using Microsoft.UI.Dispatching;

using Microsoft.Web.WebView2.Core;



namespace Exo.Services;



/// <summary>

/// JSON-RPC bridge between the React UI (WebView2) and native optimizer services.

/// UI owns pixels; this host owns elevation, scripts, and live machine reads.

/// </summary>

public sealed partial class WebHostBridge

{

    private async Task<object> PeekUpdatesAsync()

    {

        try

        {

            var check = await _services.Updater

                .CheckAppUpdateAsync()

                .ConfigureAwait(true);

            return new

            {

                updateAvailable = check.UpdateAvailable,

                message = check.Message,

                alreadyLatest = check.AlreadyLatest,

                localVersion = check.LocalVersion,

                remoteVersion = check.RemoteVersion,

                releaseSummary = check.ReleaseSummary

            };

        }

        catch (Exception ex)

        {

            // Offline / rate-limited: the brain just doesn't ask this launch.

            return new

            {

                updateAvailable = false,

                message = ex.Message,

                alreadyLatest = false,

                localVersion = (string?)null,

                remoteVersion = (string?)null,

                releaseSummary = (string?)null

            };

        }

    }



    private async Task<object> CheckUpdatesAsync()

    {

        string AppVer()

        {

            var v = typeof(App).Assembly.GetName().Version;

            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";

        }



        void PushProgress(string status, double percent) =>

            PostEvent("settings.updateProgress", new { status, percent });



        try

        {

            var status = new Progress<string>(m => PushProgress(m, -1));

            var detail = new Progress<AppUpdateProgress>(p =>

                PushProgress(p.Status, p.Percent));



            PushProgress("Checking GitHub releases…", -1);

            var check = await _services.Updater

                .CheckAppUpdateAsync(status: status, progress: detail)

                .ConfigureAwait(true);



            if (!check.UpdateAvailable)

            {

                PushProgress(check.Message, check.AlreadyLatest ? 100 : -1);

                return new

                {

                    message = check.Message,

                    updateAvailable = false,

                    alreadyLatest = check.AlreadyLatest,

                    installed = false,

                    shouldExit = false,

                    appVersion = AppVer(),

                    localVersion = check.LocalVersion,

                    remoteVersion = check.RemoteVersion,

                    releaseSummary = check.ReleaseSummary

                };

            }



            // InstallAppUpdateAsync already reports Downloading / Verifying / Installing —

            // do not pre-push a second "Downloading" line (UI showed it twice).

            var install = await _services.Updater

                .InstallAppUpdateAsync(check, status: status, progress: detail)

                .ConfigureAwait(true);



            if (install.ShouldExit)

            {

                PushProgress(install.Message, 100);

                // SFX is waiting on our PID (/waitpid) — exit quickly so it can replace the app folder.

                _ = Task.Run(async () =>

                {

                    try { await Task.Delay(250).ConfigureAwait(false); } catch { }

                    try

                    {

                        _queue.TryEnqueue(() =>

                        {

                            try { Microsoft.UI.Xaml.Application.Current?.Exit(); } catch { }

                            try { Environment.Exit(0); } catch { }

                        });

                    }

                    catch

                    {

                        try { Environment.Exit(0); } catch { }

                    }

                });

            }

            else

            {

                // Installer never launched or refused — show the real error in Settings.

                PushProgress(install.Message, -1);

            }



            return new

            {

                message = install.Message,

                updateAvailable = true,

                alreadyLatest = false,

                installed = install.ShouldExit,

                shouldExit = install.ShouldExit,

                appVersion = AppVer(),

                localVersion = install.LocalVersion,

                remoteVersion = install.RemoteVersion,

                releaseSummary = check.ReleaseSummary

            };

        }

        catch (Exception ex)

        {

            PushProgress(ex.Message, -1);

            return new

            {

                message = ex.Message,

                updateAvailable = false,

                alreadyLatest = false,

                installed = false,

                shouldExit = false,

                appVersion = AppVer()

            };

        }

    }



    // ── NVIDIA driver (Phase C) ────────────────────────────────────────────────────────────

    // Held between calls so the UI can show a plan, then act on the same one. Not persisted:

    // a stale plan across restarts would point at a driver that may have been superseded, and

    // hotfixes in particular are withdrawn when they are.

    private NvidiaDriverInstaller.InstallPlan? _driverPlan;

    private NvidiaDriverInstaller.PreparedInstall? _driverPrepared;

    private ChipsetDriverInstaller.InstallPlan? _chipsetPlan;

    private ChipsetDriverInstaller.PreparedInstall? _chipsetPrepared;



    private static string CurrentGpuName()

    {

        foreach (var d in GpuTopology.AdapterDescriptions())

            if (d.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)

                || d.Contains("GeForce", StringComparison.OrdinalIgnoreCase))

                return d;

        return "";

    }



}

