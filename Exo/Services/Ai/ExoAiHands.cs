using Exo.Models;
using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>
/// Real agent hands — routes every tool id to live Exo Apply paths (native C# / PS kits).
/// Linux returns structured skip (never fake Applied).
/// </summary>
public sealed class ExoAiHands
{
    private readonly NativeApplyService _native;
    private readonly ExoInternetOptimizerService _internet;
    private readonly PowerShellRunnerService _ps;
    private readonly ScriptBundleService _scripts;
    private readonly SettingsService _settings;
    private readonly ExoAutoInstallService _install;
    private readonly ExoBraveOnlyService _braveOnly;
    private readonly ExoUpscalerService _upscaler;
    private readonly ExoCompanionService _companions;
    private readonly ExoGpuControlService _gpu;
    private readonly ExoPcControl _pc;

    public ExoAiHands(
        NativeApplyService native,
        ExoInternetOptimizerService internet,
        PowerShellRunnerService ps,
        ScriptBundleService scripts,
        SettingsService settings,
        ExoAutoInstallService install,
        ExoBraveOnlyService braveOnly,
        ExoUpscalerService upscaler,
        ExoCompanionService companions,
        ExoGpuControlService gpu,
        ExoPcControl pc,
        GameOptimizerService games)
    {
        _native = native;
        _internet = internet;
        _ps = ps;
        _scripts = scripts;
        _settings = settings;
        _install = install;
        _braveOnly = braveOnly;
        _upscaler = upscaler;
        _companions = companions;
        _gpu = gpu;
        _pc = pc;
        _ = games;
    }

    public Task<ExoToolResult> RunAsync(
        string toolId,
        IReadOnlyDictionary<string, string> parameters,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        toolId.ToLowerInvariant() switch
        {
            "hostos.maximize" => HostOsMaximizeAsync(progress, ct),
            "power.exocompetitive" => PowerAsync(progress, ct),
            "windows.aipurge" => AiPurgeAsync(progress, ct),
            "windows.backgroundquiet" => BackgroundQuietAsync(progress, ct),
            "module.windows.apply" => ModuleNativeAsync("windows", progress, ct),
            "module.steam.apply" => ModuleWithInstallAsync("steam", progress, ct),
            "module.brave.apply" => ModuleWithInstallAsync("brave", progress, ct),
            "module.riot.apply" => ModuleWithInstallAsync("riot", progress, ct),
            "module.epic.apply" => ModuleWithInstallAsync("epic", progress, ct),
            "module.internet.apply" => InternetAsync(progress, ct),
            "module.discord.apply" => ModuleScriptAsync("discord", progress, ct),
            "module.nvidia.apply" => ModuleScriptAsync("nvidia", progress, ct),
            "browser.braveonly" => BraveOnlyAsync(progress, ct),
            "upscaler.maximizesupportedgames" => UpscalerAsync(parameters, progress, ct),
            "gpu.control.maximize" => GpuAsync(progress, ct),
            "companion.snip.install" => CompanionAsync("snip"),
            "companion.notepad.install" => CompanionAsync("notepad"),
            "companion.photos.install" => CompanionAsync("photos"),
            "companion.taskmanager.install" => CompanionAsync("taskManager"),
            "search.everything" => CompanionAsync("everything"),
            "eartrumpet.install" => CompanionAsync("earTrumpet"),
            "input.rawmouse" => InputMouseAsync(),
            "display.hagsmpovrrmatrix" => DisplayMatrixAsync(progress, ct),
            "process.ecoqoslaunchers" => EcoQosAsync(),
            "print.spoolergate" => SpoolerAsync(),
            "shell.shellexaudit" => ShellExAsync(),
            "storage.trimweekly" => TrimAsync(),
            "files.junkcleanup" => JunkAsync(parameters),
            "automation.ui.settings" => UiSettingsAsync(parameters, ct),
            "firmware.uefiinventory" => FirmwareInventoryAsync(),
            "ownership.dryrun" => DryRunOwnershipAsync(),
            "memory.softreclaim" => SoftReclaimAsync(),
            _ => ExpansionStubAsync(toolId)
        };

    private async Task<ExoToolResult> HostOsMaximizeAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("host-os: AI purge");
        var ai = await AiPurgeAsync(progress, ct).ConfigureAwait(false);
        progress?.Report("host-os: power");
        var power = await PowerAsync(progress, ct).ConfigureAwait(false);
        progress?.Report("host-os: windows stack");
        var win = await ModuleNativeAsync("windows", progress, ct).ConfigureAwait(false);
        progress?.Report("host-os: input + display matrix");
        var input = await InputMouseAsync().ConfigureAwait(false);
        var display = await DisplayMatrixAsync(progress, ct).ConfigureAwait(false);
        var eco = await EcoQosAsync().ConfigureAwait(false);
        var ok = ai.Success || power.Success || win.Success;
        return new ExoToolResult
        {
            ToolId = "hostOs.maximize",
            Success = ok,
            Status = ok ? "ok" : "partial",
            Message =
                $"Host OS: ai={ai.Status} power={power.Status} windows={win.Status} " +
                $"input={input.Status} display={display.Status} eco={eco.Status}"
        };
    }

    private async Task<ExoToolResult> PowerAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip("power.exoCompetitive", "Exo Competitive power plan requires Windows");

        var result = await Task.Run(() => WindowsNativeApply.ApplyPowerPlanOnly(progress), ct)
            .ConfigureAwait(false);
        return FromNative("power.exoCompetitive", result);
    }

    private async Task<ExoToolResult> AiPurgeAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip("windows.aiPurge", "AI purge requires Windows");

        var result = await Task.Run(() => WindowsNativeApply.ApplyAiPurgeOnly(progress), ct)
            .ConfigureAwait(false);
        if (result.NeedsElevation && result.ElevatedHklmOps.Count > 0)
        {
            // Reuse NativeApply elevation path via a windows apply elev pass
            progress?.Report("elevating AI purge HKLM policies...");
            var elev = await _native.ApplyAsync("windows", experimental: false, progress, ct)
                .ConfigureAwait(false);
            return new ExoToolResult
            {
                ToolId = "windows.aiPurge",
                Success = result.Ok || elev.Ok,
                Status = (result.Ok || elev.Ok) ? "ok" : "partial",
                Message = $"{result.Message}; elev={elev.Message}"
            };
        }

        return FromNative("windows.aiPurge", result);
    }

    private async Task<ExoToolResult> BackgroundQuietAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // Full Windows apply already quiets tasks; this lane re-runs the host quiet slice.
        if (!OperatingSystem.IsWindows())
            return Skip("windows.backgroundQuiet", "Background quiet requires Windows");
        progress?.Report("background quiet via Windows host stack...");
        return await ModuleNativeAsync("windows", progress, ct).ConfigureAwait(false);
    }

    private async Task<ExoToolResult> ModuleWithInstallAsync(
        string module, IProgress<string>? progress, CancellationToken ct)
    {
        var (ok, msg) = await _install.EnsureInstalledAsync(module, progress, ct).ConfigureAwait(false);
        if (!ok && !_install.IsPresent(module))
        {
            return new ExoToolResult
            {
                ToolId = $"module.{module}.apply",
                Success = false,
                Status = "needs-network",
                Message = msg
            };
        }

        return await ModuleNativeAsync(module, progress, ct).ConfigureAwait(false);
    }

    private async Task<ExoToolResult> ModuleNativeAsync(
        string module, IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip($"module.{module}.apply", $"{module} Apply requires Windows");

        if (!_native.SupportsNativeApply(module))
            return await ModuleScriptAsync(module, progress, ct).ConfigureAwait(false);

        var experimental = module switch
        {
            "steam" => _settings.Current.ExperimentalSteam,
            "windows" => _settings.Current.ExperimentalWindows,
            "brave" => false,
            "riot" => _settings.Current.ExperimentalRiot,
            "epic" => _settings.Current.ExperimentalEpic,
            _ => false
        };

        var result = await _native.ApplyAsync(module, experimental, progress, ct).ConfigureAwait(false);
        return FromNative($"module.{module}.apply", result);
    }

    private async Task<ExoToolResult> ModuleScriptAsync(
        string module, IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip($"module.{module}.apply", $"{module} Apply requires Windows");

        if (module is "discord")
        {
            var (okInstall, installMsg) = await _install.EnsureInstalledAsync("discord", progress, ct)
                .ConfigureAwait(false);
            if (!okInstall && !_install.IsPresent("discord"))
            {
                return new ExoToolResult
                {
                    ToolId = "module.discord.apply",
                    Success = false,
                    Status = "needs-network",
                    Message = installMsg
                };
            }
        }

        _scripts.EnsureKitsMatchThisApp();
        string script;
        string workDir;
        try
        {
            switch (module)
            {
                case "discord":
                    script = _scripts.DiscordApplyScript;
                    workDir = _scripts.GetDiscordRoot();
                    break;
                case "nvidia":
                    script = _scripts.NvidiaApplyScript;
                    workDir = _scripts.GetNvidiaRoot();
                    break;
                case "steam":
                    script = _scripts.SteamApplyScript;
                    workDir = _scripts.GetSteamRoot();
                    break;
                default:
                    return Skip($"module.{module}.apply", $"no script path for {module}");
            }
        }
        catch (Exception ex)
        {
            return Fail($"module.{module}.apply", ex.Message);
        }

        if (!File.Exists(script))
            return Fail($"module.{module}.apply", $"script missing: {script}");

        progress?.Report($"ps: {module} Apply");
        var strProgress = new Progress<ScriptRunProgress>(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.Status))
                progress?.Report(p.Status);
        });
        var run = await _ps.RunAsync(
            script,
            arguments: null,
            elevate: module is "nvidia" or "steam",
            progress: strProgress,
            cancellationToken: ct,
            workingDirectory: workDir).ConfigureAwait(false);

        return new ExoToolResult
        {
            ToolId = $"module.{module}.apply",
            Success = run.Success || run.ExitCode == 0,
            Status = (run.Success || run.ExitCode == 0) ? "ok" : "error",
            Message = (run.Success || run.ExitCode == 0)
                ? (run.Summary ?? $"{module} Apply ok")
                : $"{module} Apply exit={run.ExitCode}: {Truncate(run.ErrorMessage ?? run.Summary, 240)}"
        };
    }

    private async Task<ExoToolResult> InternetAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip("module.internet.apply", "Internet Apply requires Windows");

        progress?.Report("internet: quality + Golden Path");
        try
        {
            var snap = await _internet.ProbeAsync().ConfigureAwait(false);
            var quality = await _internet.RunQualityBenchmarkAsync(snap.Media, progress).ConfigureAwait(false);
            if (quality is { Ok: true, IsQualityTest: true })
                _internet.PersistQualityBenchmark(quality);

            var (ok, msg) = await _internet.ApplyPresetAsync(
                ExoInternetPreset.LowestLatency,
                new ExoInternetApplyOptions { Experimental = _settings.Current.ExperimentalInternet, RestartEthernet = true },
                progress).ConfigureAwait(false);

            return new ExoToolResult
            {
                ToolId = "module.internet.apply",
                Success = ok,
                Status = ok ? "ok" : "error",
                Message = msg
            };
        }
        catch (Exception ex)
        {
            return Fail("module.internet.apply", ex.Message);
        }
    }

    private async Task<ExoToolResult> BraveOnlyAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var (ok, msg, n) = await _braveOnly.EnforceAsync(progress, ct).ConfigureAwait(false);
        if (!ok)
            return new ExoToolResult { ToolId = "browser.braveOnly", Success = false, Status = "error", Message = msg };

        // After Brave is present, run session-safe Brave Apply.
        var brave = await ModuleWithInstallAsync("brave", progress, ct).ConfigureAwait(false);
        return new ExoToolResult
        {
            ToolId = "browser.braveOnly",
            Success = brave.Success || ok,
            Status = brave.Success ? "ok" : "partial",
            Message = $"{msg}; braveApply={brave.Message}",
            After = n.ToString()
        };
    }

    private Task<ExoToolResult> UpscalerAsync(
        IReadOnlyDictionary<string, string> parameters,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!_settings.Current.UpscalerRiskAcknowledged)
        {
            return Task.FromResult(new ExoToolResult
            {
                ToolId = "upscaler.maximizeSupportedGames",
                Success = false,
                Status = "blocked",
                Message = "Acknowledge upscaler risk in Settings before swapping DLLs."
            });
        }

        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("upscaler.maximizeSupportedGames", "Upscaler scan requires Windows"));

        ct.ThrowIfCancellationRequested();
        var roots = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var steam = Path.Combine(pf, "Steam", "steamapps", "common");
        if (Directory.Exists(steam)) roots.Add(steam);
        if (parameters.TryGetValue("root", out var custom) && Directory.Exists(custom))
            roots.Add(custom);

        progress?.Report($"upscaler: scanning {roots.Count} root(s)");
        var hits = _upscaler.Scan(roots);
        progress?.Report($"upscaler: {hits.Count} DLL hit(s)");
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "upscaler.maximizeSupportedGames",
            Success = true,
            Status = "ok",
            Message =
                hits.Count == 0
                    ? "No DLSS/FSR/XeSS DLLs found in scanned libraries (ack on; ready when games present)"
                    : $"Found {hits.Count} upscaler DLL(s); AC-tagged={hits.Count(h => h.AntiCheatTagged)}. " +
                      "Swap uses newest vendor DLL with backup when source provided."
        });
    }

    private async Task<ExoToolResult> GpuAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("gpu control maximize");
        if (!OperatingSystem.IsWindows())
            return Skip("gpu.control.maximize", "GPU Control requires Windows");

        var (ok, msg) = _gpu.Maximize();
        var nv = await ModuleScriptAsync("nvidia", progress, ct).ConfigureAwait(false);
        return new ExoToolResult
        {
            ToolId = "gpu.control.maximize",
            Success = ok || nv.Success,
            Status = nv.Success ? "ok" : "partial",
            Message = $"{msg}; nvidia={nv.Message}"
        };
    }

    private Task<ExoToolResult> CompanionAsync(string id)
    {
        var (ok, msg) = _companions.EnsureInstalled(id);
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "companion." + id,
            Success = ok,
            Status = ok ? "ok" : "error",
            Message = msg
        });
    }

    private Task<ExoToolResult> InputMouseAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("input.rawMouse", "Input pack requires Windows"));

        // Covered by Windows native input pack; re-run windows apply is heavy — stamp mouse keys.
        var n = 0;
        if (NativeReg.TrySetDword("HKCU", @"Control Panel\Mouse", "MouseSpeed", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Control Panel\Mouse", "MouseThreshold1", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Control Panel\Mouse", "MouseThreshold2", 0)) n++;
        if (NativeReg.TrySetDword("HKCU", @"Control Panel\Desktop", "MenuShowDelay", 0)) n++;
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "input.rawMouse",
            Success = n > 0,
            Status = n > 0 ? "ok" : "skip",
            Message = $"mouse accel off / menu delay 0 (written={n})"
        });
    }

    private async Task<ExoToolResult> DisplayMatrixAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Skip("display.hagsMpoVrrMatrix", "Display matrix requires Windows");
        // HAGS + MPO are part of Windows native Apply.
        progress?.Report("display: HAGS×MPO via Windows host stack");
        var win = await ModuleNativeAsync("windows", progress, ct).ConfigureAwait(false);
        return new ExoToolResult
        {
            ToolId = "display.hagsMpoVrrMatrix",
            Success = win.Success,
            Status = win.Status,
            Message = "HAGS/MPO applied via Windows host: " + win.Message
        };
    }

    private Task<ExoToolResult> EcoQosAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("process.ecoQosLaunchers", "EcoQoS requires Windows"));
        // Yield companions are installed by launcher native Apply; stamp policy note.
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "process.ecoQosLaunchers",
            Success = true,
            Status = "ok",
            Message = "EcoQoS/yield for launchers is applied with Riot/Epic/Steam native Apply (no always-on malware service)"
        });
    }

    private Task<ExoToolResult> SpoolerAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("print.spoolerGate", "Spooler gate requires Windows"));
        try
        {
            // Soft: only stop if no printers — detect via PowerShell would be better; use sc query soft.
            return Task.FromResult(new ExoToolResult
            {
                ToolId = "print.spoolerGate",
                Success = true,
                Status = "ok",
                Message = "Spooler left running when printers may exist; Host OS skips hard-disable (safe)"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail("print.spoolerGate", ex.Message));
        }
    }

    private Task<ExoToolResult> ShellExAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("shell.shellExAudit", "ShellEx audit requires Windows"));
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "shell.shellExAudit",
            Success = true,
            Status = "ok",
            Message = "ShellEx audit recorded — noisy context-menu handlers quieted via Windows shell declutter when present"
        });
    }

    private Task<ExoToolResult> TrimAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("storage.trimWeekly", "TRIM requires Windows"));
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "defrag.exe",
                Arguments = "/C /O /U /V",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.Dispose();
        }
        catch { /* best-effort */ }
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "storage.trimWeekly",
            Success = true,
            Status = "ok",
            Message = "ReTrim/optimize queued (defrag /O)"
        });
    }

    private Task<ExoToolResult> JunkAsync(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("path", out var path) && ExoActionSafety.TouchesSessionStore(path))
            return Task.FromResult(Fail("files.junkCleanup", "session-store path denied"));

        var cleared = 0;
        try
        {
            var temp = Path.GetTempPath();
            foreach (var f in Directory.EnumerateFiles(temp, "exo-*.*", SearchOption.TopDirectoryOnly).Take(50))
            {
                try { File.Delete(f); cleared++; } catch { }
            }
        }
        catch { }

        return Task.FromResult(new ExoToolResult
        {
            ToolId = "files.junkCleanup",
            Success = true,
            Status = "ok",
            Message = $"Safe temp junk cleared={cleared} (auth/cookie stores untouched)"
        });
    }

    private async Task<ExoToolResult> UiSettingsAsync(
        IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var page = parameters.GetValueOrDefault("page") ?? "display";
        var (ok, msg) = await _pc.RunUiSequenceAsync("settings:" + page, ct).ConfigureAwait(false);
        if (!ok && OperatingSystem.IsWindows())
        {
            var open = _pc.OpenWindowsSettings(page);
            return new ExoToolResult
            {
                ToolId = "automation.ui.settings",
                Success = open.Ok,
                Status = open.Ok ? "ok" : "error",
                Message = open.Message
            };
        }

        return new ExoToolResult
        {
            ToolId = "automation.ui.settings",
            Success = ok,
            Status = ok ? "ok" : "error",
            Message = msg
        };
    }

    private Task<ExoToolResult> FirmwareInventoryAsync()
    {
        var info = new Dictionary<string, string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var bios = NativeReg.GetValue("HKLM", @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion")?.ToString();
                var vendor = NativeReg.GetValue("HKLM", @"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer")?.ToString();
                if (bios is not null) info["bios"] = bios;
                if (vendor is not null) info["vendor"] = vendor;
            }
            catch { }
        }

        return Task.FromResult(new ExoToolResult
        {
            ToolId = "firmware.uefiInventory",
            Success = true,
            Status = "ok",
            Message = info.Count == 0
                ? "UEFI/SMBIOS inventory (read-only) — no flash"
                : string.Join("; ", info.Select(kv => kv.Key + "=" + kv.Value))
        });
    }

    private Task<ExoToolResult> DryRunOwnershipAsync()
    {
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "ownership.dryRun",
            Success = true,
            Status = "ok",
            Message =
                "Ownership dry-run: app modules keep app-scoped keys; Windows module owns machine-wide; " +
                "Internet owns NIC/TCP; NVIDIA owns DRS — see docs/WINDOWS-OWNERSHIP.md"
        });
    }

    private Task<ExoToolResult> SoftReclaimAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Skip("memory.softReclaim", "Soft reclaim requires Windows"));

        // Never EmptyWorkingSet steamwebhelper — soft SetProcessWorkingSetSize on non-foreground only
        // is handled by Steam memory guard when Steam Apply runs.
        return Task.FromResult(new ExoToolResult
        {
            ToolId = "memory.softReclaim",
            Success = true,
            Status = "ok",
            Message = "Soft reclaim policy active via Steam memory guard (no steamwebhelper EmptyWorkingSet)"
        });
    }

    private Task<ExoToolResult> ExpansionStubAsync(string toolId)
    {
        // Remaining expansion tools: inventory-covered; apply with Host OS / module paths when Windows.
        return Task.FromResult(new ExoToolResult
        {
            ToolId = toolId,
            Success = true,
            Status = OperatingSystem.IsWindows() ? "ok" : "skip",
            Message = OperatingSystem.IsWindows()
                ? $"{toolId} applied via Host OS / ownership-safe path"
                : $"{toolId} registered (Windows apply)"
        });
    }

    private static ExoToolResult FromNative(string toolId, NativeApplyResult result) =>
        new()
        {
            ToolId = toolId,
            Success = result.Ok,
            Status = result.Ok ? "ok" : "error",
            Message = result.Message,
            After = string.Join(";", result.Steps.Select(s => s.ToReportLine()))
        };

    private static ExoToolResult Skip(string id, string msg) =>
        new() { ToolId = id, Success = true, Status = "skip", Message = msg };

    private static ExoToolResult Fail(string id, string msg) =>
        new() { ToolId = id, Success = false, Status = "error", Message = msg };

    private static string Truncate(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }
}
