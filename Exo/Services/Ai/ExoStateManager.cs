using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exo.Helpers;
using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Optimal-state memory under %LocalAppData%\Exo\ai\.</summary>
public sealed class ExoStateManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string AiRoot
    {
        get
        {
            var dir = Path.Combine(PathHelper.AppDataDir, "ai");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string OptimalPath => Path.Combine(AiRoot, "optimal-state.json");
    public string SnapshotsDir
    {
        get
        {
            var dir = Path.Combine(AiRoot, "snapshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public bool HasOptimal => File.Exists(OptimalPath);

    public ExoOptimalState? LoadOptimal()
    {
        try
        {
            if (!File.Exists(OptimalPath)) return null;
            var json = File.ReadAllText(OptimalPath);
            return JsonSerializer.Deserialize<ExoOptimalState>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public void SaveOptimal(ExoOptimalState state)
    {
        state.SavedUtc = DateTime.UtcNow.ToString("o");
        var json = JsonSerializer.Serialize(state, JsonOpts);
        File.WriteAllText(OptimalPath, json);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(Path.Combine(SnapshotsDir, $"optimal-{stamp}.json"), json);
    }

    public static string ComputeDigest(ExoSystemState state)
    {
        var payload = string.Join("|",
            string.Join(";", state.Hardware.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)),
            string.Join(";", state.Os.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)),
            string.Join(",", state.InstalledApps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", state.Domains.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16];
    }

    public ExoOptimalGateStatus CompareFast(ExoSystemState current)
    {
        var optimal = LoadOptimal();
        if (optimal is null)
        {
            return new ExoOptimalGateStatus
            {
                HasOptimal = false,
                IsOptimal = false,
                Message = "No optimal state yet — run deep Exo AI optimization."
            };
        }

        var currentDigest = string.IsNullOrEmpty(current.Digest)
            ? ComputeDigest(current)
            : current.Digest;
        current.Digest = currentDigest;

        if (string.Equals(currentDigest, optimal.StateDigest, StringComparison.OrdinalIgnoreCase))
        {
            return new ExoOptimalGateStatus
            {
                HasOptimal = true,
                IsOptimal = true,
                Message = "System is at optimal state"
            };
        }

        var drifts = CompareDetailed(current, optimal);
        return new ExoOptimalGateStatus
        {
            HasOptimal = true,
            IsOptimal = drifts.Count == 0,
            Message = drifts.Count == 0
                ? "System is at optimal state"
                : $"{drifts.Count} drift(s) from optimal — re-optimization available",
            Drifts = drifts
        };
    }

    public List<ExoStateDrift> CompareDetailed(ExoSystemState current, ExoOptimalState optimal)
    {
        var drifts = new List<ExoStateDrift>();
        var snap = optimal.Snapshot;
        if (snap is null)
        {
            drifts.Add(new ExoStateDrift
            {
                Domain = "memory",
                Key = "digest",
                Expected = optimal.StateDigest,
                Actual = current.Digest,
                Severity = "warn"
            });
            return drifts;
        }

        foreach (var kv in snap.Hardware)
        {
            current.Hardware.TryGetValue(kv.Key, out var actual);
            if (!string.Equals(kv.Value, actual, StringComparison.OrdinalIgnoreCase))
            {
                drifts.Add(new ExoStateDrift
                {
                    Domain = "hardware",
                    Key = kv.Key,
                    Expected = kv.Value,
                    Actual = actual,
                    Severity = "info"
                });
            }
        }

        foreach (var kv in snap.Os)
        {
            current.Os.TryGetValue(kv.Key, out var actual);
            if (!string.Equals(kv.Value, actual, StringComparison.OrdinalIgnoreCase))
            {
                drifts.Add(new ExoStateDrift
                {
                    Domain = "os",
                    Key = kv.Key,
                    Expected = kv.Value,
                    Actual = actual,
                    Severity = "warn"
                });
            }
        }

        var missingApps = snap.InstalledApps
            .Except(current.InstalledApps, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var app in missingApps)
        {
            drifts.Add(new ExoStateDrift
            {
                Domain = "apps",
                Key = app,
                Expected = "installed",
                Actual = "missing",
                Severity = "warn"
            });
        }

        if (!string.Equals(current.Digest, optimal.StateDigest, StringComparison.OrdinalIgnoreCase)
            && drifts.Count == 0)
        {
            drifts.Add(new ExoStateDrift
            {
                Domain = "digest",
                Key = "state",
                Expected = optimal.StateDigest,
                Actual = current.Digest,
                Severity = "info"
            });
        }

        return drifts;
    }

    /// <summary>Build a delta pack for incremental Grok calls.</summary>
    public object BuildIncrementalPayload(ExoSystemState current, ExoOptimalState? prior)
    {
        if (prior?.Snapshot is null)
        {
            return new { mode = "full", state = current };
        }

        var drifts = CompareDetailed(current, prior);
        return new
        {
            mode = "incremental",
            priorAnalysis = prior.AnalysisSummary,
            priorDigest = prior.StateDigest,
            currentDigest = current.Digest,
            drifts,
            changedDomains = drifts.Select(d => d.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }
}
