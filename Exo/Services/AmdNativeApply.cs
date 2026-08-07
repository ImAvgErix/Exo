using System.Diagnostics;
using System.Text.Json;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// AMD Radeon debloat — the job Radeon Software Slimmer actually does, and nothing beyond it.
///
/// Scope is deliberately narrow and entirely reversible:
///   * autostart / updater / crash-reporter SCHEDULED TASKS are disabled, not deleted;
///   * telemetry values that ALREADY EXIST are set to off;
///   * nothing else.
///
/// What this explicitly does NOT do, and why:
///   * No driver install, uninstall or component stripping. AMD publishes no driver-lookup API
///     and their package has no setup.cfg manifest to edit, so the NVIDIA approach does not
///     transfer. Half a driver installer is how a machine ends up with no display.
///   * No service disabling. "AMD External Events Utility" carries FreeSync and the overlay;
///     Radeon Software Slimmer will happily switch it off and that is a real regression.
///   * No removal of the Radeon Software panel. It is the only UI for FreeSync, fan curves and
///     undervolting.
///   * No registry values are CREATED. A telemetry key that is not present is not a telemetry
///     key that is on, and inventing one is asserting a machine state nobody measured.
///
/// Everything changed is written to a snapshot first, so Repair restores what was actually
/// there rather than a guess at a default.
/// </summary>
public static class AmdNativeApply
{
    public const string Module = "amd";

    /// <summary>Repair baseline only — never the applyReport state file (amd-optimizer.json).</summary>
    private static string SnapshotPath =>
        Path.Combine(PathHelper.AppDataDir, "amd-snapshot.json");

    private static string LegacyCollisionPath =>
        Path.Combine(PathHelper.AppDataDir, "amd-optimizer.json");

    /// <summary>
    /// Autostart, updater and crash-reporting tasks. Every one of these is something the user
    /// can turn back on, and none of them is required for the display driver to work.
    /// </summary>
    private static readonly (string Match, string Why)[] TaskTargets =
    {
        ("StartCN", "Radeon Software auto-start."),
        ("StartDVR", "ReLive / instant-replay auto-start."),
        ("AMD Crash Defender", "Crash reporting."),
        ("AMDInstallLauncher", "Driver installer relaunch task."),
        ("AMDLinkUpdate", "AMD Link updater."),
        ("AMDRyzenMasterSDKTask", "Ryzen Master SDK background task."),
        ("AMD Software Update", "Driver update checker."),
        ("AMDFuel", "AMD Fuel utility task."),
        ("AUEPMaster", "AMD user experience program."),
        ("ModifyLinkUpdate", "AMD link update helper."),
        ("AMDInstallUEP", "AMD install telemetry."),
    };

    /// <summary>Telemetry values, switched off only where the value already exists.</summary>
    private static readonly (RegistryHive Hive, string Path, string Name)[] TelemetryValues =
    {
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "AnalyticsEnabled"),
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "TelemetryEnabled"),
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "AutoUpdate"),
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "ProfileEnabled"),
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "CollectAnalyticsData"),
        (RegistryHive.CurrentUser, @"Software\AMD\CN", "AllowAnalytics"),
        (RegistryHive.LocalMachine, @"SOFTWARE\AMD\CN", "AnalyticsEnabled"),
        (RegistryHive.LocalMachine, @"SOFTWARE\AMD\CN", "TelemetryEnabled"),
        (RegistryHive.LocalMachine, @"SOFTWARE\AMD\AUEP", "AUEPEnable"),
    };

    private sealed class Snapshot
    {
        public Dictionary<string, string> Tasks { get; set; } = new();   // taskPath -> "Ready"/"Disabled"
        public Dictionary<string, int> Telemetry { get; set; } = new();  // hive|path|name -> old dword
        public string? TakenUtc { get; set; }
    }

    /// <summary>
    /// Short model label: "AMD Ryzen 5 5600X 6-Core Processor" → "5600X".
    /// Falls back to a trimmed name when no model token is found.
    /// </summary>
    public static string ShortCpuName(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "AMD CPU";
        var s = full.Trim();
        // Prefer discrete model tokens: 5600X, 7800X3D, 9950X, 3700X, etc.
        var m = System.Text.RegularExpressions.Regex.Match(
            s, @"\b(\d{3,5}X3D|\d{3,5}X|\d{3,5}G|\d{3,5}T|\d{3,5}HS|\d{3,5}H|\d{3,5}U|\d{4,5})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        // Threadripper / EPYC style
        m = System.Text.RegularExpressions.Regex.Match(
            s, @"\b(Threadripper|EPYC)\s+(\w+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return $"{m.Groups[1].Value} {m.Groups[2].Value}";
        return s.Length > 24 ? s[..24].Trim() : s;
    }

    /// <summary>
    /// Chipset presence / currency against the live newest package revision (network +
    /// catalog floor). Package revisions and INF DriverVersion (5.x PSP) are different
    /// number spaces — only package revisions count for "current".
    /// </summary>
    public static (bool Present, bool Current, string? Name, string? Installed, string? Target, bool PackageVersion) ReadChipsetCurrency(
        HardwareInventory.CpuVendor vendor)
    {
        var (name, installed) = ChipsetDriverInstaller.FindInstalledChipset(vendor);
        var local = ChipsetDriverInstaller.ReadLocal();
        var catalogFloor = local.Spec?.TargetVersion;
        // Live newest: MUC + AMD pages (cached 12h). Falls back to catalog when offline.
        var target = ChipsetLatestLookup.ResolveLatest(
            vendor == HardwareInventory.CpuVendor.Amd ? "amd" : "intel",
            catalogFloor);

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(installed))
            return (false, false, null, null, target, false);

        var isIoDrivers = name is not null &&
                          name.Contains("IO Drivers", StringComparison.OrdinalIgnoreCase);
        // IO Drivers alone without a package revision: present, not "newest package".
        if (isIoDrivers)
            return (true, false, name, installed ?? "installed", target, false);

        if (string.IsNullOrWhiteSpace(installed))
            return (true, false, name, null, target, false);

        // Package-style version (7.x / 8.x) vs live/catalog target.
        var packageStyle = installed.StartsWith("7.", StringComparison.Ordinal)
                           || installed.StartsWith("8.", StringComparison.Ordinal)
                           || installed.StartsWith("6.", StringComparison.Ordinal);
        if (!packageStyle)
            return (true, false, name, installed, target, false);

        if (string.IsNullOrWhiteSpace(target))
            return (true, true, name, installed, target, true);

        var current = ChipsetDriverInstaller.CompareVersions(installed, target) >= 0;
        return (true, current, name, installed, target, true);
    }

    // ── Detect ────────────────────────────────────────────────────────────────────────────

    public static (bool Installed, bool Applied, List<(string Title, string Detail, bool Active)> Rows) Detect()
    {
        var rows = new List<(string, string, bool)>();
        var inventory = HardwareInventory.Read();
        var hasAmdGpu = inventory.HasAmdGpu;
        var hasAmdCpu = inventory.Cpu?.Vendor == HardwareInventory.CpuVendor.Amd;

        // Present whenever AMD CPU or Radeon exists.
        if (!hasAmdGpu && !hasAmdCpu) return (false, false, rows);

        // CPU-only (Ryzen + NVIDIA): chipset driver is the whole module.
        if (hasAmdCpu && !hasAmdGpu)
        {
            var shortCpu = ShortCpuName(inventory.Cpu?.Name);
            var socket = ChipsetDriverInstaller.InferSocket(
                inventory.Cpu?.Name ?? "", HardwareInventory.CpuVendor.Amd);
            // Info row (not a fail/check) — UI must not paint this as "off"/red.
            rows.Add((
                "CPU (info)",
                string.IsNullOrWhiteSpace(socket) ? shortCpu : $"{shortCpu} · {socket}",
                true));

            // Live health (package + PSP/SMBus/PCI…) — not "log said 8.07 once".
            var health = ChipsetDriverInstaller.AssessAmdChipsetHealth();
            foreach (var r in health.Rows)
                rows.Add((r.Title is "Package" ? "Chipset package" : r.Title + " (info)", r.Detail, r.Ok));

            var chip = ReadChipsetCurrency(HardwareInventory.CpuVendor.Amd);
            if (!health.Healthy || !chip.Current)
            {
                var need = chip.Target ?? "latest";
                rows.Add((
                    "Chipset clean install",
                    $"Apply installs core drivers silently (NVCleanstall-style). Newest package: {need}.",
                    false));
                return (true, false, rows);
            }

            rows.Add((
                "Chipset clean install",
                $"{chip.Name ?? "AMD Chipset Software"} {chip.Installed} — platform devices healthy.",
                true));
            return (true, true, rows);
        }

        var live = EnumerateTasks();
        var enabledBloat = live.Where(t => t.Enabled).ToList();
        var tasksOk = enabledBloat.Count == 0;
        rows.Add((
            "Radeon background tasks off",
            tasksOk
                ? "No Radeon auto-start, updater or crash-reporter tasks are running."
                : $"Still enabled: {string.Join(", ", enabledBloat.Select(t => t.Name).Take(4))}.",
            tasksOk));

        var telemetryOn = TelemetryValues
            .Select(v => ReadDword(v.Hive, v.Path, v.Name))
            .Where(v => v is not null and not 0)
            .Count();
        rows.Add((
            "Radeon telemetry off",
            telemetryOn == 0
                ? "No Radeon analytics or auto-update values are switched on."
                : $"{telemetryOn} telemetry value(s) still on.",
            telemetryOn == 0));

        return (true, rows.All(r => r.Item3), rows);
    }

    // ── Apply ─────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Apply(IProgress<string>? progress = null)
    {
        void Report(string m) => progress?.Report(m);
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();

        var inv = HardwareInventory.Read();
        if (!inv.HasAmdGpu)
        {
            var amdCpu = inv.Cpu?.Vendor == HardwareInventory.CpuVendor.Amd;
            if (!amdCpu)
            {
                return NativeApplyResult.Fail(Module,
                    "No AMD hardware found, so nothing was changed.",
                    new[] { new NativeApplyStep { Id = "detect", Status = "fail", Reason = "no AMD" } });
            }

            // Chipset-only: Applied only when the newest catalog chipset package is installed.
            // Apply does not silently install reboot-class chipset packages here — Detect is
            // honest about currency; the driver/chipset install path remains the install route.
            var chip = ReadChipsetCurrency(HardwareInventory.CpuVendor.Amd);
            var shortCpu = ShortCpuName(inv.Cpu?.Name);
            if (chip.Present && chip.Current)
            {
                var ver = string.IsNullOrWhiteSpace(chip.Installed) ? "installed" : chip.Installed;
                return NativeApplyResult.Success(Module,
                    $"{shortCpu}: AMD Chipset Software {ver} is current.",
                    new[] { new NativeApplyStep { Id = "chipset", Status = "ok", Reason = ver } });
            }

            var need = string.IsNullOrWhiteSpace(chip.Target) ? "8.07.x" : chip.Target;
            var have = chip.Present
                ? (string.IsNullOrWhiteSpace(chip.Installed) ? "incomplete install" : chip.Installed)
                : "not installed";
            return NativeApplyResult.Fail(Module,
                $"{shortCpu}: chipset package {have}; need {need}. " +
                "Install AMD Chipset Software from AMD (or re-run chipset install), then Verify.",
                new[]
                {
                    new NativeApplyStep
                    {
                        Id = "chipset",
                        Status = "fail",
                        Reason = chip.Present ? $"outdated/incomplete {have} < {need}" : "not installed",
                    },
                });
        }

        // Read and persist the entire pre-Exo baseline BEFORE anything moves. The old path
        // assembled this object as it mutated the machine and wrote it afterwards; a disk
        // error at that point left real changes with no Repair baseline.
        var liveTasks = EnumerateTasks();
        var telemetryBefore = TelemetryValues
            .Select(v => (Target: v, Value: ReadDword(v.Hive, v.Path, v.Name)))
            .ToList();
        var snap = new Snapshot { TakenUtc = DateTime.UtcNow.ToString("o") };
        foreach (var t in liveTasks.Where(t => t.Enabled))
            snap.Tasks[t.Path] = "Ready";
        foreach (var entry in telemetryBefore.Where(e => e.Value is not null and not 0))
        {
            var v = entry.Target;
            snap.Telemetry[$"{v.Hive}|{v.Path}|{v.Name}"] = entry.Value!.Value;
        }

        Report("Recording Radeon settings for Repair…");
        var snapshot = WriteSnapshot(snap);
        steps.Add(snapshot);
        if (snapshot.Status == "fail")
        {
            return NativeApplyResult.Fail(Module,
                $"Could not record the current Radeon settings, so nothing was changed. ({snapshot.Reason})",
                steps);
        }

        Report("Turning off Radeon auto-start and updater tasks…");
        var disabled = 0;
        var taskFailures = new List<string>();
        var taskPending = 0;
        foreach (var t in liveTasks)
        {
            if (!t.Enabled) continue;
            if (!admin)
            {
                elevOps.Add($"schtask:disable|{t.Path}");
                taskPending++;
            }
            else if (RunSchtasks($"/Change /TN \"{t.Path}\" /DISABLE")) disabled++;
            else taskFailures.Add(t.Name);
        }
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-tasks",
            Status = taskFailures.Count > 0
                ? disabled > 0 ? "partial" : "fail"
                : taskPending > 0 ? "pending-elev" : "ok",
            Reason = taskFailures.Count > 0
                ? $"disabled={disabled}; could not disable: {string.Join(", ", taskFailures)}"
                : taskPending > 0 ? $"{taskPending} task(s) need Administrator"
                : disabled > 0 ? $"disabled={disabled} task(s)" : "already off"
        });

        Report("Turning off Radeon analytics…");
        var userCleared = 0;
        var userFailures = new List<string>();
        var machineCleared = 0;
        var machineFailures = new List<string>();
        var machinePending = 0;
        foreach (var entry in telemetryBefore)
        {
            var v = entry.Target;
            var cur = entry.Value;
            if (cur is null || cur == 0) continue;   // absent stays absent; off stays off
            if (v.Hive == RegistryHive.LocalMachine && !admin)
            {
                elevOps.Add($"dword:HKLM\\{v.Path}|{v.Name}|0");
                machinePending++;
            }
            else if (WriteDword(v.Hive, v.Path, v.Name, 0))
            {
                if (v.Hive == RegistryHive.LocalMachine) machineCleared++;
                else userCleared++;
            }
            else if (v.Hive == RegistryHive.LocalMachine) machineFailures.Add(v.Name);
            else userFailures.Add(v.Name);
        }
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-telemetry-user",
            Status = userFailures.Count > 0 ? userCleared > 0 ? "partial" : "fail" : "ok",
            Reason = userFailures.Count > 0
                ? $"switched off={userCleared}; failed: {string.Join(", ", userFailures)}"
                : userCleared > 0 ? $"switched off {userCleared} user value(s)" : "no user values were on"
        });
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-telemetry-machine",
            Status = machineFailures.Count > 0
                ? machineCleared > 0 ? "partial" : "fail"
                : machinePending > 0 ? "pending-elev" : "ok",
            Reason = machineFailures.Count > 0
                ? $"switched off={machineCleared}; failed: {string.Join(", ", machineFailures)}"
                : machinePending > 0 ? $"{machinePending} machine value(s) need Administrator"
                : machineCleared > 0 ? $"switched off {machineCleared} machine value(s)" : "no machine values were on"
        });

        var incomplete = steps.Any(s => s.Status is "fail" or "partial");
        var changed = disabled + userCleared + machineCleared;
        var result = new NativeApplyResult
        {
            Ok = !incomplete,
            Module = Module,
            Message = incomplete
                ? "Radeon debloat incomplete — review the failed task or telemetry writes."
                : elevOps.Count > 0
                    ? $"Radeon debloat prepared; {elevOps.Count} change(s) need one Administrator approval."
                    : changed == 0
                        ? "Radeon was already clean — nothing needed changing."
                        : $"Radeon debloated: {disabled} background task(s) off, {userCleared + machineCleared} telemetry value(s) off.",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
        NativeModuleStateWriter.Save(Module, result);
        return result;
    }

    // ── Repair ────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Repair(IProgress<string>? progress = null)
    {
        void Report(string m) => progress?.Report(m);
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        var snap = LoadSnapshot();

        if (snap is null)
        {
            return NativeApplyResult.Fail(Module,
                "Nothing to restore — Exo has no saved Radeon settings on this PC, so nothing was changed back.",
                new[] { new NativeApplyStep { Id = "snapshot", Status = "skip", Reason = "no snapshot" } });
        }

        Report("Putting Radeon tasks back…");
        var restored = 0;
        var failures = new List<string>();
        var taskPending = 0;
        foreach (var (path, _) in snap.Tasks)
        {
            if (!admin)
            {
                elevOps.Add($"schtask:enable|{path}");
                taskPending++;
            }
            else if (RunSchtasks($"/Change /TN \"{path}\" /ENABLE")) restored++;
            else failures.Add(path);
        }
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-tasks",
            Status = failures.Count > 0
                ? restored > 0 ? "partial" : "fail"
                : taskPending > 0 ? "pending-elev" : "ok",
            Reason = failures.Count > 0
                ? $"re-enabled={restored}; could not restore: {string.Join(", ", failures)}"
                : taskPending > 0 ? $"{taskPending} task(s) need Administrator"
                : $"re-enabled={restored} task(s)"
        });

        Report("Putting Radeon analytics back…");
        var userBack = 0;
        var userFailures = new List<string>();
        var machineBack = 0;
        var machineFailures = new List<string>();
        var machinePending = 0;
        foreach (var (key, old) in snap.Telemetry)
        {
            var parts = key.Split('|');
            if (parts.Length != 3) continue;
            if (!Enum.TryParse<RegistryHive>(parts[0], out var hive)) continue;
            if (hive == RegistryHive.LocalMachine && !admin)
            {
                elevOps.Add($"dword:HKLM\\{parts[1]}|{parts[2]}|{old}");
                machinePending++;
            }
            else if (WriteDword(hive, parts[1], parts[2], old))
            {
                if (hive == RegistryHive.LocalMachine) machineBack++;
                else userBack++;
            }
            else if (hive == RegistryHive.LocalMachine) machineFailures.Add(parts[2]);
            else userFailures.Add(parts[2]);
        }
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-telemetry-user",
            Status = userFailures.Count > 0 ? userBack > 0 ? "partial" : "fail" : "ok",
            Reason = userFailures.Count > 0
                ? $"restored={userBack}; failed: {string.Join(", ", userFailures)}"
                : $"restored={userBack} user value(s)"
        });
        steps.Add(new NativeApplyStep
        {
            Id = "radeon-telemetry-machine",
            Status = machineFailures.Count > 0
                ? machineBack > 0 ? "partial" : "fail"
                : machinePending > 0 ? "pending-elev" : "ok",
            Reason = machineFailures.Count > 0
                ? $"restored={machineBack}; failed: {string.Join(", ", machineFailures)}"
                : machinePending > 0 ? $"{machinePending} machine value(s) need Administrator"
                : $"restored={machineBack} machine value(s)"
        });

        var incomplete = steps.Any(s => s.Status is "fail" or "partial");
        return new NativeApplyResult
        {
            Ok = !incomplete,
            Module = Module,
            Message = incomplete
                ? "Radeon was only partly restored — review the failed task or telemetry writes."
                : elevOps.Count > 0
                    ? $"Radeon restore prepared; {elevOps.Count} change(s) need one Administrator approval."
                    : "Radeon settings restored to what they were before Exo changed them.",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────────────

    internal sealed record TaskEntry(string Name, string Path, bool Enabled);

    /// <summary>
    /// Live task list via schtasks, matched against the target names. Only tasks that actually
    /// exist are ever reported or touched.
    /// </summary>
    internal static List<TaskEntry> EnumerateTasks()
    {
        var found = new List<TaskEntry>();
        try
        {
            var csv = RunCapture("schtasks.exe", "/Query /FO CSV /NH");
            foreach (var line in csv.Split('\n'))
            {
                var cells = SplitCsv(line);
                if (cells.Count < 3) continue;
                var path = cells[0].Trim();
                if (path.Length == 0) continue;
                var name = path.Contains('\\') ? path[(path.LastIndexOf('\\') + 1)..] : path;
                if (!TaskTargets.Any(t => name.Contains(t.Match, StringComparison.OrdinalIgnoreCase))) continue;
                var status = cells[2].Trim();
                var enabled = !status.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
                if (found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(new TaskEntry(name, path, enabled));
            }
        }
        catch { }
        return found;
    }

    internal static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes) { cells.Add(cur.ToString()); cur.Clear(); continue; }
            if (ch == '\r') continue;
            cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    private static int? ReadDword(RegistryHive hive, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            var v = key?.GetValue(name);
            return v is null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static bool WriteDword(RegistryHive hive, string path, string name, int value)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            // Open, never create: a value that is not there was never on.
            using var key = root.OpenSubKey(path, true);
            if (key is null) return false;
            if (key.GetValue(name) is null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(20_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p is null) return string.Empty;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return text;
    }

    private static NativeApplyStep WriteSnapshot(Snapshot snap)
    {
        if (LoadSnapshot() is not null)
            return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "keeping original pre-Exo snapshot" };

        if (File.Exists(SnapshotPath))
        {
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = "existing Radeon snapshot is unreadable" };
        }

        if (snap.Tasks.Count == 0 && snap.Telemetry.Count == 0)
        {
            return new NativeApplyStep
            {
                Id = "snapshot",
                Status = "ok",
                Reason = "nothing is on, so no mutable baseline is needed"
            };
        }

        return SaveSnapshot(snap)
            ? new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "pre-Exo Radeon state recorded" }
            : new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = "could not write amd-snapshot.json" };
    }

    private static bool SaveSnapshot(Snapshot snap)
    {
        try
        {
            Directory.CreateDirectory(PathHelper.AppDataDir);
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snap));
            return File.Exists(SnapshotPath);
        }
        catch { return false; }
    }

    private static Snapshot? LoadSnapshot()
    {
        try
        {
            foreach (var path in new[] { SnapshotPath, LegacyCollisionPath })
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                // applyReport state files have "applyReport" and no Tasks — skip them.
                if (text.Contains("\"applyReport\"", StringComparison.Ordinal) &&
                    !text.Contains("\"Tasks\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                var snap = JsonSerializer.Deserialize<Snapshot>(text);
                if (snap?.Tasks is { Count: > 0 } || snap?.Telemetry is { Count: > 0 })
                {
                    // Migrate legacy collision path once.
                    if (!string.Equals(path, SnapshotPath, StringComparison.OrdinalIgnoreCase) &&
                        snap is not null && !File.Exists(SnapshotPath))
                        _ = SaveSnapshot(snap);
                    return snap;
                }
            }
            return null;
        }
        catch { return null; }
    }
}
