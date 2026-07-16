# Exo agent workflow

## Product direction

Exo is a no-compromise Windows performance and debloat tool. Aggressive memory trimming, background-process reduction, priority tuning, cache cleanup, telemetry removal, and latency/FPS optimization are intentional core features. Do not quietly weaken them into conservative defaults.

Aggressive must still be deterministic: scope actions to the selected application or hardware, report partial failures honestly, avoid invented registry settings, preserve data needed to prevent corruption, and keep Discord/Steam repair paths working. Never describe NVIDIA Reset as rollback: it only clears Exo status, while NVIDIA recovery remains manual through NVIDIA settings or a driver reinstall.

## Shell UI (current — v2.6 Exo Instrument)

- **Fixed frame** 1180×760, no maximize / free resize
- **Dark = AMOLED** pure black + edge-glass fills (hard top specular; WinUI cannot match CSS `backdrop-filter`)
- **Workspace** = full-width **top bar** + content stage that fills the rest of the frame
- **Navigation** = top glass bar (`NavRail`): EXO left · modules centered · Settings right — **not** WinUI `NavigationView`, **not** a left sidebar
- **Settings** = gear flyout under the top-bar gear (acrylic/frosted panel — not modal overlay, not a separate settings page)
- **Home** = four-metric dashboard: **FPS gain · Frame time · RAM reclaimed · Latency** — top-bar EXO control hidden on home (page brand owns it); modules stay in the top bar; no Detect* probes on home; FPS/frame-time stay `—` until capture ships; RAM/latency read LocalAppData; cached so returns do not re-stagger
- **Top bar** = equal end caps (56px); EXO optically centered when shown; Settings mirrored right
- **Modules** = one `ExoModulePlate` filling the stage (header + hairline feature list + action foot)
- **Motion** = short XAML Storyboards only; never Composition Opacity = 0 (blanks UI); no spring bounce on content
- **Hover feedback** = highlight wash / accent ring — avoid scale transforms on content with logos (softens bitmaps)
- **Feature rows** = thin status rail + Applied/Not applied (live detect)
- **Version** = `VERSION` file and `Exo/Exo.csproj` must match; Ui.Smoke gates both
- **Agent preview** = `tools/Exo.UiPreview` for Linux click QA of this layout language — keep out of public README product marketing

## Team structure

For substantial audits, refactors, optimizer work, or releases:

1. Keep the root agent as coordinator, integrator, verifier, and publisher.
2. Delegate implementation to parallel executors with non-overlapping ownership when useful.
3. Give each executor exact files, acceptance criteria, tests, and prohibited actions.
4. Executors must not commit, push, merge, publish releases, or run optimizer Apply/Repair actions unless explicitly authorized.
5. The coordinator reviews the combined diff and runs full builds, script/data validation, package checks, publish smoke tests, and appropriate UI QA.

Use concise prompts and targeted diffs. Do not have the coordinator redo completed executor analysis.

## Ship checklist

1. `pwsh -NoProfile -File ./tools/Test-Linux.ps1` (Linux/cloud) or `dotnet run --project tools/Ui.Smoke -c Release` (Windows)
2. `.\tools\Test-Repository.ps1`
3. Optimizer smokes as needed (Network / Discord / Steam / NVIDIA)
4. `.\Publish-Exo.ps1` then install to `%LocalAppData%\Exo\app` for local QA
5. `.\Release-Exo.ps1` only when intentionally publishing a GitHub release

## Cursor Cloud specific instructions

Exo is a **Windows-only WinUI 3** app. Cloud agents run on **Linux**, so what is verifiable here is limited; the full product, real Apply/Repair, `Publish-Exo.ps1`, and CI parity remain Windows-only.

Toolchain (already present via the VM snapshot; the update script only runs `dotnet restore`): **.NET 8 SDK** at `~/.dotnet` (symlinked to `/usr/local/bin/dotnet`) and **PowerShell 7** as `pwsh`. A `/usr/local/bin/powershell.exe -> pwsh` shim exists because the Discord/Steam/Nvidia smoke harnesses hardcode `FileName = "powershell.exe"`; without that shim they abort at the `*DetectCore.ps1` step on Linux.

What runs on Linux (one command):
- `pwsh -NoProfile -File ./tools/Test-Linux.ps1` — repository integrity + Network / Steam / Nvidia / Discord / Ui smokes. Must pass with `failed=0`.

UI click-testing on Linux (mock shell only — not the WinUI app):
- `cd tools/Exo.UiPreview && npm install && npx playwright install chromium && npm run preview:click`
- Dev server: `npm run dev` then open `http://127.0.0.1:5173`
- This is a **React/Vite preview** of the v2.5 rail UI for layout/nav QA. Real Apply/Repair remains Windows-only.

Also individually:
- `pwsh -NoProfile -File ./tools/Test-Repository.ps1`
- `dotnet run --project tools/Network.Smoke|Steam.Smoke|Nvidia.Smoke|Discord.Smoke|Ui.Smoke -c Release`

`DiscordLogic.IsStableDiscordPathText` normalizes Windows-style Discord roots with backslash compare so the Discord smoke passes on Linux too (product paths remain Windows install paths).

Windows-only here (do not treat failures as regressions):
- Main app build `dotnet build Exo.sln -c Release -p:Platform=x64` — restore succeeds, but the WinUI XAML compiler (`XamlCompiler.exe`) is a Windows binary → "Exec format error". **Cannot build or run the GUI on Linux.**
- Ui.Smoke logo *ink* measurement (`System.Drawing.Common`) — skipped on Linux; logo *file presence* is still asserted.
- `dotnet format Exo.sln --verify-no-changes` and the SFX `csc.exe` compile step depend on the Windows build/toolchain.
