using Exo.Models.Ai;

namespace Exo.Services.Ai;

/// <summary>Exo Host OS — coordinated Repairable machine-wide maximize.</summary>
public sealed class ExoHostOsService
{
    private readonly ExoToolRegistry _registry;
    private readonly ExoOptimizerService _optimizer;

    public ExoHostOsService(ExoToolRegistry registry, ExoOptimizerService optimizer)
    {
        _registry = registry;
        _optimizer = optimizer;
    }

    public static IReadOnlyList<ExoToolAction> BuildMaximizeActions() =>
    [
        new() { ToolId = "windows.aiPurge", Reason = "AI/background purge" },
        new() { ToolId = "windows.backgroundQuiet", Reason = "Background quiet" },
        new() { ToolId = "power.exoCompetitive", Reason = "Deep power plan" },
        new() { ToolId = "module.windows.apply", Reason = "Windows host stack" },
        new() { ToolId = "module.internet.apply", Reason = "Internet" },
        new() { ToolId = "display.hagsMpoVrrMatrix", Reason = "Display matrix" },
        new() { ToolId = "input.rawMouse", Reason = "Input" },
        new() { ToolId = "audio.exclusive48k", Reason = "Audio" },
        new() { ToolId = "storage.trimWeekly", Reason = "Storage" },
        new() { ToolId = "process.ecoQosLaunchers", Reason = "EcoQoS" },
        new() { ToolId = "shell.shellExAudit", Reason = "ShellEx" },
        new() { ToolId = "print.spoolerGate", Reason = "Spooler gate" }
    ];

    public Task<IReadOnlyList<ExoToolResult>> MaximizeAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("host-os: coordinated maximize");
        return _optimizer.ExecuteAsync(BuildMaximizeActions(), progress, ct);
    }

    public IReadOnlyList<string> CatalogCheck() =>
        BuildMaximizeActions()
            .Select(a => a.ToolId)
            .Where(id => _registry.Get(id) is null)
            .ToList();
}
