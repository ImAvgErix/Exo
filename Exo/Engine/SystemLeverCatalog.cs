namespace Exo.Engine;

/// <summary>
/// One machine-wide registry lever owned by the System module, with the value Exo
/// wants and the evidence-backed reason it wants it. This is the single source of
/// truth: the System module's apply/restore/detect flows consume these definitions,
/// and the tweak catalog exposes each lever as a full-lifecycle adapter.
/// </summary>
public sealed record SystemLeverDefinition(
    string Id,
    string Title,
    string Hive,
    string Path,
    string Name,
    int Value,
    string Why,
    bool NeedsReboot = false);

/// <summary>
/// The System module's registry levers as catalog data. Values here are evidence-
/// backed and locked by Engine.Smoke: forbidden folklore sentinels (for example
/// NetworkThrottlingIndex=0xFFFFFFFF) are rejected by contract, not by a text scan.
/// </summary>
public static class SystemLeverCatalog
{
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string FileSystem = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string MmcssProfile =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string PowerThrottling = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    private const string GameConfigStore = @"System\GameConfigStore";
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GameDvrUser = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameDvrPolicy = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
    private const string ContentDelivery =
        @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerAdvanced =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static IReadOnlyList<SystemLeverDefinition> Levers { get; } =
    [
        // Hardware-accelerated GPU scheduling. Takes effect on reboot, and a driver that does
        // not support it simply ignores the value — which is why the row says "pending reboot"
        // rather than claiming a win the moment it is written.
        new("hags", "GPU scheduling (HAGS)", "HKLM", GraphicsDrivers, "HwSchMode", 2,
            "Hardware-accelerated GPU scheduling on.", NeedsReboot: true),

        // Game Mode. Genuinely useful on Windows 11 — it holds scheduler and driver priority
        // for the foreground game.
        new("game-mode", "Game Mode", "HKCU", GameBar, "AutoGameModeEnabled", 1,
            "Game Mode on."),

        // Game Bar chrome off — widgets, tips, and the Win+G overlay surface steal focus and
        // GPU while gaming. Capture stays off via the DVR keys below; this is the UI noise.
        new("gamebar-nexus", "Game Bar widget", "HKCU", GameBar, "UseNexusForGameBarEnabled", 0,
            "Game Bar widget / nexus off."),
        new("gamebar-startup", "Game Bar startup tip", "HKCU", GameBar, "ShowStartupPanel", 0,
            "Game Bar startup panel off."),
        new("gamebar-tip-index", "Game Bar tip index", "HKCU", GameBar, "GamePanelStartupTipIndex", 3,
            "Game Bar tip panel suppressed."),

        // Background recording. Capture keys Windows actually reads for DVR / clips.
        new("game-dvr-user", "Background recording", "HKCU", GameDvrUser, "AppCaptureEnabled", 0,
            "Background game recording off."),
        new("game-dvr-historical", "Historical capture", "HKCU", GameDvrUser, "HistoricalCaptureEnabled", 0,
            "Instant replay / historical capture off."),
        new("game-dvr-store", "Game DVR", "HKCU", GameConfigStore, "GameDVR_Enabled", 0,
            "Game DVR off in the game config store."),
        new("game-dvr-policy", "Game DVR policy", "HKLM", GameDvrPolicy, "AllowGameDVR", 0,
            "Game DVR pinned off by policy."),

        // Windows consumer noise — tips, suggestions, lock-screen ads. Declutter only;
        // never Defender / Update policy.
        new("tips-disabled", "Windows tips", "HKCU", ContentDelivery, "SubscribedContent-338389Enabled", 0,
            "Suggested tips / fun facts off."),
        new("soft-landing", "Soft landing tips", "HKCU", ContentDelivery, "SoftLandingEnabled", 0,
            "First-logon soft-landing tips off."),
        new("silent-install", "Silent app installs", "HKCU", ContentDelivery, "SilentInstalledAppsEnabled", 0,
            "Silent Store app installs off."),
        new("content-delivery", "Content delivery", "HKCU", ContentDelivery, "SystemPaneSuggestionsEnabled", 0,
            "Start suggestions off."),
        new("show-sync-provider", "Sync provider notifications", "HKCU", ExplorerAdvanced, "ShowSyncProviderNotifications", 0,
            "Explorer sync provider toasts off."),

        // MMCSS. SystemResponsiveness is the share of CPU reserved for background work;
        // Windows ships 20, and 10 is the long-standing value for a machine whose foreground
        // task is a game. Zero is not used here — starving background work entirely causes
        // audio dropouts, which is the opposite of the point.
        new("mmcss-responsiveness", "Multimedia scheduler", "HKLM", MmcssProfile, "SystemResponsiveness", 10,
            "Multimedia scheduler weighted toward the foreground game."),

        // 10, the OS default — NOT 0xFFFFFFFF. The disabled sentinel (-1) shipped once in
        // 4.4.0 and put the repo in contradiction with itself in five places at once; it also
        // made Steam and System fight over the key with no exit. 10 is what everything else
        // agrees on. Engine.Smoke refuses the forbidden sentinels by contract.
        new("mmcss-net-throttle", "Network throttling", "HKLM", MmcssProfile, "NetworkThrottlingIndex", 10,
            "Network throttling at the OS default (10)."),

        // EcoQoS off for foreground work. Written once by Steam's host-latency restamp with
        // no snapshot, no detect row and no repair entry; owning it here gives it the same
        // snapshot/detect/repair treatment as every other lever.
        new("power-throttling-off", "CPU power throttling", "HKLM", PowerThrottling, "PowerThrottlingOff", 1,
            "EcoQoS background throttling off."),

        // Fullscreen Optimizations, left ON (0 is the Windows default, i.e. FSO enabled).
        // A de-tweak: on an untouched machine writing 0 changes nothing observable, but it
        // undoes guide-driven "disable fullscreen optimizations" (2), which gives up the
        // flip-model present path — worse frame pacing and higher latency, broken alt-tab.
        new("fso-behavior", "Fullscreen optimizations", "HKCU", GameConfigStore, "GameDVR_FSEBehavior", 0,
            "Fullscreen optimizations left on (flip-model present path)."),
        new("fso-behavior-mode", "Fullscreen optimizations (mode)", "HKCU", GameConfigStore, "GameDVR_FSEBehaviorMode", 0,
            "Fullscreen optimizations mode at the Windows default."),

        // Storage. All three are the registry values fsutil edits, so Exo can set and verify
        // them through the same path as everything else instead of shelling out.
        new("ntfs-last-access", "NTFS last-access stamps", "HKLM", FileSystem, "NtfsDisableLastAccessUpdate", 1,
            "NTFS last-access timestamps off — fewer metadata writes."),
        new("ntfs-8dot3", "8.3 short filenames", "HKLM", FileSystem, "NtfsDisable8dot3NameCreation", 1,
            "8.3 short-name creation off."),
        new("trim", "SSD TRIM", "HKLM", FileSystem, "DisableDeleteNotify", 0,
            "TRIM enabled."),
    ];

    /// <summary>
    /// Builds one full-lifecycle tweak adapter per lever, namespaced under
    /// <c>system.&lt;lever&gt;</c> so the catalog stays globally unique.
    /// </summary>
    public static IEnumerable<RegistryTweakAdapter> BuildAdapters()
    {
        foreach (var lever in Levers)
        {
            yield return new RegistryTweakAdapter(
                new TweakDefinition<int>(
                    Id: "system." + lever.Id,
                    Title: lever.Title,
                    Description: lever.Why,
                    Risk: TweakRisk.Safe,
                    Reversibility: Reversibility.FullyReversible,
                    RestartRequirement: lever.NeedsReboot
                        ? RestartRequirement.System
                        : RestartRequirement.None,
                    DefaultDesiredValue: lever.Value),
                new WindowsRegistryValueStore(),
                lever.Hive,
                lever.Path,
                lever.Name);
        }
    }
}
