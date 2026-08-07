namespace Exo.Engine;

/// <summary>
/// Pre-apply registry state: the value that existed (null when the value did not
/// exist), so Restore can put the machine back exactly — including deleting a value
/// Exo created where none existed before.
/// </summary>
public sealed record RegistryTweakSnapshot(int? PreviousValue, bool PreviouslyExisted);

/// <summary>
/// A catalog-backed registry tweak: one DWORD lever with the full
/// Detect → Plan → Snapshot → Apply → Verify → Restore lifecycle. The registry
/// surface is injected so tests can substitute an in-memory store.
/// </summary>
public sealed class RegistryTweakAdapter : ITweakAdapter<int, RegistryTweakSnapshot>
{
    private readonly IRegistryValueStore _store;
    private readonly string _hive;
    private readonly string _path;
    private readonly string _name;

    public TweakDefinition<int> Definition { get; }

    public RegistryTweakAdapter(
        TweakDefinition<int> definition,
        IRegistryValueStore store,
        string hive,
        string path,
        string name)
    {
        Definition = definition;
        _store = store;
        _hive = hive;
        _path = path;
        _name = name;
    }

    private string Location => $"{_hive}\\{_path}\\{_name}";

    public ValueTask<TweakState<int>> Detect(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _store.GetDword(_hive, _path, _name) ?? 0;
        var desired = Definition.DefaultDesiredValue;
        var applied = current == desired;
        return ValueTask.FromResult(new TweakState<int>(
            current,
            desired,
            IsApplicable: !applied,
            Detail: applied
                ? "Already at the desired value."
                : $"Currently {current}, wants {desired}."));
    }

    public ValueTask<TweakPlan> Plan(
        TweakState<int> state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!state.IsApplicable)
        {
            return ValueTask.FromResult(new TweakPlan(
                Definition.Id,
                "No change needed: the value already matches.",
                [],
                Definition.RestartRequirement));
        }

        return ValueTask.FromResult(new TweakPlan(
            Definition.Id,
            $"Sets {Location} to {Definition.DefaultDesiredValue} ({Definition.Description}).",
            [new TweakPlanStep("write", $"Set {Location} to {Definition.DefaultDesiredValue}.", IsMutating: true)],
            Definition.RestartRequirement));
    }

    public ValueTask<RegistryTweakSnapshot> Snapshot(
        TweakState<int> state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _store.GetDword(_hive, _path, _name);
        return ValueTask.FromResult(new RegistryTweakSnapshot(
            current,
            PreviouslyExisted: current is not null));
    }

    public ValueTask<OperationResult> Apply(
        TweakPlan plan,
        RegistryTweakSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Steps.Count == 0)
        {
            return ValueTask.FromResult(new OperationResult(
                OperationOutcome.Skipped,
                [],
                "No change needed."));
        }

        var wrote = _store.TrySetDword(_hive, _path, _name, Definition.DefaultDesiredValue);
        var verified = _store.GetDword(_hive, _path, _name) == Definition.DefaultDesiredValue;
        return ValueTask.FromResult(new OperationResult(
            wrote && verified ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            [new TweakStepResult(
                "write",
                wrote && verified ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                wrote && verified
                    ? $"Set {Location} to {Definition.DefaultDesiredValue}."
                    : $"Could not set {Location}.")],
            wrote && verified
                ? $"Applied: {Definition.Title}."
                : $"Apply failed for {Definition.Title}."));
    }

    public ValueTask<TweakVerifyReport<int>> Verify(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _store.GetDword(_hive, _path, _name) ?? 0;
        var desired = Definition.DefaultDesiredValue;
        var applied = current == desired;
        return ValueTask.FromResult(new TweakVerifyReport<int>(
            applied ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            new TweakState<int>(
                current,
                desired,
                IsApplicable: !applied,
                Detail: applied
                    ? "Verified: value matches."
                    : $"Verify failed: currently {current}, wants {desired}."),
            applied
                ? $"Verified: {Definition.Title} is applied."
                : $"Not applied: {Definition.Title} is at {current}, wants {desired}."));
    }

    public ValueTask<OperationResult> Restore(
        RegistryTweakSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
        {
            return ValueTask.FromResult(new OperationResult(
                OperationOutcome.Refused,
                [],
                "Refused to restore without a snapshot: no known previous value."));
        }

        var restored = snapshot.PreviouslyExisted
            ? snapshot.PreviousValue is { } previous && _store.TrySetDword(_hive, _path, _name, previous)
            : _store.TryDeleteValue(_hive, _path, _name);

        return ValueTask.FromResult(new OperationResult(
            restored ? OperationOutcome.Succeeded : OperationOutcome.Failed,
            [new TweakStepResult(
                "restore",
                restored ? OperationOutcome.Succeeded : OperationOutcome.Failed,
                restored
                    ? snapshot.PreviouslyExisted
                        ? $"Restored {Location} to {snapshot.PreviousValue}."
                        : $"Deleted {Location} (Exo created it)."
                    : $"Could not restore {Location}.")],
            restored
                ? $"Restored: {Definition.Title}."
                : $"Restore failed for {Definition.Title}."));
    }
}
