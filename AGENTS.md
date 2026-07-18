# Exo agent workflow

## Product direction

Exo is a no-compromise Windows performance and debloat tool. **Dual-scope:** the same bar applies to **Exo itself** and to **every target** it optimizes (Discord, Steam, Internet, NVIDIA, Riot, Epic, …). Vendor apps are treated as bloated by default — cut RAM, background CPU, autostart, telemetry, and clutter. Do not quietly weaken policies into conservative “leave the host alone” defaults.

**Goals (app + targets):** performance/speed, less RAM / soft-reclaimed idle pages, debloat, privacy, black/uncluttered UI, reversible Apply/Repair.

**Hard stops only:**
1. Do not brick the PC or network (snapshot + canary + Repair; never permanently kill Wi-Fi).
2. Do not ban the user (never inject/kill/trim **game anti-cheat** or protected game shipping processes). Launchers and chat clients are fair game.
3. No folklore (no invented FPS registry, no fake claims).
4. Never `EmptyWorkingSet` thrash **Steam CEF** (`steamwebhelper`) — it freezes the library UI. Soft reclaim (`SetProcessWorkingSetSize(-1,-1)`) on **non-foreground** CEF/helpers is allowed.

**Lifecycle helpers (authorized):** scoped companions that lower RAM/CPU for a target are OK when they are (a) installed by Apply, (b) reversible by Repair, (c) limited to that target’s processes (e.g. Steam memory guard while Steam runs; Riot/Epic yield on **launcher** processes only). Prefer attaching to the target’s launch path when possible. Do **not** install anonymous always-on malware-style services. Disabling *vendor* junk autostart (Steam/Discord/NVIDIA App) is fine.

Aggressive must still be deterministic: scope actions to the selected application or hardware, make Apply *work* (retry hard paths), preserve data needed to prevent corruption, and keep Discord/Steam/Internet/NVIDIA Repair paths working. Never describe NVIDIA Reset as full driver rollback: it clears Exo status / restores DRS snapshot when present; vendor recovery may still need NVIDIA tools.

## Shell UI (current overhaul contract)

- **Responsive frame** opens near 1180×760, resizes/maximizes, preferred minimum 960×600, clamps to the active work area, and centers content up to 1120px
- **Dark-only** pure black canvas + crisp opaque lifted surfaces; Windows High Contrast remains an accessibility mode, not a second product theme
- **Workspace** = full-width **top bar** + content stage that fills the rest of the frame
- **Navigation** = top glass bar (`NavRail`): EXO left · modules centered · Settings right — **not** WinUI `NavigationView`, **not** a left sidebar
- **Settings** = solid dark tokenized flyout under the top-bar gear—not a modal, separate page, or unique transparent material
- **Home** = verified optimizer state + live proof + system memory; no invented FPS/frame-time claims and no optimizer Detect* script probes on home; local state/system counters are cached so returns do not re-stagger
- **Top bar** = liquid-glass **circles** floating on pure black (no bar plate): hairline rim (~0.5px feel), rim-lit gradient + dark center, soft shadow, hover = scale + sibling fade + label pill (preview) / wash (WinUI); equal 56px end caps; EXO hidden on home
- **Modules** = one `ExoModulePlate` filling the stage (header + hairline feature list + action foot)
- **Motion** = short XAML Storyboards only; **never write hand-off composition visuals** (`ElementCompositionPreview` `Visual.Offset`/`Scale`/`Opacity`) — it detaches elements from XAML layout (everything piles at the origin) and pre-first-frame pokes crash real GPUs with `0xC000027B` (v2.6.0 launch regression); no spring bounce on content
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
