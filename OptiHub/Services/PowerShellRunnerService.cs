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
            progress?.Report(new ScriptRunProgress
            {
                Percent = 0,
                Status = "Cancelled",
                IsComplete = true,
                IsError = true
            });
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
                IsComplete = true,
                IsError = true
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
            IsComplete = true,
            IsError = !ok
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
        var logPath = Path.Combine(PathHelper.LogsDir, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        var wrapper = Path.Combine(PathHelper.LogsDir, $"wrap-{Guid.NewGuid():N}.ps1");

        var scriptEsc = scriptPath.Replace("'", "''");
        var logEsc = logPath.Replace("'", "''");
        var workEsc = workDir.Replace("'", "''");

        // Build PowerShell wrapper without C# raw-string brace conflicts
        var wrapperBody =
            "$ErrorActionPreference = 'Continue'" + Environment.NewLine +
            "$env:OPTIHUB = '1'" + Environment.NewLine +
            "$env:DISCOPT_NONINTERACTIVE = '1'" + Environment.NewLine +
            "Set-Location -LiteralPath '" + workEsc + "'" + Environment.NewLine +
            "$log = '" + logEsc + "'" + Environment.NewLine +
            "$script = '" + scriptEsc + "'" + Environment.NewLine +
            "\"OPTIHUB_PROGRESS:5|Elevated session started\" | Set-Content -Path $log -Encoding UTF8" + Environment.NewLine +
            "try {" + Environment.NewLine +
            "  $output = & $script @args 2>&1" + Environment.NewLine +
            "  foreach ($line in $output) {" + Environment.NewLine +
            "    Add-Content -Path $log -Value (\"$line\") -Encoding UTF8" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  $code = 0" + Environment.NewLine +
            "  if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE }" + Environment.NewLine +
            "} catch {" + Environment.NewLine +
            "  Add-Content -Path $log -Value (\"[-] \" + $_.Exception.Message) -Encoding UTF8" + Environment.NewLine +
            "  $code = 1" + Environment.NewLine +
            "}" + Environment.NewLine +
            "if ($code -eq 0) {" + Environment.NewLine +
            "  Add-Content -Path $log -Value \"OPTIHUB_PROGRESS:100|Completed successfully\" -Encoding UTF8" + Environment.NewLine +
            "} else {" + Environment.NewLine +
            "  Add-Content -Path $log -Value \"OPTIHUB_PROGRESS:100|Finished with errors\" -Encoding UTF8" + Environment.NewLine +
            "}" + Environment.NewLine +
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

        // Hidden elevated launch via VBScript Shell.Application (CreateNoWindow + runas still flashes a console)
        var vbsPath = Path.Combine(PathHelper.LogsDir, $"elevate-{Guid.NewGuid():N}.vbs");
        var psEsc = psExe.Replace("'", "''");
        var argsEsc = argBuilder.ToString().Replace("\"", "\"\"");
        var vbsBody =
            "Set shell = CreateObject(\"Shell.Application\")\r\n" +
            "shell.ShellExecute \"" + psEsc + "\", \"" + argsEsc + "\", \"\", \"runas\", 0\r\n";
        await File.WriteAllTextAsync(vbsPath, vbsBody, cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"),
            Arguments = "//B //Nologo \"" + vbsPath + "\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ScriptRunResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = "Failed to start elevated PowerShell.",
                Summary = "Launch failed"
            };
        }

        var lastPercent = 5.0;
        var lastStatus = "Waiting for elevated session…";
        var lastLength = 0;

        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);
            await Task.Delay(250, cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);
        PollLog(logPath, ref lastLength, ref lastPercent, ref lastStatus, progress);

        var full = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : string.Empty;
        var ok = process.ExitCode == 0;

        progress?.Report(new ScriptRunProgress
        {
            Percent = ok ? 100 : lastPercent,
            Status = ok ? "Completed successfully" : "Finished with errors",
            IsComplete = true,
            IsError = !ok
        });

        try { File.Delete(wrapper); } catch { /* ignore */ }
        try { File.Delete(vbsPath); } catch { /* ignore */ }

        return new ScriptRunResult
        {
            Success = ok,
            ExitCode = process.ExitCode,
            FullOutput = full,
            Summary = ok ? "Completed successfully" : $"Exited with code {process.ExitCode}",
            ErrorMessage = ok ? null : ExtractError(full)
        };
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
                DetailLine = line
            });
            return;
        }

        if (line.StartsWith("[*]", StringComparison.Ordinal) ||
            line.StartsWith("[+]", StringComparison.Ordinal) ||
            line.StartsWith("[!]", StringComparison.Ordinal) ||
            line.StartsWith("[-]", StringComparison.Ordinal))
        {
            lastStatus = line.Trim();
            if (lastPercent < 92)
                lastPercent = Math.Min(92, lastPercent + 1.5);

            progress?.Report(new ScriptRunProgress
            {
                Percent = lastPercent,
                Status = lastStatus,
                DetailLine = line,
                IsError = line.StartsWith("[-]", StringComparison.Ordinal)
            });
        }
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

    /// <summary>
    /// Prefer PowerShell 7.7 (preview) — same target as DiscOpti — then any pwsh, then Windows PowerShell 5.1.
    /// </summary>
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

        // PATH search for pwsh
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

        // 1) Portable copy next to DiscOpti kit (downloaded by Disc-Optimizer if needed)
        yield return Path.Combine(PathHelper.WorkingScriptsDir, "Discord", "kit", "tools", "pwsh", "pwsh.exe");
        yield return Path.Combine(PathHelper.DiscordScriptsDir, "kit", "tools", "pwsh", "pwsh.exe");

        // 2) Store / WindowsApps PowerShell Preview (7.7.x)
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "Microsoft.PowerShellPreview_8wekyb3d8bbwe", "pwsh.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh-preview.exe");
        yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe");

        // WindowsApps package folders (versioned)
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

        // 3) Classic installs
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
