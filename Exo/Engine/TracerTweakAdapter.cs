namespace Exo.Engine;

public sealed record TracerSnapshot(DateTimeOffset CapturedAt, bool ObservedValue);

public sealed class TracerTweakAdapter : ITweakAdapter<bool, TracerSnapshot>
{
    public TweakDefinition<bool> Definition { get; } = new(
        Id: "foundation.tracer",
        Title: "Foundation tracer",
        Description: "Exercises detection and planning without changing the machine.",
        Risk: TweakRisk.Safe,
        Reversibility: Reversibility.FullyReversible,
        RestartRequirement: RestartRequirement.None,
        DefaultDesiredValue: true);

    public ValueTask<TweakState<bool>> Detect(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakState<bool>(
            CurrentValue: true,
            DesiredValue: Definition.DefaultDesiredValue,
            Detail: "The tracer is healthy and requests no change."));

    public ValueTask<TweakPlan> Plan(
        TweakState<bool> state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakPlan(
            Definition.Id,
            "Records an observational engine trace. No setting is read from or written to the machine.",
            [new TweakPlanStep("trace", "Record an observational engine trace.", IsMutating: false)],
            Definition.RestartRequirement));

    public ValueTask<TracerSnapshot> Snapshot(
        TweakState<bool> state,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TracerSnapshot(DateTimeOffset.UtcNow, state.CurrentValue));

    public ValueTask<OperationResult> Apply(
        TweakPlan plan,
        TracerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (plan.Steps.Any(step => step.IsMutating))
        {
            return ValueTask.FromResult(new OperationResult(
                OperationOutcome.Refused,
                [new TweakStepResult(
                    "trace",
                    OperationOutcome.Refused,
                    "The tracer refuses mutating plans: it is observational only.")],
                "The tracer refused a mutating plan."));
        }

        return ValueTask.FromResult(Succeeded("Tracer performed no machine mutation."));
    }

    public ValueTask<TweakVerifyReport<bool>> Verify(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new TweakVerifyReport<bool>(
            OperationOutcome.Succeeded,
            new TweakState<bool>(true, Definition.DefaultDesiredValue),
            "Tracer state verified: no machine state was changed."));

    public ValueTask<OperationResult> Restore(
        TracerSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        if (snapshot is null)
        {
            return ValueTask.FromResult(new OperationResult(
                OperationOutcome.Refused,
                [],
                "Tracer refused to restore without a snapshot."));
        }

        return ValueTask.FromResult(new OperationResult(
            OperationOutcome.Skipped,
            [new TweakStepResult(
                "trace",
                OperationOutcome.Skipped,
                "Tracer had no machine state to restore.")],
            "Tracer restore skipped: no machine state was changed."));
    }

    private static OperationResult Succeeded(string message) => new(
        OperationOutcome.Succeeded,
        [new TweakStepResult("trace", OperationOutcome.Succeeded, message)],
        message);
}
