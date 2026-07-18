# Exo overhaul summary

This document records implemented slices from `docs/EXO-MASTER-SPEC-v3.md`.
Planned work is not marked complete until its automated and live gates pass.

## Slice 1 — dark-only UI foundation

### Changed

- Centralized the dark palette, High Contrast mappings, typography, spacing, and
  radii in `Styles/Tokens.*.xaml`.
- Removed color literals from view/control XAML and added a UI gate preventing
  palette drift.
- Kept one dark product theme while allowing Windows High Contrast to own the
  accessibility palette. No Light resources or theme controls were added.
- Replaced the fixed 1180 x 760 presenter with a resizable/maximizable window,
  960 x 600 preferred minimum, work-area-clamped initial size, and centered
  1120px content cap.
- Replaced manual caption spacer/drag elements with the WinUI `TitleBar` control
  and removed dead titlebar/back/logo surface.
- Rebuilt the dashboard around verified module state, live proof, system memory,
  and direct module actions. Removed sub-12px dashboard text.
- Made dashboard modules real bordered card surfaces with full-cell hit targets,
  keyboard names, module rails, live status, and concise evidence.
- Rebuilt Settings as a solid tokenized sheet with one update card, support
  actions, version, shorter motion, and a reduced-motion fast path.
- Standardized shared module type/radii/action hit targets and removed internal
  `CTA`/`Still open`/`aggressive pack` prose from the user-facing advisor.

### Live findings and fixes

- Windows Insider build `26200` exposed `AccessibilitySettings` but threw
  `0x80070490` while registering `HighContrastChanged`. Event registration is now
  optional; `ActualThemeChanged` remains the compatible live fallback.
- The first dashboard revision used a transparent centered button template, so
  cards looked ungrouped despite correct content. The template now stretches,
  uses the shared card fill/stroke, and preserves separate hover wash/ring layers.

### Verified

- Release x64 build: 0 warnings, 0 errors.
- Updated `Ui.Smoke`: all checks pass, including centralized colors, High
  Contrast, removed Light theme, responsive shell, native TitleBar, white Apply,
  and dashboard navigation.
- Published self-contained SFX and installed over v3.5.2.
- Real Windows UI: launch, first render, dashboard, Settings open/dismiss,
  Internet detection layout, NVIDIA detection layout, maximize, restore, and
  1120px large-window content cap.

### Not claimed yet

- Manual High Contrast/Narrator/DPI passes, ARM64, and code signing.

## Slice 2 — safety kernel

- Added early current-directory DLL-search removal without replacing the Windows
  App SDK package graph. A stricter blanket loader policy was rejected by a real
  launch test because it crashed unpackaged WinUI before first frame.
- Added single-instance redirection and verified on the live x64 build that the
  primary stays running while a second launch exits.
- Added privacy-redacted fatal startup diagnostics with phase, OS, architecture,
  and runtime information.
- Added a generated 70-entry compiled manifest for shipped scripts and helpers.
  Elevated shipped actions fail closed on length or SHA-256 mismatch.
- Replaced the temporary VBScript and PowerShell wrapper files with a direct UAC
  request and in-memory encoded bootstrap. The elevated boundary re-hashes the
  script before starting it and has no unverified re-run fallback.
- Migrated Internet benchmark, Apply, and Repair to the shared runner. App-built
  elevated network scripts use an explicit, path-restricted trust policy.
- Added freshness, fail-closed, runner-routing, and bootstrap contract gates.
- Moved elevated logs/exit state to a machine-owned ProgramData transaction
  directory with SID-based ACLs, reparse-point rejection, and read-only access
  for normal users.
- Made C# app-update and portable PowerShell downloads HTTPS-only and fail closed
  when GitHub does not publish a SHA-256 digest.

The protected result boundary, script manifest, download verification, and exact
NVIDIA DRS Repair path are now implemented. Per-user optimizer snapshots remain
readable by the signed-in user by design; privileged success/exit state does not.

## Slices 3–7 — optimizer policy and release

- Internet now preserves multi-gig offloads/autotuning, measures three DNS
  providers, separates idle loss from loaded traffic, and reports router-side
  queueing without claiming Windows repaired it.
- NVIDIA uses a hardware-aware safe policy, explicit G-SYNC choice, verified
  per-game profiles, full DRS backup, and exact DRS restore.
- Steam protects the foreground renderer and applies EcoQoS/low memory priority
  only to background helpers while a game runs. The dashboard distinguishes
  resident working set from private committed memory.
- Riot and Epic ship as real capability-aware modules with reversible Windows
  startup/GPU/CPU policy and hard anti-cheat/game-file boundaries.
- The repository now has a current README, MIT license, contributing guide,
  privacy disclosure, v3.6.0 changelog, and synchronized tweak audit.
