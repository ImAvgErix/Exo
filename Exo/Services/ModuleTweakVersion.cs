using System.Text.Json;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// What a module's tweaks looked like the last time they were applied.
///
/// "Applied" used to mean "applied at some point", which is not the same as "applied with the
/// tweaks Exo ships today". Internet was reported applied and never re-offered after its tweak
/// set changed underneath it, so a machine sat on a superseded configuration indefinitely and
/// the orb called the whole rig good to go. Every module had the same hole; Internet is only
/// where it was noticed.
///
/// The version is a hand-set string per module, bumped in the same commit that changes what
/// that module writes. Deriving it from a hash of the apply code was tempting and wrong: a
/// comment edit would then invalidate everyone's state and re-prompt the world for nothing.
/// </summary>
internal static class ModuleTweakVersion
{
    /// <summary>
    /// Bump a module's entry when you change what it applies — not when you change how it is
    /// written, and not for detect-only or logging changes. The user-visible effect of a bump
    /// is one "this changed since you ran it" prompt per machine.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Current =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 4.4.2: the pre-Exo DRS database is now snapshotted on every Apply rather than only
            // under -SafePolicy (which production never passed), the native NVAPI path no longer
            // falls through to Profile Inspector on settings the driver simply does not
            // implement, and the telemetry debloat runs once instead of twice.
            // 4.4.6: the PowerMizer loop read .DriverDesc straight off a display class node.
            // A node without that value throws under StrictMode, the bare catch swallowed it,
            // and the loop abandoned every remaining node while the log still said "PowerMizer
            // tuned". Single-GPU desktops were unaffected (one node, always has DriverDesc);
            // hybrid and multi-adapter machines could lose the whole pass. Those machines read
            // "applied" today, so without this bump the repaired pass never runs on them.
            // 4.4.7: DisableDynamicPstate is removed rather than written. It crashed the NVIDIA
            // usermode driver (nvwgf2umx) on a real RTX 3070, and every machine that ran an
            // older Apply still has it in the display class node -- so this bump exists to get
            // the REMOVAL onto them, not to deliver a new tweak.
            ["nvidia"] = "4.5.4",
            // 4.5.5: Safe Browsing is no longer forced off, old disabling policies are
            // removed through the elevated batch, and the live contract covers the full
            // Shields/privacy/efficiency pack instead of accepting one half of Shields.
            ["brave"] = "4.5.5",
            ["discord"] = "4.5.3",
            ["steam"] = "4.5.4",
            ["system"] = "4.5.4",
            ["spotify"] = "4.5.4",
            // 4.5.3: congestion + bindings + Ethernet metric + feature majority in MatchesPreset.
            ["internet"] = "4.5.3",
            // 4.5.5: task and HKLM telemetry changes now use the shared one-prompt elevated
            // batch, with a durable snapshot written before the first mutation and live readback.
            ["amd"] = "4.5.5",
        };

    private static string StampPath => Path.Combine(PathHelper.AppDataDir, "tweak-versions.json");

    public static string? CurrentFor(string module) =>
        Current.TryGetValue(module ?? "", out var v) ? v : null;

    private static Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(StampPath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StampPath))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Records what was applied. Called only after an apply that actually succeeded.</summary>
    public static void Stamp(string module)
    {
        var current = CurrentFor(module);
        if (current is null) return;
        try
        {
            var all = Read();
            all[module] = current;
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.WriteAllText(StampPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a missing stamp re-prompts once; a thrown apply is worse */ }
    }

    /// <summary>
    /// Whether this module's tweaks have moved on since it was last applied.
    ///
    /// No stamp counts as stale, and that is a reversal of my first attempt. I argued that
    /// re-prompting everyone once after an upgrade would look like breakage — but the honest
    /// reading of an absent stamp is not "current", it is "Exo has no idea what is on this
    /// machine", and that is much closer to stale. It also produced the exact bug it was
    /// meant to avoid: a machine whose Internet tuning had changed was still never offered,
    /// because it had no stamp to be stale against.
    ///
    /// The cost is one prompt per module, once, on the upgrade that introduces stamps. That
    /// is a fair price for never silently sitting on a superseded configuration again.
    /// </summary>
    public static bool IsStale(string module)
    {
        var current = CurrentFor(module);
        if (current is null) return false;
        if (!Read().TryGetValue(module, out var seen)) return true;
        return !string.Equals(seen, current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when there is no record at all of what this module last applied, as opposed to a
    /// record that has since been superseded.
    ///
    /// Both make <see cref="IsStale"/> true and both should re-offer the module, but they are
    /// different facts and the user is owed the right one: "Exo's tuning changed since you ran
    /// it" is wrong for a module whose version string has not moved and which simply predates
    /// the stamp file. On this machine tweak-versions.json holds three of eight modules, so
    /// five were being told their tuning had changed when nothing had.
    /// </summary>
    public static bool HasNoStamp(string module) =>
        CurrentFor(module) is not null && !Read().ContainsKey(module);
}
