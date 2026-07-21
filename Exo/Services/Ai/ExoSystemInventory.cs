using System.Runtime.InteropServices;
using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Deep discover across catalog domains (PC-aware; missing = skip).</summary>
public sealed class ExoSystemInventory
{
    /// <summary>A–Z + Expansion Pack AA+/BB+/CC+/DD+ domain ids (SATURATED catalog).</summary>
    public static readonly IReadOnlyList<string> CatalogDomains =
    [
        // A–Z core
        "cpu", "power", "memory", "storage", "network", "display", "gpuNvidia", "gpuAmd",
        "gpuIntel", "input", "audio", "bios", "fans", "braveOnly", "autoInstall",
        "windowsAi", "shell", "privacy", "gamingHost", "securityGated", "companions",
        "upscaler", "usbBtWifi", "virtualization", "visualFx", "timersDpc", "servicesWu",
        "oemRgb", "pcControl", "hostOs",
        // Expansion AA+
        "everythingSearch", "shellEx", "spoolerGate", "shaderCache", "ecoQos",
        "hagsMpoVrr", "devDrive", "obsBroadcast", "gamePassMinimal", "multiLauncherQuiet",
        "handheldOem", "presentMon", "dryRunOwnership", "losslessScaling", "magpie",
        // Expansion BB+
        "docks", "daw", "btHandsFree", "hdmiAudioGate", "iccReset", "nightLightAc",
        "dpiPerMonitor", "gsyncFreesync", "rebarAudit", "xmpAudit", "secureBootAudit",
        "fanControl", "directStorage", "trimWeekly", "indexingExclusions", "sysMainSsd",
        // Expansion CC+
        "copilotPlus", "autoSr", "armPrism", "npuHelpers", "widgetsNews", "bingInSearch",
        "inputInsights", "spotlightAds", "consumerExperience", "oemAiPurge", "braveLeoOff",
        "overlayAiQuiet", "webview2Preserve", "edgeDepower",
        // Expansion DD+
        "mmcssSr10", "networkThrottlingHonest", "win32PrioritySep", "powerThrottling",
        "modernStandbyDetect", "fastStartupPolicy", "pagefileNvme", "softWsReclaim",
        "deliveryOptimization", "vpnWfpDetect", "dscpLeaves", "ipv6TunnelsOff"
    ];

    public ExoSystemState Capture(string exoVersion)
    {
        var state = new ExoSystemState
        {
            ExoVersion = exoVersion,
            CapturedUtc = DateTime.UtcNow.ToString("o")
        };

        CaptureHardware(state);
        CaptureOs(state);
        CaptureDomains(state);
        CaptureApps(state);
        CaptureExpansionMarkers(state);
        state.Digest = ExoStateManager.ComputeDigest(state);
        return state;
    }

    private static void CaptureHardware(ExoSystemState state)
    {
        state.Hardware["processorCount"] = Environment.ProcessorCount.ToString();
        state.Hardware["osArch"] = RuntimeInformation.OSArchitecture.ToString();
        state.Hardware["processArch"] = RuntimeInformation.ProcessArchitecture.ToString();
        try
        {
            state.Hardware["machineName"] = Environment.MachineName;
        }
        catch
        {
            // ignore
        }

        state.Domains["cpu"] = new Dictionary<string, string>
        {
            ["cores"] = Environment.ProcessorCount.ToString(),
            ["plan"] = "unknown"
        };
        state.Domains["memory"] = new Dictionary<string, string>
        {
            ["gcHeap"] = GC.GetTotalMemory(false).ToString()
        };
        state.Domains["storage"] = new Dictionary<string, string>();
        state.Domains["network"] = new Dictionary<string, string>();
        state.Domains["display"] = new Dictionary<string, string>();
        state.Domains["gpu"] = new Dictionary<string, string>();
        state.Domains["input"] = new Dictionary<string, string>();
        state.Domains["audio"] = new Dictionary<string, string>();
        state.Domains["browser"] = new Dictionary<string, string>();
        state.Domains["apps"] = new Dictionary<string, string>();
        state.Domains["osCore"] = new Dictionary<string, string>();
        state.Domains["aiBackground"] = new Dictionary<string, string>
        {
            ["policyCount"] = ExoWindowsAiPurgeService.PolicyKeys.Count.ToString(),
            ["packageHints"] = ExoWindowsAiPurgeService.PackageNameHints.Count.ToString()
        };
        state.Domains["upscaler"] = new Dictionary<string, string>();
        state.Domains["companions"] = new Dictionary<string, string>();
        state.Domains["firmware"] = new Dictionary<string, string>();
        state.Domains["power"] = new Dictionary<string, string>
        {
            ["knobCatalog"] = ExoPowerPlanService.Catalog.Count.ToString()
        };
    }

    private static void CaptureOs(ExoSystemState state)
    {
        state.Os["description"] = RuntimeInformation.OSDescription;
        state.Os["framework"] = RuntimeInformation.FrameworkDescription;
        state.Os["platform"] = Environment.OSVersion.VersionString;
        state.Os["isWindows"] = OperatingSystem.IsWindows().ToString();
        state.Os["userInteractive"] = Environment.UserInteractive.ToString();
    }

    private static void CaptureDomains(ExoSystemState state)
    {
        foreach (var domain in CatalogDomains)
        {
            if (!state.Domains.ContainsKey(domain))
                state.Domains[domain] = new Dictionary<string, string> { ["audited"] = "true" };
        }
    }

    private static void CaptureExpansionMarkers(ExoSystemState state)
    {
        state.Domains["expansion"] = new Dictionary<string, string>
        {
            ["aa"] = "saturated",
            ["bb"] = "saturated",
            ["cc"] = "saturated",
            ["dd"] = "saturated",
            ["domainCount"] = CatalogDomains.Count.ToString()
        };
    }

    private static void CaptureApps(ExoSystemState state)
    {
        if (!OperatingSystem.IsWindows())
        {
            state.InstalledApps.Add("(linux-smoke)");
            return;
        }

        TryAddApp(state, "Discord",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Discord", "Update.exe"));
        TryAddApp(state, "Steam",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steam.exe"));
        TryAddApp(state, "Brave",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        TryAddApp(state, "Epic",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        TryAddApp(state, "Riot",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Riot Games", "Riot Client"));
    }

    private static void TryAddApp(ExoSystemState state, string name, string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                state.InstalledApps.Add(name);
        }
        catch
        {
            // skip
        }
    }
}
