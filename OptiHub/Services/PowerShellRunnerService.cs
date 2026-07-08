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

        progress?.Report(new ScriptRunProgress { Percent = 2, Status = "Starting…" });

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

        var output = new StringBuilder();
        var lastStatus = "Starting…";
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
            ErrorMessage = ok ? null : ExtractError(text)
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

        var scriptEsc = scriptPath.Replace("'", "''");
        var logEsc = logPath.Replace("'", "''");
        var exitEsc = exitPath.Replace("'", "''");
        var workEsc = workDir.Replace("'", "''");

        // Stream each line to the log as it arrives so the UI can poll live progress.
        // Write exit code to a sentinel file when finished (ShellExecute cannot wait).
        var wrapperBody =
            "$ErrorActionPreference = 'Continue'" + Environment.NewLine +
            "$env:OPTIHUB = '1'" + Environment.NewLine +
            "$env:DISCOPT_NONINTERACTIVE = '1'" + Environment.NewLine +
            "Set-Location -LiteralPath '" + workEsc + "'" + Environment.NewLine +
            "$log = '" + logEsc + "'" + Environment.NewLine +
            "$exitFile = '" + exitEsc + "'" + Environment.NewLine +
            "$script = '" + scriptEsc + "'" + Environment.NewLine +
            "function Write-LogLine([string]$line) {" + Environment.NewLine +
            "  Add-Content -LiteralPath $log -Value $line -Encoding UTF8" + Environment.NewLine +
            "}" + Environment.NewLine +
            "'' | Set-Content -LiteralPath $log -Encoding UTF8" + Environment.NewLine +
            "Write-LogLine 'OPTIHUB_PROGRESS:5|Waiting for Administrator approval…'" + Environment.NewLine +
            "$code = 1" + Environment.NewLine +
            "try {" + Environment.NewLine +
            "  Write-LogLine 'OPTIHUB_PROGRESS:8|Elevated session started'" + Environment.NewLine +
            "  $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @(" + Environment.NewLine +
            "    '-NoProfile','-ExecutionPolicy','Bypass','-File', $script" + Environment.NewLine +
            "  ) + $args -WorkingDirectory '" + workEsc + "' -NoNewWindow -PassThru -RedirectStandardOutput $log.tmp.out -RedirectStandardError $log.tmp.err" + Environment.NewLine +
            "  # Prefer streaming via a child that tees; fall back to in-process call" + Environment.NewLine +
            "  if ($null -eq $p) { throw 'child start failed' }" + Environment.NewLine +
            "  $outPath = \"$log.tmp.out\"; $errPath = \"$log.tmp.err\"" + Environment.NewLine +
            "  $outPos = 0L; $errPos = 0L" + Environment.NewLine +
            "  while (-not $p.HasExited) {" + Environment.NewLine +
            "    Sync-Stream $outPath ([ref]$outPos)" + Environment.NewLine +
            "    Sync-Stream $errPath ([ref]$errPos)" + Environment.NewLine +
            "    Start-Sleep -Milliseconds 150" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  Sync-Stream $outPath ([ref]$outPos)" + Environment.NewLine +
            "  Sync-Stream $errPath ([ref]$errPos)" + Environment.NewLine +
            "  $code = $p.ExitCode" + Environment.NewLine +
            "} catch {" + Environment.NewLine +
            "  Write-LogLine ('[-] Elevated wrapper: ' + $_.Exception.Message)" + Environment.NewLine +
            "  Write-LogLine 'OPTIHUB_PROGRESS:12|Running optimizer…'" + Environment.NewLine +
            "  try {" + Environment.NewLine +
            "    & $script @args 2>&1 | ForEach-Object { Write-LogLine (\"$_\") }" + Environment.NewLine +
            "    $code = 0" + Environment.NewLine +
            "    if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }" + Environment.NewLine +
            "  } catch {" + Environment.NewLine +
            "    Write-LogLine ('[-] ' + $_.Exception.Message)" + Environment.NewLine +
            "    $code = 1" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "}" + Environment.NewLine +
            "function Sync-Stream([string]$path, [ref]$pos) {" + Environment.NewLine +
            "  if (-not (Test-Path -LiteralPath $path)) { return }" + Environment.NewLine +
            "  try {" + Environment.NewLine +
            "    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')" + Environment.NewLine +
            "    try {" + Environment.NewLine +
            "      if ($fs.Length -le $pos.Value) { return }" + Environment.NewLine +
            "      $fs.Seek($pos.Value, 'Begin') | Out-Null" + Environment.NewLine +
            "      $sr = New-Object IO.StreamReader($fs)" + Environment.NewLine +
            "      while ($null -ne ($line = $sr.ReadLine())) { Write-LogLine $line }" + Environment.NewLine +
            "      $pos.Value = $fs.Position" + Environment.NewLine +
            "    } finally { $fs.Dispose() }" + Environment.NewLine +
            "  } catch { }" + Environment.NewLine +
            "}" + Environment.NewLine +
            "if ($code -eq 0) { Write-LogLine 'OPTIHUB_PROGRESS:100|Completed successfully' }" + Environment.NewLine +
            "else { Write-LogLine 'OPTIHUB_PROGRESS:100|Finished with errors' }" + Environment.NewLine +
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii" + Environment.NewLine +
            "exit $code" + Environment.NewLine;

        // Simpler, reliable wrapper: stream via ForEach-Object (no nested Start-Process)
        wrapperBody =
            "$ErrorActionPreference = 'Continue'" + Environment.NewLine +
            "$env:OPTIHUB = '1'" + Environment.NewLine +
            "$env:DISCOPT_NONINTERACTIVE = '1'" + Environment.NewLine +
            "Set-Location -LiteralPath '" + workEsc + "'" + Environment.NewLine +
            "$log = '" + logEsc + "'" + Environment.NewLine +
            "$exitFile = '" + exitEsc + "'" + Environment.NewLine +
            "$script = '" + scriptEsc + "'" + Environment.NewLine +
            "function Write-LogLine([string]$line) {" + Environment.NewLine +
            "  Add-Content -LiteralPath $log -Value $line -Encoding UTF8" + Environment.NewLine +
            "}" + Environment.NewLine +
            "'' | Set-Content -LiteralPath $log -Encoding UTF8" + Environment.NewLine +
            "Write-LogLine 'OPTIHUB_PROGRESS:5|Elevated session started'" + Environment.NewLine +
            "$code = 1" + Environment.NewLine +
            "try {" + Environment.NewLine +
            "  & $script @args 2>&1 | ForEach-Object {" + Environment.NewLine +
            "    $line = \"$_\"" + Environment.NewLine +
            "    Write-LogLine $line" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  $code = 0" + Environment.NewLine +
            "  if ($null -ne $LASTEXITCODE) { $code = [int]$LASTEXITCODE }" + Environment.NewLine +
            "} catch {" + Environment.NewLine +
            "  Write-LogLine ('[-] ' + $_.Exception.Message)" + Environment.NewLine +
            "  $code = 1" + Environment.NewLine +
            "}" + Environment.NewLine +
            "if ($code -eq 0) { Write-LogLine 'OPTIHUB_PROGRESS:100|Completed successfully' }" + Environment.NewLine +
            "else { Write-LogLine 'OPTIHUB_PROGRESS:100|Finished with errors' }" + Environment.NewLine +
            "Set-Content -LiteralPath $exitFile -Value $code -Encoding ascii" + Environment.NewLine +
            "exit $code" + Environment.NewLine;

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
            Status = "Waiting for Administrator approval…"
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

        // ShellExecute returns immediately — wait on the exit sentinel written by the elevated wrapper.
        var lastPercent = 5.0;
        var lastStatus = "Waiting for Administrator approval…";
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
                    lastStatus = "Optimizer running…";
                    progress?.Report(new ScriptRunProgress { Percent = 8, Status = lastStatus });
                }
                PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
            }
            else if (DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(90) && !sawLog)
            {
                // UAC likely cancelled — no elevated process ever started writing.
                progress?.Report(new ScriptRunProgress { Percent = 0, Status = "Elevation cancelled" });
                CleanupTemp(wrapper, vbsPath, logPath, exitPath);
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
                CleanupTemp(wrapper, vbsPath, logPath, exitPath);
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

        // Drain any remaining log lines after exit file appears
        for (var i = 0; i < 10; i++)
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

        CleanupTemp(wrapper, vbsPath, null, exitPath);

        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = exitCode,
            FullOutput = full,
            Summary = ok ? "Completed successfully" : $"Exited with code {exitCode}",
            ErrorMessage = ok ? null : ExtractError(full)
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
        if (s.Length > 90) s = s[..87] + "…";
        return s;
    }

    private static string? ExtractError(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("[-]", StringComparison.Ordinal) ||
                        l.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
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
