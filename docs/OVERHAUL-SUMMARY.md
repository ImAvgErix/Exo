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

- Manual High Contrast, Narrator, 125/150% DPI, and text-scale passes.
- ARM64 support.
- Safety-kernel, measurement-harness, optimizer-policy, Riot/Epic, package-size,
  documentation-recovery, signing, and final release slices.
