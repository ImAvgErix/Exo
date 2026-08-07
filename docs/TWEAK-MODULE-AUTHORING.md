# Tweak catalog and module authoring

## Admission checklist

Before adding a definition, document:

- the user-visible problem and evidence that the proposed setting affects it;
- exact eligibility detection and the owner of every setting touched;
- current and desired typed values;
- risk, reversibility, and restart requirement;
- a pre-state snapshot schema and exact Restore behavior;
- live post-apply verification and expected partial/failure outcomes.

A tweak missing any item stays out of the catalog. Do not add a speculative or mutating example just to exercise the engine; `foundation.tracer` is deliberately observational.

## Implement the adapter

1. Choose a stable lowercase ID such as `system.game-mode`.
2. Create `TweakDefinition<TValue>` with a clear title, description, risk, reversibility, restart requirement, and desired default.
3. Implement `ITweakAdapter<TValue, TSnapshot>` in lifecycle order.
4. Keep `Plan` pure. Mark every step with `IsMutating`; user consent belongs before any mutating step.
5. Make `Snapshot` durable before the first write. Reapply must preserve the original pre-Exo state.
6. Return a `TweakStepResult` for each attempted step. Refusals are not successes.
7. Make `Verify` read live state independently of apply markers.
8. Make `Restore` refuse safely when no valid snapshot exists rather than inventing defaults.
9. Register once in the composition-root catalog. Duplicate IDs and invalid schemas fail catalog construction.

## Test first

Use a portable smoke fixture and strict RED -> GREEN cycles. Cover at minimum detection, planning, invalid catalog schema, duplicate IDs, snapshot round-trip, failure/refusal outcomes, and Restore. Machine-mutating integration tests run only on disposable elevated Windows runners.

Run:

```powershell
pwsh -File tools/Run-Smokes.ps1
pwsh -File tools/Test-Repository.ps1
```

If a shipped PowerShell kit changes, regenerate `Exo/Security/ShippedScriptManifest.g.cs` and commit it.
