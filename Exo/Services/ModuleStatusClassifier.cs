namespace Exo.Services;

/// <summary>
/// Decides whether a module reads as ready / applied / partial / missing.
///
/// This lived inline in WebHostBridge.MapState, where nothing could reach it. That is how
/// three separate bugs shipped — the worst being a module whose rows were all info titles,
/// so the "checkable" set was empty and an empty set was read as evidence of being applied.
/// The shell skips anything applied, so that module was invisible to every new user. Every
/// gate in the repo stayed green throughout, because nothing could call this logic with a
/// made-up machine and check the answer.
///
/// It is pure and static now specifically so Contracts.Smoke can drive it with the exact
/// scenarios that broke, and fail the build if any of them regress.
/// </summary>
internal static class ModuleStatusClassifier
{
    public readonly record struct Row(string? Title, bool IsActive);

    public readonly record struct Result(string Kind, string Text);

    /// <summary>
    /// Rows that describe the module rather than report a setting. They must never count
    /// toward "is this applied", because they are active by construction.
    /// </summary>
    public static bool IsInfoTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var t = title.Trim();
        // Firmware rows are advisory by construction: they report UEFI state Exo can read and
        // will never write. Counting them as checkable would mean a machine with XMP switched
        // off could never read as applied no matter how much Exo correctly set — punishing the
        // module for surfacing the most valuable thing it found. Suffix rather than an exact
        // title so the advisor can add findings without editing this list.
        if (t.EndsWith("(firmware)", StringComparison.OrdinalIgnoreCase)) return true;
        // Same rule for anything Exo only reports on. A chipset driver Exo deliberately does
        // not install must not hold its module at "partial" forever for not being applied.
        if (t.EndsWith("(info)", StringComparison.OrdinalIgnoreCase)) return true;
        return t.Equals("Optimization verified", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Anti-cheat untouched", StringComparison.OrdinalIgnoreCase)
               || t.Equals("One-click Repair ready", StringComparison.OrdinalIgnoreCase)
               // These two were info to NativeLiveDetect and checkable here, so Brave reported
               // IsApplied=true from detect and "partial - 1 still off" from this classifier on
               // the same request. NativeLiveDetect.IsInfo now delegates here; this is the list.
               || t.Equals("Proton Pass (optional)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Launcher junk cleaned", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Safe repair", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Policy", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Adapter", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Last apply", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Display scaling & color", StringComparison.OrdinalIgnoreCase)
               // Optional NVIDIA UI only — Exo applies display via NVAPI; panel presence is not a miss.
               || t.Equals("Control Panel access", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Latency / sync policy", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Stack profile", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Host gaming stack", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Profile", StringComparison.OrdinalIgnoreCase)
               || t.Equals("DLSS left alone", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Install / configs", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Method", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Ban-safe surface", StringComparison.OrdinalIgnoreCase)
               || t.Equals("NVIDIA Reflex", StringComparison.OrdinalIgnoreCase)
               || t.Equals("FPS limits off (menu/bg/battery)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Texture / Material / Detail", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Shadows / Bloom / AA", StringComparison.OrdinalIgnoreCase)
               // AMD platform presence rows — not settings Exo toggles.
               || t.Equals("AMD platform", StringComparison.OrdinalIgnoreCase)
               || t.Equals("AMD CPU (chipset)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Radeon GPU", StringComparison.OrdinalIgnoreCase)
               || t.Equals("CPU", StringComparison.OrdinalIgnoreCase)
               || t.Equals("CPU (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("PSP (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("SMBus (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("PCI (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("I2C (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("GPIO (info)", StringComparison.OrdinalIgnoreCase)
               || t.Equals("Chipset package", StringComparison.OrdinalIgnoreCase);
    }

    public static Result Classify(
        string id,
        bool isApplied,
        string? statusText,
        string? detail,
        IReadOnlyList<Row> features)
    {
        features ??= Array.Empty<Row>();
        var checkable = features.Where(f => !IsInfoTitle(f.Title)).ToList();
        var off = checkable.Where(f => !f.IsActive).Select(f => f.Title ?? "").ToList();
        var visibleOn = features.Count(f => f.IsActive);
        var visibleTotal = features.Count;

        var hostBlob = $"{statusText} {detail}".ToLowerInvariant();
        var missing = hostBlob.Contains("not installed")
                      || hostBlob.Contains("no nvidia")
                      || hostBlob.Contains("not found in steam");

        if (missing && !isApplied)
            return new Result("missing", "Missing target");

        // An empty `checkable` set is evidence of nothing — it used to be treated as
        // evidence of "applied" — the bug that made a module invisible to every new user.
        if (off.Count == 0 && (isApplied || (checkable.Count > 0 && checkable.All(f => f.IsActive))))
        {
            return new Result("applied",
                visibleTotal > 0 ? $"Applied · {visibleOn}/{visibleTotal} on" : "Applied");
        }

        // Checkable rows only: info rows are active by construction, so counting them here
        // pushed an untouched module carrying one straight from "ready" to "partial".
        if (off.Count > 0 && (isApplied || checkable.Any(f => f.IsActive)))
            return new Result("partial", $"Partial · {off.Count} still off · {visibleOn}/{visibleTotal} on");

        if (off.Count > 0)
        {
            return new Result("ready",
                off.Count == 1 ? $"Ready · 1 need Apply ({off[0]})" : $"Ready · {off.Count} need Apply");
        }

        return new Result("ready", "Ready");
    }
}
