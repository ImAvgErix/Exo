using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Executes plans with clean-slate + safety filter + structured results.</summary>
public sealed class ExoOptimizerService
{
    private readonly ExoToolRegistry _registry;
    private readonly ExoStateManager _state;

    public ExoOptimizerService(ExoToolRegistry registry, ExoStateManager state)
    {
        _registry = registry;
        _state = state;
    }

    public async Task<IReadOnlyList<ExoToolResult>> ExecuteAsync(
        IEnumerable<ExoToolAction> actions,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var filtered = ExoActionSafety.FilterActions(actions, out var rejected);
        foreach (var r in rejected)
            progress?.Report("safety: rejected " + r);

        var results = new List<ExoToolResult>();
        foreach (var action in filtered)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("clean-slate: " + action.ToolId);

            var tool = _registry.Get(action.ToolId);
            if (tool is null)
            {
                results.Add(new ExoToolResult
                {
                    ToolId = action.ToolId,
                    Success = false,
                    Status = "skip",
                    Message = "unknown tool"
                });
                continue;
            }

            try
            {
                var preflight = await RunCleanSlatePreflightAsync(action, ct).ConfigureAwait(false);
                if (!preflight.Ok)
                {
                    results.Add(new ExoToolResult
                    {
                        ToolId = action.ToolId,
                        Success = false,
                        Status = "blocked",
                        Message = preflight.Message,
                        Before = preflight.SnapshotPath
                    });
                    progress?.Report($"blocked: {action.ToolId} — {preflight.Message}");
                    continue;
                }

                progress?.Report("apply: " + action.ToolId);
                var result = await tool.ExecuteAsync(action.Params, ct).ConfigureAwait(false);
                result.ToolId = action.ToolId;
                if (!string.IsNullOrEmpty(preflight.SnapshotPath))
                    result.Before ??= preflight.SnapshotPath;
                results.Add(result);
                progress?.Report($"{result.Status}: {action.ToolId} — {result.Message}");
            }
            catch (Exception ex)
            {
                results.Add(new ExoToolResult
                {
                    ToolId = action.ToolId,
                    Success = false,
                    Status = "error",
                    Message = ex.Message
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Lightweight per-tool preflight: always stamp a snapshot marker; browser tools
    /// refuse any session-store path via <see cref="ExoActionSafety.TouchesSessionStore"/>.
    /// </summary>
    private async Task<(bool Ok, string Message, string? SnapshotPath)> RunCleanSlatePreflightAsync(
        ExoToolAction action,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_state.SnapshotsDir);
        var stamp = Path.Combine(
            _state.SnapshotsDir,
            $"{Sanitize(action.ToolId)}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");

        var payload =
            $"{{\"toolId\":{JsonEscape(action.ToolId)},\"reason\":{JsonEscape(action.Reason ?? "")}," +
            $"\"utc\":{JsonEscape(DateTime.UtcNow.ToString("o"))},\"paramCount\":{action.Params.Count}}}";
        await File.WriteAllTextAsync(stamp, payload, ct).ConfigureAwait(false);

        if (ExoActionSafety.IsBrowserTool(action.ToolId))
        {
            foreach (var kv in action.Params)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (ExoActionSafety.TouchesSessionStore(kv.Value))
                {
                    return (
                        false,
                        $"clean-slate refused session-store path ({kv.Key})",
                        stamp);
                }
            }
        }

        return (true, "snapshot recorded", stamp);
    }

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id.Replace('.', '_');
    }

    private static string JsonEscape(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
}
