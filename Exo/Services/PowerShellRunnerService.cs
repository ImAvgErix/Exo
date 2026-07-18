using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;
using Exo.Security;

namespace Exo.Services;

public sealed class PowerShellRunnerService
{
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private static readonly Regex ProgressRegex = new(
        @"EXO_PROGRESS\s*:\s*(\d{1,3})\s*\|\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ScriptRunResult> RunAsync(
        string scriptPath,
        IEnumerable<string>? arguments = null,
        bool elevate = false,
        IProgress<ScriptRunProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null,
        bool ensureRuntime = false,
        ScriptTrustPolicy trustPolicy = ScriptTrustPolicy.ShippedManifest)
    {
        if (!File.Exists(scriptPath))
        {
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = $"Script not found: {scriptPath}",
                Summary = "Script missing"
            };
        }

        var workDir = workingDirectory ?? Path.GetDirectoryName(scriptPath) ?? PathHelper.AppDirectory;
        var opts = arguments?.ToList() ?? new List<string>();

        progress?.Report(new ScriptRunProgress { Percent = 2, Status = "Starting..." });

        try
        {
            if (elevate)
            {
                var integrity = ShippedScriptManifest.Verify(scriptPath, trustPolicy);
                if (!integrity.Ok)
                {
                    return new ScriptRunResult
                    {
                        Success = false,
                        ExitCode = -1,
                        Summary = "Integrity check failed",
                        ErrorMessage = integrity.Message
                    };
                }
            }

            if (ensureRuntime)
            {
                progress?.Report(new ScriptRunProgress { Percent = 1, Status = "Preparing PowerShell 7..." });
                var runtime = await EnsurePowerShellRuntimeAsync(cancellationToken).ConfigureAwait(false);
                if (!runtime.Ok)
                {
                    return new ScriptRunResult
                    {
                        Success = false,
                        ExitCode = -1,
                        Summary = "PowerShell 7 unavailable",
                        ErrorMessage = runtime.Message
                    };
                }
            }

            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Always silent: PowerShell 7 (stable), no visible terminal window.
                // Elevated and non-elevated runs both stay hidden.
                if (elevate)
                {
                    return await RunElevatedSilentAsync(
                            scriptPath, opts, workDir, progress, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await RunRedirectedAsync(scriptPath, opts, workDir, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _runGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ScriptRunProgress { Percent = 0, Status = "Cancelled" });
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -2,
                Summary = "Cancelled",
                ErrorMessage = "Operation cancelled"
            };
        }
        catch (Exception ex)
        {
            var cancelled = ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

            progress?.Report(new ScriptRunProgress
            {
                Percent = 0,
                Status = cancelled ? "Elevation cancelled" : "Failed",
            });

            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                Summary = cancelled ? "Elevation cancelled" : "Failed",
                ErrorMessage = ex.Message
            };
        }
    }

    private static async Task<ScriptRunResult> RunRedirectedAsync(
        string scriptPath,
        List<string> opts,
        string workDir,
        IProgress<ScriptRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in opts)
            psi.ArgumentList.Add(a);

        // Non-elevated path: scripts can also append to this log for consistency
        var stamp = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(PathHelper.LogsDir, $"run-{stamp}.log");
        Directory.CreateDirectory(PathHelper.LogsDir);
        psi.Environment["EXO"] = "1";
        psi.Environment["EXO_LOG"] = logPath;
        psi.Environment["DISCOPT_NONINTERACTIVE"] = "1";
        psi.Environment["EXO_SKIP_BOOT_FLASH"] = "1";
        psi.Environment["DISCOPT_SKIP_MANIFEST"] = "1";

        var output = new StringBuilder();
        var lastStatus = "Starting...";
        var lastPercent = 2.0;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output)
            {
                output.AppendLine(e.Data);
                ParseLine(e.Data, ref lastPercent, ref lastStatus, progress);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output)
            {
                output.AppendLine(e.Data);
                ParseLine(e.Data, ref lastPercent, ref lastStatus, progress);
            }
        };

        if (!process.Start())
        {
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "Failed to start PowerShell.",
                Summary = "Launch failed"
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminateProcess(process);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // The cancellation result is still returned even if process teardown races.
            }
            throw;
        }

        string text;
        lock (output) text = output.ToString();
        var ok = process.ExitCode == 0;
        progress?.Report(new ScriptRunProgress
        {
            Percent = ok ? 100 : lastPercent,
            Status = ok ? "Completed successfully" : "Finished with errors",
        });

        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = process.ExitCode,
            FullOutput = text,
            Summary = ok ? "Completed successfully" : $"Exited with code {process.ExitCode}",
            ErrorMessage = ok ? null : ExtractError(text, logPath),
            LogPath = logPath
        };
    }

    /// <summary>
    /// Elevated apply/repair via PowerShell 7 — hidden, no terminal window.
    /// Progress still polled from EXO_LOG + exit file.
    /// </summary>
    private static async Task<ScriptRunResult> RunElevatedSilentAsync(
        string scriptPath,
        List<string> opts,
        string workDir,
        IProgress<ScriptRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PathHelper.LogsDir);
        var stamp = Guid.NewGuid().ToString("N");
        var transactionPath = Path.Combine(PathHelper.MachineTransactionsDir, stamp);
        var logPath = Path.Combine(transactionPath, "run.log");
        var exitPath = Path.Combine(transactionPath, "exit.txt");
        var cancelPath = Path.Combine(PathHelper.LogsDir, $"cancel-{stamp}.txt");
        var outTmp = logPath + ".out";
        var errTmp = logPath + ".err";

        var pwsh = ResolvePowerShell();
        var scriptEsc = scriptPath.Replace("'", "''");
        var logEsc = logPath.Replace("'", "''");
        var exitEsc = exitPath.Replace("'", "''");
        var workEsc = workDir.Replace("'", "''");
        var cancelEsc = cancelPath.Replace("'", "''");
        var outEsc = outTmp.Replace("'", "''");
        var errEsc = errTmp.Replace("'", "''");
        var pwshEsc = pwsh.Replace("'", "''");
        var transactionEsc = transactionPath.Replace("'", "''");
        var storeEsc = PathHelper.MachineTransactionsDir.Replace("'", "''");
        string expectedHash;
        await using (var scriptStream = File.OpenRead(scriptPath))
            expectedHash = Convert.ToHexString(
                await SHA256.HashDataAsync(scriptStream, cancellationToken).ConfigureAwait(false));
        var optionsBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(opts)));

        var bootstrapBody = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$env:EXO = '1'",
            "$env:DISCOPT_NONINTERACTIVE = '1'",
            "$env:EXO_SKIP_BOOT_FLASH = '1'",
            "$env:DISCOPT_SKIP_MANIFEST = '1'",
            "Set-Location -LiteralPath '" + workEsc + "'",
            "$storeRoot = '" + storeEsc + "'",
            "$transactionRoot = '" + transactionEsc + "'",
            "$log = '" + logEsc + "'",
            "$exitFile = '" + exitEsc + "'",
            "$cancelFile = '" + cancelEsc + "'",
            "$script = '" + scriptEsc + "'",
            "$pwsh = '" + pwshEsc + "'",
            "$outPath = '" + outEsc + "'",
            "$errPath = '" + errEsc + "'",
            "$expectedHash = '" + expectedHash + "'",
            "$optionsJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + optionsBase64 + "'))",
            "$scriptArgs = @($optionsJson | ConvertFrom-Json)",
            "function Assert-PlainDirectory([string]$path) {",
            "  if (-not (Test-Path -LiteralPath $path -PathType Container)) { return }",
            "  $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop",
            "  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Protected store contains a reparse point: ' + $path) }",
            "}",
            "function Protect-Directory([string]$path) {",
            "  $icacls = Join-Path $env:SystemRoot 'System32\\icacls.exe'",
            "  & $icacls $path '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null",
            "  if ($LASTEXITCODE -ne 0) { throw ('Could not protect transaction directory (icacls ' + $LASTEXITCODE + ')') }",
            "}",
            "$storeParent = Split-Path -Parent $storeRoot",
            "Assert-PlainDirectory $storeParent",
            "Assert-PlainDirectory $storeRoot",
            "if (-not (Test-Path -LiteralPath $storeParent)) { [void](New-Item -ItemType Directory -Path $storeParent -ErrorAction Stop); Protect-Directory $storeParent }",
            "if (-not (Test-Path -LiteralPath $storeRoot)) { [void](New-Item -ItemType Directory -Path $storeRoot -ErrorAction Stop); Protect-Directory $storeRoot }",
            "if (Test-Path -LiteralPath $transactionRoot) { throw 'Transaction directory already exists; execution blocked.' }",
            "[void](New-Item -ItemType Directory -Path $transactionRoot -ErrorAction Stop)",
            "Protect-Directory $transactionRoot",
            "$env:EXO_LOG = $log",
            "function Write-LogLine([string]$line) {",
            "  if ([string]::IsNullOrWhiteSpace($line)) { return }",
            "  try { Add-Content -LiteralPath $log -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }",
            "}",
            "function Sync-Stream([string]$path, [ref]$pos) {",
            "  if (-not (Test-Path -LiteralPath $path)) { return }",
            "  try {",
            "    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')",
            "    try {",
            "      if ($fs.Length -le $pos.Value) { return }",
            "      [void]$fs.Seek($pos.Value, 'Begin')",
            "      $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)",
            "      while ($null -ne ($line = $sr.ReadLine())) { Write-LogLine $line }",
            "      $pos.Value = $fs.Position",
            "    } finally { $fs.Dispose() }",
            "  } catch { }",
            "}",
            "'' | Set-Content -LiteralPath $log -Encoding UTF8",
            "Write-LogLine 'EXO_PROGRESS:5|Elevated PowerShell 7 (silent)'",
            "$code = 1",
            "if (Test-Path -LiteralPath $cancelFile) {",
            "  Write-LogLine 'EXO_PROGRESS:0|Cancelled'",
            "  Set-Content -LiteralPath $exitFile -Value -2 -Encoding ascii",
            "  exit -2",
            "}",
            "try {",
            "  $actualHash = (Get-FileHash -LiteralPath $script -Algorithm SHA256 -ErrorAction Stop).Hash",
            "  if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Optimizer script changed after approval; execution blocked.' }",
            "  $argText = '-NoProfile -ExecutionPolicy Bypass -File \"' + $script + '\"'",
            "  foreach ($item in $scriptArgs) { $argText += ' \"' + ([string]$item).Replace('\"','\\\"') + '\"' }",
            "  Write-LogLine 'EXO_PROGRESS:8|Starting optimizer...'",
            "  $p = Start-Process -FilePath $pwsh -ArgumentList $argText -WorkingDirectory '" + workEsc + "' -PassThru -WindowStyle Hidden -RedirectStandardOutput $outPath -RedirectStandardError $errPath",
            "  if ($null -eq $p) { throw 'Failed to start optimizer process' }",
            "  $outPos = 0L; $errPos = 0L",
            "  $wasCancelled = $false",
            "  while (-not $p.HasExited) {",
            "    if (Test-Path -LiteralPath $cancelFile) {",
            "      Write-LogLine 'EXO_PROGRESS:0|Cancelling...'",
            "      try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }",
            "      try { $p.WaitForExit(5000) } catch { }",
            "      $wasCancelled = $true",
            "      break",
            "    }",
            "    Sync-Stream $outPath ([ref]$outPos)",
            "    Sync-Stream $errPath ([ref]$errPos)",
            "    Start-Sleep -Milliseconds 120",
            "  }",
            "  Sync-Stream $outPath ([ref]$outPos)",
            "  Sync-Stream $errPath ([ref]$errPos)",
            "  if ($wasCancelled) { $code = -2 } else { $code = [int]$p.ExitCode }",
            "} catch {",
            "  Write-LogLine ('[-] Elevated child failed: ' + $_.Exception.Message)",
            "  $code = 1",
            "}",
            "if ($code -eq 0) { Write-LogLine 'EXO_PROGRESS:100|Completed successfully' }",
            "elseif ($code -eq -2) { Write-LogLine 'EXO_PROGRESS:0|Cancelled' }",
            "else { Write-LogLine 'EXO_PROGRESS:100|Finished with errors' }",
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii",
            "exit $code"
        });
        var encodedBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrapBody));

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            SignalElevatedCancellation(cancelPath, cancelPath, exitPath, outTmp, errTmp);
        });
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ScriptRunProgress
        {
            Percent = 4,
            Status = "Waiting for Administrator approval..."
        });

        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encodedBootstrap,
            WorkingDirectory = workDir,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var launcher = Process.Start(psi);
        if (launcher is null)
        {
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "Failed to start elevated PowerShell 7.",
                Summary = "Launch failed"
            };
        }
        var lastPercent = 5.0;
        var lastStatus = "Waiting for Administrator approval...";
        var lastLength = 0;
        var startedUtc = DateTime.UtcNow;
        var sawLog = false;
        var timeout = TimeSpan.FromMinutes(25);

        while (!File.Exists(exitPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(logPath))
            {
                if (!sawLog)
                {
                    sawLog = true;
                    lastStatus = "Optimizer running...";
                    progress?.Report(new ScriptRunProgress { Percent = 8, Status = lastStatus });
                }
                PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
            }
            else if ((launcher.HasExited && DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(2)) ||
                     (DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(30) && !sawLog))
            {
                var bootstrapFailed = launcher.HasExited && launcher.ExitCode != 0;
                progress?.Report(new ScriptRunProgress
                {
                    Percent = 0,
                    Status = bootstrapFailed ? "Secure transaction failed" : "Elevation cancelled"
                });
                CleanupTemp(cancelPath, logPath, exitPath, outTmp, errTmp);
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Summary = bootstrapFailed ? "Secure transaction failed" : "Elevation cancelled",
                    ErrorMessage = bootstrapFailed
                        ? $"The elevated transaction boundary failed before logging (exit {launcher.ExitCode})."
                        : "Administrator approval was cancelled or the elevated session never started."
                };
            }

            if (DateTime.UtcNow - startedUtc > timeout)
            {
                var timedOutOutput = File.Exists(logPath)
                    ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                SignalElevatedCancellation(cancelPath, cancelPath, exitPath, outTmp, errTmp);
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    FullOutput = timedOutOutput,
                    Summary = "Timed out",
                    ErrorMessage = "Optimizer timed out.",
                    LogPath = logPath
                };
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        await launcher.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
        var exitText = (await File.ReadAllTextAsync(exitPath, cancellationToken).ConfigureAwait(false)).Trim();
        _ = int.TryParse(exitText, out var exitCode);
        var full = File.Exists(logPath)
            ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var ok = exitCode == 0;
        progress?.Report(new ScriptRunProgress
        {
            Percent = ok ? 100 : lastPercent,
            Status = ok ? "Completed successfully" : exitCode == -2 ? "Cancelled" : "Finished with errors"
        });
        CleanupTemp(cancelPath);
        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = exitCode,
            FullOutput = full,
            Summary = ok ? "Completed successfully" : exitCode == -2 ? "Cancelled" : $"Exited with code {exitCode}",
            ErrorMessage = ok ? null : ExtractError(full, logPath),
            LogPath = logPath
        };
    }


    private static void CleanupTemp(params string?[] paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            try { File.Delete(p); } catch { /* ignore */ }
        }
    }

    private static async Task CleanupCancelledElevationAsync(params string[] paths)
    {
        try
        {
            // Leave the marker long enough for the elevated wrapper to observe it,
            // then remove only transient coordination files (the run log is kept).
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            CleanupTemp(paths);
        }
        catch
        {
            // Fire-and-forget cleanup must never surface an unobserved exception.
        }
    }

    private static void SignalElevatedCancellation(string cancelPath, params string[] transientPaths)
    {
        try { File.WriteAllText(cancelPath, "cancel"); } catch { /* best effort */ }
        _ = CleanupCancelledElevationAsync(transientPaths);
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort; the caller still receives a cancellation result.
        }
    }

    private static void PollLog(
        string logPath,
        ref int lastLength,
        ref double lastPercent,
        ref string lastStatus,
        IProgress<ScriptRunProgress>? progress)
    {
        if (!File.Exists(logPath)) return;
        string chunk;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= lastLength) return;
            fs.Seek(lastLength, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            chunk = reader.ReadToEnd();
            lastLength = (int)fs.Position;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(chunk)) return;
        foreach (var line in chunk.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            ParseLine(trimmed, ref lastPercent, ref lastStatus, progress);
        }
    }

    private static void ParseLine(
        string line,
        ref double lastPercent,
        ref string lastStatus,
        IProgress<ScriptRunProgress>? progress)
    {
        var m = ProgressRegex.Match(line);
        if (m.Success)
        {
            if (int.TryParse(m.Groups[1].Value, out var p))
                lastPercent = Math.Clamp(p, 0, 100);
            lastStatus = m.Groups[2].Value.Trim();
            progress?.Report(new ScriptRunProgress
            {
                Percent = lastPercent,
                Status = lastStatus,
            });
            return;
        }

        if (line.StartsWith("[*]", StringComparison.Ordinal) ||
            line.StartsWith("[+]", StringComparison.Ordinal) ||
            line.StartsWith("[!]", StringComparison.Ordinal) ||
            line.StartsWith("[-]", StringComparison.Ordinal))
        {
            lastStatus = CleanStatus(line);
            if (lastPercent < 94)
                lastPercent = Math.Min(94, lastPercent + 2);

            progress?.Report(new ScriptRunProgress
            {
                Percent = lastPercent,
                Status = lastStatus,
            });
        }
    }

    private static string CleanStatus(string line)
    {
        var s = line.Trim();
        if (s.Length > 2 && (s.StartsWith("[*]", StringComparison.Ordinal) ||
                             s.StartsWith("[+]", StringComparison.Ordinal) ||
                             s.StartsWith("[!]", StringComparison.Ordinal) ||
                             s.StartsWith("[-]", StringComparison.Ordinal)))
            s = s[3..].Trim();
        if (s.Length > 90) s = s[..87] + "...";
        return s;
    }

    private static string? ExtractError(string text, string? runLogPath = null)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("[-]", StringComparison.Ordinal) ||
                        l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Error log:", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("Full log:", StringComparison.OrdinalIgnoreCase) ||
                        l.StartsWith("Message:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .ToArray();

        var message = lines.Length == 0 ? "Optimizer failed." : string.Join(Environment.NewLine, lines);

        if (!string.IsNullOrWhiteSpace(runLogPath) && File.Exists(runLogPath))
            message += Environment.NewLine + "Exo log: " + runLogPath;

        return message;
    }

    private static string? _cachedPowerShellPath;

    /// <summary>
    /// Path to PowerShell 7 (stable channel preferred). Never Windows PowerShell 5.1.
    /// Preview installs are accepted only as a fallback when no stable copy exists.
    /// </summary>
    public static string ResolvePowerShell()
    {
        if (_cachedPowerShellPath is not null && File.Exists(_cachedPowerShellPath))
            return _cachedPowerShellPath;

        string? previewFallback = null;
        foreach (var path in EnumeratePowerShellCandidates())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;
                if (IsWindowsPowerShell51(path))
                    continue;
                if (LooksLikePowerShellPreview(path))
                {
                    previewFallback ??= path;
                    continue;
                }

                // Any non-preview pwsh.exe is the stable channel we want.
                _cachedPowerShellPath = path;
                return path;
            }
            catch { /* continue */ }
        }

        if (previewFallback is not null)
        {
            // Preview channel accepted as fallback only; the dependency doctor
            // migrates these machines to stable PowerShell 7.
            _cachedPowerShellPath = previewFallback;
            return previewFallback;
        }

        throw new InvalidOperationException(
            "PowerShell 7 not found.\n" +
            "Install: winget install Microsoft.PowerShell\n" +
            "Or Microsoft Store -> \"PowerShell\". Then restart Exo.");
    }

    public static string? TryGetPowerShellPath()
    {
        try { return ResolvePowerShell(); }
        catch { return null; }
    }

    /// <summary>Prime the pwsh path cache (background warm after first paint).</summary>
    public void WarmResolvePowerShell()
    {
        try { _ = ResolvePowerShell(); } catch { /* cold open still resolves later */ }
    }

    /// <summary>Stable PowerShell 7 only — returns null when only preview/5.1 exist.</summary>
    public static string? TryGetStablePowerShellPath()
    {
        foreach (var path in EnumeratePowerShellCandidates())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;
                if (IsWindowsPowerShell51(path) || LooksLikePowerShellPreview(path))
                    continue;
                return path;
            }
            catch { /* continue */ }
        }

        return null;
    }

    /// <summary>
    /// Requires stable PowerShell 7. Installs it via winget when available;
    /// otherwise (debloated Windows without winget, or a failed winget install)
    /// falls back to the official portable zip from the Microsoft GitHub release.
    /// An existing preview install keeps Exo working until the doctor migrates it.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsurePowerShellRuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        if (TryGetStablePowerShellPath() is { } stable)
            return (true, "PowerShell 7: " + stable);

        var parts = new List<string>();
        var winget = FindWinget();
        if (winget is not null)
        {
            var psInstall = await RunWingetAsync(
                winget,
                new[]
                {
                    "install", "--id", "Microsoft.PowerShell", "-e",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity", "--silent"
                },
                cancellationToken).ConfigureAwait(false);
            _cachedPowerShellPath = null;
            if (TryGetStablePowerShellPath() is { } viaWinget)
                return (true, "PowerShell 7 installed via winget: " + viaWinget);
            parts.Add("winget pwsh install failed: " + psInstall.Detail);
        }
        else
        {
            parts.Add("winget not found");
        }

        var portable = await InstallPortablePwshStableAsync(cancellationToken).ConfigureAwait(false);
        _cachedPowerShellPath = null;
        if (TryGetStablePowerShellPath() is { } viaPortable)
            return (true, "PowerShell 7 ready (" + portable.Detail + "): " + viaPortable);
        parts.Add("portable pwsh install failed: " + portable.Detail);

        if (TryGetPowerShellPath() is { } preview)
            return (true, "Using PowerShell 7 Preview as fallback: " + preview + " (" + string.Join("; ", parts) + ")");

        return (false,
            "PowerShell 7 unavailable: " + string.Join("; ", parts) +
            " — install with 'winget install Microsoft.PowerShell' or from the Microsoft Store (\"PowerShell\"), then restart Exo.");
    }

    private static readonly HttpClient RuntimeHttp = CreateRuntimeHttp();

    private static HttpClient CreateRuntimeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Exo", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private static string PortablePwshDir =>
        Path.Combine(PathHelper.AppDataDir, "runtime", "PowerShell");

    /// <summary>
    /// Find the newest asset on a GitHub stable release matching a name filter.
    /// Returns url + size + sha256 digest (when GitHub provides one).
    /// </summary>
    private static async Task<(string? Url, long Size, string? Sha256, string? Tag)> FindLatestStableAssetAsync(
        string repo,
        Func<string, bool> assetNameMatches,
        CancellationToken ct)
    {
        var json = await RuntimeHttp.GetStringAsync(
            $"https://api.github.com/repos/{repo}/releases?per_page=15", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                continue;
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            var tag = rel.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!assetNameMatches(name))
                    continue;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
                string? sha256 = null;
                var digest = a.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha256 = digest["sha256:".Length..];
                return (url, size, sha256, tag);
            }
        }
        return (null, 0, null, null);
    }

    private static async Task<(bool Ok, string Detail)> DownloadVerifiedAsync(
        string url, long expectedSize, string? expectedSha256, string destPath, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (false, "download URL was not HTTPS");
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return (false, "release asset did not publish a SHA-256 digest");

        await using (var fs = File.Create(destPath))
        await using (var stream = await RuntimeHttp.GetStreamAsync(uri, ct).ConfigureAwait(false))
        {
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        var length = new FileInfo(destPath).Length;
        if (expectedSize > 0 && length != expectedSize)
        {
            TryDeleteFile(destPath);
            return (false, "download size mismatch");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            await using var check = File.OpenRead(destPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(destPath);
                return (false, "download failed SHA-256 verification");
            }
        }

        return (true, "verified");
    }

    /// <summary>
    /// Winget-less fallback: install the official stable PowerShell 7 portable zip
    /// (github.com/PowerShell/PowerShell stable release, win-x64) under %LocalAppData%\Exo\runtime.
    /// Per-user, no elevation. Stable only — prereleases are never selected.
    /// </summary>
    private static async Task<(bool Ok, string Detail)> InstallPortablePwshStableAsync(CancellationToken ct)
    {
        try
        {
            var (url, size, sha256, tag) = await FindLatestStableAssetAsync(
                "PowerShell/PowerShell",
                name => name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase),
                ct).ConfigureAwait(false);
            if (url is null || tag is null || tag.Contains("preview", StringComparison.OrdinalIgnoreCase))
                return (false, "no stable PowerShell win-x64 zip release found");

            var runtimeRoot = Path.GetDirectoryName(PortablePwshDir)!;
            Directory.CreateDirectory(runtimeRoot);
            var zipPath = Path.Combine(runtimeRoot, "pwsh-download.zip");
            var staging = PortablePwshDir + ".staging-" + Guid.NewGuid().ToString("N");

            var download = await DownloadVerifiedAsync(url, size, sha256, zipPath, ct).ConfigureAwait(false);
            if (!download.Ok)
                return download;

            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            }
            finally
            {
                TryDeleteFile(zipPath);
            }

            if (!File.Exists(Path.Combine(staging, "pwsh.exe")))
            {
                TryDeleteDirectory(staging);
                return (false, "zip did not contain pwsh.exe");
            }

            if (Directory.Exists(PortablePwshDir))
            {
                try { Directory.Delete(PortablePwshDir, recursive: true); }
                catch { /* in use — keep the existing copy */ }
            }
            if (Directory.Exists(PortablePwshDir))
            {
                TryDeleteDirectory(staging);
                return (true, "existing portable copy kept (in use)");
            }

            Directory.Move(staging, PortablePwshDir);
            return (true, "portable " + tag);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string? FindWinget()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Microsoft", "WindowsApps", "winget.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "WindowsApps", "Microsoft.DesktopAppInstaller_*", "winget.exe"),
                 })
        {
            if (path.Contains('*'))
            {
                try
                {
                    var dir = Path.GetDirectoryName(path)!;
                    var root = Path.GetDirectoryName(dir)!;
                    var pattern = Path.GetFileName(dir);
                    if (Directory.Exists(root))
                    {
                        foreach (var d in Directory.GetDirectories(root, pattern)
                                     .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            var exe = Path.Combine(d, "winget.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
                continue;
            }

            if (File.Exists(path)) return path;
        }

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim('"'), "winget.exe");
                if (File.Exists(full)) return full;
            }
        }
        catch { }

        return null;
    }

    private static async Task<(bool Ok, string Detail)> RunWingetAsync(
        string winget,
        string[] args,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = winget,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (false, "winget failed to start");
            var stdout = await p.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // 0 = success, -1978335189 often already installed
            var ok = p.ExitCode is 0 or -1978335189 or -1978335212;
            return (ok, $"exit={p.ExitCode} {TrimWinget(stdout)} {TrimWinget(stderr)}".Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string TrimWinget(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > 180 ? t[..180] + "…" : t;
    }

    /// <summary>Windows PowerShell 5.1 is never a valid host for Exo scripts.</summary>
    private static bool IsWindowsPowerShell51(string path)
    {
        if (path.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Path.GetFileName(path);
        return name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePowerShellPreview(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("pwsh-preview.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains("7-preview", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.Contains("PowerShellPreview", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var blob = $"{vi.ProductName} {vi.FileDescription} {vi.ProductVersion} {vi.FileVersion} {vi.InternalName}";
            if (blob.Contains("preview", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }

        // Store execution alias: version info is unreadable on the reparse point,
        // so a preview-only store install must be identified via siblings/AppX.
        if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            if (File.Exists(Path.Combine(dir, "pwsh-preview.exe")) &&
                TryFindAppxInstallPath("Microsoft.PowerShell") is null &&
                TryFindAppxInstallPath("Microsoft.PowerShellPreview") is not null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Store/AppX install path without listing Program Files\WindowsApps (often access-denied).
    /// Exact package name match — "Microsoft.PowerShell" must never match
    /// "Microsoft.PowerShellPreview".
    /// </summary>
    private static string? TryFindAppxInstallPath(string packageName)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    var name = pkg.Id?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var loc = pkg.InstalledLocation?.Path;
                    if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                        return loc;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// PowerShell 7 candidates, stable first:
    /// (a) %ProgramFiles%\PowerShell\7\pwsh.exe, (b) pwsh.exe on PATH,
    /// (c) Microsoft.PowerShell store install (AppX / WindowsApps),
    /// (d) preview locations — accepted by ResolvePowerShell as fallback only.
    /// </summary>
    private static IEnumerable<string> EnumeratePowerShellCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        var roots = new[] { programFiles, programW6432, programFilesX86 };

        // (a) Machine-wide MSI install of stable PowerShell 7.
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "PowerShell", "7", "pwsh.exe");
        }

        // (b) pwsh.exe on PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim().Trim('"'), "pwsh.exe"); }
            catch { continue; }
            yield return full;
        }

        // (c) Microsoft Store install of stable PowerShell.
        var stableAppx = TryFindAppxInstallPath("Microsoft.PowerShell");
        if (stableAppx is not null)
            yield return Path.Combine(stableAppx, "pwsh.exe");

        var winApps = Path.Combine(programFiles, "WindowsApps");
        if (Directory.Exists(winApps))
        {
            string[] stableMatches = Array.Empty<string>();
            try
            {
                stableMatches = Directory.GetDirectories(winApps, "Microsoft.PowerShell_*")
                    .Where(d => !d.Contains("~", StringComparison.Ordinal))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { }

            foreach (var dir in stableMatches)
                yield return Path.Combine(dir, "pwsh.exe");
        }

        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe");

        // Exo-managed portable stable copy (winget-less fallback install).
        yield return Path.Combine(PortablePwshDir, "pwsh.exe");

        // (d) Preview locations — fallback only, never preferred over stable.
        var previewAppx = TryFindAppxInstallPath("Microsoft.PowerShellPreview");
        if (previewAppx is not null)
        {
            yield return Path.Combine(previewAppx, "pwsh.exe");
            yield return Path.Combine(previewAppx, "pwsh-preview.exe");
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "PowerShell", "7-preview", "pwsh.exe");
            yield return Path.Combine(root, "PowerShell", "7-preview", "pwsh-preview.exe");
        }

        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh-preview.exe");

        // Legacy Exo-managed portable preview copy (pre-stable-migration installs).
        yield return Path.Combine(PathHelper.AppDataDir, "runtime", "PowerShellPreview", "pwsh.exe");

        if (Directory.Exists(winApps))
        {
            string[] previewMatches = Array.Empty<string>();
            try
            {
                previewMatches = Directory.GetDirectories(winApps, "Microsoft.PowerShellPreview_*")
                    .Where(d => !d.Contains("~", StringComparison.Ordinal))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { }

            foreach (var dir in previewMatches)
                yield return Path.Combine(dir, "pwsh.exe");
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim().Trim('"'), "pwsh-preview.exe"); }
            catch { continue; }
            yield return full;
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
