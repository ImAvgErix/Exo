using System.Text;
using Exo.Models;
using Exo.Security;

namespace Exo.Services;

/// <summary>
/// Best-path apply router (see WebHostBridge pipeline policy):
/// <list type="bullet">
/// <item>Riot / Epic / Windows / Brave — native C# is the full competitive apply</item>
/// <item>Steam — native C# essentials; optional PS deep pack soft-fails</item>
/// <item>Internet — ExoInternetOptimizerService (not this class)</item>
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
        module.ToLowerInvariant() is "steam" or "windows" or "riot" or "epic" or "brave";

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
                "windows" => await Task.Run(() => WindowsNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
                "riot" => await Task.Run(() => LauncherNativeApply.Apply("riot", experimental, progress), ct).ConfigureAwait(false),
                "epic" => await Task.Run(() => LauncherNativeApply.Apply("epic", experimental, progress), ct).ConfigureAwait(false),
                "brave" => await Task.Run(() => BraveNativeApply.Apply(experimental, progress), ct).ConfigureAwait(false),
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
                result.Steps.Add(new NativeApplyStep
                {
                    Id = "elevated-hklm",
                    Status = elev.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "skip" : "fail",
                    Reason = elev.Message
                });
                // Core HKCU success still counts as ok for steam/windows/riot/epic/brave
            }
        }
        else if (NativeReg.IsAdministrator() && result.ElevatedHklmOps.Count > 0)
        {
            // Already admin — ops should have been written inline; nothing extra.
        }

        return result;
    }

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
        sb.AppendLine("Write-ExoProgress 5 'Native elevated pack'");
        sb.AppendLine("$ok = 0; $fail = 0");

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
                sb.AppendLine($"  New-ItemProperty -LiteralPath $p -Name '{psName}' -Value {val} -PropertyType DWord -Force | Out-Null");
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
                sb.AppendLine($"  New-ItemProperty -LiteralPath $p -Name '{psName}' -Value '{val}' -PropertyType String -Force | Out-Null");
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
        }

        sb.AppendLine("Write-ExoProgress 100 'Native elevated pack done'");
        sb.AppendLine("Write-ExoReport 'elevated-pack' $(if ($fail -eq 0) { 'ok' } else { 'partial' }) (\"ok=$ok fail=$fail\")");
        sb.AppendLine("if ($fail -gt 0 -and $ok -eq 0) { exit 1 } else { exit 0 }");
        return sb.ToString();
    }
}
