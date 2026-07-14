## 2.2.6

- **Faster startup after updates**: the full script-kit reinstall that ran before first paint on every post-update launch now happens on the background warm-up path (consumers still self-ensure correctness under the same lock)
- **Window focus no longer does wasted work**: the taskbar-icon workaround re-ran file probing + Win32 icon loads (leaking icon handles) on every activation; it now runs once
- **Faster NVIDIA detection**: process checks query the exact process names service-side instead of enumerating and regex-filtering every process on the system, twice

## 2.2.5

- **Upgrade path from OptiHub**: the installer now closes any running legacy OptiHub app, migrates saved settings and optimizer state from `%LocalAppData%\OptiHub` into `%LocalAppData%\Exo`, and removes the old install folder and Start Menu shortcut
- Settings migration rewrites legacy script-update repos (`UhhErix`/`OptiHub`) to `ImAvgErix/Exo`, so carried-over settings can't point script updates at the deleted repo

## 2.2.4

- **Repo polish**: README / CONTRIBUTING / SECURITY, issue + PR templates, CI runs peak + UI smokes, `.editorconfig`
- Removed dead `SettingsOverlayState` (settings is a flyout, not a modal overlay)
- `.gitignore` covers local agent/IDE scratch (`override-diff-*`)
- Docs and agent notes aligned with gear-flyout shell + crisp motion rules

## 2.2.3

- **Cleaner interaction motion**: hover/press uses animated highlight wash + accent ring (not scale) so you always know focus without blur
- **Home entrance** back: soft fade + short rise, then transform is dropped so type stays sharp
- **Settings open**: fade + 8px drop from the gear again
- **Select pulse** clearer dim before navigate

## 2.2.2

- **Sharpness pass**: drop hover/press **scale** on cards + CTAs (opacity only) — scale was softing type and logos
- Entrance is **fade-only** (no translate); transforms are cleared after motion so nothing stays subpixel
- Logos decode at **128 logical px** (2× the 64px display size) for clean DPI downscale

## 2.2.1

- **Borderless icons**: home logos float without a well outline; feature icon wells are fill-only (no stroke)
- **Crisper logos & type**: full-fidelity bitmap decode (no forced 256 downscale), 64px marks, integer font sizes (13.5 → 14), gentler card hover so bitmaps stay sharp

## 2.2.0

- **UI polish pass**: selected Dark/Light theme pills, clearer card depth, tighter module headers
- **Settings**: section caps + theme radios with white selected state
- **Modules**: consistent page padding / title rhythm; feature tiles and icon wells more deliberate

## 2.1.6

- **Settings open is cohesive**: gear crank and menu entrance share the same ~220ms motion
- **Back no longer shifts UI left**: home page is cached; transforms cleared on leave/return

## 2.1.5

- **Taskbar blank-paper fix**: stable `Exo.ico` + auto-repair on launch
- **AppUserModelID** set before window create; icon reapplied on activate

## 2.1.4

- **Settings opens instantly**: flyout shows on tap; gear spins in parallel
- **Taskbar icon**: Win32 WM_SETICON big+small in addition to AppWindow.SetIcon

## 2.1.3

- **Settings back to 2.1.0**: gear crank + dropdown flyout under the gear (not side rail)

## 2.1.2

- **Settings rail restored** (side panel under gear, not centered modal)
- **Faster open**: gear crank ~140ms, rail expand ~160ms

## 2.1.1

- **Settings rail**: full-height left panel from the gear
- Home content shifts right so the rail never covers cards

## 2.1.0

- **Settings restart**: gear crank + **dropdown flyout** (no full-screen modal)
- Flyout: Dark / Light, check-on-launch, Check for updates, Report issue / Open logs, app version

## 2.0.6

- Settings star-grid center host; check-for-updates fixed-height progress slot
- **Home card names**: title under each optimizer logo

## 2.0.5

- No top-left drift on settings; card select pulse; card hover scale without TranslateY

## 2.0.4

- Soft open animations via XAML Storyboards; never Composition Opacity

## 2.0.3

- Settings Visibility-only open/close; removed Motion slider

## 2.0.2

- Stop composition blanking UI; home tagline centered; compact cards

## 2.0.1

- Settings open order fix; EnsureVisible fail-safes; home balance

## 2.0.0

- **Settings never teleports**: sheet host is opacity-only; layout owns center (full-bleed stage). No composition Offset/Scale on the host — re-open stays dead center
- **Settings close/open race**: epoch-gated Finish so a delayed close cannot collapse the sheet after a fast re-open (overlay no longer stuck unusable)
- **Home balance**: 48px centered tagline is the hero; cards 300×158 sit under it without overpowering
- **Live Motion slider**: continuous 0–100 control drives entrance travel (not just toggles)
- **Premium secondary buttons**: deliberate stroke + soft sheen + stable hover/press (Marcel craft)
- **Theme clarity**: current mode large + “Tap to switch…”; high-contrast theme-aware fill
- **Motion language**: one Kinetics family; hard identity reset after every open/close and entrance
## 1.9.104

- **Settings corner fix**: full composition reset (Offset/Scale/Opacity) on every open/close — sheet no longer sticks in a corner
- **Theme clarity**: button shows current mode large (“Dark mode” / “Light mode”) + “Tap to switch…” hint; theme-aware high-contrast fill
- **Home**: tagline stays centered; cards larger (352×200) to match hero weight
- **Loader**: one orbit language (bead + trail + breath) — no competing sweep/ghost rings
## 1.9.103

- **Motion system**: shared OptiMotion (Composition) across home, settings, modules, loader
- **Home cards**: no flicker — prime invisible then Kinetics stagger rise/scale
- **Settings**: spring open/close + row stagger
- **Modules**: soft page enter on navigate
- **Loader**: orbit + arc sweep + breath (premium loading)
## 1.9.102

- **Settings theme**: single dark button “Dark mode” / “Light mode” — press to flip UI (no slider)
## 1.9.101

- **Settings**: premium layout fix — title/switch/close no longer collide; Light · slider · Dark centered
- **Motion**: Kinetics-style spring open + stagger rows (settings); spring home card entrance
## 1.9.100

- **Settings**: Dark/Light is a slider; About is “APP VERSION” with app version only
## 1.9.99

- **Settings**: even 18/16 card padding, 14px section rhythm; home-card chrome
- **Theme pills**: layered fill so selected white Dark never vanishes on hover
## 1.9.98

- **Settings**: padding/spacing/type match optimizer pages (OptiSectionTitle, OptiCard 18×16, button heights, tile radius)
## 1.9.97

- **Internet**: no open banner for Ethernet/Wi-Fi detect — header path only
- **Theme**: Dark and Light are separate full tiles (selected = solid white)
- **Update UI**: short new-version prompt; one percent line while the bar runs
## 1.9.96

- **Settings**: one card (not 4 boxes); no Settings title
- **Theme pills**: selected side is solid white with dark text
## 1.9.95

- **No tooltips** anywhere in the app
- **Settings overlay**: home-style hero tagline + 2×2 OptiCard tiles (matches dashboard language)
## 1.9.94

- **Settings**: modal overlay on the home page (acrylic blur scrim) instead of a separate page
- **Theme pills**: Dark no longer vanishes on hover (checked fill paints above hover)
- Close via ✕, backdrop tap, or Back
## 1.9.93

- **Settings**: single centered settings sheet (no sparse 2×2 cards); Dark/Light segment pills
- **Chrome**: remove leftover “Settings” title next to Back (page owns the header)
## 1.9.92

- **NVIDIA tray**: stop restarting NVDisplay.Container (that re-promoted the icon); restore logon re-hide task (was removed in 1.9.24)
- **Finish banners**: same **Done.** / **Repair finished.** / **Cancelled.** on every optimizer
- **Updates UI**: percent + progress bar only (no orbit loader, no MB text) in Settings + install dialog
- **Settings**: denser cards; consistent tooltips on chrome + actions
## 1.9.91

- **Update loader**: Windows Composition orbit (actually moves in Settings + install dialog)
- **Internet icon**: tighter SF-style Wi‑Fi — equal air between arcs and the center dot
## 1.9.90

- **Internet icon**: clean Apple/SF-style minimal Wi‑Fi (3 even arcs + dot)
## 1.9.89

- **Home tagline**: larger hero type (44px) so it sits above the product cards
- **Internet icon**: modern Fluent full Wi‑Fi (complete, not cut off)
- **Windows icon**: official Windows 11 four-pane mark
- **Update loader**: DispatcherTimer orbit — animates on Settings Updates card
## 1.9.87

- **Home cards**: larger tiles (340×190, 96px logos) — fills fixed 1180×760 frame
- Drop “Pick a target…” blurb (tagline only)
- **Internet logo**: high-quality classic 3-arc Wi‑Fi mark
- **Windows** coming-soon card with Windows 11 logo

## 1.9.86

- **Fixed window** 1180×760 — no maximize, no free resize (UI designed for this frame)
- **Home**: static 3-col cards (240×148), deleted responsive layout code
- **Internet**: white **Low latency** | **Highest download** side by side (no prompt)

## 1.9.85

- **Internet**: two white buttons — **Low latency** | **Highest download** (no slow popup); refreshes tweak rows + header in place after apply
- **Home cards**: denser sizing (smaller max tile); settings tighter padding
- **Wi‑Fi logo**: complete 3-arc + dot mark

## 1.9.84

- **Internet deep pass (Ethernet + Wi‑Fi)**: DMA coalescing/IFS off, jumbo standard, priority VLAN, RSS profile; Wi‑Fi TX power, channel width, mode, MU‑MIMO/OFDMA/beamform, WoWLAN/BT collab off; NetBIOS off; NCSI active probe off
- **Update card OptiLoader**: stays mounted (opacity) + restart-on-visible so animation actually runs
- **Internet logo**: more minimal 2-arc Wi‑Fi mark

## 1.9.83

- **Internet Repair** button (like Discord/Steam) — restores stock-like bindings, metric auto, TCP defaults, re-enables Wi‑Fi
- **Ethernet Properties checkboxes**: Apply sets full binding set (QoS+IPv4+IPv6 on; Client/File share/LLDP/LLTD/Multiplexor off) + detect row
- **Internet logo**: modern Wi‑Fi arcs (white on transparent)

## 1.9.82

- **Internet**: open detect shows Ethernet vs Wi‑Fi path; single **Apply** → latency/download choice only (no second confirm); Ethernet metric re-stamp after restart fixed; QoS/LLTD/DO/tunnels applied
- **Apply on Discord / Steam / NVIDIA**: no confirm dialog — runs immediately (Repair still confirms)

## 1.9.81

- **In-app update UI**: branded confirm + install dialog with OptiLoader (card loader) and download progress bar; Settings shows the same loader/bar while checking; launch auto-update uses the same chrome

## 1.9.80

- **Steam logo size restored** — was over-shrunk (140px); back to ~212px peer diameter (window-screenshot verified)

## 1.9.79

- **Cards no longer clip**: ScrollViewer + responsive columns/card size by window width
- **Title bar**: no logo + module name on optimizer pages (page already shows the name)
- **Logos rebalanced again** (Steam disc smaller; contact-sheet verified)

## 1.9.78

- **Logo optical balance**: normalize all hub marks to a shared size — Steam solid disc scaled down (was dominating), AMD/NVIDIA brought up, equal padding on transparent canvas

## 1.9.77

- **Home cards logo-only** — title removed (shown on the module page after open); larger mark
- **Report issue** button solid white

## 1.9.76

- **AMD logo transparent**: drop the solid white disc — white corporate mark on clear alpha so it blends like Steam/Discord/NVIDIA

## 1.9.75

- **Ethernet metric actually sticks**: disable AutomaticMetric + set metric 1, then **re-stamp after adapter restart** (restart was putting Windows back to ~20–25 auto metric → red X)
- **Bit depth is not cosmetic**: NVAPI set verifies driver readback; fail with plain reason if panel stays 8-bit; UI re-syncs pickers to live depth after Apply; offer one-step upgrade (8→10) when driver claims support

## 1.9.74

- **Internet detect after apply**: no more false “not checked” rows — unknown autotune skips (like LSO/RSC), MMCSS/QoS parse any DWORD type, Wi‑Fi-while-Ethernet only fails when Ethernet-first was chosen, longer settle + one re-probe
- **NVIDIA Apply**: removed triple-pass of Advanced3D / Overlay / Telemetry / notifications — single ordered stage + one DRS re-assert after display (App wipe still retries only if needed)
- **OptiLoader**: new “orbit bead” loader (soft track + accent bead + ghost trail + breathing core) — not ProgressRing, triad, or signal bars

## 1.9.58

- **Restored ~v1.7 SPA look** in modern WinUI (not WebView2 rewrite)
- Gear top-left, no sidebar — home product cards are the only module picker
- Hero: “Maximum performance. No compromise.” + 3-col logo cards
- Settings: 1.7-style Appearance / Support / Updates panels
- AMOLED black + cream light from original SPA palette
## 1.9.57

- **Restored pre-redesign UI (v1.9.46 AMOLED)** — top bar + home modules as navigation
- Removed permanent sidebar (was redundant with home modules)
- Settings back to original 2×2 card layout (gear in title bar)
- Keeps later fixes: resizable window, panel apply/refresh, scaling path
## 1.9.56

- **Anti-generic PEAK UI** (screenshot-verified): pure AMOLED black + white signal — not purple SaaS
- Dropped WORKSPACE / Command center / Linear violet clone
- Italic Exo brand, dense module lanes, white primary CTA
- Thin rail selection bar instead of colored pill
## 1.9.55

- **Linear/Raycast-inspired UI** (apps people actually praise) — verified with live screenshots
- Custom dark sidebar (not stock Windows NavigationView)
- Near-black canvas + violet accent (#5E6AD2) + elevated product tiles
- Home: Command center + large logo cards; optimizers: full-width feature rows
## 1.9.54

- **Verified with real window screenshots** (home + Discord)
- Full-width feature rows (no half-empty card / dead zone)
- Home 2-col module grid; no nested floating card shells
- Compact footer only when needed (no reserved empty status blocks)
## 1.9.53

- **Stable UI**: no page transitions, no hover lift, no entrance fades
- Status/progress use opacity in reserved space — apply/refresh no longer reflows buttons
- Feature lists stay mounted while loading (ring overlays instead of swap)
- Unified Fluent list home + consistent 8px cards/controls so pieces match
## 1.9.52

- **Soul pass** on professional Fluent base (not a redesign from zero)
- Ink-indigo surfaces + soft signal accent; Cormorant hero type
- Home brand mark + tagline; 2-col module cards with accent rail + hover lift
- Softer radius, livelier buttons — still NavigationView + Mica bones
## 1.9.51

- **STUDIO from-scratch UI** — professional Windows 11 Fluent only
- Shell: Mica backdrop + standard NavigationView (left pane + settings)
- Calm solid surfaces, 8px Fluent corners, single blue accent
- Home: clean module list rows; optimizers: standard card layout; settings: simple sections
- Removed glass dock, acrylic orbs, experimental layouts
## 1.9.50

- **GLASS redesign** — Liquid Glass + Tesla clean (not flat recolors)
- Real Desktop Acrylic window blur (Mica fallback)
- Floating glass dock, translucent panels, specular edges, ambient color orbs
- Transparent chrome so materials show through; cyan glass accent
- Home/optimizers/settings on glass surfaces; minimal Tesla-like density
## 1.9.49

- **AURA redesign**: clean Apple/Tesla-calm UI — soft system dark, Apple blue accent, airy spacing
- Shell: quiet top bar with center pill nav (no heavy sidebar)
- Home: centered logo grid, title only (no explain walls)
- Optimizers: minimal centered stage, soft chips, pill CTA
- Settings: iOS-style grouped sections
## 1.9.48

- **NOVA full redesign** (layout + chrome + pages — not a recolor):
  - Wide labeled sidebar (icon + text + selection rail), not icon-only rail
  - Home: full-width horizontal module lanes with LIVE/SOON pills (not bento wrap grid)
  - Settings: two-pane master-detail (category rail + detail panes)
  - Optimizers + Display: cockpit split (sticky action column + vertical feature checklist)
  - Pure AMOLED black + white signal accents; sharp 10–14px geometry; slide page transitions
  - Lane hover-shift animation; staggered entrance on home
## 1.9.47

- **LUMEN full redesign**: deep ink canvas, soft blue accent, floating pill title chrome
- Dashboard: centered italic hero + large product tiles (not gray list)
- Optimizers + Display: soft elevated panel shell, pill CTAs, soft feature tiles
- Distinct from prior AMOLED/list and orange FORGE looks
## 1.9.46

- **Panel gray-out fix**: combos stay enabled during apply (no IsApplying disable); refresh after busy clears
- **UI cleanup**: remove bleeding side rails / nested card rings; flat AMOLED cards; shared simple page layout on all optimizers + hub
## 1.9.45

- **Borderless black bars fix**: stop forcing path `GPUScanOutToNative`; reset path to `GPUScanOutToClosest` so the panel fills. Registry still GPU + No scaling + Override.
- **UI consistency**: single-column AMOLED hub (same padding/max width as optimizers); cleaner chrome
## 1.9.44

- **Scaling**: restore peak default to GPU + **No scaling** + Override ON (your working config; full-screen was wrong)
- **Panel UI**: combos no longer blank on select; only Apply dirty fields; soft refresh keeps controls alive
- Live re-applied `--set-scaling gpu-noscaling` to clear full-screen side effects
## 1.9.43

- **AMOLED UI**: pure black + white accents (removes orange Forge palette)
- **Black bars fix**: peak scaling is GPU full-screen, not "no scaling" (no-scaling letterboxes games/desktop)
- **Color depth honesty**: only list depths at/below the live working depth (no fake 12-bit on 8-bit panels)
- Peak color pick keeps current depth instead of forcing 12-bit
## 1.9.42

- **FORGE UI redesign** (not a token pass): warm stone + amber rail language, sharp 8–10px geometry
- Shell: branded chrome bar with amber underline (replaces thin zinc title + divider)
- Dashboard: asymmetric left brand rail + vertical LIVE module list (no 4-column logo grid)
- Optimizer pages: open surface header strips + rail-edged feature tiles (no single nested card shell)
- Cards/buttons/section labels restyled for Forge (accent section titles, bold CTAs)
## 1.9.41

- **NVIDIA Panel**: post-apply refresh no longer skipped while IsBusy (`RefreshCoreAsync(force: true)`)
- Launch/UI smoke asserts force-refresh path after apply
## 1.9.40

- **Resizable shell**: maximize + edge resize; removed fixed chrome re-lock and maximize block
- **UI**: fluid layouts (wider page max), refreshed dashboard hero, taller title bar
- **NVIDIA Display panel**: Control Panel–style per-monitor Resolution, Refresh rate, Color depth, NVIDIA color (Full/Limited), Scaling with real NVAPI/Win32 apply
- Exo.NvDisplay: `--list-displays`, `--set-mode`, `--set-scaling`, `--set-color-range`
- `NvidiaPanelLogic` pure CLI builders + smoke coverage
## 1.9.39

- **Discord detect fix**: Complete client debloat row always emitted (empty-locale `@()` unwrap / Count throw under StrictMode)
- Soft-drift recovery only when hard signals clean; never trust state when leftover app-* or payload modules remain
- Host heuristic payload-aware optional modules + shared `IsClientDebloatApplied` / `Test-DiscOptClientDebloat`
- DiscordPeak.Smoke fixtures + live detect 5x debloat-row proof
## 1.9.38

- **UI Signal theme**: teal/mint accent on cool graphite (dark) and clean teal (light); denser cards, refreshed dashboard hero
- **NVIDIA Panel**: live color bit-depth dropdowns per display (`--list-color` / `--set-depth` via Exo.NvDisplay); peak Apply still forces best defaults
- **Discord detect**: Complete client debloat no longer false-fails on empty recreated modules, soft SDK/locale drift, or verified full apply for the same build
- **README**: project-page style (Winhance-class) with tables, download, layout, smokes
- Dead-code trim on NvDisplay depth picker; color-depth elevated script path
## 1.9.37

- **UI peak**: shared design system (page titles, feature tiles, message banners, muted hierarchy); refined dark/light surfaces + divider chrome
- Dashboard / Internet / Discord / Steam / NVIDIA / Settings re-skinned for clearer CTA hierarchy without clutter
- `UiStatusPresentation` + `tools/UiPeak.Smoke` for consistent status tone/glyph mapping
## 1.9.36

- **NVIDIA peak 1.10.3**: display status ignores orphan NVTweak keys; peak OK = max-Hz refresh + (active registry OR live Full RGB + GPU scale)
- Sticky game profile deltas expanded (pre-render 1, max perf, highest Hz, FG off for competitive); tray feature row (IsPromoted=0 / App ghosts gone)
- `NvidiaPeakLogic` + `NvidiaDetectCore` + `tools/NvidiaPeak.Smoke`; no logon tray tasks; MSI verify skips when PCI unreadable
## 1.9.35

- **Steam peak detect 1.7.8**: `SteamDetectCore.ps1` + `SteamPeakLogic` — CEF launcher + trim helper classifiers; trim accepts 2–15s (not hard-coded 5s only)
- Host heuristic uses same CEF/trim rules; smoke `tools/SteamPeak.Smoke`; no Exo-Steam scheduled tasks
- Ships with Internet peak (NetworkPeakLogic) + Discord peak (DiscordDetectCore) from prior work
## Discord 1.3.22 / detect peak

- **Discord detect peak**: `DiscordDetectCore.ps1` + `DiscordPeakLogic` — kernel OK for kit TrimIntervalMs=4000 and prior 5000; no exact config.ini hash false-fail
- Toast quiet policy aligned host/heuristic with detect (≥1 Discord toast key Enabled=0)
- Smoke: `tools/DiscordPeak.Smoke` drives shipped classifiers + apply audit (no Exo-Discord scheduled tasks / folklore)
## 1.9.34

- **Probe preset-aware**: NIC peak (Flow Control / IM / IdleRestriction) scored per active preset — download intentional ons no longer false-fail
- **Autotune match**: HighestThroughput requires `experimental` (not any non-disabled); MatchesPreset uses NetworkPeakLogic knobs for LSO/RSC/autotune
- Smoke + live probe-summary cover both latency and throughput with false_fail_count=0
## 1.9.33

- **Internet peak freeze**: pure shipped decision core `NetworkPeakLogic` + `NetworkApplyScriptBuilder` (band score, path policy, preset knobs, apply audit)
- Smoke tests drive real sources (`tools/NetworkPeak.Smoke`): Prefer>Only, eth-usable vs link-no-IP, latency vs throughput script diverge, no folklore
- Detection/apply Wi-Fi classifier aligned (exclude Bluetooth/Hyper-V/VPN tunnels); live 6 GHz re-probe at apply
- Docs/golden path frozen at 1.9.33
## 1.9.32

- **Internet peak pass**: force NetworkThrottlingIndex **10** (overwrite ffffffff), Ethernet metric **1** on usable link, Flow Control off (latency), IdleRestriction **on** (block NIC low-power idle)
- powercfg: wireless max performance, PCIe ASPM off, USB selective suspend off (AC)
- Live re-probe of 6 GHz capability at apply time; broader Wi-Fi power-save property kill list
- Probe/UI: show throttle value, eth metric, NIC peak (flow control / IM / idle restrict), current Preferred Band value
- Apply log: %TEMP%\exo-net-last.log
## 1.9.31

- **Internet Wi-Fi band matching**: fuzzy Preferred Band property + display-value matching for Intel/Realtek/MediaTek/Qualcomm/Killer string variants (Prefer 6GHz band, 5 GHz preferred, Preferable Band, etc.)
- Prefer-* still beats Only-*; never force band-only; same golden path policy as 1.9.30
## 1.9.30

- **Internet golden path (freeze)**: deep detection for Ethernet vs Wi‑Fi via PhysicalMediaType; usable Ethernet = Up + real IPv4 → metric 1 + disable Wi‑Fi
- Wi‑Fi: detect 5/6 GHz, Wi‑Fi 6/6E/7 from driver + Preferred Band values + netsh; prefer 6 then 5 (never force-only)
- Connected band/radio/channel hints; eth/wifi apply branches locked in docs/INTERNET-GOLDEN-PATH.md
## 1.9.29

- **Ethernet preferred 100%** when linked with a real IPv4: lower interface metric + disable Wi‑Fi (gaming lowest-latency path)
- Cable with no IP still leaves Wi‑Fi alone
## 1.9.28

- **Wi‑Fi disable only when Ethernet is in use**: default IPv4 route + real IPv4 (not just adapter Status=Up / cable with no route)
- Linked-but-unused Ethernet leaves Wi‑Fi alone
## 1.9.27

- **Smart path policy**: if Ethernet is up, prefer Ethernet and **disable Wi‑Fi**; Wi‑Fi-only path uses capability detect
- **Band smarts**: prefer 6 GHz when the client supports it, else 5 GHz (never force-only)
- **Restart prompt**: no silent adapter restart — dialog asks Apply + restart Ethernet vs Apply without restart
- Detection is local (adapter + netsh), not a cloud AI
## 1.9.26

- **Internet: Ethernet vs Wi‑Fi branches** — RSS only on Ethernet (MS: many wireless NICs lack RSS); Wi‑Fi power-save/uAPSD off + prefer 5 GHz (not 5-only); never Restart-NetAdapter on Wi‑Fi (avoids drop); still tunes all physical NICs for dual-homed PCs
## 1.9.25

- **Evidence audit (all optimizers)**: Internet stack cut to documented knobs only — SystemResponsiveness **10**, NetworkThrottlingIndex **10** (not ffffffff), drop XP/server folklore (MaxUserPort, chimney, LargeSystemCache, AFD backlog, WinINET, etc.)
- Keep real tradeoffs: autotune, RSS, RSC/LSO by preset, Nagle for latency, NIC power-save off, QoS reserve 0
- NVIDIA tray: no scheduled tasks; Discord/Steam unchanged (already real client-side work)
- See docs/TWEAK-AUDIT.md
## 1.9.24

- **No tray scheduled task**: remove logon noise; tray only on Apply/Clear (hide display icon, delete App ghosts)
- **SystemResponsiveness = 10**: Microsoft clamps values &lt;10 to 20 — 0 was wrong; 10 is the real gaming minimum
- Unregister any leftover Exo-NvidiaTrayHide tasks on apply
## 1.9.23

- **NVIDIA tray**: stop resurrection — hide NVDisplay container (IsPromoted=0) instead of deleting; wipe App ghosts; logon re-hide task; multi-pass after soft refresh
- **Gaming stack**: Discord kernel trim tighter; Internet latency SystemResponsiveness=0; tray clear on apply paths
## 1.9.22

- **Feature tiles**: only optimizer features (no path/DNS/adapter/provider/ping cards); same idea on Discord/Steam/NVIDIA rows
## 1.9.21

- **Logos**: solid-fill AMD (red plate) + Internet globe matching other hub marks
- **Internet UI**: shortened like Discord/Steam (title + status + feature grid + actions)
- **Probe fix**: latency preset treats LSO/RSC off as pass; Nagle/throttle rows; post-apply verify
## 1.9.20

- **Back to classic hub**: centered Exo + card grid (no sidebar / home stats)
- **Cards**: Internet (live) + AMD (coming soon) alongside Discord, Steam, NVIDIA, Brave, Riot, Epic
- **Layout**: larger window, 4×2 grid, matching logo wells; Internet globe + official red AMD mark
## 1.9.19

- **Home dashboard**: default landing with live PC/CPU/GPU/RAM/network/latency/optimizer stats + quick open
- **Collapsible sidebar**: Home · Apps · Internet · GPU (icon rail, remembered)
- **UI polish**: tighter cards, captions, icon nav, larger shell; official red AMD logo
## 1.9.18

- **Windows hub shell**: left sidebar **Apps | Internet | GPU** with section-filtered cards
- **Internet optimizer**: full SG TCP Optimizer–class stack + NIC/power/QoS/DNS/AFD/Wi‑Fi; auto-detect adapter/provider/area/latency; **Lowest latency** / **Highest download** presets (admin)
- **GPU**: **AMD** coming-soon card + logo
- Internet card navigates to dedicated optimizer page
# Changelog
















## 1.9.73
- OptiLoader: signal-meter bars + scan sweep (unique, not a stock spinner)
## 1.9.72
- Unique OptiLoader (orbiting triad + pulse) replaces stock ProgressRing on all module pages
## 1.9.71
- Settings Support: Report issue first, Open logs second
## 1.9.70
- NVIDIA reliability: laptops no longer stuck Not applied; Optimus display N/A; soft GDI map + multi-GPU enum; clearer Display errors
- Detect/status align across desktop, laptop, and hybrid configs
## 1.9.69
- Home cards are nav only — no Checking/Applied status (that runs when you open a module)
## 1.9.68
- Home Discord/Steam/NVIDIA status uses the same full detect as the module pages (fixes false Not applied)
## 1.9.67
- One-click buttons (ClickMode=Press), no nav animation delay
- Home status chips: Applied / Not applied / Ready / etc. (plain labels)
- Display Apply: dirty hint, Apply vs Up to date, clear applied feedback
## 1.9.66
- Fix title-bar chrome: Settings/Back live outside the drag region so one click works (no double-hit maximize)
## 1.9.65
- UI polish: equal logo wells, B&W AMD + Fluent Globe peer weight, softer coming-soon opacity, stronger UiPeak.Smoke logo ink asserts
## 1.9.64
- AMD black-and-white official mark on white disc (Steam-size); Fluent Globe scaled to match; hub logo well 56px
## 1.9.63
- AMD + Internet: real official icons only (AMD brand mark, Microsoft Fluent Globe) — same flat style as Discord/NVIDIA
## 1.9.62
- AMD + Internet logos remade to match the hub set: flat Steam-style white circle AMD mark, white outline globe (no glossy tiles)
## 1.9.61
- High-quality AMD + Internet home logos (brand AMD badge, gradient globe) so they match Discord/Steam/NVIDIA weight
## 1.9.60
- NVIDIA Display panel: only selectable options (res/Hz/depth/color/scaling) — remove policy applied tiles, peak defaults, and tray clear
## 1.9.59
- Restore polished 1.8.32-era UI (hero home, 300×188 cards, 2×2 Settings, Kinetics motion) — last of the pre-redesign shell
- Keep Internet + NVIDIA Display, resizable window, panel force-refresh, Closest path scaling
## 1.9.4

- **NVIDIA Panel page**: full-card UI (same Exo styles) — Applied checkmarks + Apply; live probe of display/video/clients/tray; Clear tray icons
- **Tray**: remove ALL NVIDIA overflow icons (including NVDisplay.Container registration) + ProgramData App leftovers
- **Back** from panel returns to NVIDIA optimizer card
## 1.9.3

- **NVIDIA Panel**: **Apply** (not Fix); checkmark when **Applied**; fixed policy primary highest Hz / secondary 60 Hz (no dropdowns)
## 1.9.2

- **NVIDIA Panel**: **Applied** (checkmark) / **Not applied** rows with **Apply** (not Fix); **Apply all** sets Exo policy — primary highest Hz, secondary 60 Hz (no refresh dropdowns/toggles)
## 1.9.1

- **NVIDIA Panel**: fix false **Apply failed** when turning settings **off** — verify against your panel prefs (not hard-coded ON); NVAPI skips Full RGB/GPU scale when disabled; Store hive stamp is best-effort

## 1.9.0

- **NVIDIA Panel UI**: new **NVIDIA panel** dialog on the NVIDIA card — display refresh (primary/secondary), Full RGB, GPU no-scaling + override, video NVIDIA color/image, developer counters, strip clients
- **NVIDIA 1.10.0**: **driver only** — removes **App + Control Panel**; Exo panel is the only settings UI; panel prefs saved to `%LocalAppData%\Exo\nvidia-panel-settings.json` and applied via NVAPI
## 1.8.32

- **NVIDIA 1.9.8**: **Exo is the control panel** - green checks use live NVAPI/DRS (not Store CPL UI). Also stamp Store CPL **virtual hive** (`Packages\...\Helium\User.dat`) so CPL may match; CPL alone was never reading real HKCU
## 1.8.31

- **NVIDIA 1.9.7**: hard-stamp **every** monitor NVTweak key - scaling **override ON**, desktop **Use NVIDIA + Full** range, video **color+image NVIDIA** (both monitors); re-assert **Gestalt=2** after container refresh; re-disable App container + clear tray ghosts after soft refresh (was re-arming hidden icons)
## 1.8.30

- **NVIDIA 1.9.6**: always enable Control Panel **Developer Settings** (`NvDevToolsVisible=1`) + **GPU performance counters for all users** (`RmProfilingAdminOnly=0`)
## 1.8.29

- **NVIDIA 1.9.5**: **secondary monitors force 60 Hz** (primary keeps max Hz); re-assert **Use the advanced 3D image settings** (`Gestalt=2`, not Balanced) after display apply + close CPL so UI reloads
## 1.8.28

- **NVIDIA 1.9.4**: clear **taskbar overflow ghost icons** for uninstalled NVIDIA App (`NotifyIconSettings` nvcontainer.exe); disable App `NvContainerLocalSystem` (do not restart it); keep display `NVDisplay.Container` only
## 1.8.27

- **NVIDIA 1.9.3**: stack is **Display.Driver + Control Panel only** - strip **Virtual Audio / HD Audio**; NVCleanstall-class expert tweaks restored (MSI High, telemetry/Ansel off, **HDCP off**); no App/audio preserve messaging
## 1.8.26

- **NVIDIA 1.9.2**: hard silent **NVIDIA App uninstall** via NVI2 - **64-bit System32 RunDll32** + `-silent -noreboot` (SysWOW64 was returning invalid args and leaving App installed); no winget; all Display.NvApp/ShadowPlay/FrameView/Telemetry packages; force-delete folders/ARP/pending; 3 wipe passes
## 1.8.25

- **NVIDIA 1.9.1**: enable Control Panel **Use the advanced 3D image settings** (`NVTweak` Gestalt=2) so Manage 3D / imported profiles take effect
- **NVIDIA detect**: fix undefined `$appOk` (status wrongly stuck on App); Control Panel-only client checks + advanced 3D feature row
## 1.8.24

- **NVIDIA 1.9.0**: **Control Panel only** - always remove NVIDIA App/GFE, install classic Control Panel, accept CPL EULA, NVAPI for scaling/Hz (no App download/install path)
## 1.8.23

- **Installer**: on every install/update, clear Windows icon/thumbnail caches + SHChangeNotify so Start Menu shows the new Exo icon (not a stale older mark)
- **NVIDIA 1.8.10**: Brian 1.8.9 log - after App exit -436207616, `[uint32]` hex logging threw and **Failed** Apply before Control Panel; safe Format-ExitCodeHex so Apply continues to CPL + NVAPI
## 1.8.22

- **NVIDIA 1.8.10**: Brian log 1.8.9 still **Failed** - after App exit -436207616, logging used `[uint32]` hex format which **threw** and aborted Apply before Control Panel/NVAPI; use safe Format-ExitCodeHex
## 1.8.21

- **NVIDIA 1.8.9**: fix unsupported-exit detector - PS `[uint32]` cast threw on **-436207616**, so 1.8.20 still missed Brian's code; use BitConverter + exact signed match
## 1.8.20

- **NVIDIA 1.8.8**: treat App setup exit **-436207616 / 0xE6000000** as system-not-supported (Brian GTX 1080 log) so Apply fails fast and falls back to Control Panel + NVAPI instead of retrying for minutes
## 1.8.19

- **All 3 optimizers**: Windows toast notifications off for Discord / Steam / NVIDIA (App + Control Panel keys); also set ShowInActionCenter=0
- **NVIDIA 1.8.7 / Steam 1.7.7 / Discord 1.3.7** kit stamps
## 1.8.18

- **NVIDIA 1.8.6**: remove broken minimized App open/close first-run (did nothing on CEF UI)
- **NVIDIA**: stop wiping classic Control Panel; if App fails, always ensure CPL + run NVAPI display (scaling/Hz)
- **NVIDIA**: soft-pass overlay/debloat checks when App is absent so Apply still completes
## 1.8.17

- **NVIDIA 1.8.5**: detect NVIDIA App installer reject exit **0x1A000000 / 436207616** ("system configuration not supported") and **fail fast** - no more pause on setup exit
- **NVIDIA**: if App cannot install, install **classic Control Panel** fallback and still apply scaling/Hz/Full RGB via **NVAPI**; App optional when unsupported
## 1.8.16

- **NVIDIA 1.8.4**: silent minimized first-run of NVIDIA App to click through EULA/onboarding (never Enable Overlay), then re-assert overlay off / beta / debloat
- **NVIDIA**: GTX 10-series note - App is supported; drivers stay on security branch (~582.x)
## 1.8.15

- **NVIDIA 1.8.3**: App install is official nvidia.com CDN first (fast); winget last-resort with 30s kill (no more 5 min hangs)
- **NVIDIA**: fix EULA/OOTB - set NVAPP_FIRST_LAUNCH=0, OOTBStatus=2, clear CEF onboarding cache so accept/overlay onboarding do not reappear; stronger overlay/ShadowPlay off
## 1.8.14

- **NVIDIA 1.8.2**: after fresh App install - auto-accept EULA, enable beta OTA channel, disable overlay + notifications, App backend debloat + system telemetry pass; no desktop shortcut
## 1.8.13

- **NVIDIA 1.8.1**: robust App install - elevated winget discovery, multi-flag Store attempts, official NVIDIA CDN fallback when winget fails
- **NVIDIA**: strip NVIDIA App / GFE desktop shortcuts after install (no desktop icon)
## 1.8.12

- **Updates**: longer download timeout (30 min), live download progress, clearer GitHub/rate-limit errors
- **Updates**: verify installer ProductVersion/FileVersion; SHA-256 still preferred when GitHub provides digest
## 1.8.11

- **NVIDIA 1.8.0**: wipe App + classic Control Panel + GFE -> fresh NVIDIA App -> debloat -> NVAPI display (series-correct drivers)
- **All optimizers**: PowerShell **7 Preview only** (no stable 7 / no 5.1); Discord no longer downloads stable portable pwsh
- **Steam 1.7.6 / Discord 1.3.6**: Preview host assert + launch helpers pin Preview; progress mirrored to host log
## 1.8.10

- **Settings**: balanced 2×2 cards — About (version) + Updates match Appearance / Support (no wide bottom strip)
## 1.8.9

- **Chrome**: stop auto-focusing Settings on launch (no gear highlight when the app opens)
## 1.8.8

- **Scripts run silently** — PowerShell 7 Preview still required (+ Terminal Preview on the system), but apply/repair no longer open a visible window
- **Settings Updates card**: app version only; no kit list, no double version footer, no empty gray status well until you check
## 1.8.7

- **Require PowerShell 7 Preview only** (no stable 7 / no 5.1)
- **Require Windows Terminal Preview** — install both via winget on startup if missing
- **Apply/repair** run inside Terminal Preview hosting PowerShell Preview (visible); detect stays headless Preview
## 1.8.6

- **Steam**: fix Verified apply false-negative when kit version > 1.7.2 (all checklist items mark correctly after success)
- **Discord / Steam**: stop pinning exact kit version strings for apply markers; trust full-apply flags
- **PowerShell**: prefer Preview; expand WindowsApps discovery; auto-install Microsoft.PowerShell.Preview + Windows Terminal Preview via winget when missing
- **Steam kit** 1.7.5; Discord DiscOpt version stamp aligned to 1.3.5
## 1.8.5

- **Brand icon**: Microsoft Fluent **Developer Board** filled (MIT) — solid Windows-native mark, not thin Lucide outlines / not a speedometer
## 1.8.4

- **Brand icon**: Lucide `cpu` (ISC) instead of gauge — avoids Speedtest-like speedometer look; multi-size Start Menu ICO
## 1.8.3

- **Brand icon**: real **Lucide** `gauge` icon (ISC license, free commercial use) on pure black Start Menu tile
- Multi-size ICO 16–256; source SVG under Assets/Icons with LICENSE note
## 1.8.2

- **Brand icon**: clean **OH** monogram (Exo) — fused O+H on pure black with mint accent; multi-size Start Menu ICO
## 1.8.1

- **Brand icon**: new unique Exo mark (hex hub + performance bars + mint accent) on pure black
- **Start Menu / pin**: multi-size .ico (16–256) so the glyph fills Windows Start tiles like other modern apps
- Packaged as ApplicationIcon + Start Menu shortcut IconLocation (versioned path + shell refresh)
## 1.8.0

- **Shell**: pure **WinUI 3** again — exact **1.6.13** UI (Jakarta/Cormorant fonts, hover cards, full names, Coming soon, Settings, AMOLED/cream)
- **No WebView SPA shell** — native Frame navigation only (more reliable logos, Settings, motion)
- **Backend**: current optimizers, quiet `/quiet` app updates, PS7 host, installer/WebView2 prereq helpers remain

## 1.7.9

- **UI**: restored the polished fluid SPA (1.7.4 design language) — the thin 1.7.8 shell is gone
- **Logos**: fixed blank icons — virtual host navigation + embedded base64 logo map (NavigateToString blocked file:// images)
- Larger logo wells; dark wells in light mode so product marks stay visible
## 1.7.8

- **UI overhaul**: minimal Raycast/Linear-style SPA — tight 8px rhythm, hairline cards, no page scroll
- **Home / Settings / Optimizer**: fit the fixed window; compact status, denser feature grid, clean updates strip
- Removed heavy ambient chrome, italic hero type, oversized status wells, and version footer clutter
## 1.7.7

- **WebView2**: detect incomplete Evergreen Runtime (missing icudtl/resources) and repair on launch + install
- **WebView2**: create environment before Ensure; clear bad browser-folder env; init after window Activated
- **Settings**: centered update status text only; removed duplicate version footer under the status well
- **SPA**: update status well fully centered (no misaligned icon)
## 1.7.6

- **Settings**: center the Updates status well (check-result text + icon)

## 1.7.5

- **Settings gear top-left** in host chrome (1.7.4 had it top-right on XAML fallback)
- **Quiet in-app updates**: `/quiet` SFX, no console window, no installer MessageBox
- **Start Menu icon**: versioned `.ico` path + shell notify so the brand mark refreshes
- **Updates card**: status well matches Discord/Steam/NVIDIA result panels
- **WebView2 init**: keep host HWND path; XAML fallback remains if runtime fails

## 1.5.7

- **Discord**: lean Equicord profile (minimalism/privacy/QoL) with tuned plugin options; `eagerPatches` forced **off** (blanks Discord 1.0.9245)
- **Discord**: no forced pure-black OpenAsar CSS / `cmdPreset=perf` / HW accel off; AMOLED via Equicord theme only
- **Discord**: force-disable high-overhead convenience plugins (ImageZoom, ViewIcons, CopyUserURLs, CallTimer, etc.)
- **Discord**: preserve healthy Equicord settings on re-run; safer boot/TTI/audio defaults
- **NVIDIA**: Clean Driver+ / display apply / NvDisplay helper improvements; NvCpl scale tooling; profile pack notes (layered on 1.5.0 max-perf path)
- **App**: GitHub update service + script bundle service updates; settings/NVIDIA UI polish; Steam optimizer tweaks

## 1.5.0

- **NVIDIA maximum performance**: replaced fragile Control Panel mouse automation with verified NVAPI/profile operations; added MSI High, profile hash/invariant checks, Full RGB/max-refresh/GPU-scaling verification, and aggressive overlay/telemetry/updater/FrameView/App/GFE background suppression while preserving display audio.
- **Steam no-compromise pack**: added 5-second webhelper working-set reclamation, High idle and Below Normal in-game priority control, aggressive CEF/client/download tuning, deep cache cleanup, fail-closed orphan-only shader cleanup, active-game preflight, durable pre-mutation recovery, and live applied-state verification.
- **Discord no-compromise pack**: added 5-second working-set reclamation, Above Normal priority and thread/raw-input tuning, deep cache and allowlisted module/game-SDK/locale debloat, plus stable-client-scoped Windows suppression, exact captured-state repair, and live full-pack verification.
- **Faster, cleaner app**: current stable Windows App SDK, concurrent dashboard status checks, cached images, cancellation-aware background work, atomic settings/script updates, and safer native-window cleanup.
- **Consistent UI**: accessible navigation, unified button and loading states, a single theme choice, clearer optimizer status, and consistent G-SYNC terminology.
- **No-compromise UX**: made the performance-first policy and tradeoffs explicit on the dashboard, in Settings, and at every apply confirmation; distinguished Discord/Steam Repair from NVIDIA's status-only Reset; added large-text scrolling; and kept card interactions motion-free for reduced-motion compatibility.
- **Release integrity**: verified installer size/SHA-256 metadata, preserved release history by default, added rollback-safe installation, and expanded CI/repository validation.

## 1.2.1

- **NVIDIA**: always check NVIDIA for newest Game Ready; if behind, prompt/launch NVCleanstall + official download
- **NVCleanstall checklist**: unattended express, auto reboot, clean install, disable Ansel, disable installer+driver telemetry, MSI High, disable HDCP, EAC-compatible method, accept unsigned driver
- **Conflict cleanup** for App/GFE/CPL leftovers; Steam/Discord leftover clears on apply
## 1.2.0

- **NVIDIA Optimizer** (live card): auto-detect GPU series, G-SYNC toggle, import Exo Base Profile via Profile Inspector
- Improved public .nip packs for 10/20/30/40/50 series (FPS/latency + series rBAR/DLSS)
- Downloads Profile Inspector + optional NVIDIA App; telemetry task/service trim; display Full RGB / high bpc guidance

## 1.1.8

- **Light mode**: stronger charcoal outlines; dark logo wells so white Steam/Epic marks stay visible
- **About / README**: hub wording for Discord, Steam, and more (not Discord-only)

## 1.1.7

- **Steam**: former aggressive CEF flags are now the only/default launcher (nofriendsui, nointro, etc.)
- **No desktop shortcuts** created for Steam or Discord; removes prior Exo desktop icons
- Start Menu / taskbar still retargeted to Exo launchers

## 1.1.6

- **Steam**: retarget Start Menu, taskbar pins, and Desktop shortcuts to lean launcher (Open Steam from Start apps still gets CEF flags + trim helper)
- **Discord**: retarget Start Menu, taskbar, and Desktop Discord shortcuts to Discord.vbs (-Launch) so OpenASAR/kernel always load
- Ensures canonical Start Menu entries exist for both apps

## 1.1.5

- **Steam aggressive launcher** (optional Desktop shortcut): nofriendsui, nointro, nobigpicture, vrdisable, no-dwrite, cef-disable-breakpad
- **In-game priority yield**: steam + steamwebhelper set BELOW_NORMAL while a game runs; HIGH in library
- **5s webhelper EmptyWorkingSet** idle + in-game (no suspend)
- **Shader pre-cache clean** + multi-library cache paths
- **Overlay/library VDF hints**: quieter overlay noise, no downloads while playing when keys exist
- Lean default launcher remains **Steam (Exo Lean)**

## 1.1.4

- **Steam webhelper**: trim every 5s (DiscOpt cadence), always on idle + in-game
- **In-game**: suspend `steamwebhelper` while a Steam game runs; resume after (overlay may pause)
- Fixed game-detection typo that skipped idle trim logic
- CI: ASCII-only PowerShell scripts (non-ASCII was failing GitHub checks)

## 1.1.3

- Steam performance pack confirm copy / CEF lean webhelper notes

## 1.1.0

- **Steam Optimizer** live: startup quieting, safe cache clean, client config hints
- Universal multi-PC Steam detect / run / repair (no game file injection)
- Discord kit modular lib (from 1.0.42)

## 1.0.42

- Deeper kit split: kit/lib modules
- Universal Equicord profile
























