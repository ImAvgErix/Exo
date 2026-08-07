// Brave has no PowerShell kit -- native C# is the entire apply/repair surface
// (see Exo/Services/BraveNativeApply.cs). This smoke reads the shipped source
// as text and asserts the safety properties promised in AGENTS.md / the
// cleanup plan, mirroring the source-shape checks Ui.Smoke/Contracts.Smoke
// already use for other native-only paths.

var logPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "brave-smoke.log");
var lines = new List<string>();
var failed = 0;
void Log(string s) { lines.Add(s); Console.WriteLine(s); }
void Expect(string name, bool cond, string detail = "")
{
    if (cond) Log($"PASS  {name}");
    else { failed++; Log($"FAIL  {name}" + (detail.Length > 0 ? " :: " + detail : "")); }
}

Log("=== Brave.Smoke ===");

var repo = FindRepoRoot();
var applyPath = Path.Combine(repo, "Exo", "Services", "BraveNativeApply.cs");
var detectPath = Path.Combine(repo, "Exo", "Services", "NativeLiveDetect.cs");
Expect("BraveNativeApply.cs exists", File.Exists(applyPath));
Expect("NativeLiveDetect.cs exists", File.Exists(detectPath));
if (!File.Exists(applyPath))
{
    Log($"=== SUMMARY failed={failed} ===");
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    File.WriteAllLines(logPath, lines);
    Environment.Exit(1);
}
var src = File.ReadAllText(applyPath);
var detectSrc = File.Exists(detectPath) ? File.ReadAllText(detectPath) : "";

// Entry points the bridge/native-apply router depends on.
Expect("Apply entry point", src.Contains("public static NativeApplyResult Apply(", StringComparison.Ordinal));
Expect("Repair entry point", src.Contains("public static NativeApplyResult Repair(", StringComparison.Ordinal));
Expect("Discover entry point", src.Contains("public static BraveInstall Discover(", StringComparison.Ordinal));

// Privacy/telemetry policy pack: every reporting/tracking channel disabled.
foreach (var disabledPolicy in new[]
{
    "MetricsReportingEnabled", "CloudReportingEnabled", "SafeBrowsingExtendedReportingEnabled",
    "UrlKeyedAnonymizedDataCollectionEnabled", "BraveP3AEnabled", "BraveStatsPingEnabled",
    "UserFeedbackAllowed", "DeviceMetricsReportingEnabled", "WebRtcEventLogCollectionAllowed",
    "DomainReliabilityAllowed"
})
{
    Expect($"policy disables {disabledPolicy}",
        System.Text.RegularExpressions.Regex.IsMatch(src,
            $@"\(""{disabledPolicy}"",\s*0,\s*RegistryValueKind\.DWord\)"));
}

// Security-critical exception: component updates (safe-browsing/cert lists) stay ON
// even though every telemetry/reporting channel above is off.
Expect("component updates stay enabled (security exception)",
    System.Text.RegularExpressions.Regex.IsMatch(src,
        @"\(""ComponentUpdatesEnabled"",\s*1,\s*RegistryValueKind\.DWord\)"));
Expect("Safe Browsing is never forced to No Protection",
    !HasNonCommentText(src, "(\"SafeBrowsingProtectionLevel\", 0")
    && !HasNonCommentText(src, "(\"SafeBrowsingForTrustedSourcesEnabled\", 0"));
Expect("older Safe Browsing disabling policies are actively retired",
    src.Contains("SafeBrowsingProtectionLevel", StringComparison.Ordinal)
    && src.Contains("SafeBrowsingForTrustedSourcesEnabled", StringComparison.Ordinal)
    && src.Contains("RemoveRetiredPolicies(admin, elevOps)", StringComparison.Ordinal));
Expect("an unelevated policy pack reports pending rather than permanent partial",
    src.Contains("completeOrStaged", StringComparison.Ordinal)
    && src.Contains("\"pending-elev\"", StringComparison.Ordinal));
Expect("Shields live detect requires both managed policies",
    System.Text.RegularExpressions.Regex.IsMatch(detectSrc,
        @"DefaultBraveAdblockSetting"",\s*2\)\s*&&\s*Pol\(""DefaultBraveFingerprintingV2Setting"",\s*3\)"));
Expect("Safe Browsing has a live evidence row",
    detectSrc.Contains("Safe Browsing preserved", StringComparison.Ordinal));

// New tab: the Brave stats card, and a background that is not brighter than the rest of the
// browser. The stats widget must be ON and hide_all_widgets OFF — true suppresses the stats
// card too. This block used to require background.type="color" and
// background.selected_value="#000000", keys that do not exist in brave-core, so the gate was
// enforcing a phantom tweak; those names are still banned below.
//
// Background images ON. Off is not black - it is Brave's "None", a purple-tinted default
// gradient, and on an otherwise AMOLED profile that gradient is the brightest thing left.
// Reported from a real machine. Brave's own dark photography is closer to black than the
// fallback is, and the branded/sponsored variants are separately off, so nothing is sold.
Expect("new tab shows Brave's dark imagery rather than the purple fallback",
    src.Contains("\"brave.new_tab_page.show_background_image\", true", StringComparison.Ordinal));
Expect("branded and sponsored backgrounds stay off",
    src.Contains("\"brave.new_tab_page.show_branded_background_image\", false", StringComparison.Ordinal)
    && src.Contains("\"brave.new_tab_page.show_sponsored_sites\", false", StringComparison.Ordinal));
Expect("the invented background colour keys stay gone",
    !HasNonCommentText(src, "background.selected_value")
    && !HasNonCommentText(src, "background.type"));

// Every NTP pref below was checked against brave-core rather than guessed at. The list of
// prefs the new tab page actually reads is the observer registration in
// brave/browser/ui/webui/new_tab_page/brave_new_tab_message_handler.cc; anything absent
// from it is a write into nowhere. Four more were found phantom after 4.3.5 shipped.
foreach (var phantom in new[]
{
    "brave.new_tab_page.show_top_sites",      // was marked UNVERIFIED in 4.3.5; not real
    "brave.new_tab_page.show_search_widget",  // flag-gated, not a pref
    "brave.new_tab_page.show_sponsored_images" // real key is show_branded_background_image
})
{
    Expect($"phantom pref {phantom} stays gone", !HasNonCommentText(src, phantom));
}
// The C++ constant is kNewTabPageShowBraveTalk but its value is "...show_together".
// Matching the constant's NAME instead of its VALUE is what made this one invisible.
Expect("Brave Talk widget uses the real pref value, not the constant's name",
    src.Contains("\"brave.new_tab_page.show_together\", false", StringComparison.Ordinal)
    && !HasNonCommentText(src, "new_tab_page.show_brave_talk"));

// --- chrome://flags entries that Chromium removed ---
// The labs list was annotated "verified present" and had never actually been checked against
// source. 23 of 83 entries did not exist in chromium/chrome/browser/about_flags.cc, brave's
// about_flags.cc, or flag-metadata.json — removed upstream years ago and inert ever since.
//
// The ones that mattered were @2 (disable) entries for telemetry and phone-home features, and
// every one of those is already covered by a managed policy that does work: media routing by
// EnableMediaRouter, reporting by MetricsReportingEnabled and DomainReliabilityAllowed, the
// ad APIs by the PrivacySandbox* set. So the intent survives; only the dead spelling went.
foreach (var removedFlag in new[]
{
    "enable-hardware-overlays", "enable-native-gpu-memory-buffers",
    "enable-gpu-memory-buffer-compositor-resources", "intensive-wake-up-throttling",
    "enable-desktop-pwas", "enable-fenced-frames", "brave-vpn", "brave-wayback-machine",
    "brave-video-transcript", "brave-ephemeral-storage", "reduce-accept-language",
    "privacy-sandbox-ads-apis", "enable-privacy-sandbox-ads-apis", "fedcm", "enable-webusb",
    "enable-domain-reliability", "enable-metrics-reporting", "media-router",
    "cast-media-route-provider", "price-tracking", "optimization-guide-model-execution",
    "compose", "read-later",
})
{
    Expect($"removed flag stays gone: {removedFlag}",
        !System.Text.RegularExpressions.Regex.IsMatch(src, $@"""{System.Text.RegularExpressions.Regex.Escape(removedFlag)}@\d"""));
}
// Two were not removed, just renamed. Deleting them would have lost a working setting.
Expect("renamed flags use their current upstream spelling",
    src.Contains("\"smooth-scrolling@1\"", StringComparison.Ordinal)
    && src.Contains("\"partition-visited-link-database-with-self-links@1\"", StringComparison.Ordinal));
// The policies those dead @2 flags were standing in for have to actually be in the pack,
// or removing the flags would quietly drop the protection instead of relocating it.
foreach (var covering in new[]
{
    "EnableMediaRouter", "MetricsReportingEnabled", "DomainReliabilityAllowed",
    "PrivacySandboxAdTopicsEnabled", "PrivacySandboxAdMeasurementEnabled",
    "IntensiveWakeUpThrottlingEnabled", "BraveVPNDisabled", "ShoppingListEnabled",
})
{
    Expect($"policy still covers the retired flag's intent: {covering}",
        System.Text.RegularExpressions.Regex.IsMatch(src, $@"\(""{covering}"",\s*\d+,\s*RegistryValueKind"));
}

// Apply verification is internal. Opening policy/settings tabs stole focus, first as two
// windows and later as one; neither is acceptable for a gaming utility running by consent.
Expect("Apply never launches Brave for verification",
    !HasNonCommentText(src, "brave://policy")
    && !HasNonCommentText(src, "brave://settings/shields/filters")
    && !HasNonCommentText(src, "FileName = install.ExePath"));
Expect("the Proton Pass extension page is not opened",
    !HasNonCommentText(src, "chrome-extension://"),
    "navigating to an unloaded extension shows Brave's blocked-by-administrator page");

// --- AMOLED: a modifier is not a switch ---
// brave.darker_mode is real, but brave-core documents it as making the UI darker "with
// 'dark mode' enabled". Without brave.dark_mode pinned to 1 (BraveDarkModeType dark) it is
// inert on any machine following a light system theme — which is how the AMOLED request
// stayed unfulfilled while the pref was already being written.
Expect("dark mode is pinned, not left to follow the system theme",
    src.Contains("\"brave.dark_mode\", 1", StringComparison.Ordinal));
Expect("the darker theme rides on top of pinned dark mode",
    src.Contains("\"brave.darker_mode\", true", StringComparison.Ordinal));
Expect("the ultra-dark theme flag is enabled",
    src.Contains("brave-ultra-dark-theme@1", StringComparison.Ordinal));
// Chromium renamed these prefs and kept the old spellings registered as dead weight.
// Writing the un-suffixed name is a silent no-op, so the suffix is the whole assertion.
Expect("theme prefs use the current 2-suffixed Chromium names",
    src.Contains("\"browser.theme.color_scheme2\", 2", StringComparison.Ordinal)
    && src.Contains("\"browser.theme.is_grayscale2\", true", StringComparison.Ordinal));
Expect("the deprecated un-suffixed theme prefs are not written",
    !System.Text.RegularExpressions.Regex.IsMatch(src,
        @"""browser\.theme\.(color_scheme|is_grayscale|user_color)""\s*,"));

// Detect must require both halves. Reporting AMOLED applied off darker_mode alone is
// exactly the false green this release exists to remove.
if (File.Exists(detectPath))
{
    Expect("AMOLED detect requires dark mode AND the darker theme",
        detectSrc.Contains("\"\\\"dark_mode\\\":1\"", StringComparison.Ordinal)
        && detectSrc.Contains("\"\\\"darker_mode\\\":true\"", StringComparison.Ordinal));
    Expect("AMOLED detect no longer accepts the phantom black-background key",
        !HasNonCommentText(detectSrc, "selected_value"));
}
Expect("new tab: stats card shown, all other widgets off",
    src.Contains("\"brave.new_tab_page.show_stats\", true", StringComparison.Ordinal)
    && src.Contains("\"brave.new_tab_page.hide_all_widgets\", false", StringComparison.Ordinal));
// Bookmarks bar: new tab page only. The managed policy would hide it everywhere and
// override the pref, so the policy must stay OUT and the pref must stay false.
// Matches the POLICY-PACK tuple shape specifically. A bare name match would also hit the
// DeleteValue call that removes the retired policy, i.e. flag the fix as the bug.
Expect("bookmark bar is not force-hidden by policy",
    !System.Text.RegularExpressions.Regex.IsMatch(src,
        @"\(""BookmarkBarEnabled"",\s*\d+,\s*RegistryValueKind"));
Expect("bookmark bar shows on the new tab page only",
    src.Contains("\"bookmark_bar.show_on_all_tabs\", false", StringComparison.Ordinal));
// Proton Pass is reported, never force-installed. The force-list pointed at the Chrome
// Web Store update URL, which Brave rejects as "blocked by administrator" — and a
// force-installed extension cannot be removed by the user, which contradicts consent-first.
// Dropping a policy from the pack stops Exo writing it; it does not remove one an older
// build already put in HKLM, and a managed policy keeps overriding the profile pref
// forever. Apply has to clean up after previous versions, not just stop adding to them.
Expect("apply clears policies retired from the pack",
    src.Contains("RemoveRetiredPolicies(admin, elevOps)", StringComparison.Ordinal)
    && src.Contains("SafeBrowsingProtectionLevel", StringComparison.Ordinal)
    && src.Contains("key.DeleteValue(name, false)", StringComparison.Ordinal));
Expect("retired force-list is cleared on apply, not only on repair",
    src.Contains("DeleteSubKeyTree(PolicyPath + @\"\\ExtensionInstallForcelist\", false)", StringComparison.Ordinal));
Expect("retired-policy cleanup reports what it could not remove",
    src.Contains("need elevation", StringComparison.Ordinal)
    && src.Contains("Id = \"retired-policies\"", StringComparison.Ordinal));
Expect("Proton Pass is still identified", src.Contains("ghmbeldphafepmbegfdlkpapadhbakde", StringComparison.Ordinal));
// Scoped to the WRITE, not the identifier — Repair must keep deleting the key, so
// banning the string outright would forbid the cleanup path too.
Expect("Proton Pass is not force-installed",
    !HasNonCommentText(src, "clients2.google.com/service/update2/crx")
    && !HasNonCommentText(src, "const string forcePath = PolicyPath"));
Expect("Repair still clears a force-list left by an older build",
    src.Contains("RemoveExtensionForceList(admin, elevOps)", StringComparison.Ordinal));

// GPU preference: see the "GPU routing" block near the end of this file. The old
// assertion here required GpuPreference=2, which encoded the wrong policy — Brave is a
// Chromium UI process and belongs on the integrated GPU when there is one, exactly like
// Steam's webhelper. Asserting the corrected policy in one place instead of two.

// Repair must be a real undo: full pre-apply snapshot + restore, not a partial reset.
Expect("full snapshot before apply", src.Contains("WriteFullSnapshot(install)", StringComparison.Ordinal));
Expect("full snapshot restore on repair", src.Contains("RestoreFullSnapshot(install)", StringComparison.Ordinal));

// Never silently wipe user data (passwords/bookmarks/history) -- only scoped,
// documented cache/vault table clears are allowed.
Expect("no full profile wipe",
    !src.Contains("Directory.Delete(install.UserData", StringComparison.Ordinal)
    && !src.Contains(".UserData, true)", StringComparison.Ordinal));

// --- GPU routing: one policy shared with Steam, and undone by Repair ---
// Brave used to stamp GpuPreference=2 unconditionally: the opposite call to Steam for
// the same class of Chromium process, and an inert write on the single-GPU desktops
// most users have. It also was never snapshotted, so Repair left the stamp behind.
Expect("Brave GPU routing goes through the shared policy",
    src.Contains("GpuTopology.RouteBrowserUi", StringComparison.Ordinal));
// Comment-aware: the doc comment on ApplyGpu explains what the old behaviour was and
// why it was wrong, and must not trip the check that the behaviour is gone.
Expect("Brave does not hard-code high-performance GPU",
    !HasNonCommentText(src, "GpuPreference=2"));
Expect("Brave snapshots GPU preferences before stamping",
    src.Contains("GpuTopology.SnapshotPreferences", StringComparison.Ordinal)
    && src.Contains("gpu-preferences.json", StringComparison.Ordinal));
Expect("Brave Repair restores the GPU preference",
    src.Contains("RestoreGpu(install)", StringComparison.Ordinal)
    && src.Contains("GpuTopology.RestorePreferences", StringComparison.Ordinal));

// --- Honesty: a step that failed at everything must not report ok ---
// Same class of bug as the Steam tray step: failures were swallowed per-item and the
// step returned ok regardless, so Apply claimed success while Detect showed not-applied.
Expect("update-task disabling distinguishes denied from absent",
    src.Contains("denied > 0", StringComparison.Ordinal)
    && src.Contains("no Brave update tasks present", StringComparison.Ordinal)
    && src.Contains("needs elevation", StringComparison.Ordinal));
Expect("a Run key we matched but could not delete is reported",
    src.Contains("stuck++", StringComparison.Ordinal)
    && src.Contains("stuck == 0 ? \"ok\"", StringComparison.Ordinal));
Expect("cache clears report what stayed locked",
    src.Contains("locked == 0 ? \"ok\" : \"partial\"", StringComparison.Ordinal));

// The shared helper must clear rather than stamp when there is no second adapter,
// and must restore a recorded absence as a delete (not as a zero).
var gpuTopoPath = Path.Combine(repo, "Exo", "Services", "GpuTopology.cs");
Expect("GpuTopology.cs present", File.Exists(gpuTopoPath));
if (File.Exists(gpuTopoPath))
{
    var topo = File.ReadAllText(gpuTopoPath);
    Expect("single-GPU machines get the stamp cleared, not written",
        topo.Contains("DeleteValue(exe, throwOnMissingValue: false)", StringComparison.Ordinal));
    Expect("a recorded absence restores as a delete",
        topo.Contains("if (before is null) key.DeleteValue(exe, throwOnMissingValue: false)", StringComparison.Ordinal));
}

Log($"=== SUMMARY failed={failed} ===");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
File.WriteAllLines(logPath, lines);
Environment.Exit(failed == 0 ? 0 : 1);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "VERSION")) && Directory.Exists(Path.Combine(dir.FullName, "Exo", "Services")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

/// <summary>
/// True when <paramref name="needle"/> appears on a line that is actual code rather than
/// a comment. Assertions that ban a pattern must not be tripped by the comment explaining
/// why that pattern is banned. Splits on '\n' and trims, so it is CRLF-safe.
/// </summary>
static bool HasNonCommentText(string source, string needle)
{
    foreach (var raw in source.Split('\n'))
    {
        var line = raw.Trim();
        if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
        if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
}
