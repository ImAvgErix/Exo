using System.Diagnostics;
using System.Text.Json;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Exo's answer to DDU: remove every trace of the NVIDIA driver from Safe Mode, so the next
/// install lands on a clean machine.
///
/// This is the most destructive thing in the app and the only operation whose failure mode is
/// no display at all. Everything else Exo does is snapshotted and reversible; a half-removed
/// display driver is recovered with a Windows USB, not with Repair. The design is shaped
/// entirely around two facts:
///
/// <list type="number">
/// <item><b>The Safe Mode flag is the real danger, not the deletions.</b> Setting
/// <c>bcdedit safeboot</c> and dying before clearing it leaves a machine that boots into Safe
/// Mode forever, and the user has no working Exo to fix it with. So the flag is cleared on
/// startup by <see cref="ClearPendingBootFlagIfAny"/> no matter how the previous run ended,
/// before anything else is attempted.</item>
/// <item><b>It must refuse to run when there is nothing wrong.</b> Sweeping a healthy machine
/// is all risk and no benefit, so <see cref="NvidiaDriverHealth"/> gates it.</item>
/// </list>
/// </summary>
internal static class NvidiaDriverCleaner
{
    /// <summary>
    /// Written before the reboot, read on next launch. Its existence means "a sweep was
    /// started and the boot flag may still be set" — the state that must never be silent.
    /// </summary>
    private static string StatePath => Path.Combine(PathHelper.AppDataDir, "driver-sweep.json");

    internal sealed record SweepState(string Stage, string StartedUtc, string Token);

    internal sealed record SweepPlan(
        IReadOnlyList<string> PackagesToRemove,
        IReadOnlyList<string> ServicesToRemove,
        IReadOnlyList<string> FoldersToRemove,
        IReadOnlyList<string> Reasons,
        string Token);

    // ── The boot flag, and getting out of Safe Mode whatever happened ─────────────────────

    /// <summary>
    /// Called unconditionally at startup, before anything else. If a sweep was in flight the
    /// machine may be sitting on a safeboot flag; clearing it here is what stops a crashed
    /// sweep from stranding someone in Safe Mode with no way back.
    ///
    /// Deliberately does NOT try to resume the sweep. A run that died once is not a run to
    /// automatically retry on a machine whose display driver is half removed.
    /// </summary>
    public static string? ClearPendingBootFlagIfAny()
    {
        SweepState? state = null;
        try
        {
            if (File.Exists(StatePath))
                state = JsonSerializer.Deserialize<SweepState>(File.ReadAllText(StatePath));
        }
        catch { /* an unreadable state file still means a sweep happened */ }

        if (state is null && !File.Exists(StatePath)) return null;

        // Clear the flag whether or not the file parsed. Being wrong about the stage is
        // survivable; leaving safeboot set is not.
        var cleared = RunTool("bcdedit.exe", "/deletevalue {current} safeboot", 30_000);
        try { File.Delete(StatePath); } catch { }

        return state?.Stage switch
        {
            "rebooting" => cleared.Ok
                ? "A driver sweep was interrupted before it ran. Normal boot restored; nothing was removed."
                : "A driver sweep was interrupted and the Safe Mode boot flag could not be cleared. Run `bcdedit /deletevalue {current} safeboot` from an admin prompt.",
            "sweeping" => "A driver sweep was interrupted while removing files. Reinstall the driver before gaming — some components may be missing.",
            _ => cleared.Ok ? "A previous driver sweep did not finish. Normal boot restored." : null
        };
    }

    /// <summary>
    /// The single thing startup calls. Two different situations look identical from the state
    /// file alone, and only one of them is a recovery:
    ///
    /// <list type="bullet">
    /// <item>Booted into Safe Mode with a sweep armed — this is the sweep running as intended.
    /// It does the removal and restores normal boot.</item>
    /// <item>Booted normally with a sweep armed — the reboot never reached Safe Mode, or the
    /// previous run died. Clear the flag and report; do not retry.</item>
    /// </list>
    /// </summary>
    public static string? ResumeOrRecover(IProgress<string>? progress = null)
    {
        if (!File.Exists(StatePath)) return null;

        if (IsSafeMode())
        {
            // Rebuild the plan from the machine as it is now rather than trusting a plan
            // serialised before a reboot — the store is what it is, and acting on a stale
            // list is how a sweep removes something that is no longer what it thought.
            var health = NvidiaDriverHealth.Check();
            var plan = Plan(health, CandidateFolders());
            var (ok, message) = Sweep(plan, progress);
            return message + (ok ? "" : " Nothing further was attempted.");
        }

        return ClearPendingBootFlagIfAny();
    }

    // ── Planning: pure, so the decision can be tested without a machine ───────────────────

    /// <summary>
    /// What a sweep would remove. Pure: everything is passed in. Nothing here touches disk,
    /// which is the point — the plan is shown before any consent is asked for.
    /// </summary>
    public static SweepPlan Plan(NvidiaDriverHealthLogic.Report health, IReadOnlyList<string> presentFolders)
    {
        var packages = health.NvidiaPackages.Select(p => p.OemInf).ToList();

        // Services Exo will unregister. NvContainer hosts the driver's own services and is
        // reinstalled by the next driver, so it goes; anything not matching NVIDIA does not.
        var services = new[]
        {
            "nvlddmkm", "NVDisplay.ContainerLocalSystem", "nvagent",
            "NvContainerLocalSystem", "NvTelemetryContainer", "FvSvc",
        }.ToList();

        var reasons = health.Findings.Select(f => $"{f.Title} — {f.Detail}").ToList();

        // The token exists for the same reason the driver installer has one: a caller that
        // only meant to look at the plan cannot produce it.
        var token = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", packages) + services.Count + presentFolders.Count)))[..16];

        return new SweepPlan(packages, services, presentFolders, reasons, token);
    }

    /// <summary>Folders a sweep would delete, filtered to the ones actually present.</summary>
    public static IReadOnlyList<string> CandidateFolders()
    {
        string Env(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation"),
            Path.Combine(Env("ProgramData"), "NVIDIA Corporation"),
            Path.Combine(Env("ProgramData"), "NVIDIA"),
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "NVIDIA"),
            Path.Combine(Env("LOCALAPPDATA"), "NVIDIA"),
            Path.Combine(Env("LOCALAPPDATA"), "NVIDIA Corporation"),
        };
        var present = new List<string>();
        foreach (var c in candidates)
        {
            try { if (c.Length > 0 && Directory.Exists(c)) present.Add(c); } catch { }
        }
        return present;
    }

    // ── Doing it ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Arms the sweep: writes the state file, sets the one-shot Safe Mode boot, and returns.
    /// The caller reboots. Requires the plan's token and an explicit confirmation, so no
    /// single call can arm it by accident.
    ///
    /// The state file is written BEFORE the boot flag, deliberately. If the process dies
    /// between the two, startup finds the file and clears a flag that was never set — which is
    /// harmless. The other order can set the flag with nothing recording that it happened.
    /// </summary>
    public static (bool Ok, string Message) Arm(
        SweepPlan plan, string token, bool userConfirmed, IProgress<string>? progress = null)
    {
        if (!userConfirmed) return (false, "Not confirmed — nothing was changed.");
        if (!string.Equals(token, plan.Token, StringComparison.Ordinal))
            return (false, "That plan is not the one that was shown. Nothing was changed.");
        if (!NativeReg.IsAdministrator())
            return (false, "A driver sweep needs administrator rights.");

        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(
                new SweepState("rebooting", DateTimeOffset.UtcNow.ToString("o"), plan.Token)));
        }
        catch (Exception ex) { return (false, $"Could not record the sweep, so it was not started: {ex.Message}"); }

        progress?.Report("Setting a one-time Safe Mode boot…");
        var set = RunTool("bcdedit.exe", "/set {current} safeboot minimal", 30_000);
        if (!set.Ok)
        {
            try { File.Delete(StatePath); } catch { }
            return (false, $"Could not set the Safe Mode boot: {set.Message}. Nothing was changed.");
        }

        return (true, "Ready. Reboot when you are — Windows will start in Safe Mode, Exo will finish the sweep, and it will restart normally afterwards.");
    }

    /// <summary>
    /// The removal itself. Only ever runs in Safe Mode: outside it the driver is loaded, the
    /// files are locked, and a partial delete is the worst of both worlds.
    /// </summary>
    public static (bool Ok, string Message) Sweep(SweepPlan plan, IProgress<string>? progress = null)
    {
        if (!IsSafeMode())
            return (false, "A sweep only runs in Safe Mode — outside it the driver files are in use and a partial removal is worse than none.");
        if (!NativeReg.IsAdministrator())
            return (false, "A driver sweep needs administrator rights.");

        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(
                new SweepState("sweeping", DateTimeOffset.UtcNow.ToString("o"), plan.Token)));
        }
        catch { /* the flag clear on startup matters more than the stage label */ }

        var removed = 0;
        var failed = new List<string>();

        foreach (var inf in plan.PackagesToRemove)
        {
            progress?.Report($"Removing driver package {inf}…");
            var r = RunTool("pnputil.exe", $"/delete-driver {inf} /uninstall /force", 120_000);
            if (r.Ok) removed++; else failed.Add($"{inf}: {r.Message}");
        }

        foreach (var svc in plan.ServicesToRemove)
        {
            progress?.Report($"Unregistering {svc}…");
            // Failure here is expected and fine: most machines will not have most of these.
            RunTool("sc.exe", $"delete \"{svc}\"", 20_000);
        }

        foreach (var folder in plan.FoldersToRemove)
        {
            progress?.Report($"Deleting {folder}…");
            try { Directory.Delete(folder, recursive: true); removed++; }
            catch (Exception ex) { failed.Add($"{folder}: {ex.Message}"); }
        }

        progress?.Report("Restoring normal boot…");
        var cleared = RunTool("bcdedit.exe", "/deletevalue {current} safeboot", 30_000);
        try { File.Delete(StatePath); } catch { }

        if (!cleared.Ok)
            return (false, "The sweep finished but the Safe Mode boot flag could not be cleared. Run `bcdedit /deletevalue {current} safeboot` from an admin prompt before rebooting.");

        var summary = $"Removed {removed} item(s).";
        if (failed.Count > 0) summary += $" {failed.Count} could not be removed: {string.Join("; ", failed.Take(3))}";
        return (true, summary + " Reboot, then install a driver — the machine has no NVIDIA display driver right now.");
    }

    /// <summary>Windows sets this for the duration of a Safe Mode session.</summary>
    public static bool IsSafeMode()
    {
        try { return string.Equals(Environment.GetEnvironmentVariable("SAFEBOOT_OPTION"), "MINIMAL", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static (bool Ok, string Message) RunTool(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "did not start");
            // Drain both pipes while waiting — an unread redirected pipe that fills up
            // blocks the child, and the run then dies here as "timed out".
            _ = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "timed out"); }
            return p.ExitCode == 0
                ? (true, "ok")
                : (false, $"exit {p.ExitCode}: {stderr.Result.Trim()}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
