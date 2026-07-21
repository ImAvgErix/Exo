using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Hard-stop denylist + session preservation + conflict matrix.</summary>
public static class ExoActionSafety
{
    private static readonly HashSet<string> DeniedToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel.replaceOs",
        "bios.rawFlash",
        "anticheat.inject",
        "anticheat.kill",
        "anticheat.trim",
        "steam.cefEmptyWorkingSet",
        "network.killWifiPermanent",
        "browser.wipeCookies",
        "browser.wipeLoginData",
        "browser.wipeLocalStorage",
        "browser.sessionOnlyCookies",
        "webview2.uninstall",
        "bitlocker.destroy",
        "bcd.destroy",
        "shell.replaceExplorer",
        "shell.exoAsShell",
        "cred.providerInject",
        "display.cruEdidWrite",
        "inject.specialK",
        "inject.reshadeAuto",
        "driver.verifierEnable",
        "pagefile.disable",
        "ipv6.disable",
        "sysmon.autoInstall"
    };

    private static readonly string[] SessionPathDeny =
    [
        "Cookies", "Login Data", "Local Storage", "Session Storage", "IndexedDB", "Web Data"
    ];

    private static readonly HashSet<string> BrowserToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser.braveOnly",
        "module.brave.apply",
        "files.junkCleanup"
    };

    public static bool IsDenied(string toolId) => DeniedToolIds.Contains(toolId);

    public static bool IsBrowserTool(string toolId) =>
        BrowserToolIds.Contains(toolId) ||
        toolId.StartsWith("browser.", StringComparison.OrdinalIgnoreCase);

    public static bool TouchesSessionStore(string path)
    {
        foreach (var deny in SessionPathDeny)
        {
            if (path.Contains(deny, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<ExoToolAction> FilterActions(
        IEnumerable<ExoToolAction> actions,
        out List<string> rejected)
    {
        rejected = [];
        var kept = new List<ExoToolAction>();
        var toolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ToolId))
            {
                rejected.Add("(empty tool id)");
                continue;
            }

            if (IsDenied(action.ToolId))
            {
                rejected.Add($"{action.ToolId}: denylist");
                continue;
            }

            if (action.Params.TryGetValue("path", out var path) && TouchesSessionStore(path))
            {
                rejected.Add($"{action.ToolId}: session-store path denied");
                continue;
            }

            // Conflict: Brave-only × WebView2 — remove Edge browser only; pin WV2
            if (toolIds.Contains("browser.braveOnly") &&
                action.ToolId.Contains("webview2", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: conflicts with browser.braveOnly (pin WebView2)");
                continue;
            }

            if (action.ToolId.Equals("browser.braveOnly", StringComparison.OrdinalIgnoreCase) &&
                toolIds.Any(id => id.Contains("webview2", StringComparison.OrdinalIgnoreCase)))
            {
                rejected.Add($"{action.ToolId}: conflicts with webview2 tool already in plan");
                continue;
            }

            // Conflict: HAGS×MPO×VRR single Windows preset bundle — no independent thrash
            if (toolIds.Contains("module.windows.apply") &&
                action.ToolId.Equals("display.hagsMpoVrrMatrix", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: HAGS bundle already covered by module.windows.apply");
                continue;
            }

            if (toolIds.Contains("display.hagsMpoVrrMatrix") &&
                action.ToolId.Equals("module.windows.apply", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: HAGS bundle already covered by display.hagsMpoVrrMatrix");
                continue;
            }

            // Conflict: spooler-off × printers present (detectable via params)
            if (action.ToolId.Equals("print.spoolerGate", StringComparison.OrdinalIgnoreCase) &&
                PrintersPresent(action.Params))
            {
                rejected.Add($"{action.ToolId}: printers present — spooler stays");
                continue;
            }

            // Conflict: never enable Reflex and Anti-Lag together
            if (toolIds.Contains("gpu.nvidia.reflex") &&
                action.ToolId.Equals("gpu.amd.antiLag", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: conflicts with nvidia.reflex");
                continue;
            }

            if (toolIds.Contains("gpu.amd.antiLag") &&
                action.ToolId.Equals("gpu.nvidia.reflex", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: conflicts with amd.antiLag");
                continue;
            }

            // Conflict: ISLC × memory compression — mutually exclusive (keep first)
            if (toolIds.Contains("memory.islc") &&
                action.ToolId.Equals("memory.compressionOff", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: mutually exclusive with memory.islc");
                continue;
            }

            if (toolIds.Contains("memory.compressionOff") &&
                action.ToolId.Equals("memory.islc", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: mutually exclusive with memory.compressionOff");
                continue;
            }

            toolIds.Add(action.ToolId);
            kept.Add(action);
        }

        return kept;
    }

    private static bool PrintersPresent(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("printersPresent", out var flag) &&
            (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1"))
            return true;

        if (parameters.TryGetValue("printerCount", out var countRaw) &&
            int.TryParse(countRaw, out var count) &&
            count > 0)
            return true;

        return false;
    }
}
