using System.Text;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Detailed per-module apply log written under %LocalAppData%\Exo\logs\.
/// Always creates apply-{module}-latest.log plus a timestamped copy so failures
/// are easy to find after Riot/Windows/etc. die mid-run.
/// </summary>
public sealed class ModuleApplyLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public string Module { get; }
    public string TimestampedPath { get; }
    public string LatestPath { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public ModuleApplyLog(string module)
    {
        Module = (module ?? "unknown").ToLowerInvariant();
        Directory.CreateDirectory(PathHelper.LogsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        TimestampedPath = Path.Combine(PathHelper.LogsDir, $"apply-{Module}-{stamp}.log");
        LatestPath = Path.Combine(PathHelper.LogsDir, $"apply-{Module}-latest.log");

        // Write through to timestamped file; mirror to latest at dispose/flush.
        _writer = new StreamWriter(new FileStream(TimestampedPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
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
        Line($"log={TimestampedPath}");
        Line("============================================================");
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
        Line($"latestCopy={LatestPath}");
        Line("============================================================");
        FlushToLatest();
    }

    private void FlushToLatest()
    {
        try
        {
            lock (_gate)
            {
                _writer.Flush();
            }
            File.Copy(TimestampedPath, LatestPath, overwrite: true);
        }
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
        FlushToLatest();
    }

    /// <summary>Best-effort: copy elevated ProgramData transaction log into user logs.</summary>
    public static void MirrorElevatedTransaction(string module, string? elevatedLogPath, ModuleApplyLog? session)
    {
        if (session is null || string.IsNullOrWhiteSpace(elevatedLogPath)) return;
        session.AttachFile("elevated-run.log", elevatedLogPath);
        try
        {
            if (!File.Exists(elevatedLogPath)) return;
            var dest = Path.Combine(PathHelper.LogsDir, $"apply-{module}-elevated-latest.log");
            File.Copy(elevatedLogPath, dest, overwrite: true);
            session.Line($"Mirrored elevated log → {dest}");
        }
        catch (Exception ex)
        {
            session.Line($"Mirror elevated log failed: {ex.Message}");
        }
    }
}
