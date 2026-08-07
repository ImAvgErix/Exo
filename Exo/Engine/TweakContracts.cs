namespace Exo.Engine;

public enum TweakRisk
{
    Safe,
    Caution,
    High,
}

public enum Reversibility
{
    FullyReversible,
    BestEffort,
    Irreversible,
}

public enum RestartRequirement
{
    None,
    Application,
    SignOut,
    System,
}

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Skipped,
    Refused,
}

public abstract record TweakDefinition(
    string Id,
    string Title,
    string Description,
    TweakRisk Risk,
    Reversibility Reversibility,
    RestartRequirement RestartRequirement,
    Type ValueType);

public sealed record TweakDefinition<TValue>(
    string Id,
    string Title,
    string Description,
    TweakRisk Risk,
    Reversibility Reversibility,
    RestartRequirement RestartRequirement,
    TValue DefaultDesiredValue)
    : TweakDefinition(Id, Title, Description, Risk, Reversibility, RestartRequirement, typeof(TValue));

public sealed record TweakState<TValue>(
    TValue CurrentValue,
    TValue DesiredValue,
    bool IsApplicable = true,
    string? Detail = null);

public sealed record TweakPlanStep(string Id, string Description, bool IsMutating);

public sealed record TweakPlan(
    string TweakId,
    string Explanation,
    IReadOnlyList<TweakPlanStep> Steps,
    RestartRequirement RestartRequirement);

public sealed record TweakStepResult(
    string StepId,
    OperationOutcome Outcome,
    string? Message = null);

public sealed record OperationResult(
    OperationOutcome Outcome,
    IReadOnlyList<TweakStepResult> Steps,
    string? Message = null)
{
    public bool IsSuccess => Outcome == OperationOutcome.Succeeded;
}

/// <summary>
/// Outcome of a post-apply verification pass: the live state read independently of
/// apply markers, plus a human-readable verdict.
/// </summary>
public sealed record TweakVerifyReport<TValue>(
    OperationOutcome Outcome,
    TweakState<TValue> State,
    string? Message = null)
{
    public bool IsSuccess => Outcome == OperationOutcome.Succeeded;
}

public interface ITweakAdapter
{
    TweakDefinition Definition { get; }

    Type SnapshotType { get; }
}

public interface ITweakAdapter<TValue, TSnapshot> : ITweakAdapter
{
    new TweakDefinition<TValue> Definition { get; }

    ValueTask<TweakState<TValue>> Detect(CancellationToken cancellationToken = default);

    ValueTask<TweakPlan> Plan(
        TweakState<TValue> state,
        CancellationToken cancellationToken = default);

    ValueTask<TSnapshot> Snapshot(
        TweakState<TValue> state,
        CancellationToken cancellationToken = default);

    ValueTask<OperationResult> Apply(
        TweakPlan plan,
        TSnapshot snapshot,
        CancellationToken cancellationToken = default);

    ValueTask<TweakVerifyReport<TValue>> Verify(CancellationToken cancellationToken = default);

    ValueTask<OperationResult> Restore(
        TSnapshot? snapshot,
        CancellationToken cancellationToken = default);

    TweakDefinition ITweakAdapter.Definition => Definition;

    Type ITweakAdapter.SnapshotType => typeof(TSnapshot);
}
