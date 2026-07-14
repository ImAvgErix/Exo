using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Exo.Models;

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
        string? workingDirectory = null)
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
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Always silent: PowerShell 7 Preview only, no visible Terminal window.
                // Windows Terminal Preview is still required on the machine (checked at startup)
                // but scripts never flash UI to the user.
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
    /// Runs apply/repair in Windows Terminal Preview hosting PowerShell 7 Preview.
    /// Progress is polled from EXO_LOG + exit file.
    /// </summary>
    private static async Task<ScriptRunResult> RunViaTerminalPreviewAsync(
        string scriptPath,
        List<string> opts,
        string workDir,
        IProgress<ScriptRunProgress>? progress,
        bool elevate,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PathHelper.LogsDir);
        var stamp = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(PathHelper.LogsDir, $"run-{stamp}.log");
        var exitPath = Path.Combine(PathHelper.LogsDir, $"exit-{stamp}.txt");
        var wrapper = Path.Combine(PathHelper.LogsDir, $"wrap-{stamp}.ps1");
        var vbsPath = Path.Combine(PathHelper.LogsDir, $"elevate-{stamp}.vbs");
        var cancelPath = Path.Combine(PathHelper.LogsDir, $"cancel-{stamp}.txt");

        var pwsh = ResolvePowerShell();
        var terminal = ResolveWindowsTerminalPreview();

        var scriptEsc = scriptPath.Replace("'", "''");
        var logEsc = logPath.Replace("'", "''");
        var exitEsc = exitPath.Replace("'", "''");
        var workEsc = workDir.Replace("'", "''");
        var cancelEsc = cancelPath.Replace("'", "''");
        var pwshEsc = pwsh.Replace("'", "''");

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Continue'",
            "$Host.UI.RawUI.WindowTitle = 'Exo · PowerShell 7 Preview'",
            "$env:EXO = '1'",
            "$env:DISCOPT_NONINTERACTIVE = '1'",
            "$env:EXO_SKIP_BOOT_FLASH = '1'",
            "$env:DISCOPT_SKIP_MANIFEST = '1'",
            "$env:EXO_LOG = '" + logEsc + "'",
            "Set-Location -LiteralPath '" + workEsc + "'",
            "$log = '" + logEsc + "'",
            "$exitFile = '" + exitEsc + "'",
            "$cancelFile = '" + cancelEsc + "'",
            "$script = '" + scriptEsc + "'",
            "$pwsh = '" + pwshEsc + "'",
            "function Write-LogLine([string]$line) {",
            "  if ([string]::IsNullOrWhiteSpace($line)) { return }",
            "  try { Add-Content -LiteralPath $log -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }",
            "  try { Write-Host $line } catch { }",
            "}",
            "'' | Set-Content -LiteralPath $log -Encoding UTF8",
            "Write-LogLine 'EXO_PROGRESS:5|Windows Terminal Preview + PowerShell 7 Preview'",
            "$code = 1",
            "if (Test-Path -LiteralPath $cancelFile) {",
            "  Write-LogLine 'EXO_PROGRESS:0|Cancelled'",
            "  Set-Content -LiteralPath $exitFile -Value -2 -Encoding ascii",
            "  exit -2",
            "}",
            "try {",
            "  Write-LogLine 'EXO_PROGRESS:8|Starting optimizer...'",
            "  $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script)"
        };
        foreach (var o in opts)
            lines.Add("  $argList += '" + o.Replace("'", "''") + "'");
        lines.AddRange(new[]
        {
            "  & $pwsh @argList 2>&1 | ForEach-Object { Write-LogLine (\"$_\") }",
            "  $code = 0",
            "  if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }",
            "} catch {",
            "  Write-LogLine ('[-] ' + $_.Exception.Message)",
            "  $code = 1",
            "}",
            "if ($code -eq 0) { Write-LogLine 'EXO_PROGRESS:100|Completed successfully' }",
            "elseif ($code -eq -2) { Write-LogLine 'EXO_PROGRESS:0|Cancelled' }",
            "else { Write-LogLine 'EXO_PROGRESS:100|Finished with errors' }",
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii",
            "if ($code -ne 0) { Start-Sleep -Seconds 8 } else { Start-Sleep -Seconds 2 }",
            "exit $code"
        });
        await File.WriteAllTextAsync(wrapper, string.Join(Environment.NewLine, lines), cancellationToken)
            .ConfigureAwait(false);

        var wtArgs =
            "-w 0 nt --title \"Exo\" -- \"" + pwsh + "\" -NoProfile -ExecutionPolicy Bypass -File \"" +
            wrapper + "\"";

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { File.WriteAllText(cancelPath, "1"); } catch { }
        });
        cancellationToken.ThrowIfCancellationRequested();

        if (elevate)
        {
            progress?.Report(new ScriptRunProgress
            {
                Percent = 4,
                Status = "Waiting for Administrator approval (Terminal Preview)..."
            });
            var wtEsc = terminal.Replace("\"", "\"\"");
            var argsEsc = wtArgs.Replace("\"", "\"\"");
            var vbsBody =
                "Set shell = CreateObject(\"Shell.Application\")\r\n" +
                "shell.ShellExecute \"" + wtEsc + "\", \"" + argsEsc + "\", \"\", \"runas\", 1\r\n";
            await File.WriteAllTextAsync(vbsPath, vbsBody, cancellationToken).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"),
                Arguments = "//B //Nologo \"" + vbsPath + "\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var launcher = Process.Start(psi);
            if (launcher is null)
            {
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    ErrorMessage = "Failed to start elevated Windows Terminal Preview.",
                    Summary = "Launch failed"
                };
            }
            await launcher.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new ScriptRunProgress
            {
                Percent = 4,
                Status = "Opening Windows Terminal Preview..."
            });
            var psi = new ProcessStartInfo
            {
                FileName = terminal,
                Arguments = wtArgs,
                WorkingDirectory = workDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            using var launcher = Process.Start(psi);
            if (launcher is null)
            {
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    ErrorMessage = "Failed to start Windows Terminal Preview.",
                    Summary = "Launch failed"
                };
            }
        }

        var lastPercent = 5.0;
        var lastStatus = elevate
            ? "Waiting for Administrator approval (Terminal Preview)..."
            : "Running in Terminal Preview...";
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
                    lastStatus = "Optimizer running in Terminal Preview...";
                    progress?.Report(new ScriptRunProgress { Percent = 8, Status = lastStatus });
                }
                PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
            }
            else if (elevate && DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(45) && !sawLog)
            {
                progress?.Report(new ScriptRunProgress { Percent = 0, Status = "Elevation cancelled" });
                try { File.Delete(wrapper); } catch { }
                try { File.Delete(vbsPath); } catch { }
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Summary = "Elevation cancelled",
                    ErrorMessage = "Administrator approval was cancelled or Terminal Preview never started."
                };
            }

            if (DateTime.UtcNow - startedUtc > timeout)
            {
                var timedOutOutput = File.Exists(logPath)
                    ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    FullOutput = timedOutOutput,
                    Summary = "Timed out",
                    ErrorMessage = "Optimizer timed out in Terminal Preview.",
                    LogPath = logPath
                };
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
        var exitText = (await File.ReadAllTextAsync(exitPath, cancellationToken).ConfigureAwait(false)).Trim();
        _ = int.TryParse(exitText, out var exitCode);
        var fullOutput = File.Exists(logPath)
            ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var ok = exitCode == 0;
        progress?.Report(new ScriptRunProgress
        {
            Percent = ok ? 100 : lastPercent,
            Status = ok ? "Completed successfully" : exitCode == -2 ? "Cancelled" : "Finished with errors"
        });

        try { File.Delete(wrapper); } catch { }
        try { File.Delete(vbsPath); } catch { }
        try { File.Delete(cancelPath); } catch { }
        try { File.Delete(exitPath); } catch { }

        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = exitCode,
            FullOutput = fullOutput,
            Summary = ok ? "Completed successfully" : exitCode == -2 ? "Cancelled" : $"Exited with code {exitCode}",
            ErrorMessage = ok ? null : ExtractError(fullOutput, logPath),
            LogPath = logPath
        };
    }

    /// <summary>
    /// Elevated apply/repair via PowerShell 7 Preview — hidden, no Terminal window.
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
        var logPath = Path.Combine(PathHelper.LogsDir, $"run-{stamp}.log");
        var exitPath = Path.Combine(PathHelper.LogsDir, $"exit-{stamp}.txt");
        var wrapper = Path.Combine(PathHelper.LogsDir, $"wrap-{stamp}.ps1");
        var vbsPath = Path.Combine(PathHelper.LogsDir, $"elevate-{stamp}.vbs");
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

        var wrapperBody = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Continue'",
            "$env:EXO = '1'",
            "$env:DISCOPT_NONINTERACTIVE = '1'",
            "$env:EXO_SKIP_BOOT_FLASH = '1'",
            "$env:DISCOPT_SKIP_MANIFEST = '1'",
            "$env:EXO_LOG = '" + logEsc + "'",
            "Set-Location -LiteralPath '" + workEsc + "'",
            "$log = '" + logEsc + "'",
            "$exitFile = '" + exitEsc + "'",
            "$cancelFile = '" + cancelEsc + "'",
            "$script = '" + scriptEsc + "'",
            "$pwsh = '" + pwshEsc + "'",
            "$outPath = '" + outEsc + "'",
            "$errPath = '" + errEsc + "'",
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
            "Write-LogLine 'EXO_PROGRESS:5|Elevated PowerShell 7 Preview (silent)'",
            "$code = 1",
            "if (Test-Path -LiteralPath $cancelFile) {",
            "  Write-LogLine 'EXO_PROGRESS:0|Cancelled'",
            "  Set-Content -LiteralPath $exitFile -Value -2 -Encoding ascii",
            "  exit -2",
            "}",
            "try {",
            "  $argText = '-NoProfile -ExecutionPolicy Bypass -File \"' + $script + '\"'",
            "  foreach ($item in @($args)) { $argText += ' \"' + ([string]$item) + '\"' }",
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
            "  try {",
            "    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $script @args 2>&1 | ForEach-Object { Write-LogLine (\"$_\") }",
            "    $code = 0",
            "    if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }",
            "  } catch {",
            "    Write-LogLine ('[-] ' + $_.Exception.Message)",
            "    $code = 1",
            "  }",
            "}",
            "if ($code -eq 0) { Write-LogLine 'EXO_PROGRESS:100|Completed successfully' }",
            "elseif ($code -eq -2) { Write-LogLine 'EXO_PROGRESS:0|Cancelled' }",
            "else { Write-LogLine 'EXO_PROGRESS:100|Finished with errors' }",
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii",
            "exit $code"
        });
        await File.WriteAllTextAsync(wrapper, wrapperBody, cancellationToken).ConfigureAwait(false);

        var argBuilder = new StringBuilder();
        argBuilder.Append("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ");
        argBuilder.Append('"').Append(wrapper).Append('"');
        foreach (var o in opts)
        {
            argBuilder.Append(' ');
            argBuilder.Append(QuoteArg(o));
        }

        var psEsc = pwsh.Replace("\"", "\"\"");
        var argsEsc = argBuilder.ToString().Replace("\"", "\"\"");
        var vbsBody =
            "Set shell = CreateObject(\"Shell.Application\")\r\n" +
            "shell.ShellExecute \"" + psEsc + "\", \"" + argsEsc + "\", \"\", \"runas\", 0\r\n";
        await File.WriteAllTextAsync(vbsPath, vbsBody, cancellationToken).ConfigureAwait(false);

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            SignalElevatedCancellation(cancelPath, wrapper, vbsPath, cancelPath, exitPath, outTmp, errTmp);
        });
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new ScriptRunProgress
        {
            Percent = 4,
            Status = "Waiting for Administrator approval..."
        });

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"),
            Arguments = "//B //Nologo \"" + vbsPath + "\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var launcher = Process.Start(psi);
        if (launcher is null)
        {
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "Failed to start elevated PowerShell 7 Preview.",
                Summary = "Launch failed"
            };
        }
        await launcher.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
            else if (DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(30) && !sawLog)
            {
                progress?.Report(new ScriptRunProgress { Percent = 0, Status = "Elevation cancelled" });
                CleanupTemp(wrapper, vbsPath, cancelPath, logPath, exitPath, outTmp, errTmp);
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Summary = "Elevation cancelled",
                    ErrorMessage = "Administrator approval was cancelled or the elevated session never started."
                };
            }

            if (DateTime.UtcNow - startedUtc > timeout)
            {
                var timedOutOutput = File.Exists(logPath)
                    ? await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                SignalElevatedCancellation(cancelPath, wrapper, vbsPath, cancelPath, exitPath, outTmp, errTmp);
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
        CleanupTemp(wrapper, vbsPath, cancelPath, exitPath, outTmp, errTmp);
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
    private static string? _cachedTerminalPreviewPath;

    /// <summary>
    /// Path to PowerShell 7 Preview only. Never Windows PowerShell 5.1, never stable 7.
    /// </summary>
    public static string ResolvePowerShell()
    {
        if (_cachedPowerShellPath is not null && File.Exists(_cachedPowerShellPath) &&
            LooksLikePowerShellPreview(_cachedPowerShellPath))
            return _cachedPowerShellPath;

        string? stableHint = null;
        foreach (var path in EnumeratePowerShellPreviewCandidates())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;
                if (LooksLikePowerShellPreview(path))
                {
                    _cachedPowerShellPath = path;
                    return path;
                }

                // Remember stable 7 for a clearer error (common mix-up).
                if (stableHint is null && LooksLikePowerShellStable(path))
                    stableHint = path;
            }
            catch { /* continue */ }
        }

        if (stableHint is not null)
        {
            throw new InvalidOperationException(
                "Found stable PowerShell 7, but Exo needs PowerShell 7 Preview.\n" +
                $"Stable: {stableHint}\n" +
                "Install Preview: winget install Microsoft.PowerShell.Preview\n" +
                "Then restart Exo (and sign out if the Store just finished installing).");
        }

        throw new InvalidOperationException(
            "PowerShell 7 Preview not found.\n" +
            "Install: winget install Microsoft.PowerShell.Preview\n" +
            "Or Microsoft Store → “PowerShell Preview”. Then restart Exo.");
    }

    /// <summary>Windows Terminal Preview host used to run optimizer scripts.</summary>
    public static string ResolveWindowsTerminalPreview()
    {
        if (_cachedTerminalPreviewPath is not null && File.Exists(_cachedTerminalPreviewPath))
            return _cachedTerminalPreviewPath;

        foreach (var path in EnumerateTerminalPreviewCandidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    _cachedTerminalPreviewPath = path;
                    return path;
                }
            }
            catch { /* continue */ }
        }

        throw new InvalidOperationException(
            "Windows Terminal Preview not found.\n" +
            "Install: winget install Microsoft.WindowsTerminal.Preview\n" +
            "Then restart Exo.");
    }

    public static string? TryGetPowerShellPath()
    {
        try { return ResolvePowerShell(); }
        catch { return null; }
    }

    public static string? TryGetWindowsTerminalPreviewPath()
    {
        try { return ResolveWindowsTerminalPreview(); }
        catch { return null; }
    }

    /// <summary>
    /// Requires PowerShell 7 Preview + Windows Terminal Preview. Installs both via winget if missing.
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsurePowerShellRuntimeAsync(
        CancellationToken cancellationToken = default)
    {
        var winget = FindWinget();
        var parts = new List<string>();

        var psOk = TryGetPowerShellPath() is not null;
        if (!psOk)
        {
            if (winget is null)
            {
                return (false,
                    "PowerShell 7 Preview is required. Install Microsoft.PowerShell.Preview (winget/Store), then restart Exo.");
            }

            var psInstall = await RunWingetAsync(
                winget,
                new[]
                {
                    "install", "--id", "Microsoft.PowerShell.Preview", "-e",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity", "--silent"
                },
                cancellationToken).ConfigureAwait(false);
            _cachedPowerShellPath = null;
            psOk = TryGetPowerShellPath() is not null;
            parts.Add(psOk
                ? "PowerShell Preview ready"
                : "PowerShell Preview install failed: " + psInstall.Detail);
        }
        else
        {
            parts.Add("PowerShell Preview: " + TryGetPowerShellPath());
        }

        var wtOk = TryGetWindowsTerminalPreviewPath() is not null;
        if (!wtOk)
        {
            if (winget is null)
            {
                return (false,
                    "Windows Terminal Preview is required. Install Microsoft.WindowsTerminal.Preview (winget/Store), then restart Exo. " +
                    string.Join("; ", parts));
            }

            var wtInstall = await RunWingetAsync(
                winget,
                new[]
                {
                    "install", "--id", "Microsoft.WindowsTerminal.Preview", "-e",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity", "--silent"
                },
                cancellationToken).ConfigureAwait(false);
            _cachedTerminalPreviewPath = null;
            wtOk = TryGetWindowsTerminalPreviewPath() is not null;
            parts.Add(wtOk
                ? "Terminal Preview ready"
                : "Terminal Preview install failed: " + wtInstall.Detail);
        }
        else
        {
            parts.Add("Terminal Preview: " + TryGetWindowsTerminalPreviewPath());
        }

        if (psOk && wtOk)
            return (true, string.Join("; ", parts));

        return (false, string.Join("; ", parts));
    }

    private static IEnumerable<string> EnumerateTerminalPreviewCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var winApps = Path.Combine(programFiles, "WindowsApps");

        // AppX path first — avoids access-denied when listing WindowsApps.
        var appx = TryFindAppxInstallPath("Microsoft.WindowsTerminalPreview");
        if (appx is not null)
        {
            yield return Path.Combine(appx, "WindowsTerminal.exe");
            yield return Path.Combine(appx, "wt.exe");
        }

        if (Directory.Exists(winApps))
        {
            string[] dirs = Array.Empty<string>();
            try
            {
                dirs = Directory.GetDirectories(winApps, "Microsoft.WindowsTerminalPreview_*")
                    .Where(d => !d.Contains("~", StringComparison.Ordinal))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { }

            foreach (var dir in dirs)
            {
                yield return Path.Combine(dir, "WindowsTerminal.exe");
                yield return Path.Combine(dir, "wt.exe");
            }
        }

        yield return Path.Combine(local, "Microsoft", "WindowsApps", "wt-preview.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "WindowsTerminalPreview.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe");
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

    private static bool LooksLikePowerShellStable(string path)
    {
        if (path.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileName(path);
        if (!name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("7-preview", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PowerShellPreview", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("PowerShell\\7\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PowerShell/7/", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var blob = $"{vi.ProductName} {vi.FileDescription} {vi.ProductVersion} {vi.FileVersion}";
            if (blob.Contains("preview", StringComparison.OrdinalIgnoreCase))
                return false;
            if (blob.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    private static bool LooksLikePowerShellPreview(string path)
    {
        if (path.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase))
            return false;

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

        if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            if (File.Exists(Path.Combine(dir, "pwsh-preview.exe")))
                return true;
            if (TryFindAppxInstallPath("Microsoft.PowerShellPreview") is not null)
                return true;
            try
            {
                var winApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                if (Directory.Exists(winApps) &&
                    Directory.GetDirectories(winApps, "Microsoft.PowerShellPreview_*").Length > 0)
                    return true;
            }
            catch { }
        }

        if (path.Contains("PowerShell\\7\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("PowerShell/7/", StringComparison.OrdinalIgnoreCase))
            return false;

        return false;
    }

    /// <summary>
    /// Store/AppX install path without listing Program Files\WindowsApps (often access-denied).
    /// </summary>
    private static string? TryFindAppxInstallPath(string packageNamePrefix)
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
                    if (!name.StartsWith(packageNamePrefix, StringComparison.OrdinalIgnoreCase))
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

    private static IEnumerable<string> EnumeratePowerShellPreviewCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");

        var appx = TryFindAppxInstallPath("Microsoft.PowerShellPreview");
        if (appx is not null)
        {
            yield return Path.Combine(appx, "pwsh.exe");
            yield return Path.Combine(appx, "pwsh-preview.exe");
        }

        foreach (var root in new[] { programFiles, programW6432, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            yield return Path.Combine(root, "PowerShell", "7-preview", "pwsh.exe");
            yield return Path.Combine(root, "PowerShell", "7-preview", "pwsh-preview.exe");
        }

        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh-preview.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe");

        var winApps = Path.Combine(programFiles, "WindowsApps");
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

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in new[] { "pwsh-preview.exe", "pwsh.exe" })
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string full;
                try { full = Path.Combine(dir.Trim().Trim('"'), name); }
                catch { continue; }
                yield return full;
            }
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
