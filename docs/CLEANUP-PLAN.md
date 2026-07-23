# Exo Cleanup & Hardening Plan

## Context

Exo (`ImAvgErix/Exo`, v3.16.13) is a WinUI 3 app hosting a React 19/Vite SPA in WebView2 (`ui/` → built into `Exo/wwwroot/`, served via `app.exo.local`). Optimizer modules run through `Exo/Services/WebHostBridge.cs` → native C# apply services and/or SHA-256-verified PowerShell kits staged to `%LocalAppData%\Exo\scripts`.

The owner wants an aggressive cleanup: Riot/Epic/Windows modules are broken or redundant (external tools — Nexus Playbook, FSOS-X 8.0 — cover Windows-level optimization), and the repo is bloated with dev scaffolding. Remaining modules must work at best-known 2026 values.

**Owner decisions (Q&A + follow-up message):**
1. **Windows module: delete fully.** README gets a "for OS-level optimization use Nexus Playbook / FSOS-X 8.0" pointer. No UI card.
2. **Riot + Epic launcher modules: delete fully.**
3. **Brave: KEEP and strengthen** (owner: keep if it can be made better — verified it can: `BraveNativeApply.cs` is production-grade with policy pack, prefs mutations, full snapshot/restore Repair). **AMD coming-soon card: delete** (zero code behind it).
4. **Games: KEEP config-only, delete the pak/bypass subsystem.** The safe layer (per-title config optimization: UE Scalability/GameUserSettings, borderless via real per-game tokens, config backup + Repair — "User configs only — no AC process edits", `GameOptimizerService.MultiGame.cs:111`) is viable and stays. The Marvel Rivals **UTOC signature bypass + IoStore mod packs** (`dsound.dll` + `.asi` injection + 61 MB `.pak/.ucas/.utoc`) cannot be made reliable — breaks every game patch, contradicts Exo's own anti-cheat-safe contract — so that subsystem goes; Marvel Rivals remains a config-only title (`GameOptimizerService.cs:294` confirms "Configs can still apply" without bypass).
5. **Game-traffic DSCP 46 QoS stays** in the Internet module.
6. **Legacy WinUI XAML layer: delete** (all `*OptimizerPage`, `SharedModulePlate`, `FeatureTileGrid`, optimizer ViewModels) **+ rewrite `tools/Ui.Smoke`** around the real WebView2/wwwroot contract.

Final module set: `discord`, `steam`, `internet`, `nvidia`, `brave`, `games` (config-only).

---

## 1. Dead weight audit

### 1.1 PowerShell scripts & script payloads — delete

- [ ] `Exo/Scripts/GameLaunchers/` (whole folder, ~1,384 lines): `Exo-Riot-{Detect,Run,Repair}.ps1`, `Exo-Epic-{Detect,Run,Repair}.ps1`, `GameLauncher-Detect.ps1` (423), `GameLauncher-Optimizer.ps1` (945), `VERSION`
- [ ] `Exo/Scripts/Windows/` (whole folder, ~631 lines): `Exo-Windows-{Detect,Run,Repair}.ps1`, `Windows-Optimizer.ps1` (451), `VERSION`
- [ ] `Exo/Scripts/Games/` (whole folder, **~61 MB**) — contains ONLY the pak/bypass payloads: `MarvelRivals/bypass/` (`MarvelRivalsUTOCSignatureBypass.asi`, `dsound.dll`), `MarvelRivals/packs/` (5 pack sets), `README.txt`. The Games module's config logic is C# and survives; no scripts are lost.
- [ ] `Exo/Scripts/Placeholders/` — `Brave-Optimizer.ps1` 5-line stub (Brave is native-only; stub already excluded from manifest/packaging; folder then empty)

### 1.2 Shared PowerShell libs (`Exo/Scripts/lib/`) — verified by dot-source trace

**Delete** (referenced only by Windows/GameLaunchers; `Import-ExoSharedLibFiles` in `Exo.Common.ps1:110-125` is `Test-Path`-guarded, so keepers skip missing libs safely):
- [ ] `Exo.PowerPlan.ps1` (339), `Exo.GamingStack.ps1` (602), `Exo.ShellDebloat.ps1` (313), `Exo.DefenderPurge.ps1` (237), `Exo.WindowsUpdate.ps1` (172), `Exo.OptionalFeatures.ps1` (98), `Exo.ScheduledTasks.ps1` (175), `Exo.Controllers.ps1` (226), `Exo.InputDevices.ps1` (248), `Exo.AmoledTheme.ps1`

**Keep** (used by keeper runners / C# / publish guards): `Exo.Common.ps1` (Nvidia/Steam/Discord runners, `Publish-Exo.ps1:273-278`, Ui.Smoke), `Exo.NoBackground.ps1` (same), `Exo.GameBar.ps1` (`Steam-Optimizer.ps1`, `ScriptBundleService.cs:372`), `Exo.RunHidden.vbs` (`Steam-Optimizer.ps1`, `SteamNativeApply.cs`)
- [ ] Trim the import list in `Exo.Common.ps1:117-120` to the three surviving libs

### 1.3 C# — delete whole files

Dead modules:
- [ ] `Exo/Services/WindowsNativeApply.cs` (1,376)
- [ ] `Exo/Services/LauncherNativeApply.cs` (822 — contains the stubs at `:711-712`, `:780`)
- [ ] `Exo/Services/ExoPowerPlanNative.cs` (only consumers: `WindowsNativeApply`, `NativeLiveDetect.DetectWindows` — both dying; confirm with compiler)
- [ ] `Exo/ViewModels/GameLauncherOptimizerViewModel.cs` (303)
- [ ] `Exo/Views/RiotOptimizerPage.xaml(.cs)`, `Exo/Views/EpicOptimizerPage.xaml(.cs)`

**KEEP (revised)**: `GameOptimizerService.cs` + `.MultiGame.cs` (edited, §1.4), `BraveNativeApply.cs` (untouched).

Dead legacy XAML layer (app is WebView2-only, `MainWindow.xaml:24`; only `DashboardViewModel` is still used, via `WebHostBridge.cs:206`):
- [ ] Every page under `Exo/Views/`: `DiscordOptimizerPage`, `SteamOptimizerPage`, `NvidiaOptimizerPage`, `InternetOptimizerPage`, `DashboardPage` (`.xaml` + `.xaml.cs`)
- [ ] `Exo/Views/Controls/SharedModulePlate.*`, `Exo/Views/Controls/FeatureTileGrid.*`
- [ ] `Exo/ViewModels/`: `DiscordOptimizerViewModel`, `SteamOptimizerViewModel`, `NvidiaOptimizerViewModel`, `InternetOptimizerViewModel`, `NvidiaPolicyRowViewModel`, `ApplyReportRowViewModel`
- [ ] Keep `DashboardViewModel.cs` (trimmed, §1.4). After XAML deletion, delete now-orphaned XAML-only helpers (`ValueConverters.cs`, `UiStatusPresentation.cs`, `ExoMotion.cs` — compiler/reference search decides)
- [ ] Strip vestigial native chrome from `MainWindow.xaml`: hidden `<Frame x:Name="ContentFrame">` (`:71`), rail buttons incl. `NavRiot`/`NavEpic`; keep WebView2 host + title bar

### 1.4 Shared C# — edit in place (line refs verified this session)

- [ ] `Exo/Services/GameOptimizerService.cs` (1,635) — **strip the pak/bypass subsystem, keep the config layer**: bypass detect rows (`:252-254`, `:294`, `:319`), "install bypass → install packs → verify" apply stages (`:454-466` incl. `PackCacheHasMinimum`/`HasBypassFiles`), pack-seed loading, "Exo packs ready" states. Keep: catalog, probes, `WriteScalabilityIni`, `PatchGameUserSettings`, borderless policy, markers, Repair.
- [ ] `Exo/Services/GameOptimizerService.MultiGame.cs` (2,287) — config-only already; audit for pack references, keep otherwise
- [ ] `Exo/Services/WebHostBridge.cs` (1,952): remove **windows/riot/epic only** — dashboard rows (`:213-217` keep discord/brave/steam/internet/nvidia/games), `experimentalDefaults` (`:416-419`), `MapNextId` (`:764-768`), `DetectCoreAsync` cases (`:798-806` — keep `games`/`brave`), launcher/Vanguard `IsInfoTitle` entries (`:1066-1078` — keep games titles minus pak strings), experimental switches (`:1185-1191`, `:1319-1325`), native-pipeline flags (`:1546-1552`, `:1596-1605` — keep `brave`), `RunModuleScriptAsync` cases (`:1614-1644`), `needPwshBootstrap` (`:1683`), Windows deep-pack retry (`:1726-1762`), riot/epic yield + windows restamp (`:1773-1791`). Keep `games.*` RPC handlers (`:94-98`) and `ApplyGameHubAsync`/`RepairGameHubAsync`/`MapGamesHub` (`:1832-1907`), minus pak-status strings. **⚠ Preserve `RestampHostLatency` for Steam** (`:1718-1719`, `:1790` serve both `windows` and `steam`).
- [ ] `Exo/ViewModels/DashboardViewModel.cs` (850): remove cards `windows`/`riot`/`epic`/`amd` (`:28`, `:31`, `:33-34` — **keep brave, promote from ComingSoon**); dead CheckRows (`:47`, `:50-51`); windows/riot/epic status props (`:127-142`); windows tile (`:491-508`); launcher tiles (`:561-569`); `RefreshLauncherTile` (`:683-715`); **hard-coded "/ 8 verified" counts (`:623`, `:629-633`) → derive from module list**; trim `UpdateNextAction` (`:649-659`). Keep games block (`:571-605`).
- [ ] `Exo/Services/ScriptBundleService.cs`: `_gameLaunchersSyncDone`/`_windowsSyncDone` (`:22-23`), `GetGameLaunchersRoot` (`:117-140`), `GetWindowsRoot` (`:142-158`), Riot/Epic/Windows script-path props (`:187-196`), reset-job entries (`:269-288`), `EnsureGameLauncherScriptsSynced` (`:496-517`), `EnsureWindowsScriptsSynced` (`:519-539`)
- [ ] `Exo/Services/OptimizerStateService.cs`: `DetectRiotAsync`/`DetectEpicAsync` (`:52-56`), `DetectWindowsAsync` (`:61-62`), `DetectGameLauncherAsync` (`:64-113`). Keep `DetectBraveAsync`.
- [ ] `Exo/Services/NativeApplyService.cs`: `SupportsNativeApply` (`:27`) → `steam|brave`; router (`:41-49`) drops windows/riot/epic cases
- [ ] `Exo/Services/NativeLiveDetect.cs`: remove `DetectWindows` (~`:166-390`) + `DetectLauncher("riot"/"epic")`; keep `DetectSteam`, `DetectBrave`
- [ ] `Exo/Helpers/PathHelper.cs:23,25` (GameLauncher/Windows dirs); `Exo/Models/AppSettings.cs:26,29,30` + `Clone :46,49,50` (Windows/Riot/Epic experimental flags — keep any brave/games flags); `Exo/Services/AppServices.cs:51` (drop `GetGameLaunchersRoot()` warm; keep `Games` service at `:15`)
- [ ] `Exo/MainWindow.xaml.cs`: `NavigateToWindows/Riot/Epic` (`:348`, `:351-352`), `NavRiot_Click`/`NavEpic_Click` (`:444-445`). Keep `NavigateToGames` (`:353`).
- [ ] **Intentionally NOT touched**: `NetworkApplyScriptBuilder.cs:1055-1121` — game-exe DSCP 46 QoS stays (decision #5)
- Verified clean, no edits: `App.xaml.cs`, `ExoJsonContext.cs`, `OptimizerMessages.cs`, `HubSection.cs`, `OptimizerDefinition.cs`, `Install/Run/Publish/Release-Exo.ps1`, `Exo.csproj` (Scripts/Assets globs auto-drop deleted files)

### 1.5 React UI (`ui/`) — edit + rebuild

- [ ] `ui/src/lib/host.ts:3-13`: `ModuleId` union → `'discord'|'brave'|'steam'|'games'|'internet'|'nvidia'`; keep `listGames/applyGame/repairGame/openGameInstall` + game types; remove windows/riot/epic mock rows (`:370-377`); trim pak/bypass strings from the mock game catalog (`:481-497`)
- [ ] `ui/src/components/Shell.tsx:8-18` — nav registry: remove `windows`, `riot`, `epic` only
- [ ] `ui/src/pages/ModulePage.tsx:25-30` — drop windows/riot/epic ids; keep games special-casing (`:56,237,293`)
- [ ] `ui/src/pages/GamesPage.tsx` — **keep**; remove pak/bypass UI copy ("Exo packs", bypass rows) to match the config-only backend
- [ ] `ui/src/lib/featurePreview.ts` games branches (`:171-185`, `:209-217`) — keep; `ui/src/lib/moduleUx.ts:191` ("marvel rivals not installed") — keep
- [ ] `ui/src/index.css:18,21,22` — remove `--color-windows/riot/epic` vars only
- [ ] Keep `HomePage.tsx:131` — "WINDOWS" is the OS spec label, not the module
- [ ] `npm ci && npm run build` → commit regenerated `Exo/wwwroot/` (purges dead code + logos from the shipped bundle)

### 1.6 Assets / logos

- [ ] Delete from `Exo/Assets/Logos/`: `epic.png`, `riot.png`, `windows.png`, `amd.png`, `amd.svg`
- [ ] Delete from `ui/public/logos/` + committed mirror `Exo/wwwroot/logos/`: `epic.png`, `riot.png`, `windows.png`
- [ ] Keep: `discord*`, `steam.png`, `internet.*`, `nvidia.png`, `brave.*`, `games.png`, `exo.png`, and all per-game title logos (`marvel-rivals*`, `valorant`, `cs2`, `fortnite`, `league-of-legends`, `apex-legends`, `helldivers-2`, `the-finals`, `predecessor`, `black-ops-7`) — Games hub still renders them

### 1.7 Script manifest & CI

- [ ] Re-run `tools/Generate-ScriptManifest.ps1` after script deletions → regenerates `Exo/Security/ShippedScriptManifest.g.cs` (never hand-edit; verifier `ShippedScriptManifest.cs` is generic; drops `GameLaunchers/*`, `Windows/*`, `Games/MarvelRivals/*`, deleted `lib/*` entries)
- [ ] `.github/workflows/ci.yml:90` + `release.yml:69` — remove `tools/GameLaunchers.Smoke` from smoke matrices
- [ ] `tools/Ui.Smoke/Program.cs` — rewrite (Phase 1 step 8): dead assertions at `:203` (riot color), `:284-285` (NavRiot/NavEpic), `:445-446`, `:627,702,742,781,925` (Riot/Epic pages), `:767-776` (GameLauncherOptimizerViewModel), `:1020`, `:1223-1224` + `:1266-1269` (windows card/logo), plus every legacy-XAML structural assert once the layer is deleted

### 1.8 Half-implemented / stubbed code found

- `LauncherNativeApply.cs:711-712`, `:780` — placeholder stubs, deleted with the file
- `DashboardViewModel.cs:31` — AMD ComingSoon card → deleted; `:32` Brave ComingSoon → **promoted to live module** (Phase 2)
- No `NotImplementedException` / TODO / FIXME anywhere in keeper code (verified)

---

## 2. Development bloat audit

Nothing in build/CI/app references `.agents/`, `skills-lock.json`, `docs/cua-qa/`, `docs/rewrite/`, or `Exo.UiPreview` (verified: zero hits).

### Delete (~10.5 MB + 9 tool projects)

- [ ] `.agents/skills/` — 52 of 54 skills are vendored third-party dumps (5.3 MB, 555 files, incl. `deploy-to-vercel/Archive.zip`); keep only hand-written `exo-ui-craft` (move to `docs/UI-CRAFT.md`); `exo-cua-qa` dies with the CUA tooling
- [ ] `skills-lock.json` (only re-provisions the vendored skills)
- [ ] `docs/cua-qa/` — 5.1 MB: `stress/` (4.9 MB, 38 QA screenshots), `exo-uia-home.json` (172 KB), `windows-list.json`, `windows.json`, report MDs (regenerable QA output; only code reference is a comment in the now-deleted `DiscordOptimizerViewModel.cs:359`)
- [ ] `docs/rewrite/` — 172 KB of July-2026 one-off research dumps. **Before deleting, mine two sections into `docs/HARDENING-BACKLOG.md`** (they feed Phase 2): `research-internet.md` §4.5 "Detect/Apply contract mismatches" and `research-discord-steam.md` §7.2 "Critical honesty issues" (extracted, summarized in §5 below)
- [ ] Unreferenced top-level docs: `docs/EXO-MASTER-SPEC-v3.md`, `docs/REWRITE-PROGRAM.md`, `docs/OVERHAUL-SUMMARY.md`, `docs/PROMPT-V2-AUDIT.md`, `docs/CLEAN-PC-AUDIT.md`
- [ ] Dead-module docs: `docs/WINDOWS-OWNERSHIP.md` (cited only by a `Steam-Optimizer.ps1` comment — update that comment), `docs/TWEAK-AUDIT.md` (README-linked; trim to keeper scope or delete + unlink)
- [ ] Orphan images at docs root: `exo-shell.png`, `exo-steam-fix.png`, `logo-contact.png` (252 KB)
- [ ] Tool projects (none in `Exo.sln`, none in CI): `tools/Cua.Qa/`, `tools/Exo.UiPreview/` (428 KB obsolete v2.5 UI mock, empty tests dir), `tools/Exo.NvCplScale/` (orphaned, superseded by `Exo.NvDisplay`), `tools/LogoNorm/`, `tools/DetectOnly.Smoke/`, `tools/NativeApply.Smoke/`, `tools/NativeApply.LiveSmoke/` (mutates live machine!), `tools/Games.ApplyOnce/` (live-applies Marvel Rivals), `tools/Games.MultiSmoke/` (live-applies 5 games), `tools/GameLaunchers.Smoke/` (module dead)
- [ ] `AGENTS.md` — keep the file; remove Cua.Qa/UiPreview workflow sections, fix ".NET 8 SDK" → .NET 10

### Keep — real infrastructure

| Item | Why |
|---|---|
| `tools/Exo.NvDisplay/` | **Shipped product code** — NVAPI helper built by `Publish-Exo.ps1:144-176` into `Scripts/Nvidia/tools/`, invoked by `NvidiaPanelSettingsService.cs:446-455` |
| `tools/NetScriptDump/` | Drives the Internet E2E CI job |
| `tools/ExoSfx.cs`, `tools/Test-Repository.ps1` | Installer + CI validation gate |
| `tools/{Ui,Network,Discord,Steam,Nvidia,Contracts}.Smoke/` | Real assertion smokes in the CI matrix (Linux-buildable via `<Compile Include>` of app sources) |
| `tools/{Test-Linux,Bump-Version,Generate-ScriptManifest,Verify-LiveApplied}.ps1` | Ship-checklist helpers; the manifest generator is required by this cleanup |
| `CHANGELOG.md` (104 KB) | Real hand-written history, rendered in-app since 3.16.7 |
| `.github/` workflows + templates | Sound; only the two GameLaunchers.Smoke lines change |

---

## 3. Keep & strengthen list (ranked by real value)

### #1 Internet/Network — highest value, best engineering
Files: `NetworkOptimizerService.cs` (88 KB), `NetworkApplyScriptBuilder.cs` (+`.Repair`/`.Benchmark`), `NetworkLogic.cs`, `NetworkSnapshot.cs`, root `Repair-Internet.ps1`, `docs/INTERNET-GOLDEN-PATH.md`.
Already right for 2026 (keep): measured presets from a real quality benchmark (idle/loaded latency, loss, link rate); `netsh int tcp` `autotuninglevel=normal`, `congestionprovider=cubic` via supplemental template, heuristics disabled, taskoffload enabled; **removal** of folklore `TcpAckFrequency`/`TCPNoDelay`/`TcpDelAckTicks` pins; NIC tuning (interrupt-moderation profile, LSO/RSC per measurement, RSS `BaseProcessorNumber=2`, `-Profile Closest`); fail-closed rules (never disable Wi-Fi/LLDP/NCSI probe, no DNS/MTU packs, snapshot-failure aborts apply, full-snapshot rollback).
2026 target values: latency preset → RSC **disabled** + interrupt moderation **Off/Low** on the gaming NIC; throughput preset → RSC on + Adaptive (matches current measured-preset logic — verify, don't fork). Leave ECN at OS default. Keep CUBIC (BBRv2 still not a stable exposed provider on client SKUs). Keep DSCP 46 game/voice QoS; **add a verify step** that DSCP marking actually applies on non-domain networks (`HKLM\SYSTEM\...\Tcpip\QoS` → `"Do not use NLA"=1` if absent, snapshotted + repairable).

### #2 NVIDIA — strong, one real fragility
Files: `Nvidia-Optimizer.ps1` (4,730), `Exo-Nvidia-Detect.ps1`, DRS packs `profiles/*.nip` (10–50 series ± G-SYNC), `NvidiaPanelSettingsService.cs`, `tools/Exo.NvDisplay`.
Already right (keep): per-series `.nip` import with **post-import DRS re-export verification** (`:1038-1066`) and pre-Exo DRS snapshot Repair (`nvidia-drs-pre-exo.bin`); pins = ULLM on (+CPL state 2), max pre-rendered 1, prefer max performance, Resizable BAR, shader cache max, threaded optimization, overlays/Ansel off; G-SYNC vs raw-latency variants; NVTweak Gestalt stamps so DRS takes effect; vibrance/refresh/scaling via NvAPI helper.
2026 target values: G-SYNC variant = G-SYNC + driver V-Sync On + cap ≈ refresh−3 (Reflex supersedes ULLM in-game — current pins correct); raw-latency variant = all sync off, uncapped. Verify DLSS override preset IDs against current driver branch (R570+/R580) when touching packs.
Fix (the fragility): **apply-time live GitHub download of nvidiaProfileInspector** (`Nvidia-Optimizer.ps1:46-47,721,801`) — pin exact version + SHA-256, cache in the kit, hard-fail with a clear offline error.

### #3 Steam — solid native path, needs post-apply verification
Files: `SteamNativeApply.cs`, `SteamLogic.cs`, `Steam-Optimizer.ps1` (3,027), `NativeLiveDetect.DetectSteam`.
Already right (keep): quiet CEF launcher `Steam-Exo.cmd` (`-nofriendsui -nointro -cef-disable-breakpad -cef-disable-spell-checking`, `start /HIGH`); **forbidden-flag rails** banning client-blanking flags (`SteamLogic.ForbiddenApplyPatterns:137-153`); `HKCU\Software\Valve\Steam` `StartupMode=0`; high-perf GPU for `steamwebhelper.exe` via `HKCU\...\DirectX\UserGpuPreferences` `GpuPreference=2;`; Run-key autostart removal (HKCU+HKLM+WOW6432Node); toast/tray quiet; SteamMemoryGuard (EcoQoS + memory-priority drop for background CEF, foreground-gated; kill/suspend banned); `.exo-bak` VDF backups.
2026 work: re-validate CEF flags against the current Steam client (CEF churn is the rot vector — add a detect row flagging an unknown Steam build); post-apply verify = re-run `DetectSteam`, record per-feature verified state; **keep the host-latency restamp (PowerThrottling + MMCSS `SystemResponsiveness`) working for Steam** after the Windows-module extraction.

### #4 Discord — most polished; fix the honesty gaps
Files: `Disc-Optimizer.ps1` (37 KB), `Exo-Discord-Repair.ps1` (35 KB), kit libs `10-Logging…60-KernelBoot`, profiles, `DiscordLogic.cs`.
Already right (keep): Equicord lean plugin policy; host `settings.json` `SKIP_HOST_UPDATE:true` + `chromiumSwitches` (breakpad/crash-reporter/domain-reliability/component-update/background-networking/no-pings/renderer-backgrounding off); voice QoS DSCP 46 per variant; Windows quiet (Run key, toasts, tray); `DISABLEDXMAXIMIZEDWINDOWEDMODE`; apply-script **self-audit** with banned folklore (`DiscordLogic.AuditApplyScriptText:219-266`); kernel proxy `PriorityClass=3` (AboveNormal — correct; don't raise a voice app to High).
2026 work (mined research §7.2): hosted Apply never runs `Confirm-DiscordBootsAfterMods` → add boot-verify to the in-app path; reconcile kernel soft-skip vs `isApplied`; make QoS/variant partial failures fail the apply result; sync the `AppSettings.DiscordKitVersion` fallback (`AppSettings.cs:21`, hardcoded "1.3.73") from `Scripts/Discord/VERSION`; surface "Reapply needed" when a Discord update wipes the kit.

### #5 Brave — production-grade native module; promote from "coming soon"
Files: `BraveNativeApply.cs` (~1,650 lines), `NativeLiveDetect.DetectBrave`, `OptimizerStateService.DetectBraveAsync`.
Already right (keep): HKLM managed-policy pack (`Policies\BraveSoftware\Brave`) killing telemetry/reporting (`MetricsReportingEnabled=0`, `BraveP3AEnabled=0`, `BraveStatsPingEnabled=0`, `SafeBrowsingExtendedReporting=0`, `DomainReliabilityAllowed=0`, autofill/password-manager off — `ComponentUpdatesEnabled=1` kept for security); profile `Preferences` mutations + content filters + labs; GPU preference `GpuPreference=2;`; quiet startup/update tasks/services; safe cache clears; **full snapshot + `RestoreFullSnapshot` Repair**; verify pages.
2026 work: promote out of ComingSoon on the dashboard (`DashboardViewModel.cs:32`); add `Brave.Smoke` to the CI matrix (mirror Steam.Smoke's source-shape pattern — validate policy pack + forbidden ops); post-apply verify via `DetectBrave` re-run; confirm policy names against current Brave stable (Chromium policy churn).

### #6 Games — keep the config layer, drop the pak layer
Files: `GameOptimizerService.cs` (edited), `GameOptimizerService.MultiGame.cs`, `ui/src/pages/GamesPage.tsx`.
Keep (the viable, ban-safe layer): per-title probes; UE `Scalability`/`GameUserSettings` quality presets; **always-borderless via each game's real config tokens** (Valorant everywhere-walk, UE section injection incl. Fortnite/Marvel custom sections, Apex, Helldivers 2, COD `s.*/g.*` players configs); config backup before write + Repair restore; "game running → config locked" guard; marker-file verification.
Delete (cannot be made reliable): UTOC signature bypass + IoStore packs (61 MB, breaks every patch, anti-cheat contract violation) — Marvel Rivals becomes config-only like every other title.
2026 work: per-title **config-schema probe** (game patch changed the file format → detect "unknown schema", skip gracefully instead of writing stale keys); post-apply verify = re-read configs and diff; prune any title whose detection can't be made reliable on a real box; document per-title maintenance cost in `docs/HARDENING-BACKLOG.md`.

### #7 Shared infrastructure (the platform)
`PowerShellRunnerService` (elevation + manifest verify), `ScriptBundleService` (versioned kit staging), `ShippedScriptManifest` (LF-canonical SHA-256), `ModuleApplyLog`, `WebHostBridge`, `GitHubUpdateService`, React shell. All keep; hardening in §5.

---

## 4. Architecture cleanup recommendations

**Removal mechanics that keep Apply/Detect/Repair intact:**
1. Module identity lives in two React spots (`host.ts` `ModuleId`, `Shell.tsx` nav array) and ~8 C# switch sites (§1.4). Trim each to the 6 keepers; deleting dead services first lets the C# compiler + `tsc -b` catch stragglers.
2. The manifest is regenerated, not edited: delete scripts → run `tools/Generate-ScriptManifest.ps1` → commit `ShippedScriptManifest.g.cs`. Elevation verification (`PowerShellRunnerService.cs:55-65`) stays exact.
3. Kit staging: removing `GameLaunchers`/`Windows` from the `ScriptBundleService` reset jobs (`:269-273`) plus the `.app-kit-stamp` version bump wipes stale kits in `%LocalAppData%\Exo\scripts` on first run.
4. `NativeApplyService` keeps Steam + Brave; it also builds the batched elevated `exo-native-*.ps1` reg script (dword/string/delete/qos ops) both use.
5. `DashboardViewModel` survives as the dashboard-snapshot builder for `WebHostBridge`; optionally rename to `DashboardSnapshotService` in Phase 3 (it's no longer a ViewModel).
6. Optional (recommended, small): a single `ModuleRegistry` static (id → script paths, native caps, pwsh-bootstrap flag) folding the remaining `WebHostBridge`/`ScriptBundleService` switches into lookups. With a stable 6-module set this is one-time simplification, not speculation.

**Shared code currently polluted by dead modules** (all covered in §1.4): `WebHostBridge`, `DashboardViewModel`, `ScriptBundleService`, `OptimizerStateService`, `NativeApplyService`, `NativeLiveDetect`, `PathHelper`, `AppSettings`, `AppServices`, `MainWindow`, `GameOptimizerService` (pak layer), `Exo.Common.ps1`, `host.ts`, `Shell.tsx`, `ModulePage.tsx`, `index.css`.

**Target folder structure after cleanup:**

```
Exo/
  Exo.sln, VERSION, README.md, CHANGELOG.md, AGENTS.md, LICENSE, SECURITY.md, PRIVACY.md
  Install/Run/Publish/Release-Exo.ps1, Repair-Discord.ps1, Repair-Internet.ps1
  Exo/                      # WinUI host: MainWindow (WebView2) + services; no Views/ pages
    Services/  Helpers/  Models/  Serialization/  Security/
    Scripts/
      Discord/  Steam/  Nvidia/  lib/{Exo.Common,Exo.NoBackground,Exo.GameBar}.ps1 + Exo.RunHidden.vbs
    Assets/Logos/            # 6 module logos + exo
    wwwroot/                 # committed build of ui/
  ui/                        # React shell (6 modules incl. GamesPage)
  tools/
    Exo.NvDisplay/  NetScriptDump/  ExoSfx.cs  Test-Repository.ps1
    Ui.Smoke/ Network.Smoke/ Discord.Smoke/ Steam.Smoke/ Nvidia.Smoke/ Brave.Smoke/ Contracts.Smoke/
    Test-Linux.ps1  Bump-Version.ps1  Generate-ScriptManifest.ps1  Verify-LiveApplied.ps1
  docs/
    PC-AWARE.md  INTERNET-GOLDEN-PATH.md  HARDENING-BACKLOG.md  UI-CRAFT.md  media/home.png
  .github/workflows/{ci,release}.yml + templates
```

---

## 5. Quality & reliability upgrades

- [ ] **Manifest drift gate in CI** (closes a real gap: `Generate-ScriptManifest.ps1` is manual; neither ci.yml nor release.yml checks it). CI step: regenerate → `git diff --exit-code Exo/Security/ShippedScriptManifest.g.cs`.
- [ ] **Standardized post-apply verification**: after every Apply, re-run the module's Detect and persist per-feature verified state; UI shows "Applied ✓ verified" vs "Applied — verify failed" (NVIDIA's DRS re-export diff is the model; Steam/Brave/Games get detect re-runs; Internet surfaces its post-apply probe per-feature).
- [ ] **NVIDIA NPI pinning**: exact nvidiaProfileInspector version + SHA-256, kit-cached, no apply-time GitHub dependency.
- [ ] **Discord honesty fixes** (research §7.2): boot-verify in hosted apply; kernel soft-skip vs `isApplied` reconciliation; QoS partial-fail propagation; kit-version fallback sync.
- [ ] **Internet contract fixes** (research §4.5): verify CUBIC instead of always-OK congestion row; check Preferred Band as applied; remove the false "Wi-Fi disabled when Ethernet has a real IP" success string; fix golden-path doc bugs (roaming aggressiveness + ring-buffer rows contradict code).
- [ ] **Games schema-probes** + config re-read verify (§3 #6); **Brave.Smoke** + policy-name validation (§3 #5).
- [ ] **Error surfaces**: native apply paths write the same `applyReport[]` step lines PowerShell paths do; keep 80 KB log mirroring + elevated-transaction mirror.
- [ ] **Versioning**: per-kit `VERSION` + NVIDIA `PROFILE_VERSION` stay authoritative; bump every kit whose scripts changed so `.app-kit-stamp` re-stages; dashboard verified-counts derived, never hard-coded.
- [ ] **Ui.Smoke rewrite** (the CI gate after XAML deletion): assert wwwroot is built + committed (index.html present, hashed bundle referenced), logo set exactly matches the `ModuleId` union, `VERSION` == csproj `<Version>` == `ui/package.json`, MainWindow hosts WebView2 + bridge wiring, no dead-module strings (`riot`, `epic`, `windows-optimizer`, `bypass`, `UTOC`) in `ui/src` or the built bundle.

---

## 6. Execution plan (ordered, low-risk)

Every phase ends with: `dotnet build Exo.sln -c Release -p:Platform=x64` clean (Windows or CI), CI smoke matrix green.

### Phase 0 — Safety
- [ ] Work on `claude/exo-cleanup-audit-plan-bbuu6x` (already branched)
- [ ] Note baseline `v3.16.13` as the last release with Windows/Riot/Epic + pak system (stays downloadable for legacy repair)
- [ ] Confirm CI green on branch tip before any deletion

### Phase 1a — Development bloat (zero compile impact; one commit)
- [ ] Delete: `.agents/` (move `exo-ui-craft/SKILL.md` → `docs/UI-CRAFT.md`), `skills-lock.json`, `docs/cua-qa/`, `docs/rewrite/` (after mining §4.5/§7.2 → `docs/HARDENING-BACKLOG.md`), 5 unreferenced top-level docs, 3 orphan images, tool projects: `Cua.Qa`, `Exo.UiPreview`, `Exo.NvCplScale`, `LogoNorm`, `DetectOnly.Smoke`, `NativeApply.Smoke`, `NativeApply.LiveSmoke`, `Games.ApplyOnce`, `Games.MultiSmoke`
- [ ] Update `AGENTS.md` (drop Cua/UiPreview sections, .NET 10)

### Phase 1b — Dead modules + pak layer (order matters)
1. [ ] Delete script folders: `Scripts/GameLaunchers`, `Scripts/Windows`, `Scripts/Games`, `Scripts/Placeholders`; delete 10 Windows-only `Scripts/lib/*.ps1`; trim `Exo.Common.ps1:117-120`
2. [ ] Run `tools/Generate-ScriptManifest.ps1`; commit regenerated `ShippedScriptManifest.g.cs`
3. [ ] Delete dead-exclusive C# (§1.3 incl. XAML layer) and `tools/GameLaunchers.Smoke`
4. [ ] Edit shared C# per §1.4 — including the `GameOptimizerService` pak-layer strip (compiler-driven; **watch the Steam host-latency restamp**)
5. [ ] Edit `ui/` per §1.5; delete dead logos (§1.6); `npm ci && npm run build`; commit `wwwroot`
6. [ ] Update `ci.yml`/`release.yml` smoke matrices
7. [ ] Bump kit `VERSION` files for every kit whose contents changed (forces working-kit re-stage on update)
8. [ ] Rewrite `tools/Ui.Smoke` to the new contract (§5) — **land steps 3–8 in one PR so CI is never structurally red**
9. [ ] Sweep leftovers: `Steam-Optimizer.ps1` comment → deleted `WINDOWS-OWNERSHIP.md`; pak/bypass strings in GamesPage + bridge info titles

### Phase 2 — Fix & harden keepers (order justified by risk/leverage)
1. [ ] **Manifest drift gate in CI** — safety net for every later script change
2. [ ] **NVIDIA NPI pinning** — removes the one external runtime dependency (highest in-the-wild breakage)
3. [ ] **Standardized post-apply verify** — platform change the rest build on
4. [ ] **Brave promotion**: live dashboard card, `Brave.Smoke` in CI, policy-name validation
5. [ ] **Discord honesty fixes** (boot-verify, soft-skip reconciliation, QoS fail propagation, kit-version sync)
6. [ ] **Internet contract fixes** (detect rows vs apply truth; false success string; golden-path doc corrections)
7. [ ] **Games hardening**: per-title config-schema probes, config re-read verify, prune undetectable titles
8. [ ] **Steam** CEF-flag revalidation + unknown-build detect row
9. [ ] Optional: `ModuleRegistry` consolidation

### Phase 3 — Polish
- [ ] README: module table → 6 keepers (Games described as "per-title config quality + borderless — no packs, no binary mutation"); add "OS-level optimization: use Nexus Playbook / FSOS-X 8.0" note; fix the duplicated Internet row; drop/trim `TWEAK-AUDIT.md` link; refresh screenshots
- [ ] CHANGELOG: one clear cleanup-release entry incl. migration note: "Repair Windows/Riot/Epic (and Marvel Rivals packs) in 3.16.13 **before** updating"
- [ ] Version bump: recommend **4.1.0** (module removal is breaking; 4.0.0 is burned by the reverted "AI maximize" line) — owner may prefer 3.17.0
- [ ] `PC-AWARE.md` light edit; `PRIVACY.md`/`SECURITY.md` sanity pass (anti-cheat language now unconditionally true — no bypass shipping)
- [ ] Final `Generate-ScriptManifest.ps1` + `Test-Repository.ps1` + full CI

### Phase 4 — Verification checklist (owner-runnable, real Windows 11 box)
- [ ] Fresh install from new `Exo.exe`: exactly Discord/Brave/Steam/Games/Internet/NVIDIA tiles; dashboard counts derived ("/6")
- [ ] Update path: install 3.16.13 → apply Steam → update in-app → kits re-staged (`%LocalAppData%\Exo\scripts` has no GameLaunchers/Windows dirs), Steam still detected applied
- [ ] Per module: Detect (honest on a dirty box) → Apply (one UAC, per-step report, no hangs) → state shows **verified** → Repair → Detect returns to pre-Exo state
- [ ] Internet: benchmark → latency preset → `netsh int tcp show global` matches; `Get-NetQosPolicy` shows DSCP 46 entries; Repair restores snapshot
- [ ] NVIDIA: apply G-SYNC variant **offline** → succeeds from cached NPI; DRS verify passes; Repair restores `nvidia-drs-pre-exo.bin`
- [ ] Discord: apply on Stable → client boots (boot-verify green), voice QoS present, no autostart/toasts
- [ ] Brave: apply → policies in `HKLM\SOFTWARE\Policies\BraveSoftware\Brave`, prefs mutated, browser launches; Repair restores snapshot
- [ ] Games: with one installed title — apply → configs patched + borderless enforced + backup exists; launch game, confirm settings stick; Repair restores configs; Marvel Rivals shows **no** pack/bypass rows
- [ ] Logs complete per module (`apply-*-latest.log`); "Open logs" works
- [ ] `pwsh tools/Test-Repository.ps1` + full CI green; `Publish-Exo.ps1` produces a working self-extracting `Exo.exe`

---

## 7. Risks & open questions

**Risks:**
1. **Steam host-latency restamp regression** — `RestampHostLatency` (`WebHostBridge.cs:1718-1719,1790`) serves both `windows` and `steam`; the Windows extraction must keep the Steam path. Mitigation: Steam.Smoke assertion + Phase 4 check.
2. **Users lose in-app Repair for deleted modules** (Windows/Riot/Epic tweaks, Marvel Rivals packs). Mitigation: CHANGELOG "repair first on 3.16.13"; 3.16.13 stays downloadable. The pak system leaves `dsound.dll`/`.asi`/paks in game dirs of past users — the Games Repair path for pack removal should survive one release, or ship a one-shot cleanup step in the new Games repair (recommended: **new Games Repair also removes legacy bypass/pak files if found**).
3. **GameOptimizerService pak-strip is surgery, not deletion** — bypass/pack code is interleaved with the config apply (`:449-497`). Compiler + `Contracts.Smoke` + Phase 4 game test are the guards.
4. **Ui.Smoke rewrite window** — land XAML deletion + rewritten smoke in one PR (Phase 1b steps 3–8) so CI is never structurally red.
5. **Committed `wwwroot` staleness** — React rebuild must be committed with source edits; rewritten Ui.Smoke asserts no dead-module strings in the bundle.
6. **Manifest regeneration correctness** — elevated applies hard-fail on hash mismatch; always regenerate via the tool; the CI drift gate makes this permanent.
7. **Discord ffmpeg-proxy kernel** remains the most maintenance-sensitive keeper feature (breaks on Discord host updates); the "Reapply needed" state (Phase 2.5) is the mitigation.
8. **Games config schemas churn per patch** — accepted maintenance cost of keeping the module; the schema-probe (Phase 2.7) turns silent breakage into a graceful "unknown schema, skipped" row.
9. **`e2e-optimizers` CI job** (installs real Steam/Discord) exercises exactly the shared paths being edited — watch it on the cleanup PR.

**Resolved (owner):** Windows/Riot/Epic deleted (README pointer to FSOS-X/Nexus); Brave kept + promoted; AMD card deleted; Games kept config-only with pak/bypass deleted; game-exe DSCP QoS stays; XAML layer deleted with Ui.Smoke rewrite.

**Still open (non-blocking, decide at Phase 3):**
- Release number: 4.1.0 (recommended) vs 3.17.0.
- `docs/TWEAK-AUDIT.md`: trim to keeper scope vs delete + unlink from README.
- Whether the new Games Repair should proactively remove legacy pak/bypass files from past installs (recommended: yes — see risk #2).
