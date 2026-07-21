using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Living Exo AI agent — Grok deep brain; local gate + hands.</summary>
public sealed class ExoAIAgent
{
    private readonly ExoStateManager _state;
    private readonly ExoSystemInventory _inventory;
    private readonly ExoToolRegistry _registry;
    private readonly ExoOptimizerService _optimizer;
    private readonly ExoGrokClient _grok;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _exoVersion;

    public ExoAIAgent(
        ExoStateManager state,
        ExoSystemInventory inventory,
        ExoToolRegistry registry,
        ExoOptimizerService optimizer,
        ExoGrokClient grok,
        Func<string?> apiKey,
        Func<string> exoVersion)
    {
        _state = state;
        _inventory = inventory;
        _registry = registry;
        _optimizer = optimizer;
        _grok = grok;
        _apiKey = apiKey;
        _exoVersion = exoVersion;
    }

    public ExoOptimalGateStatus GetStatus()
    {
        var current = _inventory.Capture(_exoVersion());
        return _state.CompareFast(current);
    }

    public async Task<ExoAgentRunResult> RunAsync(
        bool force = false,
        bool requireGrok = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("inventory: capturing system state");
        var current = _inventory.Capture(_exoVersion());
        var gate = _state.CompareFast(current);

        if (!force && gate.IsOptimal)
        {
            return new ExoAgentRunResult
            {
                Success = true,
                SkippedOptimal = true,
                Message = gate.Message
            };
        }

        progress?.Report(gate.HasOptimal ? "drift detected — deep analysis" : "first run — deep analysis");

        ExoAnalysisResult analysis;
        var key = _apiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                progress?.Report("grok: deep whole-PC analysis");
                var prior = _state.LoadOptimal();
                var payload = _state.BuildIncrementalPayload(current, prior);
                analysis = await _grok.AnalyzeAsync(key!, payload, _registry.CatalogIds(), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!requireGrok)
            {
                progress?.Report("grok unavailable — local maximize: " + ex.Message);
                analysis = ExoGrokClient.LocalMaximizePlan(current);
            }
        }
        else
        {
            if (requireGrok)
            {
                return new ExoAgentRunResult
                {
                    Success = false,
                    Message = "Add an xAI API key in Settings to unlock deep Grok analysis."
                };
            }

            progress?.Report("local: maximize plan (add API key for Grok)");
            analysis = ExoGrokClient.LocalMaximizePlan(current);
        }

        if (analysis.Actions.Count == 0)
            analysis = ExoGrokClient.LocalMaximizePlan(current);

        progress?.Report($"execute: {analysis.Actions.Count} actions");
        var results = await _optimizer.ExecuteAsync(analysis.Actions, progress, ct).ConfigureAwait(false);

        var ok = results.Count == 0 || results.Any(r => r.Success);
        if (ok)
        {
            progress?.Report("memory: saving optimal state");
            // Re-capture after apply so digest matches the post-optimize machine.
            var after = _inventory.Capture(_exoVersion());
            _state.SaveOptimal(new ExoOptimalState
            {
                ExoVersion = _exoVersion(),
                StateDigest = after.Digest,
                AnalysisSummary = analysis.Analysis,
                AppliedActionIds = results.Where(r => r.Success).Select(r => r.ToolId).ToList(),
                Snapshot = after,
                Metrics =
                {
                    ["actionCount"] = results.Count.ToString(),
                    ["source"] = analysis.Source
                }
            });
        }

        return new ExoAgentRunResult
        {
            Success = ok,
            Message = ok
                ? "Deep optimization complete — optimal state saved"
                : "Optimization finished with failures — see results",
            Analysis = analysis,
            Results = results.ToList()
        };
    }
}
