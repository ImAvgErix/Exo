using Exo.Engine;

var failures = 0;

void Expect(string name, bool condition, string detail = "")
{
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
        return;
    }

    failures++;
    Console.WriteLine($"FAIL  {name}" + (string.IsNullOrWhiteSpace(detail) ? "" : $" :: {detail}"));
}

Console.WriteLine("=== Engine.Smoke ===");

var catalog = TweakCatalog.CreateDefault();
var tracer = catalog.Resolve<bool, TracerSnapshot>("foundation.tracer");
var state = await tracer.Detect();
var plan = await tracer.Plan(state);
var snapshot = await tracer.Snapshot(state);
var apply = await tracer.Apply(plan, snapshot);
var report = await tracer.Verify();
var restore = await tracer.Restore(snapshot);

Expect("default catalog exposes the tracer and the full system lever set",
    catalog.Definitions.Count == 1 + SystemLeverCatalog.Levers.Count + PrivacyLeverCatalog.Levers.Count
    && catalog.Definitions.Any(d => d.Id == "foundation.tracer")
    && catalog.Definitions.Any(d => d.Id == "system.game-mode")
    && catalog.Definitions.Any(d => d.Id == "system.hags")
    && catalog.Definitions.Any(d => d.Id == "system.mmcss-net-throttle")
    && catalog.Definitions.Any(d => d.Id == "privacy.advertising-id")
    && catalog.Definitions.Any(d => d.Id == "privacy.telemetry"));
Expect("every system lever resolves as a typed registry tweak",
    SystemLeverCatalog.Levers.All(lever =>
    {
        var adapter = catalog.Resolve<int, RegistryTweakSnapshot>("system." + lever.Id);
        return adapter.Definition.Id == "system." + lever.Id
               && adapter.Definition.ValueType == typeof(int);
    }));
Expect("every privacy lever resolves as a typed registry tweak",
    PrivacyLeverCatalog.Levers.All(lever =>
    {
        var adapter = catalog.Resolve<int, RegistryTweakSnapshot>("privacy." + lever.Id);
        return adapter.Definition.Id == "privacy." + lever.Id
               && adapter.Definition.ValueType == typeof(int);
    }));
Expect("no folklore registry sentinels survive in the system lever catalog",
    SystemLeverCatalog.Levers.All(lever => lever.Value != -1 && lever.Value != 0xFFFFFFFF)
    && SystemLeverCatalog.Levers.Single(l => l.Id == "mmcss-net-throttle").Value == 10);
Expect("no folklore registry sentinels survive in the privacy lever catalog",
    PrivacyLeverCatalog.Levers.All(lever => lever.Value != -1 && lever.Value != 0xFFFFFFFF));
Expect("reboot-required levers are declared as needing a restart",
    SystemLeverCatalog.Levers.Single(l => l.Id == "hags").NeedsReboot
    && catalog.Resolve<int, RegistryTweakSnapshot>("system.hags")
        .Definition.RestartRequirement == RestartRequirement.System
    && PrivacyLeverCatalog.Levers.Single(l => l.Id == "telemetry").NeedsReboot
    && catalog.Resolve<int, RegistryTweakSnapshot>("privacy.telemetry")
        .Definition.RestartRequirement == RestartRequirement.System);
Expect("tracer detect reports a healthy observation without requesting a change",
    state.CurrentValue is true && state.DesiredValue is true);
Expect("tracer is explicitly safe and reversible",
    tracer.Definition.Risk == TweakRisk.Safe
    && tracer.Definition.Reversibility == Reversibility.FullyReversible);
Expect("tracer plan explains an observational operation",
    !string.IsNullOrWhiteSpace(plan.Explanation)
    && plan.Steps.Count == 1
    && plan.Steps.All(step => !step.IsMutating));
Expect("tracer needs no restart", plan.RestartRequirement == RestartRequirement.None);
Expect("tracer snapshot preserves the detected observation", snapshot.ObservedValue == state.CurrentValue);
Expect("tracer apply completes without a mutating step",
    apply.IsSuccess && apply.Steps.Count == 1 && plan.Steps.All(step => !step.IsMutating));
Expect("tracer verify returns a successful typed report",
    report.IsSuccess && report.State.CurrentValue is true && !string.IsNullOrWhiteSpace(report.Message));
Expect("tracer restore safely reports that no machine state was changed",
    restore.Outcome == OperationOutcome.Skipped && restore.Steps.Count == 1);

var mutatingPlan = plan with
{
    Steps = [new TweakPlanStep("unsafe", "Attempt a mutation.", IsMutating: true)],
};
var refused = await tracer.Apply(mutatingPlan, snapshot);
Expect("tracer refuses a mutating plan", refused.Outcome == OperationOutcome.Refused);

var duplicateError = Capture(() => new TweakCatalog([new TracerTweakAdapter(), new TracerTweakAdapter()]));
Expect("catalog rejects duplicate tweak ids with a catalog validation error",
    duplicateError is TweakCatalogValidationException duplicate
    && duplicate.Issues.Any(issue => issue.Contains("foundation.tracer", StringComparison.Ordinal)));

var schemaError = Capture(() => new TweakCatalog([new FixtureAdapter(
    new TweakDefinition<bool>("Bad Id", "", "", TweakRisk.Safe, Reversibility.FullyReversible,
        RestartRequirement.None, false))]));
Expect("catalog rejects invalid definition schema",
    schemaError is TweakCatalogValidationException schema
    && schema.Issues.Count >= 3,
    schemaError?.Message ?? "no error");

ISnapshotStore snapshots = new RecordingSnapshotStore();
var captured = new TweakSnapshot<TracerSnapshot>(
    Guid.NewGuid(), tracer.Definition.Id, DateTimeOffset.UtcNow, await tracer.Snapshot(state));
await snapshots.Save(captured);
var loaded = await snapshots.LoadLatest<TracerSnapshot>(tracer.Definition.Id);
Expect("snapshot store contract preserves a typed snapshot",
    loaded?.SnapshotId == captured.SnapshotId && loaded.Value == captured.Value);

IOperationJournal journal = new RecordingOperationJournal();
var operationId = Guid.NewGuid();
await journal.Append(new OperationJournalEntry(
    operationId, tracer.Definition.Id, OperationKind.Detect, OperationOutcome.Succeeded,
    DateTimeOffset.UtcNow, "Observed only."));
var history = await journal.Read(tracer.Definition.Id);
Expect("operation journal contract records ordered outcomes",
    history.Count == 1 && history[0].OperationId == operationId
    && history[0].Kind == OperationKind.Detect);

var store = new MemoryRegistryStore();
var gameMode = new RegistryTweakAdapter(
    new TweakDefinition<int>(
        "system.game-mode", "Game Mode", "Game Mode on for the foreground game.",
        TweakRisk.Safe, Reversibility.FullyReversible, RestartRequirement.None, 1),
    store, "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled");

var gState = await gameMode.Detect();
Expect("registry tweak detects an unset value as not applied",
    gState.IsApplicable && gState.CurrentValue == 0 && gState.DesiredValue == 1);
var gPlan = await gameMode.Plan(gState);
Expect("registry tweak plans a mutating write when change is needed",
    gPlan.Steps.Count == 1 && gPlan.Steps[0].IsMutating
    && gPlan.Steps[0].Id == "write");
var gSnapshot = await gameMode.Snapshot(gState);
Expect("registry tweak snapshot records that no value existed",
    gSnapshot.PreviouslyExisted == false && gSnapshot.PreviousValue is null);
var gApply = await gameMode.Apply(gPlan, gSnapshot);
Expect("registry tweak apply writes and verifies the value",
    gApply.IsSuccess && store.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 1);
var gVerify = await gameMode.Verify();
Expect("registry tweak verify reports the live value as applied",
    gVerify.IsSuccess && gVerify.State.CurrentValue == 1);
var gRestore = await gameMode.Restore(gSnapshot);
Expect("registry tweak restore deletes a value Exo created",
    gRestore.IsSuccess && store.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled") is null);
Expect("registry tweak restore refuses without a snapshot",
    (await gameMode.Restore(null)).Outcome == OperationOutcome.Refused);

var store2 = new MemoryRegistryStore();
store2.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
var gameMode2 = new RegistryTweakAdapter(
    new TweakDefinition<int>(
        "system.game-mode", "Game Mode", "Game Mode on for the foreground game.",
        TweakRisk.Safe, Reversibility.FullyReversible, RestartRequirement.None, 1),
    store2, "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
var g2State = await gameMode2.Detect();
Expect("registry tweak detects an already-applied value",
    !g2State.IsApplicable && g2State.CurrentValue == 1);
var g2Plan = await gameMode2.Plan(g2State);
Expect("registry tweak already-applied plan is a no-op",
    g2Plan.Steps.Count == 0);
var g2Snapshot = await gameMode2.Snapshot(g2State);
var g2Apply = await gameMode2.Apply(g2Plan, g2Snapshot);
Expect("registry tweak no-op apply is skipped",
    g2Apply.Outcome == OperationOutcome.Skipped);

var store3 = new MemoryRegistryStore();
store3.TrySetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 5);
var gameMode3 = new RegistryTweakAdapter(
    new TweakDefinition<int>(
        "system.game-mode", "Game Mode", "Game Mode on for the foreground game.",
        TweakRisk.Safe, Reversibility.FullyReversible, RestartRequirement.None, 1),
    store3, "HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
var g3State = await gameMode3.Detect();
var g3Snapshot = await gameMode3.Snapshot(g3State);
await gameMode3.Apply(await gameMode3.Plan(g3State), g3Snapshot);
var g3Restore = await gameMode3.Restore(g3Snapshot);
Expect("registry tweak restore returns a pre-existing value unchanged",
    g3Restore.IsSuccess
    && store3.GetDword("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == 5);

var gameProfile = TweakProfileCatalog.CreateDefaultGameProfile(catalog);
Expect("default game profile validates against the real catalog",
    gameProfile.Id == "game.default" && gameProfile.Version == 1
    && gameProfile.Find("system.game-mode")?.DesiredValue == 1);
var bumped = gameProfile.Bump(2);
Expect("profile version bumps are explicit and recorded",
    bumped.Version == 2 && bumped.Id == gameProfile.Id);
var badProfile = Capture(() => TweakProfileCatalog.Validate(
    new TweakProfile("game.bad", "Bad", 1, [new TweakProfileBinding("no.such.tweak", 1)]),
    catalog));
Expect("profile validation rejects unknown tweak ids",
    badProfile is ArgumentException bad
    && bad.Message.Contains("no.such.tweak", StringComparison.Ordinal));
var dupProfile = Capture(() => TweakProfileCatalog.Validate(
    new TweakProfile("game.dup", "Dup", 1,
        [new TweakProfileBinding("system.game-mode", 1), new TweakProfileBinding("system.game-mode", 0)]),
    catalog));
Expect("profile validation rejects duplicate bindings",
    dupProfile is ArgumentException dup && dup.Message.Contains("more than once", StringComparison.Ordinal));
var zeroVersion = Capture(() => TweakProfileCatalog.Validate(
    new TweakProfile("game.zero", "Zero", 0, []),
    catalog));
Expect("profile validation rejects non-positive versions",
    zeroVersion is ArgumentException zv && zv.Message.Contains("positive version", StringComparison.Ordinal));

Console.WriteLine(failures == 0 ? "=== ALL ENGINE TESTS PASS ===" : $"=== {failures} ENGINE TEST(S) FAILED ===");
return failures == 0 ? 0 : 1;

static Exception? Capture(Action action)
{
    try
    {
        action();
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}

sealed class FixtureAdapter(TweakDefinition<bool> definition) : ITweakAdapter<bool, TracerSnapshot>
{
    public TweakDefinition<bool> Definition { get; } = definition;

    public ValueTask<TweakState<bool>> Detect(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakState<bool>(false, Definition.DefaultDesiredValue));

    public ValueTask<TweakPlan> Plan(TweakState<bool> state, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakPlan(Definition.Id, "Fixture plan.", [], Definition.RestartRequirement));

    public ValueTask<TracerSnapshot> Snapshot(TweakState<bool> state, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TracerSnapshot(DateTimeOffset.UtcNow, state.CurrentValue));

    public ValueTask<OperationResult> Apply(TweakPlan plan, TracerSnapshot snapshot, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OperationResult(OperationOutcome.Succeeded, []));

    public ValueTask<TweakVerifyReport<bool>> Verify(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakVerifyReport<bool>(
            OperationOutcome.Succeeded,
            new TweakState<bool>(false, Definition.DefaultDesiredValue)));

    public ValueTask<OperationResult> Restore(TracerSnapshot? snapshot, CancellationToken cancellationToken = default) =>
        snapshot is null
            ? ValueTask.FromResult(new OperationResult(OperationOutcome.Refused, []))
            : ValueTask.FromResult(new OperationResult(OperationOutcome.Succeeded, []));
}

sealed class MemoryRegistryStore : IRegistryValueStore
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
