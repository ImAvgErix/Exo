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

    private async Task<object> NvidiaDriverCheckAsync()

    {

        var gpu = CurrentGpuName();

        if (gpu.Length == 0)

            return new { ok = false, kind = "Unknown", headline = "No NVIDIA GPU found.", reasons = Array.Empty<string>() };



        var current = NativeLiveDetect.InstalledNvidiaDriverVersion() ?? "";

        var plan = await NvidiaDriverInstaller.CheckAsync(gpu, current).ConfigureAwait(true);

        _driverPlan = plan;

        _driverPrepared = null;



        return new

        {

            ok = true,

            kind = plan.Kind.ToString(),

            gpu,

            current = plan.CurrentVersion,

            target = plan.TargetVersion,

            beta = plan.TargetIsBeta,

            headline = plan.Headline,

            reasons = plan.Reasons,

            // Only an upgrade recommendation is actionable. Up-to-date, unsupported and unknown

            // all mean "there is nothing to press".

            canPrepare = plan.Kind is NvidiaDriverInstaller.Recommendation.UpgradeWhql

                or NvidiaDriverInstaller.Recommendation.UpgradeHotfix,

            // Reported by check so the UI can ask about the unpacker before the download

            // rather than after it. Learning about a missing prerequisite at the end of a

            // several-hundred-MB transfer is a design choice, and a bad one.

            sevenZip = NvidiaDriverInstaller.FindSevenZip() is not null,

            canInstallSevenZip = NvidiaDriverInstaller.FindWinget() is not null

        };

    }



    private async Task<object> NvidiaDriverPrepareAsync(JsonElement p, bool hasParams)

    {

        var cancellationToken = BeginRun("nvidia");

        try

        {

            if (_driverPlan is null)

                return new { ok = false, message = "Check for a driver first." };



            // Installing 7-Zip is the one moment Exo puts third-party software on the machine, so

            // permission travels as an argument. Absent it, a missing unpacker is an error.

            var allowSevenZip = hasParams && p.ValueKind == JsonValueKind.Object

                                && p.TryGetProperty("installSevenZip", out var sz)

                                && sz.ValueKind == JsonValueKind.True;



            using var log = new ModuleApplyLog("nvidia-driver");

            var progress = new Progress<string>(m => { log.Line(m); PostEvent("module.progress", new { module = "nvidia", status = m }); });

            var (prepared, message) = await NvidiaDriverInstaller

                .PrepareAsync(_driverPlan, allowSevenZip, progress, cancellationToken).ConfigureAwait(true);

            _driverPrepared = prepared;



            // The component diff goes in the log, not only on screen. Without this the only record

            // of what a driver install actually left out is a UI string the user has already

            // dismissed - so when something unwanted turns up afterwards there is no way to tell

            // whether the strip missed it or it arrived by another route.

            if (prepared is not null)

            {

                log.Line($"package components removed ({prepared.RemovedComponents.Count}): "

                         + (prepared.RemovedComponents.Count == 0 ? "none" : string.Join(", ", prepared.RemovedComponents)));

                log.Line($"package components kept ({prepared.KeptComponents.Count}): "

                         + string.Join(", ", prepared.KeptComponents));

                foreach (var r in prepared.RefusedRemovals) log.Line($"refused to remove — {r}");

                log.Line($"install command: setup.exe {prepared.InstallArguments}");

            }

            log.Finish(prepared is not null, message);



            if (prepared is null) return new { ok = false, message };

            return new

            {

                ok = true,

                message,

                version = prepared.Version,

                removed = prepared.RemovedComponents,

                kept = prepared.KeptComponents,

                refused = prepared.RefusedRemovals,

                command = prepared.InstallArguments,

                token = prepared.Token,

                plan = NvidiaDriverInstaller.DescribePlan(prepared)

            };

        }

        finally

        {

            EndRun("nvidia");

        }

    }



    private async Task<object> NvidiaDriverInstallAsync(JsonElement p, bool hasParams)

    {

        var cancellationToken = BeginRun("nvidia");

        try

        {

            if (_driverPrepared is null)

                return new { ok = false, message = "Prepare a driver first." };



            // Both must arrive from the caller. A UI that only meant to preview cannot supply the

            // token, and one that never asked the user cannot supply the confirmation.

            var token = ReadString(p, hasParams, "token") ?? "";

            var confirmed = hasParams && p.ValueKind == JsonValueKind.Object

                            && p.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;



            using var log = new ModuleApplyLog("nvidia-driver-install");

            var progress = new Progress<string>(m => { log.Line(m); PostEvent("module.progress", new { module = "nvidia", status = m }); });

            var (ok, message) = await NvidiaDriverInstaller

                .ExecuteAsync(_driverPrepared, token, confirmed, progress, cancellationToken).ConfigureAwait(true);

            log.Finish(ok, message);



            if (ok)

            {

                // A clean install resets the driver profile, so the pinned DRS pack is gone. Saying

                // so is the difference between an honest result and a machine quietly de-tuned.

                InvalidateDetectCache("nvidia");

                _driverPrepared = null;

            }

            return new { ok, message, reapplyNeeded = ok };

        }

        finally

        {

            EndRun("nvidia");

        }

    }



    // ── AMD / Intel chipset drivers (three-stage, same consent as NVIDIA) ──────────────────



    private async Task<object> ChipsetDriverCheckAsync()

    {

        using var log = new ModuleApplyLog("chipset-driver-check");

        var plan = await ChipsetDriverInstaller.CheckAsync().ConfigureAwait(true);

        _chipsetPlan = plan;

        _chipsetPrepared = null;

        log.Line($"vendor={plan.Vendor} kind={plan.Kind} current={plan.CurrentVersion} target={plan.TargetVersion}");

        foreach (var r in plan.Reasons) log.Line("reason: " + r);

        log.Finish(true, plan.Headline);

        return new

        {

            ok = plan.Kind is not ChipsetDriverInstaller.Recommendation.Unknown

                 and not ChipsetDriverInstaller.Recommendation.NotApplicable,

            kind = plan.Kind.ToString(),

            vendor = plan.Vendor,

            title = plan.Title,

            current = plan.CurrentVersion,

            target = plan.TargetVersion,

            headline = plan.Headline,

            reasons = plan.Reasons.ToArray(),

            supportUrl = plan.SupportUrl,

            dropFolder = plan.DropFolder,

            localPackage = plan.LocalPackagePath is not null,

            canPrepare = plan.CanPrepare || plan.Kind is ChipsetDriverInstaller.Recommendation.PackageReady

                         || plan.Kind is ChipsetDriverInstaller.Recommendation.UpgradeAvailable,

            canStrip = plan.CanStrip,

            beta = false,

            sevenZip = NvidiaDriverInstaller.FindSevenZip() is not null,

            canInstallSevenZip = NvidiaDriverInstaller.FindWinget() is not null,

        };

    }



    private async Task<object> ChipsetDriverPrepareAsync(JsonElement p, bool hasParams)

    {

        var cancellationToken = BeginRun("chipset");

        try

        {

            if (_chipsetPlan is null)

                return new { ok = false, message = "Run chipset.driverCheck first." };

            var installSevenZip = hasParams && p.ValueKind == JsonValueKind.Object

                                  && p.TryGetProperty("installSevenZip", out var z)

                                  && z.ValueKind == JsonValueKind.True;

            using var log = new ModuleApplyLog("chipset-driver-prepare");

            void Report(string m)

            {

                log.Line(m);

                PostEvent("module.progress", new { module = "chipset", status = m });

            }

            var progress = new Progress<string>(Report);

            var (prepared, message) = await ChipsetDriverInstaller

                .PrepareAsync(_chipsetPlan, installSevenZip, progress, cancellationToken)

                .ConfigureAwait(true);

            _chipsetPrepared = prepared;

            if (prepared is null)

            {

                log.Finish(false, message);

                return new

                {

                    ok = false,

                    message,

                    dropFolder = _chipsetPlan.DropFolder,

                    supportUrl = _chipsetPlan.SupportUrl,

                };

            }

            log.Finish(true, message);

            return new

            {

                ok = true,

                message,

                version = prepared.Version,

                vendor = prepared.Vendor,

                removed = prepared.Removed.ToArray(),

                kept = prepared.Kept.ToArray(),

                token = prepared.Token,

                plan = ChipsetDriverInstaller.DescribePlan(prepared).ToArray(),

            };

        }

        finally

        {

            EndRun("chipset");

        }

    }



    private async Task<object> ChipsetDriverInstallAsync(JsonElement p, bool hasParams)

    {

        var cancellationToken = BeginRun("chipset");

        try

        {

            if (_chipsetPrepared is null)

                return new { ok = false, message = "Prepare a chipset package first." };

            var token = ReadString(p, hasParams, "token") ?? "";

            var confirmed = hasParams && p.ValueKind == JsonValueKind.Object

                            && p.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;

            using var log = new ModuleApplyLog("chipset-driver-install");

            var progress = new Progress<string>(m =>

            {

                log.Line(m);

                PostEvent("module.progress", new { module = "chipset", status = m });

            });

            var (ok, message) = await ChipsetDriverInstaller

                .ExecuteAsync(_chipsetPrepared, token, confirmed, progress, cancellationToken)

                .ConfigureAwait(true);

            log.Finish(ok, message);

            if (ok) _chipsetPrepared = null;

            var rebootRecommended = ok && message.Contains("reboot", StringComparison.OrdinalIgnoreCase);

            return new { ok, message, rebootRecommended, rebootRequired = rebootRecommended };

        }

        finally

        {

            EndRun("chipset");

        }

    }



    private object OpenChipsetDropFolder()

    {

        try

        {

            Directory.CreateDirectory(ChipsetDriverInstaller.DropFolder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo

            {

                FileName = ChipsetDriverInstaller.DropFolder,

                UseShellExecute = true

            });

            return new { ok = true, path = ChipsetDriverInstaller.DropFolder };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message, path = ChipsetDriverInstaller.DropFolder };

        }

    }



    private object OpenChipsetSupport()

    {

        var url = _chipsetPlan?.SupportUrl;

        if (string.IsNullOrWhiteSpace(url))

        {

            var local = ChipsetDriverInstaller.ReadLocal();

            url = local.Spec?.SupportUrl

                  ?? (local.CpuVendor == HardwareInventory.CpuVendor.Amd

                      ? "https://www.amd.com/en/support/download/drivers.html"

                      : "https://www.intel.com/content/www/us/en/download/19347/chipset-inf-utility.html");

        }

        try

        {

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo

            {

                FileName = url,

                UseShellExecute = true

            });

            return new { ok = true, url };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message, url };

        }

    }



    private NvidiaDriverCleaner.SweepPlan? _sweepPlan;



    private object NvidiaSweepCheck()

    {

        using var log = new ModuleApplyLog("nvidia-sweep-check");

        var health = NvidiaDriverHealth.Check();

        var plan = NvidiaDriverCleaner.Plan(health, NvidiaDriverCleaner.CandidateFolders());

        _sweepPlan = plan;



        foreach (var f in health.Findings) log.Line($"finding {f.Id}: {f.Title} — {f.Detail}");

        log.Line($"packages: {string.Join(", ", plan.PackagesToRemove)}");

        log.Line($"folders: {string.Join(", ", plan.FoldersToRemove)}");

        log.Finish(true, health.Headline);



        return new

        {

            ok = true,

            needsSweep = health.NeedsSweep,

            headline = health.Headline,

            findings = health.Findings.Select(f => new { f.Id, f.Title, f.Detail, f.NeedsSweep }).ToArray(),

            packages = plan.PackagesToRemove,

            folders = plan.FoldersToRemove,

            token = plan.Token

        };

    }



    private object NvidiaSweepArm(JsonElement p, bool hasParams)

    {

        if (_sweepPlan is null) return new { ok = false, message = "Check the driver install first." };



        var token = ReadString(p, hasParams, "token") ?? "";

        var confirmed = hasParams && p.ValueKind == JsonValueKind.Object

                        && p.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;



        using var log = new ModuleApplyLog("nvidia-sweep-arm");

        var progress = new Progress<string>(m => { log.Line(m); PostEvent("module.progress", new { module = "nvidia", status = m }); });

        var (ok, message) = NvidiaDriverCleaner.Arm(_sweepPlan, token, confirmed, progress);

        log.Finish(ok, message);

        return new { ok, message, rebootRequired = ok };

    }



}

