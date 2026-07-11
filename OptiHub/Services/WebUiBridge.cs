using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

/// <summary>
/// JSON RPC bridge between the WebView2 SPA and OptiHub services.
/// </summary>
public sealed class WebUiBridge
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<string, Task> _postToWeb;
    private readonly Window _window;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public WebUiBridge(
        AppServices services,
        DispatcherQueue dispatcher,
        Window window,
        Func<string, Task> postToWeb)
    {
        _services = services;
        _dispatcher = dispatcher;
        _window = window;
        _postToWeb = postToWeb;
    }

    public async Task HandleMessageAsync(string raw)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var parameters = root.TryGetProperty("params", out var p) ? p : default;

            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(id))
                return;

            object? result = method switch
            {
                "getBootstrap" => await GetBootstrapAsync().ConfigureAwait(false),
                "setTheme" => SetTheme(parameters),
                "setAutoUpdate" => SetAutoUpdate(parameters),
                "openLogs" => OpenLogs(),
                "checkUpdates" => await CheckUpdatesAsync().ConfigureAwait(false),
                "installUpdate" => await InstallUpdateAsync().ConfigureAwait(false),
                "detect" => await DetectAsync(parameters).ConfigureAwait(false),
                "apply" => await ApplyAsync(parameters).ConfigureAwait(false),
                "repair" => await RepairAsync(parameters).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown method: {method}")
            };

            await ReplyAsync(id!, result, error: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(id))
                await ReplyAsync(id!, null, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task ReplyAsync(string id, object? result, string? error)
    {
        var node = new JsonObject
        {
            ["id"] = id
        };
        if (error is not null)
            node["error"] = error;
        else
            node["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, JsonOpts);

        var json = node.ToJsonString();
        await RunOnUiAsync(() => _postToWeb(json)).ConfigureAwait(false);
    }

    public async Task PushProgressAsync(string kit, double percent, string status)
    {
        var node = new JsonObject
        {
            ["type"] = "progress",
            ["kit"] = kit,
            ["percent"] = percent,
            ["status"] = status
        };
        await RunOnUiAsync(() => _postToWeb(node.ToJsonString())).ConfigureAwait(false);
    }

    public async Task PushThemeAsync(string theme)
    {
        var node = new JsonObject
        {
            ["type"] = "theme",
            ["theme"] = theme
        };
        await RunOnUiAsync(() => _postToWeb(node.ToJsonString())).ConfigureAwait(false);
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action().ConfigureAwait(true);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("UI dispatcher unavailable"));
        }
        return tcs.Task;
    }

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("UI dispatcher unavailable"));
        }
        return tcs.Task;
    }

    private Task<object> GetBootstrapAsync()
    {
        var s = _services.Settings.Current;
        var pwsh = PowerShellRunnerService.TryGetPowerShellPath() ?? "PowerShell 7 not found";
        var cards = new object[]
        {
            new { id = "discord", title = "Discord", logo = "logos/discord.png", comingSoon = false },
            new { id = "steam", title = "Steam", logo = "logos/steam.png", comingSoon = false },
            new { id = "nvidia", title = "NVIDIA", logo = "logos/nvidia.png", comingSoon = false },
            new { id = "brave", title = "Brave", logo = "logos/brave.png", comingSoon = true },
            new { id = "riot", title = "Riot", logo = "logos/riot.png", comingSoon = true },
            new { id = "epic", title = "Epic", logo = "logos/epic.png", comingSoon = true },
        };

        var kit =
            $"{_services.Scripts.GetWorkingKitVersion("Discord")} · " +
            $"{_services.Scripts.GetWorkingKitVersion("Steam")} · " +
            $"{_services.Scripts.GetWorkingKitVersion("Nvidia")}";

        var ver = typeof(WebUiBridge).Assembly.GetName().Version;
        var appVer = ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";

        return Task.FromResult<object>(new
        {
            theme = s.Theme,
            appVersion = appVer,
            kitVersion = kit,
            autoUpdate = s.AutoUpdateScripts,
            pwsh,
            cards
        });
    }

    private object SetTheme(JsonElement parameters)
    {
        var theme = parameters.TryGetProperty("theme", out var t) ? t.GetString() : AppSettings.DarkTheme;
        if (!string.Equals(theme, AppSettings.LightTheme, StringComparison.OrdinalIgnoreCase))
            theme = AppSettings.DarkTheme;
        _services.Theme.SetTheme(theme!);
        _services.Theme.Apply();
        return new { ok = true, theme };
    }

    private object SetAutoUpdate(JsonElement parameters)
    {
        var enabled = parameters.TryGetProperty("enabled", out var e) && e.GetBoolean();
        _services.Settings.Update(s => s.AutoUpdateScripts = enabled);
        return new { ok = true };
    }

    private object OpenLogs()
    {
        Directory.CreateDirectory(PathHelper.LogsDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PathHelper.LogsDir,
            UseShellExecute = true
        });
        return new { ok = true };
    }

    private async Task<object> CheckUpdatesAsync()
    {
        var app = await _services.Updater.CheckAppUpdateAsync().ConfigureAwait(false);
        return new
        {
            updateAvailable = app.UpdateAvailable,
            localVersion = app.LocalVersion,
            remoteVersion = app.RemoteVersion,
            message = app.Message
        };
    }

    private async Task<object> InstallUpdateAsync()
    {
        var check = await _services.Updater.CheckAppUpdateAsync().ConfigureAwait(false);
        if (!check.UpdateAvailable)
            return new { success = false, message = check.Message, shouldExit = false };

        var install = await _services.Updater.InstallAppUpdateAsync(check).ConfigureAwait(false);
        if (install.ShouldExit)
        {
            await RunOnUiAsync(async () =>
            {
                await Task.Delay(900).ConfigureAwait(true);
                Application.Current?.Exit();
            }).ConfigureAwait(false);
        }

        return new
        {
            success = install.ShouldExit || install.AlreadyLatest,
            message = install.Message,
            shouldExit = install.ShouldExit
        };
    }

    private async Task<object> DetectAsync(JsonElement parameters)
    {
        var kit = parameters.TryGetProperty("kit", out var k) ? k.GetString() ?? "" : "";
        OptimizerStateInfo state = kit.ToLowerInvariant() switch
        {
            "discord" => await _services.OptimizerState.DetectDiscordAsync().ConfigureAwait(false),
            "steam" => await _services.OptimizerState.DetectSteamAsync().ConfigureAwait(false),
            "nvidia" => await _services.OptimizerState.DetectNvidiaAsync().ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown kit")
        };

        bool? gsync = null;
        if (string.Equals(kit, "nvidia", StringComparison.OrdinalIgnoreCase) &&
            state.Extra is not null &&
            state.Extra.TryGetValue("gsync", out var gStr) &&
            bool.TryParse(gStr, out var parsedGsync))
        {
            gsync = parsedGsync;
        }

        return new
        {
            isApplied = state.IsApplied,
            statusText = state.StatusText,
            detail = state.Detail,
            gsync,
            features = state.Features.Select(f => new
            {
                title = f.Title,
                detail = f.Detail,
                active = f.IsActive
            }).ToList()
        };
    }

    private async Task<object> ApplyAsync(JsonElement parameters)
    {
        var kit = (parameters.TryGetProperty("kit", out var k) ? k.GetString() : "")?.ToLowerInvariant() ?? "";
        var gsync = parameters.TryGetProperty("gsync", out var g) && g.ValueKind == JsonValueKind.True;

        var (script, workDir, args) = kit switch
        {
            "discord" => (
                _services.Scripts.DiscordOptimizerScript,
                _services.Scripts.GetDiscordRoot(),
                new List<string> { "-NonInteractive" }),
            "steam" => (
                _services.Scripts.SteamOptimizerScript,
                _services.Scripts.GetSteamRoot(),
                new List<string> { "-NonInteractive" }),
            "nvidia" => (
                _services.Scripts.NvidiaOptimizerScript,
                _services.Scripts.GetNvidiaRoot(),
                BuildNvidiaArgs(gsync)),
            _ => throw new InvalidOperationException("Unknown kit")
        };

        var progress = new Progress<ScriptRunProgress>(p =>
        {
            _ = PushProgressAsync(kit, p.Percent, p.Status ?? "");
        });

        var result = await _services.PowerShell.RunAsync(
            script,
            arguments: args,
            elevate: true,
            progress: progress,
            cancellationToken: default,
            workingDirectory: workDir).ConfigureAwait(false);

        return new
        {
            success = result.Success,
            message = result.Success
                ? (string.IsNullOrWhiteSpace(result.Summary) ? "Done." : result.Summary)
                : (result.ErrorMessage ?? result.Summary ?? "Failed.")
        };
    }

    private static List<string> BuildNvidiaArgs(bool gsync)
    {
        var args = new List<string> { "-NonInteractive" };
        if (gsync) args.Add("-Gsync");
        return args;
    }

    private async Task<object> RepairAsync(JsonElement parameters)
    {
        var kit = (parameters.TryGetProperty("kit", out var k) ? k.GetString() : "")?.ToLowerInvariant() ?? "";
        var (script, workDir) = kit switch
        {
            "discord" => (_services.Scripts.DiscordRepairScript, _services.Scripts.GetDiscordRoot()),
            "steam" => (_services.Scripts.SteamRepairScript, _services.Scripts.GetSteamRoot()),
            "nvidia" => (_services.Scripts.NvidiaRepairScript, _services.Scripts.GetNvidiaRoot()),
            _ => throw new InvalidOperationException("Unknown kit")
        };

        var result = await _services.PowerShell.RunAsync(
            script,
            arguments: new[] { "-NonInteractive" },
            elevate: true,
            progress: null,
            cancellationToken: default,
            workingDirectory: workDir).ConfigureAwait(false);

        return new
        {
            success = result.Success,
            message = result.Success
                ? (result.Summary ?? "Repair finished.")
                : (result.ErrorMessage ?? result.Summary ?? "Repair failed.")
        };
    }
}
