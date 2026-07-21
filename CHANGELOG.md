## 3.16.11

- **MPO flicker clear**: Apply deletes `OverlayTestMode` / `DisableOverlays` instead of writing OverlayTestMode=5 (stock DWM overlays)
- **Steam detect honesty**: applied status no longer fail-closed on unused memory-guard template; soft reclaim cadence >= 4s
- **Brave vault default-off**: do not delete Login Data unless an explicit risk-ack setting exists (safer cache clears kept)
- **Ui.Smoke**: WebView2 full-bleed shell contract (hex allow MainWindow + ThemeResources, React Shell nav, ContentHost)

## 3.16.10

- **Home dash GPU**: detect **NVIDIA / AMD Radeon / Intel Arc + UHD/Iris** via WMI + registry; prefer discrete dGPU over iGPU
- GPU load: nvidia-smi → amd-smi/rocm-smi → Windows engine counters
- **Internet Wi‑Fi fix**: powercfg Wireless Max Performance (AC+DC) actually applied; PCIe ASPM off; USB SS off; start wlansvc; wifi-only metrics skip
- Internet: leave TCP algorithms adaptive (no forced timestamps/HyStart/Nagle pins); clear legacy ACK folklore when present
- **Brave pack 2.1**: ~150 policies + ~80 verified flags; cookie 1P keep; deeper prefs; quiet background
- Settings gear: Shell header z-index / grid nav overlap fix

## 3.16.9

- **Brave optimizer** (native): absolute debloat — expanded **managed policies** (Chromium + Brave), curated flags, multi-profile prefs, **content filters**, **surgical vault wipe**, Proton Pass force-install, GPU/startup, **Brave services + update tasks**, **safe cache clear**, full prefs snapshot Repair, post-Apply verify pages
- Keeps history/bookmarks; closes Brave on Apply; does not touch Windows/Internet host stack
- **Internet Analyze & Apply** (Web UI) now runs the full connection quality test before apply — home dashboard **Load ↓ / Load ↑ / Loss** fill for every PC (not only idle + DNS)
- Quality sample is persisted to `network-optimizer.json` so the dash shows it after reopen

## 3.16.8

- File Explorer: hide **Home / Gallery / Network** from nav; **Recycle Bin on This PC**, hidden on desktop
- Explorer useful defaults: show hidden files, show extensions, open to This PC, compact mode, less Quick Access noise
- **Long paths enabled** (HKLM when elevated); soft shell refresh (no Explorer kill)
- Settings: **Logs opens the logs folder**; Issues / Buy coffee open **one** browser tab (bridge single-flight + URL dedupe)
- Windows tasks: **never disable Update / Defender / WaaSMedic** trees (community-PC safe)
- Ownership: Steam no longer restamps Windows host latency; detect no longer writes Steam StartupMode
- Riot detect/Apply share multi-drive discovery (uninstall + fixed drives + running processes)
- UI: Apply/Repair locked when target missing; Games **Open store** when not installed
- Honest Windows apply message when elevation still needed for HAGS/MMCSS pack
- **Install/update auto-deps**: SFX + dependency doctor install **.NET 10 Desktop Runtime**, WebView2, PowerShell 7, and VC++ redist (log: `%LocalAppData%\Exo\logs\dependency-doctor.log`)

## 3.16.7

- Settings: **in-app Changelog** sheet (glass UI, version chips + bullets from bundled CHANGELOG.md)
- Settings links simplified to **Logs · Changelog · Report issue · Buy me a coffee** (no Releases button)

## 3.16.6

- **PC-aware law** (`docs/PC-AWARE.md`): community multi-PC — discover this machine live, act only on hits, protect must-keep
- Windows tasks: **enumerate this PC’s Task Scheduler**, classify (protect / quiet / leave), disable only live noise — no blind 150-path firehose
- Steam detect library GPU uses **same multi-library discovery as Apply** (`libraryfolders.vdf` on every PC)
- Empty task folders still purged after quiet

## 3.16.5

- Windows scheduled tasks: expanded quiet list (path fixes for DeviceDirectoryClient, UpdateOrchestrator, Windows AI, AppListBackup, EDP, …)
- Root-folder browser/EQ updaters (Edge/Brave/Google/EqualizerAPO) disabled by name match — never touches cua-driver or shell helpers
- Empty Task Scheduler folders removed bottom-up after quiet (security folders protected)
- Still **disable-only** for Microsoft tasks (never delete task definitions)

## 3.16.4

- Settings: Changelog + Releases buttons; cleaner layout (16px pad, 12px gaps, equal row heights)
- Section labels (Updates / More); shared link chips; coffee CTA aligned with welcome prompt

## 3.16.3

- First-install welcome: Exo is free; optional Buy Me a Coffee (costs real money — even $1 helps)
- Settings → Buy me a coffee; README support badge + link → buymeacoffee.com/UhhErix
- One-time prompt stored in settings (`welcomePromptSeen`); Continue free never blocks the app

## 3.16.2

- Cross-optimizer ownership pass — pick one owner per key-family and stop dual-writes
- **Internet** no longer writes MMCSS / HAGS / Game Mode / Win32 priority (Windows-only); Repair no longer undoes Windows host stack
- Internet detect drops folklore “gaming multimedia” rows that expected NTI=max / resp=0
- **Windows** “No Exo background” requires zero Run companions (yield not greened)
- **Steam** success no longer requires memory-guard template (never launched)
- Launchers: yield restamp language = purge + GPU/DSCP; borderless/FSO ownership from 3.16.1 kept

## 3.16.1

- Fix borderless vs launcher FSO conflict: Games hub owns display (always borderless); Riot/Epic/Steam library no longer stamp FSO-off on game EXEs
- Re-Apply clears legacy `DISABLEDXMAXIMIZEDWINDOWEDMODE` on game paths (exclusive-fullscreen lean)
- Keep high-perf GPU + DSCP; Steam **client** FSO-off unchanged (launcher only)
- Detect: “True fullscreen path” → “Display left to Games hub”; library GPU row no longer requires FSO

## 3.16.0

- No always-on background companions — Riot/Epic yield Run keys removed; Steam launches without memory-guard process
- Optimizers stay one-shot (GPU, FSO, startup quiet, game configs) with zero idle helper tasks
- Detect greys “No background companion” when Run is clean
## 3.15.9

- Riot/Epic: yield companion now minimizes + closes launcher UI ~10s after a game starts (not just priority demote)
- Steam: auto-minimizes main window while a game is running (never kills steam.exe / DRM)
- Deeper ban-safe profiles: more BO7 dvars, League HUD/FX, Fortnite/Pred Engine cuts
- Companion restarts cleanly on Apply; better game process detection
## 3.15.8

- All games: force borderless like Valorant — walk every config path, not just the first file
- UE titles (Fortnite, Pred, Finals, Marvel): FullscreenMode=1 + letterbox off on all GUS copies
- BO7 / League / Apex / CS2 / HD2: hardened display tokens across all player/cfg copies
- Feature row shows live display mode; Apply reminds to fully close the game
## 3.15.7

- Steam: Silent Windows no longer stuck red — re-pins StartupMode Steam rewrites after launch
- Valorant: force borderless on every GameUserSettings.ini; stronger potato quality writes; honest display check
- Close Valorant fully after Apply so display mode sticks
## 3.15.6

- Fix Settings button crash (React hooks order in settings drawer)
- Potato/Optimized toggle now follows each game's last applied profile (no more Optimized flash)
## 3.15.5

- Real free official game logos for every hub title (Steam / Icons8 brand marks — no placeholders or AI)
- Update UI: liquid-glass card, cleaner “what’s new”, no double “Downloading” status text
## 3.15.4

- Call of Duty: retry locked config writes + clear “close the game” error when files are in use
- Potato vs Optimized labeling fixed (no more “optimized installed” for Potato profiles)
- Profile status shows Potato/Optimized correctly after Apply

## 3.15.3

- Always force borderless on every game optimizer (per-game config tokens; no Leave/Exclusive choice)
- Fix false “partial/failed” after Apply (marker-based verify; live quality rows are diagnostics only)
- Apply success toast always fires when write succeeds; restart reminder kept
## 3.15.2

- Fix update install when app folder is locked: in-place overwrite fallback + WebView2 cleanup
- Retries and rename-aside for locked files so Update now can finish
## 3.15.1

- Fix in-app update install: SFX waits for parent PID before replacing app folder (file-in-use race)
- Retry moving locked install directory; stronger process stop
- Update card restyled to match glass shell (white CTA, dark card, TLDR chip)
- Settings update progress uses glass chip bar; install failures surface instead of silent no-op
## 3.15.0

- Games hub multi-title optimizers (BO7, Fortnite, Valorant, League, CS2, Apex, HD2, Finals, Predecessor, Rivals)
- Per-game display preference: Leave / Borderless / Exclusive (game-specific tokens; no silent force)
- Ban-safe quality profiles only; never force display by default
- Not-installed games greyed; Apply/Repair locked; no store install button
- Valorant honest detect when already on competitive lows
## 3.14.2

- **Update popup TLDR**
  - Auto-update and manual check show a plain **What's new** list from the GitHub release notes.
  - Launch check uses the branded **Update available** dialog with version jump + short bullets.
  - Settings panel shows the same summary before download.
## 3.14.1

- **Silent Windows integration (Steam / Riot / Epic)**
  - Pins Windows Startup apps (StartupApproved) + Steam StartupMode=0 on Apply.
  - Live detect shows honest Off reasons when Steam re-enables itself.
  - Toast ID lists aligned between Apply and detect.
- **Discord**: no auto-reopen after Apply (stays closed).
- **Apply progress UI**: step text shown once (not triple-printed).
- **Honesty**
  - Windows Controllers row = USB only (not Game Bar soft OR).
  - Removed duplicate Instant menus row.
  - Windows tasks/optional deep rows require real applyReport proof.
  - Launcher junk cleaned is informational only.
- **Games library**: compact Marvel.exe icon.
## 3.14.0

- **Games hub (Marvel Rivals)**
  - New **Games** module: library rail + Potato / Optimized profiles.
  - Detect Steam install, seed UTOC bypass + IoStore packs, apply Engine.ini profiles.
  - Library uses the real **Marvel.exe** app icon (sharp pre-scaled tiles).
- **Unified optimizer status language**
  - Ready / Applied / Partial / Missing shared across modules.
  - Post-apply live re-verify; expandable last-apply step report when present.
  - Feature counts match the tiles you see (including Partial with real off gaps).
- **Dashboard Verify**
  - Compact **Verify** chip next to `N / N verified` (force live re-detect all modules, no Apply).
  - Removed the huge Verify CTA from Settings.
- **Settings**
  - Classic compact settings popover restored (updates, logs, report).
- **Clean-PC readiness**
  - Bundled Marvel Rivals bypass + pack seeds under `Scripts/Games` for first-run Apply.

## 3.13.13

- **Discord stream quality actually works now**
  - Live diagnosis: FakeNitro was present but **`enabled: false`** — lean rebuilds left it off, and healthy-profile Apply never rewrote it on machines that never re-applied after 3.13.12.
  - **Always pin** FakeNitro (`enableStreamQualityBypass=true`) **and** LimitlessScreenshare (res/FPS sliders up to 1440p/60) on every Equicord profile write — both healthy preserve and lean rebuild paths.
  - Fixed lean budget check only enforcing on rebuilds (healthy profiles no longer risk a post-write throw).
  - Lean allowlist includes both plugins (max enabled raised to 36).
## 3.13.12

- **Discord FakeNitro (Nitro-quality stream without Nitro)**
  - `FakeNitro` is now on the lean essentials allowlist so Apply keeps it enabled.
  - Stream quality bypass is always pinned (`enableStreamQualityBypass=true`) — screenshare at Nitro resolutions/FPS without a Nitro subscription.
  - Full FakeNitro settings remain in `equicord-overrides.json` (emoji/sticker bypass + stream quality).
  - Healthy-profile Apply no longer leaves FakeNitro off after a lean rebuild.

## 3.13.11

- **Lock-in: every Windows tweak runs, nothing hangs**
  - Full native apply covers **every card row** (Game Mode, HAGS, host latency, USB, tasks, DISM, Defender policy, power plan, AI, UAC, etc.).
  - **No skipped competitive tweaks** — expanded scheduled-task list (~90) + expanded DISM feature list (~18).
  - **Never hang**: every `dism.exe` / `schtasks.exe` call uses hard kill timeouts + total budgets (90s tasks / 120s DISM).
  - Removed hang APIs: no `Get-WindowsOptionalFeature`, no full `Get-ScheduledTask` catalog.
  - PS libs for optional features / tasks rewritten to the same timeout-safe pattern if Repair/depth ever calls them.

## 3.13.10

- **Windows stuck at 77% fixed**: Experimental deep pack hung on DISM optional features (`Get-WindowsOptionalFeature` / `DismHost`). Windows Apply is now **always native-only** (including Experimental). PS optional-feature DISM is a no-op; native already runs a bounded `dism.exe` shortlist.

## 3.13.9

- **Apply pipeline rewrite (all optimizers)** — no more “native then always force full elevated PS kit”:
  | Module | Apply path |
  |---|---|
  | **Windows** | Native C# only (default). Experimental optional depth pack soft-fails. |
  | **Riot / Epic** | Native C# only (PS was duplicate + stripped yield). Repair still PS. |
  | **Steam** | Native C# primary; PS deep pack soft-fails if native OK. |
  | **Internet** | C# NetworkOptimizerService + MS-safe SR/NTI=10 restamp. |
  | **Discord / NVIDIA** | Specialized PowerShell kits (unchanged depth). |
- Windows PS deep pack is **depth-only** (tasks / optional features / policy Defender) — skips re-running host glue when `path=native-csharp`.
- Deep pack failure no longer fails the whole Apply when native essentials succeeded.

## 3.13.8

- **Windows Defender purge hang fixed**: deep pack froze on `Set-MpPreference` / `MpCmdRun -RemoveDefinitions` / `Stop-Service WinDefend` / Appx removal under Tamper Protection. Defender path is now **policy-first** (registry + bounded `sc config` + known task paths only) and finishes in &lt;1s. Test is policy pin only (service-dead was dishonest under TP).
- **Yield companions no longer stripped by Windows Apply**: `Unregister-ExoBackground` treated Hidden PowerShell `yield-guard` as noise and deleted Riot/Epic Run keys during Windows deep pack. Silent companions are kept; wscript/WindowsApps stubs still purged.
- **LiveYield detect honesty**: helper-without-Run-key is FAIL (not green “purged”).
- **Internet host-policy report**: no more folklore `resp=0` / `NTI=max` text — writes and reports **10/10**.
- Live PC verification (registry/powercfg/netsh, not logs): core Windows/Internet/Steam/Discord/Riot/Epic/NVIDIA knobs **PASS** on machine.

## 3.13.7

- **Windows deep pack crash fixed**: native C# foundation wrote `windows-optimizer.json` without a `recovery` property; StrictMode then threw *“The property 'recovery' cannot be found”* during snapshot capture and aborted the entire PS deep pack (~4s FAIL). Safe property accessor; re-Apply now completes the full deep pack.
- **Games MMCSS typo fixed**: `Set-ExoHostLatencyProfile` used `$game` instead of `$games` — Tasks\Games keys never wrote on the PS path.
- **SystemResponsiveness audit**: MS clamps values &lt;10 to **20** (stock). Exo pin is **10** everywhere (Windows + Internet). Removed folklore `0` / `NetworkThrottlingIndex=ffffffff`. Detect requires `PowerThrottlingOff=1` + `SystemResponsiveness=10`.

## 3.13.6

- **Windows Apply actually starts**: Windows scripts were missing from the signed script manifest → deep pack died with “not present in signed script manifest.” Full 95-entry manifest regenerated (includes `Windows/*`).
- **Riot/Epic yield companion**: Run key now verified after write; no longer strips good `-File yield-guard` entries; re-stamped after deep pack. Detect accepts hidden PowerShell yield (not WSH).
- **Host latency detect**: accepts `SystemResponsiveness` 0–20 (Internet competitive uses 0; Windows uses 10).
- **No Exo background detect**: hidden yield Run keys are allowed (were false-failing Windows when Riot/Epic yield installed).
- Live detect: Windows / Steam / Riot / Epic all `isApplied=true` after fix.

## 3.13.5

- **Detailed apply logs**: every Apply/Repair writes `%LocalAppData%\Exo\logs\apply-{module}-latest.log` (plus timestamped copy) with native steps, progress, elevated PS full output, and stack traces on failure. UI errors include the full log path. Settings → Open Logs opens the newest apply log.
- **Riot deep-pack fix**: StrictMode crash on uninstall keys missing `DisplayName` (`Get-UninstallDisplayText` safe accessor).

## 3.13.4

- **Riot/Epic Windows Script Host crash fixed**: yield companion no longer uses `wscript` or the WindowsApps `pwsh` stub (that was the WSH error popup). Uses real PowerShell/pwsh with `-WindowStyle Hidden` + quoted `-File` only; Apply never launches WSH.
- Broken `Exo-Riot-Yield` / `Exo-Epic-Yield` Run keys are stripped and rewritten on next Apply.

## 3.13.3

- **Hybrid Apply (Discord-class depth)**: Steam / Windows / Riot / Epic no longer stop at instant C# registry writes. Apply now runs:
  1. Native foundation (0–28%) — reliable registry/files
  2. Full elevated PowerShell deep pack (28–96%) — same kit depth as Discord (cache cleans, full optimizers, progress bar)
  3. Live detect after
- Progress bar shows real EXO_PROGRESS from the deep pack (not a 0.5s flash).
- Power plan PS path no longer double-`duplicatescheme` Ultimate (spam fix).

## 3.13.2

- **Honest live detect** for Steam / Windows / Riot / Epic (C# registry/file/powercfg probes — no soft marker greens).
- **Power plan fixed**: Apply creates/activates **Exo Competitive AMD/Intel** (not Ultimate spam). Purges duplicate Ultimate Performance clones. Live detect requires Exo plan active by name.
- **Epic Rocket League**: manifest launches `Launcher.exe` — also pins real `RocketLeague.exe` to `GpuPreference=2;` (was stuck on `AppStatus=0`).
- **Windows deep passes**: scheduled-task shortlist + bounded DISM optional-feature shortlist, recorded only after they actually run.
- Verified live: all card rows ON; active scheme Exo Competitive AMD; 5 plans left (Exo + Balanced + High + Saver + Nexus).

## 3.13.1

- **Partial-apply fix**: native Apply now covers every detect checklist row so cards go fully green.
  - Steam: `GPUAccelWebViewsV3`, full `Test-SteamApplyRecord` markers, config.vdf download keys, localconfig tweaks, locked memory-guard rewrite retries, library policy verified flag.
  - Windows: input pack, AMOLED, UAC, AI, inbox, WU pause, Defender policy, explorer declutter, scheduled-tasks/optional-features markers.
  - Riot/Epic: silent yield companion (or honest purge), snapshot, shellQuiet, cache clean, Start Menu quiet cmd; detect no longer dies on missing `lastError`.
- Live detect after apply: Steam / Windows / Riot / Epic all `isApplied=true` with zero checkable OFF rows.

## 3.13.0

- **Native C# apply path (best path that won’t fail)**: Steam, Windows, Riot, and Epic Apply no longer depend on the full PowerShell optimizer kits. Registry, GPU/FSO, CEF launcher, memory-guard template, Game Mode/HAGS/Game Bar, and DSCP write through pure C# (`NativeReg` + module native apply). One compact elevated reg pack only when HKLM needs admin — no lib imports, no apostrophe breakage, no DISM hangs on apply.
- **Steam**: memory guard template passes shared classifier; CEF launcher (`Steam-Exo.cmd`), library GPU/FSO (37 games on smoke), client DSCP, startup/toasts/tray — live smoke PASS.
- **Windows**: Game Bar quiet, Game Mode, HAGS, Win32 priority, mouse precision, sticky keys, menu delay, host latency, MPO, explorer quiet, power plan — live smoke PASS.
- **Riot / Epic**: game high-perf GPU + FSO off, launcher quiet, shell toasts, per-game DSCP — live smoke PASS.
- **Internet** stays C# network stack; **Discord** Equicord kit and **NVIDIA** NPI/DRS remain specialized PS/native hybrids. Repair still uses snapshot PS kits.

## 3.7.2

- **Self-contained ship guard**: publish fails if `Exo.runtimeconfig.json` is framework-dependent (missing `includedFrameworks`) so users are never prompted to install .NET 10 for a “broken” copy.
- **NVIDIA display prefs**: Apply no longer forces Control Panel scaling / Full RGB / NVIDIA color (use the Control Panel button). **Profile Inspector DRS packs stay**.
- **Brand**: new monochrome Exo app icon/logo; README hero screenshot updated to the glass home UI.

## 3.7.1

- **In-app updates actually install**: Settings → Check for updates no longer only reports a new version — it downloads and quiet-installs with an **in-settings progress bar** (no native dialog card).
- **Update hosts**: allowlisted GitHub CDN download hosts so asset redirects are not blocked.
- **Long download window**: WebView host timeout for updates raised to 30 minutes for multi‑gig/slow links.

## 3.7.0

- **WebView glass shell**: primary UI is WinUI 3 + WebView2 with a React/TypeScript/Tailwind liquid-glass surface — centered optimizer nav, in-bar min/close, solid glass panels (no fake transparency), Home meters, and module pages with Stable / Experimental modes.
- **Home network card**: hero is negotiated **link rate** (e.g. 2.5G); mini stats for Idle, Load ↓, Load ↑, Loss, DNS, and Rating from the last Internet quality sample (no “Link capacity” / media / prose noise).
- **Module load UX**: features stay on a skeleton until detect finishes — no half-list flash of toggle-only rows while Apply/Repair stay locked.
- **Discord preserve**: Stable Apply merges host/chromium flags only; does not rewrite in-app audio, reduced motion, or Discord notification prefs. Windows quiet (OS toasts / Run key / tasks / tray) stays on. Single end-of-Apply Discord open (no thrash relaunch).
- **Internet host policy**: Stable stamps full safe stack (NTI/Responsiveness/Games MMCSS/Psched); Experimental force re-stamps. Multi-gig keeps throughput; loaded queueing is reported honestly.
- **Hybrid GPU**: Riot/Epic launchers prefer iGPU when a discrete GPU exists; games stay high-performance. NVIDIA remains SafePolicy with explicit G-SYNC vs raw latency.
- **Ship path**: embedded `wwwroot` assets + script SHA manifest; Discord/Network/UI smoke gates updated for preserve + chromium-only variant detect.

## 3.6.2

- **True-black AMOLED shell**: page background is pure black with quieter hairlines, denser module plates, and 76px capability tiles so more evidence fits without marketing fluff.
- **Internet RSS honesty**: NICs without an RSS WMI surface report soft-ok instead of a false open failure row; status filters informational N/A lines.
- **Steam / Discord / launchers memory**: background soft reclaim (never EmptyWorkingSet on Steam CEF), Discord 2.5s idle trim, and Windows-only GPU/FSO/yield for Riot and Epic.
- **NVIDIA packaging**: ships framework-dependent `Exo.NvDisplay` (~0.7 MB with NvAPIWrapper) including exact DRS backup/restore for Repair; Publish-Exo rejects accidental 70 MB single-file bloat.
- **Cua install-path QA**: re-snapshot-before-click nav gate with module landing markers so automated screenshots cannot silently stay on Home.

## 3.6.1

- **Signal-deck UI rebuild**: replaces clipped icon-only chrome with labeled, stable Home/module/Settings navigation; reshapes the dashboard into a compact 3×2 status grid; gives module capabilities a responsive two-column layout; and keeps actions in normal document flow instead of pinning them below empty space.
- **Settings and motion cleanup**: the opaque settings panel now has a clear update/support hierarchy, navigation no longer waits on a decorative select pulse, and page motion is a short XAML-only crossfade that returns every element to layout-owned pixels.
- **Release XAML hardening**: publishing now cleans generated WinUI XBF/connector artifacts before compiling, preventing stale named-control maps from producing a green build that crashes at launch after shell edits.
- **Navigation regression fixed**: Discord remains in the centered module row and Settings now owns the native right-header slot, eliminating the visual collision that hid Discord.
- **Internet policy rebuilt**: keeps adaptive Windows TCP/IP behavior and removes unrelated SMB, NetBIOS, tunnel, power-plan, scheduler, QoS-reservation, dynamic-port, and undocumented timing mutations. NIC capability tuning, DNS benchmarking/DoH, metrics, RSS/offloads, rollback, and exact Repair remain.
- **NVIDIA status cleaned up**: current installs always use the reversible safe profile contract; retired driver stripping, service debloat, display mutation, and duplicate DRS requirements no longer leak into detection or cards.
- **Riot and Epic hardware policy**: detected games use the high-performance GPU without forced undocumented IFEO CPU priority. Hybrid systems route launcher UI to the integrated GPU; single-GPU PCs stay on Windows automatic selection.
- **Steam hybrid routing**: Steam client/CEF uses the integrated GPU on hybrid systems while games retain the discrete GPU, with exact snapshot/Repair. The contention guard continues to avoid unsafe webhelper trims, caps, suspension, or kills.
- **Clearer Discord evidence**: the plugin card separates curated features from required APIs instead of presenting dependencies as optional bloat.

## 3.6.0

- **Adaptive six-module dashboard**: Riot and Epic are real optimizer pages beside Internet, NVIDIA, Discord, and Steam, with capability-aware detection, concise evidence, Apply, and exact Repair.
- **Safe game-launcher policy**: Riot/Epic changes are limited to reversible startup, Windows GPU preference, and Above Normal CPU priority for detected game executables. Anti-cheat, services, client files, manifests, saves, caches, and active games are never touched.
- **NVIDIA safe policy**: Apply snapshots the full DRS database, leaves driver components/services/display state unchanged by default, makes G-SYNC explicit, and restores the exact pre-Exo profile database on Repair.
- **Multi-gig Internet policy**: 1+ GbE preserves RSS, RSC, LSO, and normal receive-window autotuning. Loaded queueing is reported as router/ONT behavior instead of “fixed” with throughput-cutting host tweaks; DNS is selected from live Cloudflare/Google/Quad9 tests.
- **Steam foreground protection**: visible Steam stays Normal priority; only background web helpers get low memory priority and EcoQoS while a game is running. All helpers return to HighQoS/normal memory priority afterward.
- **Honest memory evidence**: dashboard cards distinguish resident working set from private committed bytes and no longer attribute a normal peak-to-current drop to Exo.
- **Dark-only responsive shell**: one opaque dark visual system, resizable/maximizable layout, native title bar, crisp shared module plates, white Apply actions, and a decluttered Settings sheet.
- **Security and release hardening**: shipped scripts are compiled into a SHA-256 manifest and re-verified across elevation; privileged results use protected machine-owned transactions; downloads require HTTPS plus published digests.

## 3.5.2

- **DNS cache TTL folklore removed**: Apply no longer writes `Dnscache MaxCacheTtl=86400` (records pinned up to 24h → stale DNS). Any leftover override is removed on Apply and never restored by Repair.
- **Offline rescue restores DNS**: [`Repair-Internet.ps1`](Repair-Internet.ps1) now restores per-adapter DNS servers from the snapshot (matching in-app Repair) instead of leaving pinned resolvers behind.
- **Repo gate green**: UiPreview tsconfigs are strict JSON again (integrity check passes with 0 issues).
- **Explicit G-SYNC / VRR control**: restores an NVIDIA-only toggle and defaults to the raw-latency profile when it is off. High-refresh DisplayPort and EDID range remain useful capability hints, but Exo no longer assumes the monitor's physical adaptive-sync setting is enabled.
- **Verified NVIDIA latency policy**: keeps global Ultra Low Latency for non-Reflex games because NVIDIA Reflex takes priority automatically when a supported game enables it. Toggle-off now disables driver G-SYNC, VSync, and the OS VRR override; all required pins are verified from the live driver export.
- **Truthful Internet loss**: packet loss now comes only from the idle 24-sample series. Missed ICMP replies while Exo intentionally saturates download/upload are excluded, and full-load download versus upload latency is labeled explicitly.
- **DNS status repair**: uses Microsoft's supported DNS client APIs with the correct `netsh dnsclient` fallback, verifies automatic DoH before claiming it is active, and keeps the dashboard concise when the current Windows build cannot register encrypted DNS.
- **Internet UI clarity**: restores four concise explanation cards for connection routing, adaptive tuning, DNS privacy, and exact-state Repair. Primary Apply buttons are white again while Settings remains dark-only.

## 3.5.0

- **Reliable connection quality**: latency, jitter, and loss now come from sequential ICMP samples to one automatically selected healthy target. Loaded latency runs independently from the Cloudflare throughput streams, so client/server scheduling is no longer misreported as internet jitter.
- **Adaptive NVIDIA hardware policy**: detects GPU series, desktop/laptop path, active NVIDIA displays, primary connection, current/max refresh, and EDID refresh range before selecting the raw-latency or G-SYNC/VRR pack. The primary uses its highest refresh while secondary displays keep their current mode.
- **Truthful Steam background policy**: foreground Steam CEF remains Normal priority while only background renderers yield during games. The dashboard reports private bytes and process count instead of implying the safe priority policy forcibly removed RAM.
- **Dark-only interface**: removes the light palette, theme setting, toggle logic, and dead theme-choice style. The settings sheet and shell now use one consistent black, opaque, mixed-DPI-safe visual system.
- **Decluttered module controls**: optimizer pages expose Apply and Repair only; Refresh remains an internal post-action operation. Internet results collapse to one useful quality line, and NVIDIA shows its detected hardware policy instead of a manual G-SYNC switch.

## 3.4.0

- **Accurate connection analysis**: replaces the small PowerShell byte-array test with a sustained native streaming test that scales to 12 parallel streams, samples idle and loaded latency, reports endpoint-limited results honestly, and includes the negotiated link rate for multi-gig Ethernet.
- **Automatic encrypted DNS**: removes the DNS toggle and benchmarks Cloudflare, Google, and Quad9 directly on the current route. Analyze & Apply selects the fastest healthy resolver, registers its Windows DoH template, verifies the result, and leaves Repair able to restore the exact prior DNS state.
- **One network decision**: removes the Low latency / Highest download choice. Exo now analyzes stability, loss, loaded latency, media, and link capacity, then applies one combined policy suited to the measured connection.
- **Steam memory guard**: background `steamwebhelper` processes receive low Windows memory priority so their pages are reclaimed first under pressure, while foreground Steam remains normal and games keep CPU priority. Unsafe working-set trims, hard caps, suspension, and process killing remain prohibited.
- **NVIDIA cleanup**: audio removal and driver-package debloat each run once. The obsolete tray-icon manipulation, duplicate stages, and unreachable custom display panel are removed; the optimizer opens NVIDIA Control Panel directly.
- **Outcome dashboard**: replaces raw-stat tiles with modern cards that state what each optimizer changed, whether its apply record is verified, and one clearly separated live signal. Network samples and process memory are no longer presented as causal performance gains.

## 3.3.0

- **Connection Lab**: an explicit adaptive Cloudflare-edge test ramps download and upload sizes and measures idle/loaded latency, jitter, DNS, and sampled packet loss before choosing the low-latency or throughput profile. It reports data use and flags router-side bufferbloat instead of claiming Windows can fix it.
- **Steam background memory**: enables Steam's low-performance/low-bandwidth library modes, restores Chromium hardware acceleration, and uses Normal idle priority with Below Normal in-game yield. Unsafe working-set trimming and helper killing remain prohibited.
- **NVIDIA profiles**: Control Panel opens directly from the optimizer; hidden global rBAR, DLSS/Frame Generation, RT, CUDA-memory, and Vulkan-present overrides are pruned because their optimum is game/driver-specific. Documented performance, sync, refresh, queue, and per-game controls remain verified.
- **Dashboard and motion**: decluttered live instrument layout, four consistent signal cards, crisp fixed-scale content, opaque Settings, and quicker direct Fluent-style reveals. The stale display panel and coming-soon strip are removed from the active UI.
- **Validation**: expanded Network/Steam/UI contract tests, a live 437/291 Mbps Connection Lab run, production WinUI build, and updated browser preview click/capture coverage.

## 3.2.0

- **Internet NIC policy**: adaptive, preset-specific RSS placement now applies supported processor and queue budgets; D0 packet coalescing is disabled when the driver exposes it. Snapshot v2 captures adapter power and extended RSS state for exact Repair.
- **NVIDIA latency policy**: G-SYNC packs now combine driver VSync with Ultra Low Latency for non-Reflex DX9/DX11 games, while NVIDIA Reflex remains authoritative in supported titles. VSync joins the required live DRS drift pins.
- **Discord minimalism**: Apply enforces a dependency-aware Equicord plugin budget instead of preserving an unlimited old profile; the card reports the live enabled count and rejects optional-plugin drift.
- **Steam contention guard**: Steam and its CEF helpers yield CPU priority while a game is active and restore responsive idle priorities afterward, without unsafe working-set trimming or process kills.
- **Honest optimizer cards**: detector explanations now survive the shared state parser, so cards show the actual policy, hardware gate, and drift detail instead of replacing everything with “Applied.” Retired Steam reclaim statistics and stale background-reclaim claims are no longer shown.
- **Cohesive dark-mode frontend**: native Segoe Variable typography, opaque layered surfaces, quieter dividers, readable feature rows, branded dashboard rails, a live memory meter, and a structured solid settings control panel replace the tiny translucent mixed-material UI.

## 3.1.0

- **Lean current runtime**: moved from the full Windows App SDK bundle to the released WinUI/runtime components, removing unused AI/ML, ONNX, DirectML, Widgets, and DWrite payloads; the local installer fell from **134.0 MB to 113.6 MB** (about 15%) while retaining faster ReadyToRun startup.
- **Private, quiet startup**: opening Exo no longer copies optimizer kits, installs PowerShell, rewrites Start Menu shortcuts, or starts a dependency doctor. PowerShell preparation begins only after an explicit **Apply** or **Repair**.
- **Lower background use**: dashboard counters refresh every 5 seconds only while visible, and storyboard fallbacks use the UI dispatcher instead of thread-pool sleepers.
- **AOT-ready persistence**: source-generated JSON metadata, WinRT-compatible converter declarations, reflection-free task inspection, and warning-clean Native AOT analysis (shipping remains on the verified self-contained WinUI path).
- **Discord reliability**: fixed null apply-report initialization that broke the GitHub Actions Discord end-to-end Apply path, with a dedicated regression smoke fixture.
- **GitHub polish**: current product screenshot, clearer install/privacy documentation, and versioned optimizer kits that ship with the app instead of silently refreshing from source.

## 3.0.11

- **Dashboard**: full-width System RAM hero; Discord/Steam tiles show **RAM reclaimed** (Discord peak−live + Steam companion trim) with 2s background refresh; Internet shows real before→after **ping · jitter · DNS** plus live link speed; NVIDIA keeps Max FPS NIP
- **Internet**: Ethernet metric retry (no more false skip when link flickers); DNS ServiceProvider restored to Windows defaults; LowestLatency sets dual-stack Cloudflare DNS (fixes ~1s ISP IPv6 DNS hangs)
- **Discord**: Equilotl install path fixed (Discord root not LocalAppData); applied=true only when Equicord loader + kernel are really on disk
- **Steam**: companion no longer EmptyWorkingSet on steamwebhelper (was freezing/killing CEF)
- **NVIDIA**: tray hide skips already-hidden icons; display status StrictMode-safe; always-latest Profile Inspector from GitHub Latest
## 3.0.10

- **Always-latest tools**: NVIDIA Profile Inspector is no longer hard-pinned - every Apply resolves Orbmu2k **GitHub Latest**, verifies the asset SHA when GitHub publishes it, and only reuses cache when tag matches Latest. Offline falls back to last good managed install.
- NVIDIA App installer URL prefers the live product-page newest link (CDN pins are fallback only)
## 3.0.9

- **NVIDIA Profile Inspector**: pin bumped **v3.0.1.11 -> v3.0.2.1** (current Orbmu2k release). Old managed NPI could flash WPF/XAML UI on import; silent import hardened + leftover .nip cleanup
## 3.0.8

- **Discord opens after Apply**: elevated Apply now user-token boot-checks via explorer; if Equicord stub dies, restores stock app.asar automatically so Start Menu works
- **NVIDIA profiles**: NIP import already silent (exit 0); temp profile files deleted after import - do not open .nip in a browser (UTF-16 XML, not a document)
## 3.0.7

- **NVIDIA Apply works**: StrictMode crash on missing RebootRequired fixed; MSI residual is soft; in-place tweaks continue into profiles/display instead of failing the whole pass
- **Apply report honesty**: already-correct Steam stages report ok (not skip); Discord elevated kernel/boot report ok launch-safe so the UI shows green applied instead of "3 skip"
## 3.0.6

- **Steam Apply crash**: skip-if-verified startup path no longer leaves $startupResult unset (was failing every re-Apply when startup already quiet)
- **Discord Apply fail**: optional module residuals (e.g. discord_hook) are re-stripped before verify and soft-skip if Discord re-pulls them — no longer fails a successful Equicord/quiet Apply
- **Debloat detect**: optional modules only count when they still have payload files (empty dirs ignored)
## 3.0.5

- **Discord launch fix**: elevated Exo Apply no longer leaves DiscOpt kernel (version.dll + ffmpeg proxy) active without a boot check — kernel is skipped/disarmed so Discord opens from Start Menu; Equicord stub without asar restored to stock
- **Steam webhelper**: removed -cef-disable-gpu flags that blank modern CEF; gentler 6s trim only on large working sets; steamwebhelper stays Normal priority
- **Skip-if-verified**: Steam debloat/startup/quiet skip when already live-true; Discord kernel skip when on disk (non-elevated); NVIDIA profile re-import skip when same pack DRS-verified
- **Dashboard**: System RAM + Steam reclaim heroes; Discord/Steam/Internet/NVIDIA status tiles with real Apply state
## 3.0.4

- **NVIDIA Apply**: MSI High no longer hard-fails when PCI nodes lack Class under StrictMode; successful clean driver continues into profiles/display (soft MSI skip)
- **NVIDIA UI**: stabilize shell after elevate/driver work; panel open failures no longer blank the chrome; safer feature-list entrance after Apply
## 3.0.3

- **Shell**: left circle Settings on home / Home on module pages; module icons true-centered; full 44px circle hit target (SelectionFill)
- **Dashboard metrics**: NVIDIA pack + Discord apply + Steam cache free + live system RAM + Internet status/benchmark; 2s memory refresh
- **Skills**: expanded .agents/skills library (community + design packs) for craft guidance
## 3.0.2

- **Launch harden**: freeze entrance motion until first pixel; defer SetTitleBar, home navigate, shortcut repair, and auto-update until after first frame / Activate (stops cold-boot flicker-close when previous session never painted)
- Sticky safe-mode still keeps motion off for the whole session if the prior run died before first frame

## 3.0.1

- **Discord login detect**: false "not logged in" fixed (Local Storage / userDataCache without old IndexedDB path)
- **Shell chrome**: Settings on the **left** (old EXO slot); **Home** pill when away from dashboard; CaptionSpacer keeps chrome clear of Windows min/close; NavRail is `SetTitleBar` for drag
- **Internet safety**: still fail-closed (no Wi-Fi disable, no Client/LLDP kill, no auto winsock); **removes** MaxUserPort/static RWIN/chimney/LargeSystemCache if present; never writes folklore brick keys
- **Steam thin libs**: `Steam.Paths.ps1` + Bootstrap stage IDs only (no new aggressive god-script stages)
- **Repair**: `Repair-Internet.ps1` also under Documents\\Exo for offline rescue

## 3.0.0

- **SharedModulePlate**: all four optimizer pages (Internet / Discord / Steam / NVIDIA) host chrome via one instrument plate (header, advisor, features, sticky foot, apply report)
- **Network builder split**: `NetworkApplyScriptBuilder` partials — `.Repair.cs` + `.Benchmark.cs` (smoke-linked)
- **Detect = Apply contracts**: `tools/Contracts.Smoke` gates required/forbidden markers + detect/apply concept pairs for all modules
- **Thin Steam/NVIDIA stage libs**: `Steam/lib/Steam.Bootstrap.ps1` + `Nvidia/lib/Nvidia.Bootstrap.ps1` (stage IDs; god-scripts remain with documented size exception)
- **CI / Release**: Contracts.Smoke in validate-before-publish
- **Stack**: .NET 10 + Windows App SDK 2.2; Advisor v2 + no Exo background footprint retained from 2.7.x

## 2.7.1

- **Shared script platform**: `Exo/Scripts/lib/Exo.Common.ps1` (PS7 assert, run logs) + `Exo.NoBackground.ps1` (purge Exo tasks/Run keys); wired into Discord/Steam/NVIDIA Run wrappers
- **Advisor v2**: CTA-first guidance with missing features + last-apply fail steps (`OptimizerAdvisor.BuildV2`)
- **Publish**: hard-fail if `Exo.NvDisplay.exe` or shared libs missing after build

## 2.7.0

- **Release trust**: GitHub Release workflow runs build + repository integrity + all five smokes before publishing Exo.exe (no untested ship)
- **Steam honesty**: first-run VDF soft-skip no longer sets full `applied`; core pack can complete with incomplete status until Steam is opened once then Reapply
- **Discord elevated Apply**: quiet path disarms half-kernel (version.dll without valid ffmpeg proxy) so non-admin launch stays stock-safe; boot-check reported honestly as skip under elevated host
- **Internet UI contract**: smoke locks QoS+IP bindings label and Wi-Fi-while-Ethernet never hard-fails for "still up"
- **No Exo scheduled tasks**: Test-Repository fails if any `Exo/Scripts` file creates Exo-* tasks

## 2.6.8

- **No Exo background footprint**: never register logon/scheduled tasks (removed `Exo-NvidiaTrayHide`); purge all leftover `Exo-*` tasks on Apply/tray clear; no Exo Run-key startup or Exo services
- **NVIDIA display works**: NVAPI apply retries 3x; registry success counts as applied; Display-Apply no longer exits "partial fail"; optimizer retries display before failing
- **Live advisor UI**: every optimizer page shows real-time next-step guidance from detect (what is missing + what to click) via `OptimizerAdvisor`
- **Product rule** (AGENTS): Apply must work — not report honest failure as the feature; never install Exo background tasks

## 2.6.7

- **Internet honesty**: detect/UI match fail-closed Apply — bindings only require QoS+IPv4/IPv6 on (no more permanent "Client/LLDP off" fail); PreferEthernet defaults false; "Wi‑Fi while Ethernet" never fails for Wi‑Fi staying up
- **NVIDIA partial success**: profiles+DRS verified but display NVAPI incomplete → save honest partial state and **exit 0** (shell shows completed; display detail still surfaces) instead of fake full failure
- **Docs / launcher**: INTERNET-GOLDEN-PATH path policy + bindings table corrected; `Run-Exo.ps1` finds net10 TFM outputs
- **Stack**: build/run on **.NET 10** SDK (`net10.0-windows10.0.26100.0` + Windows App SDK 2.2)

## 2.6.6

- **UI**: loader is pure XAML Storyboards only (no composition API) - crash-loop "safe mode" is no longer required for the spinner
- **Internet fail-closed**: never disables Wi-Fi adapters; never disables Client/LLDP bindings; never writes NCSI/proxy AutoDetect; never forces Speed & Duplex; Apply defaults skip NIC restart; Repair no longer auto-runs winsock/ip reset (explicit `-Hard` only)
- **NVIDIA**: when profiles import but display NVAPI fails, save honest partial state (profiles applied, display incomplete) instead of looking like a hard profile failure
- **Discord**: Krisp/module CDN failures soft-skip; ffmpeg proxy keeps stock on mismatch; Launch heal verifies/rolls back kernel
- **Steam**: fresh installs no longer fail Apply when config/userdata are absent; taskbar pins stay on steam.exe; steam.cfg merges; Repair restores quiet shell (tasks/toasts/tray/App Paths)

## 2.6.5

- **Safety audit**: NVIDIA Apply now persists failing stage/reason, records displayPrefs only when NVAPI verifies (not registry-only), saves success state only after post-verify, and passes `-NoTask` so tray clear cannot register a logon task. Reset banner says status cleared only
- **Discord**: ASAR/resource writes keep a verified `.exo-bak` until the replacement lands; Repair downloads+signature-verifies the installer before deleting Discord files
- **UI**: crash-loop safe mode now covers `ExoLoader` (no composition when previous launch died before first frame); loader drops composition Scale/Opacity writes; window drag uses the full NavRail; feature-list overlay scrollbar stops right-edge clipping

## 2.6.4

- **CRITICAL Internet rescue**: post-apply auto-rollback now does a **full snapshot restore** (registry, NIC advanced props, bindings, TCP, metrics) instead of Wi-Fi/metrics-only — the old path left host-stack tweaks applied and could leave the box offline
- **Repair actually applies**: advanced props are restored then the NIC is restarted (`-NoRestart` alone was a silent no-op); every disabled physical adapter is re-enabled; `ms_tcpip` / `ms_tcpip6` / `ms_pacer` are force-enabled
- **Hard fallback**: if still offline after Repair → `netsh winsock reset` + `netsh int ip/ipv6 reset` (exit 2, reboot required). Standalone `Repair-Internet.ps1` gains `-Hard` plus an offline emergency paste block

## 2.6.3

- **Launch crash root cause fixed**: `ExoMotion` wrote hand-off composition visual properties (`Visual.Offset`/`Scale`) around every entrance — that detaches elements from XAML layout (v2.6.2 safe mode piled the whole home page at the top-left) and poking those visuals before first frame fails fast with `0xC000027B` on real GPUs (the v2.6.0/2.6.1 flash-and-close). All composition-visual writes removed; XAML layout owns positions, storyboards own motion. Window drag region works again
- `Ui.Smoke` now gates that `ExoMotion` never touches composition visuals

## 2.6.2

- **Crash-loop safe mode**: when a launch dies before presenting a frame (v2.6.0/2.6.1 black-flash regression), the next launch automatically disables all entrance motion — composition-animation failures can no longer brick startup twice in a row
- **First-frame proof**: `startup.log` now records `first-frame-rendered` (via `CompositionTarget.Rendered`), separating "window activated" from "pixels actually presented" in crash reports

## 2.6.1

- **CRITICAL launch fix**: v2.6.0 died at startup with `0xC000027B` (composition failure) on real GPUs — the settings-flyout `AcrylicBrush` is created while `MainWindow` parses, and the acrylic composition object can fail before first frame. Replaced with solid near-opaque sheet brushes (visually equivalent to the old tint + fallback)
- **Startup breadcrumbs**: `%LocalAppData%\Exo\logs\startup.log` marks each launch phase (main → resources → window → home → activated) so silent native crashes are diagnosable

## 2.6.0

- **UI — Exo Instrument**: full-width top bar (EXO · icon modules · Settings) with content filling the frame below; one edge-glass module plate
- **UI — home dashboard**: four metrics — FPS gain, frame time, RAM reclaimed, latency (FPS empty until capture ships)
- **UI — liquid-glass circle nav**: floating glass circles on pure black (hairline rim, dark center, soft shadow, hover sweep + sibling fade + label pill in preview); no bar plate
- **UI — top bar polish**: equal end caps; EXO hidden on home; EXO text optically centered when shown; Settings alignment fixed
- **UI — Steam mark**: keep the real Steam piston mark in the top bar (no custom glyph)
- **UI — one system**: feature rows = status rail + Applied/Not applied only; primary = solid accent; secondary = quiet glass
- **UI — denser chrome**: tighter top bar / plate / action foot; dropped unused preview chips/strips
- **UI — motion polish**: short ease-out storyboards; rail selection = wash + accent ring (no logo scale)
- **Preview — agent instrument mock**: `tools/Exo.UiPreview` tracks the same layout language for Linux click QA — not shipping Apply

## 2.5.2

- **Detect / smoke hardening**: `Ui.Smoke` runs cleanly off-Windows (`net10.0`; logo ink measure stays Windows-only); `DiscordLogic.IsStableDiscordPathText` uses a backslash-normalized compare so Discord path checks are stable across hosts

## 2.5.1

- **UI — brand-forward home**: large Exo mark leads the dashboard; italic “Maximum performance” tagline sits under the brand (not competing with it)
- **UI — rail product logos**: Discord / Steam / Internet / NVIDIA marks on the left `NavRail` (logo-friendly hover — wash only, no scale)
- **UI — module craft polish**: denser feature tiles + status rail + icon wells; taller premium directory rows; clearer sticky `ExoActionBar` hairline and padding

## 2.5.0

- **UI — full remodel**: left icon rail (`NavRail` + Home/Discord/Steam/Internet/NVIDIA) replaces the old chrome; home is an editorial directory (hero tagline + full-width module rows) instead of a wrap-grid of product cards; module features are a vertical `FeatureTileGrid` (`StackLayout`) with sticky `ExoActionBar` footers — fixed 1180×760, AMOLED shell, settings still a gear flyout on the rail
- **Naming — Opti→Exo**: theme/styles/motion/loader keys are `Exo*` (`ExoPrimaryButton`, `ExoFeatureTile`, `ExoMotion`, `ExoLoader`, …) — no `Opti*` leftovers
- **Tooling — Peak→Logic/Smoke**: peak classifiers and smoke harnesses renamed to `*Logic` / `*.Smoke` (e.g. `NetworkLogic`, `DiscordLogic`, `Ui.Smoke`)

## 2.4.3

- **UI — full visual rebuild**: redesigned theme tokens (8/12/16/20/28 spacing, tighter radii), home hero + compact 248×148 cards, clearer module page hierarchy (section label → status title → feature grid → primary CTA), NVIDIA G-SYNC strip in a lifted card, denser Settings flyout and NVIDIA display panel — same shell contracts (fixed 1180×760, gear flyout, AMOLED `#000000` / `#0C0C0C`, `FeatureTileGrid` stretch)

## 2.4.2

- **UI — layout fix**: module feature tiles no longer stack in the top-left corner — `FeatureTileGrid` keeps `ItemsRepeater` width in sync with the scroll host so the two-column grid stretches correctly on every optimizer page
- **UI — dashboard stretch**: home card grid binds to the hero column width so cards stay centered and evenly wrapped
- **NVIDIA — Control Panel fallback**: Apply no longer strips the classic NVIDIA Control Panel; winget install is restored when missing; detect treats App-removed + CPL-present as applied; Exo NVIDIA Panel adds **Open NVIDIA Control Panel** when `nvcplui` is available

## 2.4.1

- **NVIDIA Panel — digital vibrance slider**: the DVC backend that shipped in 2.4.0 (`Exo.NvDisplay --get/set-vibrance`) is now surfaced as a per-display slider on the panel page; applied through the same dirty-diff Apply as resolution/depth/color/scaling, persisted to `nvidia-panel-settings.json`, hidden when the driver's DVC API is unavailable for a display
- **NVIDIA Panel — G-SYNC decision recorded**: per-display consumer G-SYNC toggle stays excluded — public NVAPI `GSync_*` is Quadro Sync genlock hardware, NvAPIWrapper has no G-SYNC surface, and there is no documented public API for consumer VRR; the DRS `* G-SYNC.nip` pack pins remain the supported path (see `docs/TWEAK-AUDIT.md`)
- **CI — startup measurement + AOT probe**: the e2e job now publishes the real app and reports startup time (`EXO_STARTUP_MS`, process start → main window); an informational Native AOT compile probe records toolchain status. Shipping publish stays self-contained + ReadyToRun — the app's reflection-based `System.Text.Json` state serialization makes Native AOT unsafe without a source-generation migration and on-hardware QA (documented in the tweak audit)

## 2.4.0

- **Internet — snapshot + true Repair**: pre-apply snapshot of every touched setting to `%LocalAppData%\Exo\network-snapshot.json`; Repair restores that exact snapshot (not a generic "stock-like" reset); post-apply **auto-rollback** when the connectivity probe fails; Wi‑Fi disable is now gated on a connectivity probe (never cuts your only working link); standalone `Repair-Internet.ps1` rescue one-liner (`irm | iex`, like `Repair-Discord.ps1`)
- **Internet — new tweak layer**: TCP fast path (initial RTO, MinRto, timestamps off, pacing off, TCP Fast Open, HyStart per preset); UDP URO off on Win11 24H2+ for latency; ECN per preset; DNS ServiceProvider priorities; RSS CPU spread; deeper adapter power kill; Delivery Optimization service to Manual
- **Internet — locale-independent NIC matching**: advanced-property tweaks match by `RegistryKeyword`, so they apply on non-English Windows
- **Internet — before/after benchmark**: ping / jitter / DNS lookup measured before and after apply and shown in the UI
- **NVIDIA — live DRS verification**: after Profile Inspector import, Exo exports the driver's actual profile database (NPI 3.0.1.11 `-exportCustomized`, version-pinned + SHA-256) and verifies pinned values; detect re-verifies live, so the UI shows **"Verified in driver"** vs **"Drifted — re-apply"**
- **NVIDIA — expanded catalog + pins**: per-game profiles for Apex, OW2, Marvel Rivals, R6, PUBG, CoD, Rust, Tarkov, LoL, Dota 2, Rocket League, GTA V/FiveM; more per-series DRS pins (Resizable BAR, present method, background FPS cap); deeper driver component strip at install (ShadowPlay/NvBackend/telemetry sub-packages); **digital vibrance** in the Exo NVIDIA Panel. Reset remains status-clear only — driver recovery stays manual
- **Discord — voice QoS + tighter detect**: DSCP 46 QoS policy for voice UDP across Stable/PTB/Canary; all Discord variants optimized; deeper module/dictionary debloat; binary detection tightened — the legacy OpenAsar layout is no longer accepted (fully replaced by Exo Host); Equicord + AMOLED theme + DiscOpt kernel (4s RAM trim, AboveNormal priority) unchanged
- **Steam — VDF injection + deeper quiet**: peak settings now inserted even when modern Steam omits the keys; library low-bandwidth/low-perf and community content off; webhelper RAM trim stats surfaced in the UI ("RAM reclaimed"); stable launcher flags; multi-library support
- **Runtime — stable PowerShell 7**: replaces the old PowerShell 7 Preview + Windows Terminal Preview requirement; a dependency doctor at install/update auto-installs stable pwsh (winget, MSI fallback) and **uninstalls the preview channels**; cache hygiene prunes old tool/driver/app-staging versions
- **App**: .NET 10 LTS + Windows App SDK 2.2 + C# 14; per-module last-apply report (step-by-step ok/fail/skip); confirmation dialogs removed

## 2.3.4

- **Detect fix**: Discord status checks no longer require OpenAsar (Exo Host + stock `_app.asar` + host flags); Start Menu accepts `Update.exe --processStart`; StrictMode no longer false-fails host flags; checks show green when Apply actually worked

## 2.3.3

- **Discord Exo Host** (replaces OpenAsar): OpenAsar is no longer installed — it is outdated vs modern Discord host integrity and Equicord’s layout
- **Equicord install**: uses official **Equilotl** CLI when available (`app.asar` stub + full stock `_app.asar`); direct path fixed to keep large stock on `_app.asar` (missing shell caused bare “Error” window)
- **Equicord profile**: AMOLED theme + curated plugins restored; host flags via settings (`SKIP_HOST_UPDATE`, chromium lean, TTI) without OpenAsar
- **Start Menu / launch**: one Discord Inc tile; Update.exe launch path kept for reliability

## 2.3.2

- **CRITICAL launch fix**: Discord now launches via official `Update.exe` (modern host integrity); stock host restored when inject path breaks boot; shortcuts no longer point only at a dead VBS path
- **Start Menu**: one Discord entry under `Discord Inc` only — removed root `Programs\Discord.lnk` that created a second app tile
- **Steam**: removed CEF flags that blanked/hung clients on some GPUs (`-cef-disable-occlusion`, `-cef-disable-renderer-accessibility`); safer Steam-Exo.cmd
- **Discord profile**: dialed back aggressive Chromium/OpenAsar options that could prevent boot; kernel trim back to stable 4s
- **Internet tailored apply**: NIC vendor / link speed / laptop / CPU detection; RSS budget; mid vs max buffers; IPv4-first; vendor NIC extras; Host gaming detect rows
- **Discord/Steam GPU**: High preference only when a discrete GPU exists

## 2.3.1

- **UI**: original hero tagline only; tighter page padding; borderless result banners; shorter/cleaner motion (no bounce); messages shortened
- **Internet**: power throttling off, LLMNR off, DNS cache TTL, SMBv1 off when present, proxy AutoDetect off, Wi‑Fi roam Low on latency preset
- **Discord**: richer Chromium switches, OpenAsar media keys off + DOM optimizer, High-performance GPU preference, fullscreen opts off
- **Steam**: more localconfig quiet/performance keys (music, friends activity, auto-update window, shader precache noise)
- **NVIDIA**: extra privacy RIDs, Ansel keys, PowerMizer prefer max when exposed

## 2.3.0

- **AMOLED shell polish**: pure-black dark, feature status rails, stronger card hover
- **Internet peak stack**: Game Mode on, GameDVR off, HAGS on, Ultimate/High performance plan + max AC CPU, MMCSS High
- **Discord kernel**: 3s working-set trim + tighter raw-input patch timing
- **Steam**: 3s webhelper reclaim, lean CEF flags
- **NVIDIA**: expanded per-game profile catalog

## 2.2.7

- **Runtime bootstrap works without winget**: when PowerShell 7 Preview or Windows Terminal Preview are missing and winget is unavailable or fails (common on debloated Windows), Exo now installs them directly from the official Microsoft GitHub releases — PowerShell Preview as a per-user portable zip under `%LocalAppData%\Exo\runtime` (no elevation), Terminal Preview sideloaded per-user via `Add-AppxPackage`; downloads are verified against the release size and SHA-256 digest
- **Standalone Disc-Optimizer**: same portable Preview fallback when run outside the app without winget

## 2.2.6

- **Faster startup after updates**: the full script-kit reinstall that ran before first paint on every post-update launch now happens on the background warm-up path (consumers still self-ensure correctness under the same lock)
- **Window focus no longer does wasted work**: the taskbar-icon workaround re-ran file probing + Win32 icon loads (leaking icon handles) on every activation; it now runs once
- **Faster NVIDIA detection and apply**: process checks in the detect script *and* the optimizer (overlay + debloat verification) query exact process names service-side instead of enumerating and regex-filtering every process on the system (4 full scans removed)
- **README**: added Privacy and FAQ sections (SmartScreen, admin elevation, install location, update flow)
- **Compile-time regexes**: all 37 detection classifiers across Network/Discord/Steam/NVIDIA peak logic now use source-generated `[GeneratedRegex]` (no runtime compilation or cache lookups; AOT-friendly)

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


































