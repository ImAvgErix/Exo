# OptiHub agent workflow

## Product direction

OptiHub is a no-compromise Windows performance and debloat tool. Aggressive memory trimming, background-process reduction, priority tuning, cache cleanup, telemetry removal, and latency/FPS optimization are intentional core features. Do not quietly weaken them into conservative defaults.

Aggressive must still be deterministic: scope actions to the selected application or hardware, report partial failures honestly, avoid invented registry settings, preserve data needed to prevent corruption, and keep Discord/Steam repair paths working. Never describe NVIDIA Reset as rollback: it only clears OptiHub status, while NVIDIA recovery remains manual through NVIDIA settings or a driver reinstall.

## Team structure

For substantial audits, refactors, optimizer work, or releases:

1. Keep the root agent as coordinator, integrator, verifier, and publisher.
2. Delegate implementation to three parallel executors with non-overlapping ownership.
3. Prefer Luna or Terra executor models when model selection is exposed. Otherwise use available subagents and do not claim a model was forced.
4. Give each executor exact files, acceptance criteria, tests, and prohibited actions.
5. Executors must not commit, push, merge, publish releases, or run optimizer Apply/Repair actions unless explicitly authorized.
6. The coordinator reviews the combined diff and runs full builds, script/data validation, package checks, publish smoke tests, and appropriate UI QA.

Use concise prompts and targeted diffs. Do not have the coordinator redo completed executor analysis.
