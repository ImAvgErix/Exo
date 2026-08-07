using System.Text.Json;
using Exo.Helpers;

namespace Exo.Services;

/// <summary>
/// Persists applyReport + applied flags for native modules that used to leave the orb with
/// empty "last apply" detail (System, Spotify, and any future native-only path).
/// Same shape Steam/Brave already write so OptimizerStateService.TryReadApplyReport works.
/// </summary>
internal static class NativeModuleStateWriter
{
    public static void Save(string module, NativeApplyResult result)
    {
        if (string.IsNullOrWhiteSpace(module)) return;
        try
        {
            var id = module.Trim().ToLowerInvariant();
            var path = Path.Combine(PathHelper.AppDataDir, $"{id}-optimizer.json");
            Directory.CreateDirectory(PathHelper.AppDataDir);

            // Final step outcomes after elevation — pending-elev with no failure is not success.
            var steps = result.Steps ?? new List<NativeApplyStep>();
            var hardFail = steps.Any(s =>
                s.Status.Equals("fail", StringComparison.OrdinalIgnoreCase));
            var stillPending = steps.Any(s =>
                s.Status.Equals("pending-elev", StringComparison.OrdinalIgnoreCase));
            var ok = result.Ok && !hardFail && !stillPending;

            var state = new Dictionary<string, object?>
            {
                ["version"] = "native-1.0",
                ["applyStatus"] = ok ? "applied" : hardFail || stillPending ? "incomplete" : "applied",
                ["applied"] = ok,
                ["appliedUtc"] = DateTime.UtcNow.ToString("o"),
                ["path"] = "native-csharp",
                ["message"] = result.Message,
                ["applyReport"] = steps.Select(s => s.ToReportLine()).ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // State is UI detail; a missing file re-prompts or shows empty report, not a crash.
        }
    }
}
