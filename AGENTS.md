# Exo agent workflow

## Product direction

Exo is a no-compromise Windows performance and debloat tool. Aggressive memory trimming, background-process reduction, priority tuning, cache cleanup, telemetry removal, and latency/FPS optimization are intentional core features. Do not quietly weaken them into conservative defaults.

Aggressive must still be deterministic: scope actions to the selected application or hardware, report partial failures honestly, avoid invented registry settings, preserve data needed to prevent corruption, and keep Discord/Steam repair paths working. Never describe NVIDIA Reset as rollback: it only clears Exo status, while NVIDIA recovery remains manual through NVIDIA settings or a driver reinstall.

## Shell UI (current)

- **Fixed frame** 1180×760, no maximize / free resize
- **Dark = AMOLED** pure black page (`#000000`) + lifted cards (`#0C0C0C`)
- **Settings** = gear flyout under the gear (not modal overlay, not side rail)
- **Motion** = short XAML Storyboards only; never Composition Opacity = 0 (blanks UI); no spring bounce on content
- **Hover feedback** = highlight wash / accent ring — avoid scale transforms on content with logos (softens bitmaps)
- **Feature tiles** = thin status rail + Applied/Not applied (live detect)
- **Home** = hero tagline only (no support subtitle); cached dashboard so Back does not re-stagger
- **Version** = `VERSION` file and `Exo/Exo.csproj` must match; UiPeak.Smoke gates both

## Team structure

For substantial audits, refactors, optimizer work, or releases:

1. Keep the root agent as coordinator, integrator, verifier, and publisher.
2. Delegate implementation to parallel executors with non-overlapping ownership when useful.
3. Give each executor exact files, acceptance criteria, tests, and prohibited actions.
4. Executors must not commit, push, merge, publish releases, or run optimizer Apply/Repair actions unless explicitly authorized.
5. The coordinator reviews the combined diff and runs full builds, script/data validation, package checks, publish smoke tests, and appropriate UI QA.

Use concise prompts and targeted diffs. Do not have the coordinator redo completed executor analysis.

## Ship checklist

1. `dotnet run --project tools/UiPeak.Smoke -c Release`
2. `.\tools\Test-Repository.ps1`
3. Peak smokes as needed (Network / Discord / Steam / NVIDIA)
4. `.\Publish-Exo.ps1` then install to `%LocalAppData%\Exo\app` for local QA
5. `.\Release-Exo.ps1` only when intentionally publishing a GitHub release
