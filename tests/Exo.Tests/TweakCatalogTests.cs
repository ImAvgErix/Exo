using Exo.Engine;
using Xunit;

namespace Exo.Tests;

/// <summary>
/// Real unit tests for the tweak catalog: lifecycle contracts, validation, and
/// the in-memory registry store. These run on any machine with no UAC and no
/// machine mutation — the same surface Engine.Smoke probes, but as durable
/// xUnit tests instead of a console script.
/// </summary>
public sealed class TweakCatalogTests
{
    private sealed class MemoryStore : IRegistryValueStore
    {
        public Dictionary<(string Hive, string Path, string Name), int> Values { get; } = [];

        public int? GetDword(string hive, string path, string name) =>
            Values.TryGetValue((hive, path, name), out var value) ? value : null;

        public bool TrySetDword(string hive, string path, string name, int value)
        {
            Values[(hive, path, name)] = value;
            return true;
        }

        public bool TryDeleteValue(string hive, string path, string name) =>
            Values.Remove((hive, path, name));
    }

    private static RegistryTweakAdapter GameModeAdapter(IRegistryValueStore store) => new(
        new TweakDefinition<int>(
            "system.game-mode", "Game Mode", "Game Mode on for the foreground game.",
            TweakRisk.Safe, Reversibility.FullyReversible, RestartRequirement.None, 1),
        store, "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled");

    [Fact]
    public void DefaultCatalog_ExposesTracerAndBothLeverSets()
    {
        var catalog = TweakCatalog.CreateDefault();

        Assert.Contains(catalog.Definitions, d => d.Id == "foundation.tracer");
        Assert.Contains(catalog.Definitions, d => d.Id == "system.hags");
        Assert.Contains(catalog.Definitions, d => d.Id == "system.mmcss-net-throttle");
        Assert.Contains(catalog.Definitions, d => d.Id == "privacy.advertising-id");
        Assert.Contains(catalog.Definitions, d => d.Id == "privacy.telemetry");
        Assert.Equal(
            1 + SystemLeverCatalog.Levers.Count + PrivacyLeverCatalog.Levers.Count,
            catalog.Definitions.Count);
    }

    [Fact]
    public void SystemLeverValues_HaveNoFolkloreSentinels()
    {
        Assert.All(SystemLeverCatalog.Levers, lever =>
        {
            Assert.NotEqual(-1, (int)lever.Value);
            Assert.NotEqual(unchecked((int)0xFFFFFFFF), lever.Value);
        });
        Assert.Equal(10, SystemLeverCatalog.Levers.Single(l => l.Id == "mmcss-net-throttle").Value);
    }

    [Fact]
    public void HagsLever_DeclaresSystemRestart()
    {
        var catalog = TweakCatalog.CreateDefault();
        var hags = catalog.Resolve<int, RegistryTweakSnapshot>("system.hags");

        Assert.Equal(RestartRequirement.System, hags.Definition.RestartRequirement);
        Assert.True(SystemLeverCatalog.Levers.Single(l => l.Id == "hags").NeedsReboot);
    }

    [Fact]
    public void DuplicateAdapterIds_FailCatalogConstruction()
    {
        var exception = Assert.Throws<TweakCatalogValidationException>(() =>
            new TweakCatalog([new TracerTweakAdapter(), new TracerTweakAdapter()]));

        Assert.Contains(exception.Issues, i => i.Contains("foundation.tracer", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidDefinitionSchema_FailsCatalogConstruction()
    {
        var adapter = new TweakDefinition<int>(
            "Bad Id", "", "", TweakRisk.Safe, Reversibility.FullyReversible,
            RestartRequirement.None, 0);

        var exception = Assert.Throws<TweakCatalogValidationException>(() =>
            new TweakCatalog([new StubAdapter(adapter)]));

        Assert.True(exception.Issues.Count >= 3);
    }

    [Fact]
    public async Task RegistryTweak_FullLifecycle_UnsetValue()
    {
        var store = new MemoryStore();
        var tweak = GameModeAdapter(store);

        var state = await tweak.Detect();
        Assert.True(state.IsApplicable);
        Assert.Equal(0, state.CurrentValue);

        var plan = await tweak.Plan(state);
        Assert.Single(plan.Steps);
        Assert.True(plan.Steps[0].IsMutating);

        var snapshot = await tweak.Snapshot(state);
        Assert.Null(snapshot.PreviousValue);
        Assert.False(snapshot.PreviouslyExisted);

        var apply = await tweak.Apply(plan, snapshot);
        Assert.True(apply.IsSuccess);
        Assert.Equal(1, store.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled"));

        var verify = await tweak.Verify();
        Assert.True(verify.IsSuccess);
        Assert.Equal(1, verify.State.CurrentValue);

        var restore = await tweak.Restore(snapshot);
        Assert.True(restore.IsSuccess);
        Assert.Null(store.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled"));
    }

    [Fact]
    public async Task RegistryTweak_Restore_RefusesWithoutSnapshot()
    {
        var tweak = GameModeAdapter(new MemoryStore());

        var result = await tweak.Restore(null);

        Assert.Equal(OperationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task RegistryTweak_AlreadyApplied_PlanIsNoOp()
    {
        var store = new MemoryStore();
        store.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
        var tweak = GameModeAdapter(store);

        var state = await tweak.Detect();
        Assert.False(state.IsApplicable);

        var plan = await tweak.Plan(state);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task RegistryTweak_Restore_PutsBackPreExistingValue()
    {
        var store = new MemoryStore();
        store.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 5);
        var tweak = GameModeAdapter(store);

        var state = await tweak.Detect();
        var snapshot = await tweak.Snapshot(state);
        await tweak.Apply(await tweak.Plan(state), snapshot);
        await tweak.Restore(snapshot);

        Assert.Equal(5, store.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled"));
    }

    [Fact]
    public void DefaultGameProfile_ValidatesAgainstRealCatalog()
    {
        var catalog = TweakCatalog.CreateDefault();
        var profile = TweakProfileCatalog.CreateDefaultGameProfile(catalog);

        Assert.Equal("game.default", profile.Id);
        Assert.Equal(1, profile.Version);
        Assert.Equal(1, profile.Find("system.game-mode")?.DesiredValue);
    }

    [Fact]
    public void ProfileValidation_RejectsUnknownTweak()
    {
        var catalog = TweakCatalog.CreateDefault();
        var profile = new TweakProfile("game.bad", "Bad", 1, [new TweakProfileBinding("no.such.tweak", 1)]);

        var exception = Assert.Throws<ArgumentException>(() => TweakProfileCatalog.Validate(profile, catalog));
        Assert.Contains("no.such.tweak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileValidation_RejectsDuplicateBindings()
    {
        var catalog = TweakCatalog.CreateDefault();
        var profile = new TweakProfile("game.dup", "Dup", 1,
            [new TweakProfileBinding("system.game-mode", 1), new TweakProfileBinding("system.game-mode", 0)]);

        var exception = Assert.Throws<ArgumentException>(() => TweakProfileCatalog.Validate(profile, catalog));
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileValidation_RejectsNonPositiveVersion()
    {
        var catalog = TweakCatalog.CreateDefault();
        var profile = new TweakProfile("game.zero", "Zero", 0, []);

        var exception = Assert.Throws<ArgumentException>(() => TweakProfileCatalog.Validate(profile, catalog));
        Assert.Contains("positive version", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubAdapter(TweakDefinition<int> definition) : ITweakAdapter<int, RegistryTweakSnapshot>
    {
        public TweakDefinition<int> Definition { get; } = definition;

        public ValueTask<TweakState<int>> Detect(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TweakState<int>(0, Definition.DefaultDesiredValue));

        public ValueTask<TweakPlan> Plan(TweakState<int> state, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TweakPlan(Definition.Id, "stub", [], Definition.RestartRequirement));

        public ValueTask<RegistryTweakSnapshot> Snapshot(TweakState<int> state, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RegistryTweakSnapshot(null, false));

        public ValueTask<OperationResult> Apply(TweakPlan plan, RegistryTweakSnapshot snapshot, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OperationResult(OperationOutcome.Succeeded, []));

        public ValueTask<TweakVerifyReport<int>> Verify(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TweakVerifyReport<int>(
                OperationOutcome.Succeeded, new TweakState<int>(0, Definition.DefaultDesiredValue)));

        public ValueTask<OperationResult> Restore(RegistryTweakSnapshot? snapshot, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OperationResult(OperationOutcome.Skipped, []));
    }
}
