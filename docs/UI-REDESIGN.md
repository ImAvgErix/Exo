# Exo UI redesign — "Tab Bar" (Liquid Glass)

Chosen direction from the four design concepts (Exo reel / Tab Bar / Orbital / Starmap).
Source mockups live in the design zip; this doc is the implementation spec so the build
is faithful and resumable across sessions.

## Why Tab Bar

- It *is* the original "Liquid Glass, iOS-style, premium" brief: frosted-glass window,
  ambient drifting color blobs, an iOS-style bottom tab bar, an integrity ring + live
  telemetry hero.
- It fits the real app: tabs map to react-router routes; scrollable pages hold dense
  content (modules = 8 feature chips + profile toggle + apply/repair; settings = update
  + toggles + logs). The reel/orbital/starmap metaphors need heavy custom 3D/carousel
  work that is fragile in WebView2 and awkward for dense content.
- It keeps the best of the others (integrity ring, live telemetry) in a daily-usable frame.

## Design system

- **Fonts** (self-host as woff2 in `ui/public/fonts/`, `@font-face` in index.css — the
  packaged app serves local wwwroot and must work offline; no Google Fonts CDN):
  - Display / headings: **Space Grotesk** (400/500/600/700)
  - Numerals / mono labels: **JetBrains Mono** (400/500/600/700)
- **Window**: 1200x780 max, `border-radius:34px`, `backdrop-filter:blur(60px) saturate(205%)`,
  1px white/12% border, deep shadow + inset top highlight. Diagonal glass gradient
  (white 5% -> black 88%).
- **Ambient background**: 3-4 large blurred radial blobs (`#233a99`, `#0a4f47`, `#45206b`,
  `#0f5170`) with slow `drift` keyframes; an animated diagonal `sweep` highlight.
- **Tokens already in index.css** reused: page/sunken/raised/glass-border/muted/secondary/
  success/error + module accents (discord #5865f2, brave #fb542b*, steam #1a9fff,
  internet #22d3ee, nvidia #76b900). *Design uses brave #fb542b; current app token is the
  same family — confirm against the brave.png logo.
- **Tone colors**: ok `#34d399`, warn `#fbbf24`, bad `#f87171`, neutral `#8b8d92`.
- **Feature chip**: pill, green tint + ✓ when active, muted when off (from `buildView`).
- **Radii**: window 34, cards 22-26, chips 11-14, buttons 13-14, pills 999.

## Screens and the live-data mapping

Every screen binds to the existing `host` bridge (`ui/src/lib/host.ts`) — no new backend.

1. **Overview (`/`)** — hero "SYSTEM STATUS" + integrity ring (conic gradient) + verify
   button + "LIVE TELEMETRY" 4-col grid (CPU/GPU/MEM/NET).
   - Integrity ring % = applied/total from `host.verifyAll()` (or dashboard modules
     applied count); ring color ok/warn/bad by threshold.
   - Telemetry = `host.getLive()` (memoryPercent, cpuPercent/hasCpu, gpuPercent/hasGpu,
     netLinkSpeed/netRating) polled ~1.5s, same cadence as current HomePage.
   - Verify button -> `host.verifyAll()`, streams `settings.verifyProgress` events.
2. **Module detail (`/module/:id`)** — glowing icon + name + status pill + detail, optional
   profile toggle, 2-col feature-chip grid, Apply + Repair.
   - `host.detect(id)` -> statusKind/statusText/detail/features[]/options.
   - Feature chips = `features[]` (title, active). Status pill tone from statusKind
     (applied=ok, partial=warn, ready=neutral).
   - Profile toggle: internet `preferLowestLatency` (Lowest latency / High throughput),
     nvidia `useGsync` (Raw latency / G-SYNC-VRR) — pass through `host.apply(id, {...})`.
   - Apply -> `host.apply(id, options)`; Repair -> `host.repair(id)`; progress via
     `module.progress` events; re-detect result already returned by apply.
3. **Games (`/module/games`)** — fanned game-cover card deck + detail (3-col feature grid,
   potato/optimized toggle, apply/repair). Binds to `host.listGames/applyGame/repairGame/
   openGameInstall` (GamesPage already implements this logic — restyle, keep the wiring).
4. **Settings** — update banner (`host.checkUpdates`), General toggles
   (launchAtStartup/minimizeToTray via `settings.set`), Maintenance (Verify, View logs
   via `shell.openLogs`), footer (version + GitHub/BuyMeACoffee/Privacy links).
   - Reuse SettingsDrawer's existing host calls; present as a tab screen or keep as drawer.
5. **Advisor (`/advisor`)** — already built (`host.advisorInsights()`); restyle to match.
6. **Welcome dialog** — "Free forever. No catch." tip-jar modal; reuse existing
   WelcomePrompt gating (`welcomePromptSeen` in settings).

## Navigation

- Replace the header module-rail (Shell.tsx) with a **bottom tab bar**: Home, Discord,
  Brave, Steam, Games, Internet, NVIDIA, Settings (+ Advisor entry). Active tab = pill
  highlight (framer-motion layoutId), matches current nav-pill pattern.
- Keep HashRouter + the existing route table; the tab bar just drives it.

## Phased build (each increment: compiles, `tsc -b` clean, wwwroot rebuilt, testable)

1. **Foundation**: self-host fonts + `@font-face`; add design tokens (tone colors, blob
   vars); `AmbientBackground` component (blobs + sweep); window shell.
2. **Overview home** wired to live telemetry + verify ring. Swap index route.
3. **Bottom tab bar** replacing the header rail in Shell.
4. **Module detail** screen (feature chips + profile toggle + apply/repair) — replace
   ModulePage body, keep host wiring.
5. **Games** restyle (card deck) over existing GamesPage logic.
6. **Settings** screen + **Advisor** restyle + **Welcome** modal.
7. Polish: reduced-motion fallbacks, WebView2 backdrop-filter perf check, Ui.Smoke
   assertions updated for the new structure, screenshots.

## Constraints / gotchas

- **Offline fonts** — must be self-hosted; no external CDN in the packaged app.
- **WebView2 backdrop-filter** — verify perf/rendering on the real runtime; provide a
  solid-fallback if blur is heavy.
- **Ui.Smoke** asserts structure + "no dead-module strings"; update it as screens land so
  CI never goes structurally red (land related screens together).
- Keep the app working throughout — build on this branch, do not merge partial migrations.
- `x-dc` mockups use a bespoke template runtime; translate intent to React/Tailwind, do
  not port `sc-for`/`{{ }}` literally.
