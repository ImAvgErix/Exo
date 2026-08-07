using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exo.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Brave optimizer — absolute debloat for gaming + privacy (SlimBrave Neo Max class and beyond).
/// Policies (managed) + multi-profile prefs + surgical vault purge + Proton Pass + Windows quiet/GPU.
/// Never wipes history/bookmarks. Closes Brave before profile writes. Full prefs snapshot for Repair.
/// </summary>
public static class BraveNativeApply
{
    public const string ProtonPassExtensionId = "ghmbeldphafepmbegfdlkpapadhbakde";
    private const string PolicyPath = @"SOFTWARE\Policies\BraveSoftware\Brave";
    private const string StateVersion = "brave-native-2.2";

    /// <summary>
    /// Content filter UUIDs from Brave's list_catalog (annoyances / cookie / social / quiet web).
    /// Missing UUIDs on older Brave are skipped when writing regional_filters.
    /// </summary>
    private static readonly string[] ContentFilterUuids =
    {
        "AC023D22-AE88-4060-A978-4FEEEC4221693", // Cookie notice
        "67E792D4-AE03-4D1A-9EDE-80E01C81F9B8", // Annoying distractions
        "690FF3B4-8B6B-4709-8505-FEC6643D7BD9", // Newsletter popups
        "7911A1CB-304E-4CDB-ABB3-E2A94A37E4DD", // Social media
        "2F3DCE16-A19A-493C-A88F-2E110FBD37D6", // Mobile app promo
        "6b91e355-1421-4c03-9a30-911b4d0fb277", // AI suggestions
        "1ED1870B-997C-4BFE-AEBC-B67D679BAF3B", // Chat app
        "78672887-A098-4D2C-B0CB-A3DEC4834DA7", // Paywall
        "9E8EC586-4E17-4E5E-99D7-35172C4CEA74", // YouTube Shorts
        "E2FA7D98-0BD5-493E-8AF4-950604ADE9CB", // Tracking URL
    };

    /// <summary>Web Data tables that hold secrets / form junk — not keywords (search engines).</summary>
    private static readonly string[] VaultTables =
    {
        "autofill", "addresses", "address_type_tokens", "credit_cards", "local_ibans",
        "masked_credit_cards", "server_card_metadata", "payments_customer_data",
        "server_card_cloud_token_data", "offer_data", "offer_eligible_instrument",
        "offer_merchant_domain", "virtual_card_usage_data", "local_stored_cvc", "server_stored_cvc",
        "masked_bank_accounts", "masked_bank_accounts_metadata", "masked_ibans", "masked_ibans_metadata",
        "masked_credit_card_benefits", "benefit_merchant_domains", "generic_payment_instruments",
        "payment_instrument_creation_options", "autofill_ai_attributes", "autofill_ai_entities",
        "autofill_ai_entities_metadata", "loyalty_cards", "loyalty_card_merchant_domain",
        "valuables_metadata", "plus_addresses", "token_service", "secure_payment_confirmation_instrument",
        "secure_payment_confirmation_browser_bound_key", "payment_method_manifest"
    };

    /// <summary>
    /// Curated brave://flags. Format: name@1 = Enabled, name@2 = Disabled.
    /// Names verified against Brave chrome.dll (invalid names are stripped on launch —
    /// that is why a padded "4000 flags" pack only shows ~2 non-Default entries).
    /// Does not touch Windows/Internet host stack (no MMCSS/TCP/NIC).
    /// </summary>
    private static readonly string[] LabsExperiments =
    {
        // ── Performance / RAM / GPU / downloads (verified present) ──
        "enable-parallel-downloading@1",
        "enable-gpu-rasterization@1",
        "enable-zero-copy@1",
        "ignore-gpu-blocklist@1",
        "smooth-scrolling@1",
        "enable-force-dark@1",
        "enable-tab-audio-muting@1",
        "overlay-scrollbars@1",
        "partition-visited-link-database-with-self-links@1",
        "enable-quic@2", // UDP QUIC off — more consistent gaming + less phone-home surface
        "enable-tls13-early-data@2",
        "enable-isolated-web-apps@2",
        "skia-graphite@2", // keep stable D3D path for games/streaming sites
        "enable-vulkan@2",

        // ── Brave product surface off (verified) ──
        "brave-news-peek@2",
        "brave-speedreader@2",
        "brave-ai-chat@2",
        "brave-ai-chat-global-side-panel@2",
        "brave-ai-chat-show-input-on-new-tab-page@2",
        "brave-ai-chat-history@2",
        "brave-ai-first@2",
        "brave-wallet-bitcoin@2",
        "brave-wallet-zcash@2",
        "brave-wallet-cardano@2",
        "brave-wallet-polkadot@2",
        "brave-wallet-enable-ankr-balances@2",
        "brave-wallet-enable-transaction-simulations@2",
        "brave-rewards-allow-self-custody-providers@2",
        "brave-rewards-allow-unsupported-wallet-providers@2",
        "brave-email-aliases@2",
        "brave-ntp-search-widget@2",
        "brave-history-embeddings@2",
        "brave-ultra-dark-theme@1", // AMOLED-adjacent

        // ── Shields / privacy (verified) ──
        "brave-adblock-default-1p-blocking@1",
        "brave-adblock-cosmetic-filtering@1",
        "brave-adblock-csp-rules@1",
        "brave-adblock-cname-uncloaking@1",
        "brave-adblock-cookie-list-default@1",
        "brave-adblock-mobile-notifications-list-default@1",
        "brave-adblock-procedural-filtering@1",
        "brave-adblock-collapse-blocked-elements@1",
        "brave-extension-network-blocking@1",
        "brave-copy-clean-link-by-default@1",
        "brave-global-privacy-control-enabled@1",
        "brave-de-amp@1",
        "brave-debounce@1",
        "brave-domain-block@1",
        // brave-first-party-ephemeral-storage is deliberately NOT here. It makes FIRST-party
        // storage ephemeral, so every site's cookies and localStorage are discarded when the
        // site closes. Together with DefaultBraveRemember1PStorageSetting it logged the user
        // out of every site on every close, which is the single most disruptive thing this
        // module has ever done -- and it directly contradicted the comment three sections down
        // that says "keep first-party cookies so logins/history stay useful".
        "brave-reduce-language@1",
        "brave-block-screen-fingerprinting@1",
        "brave-show-strict-fingerprinting-mode@1",
        "brave-web-bluetooth-api@2",
        "strict-origin-isolation@1",
        "enable-web-bluetooth@2",
        "enable-webrtc-hide-local-ips-with-mdns@1",

        // ── Google/Chrome chrome noise off (verified) ──
        "shopping-list@2",
        "enable-lens-standalone@2",
        "optimization-guide-on-device-model@2",
        "history-journeys@2",
        "ntp-drive-module@2",
    };

    /// <summary>
    /// Managed policies (HKLM Policies\BraveSoftware\Brave + Chromium policies Brave honors).
    /// Brave-owned only — no MMCSS/HAGS/TCP/NIC (Windows/Internet own those).
    /// </summary>
    private static readonly (string Name, object Value, RegistryValueKind Kind)[] PolicyPack =
    {
        // ── Telemetry / phone-home ──
        ("MetricsReportingEnabled", 0, RegistryValueKind.DWord),
        ("CloudReportingEnabled", 0, RegistryValueKind.DWord),
        ("SafeBrowsingExtendedReportingEnabled", 0, RegistryValueKind.DWord),
        ("UrlKeyedAnonymizedDataCollectionEnabled", 0, RegistryValueKind.DWord),
        ("BraveP3AEnabled", 0, RegistryValueKind.DWord),
        ("BraveStatsPingEnabled", 0, RegistryValueKind.DWord),
        ("UserFeedbackAllowed", 0, RegistryValueKind.DWord),
        ("DeviceMetricsReportingEnabled", 0, RegistryValueKind.DWord),
        ("ReportDeviceActivityTimes", 0, RegistryValueKind.DWord),
        ("ReportDeviceNetworkStatus", 0, RegistryValueKind.DWord),
        ("AlternateErrorPagesEnabled", 0, RegistryValueKind.DWord),
        ("SpellCheckServiceEnabled", 0, RegistryValueKind.DWord),
        ("DomainReliabilityAllowed", 0, RegistryValueKind.DWord),
        ("WebRtcEventLogCollectionAllowed", 0, RegistryValueKind.DWord),
        ("ChromeCleanupEnabled", 0, RegistryValueKind.DWord),
        ("ChromeCleanupReportingEnabled", 0, RegistryValueKind.DWord),
        ("ComponentUpdatesEnabled", 1, RegistryValueKind.DWord), // keep security components

        // ── Vault → Proton Pass only ──
        ("AutofillAddressEnabled", 0, RegistryValueKind.DWord),
        ("AutofillCreditCardEnabled", 0, RegistryValueKind.DWord),
        ("PasswordManagerEnabled", 0, RegistryValueKind.DWord),
        ("PasswordLeakDetectionEnabled", 0, RegistryValueKind.DWord),
        ("PasswordSharingEnabled", 0, RegistryValueKind.DWord),
        ("BrowserSignin", 0, RegistryValueKind.DWord),
        ("PromotionalTabsEnabled", 0, RegistryValueKind.DWord),
        ("PaymentMethodQueryEnabled", 0, RegistryValueKind.DWord),
        ("ImportAutofillFormData", 0, RegistryValueKind.DWord),
        ("ImportSavedPasswords", 0, RegistryValueKind.DWord),
        ("ImportSearchEngine", 0, RegistryValueKind.DWord),

        // ── Privacy / WebRTC / cookies / prediction ──
        // Keep first-party cookies so logins/history stay useful; block 3P only.
        ("BraveGlobalPrivacyControlEnabled", 1, RegistryValueKind.DWord),
        ("BraveDeAmpEnabled", 1, RegistryValueKind.DWord),
        ("BraveDebouncingEnabled", 1, RegistryValueKind.DWord),
        ("BraveTrackingQueryParametersFilteringEnabled", 1, RegistryValueKind.DWord),
        ("BraveReduceLanguageEnabled", 1, RegistryValueKind.DWord),
        ("WebRtcIPHandling", "disable_non_proxied_udp", RegistryValueKind.String),
        ("WebRtcAllowLegacyTLSProtocols", 0, RegistryValueKind.DWord),
        ("QuicAllowed", 0, RegistryValueKind.DWord),
        ("BlockThirdPartyCookies", 1, RegistryValueKind.DWord),
        ("DefaultCookiesSetting", 1, RegistryValueKind.DWord), // allow 1P (was 4 session-only — logged people out)
        ("NetworkPredictionOptions", 2, RegistryValueKind.DWord),
        ("DnsOverHttpsMode", "secure", RegistryValueKind.String),
        ("DnsOverHttpsTemplates", "https://cloudflare-dns.com/dns-query", RegistryValueKind.String),
        ("BuiltInDnsClientEnabled", 1, RegistryValueKind.DWord),
        ("WPADQuickCheckEnabled", 0, RegistryValueKind.DWord),
        ("HttpsOnlyMode", "force_enabled", RegistryValueKind.String),
        ("InsecurePrivateNetworkRequestsAllowed", 0, RegistryValueKind.DWord),
        ("SharedClipboardEnabled", 0, RegistryValueKind.DWord),
        ("UserAgentReduction", 1, RegistryValueKind.DWord),
        ("ScrollToTextFragmentEnabled", 0, RegistryValueKind.DWord),
        ("SitePerProcess", 1, RegistryValueKind.DWord),

        // ── Site permissions default-deny noise ──
        ("DefaultNotificationsSetting", 2, RegistryValueKind.DWord),
        ("DefaultGeolocationSetting", 2, RegistryValueKind.DWord),
        ("DefaultSensorsSetting", 2, RegistryValueKind.DWord),
        ("DefaultSerialGuardSetting", 2, RegistryValueKind.DWord),
        ("DefaultWebBluetoothGuardSetting", 2, RegistryValueKind.DWord),
        ("DefaultWebUsbGuardSetting", 2, RegistryValueKind.DWord),
        ("DefaultFileSystemReadGuardSetting", 2, RegistryValueKind.DWord),
        ("DefaultFileSystemWriteGuardSetting", 2, RegistryValueKind.DWord),
        ("DefaultPopupsSetting", 2, RegistryValueKind.DWord),
        ("DefaultWindowPlacementSetting", 2, RegistryValueKind.DWord),
        ("DefaultInsecureContentSetting", 2, RegistryValueKind.DWord),
        ("AutoplayAllowed", 0, RegistryValueKind.DWord),
        ("AudioCaptureAllowed", 1, RegistryValueKind.DWord), // Discord/WebRTC when needed
        ("VideoCaptureAllowed", 1, RegistryValueKind.DWord),
        ("ScreenCaptureAllowed", 1, RegistryValueKind.DWord),
        ("AdsSettingForIntrusiveAdsSites", 2, RegistryValueKind.DWord),

        // ── Privacy Sandbox / ads APIs off ──
        ("PrivacySandboxPromptEnabled", 0, RegistryValueKind.DWord),
        ("PrivacySandboxAdTopicsEnabled", 0, RegistryValueKind.DWord),
        ("PrivacySandboxSiteEnabledAdsEnabled", 0, RegistryValueKind.DWord),
        ("PrivacySandboxAdMeasurementEnabled", 0, RegistryValueKind.DWord),

        // ── Debloat Brave product surface (official Brave policies) ──
        ("BraveRewardsDisabled", 1, RegistryValueKind.DWord),
        ("BraveWalletDisabled", 1, RegistryValueKind.DWord),
        ("BraveVPNDisabled", 1, RegistryValueKind.DWord),
        ("BraveAIChatEnabled", 0, RegistryValueKind.DWord),
        ("BraveNewsDisabled", 1, RegistryValueKind.DWord),
        ("BraveTalkDisabled", 1, RegistryValueKind.DWord),
        ("BravePlaylistEnabled", 0, RegistryValueKind.DWord),
        ("BraveWebDiscoveryEnabled", 0, RegistryValueKind.DWord),
        ("BraveSpeedreaderEnabled", 0, RegistryValueKind.DWord),
        ("TorDisabled", 1, RegistryValueKind.DWord),
        ("SyncDisabled", 1, RegistryValueKind.DWord),
        ("EmailAliasesEnabled", 0, RegistryValueKind.DWord),
        ("IPFSEnabled", 0, RegistryValueKind.DWord),
        ("BraveWaybackMachineEnabled", 0, RegistryValueKind.DWord),

        // ── Shields pins (Brave 1.83+) ──
        ("DefaultBraveAdblockSetting", 2, RegistryValueKind.DWord),
        ("DefaultBraveFingerprintingV2Setting", 3, RegistryValueKind.DWord),
        ("DefaultBraveHttpsUpgradeSetting", 2, RegistryValueKind.DWord),
        ("DefaultBraveReferrersSetting", 2, RegistryValueKind.DWord),
        // 1 = ALLOW = remember first-party storage. This shipped as 2 (BLOCK), which is Brave's
        // "Forget me when I close this site" applied to EVERY site: first-party cookies and
        // localStorage wiped on close, so the user was signed out of everything, every time.
        // Third-party cookies are still blocked above; that is where the tracking lives, and it
        // costs no logins. Debloat never means destroying the user's sessions.
        ("DefaultBraveRemember1PStorageSetting", 1, RegistryValueKind.DWord),

        // ── Performance / quiet chrome ──
        ("BackgroundModeEnabled", 0, RegistryValueKind.DWord),
        ("HardwareAccelerationModeEnabled", 1, RegistryValueKind.DWord),
        ("HighEfficiencyModeEnabled", 1, RegistryValueKind.DWord),
        ("IntensiveWakeUpThrottlingEnabled", 1, RegistryValueKind.DWord),
        ("WindowOcclusionEnabled", 1, RegistryValueKind.DWord),
        ("EnableMediaRouter", 0, RegistryValueKind.DWord),
        ("MediaRouterCastAllowAllIPs", 0, RegistryValueKind.DWord),
        ("ShoppingListEnabled", 0, RegistryValueKind.DWord),
        ("LensCameraAssistedSearchEnabled", 0, RegistryValueKind.DWord),
        ("LiveTranslateEnabled", 0, RegistryValueKind.DWord),
        ("AlwaysOpenPdfExternally", 1, RegistryValueKind.DWord),
        ("TranslateEnabled", 0, RegistryValueKind.DWord),
        ("SpellcheckEnabled", 0, RegistryValueKind.DWord),
        ("SearchSuggestEnabled", 0, RegistryValueKind.DWord),
        ("PrintingEnabled", 0, RegistryValueKind.DWord),
        ("CloudPrintProxyEnabled", 0, RegistryValueKind.DWord),
        ("DefaultBrowserSettingEnabled", 0, RegistryValueKind.DWord),
        ("DeveloperToolsAvailability", 2, RegistryValueKind.DWord),
        ("RemoteDebuggingAllowed", 0, RegistryValueKind.DWord),
        ("ShowHomeButton", 0, RegistryValueKind.DWord),
        // BookmarkBarEnabled is deliberately NOT in this pack. Setting it to 0 force-hides
        // the bookmarks bar everywhere, including the new tab page, and a managed policy
        // overrides the profile pref — so it silently defeated
        // bookmark_bar.show_on_all_tabs = false, which is what actually produces the
        // wanted behaviour: bar on the new tab page only, gone on every other tab.
        ("RestoreOnStartup", 5, RegistryValueKind.DWord), // NTP
        ("HomepageIsNewTabPage", 1, RegistryValueKind.DWord),
        ("PromptForDownloadLocation", 1, RegistryValueKind.DWord),
        ("ShowFullUrlsInAddressBar", 1, RegistryValueKind.DWord),
        ("HideWebStoreIcon", 1, RegistryValueKind.DWord),
        ("NTPCustomBackgroundEnabled", 0, RegistryValueKind.DWord),
        ("AllowDinosaurEasterEgg", 0, RegistryValueKind.DWord),
        ("InstantTetheringAllowed", 0, RegistryValueKind.DWord),
        ("RoamingProfileSupportEnabled", 0, RegistryValueKind.DWord),
        ("SSLErrorOverrideAllowed", 0, RegistryValueKind.DWord),
        ("DisableSafeBrowsingProceedAnyway", 1, RegistryValueKind.DWord),
        ("DefaultSearchProviderEnabled", 1, RegistryValueKind.DWord),
        ("DefaultSearchProviderName", "Brave", RegistryValueKind.String),
        ("DefaultSearchProviderSearchURL",
            "https://search.brave.com/search?q={searchTerms}", RegistryValueKind.String),
        ("DefaultSearchProviderSuggestURL",
            "https://search.brave.com/api/suggest?q={searchTerms}", RegistryValueKind.String),
    };

    public static NativeApplyResult Apply(bool experimental, IProgress<string>? progress = null)
    {
        _ = experimental;
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        void Report(string m) => progress?.Report(m);

        Report("Discovering Brave…");
        var install = Discover();
        if (!install.Installed)
            return NativeApplyResult.Fail("brave", "Brave is not installed on this PC.");

        Report("Closing Brave…");
        steps.Add(CloseBrave());

        Report("Snapshot prefs/Local State for Repair…");
        steps.Add(WriteFullSnapshot(install));

        Report("Managed policies (debloat + shields + privacy)…");
        steps.Add(ApplyPolicies(admin, elevOps));

        // Policies an older Exo wrote and this version no longer sets. Dropping them from
        // the pack stops us writing them; it does not remove one already in HKLM, and a
        // managed policy keeps overriding the profile pref forever. Reported from a real
        // machine after the pack change: the bookmark bar was still force-hidden and
        // Proton Pass still said "blocked by administrator", because both were left behind
        // by a previous apply. Re-applying has to clean up after older builds, not just
        // stop adding to the pile. (Same fix shape as the stale Steam webhelper FSO stamps.)
        Report("Clearing policies from older Exo builds…");
        steps.Add(RemoveRetiredPolicies(admin, elevOps));

        Report("Checking optional Proton Pass extension…");
        steps.Add(EnsureProtonPassPolicy(admin, elevOps));

        Report($"Profile prefs on {install.Profiles.Count} profile(s)…");
        steps.Add(ApplyAllProfilePrefs(install));

        Report("Content filter lists (cookie / annoyances / social)…");
        steps.Add(EnableContentFilters(install));

        // Login Data / vault wipe is default-off — no risk-ack setting exists yet.
        // Safer cache clears below still run; passwords stay intact.
        Report("Vault wipe skipped (default-off; Login Data kept)…");
        steps.Add(new NativeApplyStep
        {
            Id = "vault",
            Status = "skip",
            Reason = "Login Data wipe default-off (no risk-ack setting)"
        });

        Report("GPU high-performance…");
        steps.Add(ApplyGpu(install));

        Report("Quiet Windows startup…");
        steps.Add(QuietStartup());

        Report("Quiet Brave update tasks + services on this PC…");
        steps.Add(QuietUpdateTasks());
        steps.Add(QuietBraveServices());

        Report("Clear safe caches (keep cookies/history/bookmarks)…");
        steps.Add(ClearSafeCaches(install));

        var policyOk = steps.Any(s => s.Id == "policies" && s.Status is "ok" or "pending-elev");
        var vaultOk = steps.Any(s => s.Id == "vault" && s.Status is "ok" or "partial" or "skip");
        var essentialOk = policyOk;

        SaveState(essentialOk, install, steps, elevOps);

        return new NativeApplyResult
        {
            Ok = essentialOk,
            Module = "brave",
            Message = essentialOk
                ? "Brave tuning prepared (privacy + Shields + multi-profile + filters + quiet background; Safe Browsing preserved; vault wipe default-off)"
                : "Brave apply incomplete — accept elevation for full policy pack if prompted",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    public static NativeApplyResult Repair(IProgress<string>? progress = null)
    {
        void Report(string m) => progress?.Report(m);
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        var install = Discover();

        Report("Closing Brave…");
        steps.Add(CloseBrave());

        Report("Restoring prefs / Local State from snapshot…");
        steps.Add(RestoreFullSnapshot(install));

        Report("Restoring GPU preference…");
        steps.Add(RestoreGpu(install));

        Report("Removing Exo Brave managed policies…");
        steps.Add(RemovePolicies(admin, elevOps));

        Report("Removing Proton Pass force-install policy…");
        steps.Add(RemoveExtensionForceList(admin, elevOps));

        steps.Add(new NativeApplyStep
        {
            Id = "vault",
            Status = "skip",
            Reason = "vault wipe was default-off; nothing to undo"
        });

        SaveState(false, install, steps, elevOps);

        // Ok was a literal true, so Repair reported success no matter what the steps said -
        // including the case where every machine-wide policy was still in place because the
        // run was not elevated. The result now comes from the steps, and the ops that need
        // Administrator are handed back so the caller can run them elevated (the same shape
        // Apply already uses) rather than being silently dropped.
        var failed = steps.Where(s => s.Status == "fail").ToList();
        var partial = steps.Where(s => s.Status == "partial").ToList();
        var ok = failed.Count == 0 && (partial.Count == 0 || elevOps.Count > 0);

        var message = failed.Count > 0
            ? "Brave could not be fully restored: " + string.Join("; ", failed.Select(s => $"{s.Id} — {s.Reason}"))
            : elevOps.Count > 0
                ? $"Brave prefs restored. {elevOps.Count} machine-wide policy value(s) still need Administrator."
                : partial.Count > 0
                    ? "Brave partly restored: " + string.Join("; ", partial.Select(s => $"{s.Id} — {s.Reason}"))
                    : "Brave Exo policies removed; prefs restored from snapshot when available.";

        return new NativeApplyResult
        {
            Ok = ok,
            Module = "brave",
            Message = message,
            Steps = steps,
            ElevatedHklmOps = elevOps
        };
    }

    // ── Discovery ──────────────────────────────────────────────────────────

    public sealed class BraveInstall
    {
        public bool Installed => !string.IsNullOrEmpty(ExePath) && File.Exists(ExePath);
        public string? ExePath { get; init; }
        public string? UserData { get; init; }
        public string? DefaultProfile { get; init; }
        public List<string> ExePaths { get; init; } = new();
        /// <summary>All profile dirs (Default + Profile N) under User Data.</summary>
        public List<string> Profiles { get; init; } = new();
    }

    public static BraveInstall Discover()
    {
        var exes = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var channel in new[] { "Brave-Browser", "Brave-Browser-Beta", "Brave-Browser-Nightly", "Brave-Browser-Dev" })
        {
            foreach (var root in new[]
                     {
                         Path.Combine(local, "BraveSoftware", channel, "Application", "brave.exe"),
                         Path.Combine(pf, "BraveSoftware", channel, "Application", "brave.exe"),
                         Path.Combine(pf86, "BraveSoftware", channel, "Application", "brave.exe"),
                     })
            {
                if (File.Exists(root) && !exes.Contains(root, StringComparer.OrdinalIgnoreCase))
                    exes.Add(Path.GetFullPath(root));
            }
        }

        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName")?.ToString() ?? "";
                        if (!name.Contains("Brave", StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = k?.GetValue("InstallLocation")?.ToString();
                        if (string.IsNullOrWhiteSpace(loc)) continue;
                        var candidate = Path.Combine(loc, "brave.exe");
                        if (!File.Exists(candidate))
                            candidate = Path.Combine(loc, "Application", "brave.exe");
                        if (File.Exists(candidate) && !exes.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            exes.Add(Path.GetFullPath(candidate));
                    }
                    catch { }
                }
            }
            catch { }
        }

        try
        {
            foreach (var p in Process.GetProcessesByName("brave"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                        !exes.Contains(path, StringComparer.OrdinalIgnoreCase))
                        exes.Add(Path.GetFullPath(path));
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        var primary = exes.FirstOrDefault();
        var userData = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
        if (!Directory.Exists(userData))
        {
            foreach (var ch in new[] { "Brave-Browser-Beta", "Brave-Browser-Nightly" })
            {
                var alt = Path.Combine(local, "BraveSoftware", ch, "User Data");
                if (Directory.Exists(alt)) { userData = alt; break; }
            }
        }

        var profiles = new List<string>();
        string? defaultProfile = null;
        if (Directory.Exists(userData))
        {
            var def = Path.Combine(userData, "Default");
            if (Directory.Exists(def) && File.Exists(Path.Combine(def, "Preferences")))
            {
                profiles.Add(def);
                defaultProfile = def;
            }
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(userData, "Profile *")
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(dir, "Preferences")) &&
                        !profiles.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        profiles.Add(dir);
                }
            }
            catch { }

            defaultProfile ??= profiles.FirstOrDefault();
        }

        return new BraveInstall
        {
            ExePath = primary,
            ExePaths = exes,
            UserData = Directory.Exists(userData) ? userData : null,
            DefaultProfile = defaultProfile,
            Profiles = profiles
        };
    }

    // ── Steps ──────────────────────────────────────────────────────────────

    private static NativeApplyStep CloseBrave()
    {
        var n = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName("brave"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    n++;
                }
                catch
                {
                    try { p.Kill(); n++; } catch { }
                }
                finally { p.Dispose(); }
            }
            if (n > 0) Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "close", Status = "partial", Reason = ex.Message };
        }
        return new NativeApplyStep { Id = "close", Status = "ok", Reason = n == 0 ? "not running" : $"closed={n}" };
    }

    private static NativeApplyStep ApplyPolicies(bool admin, List<string> elevOps)
    {
        var written = 0;
        var pending = 0;
        foreach (var (name, value, kind) in PolicyPack)
        {
            if (TrySetPolicy("HKLM", name, value, kind))
            {
                written++;
                continue;
            }
            if (admin)
            {
                // Already elevated path failed somehow
                if (TrySetPolicy("HKCU", name, value, kind)) written++;
                continue;
            }
            // Stage for elev
            if (kind == RegistryValueKind.DWord && value is int i)
            {
                elevOps.Add($"dword:HKLM\\{PolicyPath}|{name}|{i}");
                pending++;
            }
            else if (value is string s)
            {
                elevOps.Add($"string:HKLM\\{PolicyPath}|{name}|{s}");
                pending++;
            }
            // HKCU mirror so something sticks without elev (Brave prefers HKLM)
            TrySetPolicy("HKCU", name, value, kind);
        }

        var completeOrStaged = written + pending == PolicyPack.Length;
        return new NativeApplyStep
        {
            Id = "policies",
            Status = written >= PolicyPack.Length
                ? "ok"
                : pending > 0 && completeOrStaged
                    ? "pending-elev"
                    : written > 0
                        ? "partial"
                        : "fail",
            Reason = $"written={written}/{PolicyPack.Length}; elevPending={pending}"
        };
    }

    private static bool TrySetPolicy(string hive, string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = NativeReg.Root(hive).CreateSubKey(PolicyPath, true);
            if (key is null) return false;
            key.SetValue(name, value, kind);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reports whether Proton Pass is present, and never force-installs it.
    ///
    /// This used to write ExtensionInstallForcelist pointing at the Chrome Web Store
    /// update URL. Brave rejects force-installs it cannot validate and surfaces that to
    /// the user as "blocked by administrator" — which is exactly what was reported from
    /// a real machine, so the policy was failing loudly and doing nothing.
    ///
    /// It was also the wrong shape. A force-installed extension cannot be removed by the
    /// person using the browser, which is the opposite of the consent-first rule the rest
    /// of Exo follows. Exo reports the state and offers the install page; the user decides.
    /// Repair still clears any force-list entry left behind by an older build.
    /// </summary>
    private static NativeApplyStep EnsureProtonPassPolicy(bool admin, List<string> elevOps)
    {
        _ = admin;
        _ = elevOps;
        try
        {
            var install = Discover();
            var present = install.Profiles.Any(pr =>
                Directory.Exists(Path.Combine(pr, "Extensions", ProtonPassExtensionId)));

            return new NativeApplyStep
            {
                Id = "proton-pass",
                Status = "ok",
                Reason = present
                    ? "Proton Pass installed"
                    : $"not installed — get it from https://chromewebstore.google.com/detail/{ProtonPassExtensionId} " +
                      "(Exo does not force-install extensions you cannot remove)"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "proton-pass", Status = "partial", Reason = ex.Message };
        }
    }

    private static NativeApplyStep ApplyAllProfilePrefs(BraveInstall install)
    {
        if (install.Profiles.Count == 0 && string.IsNullOrEmpty(install.UserData))
            return new NativeApplyStep { Id = "prefs", Status = "skip", Reason = "no profile" };

        var profilesTouched = 0;
        try
        {
            foreach (var profile in install.Profiles)
            {
                var prefPath = Path.Combine(profile, "Preferences");
                if (!File.Exists(prefPath)) continue;
                var root = JsonNode.Parse(File.ReadAllText(prefPath)) as JsonObject ?? new JsonObject();
                ApplyPreferenceMutations(root);
                File.WriteAllText(prefPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                profilesTouched++;
            }

            if (!string.IsNullOrEmpty(install.UserData))
            {
                var localPath = Path.Combine(install.UserData, "Local State");
                if (File.Exists(localPath))
                {
                    var root = JsonNode.Parse(File.ReadAllText(localPath)) as JsonObject ?? new JsonObject();
                    SetPath(root, "hardware_acceleration_mode.enabled", true);
                    SetPath(root, "background_mode.enabled", false);
                    SetPath(root, "user_experience_metrics.reporting_enabled", false);
                    SetPath(root, "browser.enabled_labs_experiments", LabsArray());
                    SetPath(root, "dns_over_https.mode", "secure");
                    SetPath(root, "dns_over_https.templates", "https://cloudflare-dns.com/dns-query");
                    // Quiet Autofill / optimization model phone-home at browser scope
                    SetPath(root, "autofill.credit_card_enabled", false);
                    SetPath(root, "autofill.profile_enabled", false);
                    File.WriteAllText(localPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "prefs", Status = "fail", Reason = ex.Message };
        }

        return new NativeApplyStep
        {
            Id = "prefs",
            Status = profilesTouched > 0 ? "ok" : "skip",
            Reason =
                $"profiles={profilesTouched}; flags={LabsExperiments.Length}; amoled + memory + no-vault + debloat prefs"
        };
    }

    private static JsonArray LabsArray()
    {
        var labs = new JsonArray();
        foreach (var exp in LabsExperiments)
            labs.Add(exp);
        return labs;
    }

    private static void ApplyPreferenceMutations(JsonObject root)
    {
        // Appearance / AMOLED.
        //
        // brave.darker_mode was already here and IS real (brave/browser/ui/darker_theme/
        // pref_names.h), but its own doc comment is the whole story: it "makes Brave's ui
        // darker with 'dark mode' enabled". It is a modifier, not a switch. Nothing here
        // ever enabled dark mode, so on any machine following a light Windows theme the
        // darker theme silently did nothing — reported as the AMOLED ask, and it was the
        // same shape as every other bug this month: a lever wired to no prerequisite.
        //
        // brave.dark_mode is an int (brave/components/constants/pref_names.h) holding a
        // BraveDarkModeType: 0 = follow system, 1 = dark, 2 = light. 1 pins dark
        // regardless of the OS theme, which is what makes the two lines below take effect.
        SetPath(root, "brave.dark_mode", 1);
        SetPath(root, "brave.darker_mode", true);
        SetPath(root, "browser.theme.color_scheme2", 2);
        // Chromium seeds its dark palette from an accent colour, which leaves the chrome a
        // tinted charcoal rather than neutral. Grayscale drops the tint, which is the
        // closest the browser UI gets to true black without a packaged theme.
        // NOTE the "2" suffix: browser.theme.is_grayscale is the DEPRECATED spelling that
        // Chromium replaced with is_grayscale2, exactly like color_scheme -> color_scheme2
        // above. Writing the old name is a no-op.
        SetPath(root, "browser.theme.is_grayscale2", true);
        // brave.new_tab_page.background.type / .selected_value / .random were written here
        // and are not defined anywhere in brave-core — not in components/constants/
        // pref_names.h, not in ntp_background_images/common/pref_names.h. Brave never read
        // them, so the "#000000" never applied. Reported from a real machine as "the
        // background was none and wasn't black", which is exactly right: turning the
        // background image off gives Brave's "None", and None falls back to its default
        // dark gradient rather than true black. Pure black is a theme change, not a
        // background-image one — tracked separately as the AMOLED work. Removed rather
        // than left in place, because a write the app ignores is the phantom tweak this
        // whole module has been audited to eliminate.
        SetPath(root, "brave.new_tab_page.show_branded_background_image", false);
        SetPath(root, "brave.new_tab_page.show_sponsored_sites", false);
        // ON, deliberately, and it is the lesser of two evils rather than the right answer.
        //
        // Turning it off does not give black — it gives Brave's "None", which falls back to a
        // purple-tinted default gradient. That is what showed up on a real machine and it is
        // the brightest thing left on an otherwise AMOLED profile. With images on you get
        // Brave's own dark photography instead: not black, but far closer, and sponsored and
        // branded images are already off above so nothing is being sold to you.
        //
        // Pure black needs the solid-colour custom background, whose pref names could not be
        // confirmed against brave-core from here. The last time this file guessed in exactly
        // this key space it wrote three prefs Brave has never read. Not doing that twice.
        SetPath(root, "brave.new_tab_page.show_background_image", true);
        SetPath(root, "brave.new_tab_page.show_rewards", false);
        // show_together, NOT show_brave_talk. The C++ constant is named
        // kNewTabPageShowBraveTalk but its VALUE is "brave.new_tab_page.show_together"
        // (brave/components/brave_talk/pref_names.h) — the widget was renamed and the pref
        // string kept its original spelling. Matching the constant name instead of its
        // value is how this one hid: it reads correct and writes nothing.
        SetPath(root, "brave.new_tab_page.show_together", false);
        SetPath(root, "brave.new_tab_page.show_brave_vpn", false);
        // Black background with JUST the Brave stats card (trackers/ads blocked,
        // bandwidth + time saved) — nothing else. This is the "black with just
        // stats" look: stats on, every other widget off, and crucially
        // hide_all_widgets MUST be false or it suppresses the stats too.
        SetPath(root, "brave.new_tab_page.show_stats", true);
        SetPath(root, "brave.new_tab_page.show_clock", false);
        SetPath(root, "brave.new_tab_page.hide_all_widgets", false);
        // Deleted here, all phantoms, all confirmed absent from brave-core:
        //   show_top_sites      — was marked UNVERIFIED in 4.3.5; now resolved as not real
        //   show_search_widget  — not a pref; the NTP search widget is flag-gated
        //   show_sponsored_images — the real key is show_branded_background_image, above
        // Source of truth is brave/browser/ui/webui/new_tab_page/
        // brave_new_tab_message_handler.cc, which registers an observer for every pref the
        // NTP actually reads. Anything not in that list is a write into nowhere.
        SetPath(root, "brave.shields.stats_badge_visible", true);
        SetPath(root, "bookmark_bar.show_on_all_tabs", false);
        SetPath(root, "browser.show_home_button", false);
        SetPath(root, "homepage_is_newtabpage", true);

        // No Brave vault (Proton Pass owns secrets)
        SetPath(root, "credentials_enable_service", false);
        SetPath(root, "credentials_enable_autosignin", false);
        SetPath(root, "autofill.profile_enabled", false);
        SetPath(root, "autofill.credit_card_enabled", false);
        SetPath(root, "autofill.payment_instrument_enabled", false);
        SetPath(root, "payments.can_make_payment_enabled", false);
        SetPath(root, "autofill_private_windows", false);
        SetPath(root, "profile.password_manager_leak_detection", false);
        SetPath(root, "password_manager.account_storage_enabled", false);

        // Memory / background / perf
        SetPath(root, "background_mode.enabled", false);
        SetPath(root, "performance_tuning.high_efficiency_mode.state", 2);
        SetPath(root, "performance_tuning.high_efficiency_mode.enabled", true);
        SetPath(root, "performance_tuning.battery_saver_mode.state", 0);
        SetPath(root, "hardware_acceleration_mode.enabled", true);
        SetPath(root, "enable_do_not_track", true);
        SetPath(root, "enable_gpc", true);
        SetPath(root, "translate.enabled", false);
        SetPath(root, "search.suggest_enabled", false);
        SetPath(root, "browser.shell_check_enabled", false);
        SetPath(root, "browser.default_browser_infobar_last_declined", "13300000000000000");
        SetPath(root, "browser.check_default_browser", false);
        SetPath(root, "net.network_prediction_options", 2);
        SetPath(root, "dns_prefetching.enabled", false);
        SetPath(root, "safebrowsing.enabled", false);
        SetPath(root, "safebrowsing.enhanced", false);
        SetPath(root, "safebrowsing.scout_reporting_enabled", false);
        SetPath(root, "alternate_error_pages.enabled", false);
        SetPath(root, "spellcheck.use_spelling_service", false);
        SetPath(root, "media_router.enable_media_router", false);
        SetPath(root, "media_router.show_cast_sessions_started_by_other_devices.enabled", false);
        SetPath(root, "gcm.channel_status", false);
        SetPath(root, "signin.allowed", false);
        SetPath(root, "signin.allowed_on_next_startup", false);
        SetPath(root, "sync.requested", false);
        SetPath(root, "download.prompt_for_download", true);
        SetPath(root, "plugins.always_open_pdf_externally", true);
        SetPath(root, "session.restore_on_startup", 5);
        SetPath(root, "profile.exit_type", "Normal");
        SetPath(root, "profile.exited_cleanly", true);

        // Cookie / privacy sandbox prefs
        SetPath(root, "profile.cookie_controls_mode", 1); // block 3P
        SetPath(root, "profile.block_third_party_cookies", true);
        SetPath(root, "privacy_sandbox.m1.ad_measurement_enabled", false);
        SetPath(root, "privacy_sandbox.m1.fledge_enabled", false);
        SetPath(root, "privacy_sandbox.m1.topics_enabled", false);
        SetPath(root, "privacy_sandbox.apis_enabled", false);
        SetPath(root, "privacy_sandbox.apis_enabled_v2", false);
        SetPath(root, "tracking_protection.block_all_3pc_toggle_enabled", true);
        SetPath(root, "tracking_protection.tracking_protection_3pcd_enabled", true);
        SetPath(root, "enable_do_not_track", true);

        // Content / privacy defaults (2 = block)
        SetPath(root, "profile.default_content_setting_values.notifications", 2);
        SetPath(root, "profile.default_content_setting_values.media_stream", 2);
        SetPath(root, "profile.default_content_setting_values.geolocation", 2);
        SetPath(root, "profile.default_content_setting_values.autoplay", 2);
        SetPath(root, "profile.default_content_setting_values.midi_sysex", 2);
        SetPath(root, "profile.default_content_setting_values.protocol_handlers", 2);
        SetPath(root, "profile.default_content_setting_values.durable_storage", 2);
        SetPath(root, "profile.default_content_setting_values.clipboard", 2);
        SetPath(root, "profile.default_content_setting_values.sensors", 2);
        SetPath(root, "profile.default_content_setting_values.usb_guard", 2);
        SetPath(root, "profile.default_content_setting_values.serial_guard", 2);
        SetPath(root, "profile.default_content_setting_values.bluetooth_guard", 2);
        SetPath(root, "profile.default_content_setting_values.hid_guard", 2);
        SetPath(root, "profile.default_content_setting_values.window_placement", 2);
        SetPath(root, "profile.default_content_setting_values.automatic_downloads", 2);
        SetPath(root, "profile.default_content_setting_values.popups", 2);
        SetPath(root, "profile.default_content_setting_values.ads", 2);
        SetPath(root, "profile.default_content_setting_values.mixed_script", 2);
        SetPath(root, "profile.default_content_setting_values.protected_media_identifier", 2);

        // Brave feature kill-switches (prefs + policies belt-and-suspenders)
        SetPath(root, "brave.brave_ads.enabled", false);
        SetPath(root, "brave.brave_ads.should_show_onboarding_dialog", false);
        SetPath(root, "brave.brave_rewards.enabled", false);
        SetPath(root, "brave.rewards.inline_tip_buttons_enabled", false);
        SetPath(root, "brave.wallet.default_wallet2", 0);
        SetPath(root, "brave.wallet.show_wallet_icon_on_toolbar", false);
        SetPath(root, "brave.wallet.keyring.default.is_backwards_compatible_mnemonic", false);
        SetPath(root, "brave.brave_vpn.show_button", false);
        SetPath(root, "brave.ai_chat.autocomplete_provider_enabled", false);
        SetPath(root, "brave.ai_chat.user_dismissed_premium_prompt", true);
        SetPath(root, "brave.leo.disabled_by_policy", true);
        SetPath(root, "brave.mru_cycling_enabled", false);
        SetPath(root, "brave.enable_window_closing_confirm", false);
        SetPath(root, "brave.today.opted_in", false);
        SetPath(root, "brave.today.should_show_toolbar_button", false);
        SetPath(root, "brave.ipfs.enabled", false);
        SetPath(root, "brave.ipfs.resolve_method", 0);
        SetPath(root, "brave.web3.dapps_list_enabled", false);
        SetPath(root, "brave.de_amp.enabled", true);
        SetPath(root, "brave.debounce.enabled", true);
        SetPath(root, "brave.reduce_language", true);
        SetPath(root, "brave.webtorrent_enabled", false);
        SetPath(root, "brave.p3a.enabled", false);
        SetPath(root, "brave.stats.reporting_enabled", false);
        SetPath(root, "brave.web_discovery_enabled", false);
        SetPath(root, "brave.speedreader.enabled", false);
        SetPath(root, "brave.wayback_machine_enabled", false);
        SetPath(root, "brave.tor.used", false);
        SetPath(root, "brave.shields.advanced_view_enabled", true);
        SetPath(root, "brave.sidebar_hidden", true);
        SetPath(root, "sidebar.show_option", 0);

        // WebRTC: default public interface only style (policy also pins)
        SetPath(root, "webrtc.ip_handling_policy", "disable_non_proxied_udp");
        SetPath(root, "webrtc.multiple_routes_enabled", false);
        SetPath(root, "webrtc.nonproxied_udp_enabled", false);
    }

    private static NativeApplyStep EnableContentFilters(BraveInstall install)
    {
        if (string.IsNullOrEmpty(install.UserData))
            return new NativeApplyStep { Id = "filters", Status = "skip", Reason = "no user data" };

        var localPath = Path.Combine(install.UserData, "Local State");
        if (!File.Exists(localPath))
            return new NativeApplyStep { Id = "filters", Status = "skip", Reason = "no Local State" };

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(localPath)) as JsonObject ?? new JsonObject();
            if (root["brave"] is not JsonObject brave)
            {
                brave = new JsonObject();
                root["brave"] = brave;
            }
            if (brave["ad_block"] is not JsonObject adBlock)
            {
                adBlock = new JsonObject();
                brave["ad_block"] = adBlock;
            }
            adBlock["checked_all_default_regions"] = true;
            if (adBlock["regional_filters"] is not JsonObject filters)
            {
                filters = new JsonObject();
                adBlock["regional_filters"] = filters;
            }

            var enabled = 0;
            foreach (var uuid in ContentFilterUuids)
            {
                if (filters[uuid] is not JsonObject entry)
                {
                    entry = new JsonObject();
                    filters[uuid] = entry;
                }
                entry["enabled"] = true;
                enabled++;
            }

            // Disable social-embed *allow* lists (hidden Neo-adjacent quiet)
            foreach (var allowId in new[]
                     {
                         "A5E6EC21-F01F-4547-9F0A-1EE1C3F2AE8D", // Facebook embeds
                         "84960ADD-1CC1-419F-81FC-9F116F5205CC", // X embeds
                         "FB626316-6CC8-4447-884B-F5A37C29B0AE", // LinkedIn embeds
                     })
            {
                if (filters[allowId] is JsonObject allow)
                    allow["enabled"] = false;
                else
                    filters[allowId] = new JsonObject { ["enabled"] = false };
            }

            File.WriteAllText(localPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            return new NativeApplyStep
            {
                Id = "filters",
                Status = "ok",
                Reason = $"enabled={enabled} lists (cookie/annoy/social/AI/chat/shorts/paywall)"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "filters", Status = "fail", Reason = ex.Message };
        }
    }

    private static void SetPath(JsonObject root, string dotted, JsonNode? value)
    {
        var parts = dotted.Split('.');
        JsonObject cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                cur[parts[i]] = next;
            }
            cur = next;
        }
        cur[parts[^1]] = value is null ? null : value.DeepClone();
    }

    private static void SetPath(JsonObject root, string dotted, bool value) =>
        SetPath(root, dotted, JsonValue.Create(value));

    private static void SetPath(JsonObject root, string dotted, int value) =>
        SetPath(root, dotted, JsonValue.Create(value));

    private static void SetPath(JsonObject root, string dotted, string value) =>
        SetPath(root, dotted, JsonValue.Create(value));

    private static NativeApplyStep PurgeBraveVaultSurgical(BraveInstall install)
    {
        if (install.Profiles.Count == 0)
            return new NativeApplyStep { Id = "vault", Status = "skip", Reason = "no profile" };

        var loginDeleted = 0;
        var tablesCleared = 0;
        var webDataFallback = 0;

        foreach (var profile in install.Profiles)
        {
            // Passwords: delete Login Data entirely (no non-secret payload we need)
            foreach (var name in new[]
                     {
                         "Login Data", "Login Data-journal",
                         "Login Data For Account", "Login Data For Account-journal",
                         "Affiliation Database", "Affiliation Database-journal",
                     })
            {
                var path = Path.Combine(profile, name);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        loginDeleted++;
                    }
                }
                catch { }
            }

            // Autofill / cards / addresses: surgical SQL — keep keywords (search engines)
            var webData = Path.Combine(profile, "Web Data");
            if (!File.Exists(webData)) continue;
            try
            {
                // Drop -journal companions so SQLite opens clean
                foreach (var side in new[] { "Web Data-journal", "Web Data-wal", "Web Data-shm" })
                {
                    var sp = Path.Combine(profile, side);
                    try { if (File.Exists(sp)) File.Delete(sp); } catch { }
                }

                tablesCleared += ClearVaultTables(webData);
            }
            catch
            {
                // Last resort: delete Web Data (loses keywords too)
                try
                {
                    File.Delete(webData);
                    webDataFallback++;
                }
                catch { }
            }
        }

        return new NativeApplyStep
        {
            Id = "vault",
            Status = "ok",
            Reason =
                $"loginFiles={loginDeleted}; tablesCleared={tablesCleared}; webDataFallbackDelete={webDataFallback}; keywords kept when surgical"
        };
    }

    private static int ClearVaultTables(string webDataPath)
    {
        var n = 0;
        // Windows system SQLite (winsqlite3) — no embedded e_sqlite3 binary.
        SQLitePCL.Batteries_V2.Init();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = webDataPath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }

        // Existing tables only
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var list = conn.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = list.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(0));
        }

        using var tx = conn.BeginTransaction();
        foreach (var table in VaultTables)
        {
            if (!existing.Contains(table)) continue;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM \"" + table.Replace("\"", "\"\"") + "\"";
                cmd.ExecuteNonQuery();
                n++;
            }
            catch { /* table locked / schema drift */ }
        }
        tx.Commit();
        return n;
    }

    private static NativeApplyStep WriteFullSnapshot(BraveInstall install)
    {
        try
        {
            var snapDir = Path.Combine(PathHelper.AppDataDir, "brave-snapshot");
            Directory.CreateDirectory(snapDir);
            // Keep first snapshot as pristine Repair baseline
            var marker = Path.Combine(snapDir, "snapshot.json");
            if (File.Exists(marker))
                return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "kept original baseline" };

            var files = new List<string>();
            if (!string.IsNullOrEmpty(install.UserData))
            {
                var local = Path.Combine(install.UserData, "Local State");
                if (File.Exists(local))
                {
                    var dest = Path.Combine(snapDir, "Local State");
                    File.Copy(local, dest, true);
                    files.Add("Local State");
                }
            }

            var i = 0;
            foreach (var profile in install.Profiles)
            {
                var leaf = Path.GetFileName(profile);
                var pref = Path.Combine(profile, "Preferences");
                if (!File.Exists(pref)) continue;
                var destName = $"Preferences.{leaf}";
                File.Copy(pref, Path.Combine(snapDir, destName), true);
                files.Add(destName);
                i++;
            }

            // GPU preferences, so Repair can undo the routing stamp. Recorded even when
            // nothing was set (null = "no value before Exo"), because deleting a key we
            // created is the only honest restore for that case.
            try
            {
                var gpuBefore = GpuTopology.SnapshotPreferences(GpuTargets(install));
                File.WriteAllText(
                    Path.Combine(snapDir, "gpu-preferences.json"),
                    JsonSerializer.Serialize(gpuBefore, new JsonSerializerOptions { WriteIndented = true }));
                files.Add("gpu-preferences.json");
            }
            catch { /* recorded absence is handled by RestoreGpu's skip path */ }

            // Policy dump
            var policyDump = new Dictionary<string, object?>();
            foreach (var (name, _, _) in PolicyPack)
            {
                var v = NativeReg.GetValue("HKLM", PolicyPath, name)
                        ?? NativeReg.GetValue("HKCU", PolicyPath, name);
                if (v is not null) policyDump[name] = v.ToString();
            }

            var meta = new
            {
                version = StateVersion,
                utc = DateTime.UtcNow.ToString("o"),
                exe = install.ExePath,
                userData = install.UserData,
                profiles = install.Profiles,
                files,
                policiesBefore = policyDump,
                note = "Repair restores prefs/Local State; vault purge not undone"
            };
            File.WriteAllText(marker, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            return new NativeApplyStep
            {
                Id = "snapshot",
                Status = "ok",
                Reason = $"saved {files.Count} file(s) under brave-snapshot/"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep RestoreFullSnapshot(BraveInstall install)
    {
        try
        {
            var snapDir = Path.Combine(PathHelper.AppDataDir, "brave-snapshot");
            if (!Directory.Exists(snapDir))
                return new NativeApplyStep { Id = "snapshot", Status = "skip", Reason = "no snapshot" };

            var restored = 0;
            if (!string.IsNullOrEmpty(install.UserData))
            {
                var src = Path.Combine(snapDir, "Local State");
                var dest = Path.Combine(install.UserData, "Local State");
                if (File.Exists(src))
                {
                    File.Copy(src, dest, true);
                    restored++;
                }
            }

            foreach (var profile in install.Profiles)
            {
                var leaf = Path.GetFileName(profile);
                var src = Path.Combine(snapDir, $"Preferences.{leaf}");
                var dest = Path.Combine(profile, "Preferences");
                if (File.Exists(src))
                {
                    File.Copy(src, dest, true);
                    restored++;
                }
            }

            return new NativeApplyStep
            {
                Id = "snapshot",
                Status = restored > 0 ? "ok" : "skip",
                Reason = $"restored={restored} files"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    /// <summary>
    /// Route Brave off the discrete GPU on hybrid machines, and clear the stamp on
    /// single-GPU machines where it does nothing.
    ///
    /// This used to write GpuPreference=2 unconditionally, which was wrong twice over.
    /// Brave is a Chromium UI process — the same class of process Steam deliberately
    /// routes to the integrated GPU so the discrete one stays free for the game. And
    /// on the single-GPU desktops most users have, UserGpuPreferences has no second
    /// adapter to select, so the write was inert while still being reported as applied.
    /// Both modules now go through <see cref="GpuTopology"/> so they cannot drift apart.
    /// </summary>
    private static NativeApplyStep ApplyGpu(BraveInstall install)
    {
        var hybrid = GpuTopology.IsHybrid();
        try
        {
            var (stamped, cleared) = GpuTopology.RouteBrowserUi(GpuTargets(install), hybrid);
            return new NativeApplyStep
            {
                Id = "gpu",
                Status = "ok",
                Reason = hybrid
                    ? $"integrated (keeps the discrete GPU for games), stamped={stamped}"
                    : $"single-GPU: preference is inert, cleared={cleared}"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "gpu", Status = "fail", Reason = ex.Message };
        }
    }

    /// <summary>Every Brave executable path a GPU preference could be keyed on, de-duplicated.</summary>
    private static List<string> GpuTargets(BraveInstall install)
    {
        var targets = new List<string>(install.ExePaths);
        if (!string.IsNullOrWhiteSpace(install.ExePath)) targets.Add(install.ExePath);
        return targets
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NativeApplyStep RestoreGpu(BraveInstall install)
    {
        try
        {
            var snapDir = Path.Combine(PathHelper.AppDataDir, "brave-snapshot");
            var path = Path.Combine(snapDir, "gpu-preferences.json");
            if (!File.Exists(path))
                return new NativeApplyStep { Id = "gpu-restore", Status = "skip", Reason = "no GPU snapshot" };

            var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path));
            if (before is null || before.Count == 0)
                return new NativeApplyStep { Id = "gpu-restore", Status = "skip", Reason = "empty GPU snapshot" };

            var restored = GpuTopology.RestorePreferences(before);
            return new NativeApplyStep
            {
                Id = "gpu-restore",
                Status = restored == before.Count ? "ok" : restored > 0 ? "partial" : "fail",
                Reason = $"restored={restored}/{before.Count}"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "gpu-restore", Status = "fail", Reason = ex.Message };
        }
    }

    private static NativeApplyStep QuietStartup()
    {
        var removed = 0;
        var stuck = 0;
        string? firstStuck = null;
        foreach (var (hive, path) in new[]
                 {
                     ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                     ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
                     ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                     ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
                 })
        {
            try
            {
                using var key = NativeReg.Root(hive).OpenSubKey(path, true);
                if (key is null) continue;
                foreach (var name in key.GetValueNames().ToArray())
                {
                    var val = key.GetValue(name)?.ToString() ?? "";
                    if (name.Contains("Brave", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("brave.exe", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("BraveUpdate", StringComparison.OrdinalIgnoreCase))
                    {
                        // A Brave entry we matched but could not delete is a real failure.
                        // Finding none at all is not — that machine is already quiet.
                        try { key.DeleteValue(name, false); removed++; }
                        catch (Exception ex) { stuck++; firstStuck ??= $"{hive}\\{path}\\{name}: {ex.Message}"; }
                    }
                }
            }
            catch { }
        }

        // Toast / notification background activity for Brave (HKCU only — no elev)
        var notifQuiet = 0;
        try
        {
            using var notif = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", true);
            if (notif is not null)
            {
                foreach (var sub in notif.GetSubKeyNames())
                {
                    if (!sub.Contains("Brave", StringComparison.OrdinalIgnoreCase) &&
                        !sub.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        using var sk = notif.OpenSubKey(sub, true);
                        sk?.SetValue("Enabled", 0, RegistryValueKind.DWord);
                        notifQuiet++;
                    }
                    catch { }
                }
            }
        }
        catch { }

        var approved = SteamNativeApply.DisableStartupApproved(new[]
        {
            "Brave", "Brave Browser", "BraveSoftware", "brave", "BraveUpdate"
        });

        return new NativeApplyStep
        {
            Id = "startup",
            Status = stuck == 0 ? "ok" : removed > 0 ? "partial" : "fail",
            Reason = stuck == 0
                ? $"runRemoved={removed}; notifQuiet={notifQuiet}; startupApproved={approved}"
                : $"runRemoved={removed}; stuck={stuck} ({firstStuck}); notifQuiet={notifQuiet}; startupApproved={approved}"
        };
    }

    private static NativeApplyStep QuietUpdateTasks()
    {
        // Disable enabled tasks whose path/name contains BraveSoftware / BraveUpdate
        var disabled = 0;
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null)
                return new NativeApplyStep { Id = "tasks", Status = "skip", Reason = "no COM" };
            dynamic? service = Activator.CreateInstance(t);
            if (service is null)
                return new NativeApplyStep { Id = "tasks", Status = "skip", Reason = "no service" };
            service.Connect();
            dynamic root = service.GetFolder("\\");
            disabled = QuietBraveTasksRecursive(root);
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "tasks", Status = "partial", Reason = ex.Message };
        }

        // schtasks belt: known update task names. Exit code 1 here usually means the task
        // simply is not present on this machine, which is not a failure — only a denied or
        // errored change is. Distinguish the two so "nothing to do" never looks like a win
        // and "access denied on everything" never reports ok.
        var denied = 0;
        string? firstError = null;
        foreach (var task in new[]
                 {
                     @"\BraveSoftware\BraveUpdateTaskMachineCore",
                     @"\BraveSoftware\BraveUpdateTaskMachineUA",
                     @"\BraveSoftware\UpdateTask",
                     @"\BraveUpdateTaskMachineCore",
                     @"\BraveUpdateTaskMachineUA",
                 })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Change /TN \"{task}\" /Disable",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (p is null) continue;
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(4000);
                if (p.ExitCode == 0) { disabled++; continue; }
                if (stderr.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                    stderr.Contains("Access", StringComparison.OrdinalIgnoreCase))
                {
                    denied++;
                    firstError ??= stderr.Trim();
                }
            }
            catch (Exception ex)
            {
                denied++;
                firstError ??= ex.Message;
            }
        }

        if (denied > 0)
        {
            return new NativeApplyStep
            {
                Id = "tasks",
                Status = disabled > 0 ? "partial" : "fail",
                Reason = $"disabled={disabled}; denied={denied} ({firstError}) — needs elevation"
            };
        }

        return new NativeApplyStep
        {
            Id = "tasks",
            Status = "ok",
            Reason = disabled == 0 ? "no Brave update tasks present" : $"disabled={disabled}"
        };
    }

    private static int QuietBraveTasksRecursive(dynamic folder)
    {
        var n = 0;
        try
        {
            foreach (dynamic task in folder.GetTasks(0))
            {
                try
                {
                    string name = (string)task.Name;
                    string path = "";
                    try { path = (string)task.Path; } catch { }
                    var blob = name + " " + path;
                    if (!blob.Contains("Brave", StringComparison.OrdinalIgnoreCase) &&
                        !blob.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Don't delete — disable only. Enabled marshals as bool; casting it to
                    // int threw through the dynamic binder, the per-task catch ate it, and
                    // this walk disabled zero tasks while the step still reported ok.
                    if ((bool)task.Enabled)
                    {
                        task.Enabled = false;
                        n++;
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (dynamic child in folder.GetFolders(0))
                n += QuietBraveTasksRecursive(child);
        }
        catch { }

        return n;
    }

    /// <summary>
    /// Quiet Brave Software Update Windows services (not Windows Update / Defender).
    /// Enumerate HKLM services by ImagePath/name, demand-start + stop via sc.exe.
    /// </summary>
    private static NativeApplyStep QuietBraveServices()
    {
        var touched = 0;
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
                return new NativeApplyStep { Id = "services", Status = "skip", Reason = "no services key" };

            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var sk = services.OpenSubKey(name);
                    if (sk is null) continue;
                    var display = sk.GetValue("DisplayName")?.ToString() ?? "";
                    var image = sk.GetValue("ImagePath")?.ToString() ?? "";
                    var blob = name + " " + display + " " + image;
                    if (!blob.Contains("Brave", StringComparison.OrdinalIgnoreCase) &&
                        !blob.Contains("BraveSoftware", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (blob.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
                        blob.Contains("Windows Update", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "sc.exe",
                            Arguments = $"stop \"{name}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        })?.WaitForExit(8000);
                    }
                    catch { }

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "sc.exe",
                            Arguments = $"config \"{name}\" start= demand",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        })?.WaitForExit(5000);
                        touched++;
                    }
                    catch { }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "services", Status = "partial", Reason = ex.Message };
        }
        return new NativeApplyStep
        {
            Id = "services",
            Status = "ok",
            Reason = touched == 0 ? "no Brave services found" : $"quieted={touched}"
        };
    }

    /// <summary>
    /// Drop regenerable caches only — never Cookies, History, Bookmarks, Preferences, Login Data leftovers we already handled.
    /// Widevine kept (DRM streaming). Component security lists regenerate on next launch.
    /// </summary>
    private static NativeApplyStep ClearSafeCaches(BraveInstall install)
    {
        if (string.IsNullOrEmpty(install.UserData))
            return new NativeApplyStep { Id = "cache", Status = "skip", Reason = "no user data" };

        var removed = 0;
        var locked = 0;
        var userDataDirs = new[]
        {
            "GrShaderCache", "ShaderCache", "GraphiteDawnCache", "GPUCache",
            "DawnCache", "DawnWebGPUCache", "component_crx_cache",
            "BrowserMetrics", "Crashpad", "Crowd Deny", "CertificateRevocation",
            "FileTypePolicies", "MEIPreload", "OnDeviceHeadSuggestModel",
            "OptimizationHints", "OriginTrials", "PKIMetadata", "SafetyTips",
            "SSLErrorAssistant", "Subresource Filter", "ZxcvbnData",
            "AutofillStates", "FirstPartySetsPreloaded", "hyphen-data",
            "ScreenAI", "WasmTtsEngine", "PrivacySandboxAttestationsPreloaded",
            "AmountExtractionHeuristicRegexes", "TrustTokenKeyCommitments",
            "OpenCookieDatabase", "pnacl", "GraphiteDawnCache",
        };
        foreach (var d in userDataDirs)
        {
            var path = Path.Combine(install.UserData, d);
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    removed++;
                }
            }
            catch { }
        }

        // Loose metrics / crash junk files under User Data
        try
        {
            foreach (var f in Directory.EnumerateFiles(install.UserData, "BrowserMetrics-*.pma"))
            {
                try { File.Delete(f); removed++; } catch { }
            }
            foreach (var f in Directory.EnumerateFiles(install.UserData, "*.log"))
            {
                try
                {
                    var name = Path.GetFileName(f);
                    if (name.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("brave", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch { }
            }
        }
        catch { }

        foreach (var profile in install.Profiles)
        {
            foreach (var rel in new[]
                     {
                         "GPUCache", "Code Cache", "Service Worker\\CacheStorage",
                         "Service Worker\\ScriptCache", "Cache", "Code Cache\\js",
                         "Code Cache\\wasm", "DawnCache", "optimization_guide_model_store",
                         "optimization_guide_hint_cache_store", "VideoDecodeStats",
                         "JumpListIconsMostVisited", "JumpListIconsRecentClosed",
                         "JumpListIconsCustom", "Feature Engagement Tracker",
                         "BudgetDatabase", "commerce_subscription_db", "discount_infos_db",
                         "parcel_tracking_db", "PersistentOriginTrials", "Download Service",
                         "shared_proto_db", "Shared Dictionary", "Reporting and NEL",
                         "Site Characteristics Database", "GCM Store",
                         "Network\\Network Action Predictor",
                     })
            {
                var path = Path.Combine(profile, rel);
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                        removed++;
                    }
                }
                catch
                {
                    // Usually Brave still holding a handle. Benign — the cache regenerates —
                    // but it did not happen, so it is reported rather than counted as done.
                    locked++;
                }
            }
        }

        return new NativeApplyStep
        {
            Id = "cache",
            Status = locked == 0 ? "ok" : "partial",
            Reason = locked == 0
                ? $"cleared={removed} cache trees (history/cookies/bookmarks kept; Widevine kept)"
                : $"cleared={removed}, {locked} still locked by a running Brave (history/cookies/bookmarks kept)"
        };
    }

    private static NativeApplyStep RemovePolicies(bool admin, List<string> elevOps)
    {
        // Count what was actually REMOVED, and stage what could not be.
        //
        // This used to run `key.DeleteValue(name, false); n++;` for every policy name. The
        // false argument means "do not throw when the value is absent", so n counted delete
        // ATTEMPTS: it reported "removed=N values" on a machine that had none of them. Worse,
        // when Repair ran unelevated every HKLM delete failed silently and the step still went
        // "ok" - the machine-wide policies stayed exactly where they were, Brave stayed locked
        // down, and Repair told the user it had undone them. Unlike system and spotify, this
        // path had no elevation route at all.
        var removed = 0;
        var pending = 0;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            var isMachine = ReferenceEquals(hive, Registry.LocalMachine);
            try
            {
                using var read = hive.OpenSubKey(PolicyPath, false);
                if (read is null) continue;

                // Only values that are really present are candidates for removal.
                var present = PolicyPack
                    .Select(p => p.Item1)
                    .Where(name => read.GetValue(name) is not null)
                    .ToList();
                if (present.Count == 0) continue;

                if (isMachine && !admin)
                {
                    foreach (var name in present) elevOps.Add($"delete:HKLM\\{PolicyPath}|{name}");
                    pending += present.Count;
                    continue;
                }

                using var key = hive.OpenSubKey(PolicyPath, true);
                if (key is null)
                {
                    if (isMachine)
                    {
                        foreach (var name in present) elevOps.Add($"delete:HKLM\\{PolicyPath}|{name}");
                        pending += present.Count;
                    }
                    continue;
                }
                foreach (var name in present)
                {
                    try { key.DeleteValue(name, false); removed++; }
                    catch
                    {
                        if (isMachine) { elevOps.Add($"delete:HKLM\\{PolicyPath}|{name}"); pending++; }
                    }
                }
            }
            catch { }
        }

        return new NativeApplyStep
        {
            Id = "policies",
            Status = pending > 0 ? "partial" : "ok",
            Reason = pending > 0
                ? $"removed={removed}; {pending} machine-wide value(s) still need Administrator"
                : removed > 0 ? $"removed={removed} value(s)" : "no Exo policies were present"
        };
    }

    /// <summary>
    /// Deletes managed policies Exo used to write and deliberately no longer does.
    ///
    /// BookmarkBarEnabled=0 force-hid the bookmarks bar everywhere including the new tab
    /// page. SafeBrowsingProtectionLevel=0 disabled phishing, malware, download and extension
    /// protection for no measured gaming benefit. ExtensionInstallForcelist tried to force-
    /// install Proton Pass from a URL Brave rejects, and a forced extension cannot be removed
    /// by the user. All are gone from the apply path; this removes older Exo values that would
    /// otherwise continue overriding the browser indefinitely.
    /// </summary>
    private static NativeApplyStep RemoveRetiredPolicies(bool admin, List<string> elevOps)
    {
        var cleared = 0;
        var blocked = 0;
        var pending = 0;
        string? firstError = null;
        var retiredValues = new[]
        {
            "BookmarkBarEnabled",
            "SafeBrowsingProtectionLevel",
            "SafeBrowsingForTrustedSourcesEnabled"
        };

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            var machine = ReferenceEquals(root, Registry.LocalMachine);
            try
            {
                using var read = root.OpenSubKey(PolicyPath, writable: false);
                var present = read is null
                    ? new List<string>()
                    : retiredValues.Where(n => read.GetValue(n) is not null).ToList();
                if (present.Count > 0)
                {
                    if (machine && !admin)
                    {
                        foreach (var name in present)
                            elevOps.Add($"delete:HKLM\\{PolicyPath}|{name}");
                        pending += present.Count;
                    }
                    else
                    {
                        using var key = root.OpenSubKey(PolicyPath, writable: true);
                        foreach (var name in present)
                        {
                            if (key is null) { blocked++; continue; }
                            try { key.DeleteValue(name, false); cleared++; }
                            catch (Exception ex) { blocked++; firstError ??= ex.Message; }
                        }
                    }
                }
            }
            catch (Exception ex) { blocked++; firstError ??= ex.Message; }

            try
            {
                using var probe = root.OpenSubKey(PolicyPath + @"\ExtensionInstallForcelist");
                if (probe is not null)
                {
                    probe.Dispose();
                    if (machine && !admin)
                    {
                        elevOps.Add($"delete-tree:HKLM\\{PolicyPath}\\ExtensionInstallForcelist");
                        pending++;
                    }
                    else
                    {
                        root.DeleteSubKeyTree(PolicyPath + @"\ExtensionInstallForcelist", false);
                        cleared++;
                    }
                }
            }
            catch (Exception ex) { blocked++; firstError ??= ex.Message; }
        }

        if (blocked > 0)
        {
            return new NativeApplyStep
            {
                Id = "retired-policies",
                Status = cleared > 0 ? "partial" : "fail",
                Reason = admin
                    ? $"cleared={cleared}; {blocked} could not be removed ({firstError})"
                    : $"cleared={cleared}; {blocked} need elevation ({firstError})"
            };
        }

        return new NativeApplyStep
        {
            Id = "retired-policies",
            Status = pending > 0 ? "pending-elev" : "ok",
            Reason = pending > 0
                ? $"cleared={cleared}; {pending} retired machine policy item(s) need Administrator"
                : cleared == 0 ? "none left by older builds" : $"cleared={cleared} retired policy item(s)"
        };
    }

    private static NativeApplyStep RemoveExtensionForceList(bool admin, List<string> elevOps)
    {
        // Report what survived. Both deletes were wrapped in bare catches and the step then
        // returned "ok" regardless, so an unelevated Repair left the machine-wide force-list
        // in place - Brave keeps force-installing the extension on every launch - and told the
        // user it had been cleared.
        const string sub = PolicyPath + @"\ExtensionInstallForcelist";
        if (!admin)
        {
            try
            {
                using var machine = Registry.LocalMachine.OpenSubKey(sub, false);
                if (machine is not null)
                    elevOps.Add($"delete-tree:HKLM\\{sub}");
            }
            catch { }
        }
        else
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(sub, false); } catch { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }

        var machineLeft = false;
        var userLeft = false;
        try { using var k = Registry.LocalMachine.OpenSubKey(sub, false); machineLeft = k is not null; } catch { machineLeft = true; }
        try { using var k = Registry.CurrentUser.OpenSubKey(sub, false); userLeft = k is not null; } catch { userLeft = true; }

        if (!machineLeft && !userLeft)
            return new NativeApplyStep { Id = "proton-pass", Status = "ok", Reason = "force-list cleared (extension left installed)" };

        if (machineLeft && !admin && elevOps.Any(o =>
                o.Equals($"delete-tree:HKLM\\{sub}", StringComparison.OrdinalIgnoreCase)))
        {
            return new NativeApplyStep
            {
                Id = "proton-pass",
                Status = "pending-elev",
                Reason = "machine-wide force-list needs Administrator"
            };
        }

        var where = machineLeft && userLeft ? "machine-wide and per-user" : machineLeft ? "machine-wide" : "per-user";
        return new NativeApplyStep
        {
            Id = "proton-pass",
            Status = "partial",
            Reason = $"{where} force-list could not be removed" + (machineLeft ? " - needs Administrator" : "")
        };
    }

    private static void SaveState(bool applied, BraveInstall install, List<NativeApplyStep> steps, List<string> elevOps)
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, "brave-optimizer.json");
            var state = new Dictionary<string, object?>
            {
                ["version"] = StateVersion,
                ["applyStatus"] = applied ? "applied" : "partial",
                ["applied"] = applied,
                ["appliedUtc"] = DateTime.UtcNow.ToString("o"),
                ["path"] = install.ExePath,
                ["userData"] = install.UserData,
                ["applyReport"] = steps.Select(s => s.ToReportLine()).ToList(),
                ["elevOps"] = elevOps,
                ["protonPassId"] = ProtonPassExtensionId
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
