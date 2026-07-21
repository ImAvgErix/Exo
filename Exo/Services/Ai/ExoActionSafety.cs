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

    public static bool IsDenied(string toolId) => DeniedToolIds.Contains(toolId);

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

            // Conflict: ISLC vs memory compression — keep first
            if (toolIds.Contains("memory.islc") &&
                action.ToolId.Equals("memory.compressionOff", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"{action.ToolId}: mutually exclusive with memory.islc");
                continue;
            }

            toolIds.Add(action.ToolId);
            kept.Add(action);
        }

        return kept;
    }
}
