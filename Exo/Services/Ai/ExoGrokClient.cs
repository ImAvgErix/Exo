using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>xAI Grok 4.5 client — JSON-only structured plans.</summary>
public sealed class ExoGrokClient
{
    public const string DefaultModel = "grok-4-1-fast-reasoning";
    public const string SystemPrompt =
        """
        You are Exo, the optimization intelligence embedded in the Exo application.
        You have deep, fundamental knowledge of how modern PCs and Windows 11 work (kernel scheduler, power management subsystem, networking stack, services, registry, storage I/O, CPU/GPU scheduling, DPC/ISR behavior, peripherals, displays, input devices, audio, browsers, installed software, file system, and their interactions).

        Analyze the full provided system state. Research and identify everything that can be safely and effectively optimized — from surface-level settings to deep kernel, power, networking, and hardware tweaks.
        For every single tweak, explicitly start from a clean slate (reset or normalize relevant settings to known defaults before applying new optimized values) to prevent conflicts with previous settings.
        Produce a prioritized, high-impact plan. Output ONLY valid JSON with this exact structure:
        {
          "analysis": "Detailed understanding of current state and optimization opportunities across the entire PC",
          "plan": [array of prioritized steps with reasoning and clean slate actions],
          "actions": [array of concrete executable actions the app can perform]
        }
        Actions must reference registered tool ids (module.*, hostOs.*, power.*, windows.*, browser.*, upscaler.*, companion.*, gpu.*, registry.*, service.*, files.*, display.*, input.*, audio.*, storage.*, automation.*, firmware.*, search.*, shell.*, print.*, shader.*, process.*, devdrive.*).
        Never propose cookie/session wipes, anti-cheat process kills, raw BIOS flash, IPv6 disable, pagefile disable, WebView2 uninstall, or Explorer replacement.
        """;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public ExoGrokClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<ExoAnalysisResult> AnalyzeAsync(
        string apiKey,
        object statePayload,
        IReadOnlyList<string> toolCatalog,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("xAI API key required for deep analysis.");

        var userContent = JsonSerializer.Serialize(new
        {
            toolCatalog,
            state = statePayload
        }, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = DefaultModel,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userContent }
            }
        }, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"xAI API {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        return ParseAnalysisJson(content);
    }

    public static ExoAnalysisResult ParseAnalysisJson(string content)
    {
        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
                content = content[start..(end + 1)];
        }

        using var parsed = JsonDocument.Parse(content);
        var root = parsed.RootElement;
        var result = new ExoAnalysisResult
        {
            Source = "grok",
            Analysis = root.TryGetProperty("analysis", out var a) ? a.GetString() ?? "" : ""
        };

        if (root.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var step in plan.EnumerateArray())
            {
                result.Plan.Add(new ExoPlanStep
                {
                    Priority = step.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pi) ? pi : ++i,
                    Title = GetStr(step, "title", "step"),
                    Reasoning = GetStr(step, "reasoning", GetStr(step, "reason", "")),
                    CleanSlate = GetStr(step, "cleanSlate", GetStr(step, "clean_slate", "")),
                    Domain = GetStr(step, "domain", "")
                });
            }
        }

        if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var act in actions.EnumerateArray())
            {
                var toolId = GetStr(act, "toolId", GetStr(act, "id", GetStr(act, "tool", "")));
                if (string.IsNullOrWhiteSpace(toolId)) continue;
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (act.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pr.EnumerateObject())
                        parameters[prop.Name] = prop.Value.ToString();
                }

                result.Actions.Add(new ExoToolAction
                {
                    ToolId = toolId,
                    Params = parameters,
                    Reason = GetStr(act, "reason", "")
                });
            }
        }

        return result;
    }

    /// <summary>Local fallback plan when no API key — deterministic Host OS maximize + apps.</summary>
    public static ExoAnalysisResult LocalMaximizePlan(ExoSystemState state)
    {
        var actions = new List<ExoToolAction>
        {
            new() { ToolId = "hostOs.maximize", Reason = "Exo Host OS: AI purge + power + Windows + input + display" },
            new() { ToolId = "browser.braveOnly", Reason = "Brave-only + session-safe Brave Apply" },
            new() { ToolId = "module.internet.apply", Reason = "Internet Golden Path (quality + apply)" },
            new() { ToolId = "module.discord.apply", Reason = "Discord (auto-install if needed)" },
            new() { ToolId = "module.steam.apply", Reason = "Steam (auto-install if needed)" },
            new() { ToolId = "module.riot.apply", Reason = "Riot Client (auto-install if needed)" },
            new() { ToolId = "module.epic.apply", Reason = "Epic Launcher (auto-install if needed)" },
            new() { ToolId = "gpu.control.maximize", Reason = "Exo GPU Control + NVIDIA Apply" },
            new() { ToolId = "upscaler.maximizeSupportedGames", Reason = "Upscaler scan (risk ack)" },
            new() { ToolId = "companion.taskManager.install", Reason = "Exo Task Manager" },
            new() { ToolId = "companion.snip.install", Reason = "Exo Snip" },
            new() { ToolId = "process.ecoQosLaunchers", Reason = "EcoQoS launchers" },
            new() { ToolId = "ownership.dryRun", Reason = "Ownership matrix verify" }
        };

        return new ExoAnalysisResult
        {
            Source = "local",
            Analysis =
                $"Local maximize plan for {state.Hardware.Count} hardware keys, " +
                $"{state.InstalledApps.Count} apps, digest {state.Digest}. " +
                "Add an xAI API key for Grok 4.5 deep whole-PC analysis.",
            Plan =
            [
                new ExoPlanStep
                {
                    Priority = 1,
                    Title = "Host OS + AI purge + power + Windows",
                    CleanSlate = "Normalize power plan and AI policies before apply",
                    Reasoning = "Foundational machine-wide maximize via live native Apply",
                    Domain = "hostOs"
                },
                new ExoPlanStep
                {
                    Priority = 2,
                    Title = "Brave-only + apps + GPU + upscaler",
                    CleanSlate = "Install missing targets; session-safe browser; snapshot GPU/DRS",
                    Reasoning = "High-impact user surfaces with auto-install",
                    Domain = "apps"
                }
            ],
            Actions = actions
        };
    }

    private static string GetStr(JsonElement el, string name, string fallback) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}
