namespace Exo.Engine;

/// <summary>
/// Privacy levers the catalog owns — the same single-source-of-truth pattern as
/// <see cref="SystemLeverCatalog"/>, scoped to telemetry, advertising, activity,
/// and per-feature consent switches. Every lever is a documented Windows setting
/// with the value Exo wants and the reason it wants it; none of them delete user
/// data or touch anti-cheat surfaces.
/// </summary>
public sealed record PrivacyLeverDefinition(
    string Id,
    string Title,
    string Hive,
    string Path,
    string Name,
    int Value,
    string Why,
    bool NeedsReboot = false);

public static class PrivacyLeverCatalog
{
    /// <summary>
    /// Evidence for each lever: the Windows setting it maps to and what the value
    /// changes. Values are the documented "privacy-friendly" positions; absent
    /// values are left absent unless a lever says otherwise.
    /// </summary>
    public static IReadOnlyList<PrivacyLeverDefinition> Levers { get; } =
    [
        // Advertising ID: the per-user id apps use for cross-app advertising.
        // HKCU ...\AdvertisingInfo: Enabled=0 turns the ID off for the user.
        new("advertising-id", "Advertising ID",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0,
            "Advertising ID off — apps stop using the cross-app id."),

        // Targeted ads (tailored experiences): driven by the same AdvertisingInfo
        // toggle for diagnostic-data-based personalisation.
        new("tailored-experiences", "Tailored experiences",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0,
            "Tailored experiences off — no personalised tips from diagnostic data."),

        // Activity history: Windows uploads activity history to the timeline.
        // HKCU ...\ActivityHistory: Enabled=0 stops new activity being collected.
        new("activity-history", "Activity history",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\ActivityHistory", "Enabled", 0,
            "Activity history off — Windows stops collecting new activity."),

        // Online speech recognition: sends speech snippets for Cortana/dictation.
        // HKCU ...\Speech_OneCore\Settings: HasAccepted=0 withdraws consent.
        new("online-speech", "Online speech recognition",
            "HKCU", @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0,
            "Online speech recognition off — dictation stays on-device."),

        // Find My Device: periodic location pings for device recovery.
        // HKLM ...\Sense\ConsentStore\FindMyDevice: DefaultConsent=0 stops sharing.
        new("find-my-device", "Find My Device",
            "HKLM", @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\FindMyDevice", "DefaultConsent", 0,
            "Find My Device off — Windows stops sharing device location."),

        // Location services master switch (user-level).
        // HKCU ...\CapabilityAccessManager\ConsentStore\location: Value=0 denies access.
        new("location", "Location services",
            "HKCU", @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", 0,
            "Location access off for new apps."),

        // Diagnostic data level: the documented policy switch is HKLM
        // ...\DataCollection\AllowTelemetry; 0 = Security (minimum).
        new("telemetry", "Diagnostic data",
            "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0,
            "Diagnostic data at the Security (minimum) level.",
            NeedsReboot: true),

        // Feedback frequency: HKCU ...\Siuf\Rules: NumberOfSIUFInPeriod=0 with
        // PeriodInNanoSeconds=0 stops the periodic feedback prompts.
        new("feedback-prompts", "Feedback prompts",
            "HKCU", @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0,
            "Feedback prompts off — no periodic 'how is Windows working?' dialogs.")
    ];

    public static IEnumerable<RegistryTweakAdapter> BuildAdapters() =>
        Levers.Select(lever => new RegistryTweakAdapter(
            new TweakDefinition<int>(
                "privacy." + lever.Id,
                lever.Title,
                lever.Why,
                TweakRisk.Safe,
                Reversibility.FullyReversible,
                lever.NeedsReboot ? RestartRequirement.System : RestartRequirement.None,
                DefaultDesiredValue: lever.Value),
            new WindowsRegistryValueStore(),
            lever.Hive,
            lever.Path,
            lever.Name));
}
