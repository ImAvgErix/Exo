# Contributing to Exo

Exo is a brain. The whole app is one monochrome thinking orb that reads the PC,
asks what to optimize, and applies verified, reversible tuning. Two layers, one
contract:

- **The face** — a React/WebView2 orb. The orb (`ui/src/components/BrainOrb.tsx`)
  and the conversation (`ui/src/pages/OrbApp.tsx`) are the entire interface. It
  must stay alive (never a frozen logo), honest (never claim "good to go" without
  re-reading the machine), and consent-first (nothing applies — updates included —
  without a tap).
- **The engine** — native C# apply/detect/repair services and SHA-256-verified
  PowerShell kits. A new optimizer mutation is complete only when it has
  capability detection, a pristine pre-state snapshot, post-apply verification,
  exact Repair behavior, and a smoke test.

## Ground rules

1. Branch, and describe the user-visible behavior being changed.
2. Do **not** add folklore registry tweaks, anti-cheat changes, game-binary or
   game-file mutation, forced hardware assumptions, telemetry, startup agents, or
   background tasks. Games touches user config files only — never packs, mods, or
   process/binary edits.
3. Preserve existing user settings and unrelated dirty-worktree changes.
4. Keep the orb honest: a verdict comes from a live re-verify, not a cached flag.
   If a tweak can't go further on a given box, say so plainly and stop nagging —
   don't fake completion.
5. Include before/after evidence for UI, startup, package-size, or performance
   claims. Measurements are observations, not universal guarantees.

## Build & check

```powershell
# UI (when changing the orb, its voice, or the conversation)
cd ui; npm ci; npm run build; cd ..     # tsc -b + vite build -> Exo/wwwroot/

dotnet build Exo.sln -c Release -p:Platform=x64
pwsh -File tools/Test-Repository.ps1
dotnet run --project tools/Ui.Smoke -c Release        # structural UI contract
dotnet run --project tools/Contracts.Smoke -c Release # engine contract
```

The committed `Exo/wwwroot/` build must be regenerated and committed alongside any
`ui/` source change — `Ui.Smoke` asserts the shipped bundle matches the source.

## The interface, specifically

- The orb is a hand-rolled Fibonacci dot-sphere on canvas — sharp at any size,
  monochrome, and autonomously animated (drift, self-directed glances, state
  gestures). Reduced-motion (Windows "animation effects off") calms it; it never
  freezes.
- Fonts are self-hosted and offline: **Instrument Serif** is the brain's voice,
  **Bricolage Grotesque** is the UI face. No CDN, no web fonts.
- The optional spoken voice uses the local OS speech engine only (no network) and
  is off until the user turns it on.
- Personality is one place — the `pick([...])` lines in `OrbApp.tsx`. Keep it in
  character, plain-language, and never overpromising.

## Security

Report vulnerabilities through a private GitHub security advisory, not a public
issue. See [SECURITY.md](SECURITY.md).
