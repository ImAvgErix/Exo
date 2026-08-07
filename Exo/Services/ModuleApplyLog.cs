using System.Text;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// One rolling apply log for every module, at %LocalAppData%\Exo\logs\exo-apply.log.
///
/// It used to write two files per apply - a timestamped one and a "-latest" copy - plus a third
/// for the elevated transaction. Eight modules and a few runs turned the log folder into
/// something nobody could hand over or read in order, which is the opposite of what a log is
/// for. One file, appended, newest at the bottom, trimmed from the front when it gets long.
///
/// The elevated transaction is attached inline instead of copied out, so it appears in the same
/// timeline as the run that caused it rather than in a file you have to correlate by hand.
/// </summary>
public sealed class ModuleApplyLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>Kept small enough to open, paste, and read end to end.</summary>
    private const long MaxBytes = 2L * 1024 * 1024;
    private const long KeepBytes = 1L * 1024 * 1024;

    public string Module { get; }
    /// <summary>The single log every module appends to.</summary>
    public string LatestPath { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public ModuleApplyLog(string module)
    {
        Module = (module ?? "unknown").ToLowerInvariant();
        Directory.CreateDirectory(PathHelper.LogsDir);
        LatestPath = Path.Combine(PathHelper.LogsDir, "exo-apply.log");

        PruneLegacyFiles();
        TrimIfLarge();

        _writer = new StreamWriter(new FileStream(LatestPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        Line("============================================================");
        Line($"Exo module apply log  module={Module}");
        Line($"started={StartedUtc:o}");
        Line($"machine={Environment.MachineName} user={Environment.UserName}");
        Line($"elevated={NativeReg.IsAdministrator()}");
        Line($"appBase={AppContext.BaseDirectory}");
        WriteMachineContext();
        Line("============================================================");
    }

    /// <summary>
    /// What machine this is, at the top of every run.
    ///
    /// Diagnosing the 4.4.0 reports meant asking which GPU, which driver, which Windows —
    /// facts the app already knows and was not writing down. A log you have to ask follow-up
    /// questions about is half a log. Every field is independently guarded: a probe that
    /// throws must cost one line, never the run.
    /// </summary>
    private void WriteMachineContext()
    {
        void Fact(string label, Func<string?> read)
        {
            try
            {
                var v = read();
                Line($"  {label}={(string.IsNullOrWhiteSpace(v) ? "?" : v)}");
            }
            catch (Exception ex) { Line($"  {label}=<unreadable: {ex.GetType().Name}>"); }
        }

        Line("-- machine --");
        Fact("exo", () => typeof(ModuleApplyLog).Assembly.GetName().Version?.ToString());
        Fact("os", () => Environment.OSVersion.VersionString);
        Fact("osLabel", HomeDashboardReader.ResolveOsLabel);
        Fact("cpu", () => NativeReg.GetValue("HKLM",
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.ToString());
        Fact("cores", () => $"{Environment.ProcessorCount} logical");
        Fact("gpu", () => string.Join(" | ", GpuTopology.AdapterDescriptions()));
        Fact("nvidiaDriver", () => NativeLiveDetect.InstalledNvidiaDriverVersion());
        Fact("disk", () =>
        {
            var d = new DriveInfo(Path.GetPathRoot(PathHelper.AppDataDir) ?? "C:\\");
            return $"{d.Name} {d.AvailableFreeSpace / (1024L * 1024 * 1024)} GB free of {d.TotalSize / (1024L * 1024 * 1024)} GB";
        });
        Fact("kits", () =>
        {
            var parts = new List<string>();
            foreach (var kit in new[] { "Discord", "Steam", "Nvidia" })
            {
                var f = Path.Combine(PathHelper.ScriptsRoot, kit, "VERSION");
                if (File.Exists(f)) parts.Add($"{kit.ToLowerInvariant()}={File.ReadAllText(f).Trim()}");
            }
            return string.Join(" ", parts);
        });
        Line("-- /machine --");
    }

    /// <summary>
    /// Deletes the per-module files older builds scattered here. Without this an upgrade leaves
    /// the previous mess in place forever and the folder never actually gets smaller.
    /// </summary>
    private static void PruneLegacyFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(PathHelper.LogsDir, "apply-*.log"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Keeps the tail. Truncating from the front loses the oldest run, not the newest.</summary>
    private void TrimIfLarge()
    {
        try
        {
            var info = new FileInfo(LatestPath);
            if (!info.Exists || info.Length <= MaxBytes) return;
            using var src = new FileStream(LatestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            src.Seek(-KeepBytes, SeekOrigin.End);
            var buf = new byte[KeepBytes];
            var read = src.Read(buf, 0, buf.Length);
            src.Dispose();
            File.WriteAllBytes(LatestPath, buf[..read]);
        }
        catch { /* a log that cannot be trimmed is still a log */ }
    }

    public void Line(string message)
    {
        var text = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (_gate)
        {
            if (_disposed) return;
            try { _writer.WriteLine(text); }
            catch { /* never throw from logger */ }
        }
    }

    public void Step(string id, string status, string? reason = null)
    {
        Line(string.IsNullOrWhiteSpace(reason)
            ? $"STEP  {id}|{status}"
            : $"STEP  {id}|{status}:{reason}");
    }

    public void Progress(double percent, string status) =>
        Line($"PROGRESS  {percent:0.#}%  {status}");

    public void Exception(Exception ex, string? context = null)
    {
        Line($"ERROR  {(context is null ? "" : context + " — ")}{ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
                Line("  " + line.TrimEnd());
        }
        if (ex.InnerException is not null)
            Line($"INNER  {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    public void AttachFile(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Line($"ATTACH  {label}: (none)");
            return;
        }

        Line($"ATTACH  {label}: {path}");
        if (!File.Exists(path))
        {
            Line("  (file missing)");
            return;
        }

        try
        {
            var info = new FileInfo(path);
            Line($"  size={info.Length} bytes  mtime={info.LastWriteTime:o}");
            // Cap attached content so logs stay readable
            var text = File.ReadAllText(path);
            if (text.Length > 120_000)
                text = text[^120_000..] + "\n…(truncated)…\n";
            Line("----- begin attached -----");
            foreach (var line in text.Split('\n'))
                Line(line.TrimEnd('\r'));
            Line("----- end attached -----");
        }
        catch (Exception ex)
        {
            Line($"  attach-read-fail: {ex.Message}");
        }
    }

    public void Finish(bool ok, string summary)
    {
        var elapsed = DateTimeOffset.UtcNow - StartedUtc;
        Line("============================================================");
        Line(ok ? $"RESULT  OK  {summary}" : $"RESULT  FAIL  {summary}");
        Line($"elapsed={elapsed.TotalSeconds:0.0}s");
        Line("============================================================");
        Flush();
    }

    private void Flush()
    {
        try { lock (_gate) { _writer.Flush(); } }
        catch { /* non-fatal */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch { }
        }
        Flush();
    }

    /// <summary>Attaches the elevated ProgramData transaction inline, in run order.</summary>
    public static void MirrorElevatedTransaction(string module, string? elevatedLogPath, ModuleApplyLog? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(elevatedLogPath)) return;
        // Inline only. The separate per-module copy this used to leave behind was a third file
        // holding a duplicate of text already sitting above it.
        session.AttachFile($"elevated-run.log ({module})", elevatedLogPath);
    }
}
