using Exo.Models.Ai;

namespace Exo.Services.Ai;

public interface IExoTool
{
    string Id { get; }
    ExoToolCategory Category { get; }
    string Description { get; }
    bool RequiresCleanSlate { get; }
    Task<ExoToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

/// <summary>Typed registry of all agent-executable actions + conflict-aware catalog.</summary>
public sealed class ExoToolRegistry
{
    private readonly Dictionary<string, IExoTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ExoToolRegistry()
    {
        RegisterBuiltins();
    }

    public void Register(IExoTool tool) => _tools[tool.Id] = tool;

    public IExoTool? Get(string id) =>
        _tools.TryGetValue(id, out var t) ? t : null;

    public IReadOnlyCollection<IExoTool> All => _tools.Values;

    public IEnumerable<IExoTool> ByCategory(ExoToolCategory category) =>
        _tools.Values.Where(t => t.Category == category);

    public IReadOnlyList<string> CatalogIds() =>
        _tools.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Replace or register an executable tool (used by AppServices to bind live hands).</summary>
    public void Rebind(
        string id,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ExoToolResult>> exec,
        ExoToolCategory? category = null,
        string? description = null)
    {
        var existing = Get(id);
        Register(new DelegateTool(
            id,
            category ?? existing?.Category ?? ExoToolCategory.OsCore,
            description ?? existing?.Description ?? id,
            exec));
    }

    private void RegisterBuiltins()
    {
        // Module wrappers
        Register(new DelegateTool("module.internet.apply", ExoToolCategory.Networking,
            "Clean-slate then apply Internet Golden Path", ModuleStub));
        Register(new DelegateTool("module.discord.apply", ExoToolCategory.Apps,
            "Install Discord if missing; clean-slate Apply", ModuleStub));
        Register(new DelegateTool("module.steam.apply", ExoToolCategory.Apps,
            "Install Steam if missing; clean-slate Apply", ModuleStub));
        Register(new DelegateTool("module.nvidia.apply", ExoToolCategory.Gpu,
            "NVIDIA driver/DRS maximize", ModuleStub));
        Register(new DelegateTool("module.windows.apply", ExoToolCategory.OsCore,
            "Windows host gaming + AI quiet", ModuleStub));
        Register(new DelegateTool("module.brave.apply", ExoToolCategory.Browser,
            "Install Brave if missing; session-safe Apply", ModuleStub));
        Register(new DelegateTool("module.riot.apply", ExoToolCategory.Apps,
            "Install Riot Client if missing; Apply", ModuleStub));
        Register(new DelegateTool("module.epic.apply", ExoToolCategory.Apps,
            "Install Epic Launcher if missing; Apply", ModuleStub));

        // Host OS / power / AI kill
        Register(new DelegateTool("hostOs.maximize", ExoToolCategory.HostOs,
            "Coordinated Exo Host OS maximize pass", ModuleStub));
        Register(new DelegateTool("power.exoCompetitive", ExoToolCategory.PowerScheduler,
            "Apply deep Exo Competitive Intel/AMD/hybrid plan", ModuleStub));
        Register(new DelegateTool("windows.aiPurge", ExoToolCategory.OsCore,
            "Purge Copilot/Recall/Widgets/AI packages+tasks+policies", ModuleStub));
        Register(new DelegateTool("windows.backgroundQuiet", ExoToolCategory.OsCore,
            "Quiet useless background tasks/services", ModuleStub));

        // Brave-only
        Register(new DelegateTool("browser.braveOnly", ExoToolCategory.Browser,
            "Install Brave; terminate/remove other browsers; set default; session-safe", ModuleStub));

        // Upscaler
        Register(new DelegateTool("upscaler.maximizeSupportedGames", ExoToolCategory.Upscaler,
            "Swap DLSS/FSR/XeSS to newest (risk ack required)", ModuleStub));

        // Companions
        Register(new DelegateTool("companion.snip.install", ExoToolCategory.Companion, "Exo Snip", ModuleStub));
        Register(new DelegateTool("companion.notepad.install", ExoToolCategory.Companion, "Exo Notepad", ModuleStub));
        Register(new DelegateTool("companion.photos.install", ExoToolCategory.Companion, "Exo Photos", ModuleStub));
        Register(new DelegateTool("companion.taskManager.install", ExoToolCategory.Companion, "Exo Task Manager", ModuleStub));

        // GPU control
        Register(new DelegateTool("gpu.control.maximize", ExoToolCategory.Gpu,
            "Exo GPU Control maximize (NV/AMD/Intel)", ModuleStub));
        Register(new DelegateTool("gpu.nvidia.reflex", ExoToolCategory.Gpu, "Enable NVIDIA Reflex", ModuleStub));
        Register(new DelegateTool("gpu.amd.antiLag", ExoToolCategory.Gpu, "Enable AMD Anti-Lag", ModuleStub));

        // Primitives
        Register(new DelegateTool("registry.setSafe", ExoToolCategory.Registry, "Set targeted safe registry value", ModuleStub));
        Register(new DelegateTool("service.setStartup", ExoToolCategory.Services, "Set service startup type (safe set)", ModuleStub));
        Register(new DelegateTool("files.junkCleanup", ExoToolCategory.FileSystem, "Safe junk/cache cleanup (no auth stores)", ModuleStub));
        Register(new DelegateTool("display.maxRefresh", ExoToolCategory.Display, "Set max refresh rate", ModuleStub));
        Register(new DelegateTool("input.rawMouse", ExoToolCategory.Input, "Disable mouse accel / raw feel", ModuleStub));
        Register(new DelegateTool("audio.exclusive48k", ExoToolCategory.Audio, "Exclusive mode + 48k where safe", ModuleStub));
        Register(new DelegateTool("storage.trimWeekly", ExoToolCategory.Storage, "Ensure weekly ReTrim", ModuleStub));
        Register(new DelegateTool("automation.ui.settings", ExoToolCategory.Automation, "Drive Settings via ExoPcControl", ModuleStub));
        Register(new DelegateTool("firmware.uefiInventory", ExoToolCategory.Firmware, "Read-only UEFI/SMBIOS inventory", ModuleStub));

        // Expansion pack AA+ / BB+ / CC+ / DD+ high-leverage surfaces
        Register(new DelegateTool("search.everything", ExoToolCategory.Companion, "Everything Search companion", ModuleStub));
        Register(new DelegateTool("shell.shellExAudit", ExoToolCategory.OsCore, "Shell extension bloat audit/quiet", ModuleStub));
        Register(new DelegateTool("print.spoolerGate", ExoToolCategory.Services, "Gate spooler when no printers", ModuleStub));
        Register(new DelegateTool("shader.cacheRelocate", ExoToolCategory.Storage, "Relocate shader caches to fast NVMe", ModuleStub));
        Register(new DelegateTool("process.ecoQosLaunchers", ExoToolCategory.Apps, "EcoQoS launchers when game FG", ModuleStub));
        Register(new DelegateTool("display.hagsMpoVrrMatrix", ExoToolCategory.Display, "Apply HAGS×MPO×VRR coherent preset", ModuleStub));
        Register(new DelegateTool("devdrive.ensure", ExoToolCategory.Storage, "Ensure Dev Drive for engine caches", ModuleStub));
        Register(new DelegateTool("upscaler.autoSrReport", ExoToolCategory.Upscaler, "Windows Auto SR / DirectSR report", ModuleStub));
        Register(new DelegateTool("obs.broadcastQuiet", ExoToolCategory.Apps, "OBS/Broadcast stack quiet when idle", ModuleStub));
        Register(new DelegateTool("gamepass.minimal", ExoToolCategory.Apps, "Game Pass / Xbox app minimal footprint", ModuleStub));
        Register(new DelegateTool("launcher.multiQuiet", ExoToolCategory.Apps, "Multi-launcher quiet (Steam/Epic/Riot/Battle.net)", ModuleStub));
        Register(new DelegateTool("handheld.oemTune", ExoToolCategory.OsCore, "Handheld OEM tune when detected", ModuleStub));
        Register(new DelegateTool("proof.presentMon", ExoToolCategory.Apps, "PresentMon proof capture hook", ModuleStub));
        Register(new DelegateTool("ownership.dryRun", ExoToolCategory.OsCore, "Dry-run Windows ownership matrix", ModuleStub));
        Register(new DelegateTool("memory.softReclaim", ExoToolCategory.OsCore, "Soft working-set reclaim (non-CEF)", ModuleStub));
        Register(new DelegateTool("timer.bcdGated", ExoToolCategory.OsCore, "Expert timer/BCD lane (gated UX only)", ModuleStub));
        Register(new DelegateTool("ddc.brightness", ExoToolCategory.Display, "DDC/CI brightness companion opt-in", ModuleStub));
        Register(new DelegateTool("eartrumpet.install", ExoToolCategory.Companion, "EarTrumpet volume companion", ModuleStub));
        Register(new DelegateTool("nvidia.hyprRx", ExoToolCategory.Gpu, "AMD HYPR-RX / NV Ansel / FrameView niches", ModuleStub));
        Register(new DelegateTool("arm.prismReport", ExoToolCategory.OsCore, "ARM/Prism compatibility report", ModuleStub));
    }

    private static Task<ExoToolResult> ModuleStub(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        // Wired to live executors by BindExecutors / Host OS on Windows.
        // Linux smoke validates registry + clean-slate contract only.
        return Task.FromResult(new ExoToolResult
        {
            Success = true,
            Status = "ok",
            Message = "registered (executor bound at runtime)"
        });
    }

    private sealed class DelegateTool : IExoTool
    {
        private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ExoToolResult>> _exec;

        public DelegateTool(
            string id,
            ExoToolCategory category,
            string description,
            Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ExoToolResult>> exec)
        {
            Id = id;
            Category = category;
            Description = description;
            _exec = exec;
        }

        public string Id { get; }
        public ExoToolCategory Category { get; }
        public string Description { get; }
        public bool RequiresCleanSlate => true;

        public Task<ExoToolResult> ExecuteAsync(
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken ct) =>
            _exec(parameters, ct);
    }
}
