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
                // Clean slate: snapshot stamp before apply
                var stamp = Path.Combine(
                    _state.SnapshotsDir,
                    $"{Sanitize(action.ToolId)}-{DateTime.UtcNow:yyyyMMddHHmmss}.marker");
                await File.WriteAllTextAsync(stamp, action.Reason ?? "", ct).ConfigureAwait(false);

                progress?.Report("apply: " + action.ToolId);
                var result = await tool.ExecuteAsync(action.Params, ct).ConfigureAwait(false);
                result.ToolId = action.ToolId;
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

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id.Replace('.', '_');
    }
}
