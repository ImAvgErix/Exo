// GPU power limit, thermal limit and cooler control via NVAPI.
//
// Everything here is bounded by the card's own reported range. The board's VBIOS publishes a
// minimum, default and maximum for both the power target and the thermal target; Exo asks for
// the maximum it reports and never a number of its own. That is what makes this safe on a card
// nobody has tested it against: the worst case is that the ceiling equals the default and the
// call is a no-op, which is exactly what happens on locked laptop and Founders cards.
//
// Deliberately NOT here: clock offsets, voltage curves, and custom fan curves.
//
//   Offsets and undervolts are per-silicon and need a stress-validate-and-revert loop to be
//   applied honestly. Shipping them as a fire-and-forget write would be shipping instability.
//
//   A custom fan curve is the one lever in this file whose failure mode is thermal. Writing a
//   curve that has never run on real hardware can undercool a card, so this file will restore
//   a cooler to driver control but will never take it away. See docs/SYSTEM-EVIDENCE.md.

using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace Exo.NvDisplay;

internal static class GpuPower
{
    /// <summary>
    /// Everything Exo changed, recorded before it changed it. Written as flat key=value so the
    /// PowerShell side can read it without a JSON parser, and so a half-written file is
    /// obviously half-written rather than silently parsed as empty.
    /// </summary>
    private const string SnapPower = "power.pcm";
    private const string SnapThermal = "thermal.degc";

    public static int Status()
    {
        var gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0)
        {
            Console.Error.WriteLine("[GPU] No NVIDIA GPU present.");
            return 3;
        }

        var anyOk = false;
        foreach (var gpu in gpus)
        {
            Console.WriteLine($"[GPU] {gpu.FullName}");
            anyOk |= ReportPower(gpu);
            anyOk |= ReportThermal(gpu);
            ReportCoolers(gpu);
        }

        // A card that exposes neither policy is not a failure — it is a locked board, and the
        // caller needs to be able to tell that apart from a broken call.
        if (!anyOk) Console.WriteLine("[GPU] power=unsupported thermal=unsupported (locked board)");
        return 0;
    }

    private static bool ReportPower(PhysicalGPU gpu)
    {
        try
        {
            var info = GPUApi.ClientPowerPoliciesGetInfo(gpu.Handle);
            var status = GPUApi.ClientPowerPoliciesGetStatus(gpu.Handle);
            if (info.PowerPolicyInfoEntries.Length == 0 || status.PowerPolicyStatusEntries.Length == 0)
                return false;

            var i = info.PowerPolicyInfoEntries[0];
            var s = status.PowerPolicyStatusEntries[0];
            Console.WriteLine($"[GPU] power current={Pct(s.PowerTargetInPCM)} " +
                              $"min={Pct(i.MinimumPowerInPCM)} default={Pct(i.DefaultPowerInPCM)} max={Pct(i.MaximumPowerInPCM)}");
            Console.WriteLine($"[GPU] power headroom={(i.MaximumPowerInPCM > s.PowerTargetInPCM ? "yes" : "no")}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU] power unavailable: {Reason(ex)}");
            return false;
        }
    }

    private static bool ReportThermal(PhysicalGPU gpu)
    {
        try
        {
            var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle);
            var status = GPUApi.GetThermalPoliciesStatus(gpu.Handle);
            if (info.ThermalPoliciesInfoEntries.Length == 0 || status.ThermalPoliciesStatusEntries.Length == 0)
                return false;

            var i = info.ThermalPoliciesInfoEntries[0];
            var s = status.ThermalPoliciesStatusEntries[0];
            Console.WriteLine($"[GPU] thermal current={s.TargetTemperature}C " +
                              $"min={i.MinimumTemperature}C default={i.DefaultTemperature}C max={i.MaximumTemperature}C");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU] thermal unavailable: {Reason(ex)}");
            return false;
        }
    }

    private static void ReportCoolers(PhysicalGPU gpu)
    {
        try
        {
            var info = gpu.CoolerInformation;
            var coolers = info.Coolers.ToList();
            if (coolers.Count == 0) { Console.WriteLine("[GPU] coolers none-reported"); return; }

            foreach (var c in coolers)
            {
                // CoolerPolicy.Manual means something took the fan off the driver's curve —
                // another tuning tool, or a profile that outlived the app that set it. Worth
                // surfacing because a fan pinned low is a silent thermal throttle.
                // (ControlMode is the board's capability — None/Toggle/Variable — not the
                // current policy, so it is the wrong field to test for this.)
                var manual = c.CurrentPolicy == CoolerPolicy.Manual;
                Console.WriteLine($"[GPU] cooler id={c.CoolerId} policy={c.CurrentPolicy} " +
                                  $"capability={c.ControlMode} level={c.CurrentLevel}% " +
                                  $"rpm={c.CurrentFanSpeedInRPM} manual={(manual ? "yes" : "no")}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU] coolers unavailable: {Reason(ex)}");
        }
    }

    /// <summary>
    /// Raise the power and thermal ceilings to whatever the board says its maximum is, and hand
    /// any manually-pinned cooler back to the driver. Writes a snapshot first when one does not
    /// already exist, so Repair restores the genuine pre-Exo values rather than Exo's own.
    /// </summary>
    public static int Apply(string? snapshotPath)
    {
        var gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0)
        {
            Console.Error.WriteLine("[GPU] No NVIDIA GPU present.");
            return 3;
        }

        var snap = new List<string>();
        var changed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var gpu in gpus)
        {
            Console.WriteLine($"[GPU] {gpu.FullName}");

            // ── Power ceiling ────────────────────────────────────────────────────────────
            try
            {
                var info = GPUApi.ClientPowerPoliciesGetInfo(gpu.Handle);
                var status = GPUApi.ClientPowerPoliciesGetStatus(gpu.Handle);
                if (info.PowerPolicyInfoEntries.Length > 0 && status.PowerPolicyStatusEntries.Length > 0)
                {
                    var max = info.PowerPolicyInfoEntries[0].MaximumPowerInPCM;
                    var now = status.PowerPolicyStatusEntries[0].PowerTargetInPCM;
                    snap.Add($"{SnapPower}={now}");

                    if (max <= now)
                    {
                        // Locked board, or already at the ceiling. Both are "nothing to do",
                        // and saying so beats reporting a change that did not happen.
                        Console.WriteLine($"[GPU] power already at ceiling ({Pct(now)}) — no change");
                        skipped++;
                    }
                    else
                    {
                        GPUApi.ClientPowerPoliciesSetStatus(gpu.Handle,
                            new PrivatePowerPoliciesStatusV1(new[]
                            {
                                new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry(max)
                            }));

                        // Read back. The driver silently clamps a request it does not like, and
                        // a write that was clamped to nothing must not be reported as applied.
                        var after = GPUApi.ClientPowerPoliciesGetStatus(gpu.Handle)
                            .PowerPolicyStatusEntries[0].PowerTargetInPCM;
                        if (after > now)
                        {
                            Console.WriteLine($"[GPU] power {Pct(now)} -> {Pct(after)}");
                            changed++;
                        }
                        else
                        {
                            Console.WriteLine($"[GPU] power write did not take (still {Pct(after)})");
                            failed++;
                        }
                    }
                }
                else { Console.WriteLine("[GPU] power policy not exposed by this board"); skipped++; }
            }
            catch (Exception ex) { Console.WriteLine($"[GPU] power failed: {Reason(ex)}"); failed++; }

            // ── Thermal ceiling ──────────────────────────────────────────────────────────
            try
            {
                var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle);
                var status = GPUApi.GetThermalPoliciesStatus(gpu.Handle);
                if (info.ThermalPoliciesInfoEntries.Length > 0 && status.ThermalPoliciesStatusEntries.Length > 0)
                {
                    var i = info.ThermalPoliciesInfoEntries[0];
                    var s = status.ThermalPoliciesStatusEntries[0];
                    snap.Add($"{SnapThermal}={s.TargetTemperature}");

                    if (i.MaximumTemperature <= s.TargetTemperature)
                    {
                        Console.WriteLine($"[GPU] thermal already at ceiling ({s.TargetTemperature}C) — no change");
                        skipped++;
                    }
                    else
                    {
                        GPUApi.SetThermalPoliciesStatus(gpu.Handle,
                            new PrivateThermalPoliciesStatusV2(new[]
                            {
                                new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(
                                    i.Controller, i.MaximumTemperature)
                            }));

                        var after = GPUApi.GetThermalPoliciesStatus(gpu.Handle)
                            .ThermalPoliciesStatusEntries[0].TargetTemperature;
                        if (after > s.TargetTemperature)
                        {
                            Console.WriteLine($"[GPU] thermal {s.TargetTemperature}C -> {after}C");
                            changed++;
                        }
                        else
                        {
                            Console.WriteLine($"[GPU] thermal write did not take (still {after}C)");
                            failed++;
                        }
                    }
                }
                else { Console.WriteLine("[GPU] thermal policy not exposed by this board"); skipped++; }
            }
            catch (Exception ex) { Console.WriteLine($"[GPU] thermal failed: {Reason(ex)}"); failed++; }

            // ── Coolers: hand back to the driver, never take over ────────────────────────
            try
            {
                var info = gpu.CoolerInformation;
                var manual = info.Coolers.Where(c => c.CurrentPolicy == CoolerPolicy.Manual).ToList();
                if (manual.Count == 0)
                {
                    Console.WriteLine("[GPU] coolers already driver-controlled");
                }
                else
                {
                    // Restoring to default puts the fan back on the VBIOS curve. This is the only
                    // cooler write Exo makes: it can raise cooling but never lower it.
                    info.RestoreCoolerSettingsToDefault(manual.Select(c => c.CoolerId).ToArray());
                    Console.WriteLine($"[GPU] {manual.Count} cooler(s) returned to driver control");
                    changed++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[GPU] coolers failed: {Reason(ex)}"); failed++; }
        }

        if (snapshotPath is not null && snap.Count > 0)
        {
            try
            {
                if (File.Exists(snapshotPath))
                {
                    Console.WriteLine("[GPU] keeping the existing pre-Exo snapshot");
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
                    File.WriteAllLines(snapshotPath, snap);
                    Console.WriteLine($"[GPU] snapshot written to {snapshotPath}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[GPU] snapshot failed: {ex.Message}"); failed++; }
        }

        Console.WriteLine($"[GPU] summary changed={changed} skipped={skipped} failed={failed}");
        // Skipped is not failure: a locked board legitimately has nothing to change. Only a
        // write that errored or silently did not take counts against the exit code.
        return failed > 0 && changed == 0 ? 1 : 0;
    }

    /// <summary>Put the power and thermal ceilings back to the recorded pre-Exo values.</summary>
    public static int Restore(string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
        {
            // Same rule as every other Repair path in Exo: never guess. Writing the board
            // default over a value the user set themselves is not a restore.
            Console.Error.WriteLine("[GPU] No snapshot to restore from — leaving the GPU alone.");
            return 2;
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(snapshotPath))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (int.TryParse(line[(eq + 1)..].Trim(), out var v)) map[line[..eq].Trim()] = v;
        }

        var gpus = PhysicalGPU.GetPhysicalGPUs();
        if (gpus.Length == 0) { Console.Error.WriteLine("[GPU] No NVIDIA GPU present."); return 3; }

        var failed = 0;
        foreach (var gpu in gpus)
        {
            if (map.TryGetValue(SnapPower, out var pcm))
            {
                try
                {
                    GPUApi.ClientPowerPoliciesSetStatus(gpu.Handle,
                        new PrivatePowerPoliciesStatusV1(new[]
                        {
                            new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry((uint)pcm)
                        }));
                    Console.WriteLine($"[GPU] power restored to {Pct((uint)pcm)}");
                }
                catch (Exception ex) { Console.WriteLine($"[GPU] power restore failed: {Reason(ex)}"); failed++; }
            }

            if (map.TryGetValue(SnapThermal, out var degc))
            {
                try
                {
                    var info = GPUApi.GetThermalPoliciesInfo(gpu.Handle);
                    var controller = info.ThermalPoliciesInfoEntries.Length > 0
                        ? info.ThermalPoliciesInfoEntries[0].Controller
                        : ThermalController.GPU;
                    GPUApi.SetThermalPoliciesStatus(gpu.Handle,
                        new PrivateThermalPoliciesStatusV2(new[]
                        {
                            new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(controller, degc)
                        }));
                    Console.WriteLine($"[GPU] thermal restored to {degc}C");
                }
                catch (Exception ex) { Console.WriteLine($"[GPU] thermal restore failed: {Reason(ex)}"); failed++; }
            }

            try
            {
                gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
                Console.WriteLine("[GPU] coolers returned to driver control");
            }
            catch (Exception ex) { Console.WriteLine($"[GPU] cooler restore failed: {Reason(ex)}"); }
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>Power targets come back in per-cent-mille: 100000 = 100%.</summary>
    private static string Pct(uint pcm) => $"{pcm / 1000.0:0.#}%";

    /// <summary>
    /// NVAPI throws NotSupported for anything the board locks down, which is the common case on
    /// laptops and Founders cards. Distinguishing it from a real error keeps the UI honest —
    /// "your card does not allow this" is not the same message as "this failed".
    /// </summary>
    private static string Reason(Exception ex) =>
        ex is NvAPIWrapper.Native.Exceptions.NVIDIANotSupportedException
            ? "not supported on this board"
            : ex.Message;
}
