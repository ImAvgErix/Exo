using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OptiHub.Helpers;
using OptiHub.Models;

namespace OptiHub.Services;

public sealed class PowerShellRunnerService
{
    private static readonly Regex ProgressRegex = new(
        @"OPTIHUB_PROGRESS\s*:\s*(\d{1,3})\s*\|\s*(.+)$",
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
            if (elevate)
                return await RunElevatedAsync(scriptPath, opts, workDir, progress, cancellationToken);

            return await RunRedirectedAsync(scriptPath, opts, workDir, progress, cancellationToken);
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
        psi.Environment["OPTIHUB"] = "1";
        psi.Environment["OPTIHUB_LOG"] = logPath;
        psi.Environment["DISCOPT_NONINTERACTIVE"] = "1";

        var output = new StringBuilder();
        var lastStatus = "Starting...";
        var lastPercent = 2.0;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            ParseLine(e.Data, ref lastPercent, ref lastStatus, progress);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            ParseLine(e.Data, ref lastPercent, ref lastStatus, progress);
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
        await process.WaitForExitAsync(cancellationToken);

        var text = output.ToString();
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

    private static async Task<ScriptRunResult> RunElevatedAsync(
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
        var outTmp = logPath + ".out";
        var errTmp = logPath + ".err";

        var scriptEsc = scriptPath.Replace("'", "''");
        var logEsc = logPath.Replace("'", "''");
        var exitEsc = exitPath.Replace("'", "''");
        var workEsc = workDir.Replace("'", "''");
        var outEsc = outTmp.Replace("'", "''");
        var errEsc = errTmp.Replace("'", "''");

        // Elevated wrapper:
        // 1) OPTIHUB_LOG lets scripts append progress directly (most reliable)
        // 2) Also redirect child stdout/stderr and tee into the same log
        var wrapperBody = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Continue'",
            "$env:OPTIHUB = '1'",
            "$env:DISCOPT_NONINTERACTIVE = '1'",
            "$env:OPTIHUB_LOG = '" + logEsc + "'",
            "Set-Location -LiteralPath '" + workEsc + "'",
            "$log = '" + logEsc + "'",
            "$exitFile = '" + exitEsc + "'",
            "$script = '" + scriptEsc + "'",
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
            "Write-LogLine 'OPTIHUB_PROGRESS:5|Elevated session started'",
            "$code = 1",
            "try {",
            "  $psExe = (Get-Process -Id $PID).Path",
            "  $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $script) + @($args)",
            "  Write-LogLine 'OPTIHUB_PROGRESS:8|Starting optimizer...'",
            "  $p = Start-Process -FilePath $psExe -ArgumentList $argList -WorkingDirectory '" + workEsc + "' -PassThru -WindowStyle Hidden -RedirectStandardOutput $outPath -RedirectStandardError $errPath",
            "  if ($null -eq $p) { throw 'Failed to start optimizer process' }",
            "  $outPos = 0L; $errPos = 0L",
            "  while (-not $p.HasExited) {",
            "    Sync-Stream $outPath ([ref]$outPos)",
            "    Sync-Stream $errPath ([ref]$errPos)",
            "    Start-Sleep -Milliseconds 120",
            "  }",
            "  Sync-Stream $outPath ([ref]$outPos)",
            "  Sync-Stream $errPath ([ref]$errPos)",
            "  $code = [int]$p.ExitCode",
            "} catch {",
            "  Write-LogLine ('[-] Elevated child failed: ' + $_.Exception.Message)",
            "  Write-LogLine 'OPTIHUB_PROGRESS:10|Retrying in-process...'",
            "  try {",
            "    & $script @args 2>&1 | ForEach-Object { Write-LogLine (\"$_\") }",
            "    $code = 0",
            "    if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }",
            "  } catch {",
            "    Write-LogLine ('[-] ' + $_.Exception.Message)",
            "    $code = 1",
            "  }",
            "}",
            "if ($code -eq 0) { Write-LogLine 'OPTIHUB_PROGRESS:100|Completed successfully' }",
            "else { Write-LogLine 'OPTIHUB_PROGRESS:100|Finished with errors' }",
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii",
            "exit $code"
        });

        await File.WriteAllTextAsync(wrapper, wrapperBody, cancellationToken);

        var psExe = ResolvePowerShell();
        var argBuilder = new StringBuilder();
        argBuilder.Append("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ");
        argBuilder.Append('"').Append(wrapper).Append('"');
        foreach (var o in opts)
        {
            argBuilder.Append(' ');
            argBuilder.Append(QuoteArg(o));
        }

        var psEsc = psExe.Replace("\"", "\"\"");
        var argsEsc = argBuilder.ToString().Replace("\"", "\"\"");
        var vbsBody =
            "Set shell = CreateObject(\"Shell.Application\")\r\n" +
            "shell.ShellExecute \"" + psEsc + "\", \"" + argsEsc + "\", \"\", \"runas\", 0\r\n";
        await File.WriteAllTextAsync(vbsPath, vbsBody, cancellationToken);

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
                ErrorMessage = "Failed to start elevated PowerShell.",
                Summary = "Launch failed"
            };
        }

        await launcher.WaitForExitAsync(cancellationToken);

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
            else if (DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(180) && !sawLog)
            {
                progress?.Report(new ScriptRunProgress { Percent = 0, Status = "Elevation cancelled" });
                CleanupTemp(wrapper, vbsPath, logPath, exitPath, outTmp, errTmp);
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
                progress?.Report(new ScriptRunProgress { Percent = lastPercent, Status = "Timed out" });
                CleanupTemp(wrapper, vbsPath, logPath, exitPath, outTmp, errTmp);
                return new ScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Summary = "Timed out",
                    ErrorMessage = "Optimizer did not finish within 25 minutes.",
                    FullOutput = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : string.Empty
                };
            }

            await Task.Delay(200, cancellationToken);
        }

        for (var i = 0; i < 15; i++)
        {
            PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
            await Task.Delay(50, cancellationToken);
        }

        var exitCode = 1;
        if (File.Exists(exitPath) && int.TryParse((await File.ReadAllTextAsync(exitPath, cancellationToken)).Trim(), out var parsed))
            exitCode = parsed;

        var full = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : string.Empty;
        var ok = exitCode == 0;

        progress?.Report(new ScriptRunProgress
        {
            Percent = ok ? 100 : lastPercent,
            Status = ok ? "Completed successfully" : "Finished with errors",
        });

        CleanupTemp(wrapper, vbsPath, null, exitPath, outTmp, errTmp);

        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = exitCode,
            FullOutput = full,
            Summary = ok ? "Completed successfully" : $"Exited with code {exitCode}",
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

        var lastError = Path.Combine(PathHelper.LogsDir, "last-discord-error.log");
        if (File.Exists(lastError))
            message += Environment.NewLine + "Error log: " + lastError;
        else if (!string.IsNullOrWhiteSpace(runLogPath) && File.Exists(runLogPath))
            message += Environment.NewLine + "OptiHub log: " + runLogPath;

        return message;
    }

    private static string? _cachedPowerShellPath;

    private static string ResolvePowerShell()
    {
        if (_cachedPowerShellPath is not null && File.Exists(_cachedPowerShellPath))
            return _cachedPowerShellPath;

        foreach (var path in EnumeratePowerShellCandidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    _cachedPowerShellPath = path;
                    return path;
                }
            }
            catch { /* continue */ }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in new[] { "pwsh-preview.exe", "pwsh.exe" })
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(full))
                    {
                        _cachedPowerShellPath = full;
                        return full;
                    }
                }
                catch { /* continue */ }
            }
        }

        _cachedPowerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return _cachedPowerShellPath;
    }

    private static IEnumerable<string> EnumeratePowerShellCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        yield return Path.Combine(PathHelper.WorkingScriptsDir, "Discord", "kit", "tools", "pwsh", "pwsh.exe");
        yield return Path.Combine(PathHelper.DiscordScriptsDir, "kit", "tools", "pwsh", "pwsh.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "Microsoft.PowerShellPreview_8wekyb3d8bbwe", "pwsh.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh-preview.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe");

        var winApps = Path.Combine(programFiles, "WindowsApps");
        if (Directory.Exists(winApps))
        {
            string[] matches = Array.Empty<string>();
            try
            {
                matches = Directory.GetDirectories(winApps, "Microsoft.PowerShellPreview_*_x64__*")
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch { /* access denied is common */ }

            foreach (var dir in matches)
                yield return Path.Combine(dir, "pwsh.exe");
        }

        yield return Path.Combine(programFiles, "PowerShell", "7-preview", "pwsh.exe");
        yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Contains(' ') || arg.Contains('"'))
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return arg;
    }
}
