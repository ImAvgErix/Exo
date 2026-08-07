using System.Text;
using Exo.Models;
using Exo.Security;

namespace Exo.Services;

/// <summary>
/// Best-path apply router (see WebHostBridge pipeline policy):
/// <list type="bullet">
/// <item>Brave — native C# is the full competitive apply</item>
/// <item>Steam — native C# essentials; optional PS deep pack soft-fails</item>
/// <item>Internet — NetworkOptimizerService (not this class)</item>
/// <item>Discord / NVIDIA — specialized PowerShell kits only</item>
/// </list>
/// HKLM ops that need admin use one compact elevated reg script (no lib imports).
/// </summary>
public sealed class NativeApplyService
{
    private readonly PowerShellRunnerService _runner;

    public NativeApplyService(PowerShellRunnerService runner)
    {
        _runner = runner;
    }

    public bool SupportsNativeApply(string module) =>
        module.ToLowerInvariant() is "steam" or "brave" or "system" or "spotify" or "amd";

    public async Task<NativeApplyResult> ApplyAsync(
        string module,
        bool experimental,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        module = module.ToLowerInvariant();
        progress?.Report($"Native apply ({module})...");

        NativeApplyResult result;
        try
        {
            result = module switch
            {
                "steam" => await Task.Run(() => SteamNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
                "brave" => await Task.Run(() => BraveNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
                "system" => await Task.Run(() => SystemNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
                "spotify" => await Task.Run(() => SpotifyNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
                "amd" => await Task.Run(() => AmdNativeApply.Apply(progress), ct).ConfigureAwait(false),
                _ => NativeApplyResult.Fail(module, "Module has no native apply path")
            };
        }
        catch (Exception ex)
        {
            // Surface full exception through progress so the host apply log captures it.
            progress?.Report($"NATIVE EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        // One elevation for all staged HKLM ops (HAGS, priority, DSCP, host latency…).
        if (result.NeedsElevation && result.ElevatedHklmOps.Count > 0)
        {
            progress?.Report("Elevating for host registry keys (one prompt)...");
            var elev = await ApplyElevatedOpsAsync(result.ElevatedHklmOps, progress, ct).ConfigureAwait(false);
            if (elev.Ok)
            {
                result.Steps.Add(new NativeApplyStep
                {
                    Id = "elevated-hklm",
                    Status = "ok",
                    Reason = elev.Message
                });
                // Re-mark pending steps as ok when elev succeeded
                foreach (var s in result.Steps.Where(s => s.Status == "pending-elev").ToList())
                {
                    var idx = result.Steps.IndexOf(s);
                    if (idx >= 0)
                        result.Steps[idx] = new NativeApplyStep { Id = s.Id, Status = "ok", Reason = "elevated" };
                }
            }
            else
            {
                var elevStatus = elev.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "skip" : "fail";
                result.Steps.Add(new NativeApplyStep
                {
                    Id = "elevated-hklm",
                    Status = elevStatus,
                    Reason = elev.Message
                });
                // Core HKCU success still counts as ok for steam/brave — but the staged ops
                // themselves did NOT run, and leaving them at "pending-elev" left every one of
                // them on screen with its optimistic reason text. Decline the UAC prompt on the
                // system module and the plan was never created, yet each lever still read as
                // though it were on its way. Carry the real outcome onto the steps, exactly as
                // the already-elevated branch below does; Ok is deliberately left alone so a
                // declined prompt does not turn into a hard Steam/Brave failure.
                foreach (var s in result.Steps.Where(s => s.Status == "pending-elev").ToList())
                {
                    var idx = result.Steps.IndexOf(s);
                    if (idx >= 0)
                        result.Steps[idx] = new NativeApplyStep { Id = s.Id, Status = elevStatus, Reason = elev.Message };
                }
            }
        }
        else if (NativeReg.IsAdministrator() && result.ElevatedHklmOps.Count > 0)
        {
            // Already admin, but staged ops still have to RUN.
            //
            // This branch used to be empty, on the assumption that an elevated process writes
            // its HKLM values inline and leaves nothing staged. That holds for Steam and Brave,
            // which do exactly that. It does not hold for anything staging an op that is not a
            // registry write: the system module's powercfg calls are always staged, because
            // powercfg is a process invocation rather than a key write. Left as it was, running
            // Exo as administrator meant CPU power policy silently never applied — no error, no
            // log line, and every step still reported ok.
            progress?.Report("Applying host settings (already elevated)...");
            var elev = await ApplyElevatedOpsAsync(result.ElevatedHklmOps, progress, ct).ConfigureAwait(false);
            result.Steps.Add(new NativeApplyStep
            {
                Id = "elevated-hklm",
                Status = elev.Ok ? "ok" : "fail",
                Reason = elev.Message
            });
            foreach (var s in result.Steps.Where(s => s.Status == "pending-elev").ToList())
            {
                var idx = result.Steps.IndexOf(s);
                if (idx >= 0)
                    result.Steps[idx] = new NativeApplyStep
                    {
                        Id = s.Id,
                        Status = elev.Ok ? "ok" : "fail",
                        Reason = elev.Ok ? s.Reason : elev.Message
                    };
            }
        }

        // Final Ok from step statuses (not the optimistic pre-elevation flag). A UAC decline
        // or elev pack failure must not leave System/Spotify looking fully green.
        var hardFail = result.Steps.Any(s =>
            s.Status.Equals("fail", StringComparison.OrdinalIgnoreCase));
        var stillPending = result.Steps.Any(s =>
            s.Status.Equals("pending-elev", StringComparison.OrdinalIgnoreCase));
        var elevationIncomplete = result.Steps.Any(s =>
            s.Id.Equals("elevated-hklm", StringComparison.OrdinalIgnoreCase)
            && !s.Status.Equals("ok", StringComparison.OrdinalIgnoreCase));
        if (hardFail || stillPending || elevationIncomplete)
        {
            result = new NativeApplyResult
            {
                Ok = false,
                Module = result.Module,
                Message = hardFail
                    ? (string.IsNullOrWhiteSpace(result.Message)
                        ? "Apply finished with failed steps."
                        : result.Message)
                    : "Apply needs Administrator approval for host settings — declined or not finished.",
                Steps = result.Steps,
                NeedsElevation = result.NeedsElevation,
                ElevatedHklmOps = result.ElevatedHklmOps
            };
        }

        // System / Spotify (and any native module without its own SaveState) get applyReport
        // for the orb. Steam/Brave already write their own richer state files.
        if (module is "system" or "spotify" or "amd")
            NativeModuleStateWriter.Save(module, result);

        return result;
    }

    /// <summary>
    /// Runs a staged batch of elevated ops in one prompt.
    /// Public because Repair needs it too: System and Spotify build their restores as the same
    /// op list Apply uses, and a Repair that returned without draining that list would report a
    /// successful undo it never performed.
    /// </summary>
    public Task<(bool Ok, string Message)> RunElevatedOpsAsync(
        IReadOnlyList<string> ops,
        IProgress<string>? progress = null,
        CancellationToken ct = default) => ApplyElevatedOpsAsync(ops, progress, ct);

    private async Task<(bool Ok, string Message)> ApplyElevatedOpsAsync(
        IReadOnlyList<string> ops,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (ops.Count == 0) return (true, "nothing");

        var script = BuildElevatedRegScript(ops);
        var temp = Path.Combine(Path.GetTempPath(), $"exo-native-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(temp, script, Encoding.UTF8, ct).ConfigureAwait(false);
            var strProgress = new Progress<ScriptRunProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Status))
                    progress?.Report(p.Status);
            });
            var run = await _runner.RunAsync(
                temp,
                arguments: null,
                elevate: true,
                progress: strProgress,
                cancellationToken: ct,
                workingDirectory: Path.GetTempPath(),
                trustPolicy: ScriptTrustPolicy.AppGeneratedNative).ConfigureAwait(false);

            if (!run.Success)
            {
                return (false, string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? (run.Summary ?? "elevated native pack failed")
                    : run.ErrorMessage!);
            }
            return (true, $"applied {ops.Count} HKLM op(s)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Minimal elevated script: only registry + QoS. No modules, no libs, no apostrophes in paths.
    /// </summary>
    public static string BuildElevatedRegScript(IReadOnlyList<string> ops)
    {
        var sb = new StringBuilder(ops.Count * 180 + 400);
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("function Write-ExoProgress([int]$p, [string]$s) { Write-Output (\"EXO_PROGRESS:{0}|{1}\" -f $p, $s) }");
        sb.AppendLine("function Write-ExoReport([string]$step, [string]$status, [string]$reason = '') {");
        sb.AppendLine("  $line = if ($reason) { \"${step}|${status}:${reason}\" } else { \"${step}|${status}\" }");
        sb.AppendLine("  Write-Output (\"EXO_REPORT:{0}\" -f $line)");
        sb.AppendLine("}");
        // Every registry write records what the value WAS, so a log distinguishes the three
        // cases that actually matter and used to look identical: the setting was already
        // correct, Exo changed it, or Exo wrote it and it did not take. "STEP hags|ok" told
        // you none of those.
        sb.AppendLine("function Get-ExoRegValue([string]$p, [string]$n) {");
        sb.AppendLine("  try { $v = (Get-ItemProperty -LiteralPath $p -Name $n -ErrorAction Stop).$n }");
        sb.AppendLine("  catch { return '<absent>' }");
        sb.AppendLine("  if ($null -eq $v) { return '<null>' }");
        sb.AppendLine("  if ($v -is [byte[]]) { return ('bytes[' + $v.Length + ']') }");
        sb.AppendLine("  return [string]$v");
        sb.AppendLine("}");
        sb.AppendLine("function Write-ExoChange([string]$target, [string]$before, [string]$after) {");
        sb.AppendLine("  $verdict = if ($before -eq $after) { 'already' } else { 'changed' }");
        sb.AppendLine("  Write-Output (\"EXO_CHANGE:{0}|{1}|{2}|{3}\" -f $target, $before, $after, $verdict)");
        sb.AppendLine("}");
        sb.AppendLine("Write-ExoProgress 5 'Native elevated pack'");
        sb.AppendLine("$ok = 0; $fail = 0");

        // Ops that take a scheme GUID resolve it themselves; nothing here needs the ACTIVE
        // plan any more. The in-place powercfg path that did was removed along with the
        // settings it wrote, which now live in the Exo plan instead.
        if (ops.Any(o => o.StartsWith("plan", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine(@"$schemeRootPlan = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'");

        var i = 0;
        foreach (var op in ops)
        {
            i++;
            var pct = 5 + (int)(90.0 * i / Math.Max(1, ops.Count));
            if (op.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            {
                // dword:HKLM\path|Name|Value
                var body = op.Substring("dword:".Length);
                var parts = body.Split('|');
                if (parts.Length != 3) continue;
                var hivePath = parts[0];
                var name = parts[1];
                var val = parts[2];
                var hive = hivePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
                var path = hivePath.Contains('\\')
                    ? hivePath[(hivePath.IndexOf('\\') + 1)..]
                    : hivePath;
                // Escape single quotes for PS literal
                var psPath = path.Replace("'", "''");
                var psName = name.Replace("'", "''");
                sb.AppendLine($"Write-ExoProgress {pct} 'Set {psName}'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $p = '{hive}:\\{psPath}'");
                sb.AppendLine("  if (-not (Test-Path -LiteralPath $p)) { New-Item -Path $p -Force | Out-Null }");
                sb.AppendLine($"  $wasVal = Get-ExoRegValue $p '{psName}'");
                sb.AppendLine($"  New-ItemProperty -LiteralPath $p -Name '{psName}' -Value {val} -PropertyType DWord -Force | Out-Null");
                sb.AppendLine($"  Write-ExoChange \"$p\\{psName}\" $wasVal (Get-ExoRegValue $p '{psName}')");
                sb.AppendLine("  $ok++");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'reg' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("string:", StringComparison.OrdinalIgnoreCase))
            {
                var body = op.Substring("string:".Length);
                var parts = body.Split('|');
                if (parts.Length != 3) continue;
                var hivePath = parts[0];
                var name = parts[1];
                var val = parts[2].Replace("'", "''");
                var hive = hivePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
                var path = hivePath.Contains('\\')
                    ? hivePath[(hivePath.IndexOf('\\') + 1)..]
                    : hivePath;
                var psPath = path.Replace("'", "''");
                var psName = name.Replace("'", "''");
                sb.AppendLine($"Write-ExoProgress {pct} 'Set {psName}'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $p = '{hive}:\\{psPath}'");
                sb.AppendLine("  if (-not (Test-Path -LiteralPath $p)) { New-Item -Path $p -Force | Out-Null }");
                sb.AppendLine($"  $wasVal = Get-ExoRegValue $p '{psName}'");
                sb.AppendLine($"  New-ItemProperty -LiteralPath $p -Name '{psName}' -Value '{val}' -PropertyType String -Force | Out-Null");
                sb.AppendLine($"  Write-ExoChange \"$p\\{psName}\" $wasVal (Get-ExoRegValue $p '{psName}')");
                sb.AppendLine("  $ok++");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'reg' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("delete:", StringComparison.OrdinalIgnoreCase))
            {
                // delete:HKLM\path|Name
                var body = op.Substring("delete:".Length);
                var parts = body.Split('|');
                if (parts.Length != 2) continue;
                var hivePath = parts[0];
                var name = parts[1];
                var hive = hivePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
                var path = hivePath.Contains('\\')
                    ? hivePath[(hivePath.IndexOf('\\') + 1)..]
                    : hivePath;
                var psPath = path.Replace("'", "''");
                var psName = name.Replace("'", "''");
                sb.AppendLine($"Write-ExoProgress {pct} 'Delete {psName}'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $p = '{hive}:\\{psPath}'");
                sb.AppendLine("  if (Test-Path -LiteralPath $p) {");
                sb.AppendLine($"    Remove-ItemProperty -LiteralPath $p -Name '{psName}' -Force -ErrorAction SilentlyContinue");
                sb.AppendLine("  }");
                sb.AppendLine("  $ok++");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'reg' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("delete-tree:", StringComparison.OrdinalIgnoreCase))
            {
                // delete-tree:HKLM\path — used only for a subtree Exo previously created.
                var hivePath = op.Substring("delete-tree:".Length);
                var hive = hivePath.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
                var path = hivePath.Contains('\\')
                    ? hivePath[(hivePath.IndexOf('\\') + 1)..]
                    : hivePath;
                var psPath = path.Replace("'", "''");
                sb.AppendLine($"Write-ExoProgress {pct} 'Remove retired policy tree'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $p = '{hive}:\\{psPath}'");
                sb.AppendLine("  if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop }");
                sb.AppendLine("  if (Test-Path -LiteralPath $p) { throw 'policy tree still exists after delete' }");
                sb.AppendLine("  $ok++; Write-ExoReport 'retired-policy-tree' 'ok' $p");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'retired-policy-tree' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("schtask:", StringComparison.OrdinalIgnoreCase))
            {
                // schtask:disable|\Task\Path or schtask:enable|\Task\Path
                var parts = op.Substring("schtask:".Length).Split('|', 2);
                if (parts.Length != 2) continue;
                var enabled = parts[0].Equals("enable", StringComparison.OrdinalIgnoreCase);
                var task = parts[1].Replace("'", "''");
                var change = enabled ? "/ENABLE" : "/DISABLE";
                var wanted = enabled ? "$true" : "$false";
                sb.AppendLine($"Write-ExoProgress {pct} 'Radeon background task'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $taskName = '{task}'");
                sb.AppendLine($"  $null = & schtasks.exe /Change /TN $taskName {change} 2>&1");
                sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw \"schtasks change exit $LASTEXITCODE\" }");
                sb.AppendLine("  $taskXmlText = (& schtasks.exe /Query /TN $taskName /XML 2>$null | Out-String)");
                sb.AppendLine("  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($taskXmlText)) { throw 'could not read task back' }");
                sb.AppendLine("  [xml]$taskXml = $taskXmlText");
                sb.AppendLine($"  if ([bool]::Parse([string]$taskXml.Task.Settings.Enabled) -ne {wanted}) {{ throw 'task state did not stick' }}");
                sb.AppendLine("  $ok++; Write-ExoReport 'radeon-task' 'ok' $taskName");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'radeon-task' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("usbpower:", StringComparison.OrdinalIgnoreCase))
            {
                // usbpower:<InstanceName>=<0|1>;<InstanceName>=<0|1>;...
                // "Allow the computer to turn off this device to save power", per device, from
                // MSPower_DeviceEnable in root\wmi. One op carrying every device rather than one
                // op each, so the pack runs a single WMI query instead of a dozen. Apply sends
                // 0 for all of them; Repair sends whatever the snapshot recorded, which is why
                // the value travels per instance in both directions.
                var body = op.Substring("usbpower:".Length);
                var entries = body.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (entries.Length == 0) continue;
                sb.AppendLine($"Write-ExoProgress {pct} 'USB device power management'");
                sb.AppendLine("try {");
                sb.AppendLine("  $usbWanted = @{}");
                foreach (var e in entries)
                {
                    var eq = e.LastIndexOf('=');
                    if (eq <= 0) continue;
                    var inst = e[..eq].Replace("'", "''");
                    var want = e[(eq + 1)..].Trim() == "1" ? "$true" : "$false";
                    sb.AppendLine($"  $usbWanted['{inst}'] = {want}");
                }
                sb.AppendLine("  $usbOk = 0; $usbMiss = 0");
                sb.AppendLine("  $usbAll = @(Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable -ErrorAction Stop)");
                sb.AppendLine("  foreach ($k in $usbWanted.Keys) {");
                sb.AppendLine("    $dev = $usbAll | Where-Object { $_.InstanceName -eq $k } | Select-Object -First 1");
                // A device that has been unplugged since the snapshot is not a failure, but it
                // is not a success either - counted separately and reported as its own number.
                sb.AppendLine("    if (-not $dev) { $usbMiss++; continue }");
                sb.AppendLine("    if ($dev.Enable -eq $usbWanted[$k]) { $usbOk++; continue }");
                sb.AppendLine("    Set-CimInstance -InputObject $dev -Property @{ Enable = $usbWanted[$k] } -ErrorAction Stop");
                // Read back rather than trusting the write: this is the same rule the rest of
                // the module follows, and a driver may simply refuse the change.
                sb.AppendLine("    $after = Get-CimInstance -Namespace root\\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue |");
                sb.AppendLine("      Where-Object { $_.InstanceName -eq $k } | Select-Object -First 1");
                sb.AppendLine("    if ($after -and $after.Enable -eq $usbWanted[$k]) { $usbOk++ } else { $usbMiss++ }");
                sb.AppendLine("  }");
                sb.AppendLine("  if ($usbMiss -eq 0) { $ok++; Write-ExoReport 'usb-power' 'ok' \"$usbOk device(s) set\" }");
                sb.AppendLine("  else { Write-ExoReport 'usb-power' 'partial' \"$usbOk set, $usbMiss not applied\" }");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'usb-power' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("qos:", StringComparison.OrdinalIgnoreCase))
            {
                // qos:PolicyName|exeName
                var body = op.Substring("qos:".Length);
                var parts = body.Split('|');
                if (parts.Length != 2) continue;
                var pol = parts[0].Replace("'", "''");
                var exe = parts[1].Replace("'", "''");
                sb.AppendLine($"Write-ExoProgress {pct} 'QoS {exe}'");
                sb.AppendLine("try {");
                sb.AppendLine("  $root = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\QoS'");
                sb.AppendLine("  if (-not (Test-Path -LiteralPath $root)) { New-Item -Path $root -Force | Out-Null }");
                sb.AppendLine($"  $qp = Join-Path $root '{pol}'");
                sb.AppendLine("  if (-not (Test-Path -LiteralPath $qp)) { New-Item -Path $qp -Force | Out-Null }");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Version' -Value '1.0' -PropertyType String -Force | Out-Null");
                sb.AppendLine($"  New-ItemProperty -LiteralPath $qp -Name 'Application Name' -Value '{exe}' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Protocol' -Value 'UDP' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Local Port' -Value '*' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Remote Port' -Value '*' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Local IP' -Value '*' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Remote IP' -Value '*' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'DSCP Value' -Value '46' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  New-ItemProperty -LiteralPath $qp -Name 'Throttle Rate' -Value '-1' -PropertyType String -Force | Out-Null");
                sb.AppendLine("  $ok++");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'qos' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("planduplicate:", StringComparison.OrdinalIgnoreCase))
            {
                // planduplicate:baseGuid|destGuid
                // powercfg accepts an explicit destination GUID, which is why Exo can use a
                // fixed one instead of parsing the new GUID back out of localised output.
                var parts = op.Substring("planduplicate:".Length).Split('|');
                if (parts.Length != 2) continue;
                if (!Guid.TryParse(parts[0], out var baseG) || !Guid.TryParse(parts[1], out var destG)) continue;
                sb.AppendLine($"Write-ExoProgress {pct} 'Create Exo power plan'");
                sb.AppendLine("try {");
                // Several bases are staged in preference order; the first that succeeds wins and
                // the rest no-op, because the plan already exists by then. Ultimate Performance
                // is hidden on most SKUs and duplicatescheme is what unhides it.
                // Double quotes in the emitted script: single-quoted, $schemeRootPlan never
                // expanded, so both Test-Path calls probed a literal '$schemeRootPlan\<guid>'
                // path — every staged base re-ran duplicatescheme over an existing plan, and
                // the 'plan ok' report could never fire.
                sb.AppendLine($"  $exists = Test-Path -LiteralPath \"$schemeRootPlan\\{destG}\"");
                sb.AppendLine("  if (-not $exists) {");
                sb.AppendLine($"    $null = powercfg /duplicatescheme {baseG} {destG} 2>&1");
                sb.AppendLine($"    if (Test-Path -LiteralPath \"$schemeRootPlan\\{destG}\") {{ $ok++; Write-ExoReport 'plan' 'ok' 'base {baseG}' }}");
                sb.AppendLine("  }");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'plan' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("planname:", StringComparison.OrdinalIgnoreCase))
            {
                // planname:guid|name  (name is pre-sanitised in C# to letters/digits/space/hyphen)
                var parts = op.Substring("planname:".Length).Split('|');
                if (parts.Length != 2) continue;
                if (!Guid.TryParse(parts[0], out var g)) continue;
                var nm = new string(parts[1].Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray());
                if (nm.Length == 0) nm = "Exo";
                sb.AppendLine($"Write-ExoProgress {pct} 'Name the plan'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $null = powercfg /changename {g} '{nm}' 'Tuned by Exo for this CPU' 2>&1");
                sb.AppendLine("  $ok++");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'plan' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("planac:", StringComparison.OrdinalIgnoreCase))
            {
                // planac:schemeGuid|subGuid|settingGuid|value  — AC only, by design.
                var parts = op.Substring("planac:".Length).Split('|');
                if (parts.Length != 4) continue;
                if (!Guid.TryParse(parts[0], out var scheme) ||
                    !Guid.TryParse(parts[1], out var sub) ||
                    !Guid.TryParse(parts[2], out var setting)) continue;
                if (!int.TryParse(parts[3], out var val)) continue;
                sb.AppendLine($"Write-ExoProgress {pct} 'Plan setting {setting}'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $null = powercfg /setacvalueindex {scheme} {sub} {setting} {val} 2>&1");
                sb.AppendLine("  if ($LASTEXITCODE -eq 0) { $ok++ }");
                sb.AppendLine($"  else {{ $fail++; Write-ExoReport 'plan' 'fail' \"setacvalueindex exit $LASTEXITCODE for {setting}\" }}");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'plan' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("planactive:", StringComparison.OrdinalIgnoreCase))
            {
                var g = op.Substring("planactive:".Length);
                if (!Guid.TryParse(g, out var pg)) continue;
                sb.AppendLine($"Write-ExoProgress {pct} 'Activate plan'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $null = powercfg /setactive {pg} 2>&1");
                sb.AppendLine("  if ($LASTEXITCODE -eq 0) { $ok++; Write-ExoReport 'plan-active' 'ok' 'plan activated' }");
                sb.AppendLine("  else { $fail++; Write-ExoReport 'plan-active' 'fail' \"setactive exit $LASTEXITCODE\" }");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'plan-active' 'fail' $_.Exception.Message }");
            }
            else if (op.StartsWith("plandelete:", StringComparison.OrdinalIgnoreCase))
            {
                // Windows refuses to delete the active scheme, so Repair always stages
                // planactive for the previous plan BEFORE this. See ExoPowerPlan.BuildRestoreOps.
                var g = op.Substring("plandelete:".Length);
                if (!Guid.TryParse(g, out var dg)) continue;
                sb.AppendLine($"Write-ExoProgress {pct} 'Remove the Exo plan'");
                sb.AppendLine("try {");
                sb.AppendLine($"  $null = powercfg /delete {dg} 2>&1");
                sb.AppendLine("  if ($LASTEXITCODE -eq 0) { $ok++ } else { $fail++; Write-ExoReport 'plan' 'fail' \"delete exit $LASTEXITCODE\" }");
                sb.AppendLine("} catch { $fail++; Write-ExoReport 'plan' 'fail' $_.Exception.Message }");
            }
        }

        sb.AppendLine("Write-ExoProgress 100 'Native elevated pack done'");
        sb.AppendLine("Write-ExoReport 'elevated-pack' $(if ($fail -eq 0) { 'ok' } else { 'fail' }) (\"ok=$ok fail=$fail\")");
        // Any failed plan/reg/usb op fails the pack — partial elev must not rewrite pending-elev→ok.
        sb.AppendLine("if ($fail -gt 0) { exit 1 } else { exit 0 }");
        return sb.ToString();
    }
}
