using System.Text.Json;
using System.Text.Json.Nodes;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Whole-machine tuning: the levers in <c>docs/SYSTEM-EVIDENCE.md</c> marked <b>APPLY</b>.
///
/// The old Windows module was deleted in 4.1 because it was mostly folklore. This is not that
/// module. Every row here is a setting Windows genuinely reads, that Exo can verify afterwards
/// and put back exactly as it found it. Anything that needed a kernel driver (CPU undervolt),
/// anything that lives in firmware (XMP, PBO, C-states, chassis fans), and anything that only
/// sounds like a tweak (<c>Win32PrioritySeparation</c>, standby-list cleaning, SSD defrag) is
/// deliberately absent — see the REFUSE and ADVISE tiers in that document.
///
/// Two rules shape the whole file:
///
/// <list type="bullet">
/// <item><b>AC only.</b> Every power setting is written to the on-mains profile and the battery
/// profile is left untouched. Forcing a laptop to keep every core unparked and PCIe links awake
/// on battery is a real cost paid for nothing while the machine is not being gamed on.</item>
/// <item><b>The active scheme, not our own.</b> Exo edits whichever power plan the user is
/// running rather than switching them to one it made up. Switching plans is visible, surprising,
/// and undoes itself the moment the vendor utility switches back.</item>
/// </list>
///
/// Power settings are <i>read</i> from the registry (reliable, and where Windows actually keeps
/// them) but <i>written</i> through <c>powercfg</c>, which is the only way to make the running
/// session pick them up. Writing the registry alone leaves the value visible in a query and
/// inert in practice — the exact failure shape this codebase has been audited to eliminate.
/// </summary>
internal static class SystemNativeApply
{
    private const string Module = "system";

    private const string PowerSchemes = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    private static string SnapshotPath => Path.Combine(PathHelper.AppDataDir, "system-snapshot.json");

    // Power settings used to be written in-place onto whichever plan the user was running.
    // They now live entirely in the Exo plan (ExoPowerPlan) instead, for two reasons: the two
    // approaches actively fought each other - the in-place path re-activated the old plan at
    // the end, switching straight back off the Exo one - and editing a plan the user chose is
    // worse than creating one Exo can delete again. Nothing here touches their plans now.

    /// <summary>A registry lever, with the value Exo wants and the reason it wants it.</summary>
    /// <remarks>
    /// Definitions now live in the engine catalog (<see cref="Exo.Engine.SystemLeverCatalog"/>)
    /// as the single source of truth; this module consumes them so detect/apply/repair and the
    /// tweak engine can never drift apart.
    /// </remarks>
    private sealed record RegLever(
        string Id,
        string Title,
        string Hive,
        string Path,
        string Name,
        int Value,
        string Why,
        bool NeedsReboot = false);

    private static readonly RegLever[] RegLevers =
        Exo.Engine.SystemLeverCatalog.Levers
            .Select(l => new RegLever(l.Id, l.Title, l.Hive, l.Path, l.Name, l.Value, l.Why, l.NeedsReboot))
            .ToArray();

    // ── USB device power saving ───────────────────────────────────────────────────────────

    /// <summary>
    /// "Allow the computer to turn off this device to save power", the per-device checkbox on
    /// the Power Management tab in Device Manager, exposed as <c>MSPower_DeviceEnable</c> in
    /// <c>root\wmi</c>.
    ///
    /// This is a different lever from the powercfg USB selective-suspend setting Exo already
    /// writes through the Exo plan. That one is global policy; this one is the per-device opt
    /// in, and a device with it ticked can still be suspended regardless of the plan. Both are
    /// needed for a mouse or keyboard to never sleep mid-game.
    ///
    /// Scoped to instances whose name starts with <c>USB\</c>, and that scoping is load-bearing
    /// rather than cosmetic: this machine's fifteen instances include a PCI Intel I226-V NIC,
    /// and the Internet module owns NIC power management (SelectiveSuspend, WakeOnMagicPacket,
    /// D0PacketCoalescing). Writing every instance here would put two modules on one setting.
    /// </summary>
    private const string UsbInstancePrefix = @"USB\";

    internal readonly record struct UsbPowerDevice(string InstanceName, bool Enable);

    /// <summary>
    /// Live read of the USB power-management opt-in. Returns null when the class cannot be
    /// read at all, which is a different fact from "no devices" and is reported as such —
    /// a machine with no readable WMI must not silently read as applied.
    /// </summary>
    internal static List<UsbPowerDevice>? ReadUsbPowerDevices()
    {
        try
        {
            // Late-bound WMI, matching HomeDashboardReader and FirmwareAdvisor — this codebase
            // deliberately carries no System.Management package reference.
            var t = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (t is null) return null;
            dynamic locator = Activator.CreateInstance(t)!;
            dynamic services = locator.ConnectServer(".", "root\\wmi");
            dynamic items = services.ExecQuery("SELECT InstanceName, Enable FROM MSPower_DeviceEnable");
            var list = new List<UsbPowerDevice>();
            foreach (dynamic item in items)
            {
                try
                {
                    var name = item.InstanceName as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(UsbInstancePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new UsbPowerDevice(name, Convert.ToBoolean(item.Enable)));
                }
                catch { }
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes one elevated op covering every listed device. One op rather than one per device
    /// so the elevated pack makes a single WMI query instead of eleven.
    /// </summary>
    private static string BuildUsbPowerOp(IEnumerable<UsbPowerDevice> devices) =>
        "usbpower:" + string.Join(";", devices.Select(d => $"{d.InstanceName}={(d.Enable ? 1 : 0)}"));

    // ── Apply ─────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Apply(bool experimental, IProgress<string>? progress = null)
    {
        _ = experimental;
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        void Report(string m) => progress?.Report(m);

        var scheme = ActiveSchemeGuid();
        if (scheme is null)
        {
            // Without the active scheme GUID nothing below can be addressed or restored.
            // Failing here is correct: a partial apply we cannot undo is worse than none.
            return NativeApplyResult.Fail(Module,
                "Could not read the active Windows power plan, so no power settings were changed.");
        }

        Report("Recording current settings for Repair…");
        var snapshot = WriteSnapshot(scheme);
        steps.Add(snapshot);
        if (snapshot.Status == "fail")
        {
            // Without a snapshot there is no undo, and this module changes CPU power policy and
            // machine-wide registry state. Applying anyway would leave the user with changes
            // Repair cannot reverse — so it stops here instead.
            return NativeApplyResult.Fail(Module,
                $"Could not record the current settings, so nothing was changed. ({snapshot.Reason})",
                steps);
        }

        Report("Building a power plan for this CPU…");
        var cpu = ExoPowerPlan.DetectTopology();
        var planSettings = ExoPowerPlan.SettingsFor(cpu);
        elevOps.AddRange(ExoPowerPlan.BuildApplyOps(cpu));
        steps.Add(new NativeApplyStep
        {
            Id = "power-plan",
            Status = "pending-elev",
            Reason = $"{ExoPowerPlan.PlanName(cpu)} - {planSettings.Count} settings for {cpu.Summary}"
        });

        Report("Scheduling, capture and storage flags…");
        steps.AddRange(ApplyRegLevers(admin, elevOps));

        Report("USB device power management…");
        steps.Add(ApplyUsbPower(elevOps));

        var needsElev = elevOps.Count > 0 && !admin;
        // Ok stays true until NativeApplyService finishes elevation and rewrites from step
        // statuses; pre-elev success with pending-elev is intentional so the elev pack still runs.
        return new NativeApplyResult
        {
            Ok = true,
            Module = Module,
            Message = BuildMessage(steps),
            Steps = steps,
            NeedsElevation = needsElev,
            ElevatedHklmOps = elevOps
        };
    }

    private static IEnumerable<NativeApplyStep> ApplyRegLevers(bool admin, List<string> elevOps)
    {
        foreach (var lever in RegLevers)
        {
            // HKCU is writable without elevation; HKLM is staged into the single elevated
            // batch so the whole module costs one UAC prompt rather than one per key.
            if (lever.Hive == "HKCU")
            {
                var ok = NativeReg.TrySetDword(lever.Hive, lever.Path, lever.Name, lever.Value);
                yield return new NativeApplyStep
                {
                    Id = lever.Id,
                    Status = ok ? "ok" : "fail",
                    Reason = ok ? lever.Why : $"could not write {lever.Name}"
                };
                continue;
            }

            if (admin)
            {
                var ok = NativeReg.TrySetDword(lever.Hive, lever.Path, lever.Name, lever.Value);
                yield return new NativeApplyStep
                {
                    Id = lever.Id,
                    Status = ok ? "ok" : "fail",
                    Reason = ok
                        ? lever.Why + (lever.NeedsReboot ? " Takes effect after a reboot." : "")
                        : $"could not write {lever.Name}"
                };
                continue;
            }

            elevOps.Add($"dword:{lever.Hive}\\{lever.Path}|{lever.Name}|{lever.Value}");
            yield return new NativeApplyStep
            {
                Id = lever.Id,
                Status = "pending-elev",
                Reason = lever.Why + (lever.NeedsReboot ? " Takes effect after a reboot." : "")
            };
        }
    }

    private static NativeApplyStep ApplyUsbPower(List<string> elevOps)
    {
        var devices = ReadUsbPowerDevices();
        if (devices is null)
            return new NativeApplyStep
            {
                Id = "usb-power",
                Status = "skip",
                Reason = "MSPower_DeviceEnable could not be read on this machine"
            };
        if (devices.Count == 0)
            return new NativeApplyStep
            {
                Id = "usb-power",
                Status = "skip",
                Reason = "no USB devices expose a power-management opt-in"
            };

        // Always routed through the elevated pack. Writing MSPower_DeviceEnable needs admin,
        // and staging it here keeps the module at one UAC prompt like every other lever.
        var wanted = devices.Select(d => d with { Enable = false });
        elevOps.Add(BuildUsbPowerOp(wanted));
        var alreadyOff = devices.Count(d => !d.Enable);
        return new NativeApplyStep
        {
            Id = "usb-power",
            Status = "pending-elev",
            Reason = $"stop Windows suspending {devices.Count} USB device(s) to save power " +
                     $"({alreadyOff} already off)"
        };
    }

    private static string BuildMessage(IReadOnlyList<NativeApplyStep> steps)
    {
        var failed = steps.Count(s => s.Status == "fail");
        var reboot = RegLevers.Any(l => l.NeedsReboot);
        var tail = reboot ? " GPU scheduling changes apply after a reboot." : "";
        return failed == 0
            ? "Machine tuned: CPU power, PCIe, USB, scheduling, capture and storage." + tail
            : $"Applied with {failed} step(s) failing — open the log for the exact keys." + tail;
    }

    // ── Repair ────────────────────────────────────────────────────────────────────────────

    public static NativeApplyResult Repair(IProgress<string>? progress = null)
    {
        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        void Report(string m) => progress?.Report(m);

        Report("Reading the pre-Exo snapshot…");
        if (!File.Exists(SnapshotPath))
        {
            // Never guess. Writing Windows' defaults over settings the user may have chosen
            // themselves is not a restore, and claiming otherwise would be the same dishonesty
            // this module is built to avoid.
            return NativeApplyResult.Fail(Module,
                "No snapshot from a previous Apply, so there is nothing to put back. " +
                "Repair only restores values Exo recorded before changing them.");
        }

        JsonNode? snap;
        try { snap = JsonNode.Parse(File.ReadAllText(SnapshotPath)); }
        catch (Exception ex) { return NativeApplyResult.Fail(Module, $"Snapshot unreadable: {ex.Message}"); }
        if (snap is null) return NativeApplyResult.Fail(Module, "Snapshot was empty.");

        Report("Restoring the previous power plan…");
        steps.Add(RestorePlan(snap, elevOps));

        Report("Restoring scheduling, capture and storage flags…");
        steps.AddRange(RestoreRegLevers(snap, admin, elevOps));

        Report("Restoring USB device power management…");
        steps.Add(RestoreUsbPower(snap, elevOps));

        return new NativeApplyResult
        {
            Ok = true,
            Module = Module,
            Message = "Machine settings restored to what they were before Exo changed them.",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    private static NativeApplyStep RestorePlan(JsonNode snap, List<string> elevOps)
    {
        var previous = snap["previousScheme"]?.GetValue<string>();
        var ops = ExoPowerPlan.BuildRestoreOps(previous);
        elevOps.AddRange(ops);

        if (ops.Count == 0)
            return new NativeApplyStep { Id = "power-plan", Status = "skip", Reason = "no Exo plan to remove" };

        return new NativeApplyStep
        {
            Id = "power-plan",
            Status = "pending-elev",
            Reason = previous is null
                ? "removing the Exo plan"
                : "switching back to the previous plan, then removing the Exo one"
        };
    }

    private static IEnumerable<NativeApplyStep> RestoreRegLevers(
        JsonNode snap, bool admin, List<string> elevOps)
    {
        var reg = snap["registry"] as JsonObject;
        if (reg is null)
        {
            yield return new NativeApplyStep
            {
                Id = "registry",
                Status = "skip",
                Reason = "not in snapshot"
            };
            yield break;
        }

        foreach (var lever in RegLevers)
        {
            if (!reg.TryGetPropertyValue(lever.Id, out var recorded)) continue;
            var had = recorded is not null;
            var before = had ? recorded!.GetValue<int>() : 0;

            if (lever.Hive == "HKCU" || admin)
            {
                var ok = had
                    ? NativeReg.TrySetDword(lever.Hive, lever.Path, lever.Name, before)
                    : NativeReg.TryDeleteValue(lever.Hive, lever.Path, lever.Name);
                yield return new NativeApplyStep
                {
                    Id = lever.Id,
                    Status = ok ? "ok" : "fail",
                    Reason = had ? $"restored to {before}" : "removed (was not set before)"
                };
                continue;
            }

            elevOps.Add(had
                ? $"dword:{lever.Hive}\\{lever.Path}|{lever.Name}|{before}"
                : $"delete:{lever.Hive}\\{lever.Path}|{lever.Name}");
            yield return new NativeApplyStep
            {
                Id = lever.Id,
                Status = "pending-elev",
                Reason = had ? $"restore to {before}" : "remove (was not set before)"
            };
        }
    }

    private static NativeApplyStep RestoreUsbPower(JsonNode snap, List<string> elevOps)
    {
        // Absent for a machine whose snapshot predates this lever. That is "nothing to put
        // back", not a failure, and it must not be reported as a restore that happened.
        if (snap["usbPower"] is not JsonObject recorded || recorded.Count == 0)
            return new NativeApplyStep
            {
                Id = "usb-power",
                Status = "skip",
                Reason = "no USB power state in the snapshot"
            };

        var wanted = new List<UsbPowerDevice>();
        foreach (var kv in recorded)
        {
            if (kv.Value is null) continue;
            bool before;
            try { before = kv.Value.GetValue<bool>(); } catch { continue; }
            wanted.Add(new UsbPowerDevice(kv.Key, before));
        }
        if (wanted.Count == 0)
            return new NativeApplyStep { Id = "usb-power", Status = "skip", Reason = "snapshot held no usable entries" };

        elevOps.Add(BuildUsbPowerOp(wanted));
        return new NativeApplyStep
        {
            Id = "usb-power",
            Status = "pending-elev",
            Reason = $"restore {wanted.Count} USB device(s) to their pre-Exo power setting"
        };
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────────────────

    private static NativeApplyStep WriteSnapshot(string scheme)
    {
        try
        {
            // Do not overwrite an existing snapshot. The first Apply captured the genuine
            // pre-Exo machine; a second Apply would otherwise record Exo's own values as the
            // thing to restore, quietly turning Repair into a no-op.
            //
            // But "do not overwrite" is not "do not extend". A snapshot taken before a lever
            // existed holds no entry for it, and RestoreRegLevers skips levers it has no record
            // of — so every lever added after a user's first Apply would be written by Apply and
            // silently left behind by Repair. Backfilling only the MISSING keys is safe and is
            // the honest reading: Exo has never written those levers on this machine, so what is
            // there right now genuinely is their pre-Exo state.
            if (File.Exists(SnapshotPath))
                return BackfillSnapshot();

            var reg = new JsonObject();
            foreach (var lever in RegLevers)
            {
                var v = NativeReg.GetDword(lever.Hive, lever.Path, lever.Name);
                reg[lever.Id] = v is null ? null : JsonValue.Create(v.Value);
            }

            // Per-device, because the setting is per-device. Recording only a count would make
            // Repair guess which devices had it ticked, and a guess is not a restore.
            var usb = new JsonObject();
            foreach (var d in ReadUsbPowerDevices() ?? new List<UsbPowerDevice>())
                usb[d.InstanceName] = JsonValue.Create(d.Enable);

            var root = new JsonObject
            {
                ["takenUtc"] = DateTime.UtcNow.ToString("o"),
                // The plan the user was on before Exo switched them. Repair puts this back and
                // then deletes the Exo plan - a true restore, because Exo never edited it.
                ["previousScheme"] = scheme,
                ["registry"] = reg,
                ["usbPower"] = usb
            };

            File.WriteAllText(SnapshotPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "pre-Exo state recorded" };
        }
        catch (Exception ex)
        {
            // A failed snapshot means Repair could not put things back, so Apply must not
            // proceed as if it could. Surfaced as a failure, not a warning.
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    /// <summary>
    /// Adds entries for levers the existing snapshot has never heard of, and changes nothing
    /// it already records. Without this, any lever introduced after a machine's first Apply is
    /// applied but never repaired — the change becomes permanent by omission.
    /// </summary>
    private static NativeApplyStep BackfillSnapshot()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(SnapshotPath)) is not JsonObject root)
                return new NativeApplyStep
                {
                    Id = "snapshot",
                    Status = "fail",
                    Reason = "existing snapshot is not a JSON object"
                };

            var added = 0;

            if (root["registry"] is not JsonObject reg)
            {
                reg = new JsonObject();
                root["registry"] = reg;
            }
            foreach (var lever in RegLevers)
            {
                if (reg.ContainsKey(lever.Id)) continue;
                var v = NativeReg.GetDword(lever.Hive, lever.Path, lever.Name);
                reg[lever.Id] = v is null ? null : JsonValue.Create(v.Value);
                added++;
            }

            // Per-device and all-or-nothing: a partially recorded USB section would let Repair
            // restore some devices and quietly abandon the rest.
            if (root["usbPower"] is not JsonObject)
            {
                var devices = ReadUsbPowerDevices();
                if (devices is not null)
                {
                    var usb = new JsonObject();
                    foreach (var d in devices) usb[d.InstanceName] = JsonValue.Create(d.Enable);
                    root["usbPower"] = usb;
                    added += devices.Count;
                }
            }

            if (added == 0)
                return new NativeApplyStep
                {
                    Id = "snapshot",
                    Status = "ok",
                    Reason = "keeping the original pre-Exo snapshot"
                };

            File.WriteAllText(SnapshotPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return new NativeApplyStep
            {
                Id = "snapshot",
                Status = "ok",
                Reason = $"original pre-Exo snapshot kept; recorded {added} setting(s) it predates"
            };
        }
        catch (Exception ex)
        {
            // Same rule as a failed first snapshot: no undo means Apply must not proceed.
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GUID of the power plan currently in use. Read from the registry rather than parsed out
    /// of <c>powercfg /getactivescheme</c> text, which is localised.
    /// </summary>
    internal static string? ActiveSchemeGuid()
    {
        var v = NativeReg.GetValue("HKLM", PowerSchemes, "ActivePowerScheme")?.ToString();
        return Guid.TryParse(v, out _) ? v : null;
    }

    // ── Elevated op encoding ──────────────────────────────────────────────────────────────

    // ── Detect ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row per lever, reporting what the machine is actually set to right now. Read live
    /// every time rather than trusting a state file, so a plan switch or a Windows update that
    /// reverts a value shows up as not-applied instead of a stale green tick.
    /// </summary>
    public static (bool Applied, List<(string Title, string Detail, bool Active)> Rows) Detect()
    {
        var rows = new List<(string, string, bool)>();

        // Power comes from the Exo plan now, including whether it is the one actually running.
        var (planOk, planRows) = ExoPowerPlan.Detect();
        rows.AddRange(planRows);

        foreach (var lever in RegLevers)
        {
            var current = NativeReg.GetDword(lever.Hive, lever.Path, lever.Name);
            var ok = current == lever.Value;
            // HAGS is written before reboot; treat correct value as active but spell pending.
            var detail = ok
                ? lever.Why + (lever.NeedsReboot ? " (value set — reboot if not live yet)." : "")
                : current is null
                    ? "Not set."
                    : $"Currently {current}, wants {lever.Value}.";
            rows.Add((lever.Title, detail, ok));
        }

        // USB power management, read live per device. An unreadable class is reported as an
        // info row rather than a failure: it is not evidence the setting is wrong, and a
        // machine that cannot answer must not be held permanently at "needs Apply" for it.
        // Title must NOT end in "(firmware)" — that suffix is reserved for UEFI ADVISE rows
        // and ModuleStatusClassifier treats it as non-blocking info.
        var usb = ReadUsbPowerDevices();
        if (usb is null)
        {
            rows.Add(("USB power management (WMI)",
                "Windows would not report per-device USB power settings on this machine.", false));
        }
        else if (usb.Count == 0)
        {
            rows.Add(("USB power management (WMI)",
                "No USB device exposes a power-management opt-in.", true));
        }
        else
        {
            var on = usb.Count(d => d.Enable);
            rows.Add(("USB power management",
                on == 0
                    ? $"Windows cannot suspend any of the {usb.Count} USB devices to save power."
                    : $"{on} of {usb.Count} USB devices can still be suspended to save power.",
                on == 0));
        }

        // Firmware findings ride along as read-only context. Only surface rows with a real
        // answer (good or actionable miss) plus the board/BIOS identity line. Skip unknown
        // probes (Ok == null) — "could not read" firmware noise is not useful on Windows.
        foreach (var f in FirmwareAdvisor.Scan())
        {
            if (f.Ok is null && f.Id is not "bios") continue;
            var detail = f.Ok switch
            {
                true => f.Detail,
                false => string.IsNullOrWhiteSpace(f.FixWhere) ? f.Detail : $"{f.Detail} {f.FixWhere}",
                null => f.Detail
            };
            // Bios is pure identity (version numbers) — always active info, never a "miss".
            var active = f.Id == "bios" || f.Ok == true;
            rows.Add(($"{f.Title} (firmware)", detail, active));
        }

        // Applied means every lever Exo owns is in the wanted state. Firmware rows are excluded
        // from the verdict — a machine with XMP off is not a machine Exo failed to apply.
        var applied = planOk && rows
            .Where(r => !ModuleStatusClassifier.IsInfoTitle(r.Item1) && r.Item1 != "Processor")
            .All(r => r.Item3);
        return (applied, rows);
    }
}
