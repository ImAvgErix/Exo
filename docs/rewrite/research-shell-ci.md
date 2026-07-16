# Research: Shell UI, Services, CI & Cross-Cutting Concerns

**Scope:** Inventory of Exo UI shell, services, publish/update paths, CI/release, and v3.0 shell rebuild plan  
**Baseline product:** v2.6.8 (`VERSION` / `Exo.csproj`) — “Exo Instrument” top-bar shell  
**Sources:** `AGENTS.md`, `docs/REWRITE-PROGRAM.md`, `Exo/**`, `tools/**`, `.github/workflows/**`, `.agents/skills/**`  
**Date:** 2026-07-16  
**Mode:** Read-only research (no product code changes)

---

## Executive summary

Exo’s shell is a **fixed 1180×760 WinUI 3 frame** with a **top liquid-glass circle nav** (not `NavigationView`, not a left rail). Modules share a **visual language** (`ExoModulePlate` + `FeatureTileGrid` + sticky `ExoActionBar`) but are still **four near-copy-pasted pages** with per-module footers and view-model shapes. Motion is deliberately constrained to **XAML Storyboards** after a v2.6.0 composition crash (`0xC000027B`). Services are a thin composition root (`AppServices`) over PowerShell elevation, kit materialization, GitHub update, and pure advisor text.

CI on `windows-latest` is strong for **build + format + repository integrity + pure smokes**, and has a real **elevated E2E job** for Discord / Steam / Internet script apply-detect-repair. Gaps remain: **release workflow does not require CI green**, **no NVIDIA GPU Apply on runners**, **no multi-NIC/hardware matrix**, and **no WinUI pixel/DPI automation** (only string/shape gates in `Ui.Smoke` + optional React `Exo.UiPreview`).

Phase 6 of `docs/REWRITE-PROGRAM.md` is the shell track for **v3.0**: shared plate, advisor v2, DPI strategy, motion audit.

---

## 1. UI architecture and design tokens

### 1.1 Frame and chrome

| Concern | Implementation | Path |
|--------|----------------|------|
| Window size | Fixed `1180×760`; `IsMaximizable=false`, `IsResizable=false`, min=max preferred size | `Exo/MainWindow.xaml.cs` → `ApplyFixedWindowChrome` |
| Title bar | `ExtendsContentIntoTitleBar`; `SetTitleBar(NavRail)` so whole top bar is draggable | same |
| Layout root | `RootGrid` padding **16**; row: Nav (Auto) → **12** gap → stage (`*`) | `Exo/MainWindow.xaml` |
| Nav | Transparent bar, equal **56px** end caps; modules centered `StackPanel` Spacing **10** | same |
| Content | `Frame ContentFrame` with stack disabled; pages own headers | same |
| Settings | Gear `Flyout` → `SettingsSheet` (not a page) | `MainWindow.xaml` + `Views/Controls/SettingsSheet.xaml` |
| Back | Only for `NvidiaPanel` → Nvidia optimizer | `ApplyChrome` |

**Shell modes** (`MainWindow.ShellMode`): `Home`, `Discord`, `Steam`, `Internet`, `Nvidia`, `NvidiaPanel`.

**Navigation:** `SuppressNavigationTransitionInfo` for all hops; soft enter via `ExoMotion.PlayPageEnter` on non-home pages (`OnContentNavigated`). Home first paint uses dashboard stagger; EXO home control **collapsed on Home** (page brand owns “Exo”).

### 1.2 Design system files

| Layer | File | Role |
|-------|------|------|
| Theme dictionaries (colors) | `Exo/App.xaml` | Dark AMOLED + Light cream; brush tokens |
| Shared styles / radii / type | `Exo/Styles/ThemeResources.xaml` | Typography, plates, buttons, glass circle |
| Runtime theme apply | `Exo/Services/ThemeService.cs` | `RequestedTheme` + title-bar button colors |
| Spacing craft skill | `.agents/skills/exo-ui-craft/SKILL.md` | 16 side / 12 gap / row heights 48→84 |
| Product shell rules | `AGENTS.md` § Shell UI | Fixed frame, AMOLED, motion bans |

### 1.3 Color tokens (Dark = product default)

From `App.xaml` ThemeDictionaries:

| Token | Dark | Light |
|-------|------|-------|
| Page | `#000000` pure black | `#F3EDE3` cream (`ThemeService.SoftStone`) |
| Glass fill | `#A80A0A0A` | `#E8FCFAF6` |
| Glass stroke | `#45FFFFFF` | `#803D3429` |
| Accent | `#F5F5F4` (stone white) | `#3D3429` |
| On accent | `#000000` | `#FCFAF6` |
| Primary text | `#FAFAF9` | `#1C1712` |
| Muted text | `#A1A1AA` | `#6B5F50` |
| Success / warning / error | emerald / amber / rose | darker equivalents |
| Settings sheet | `ExoSettingsAcrylicBrush` **solid** near-opaque (`#F00C0C0C`) — **not** real `AcrylicBrush` (startup crash class) | cream solid |

`ThemeService` mirrors cream as RGB `243, 237, 227` and forces panel background. `Ui.Smoke` asserts cream unity and forbids old `#F2EBE0` / live `AcrylicBrush`.

### 1.4 Spacing, radii, type scale

**ThemeResources scale** (comment: 8 / 12 / 16 / 20 / 28):

| Key | Value |
|-----|--------|
| `ExoCardRadius` | 22 |
| `ExoTileRadius` | 16 |
| `ExoGlassRadius` | 26 |
| `ExoPageMaxWidth` | 1120 |
| `ExoPagePadding` | 24,16,28,16 |
| Module plate corner | **14** (`ExoModulePlate`) |
| Feature row min height | **46**, padding 12,10 |
| Action bar padding | 16,8,16,10 |

**Fonts (bundled):**

- UI: `PlusJakartaSans.ttf` → `ExoUiFont` / Medium / SemiBold keys (same face; weight via `FontWeight`)
- Display italic: `CormorantGaramond-Italic.ttf` → `ExoDisplayFontItalic` / `ExoTagline`

**Type styles:** `ExoPageTitle` 28, `ExoFeatureTitle` 14, `ExoBody` 13, `ExoCaption` 12, `ExoSectionTitle` 11 tracked caps, module status titles overridden to 26/-24 on pages.

**exo-ui-craft rhythm** (agent reference, not fully encoded as XAML resources):

- Side margins 16; vertical gaps 12
- Row heights: header 48, segmented 56, slider 72, action cards 84

### 1.5 Structural styles (shell language)

| Style | Role |
|-------|------|
| `ExoModulePlate` | One edge-glass surface for header + list + foot |
| `ExoFeatureTile` | Hairline bottom border rows (not separate pills) |
| `ExoActionBar` | Sticky foot; specular top edge |
| `ExoStatusRail` | 3px success rail; opacity driven by applied state |
| `ExoMessageBanner` | Advisor / result / warning strips |
| `ExoPrimaryButton` / `ExoQuietButton` | Solid accent vs glass secondary; **hover wash, no scale** |
| `ExoCardButton` | Home metric hover ring language |
| `ExoGlassCircle` | 44×44 circle nav; gradient rim + hairline border |
| `ExoRailButton` | Base for rail (legacy left-rail comments still present) |
| `ExoThemeChoice` | Dark/Light radios with layered hover/checked fills |

### 1.6 Page map

| Page | ViewModel | Layout pattern |
|------|-----------|----------------|
| `DashboardPage` | `DashboardViewModel` | Plate + hero brand + 2×2 metrics (FPS, frame time, RAM, latency) |
| `DiscordOptimizerPage` | `DiscordOptimizerViewModel` | Standard module plate |
| `SteamOptimizerPage` | `SteamOptimizerViewModel` | Same + trim stats + apply report |
| `InternetOptimizerPage` | `InternetOptimizerViewModel` | Same + dual primary CTAs + report + benchmark |
| `NvidiaOptimizerPage` | `NvidiaOptimizerViewModel` | Same + G-SYNC toggle + Display panel + Reset honesty caption |
| `NvidiaPanelPage` | `NvidiaPanelViewModel` | Separate panel craft (sliders/rows); not full plate clone |

**Shared controls:**

- `FeatureTileGrid` — `ItemsRepeater` vertical list of `FeatureRowViewModel` (title + Applied/Not applied + rail)
- `ExoLoader` — orbit bead busy indicator
- `SettingsSheet` — appearance / updates / report issue / logs / version

### 1.7 Dashboard specifics

- Four-metric home only; **no Detect\* probes on home** (`AGENTS.md`)
- FPS / frame time stay `—` until capture ships; RAM / latency from LocalAppData via `HomeDashboardReader`
- Cached so return navigation does not re-stagger
- Metrics use nested glass cards (radius 12 / 10), not module feature rails

### 1.8 Agent UI skills (relevant to shell)

| Skill | Use for shell v3 |
|-------|------------------|
| `exo-ui-craft` | Spacing rhythm on plates/stacks |
| `better-ui` | Surfaces, hover, shadows-vs-borders, polish |
| `better-colors` | Token contrast / OKLCH if redesigning palette |
| `better-typography` | Type scale / tracking audit |
| `emil-design-eng` | Restraint, feedback quality |
| `frontend-design` | Distinctive direction without template defaults |
| `kinetics-spring-motion` | Curves — **adapt only**; product forbids spring bounce on content |
| `12-principles-of-animation` / `find-animation-opportunities` / `improve-animations` / `review-animations` | Motion audit plans (must respect Storyboard-only rule) |
| `apple-design` | Materials/depth inspiration; **do not** reintroduce composition hand-off |
| `web-design-guidelines` | A11y checklist mindset (web-oriented; map carefully to WinUI) |

`tools/Exo.UiPreview` is a **React mock** of the rail layout for Linux click QA — not the real WinUI app; keep out of public marketing (`AGENTS.md`).

---

## 2. Motion safety rules and residual risks

### 2.1 Hard product rules (`AGENTS.md` + `ExoMotion` + `Ui.Smoke`)

1. **Only XAML `Storyboard` / `DoubleAnimation` on XAML properties** (`Opacity`, `CompositeTransform.TranslateY`, `RotateTransform.Angle`, `ScaleTransform` on loader core).
2. **Never** `ElementCompositionPreview` / hand-off `Visual.Offset|Scale|Opacity` — detaches layout (elements pile at origin) and can crash real GPUs with **`0xC000027B`** (v2.6.0 black-flash launch regression).
3. **No spring bounce on content**; short ease-out only.
4. **No scale transforms on content with logos** (softens bitmaps); hover = wash / ring / opacity.
5. **Integer pixel rise** on enter; **drop `RenderTransform` after complete** so type stays crisp (`EnsureVisible` / `ResetVisual`).
6. **No real `AcrylicBrush` in startup-parsed XAML** (settings sheet uses solid brush token).

### 2.2 Crash-loop safe mode

`StartupLog` (`%LocalAppData%\Exo\logs\startup.log`):

- Writes phases; clears file each launch after reading previous
- If previous launch never wrote `first-frame-rendered`, `PreviousLaunchDiedBeforeFirstFrame == true`
- `MainWindow` ctor sets `ExoMotion.MotionDisabled = true` → all entrances collapse to `EnsureVisible`

First frame proof: `CompositionTarget.Rendered` one-shot → `StartupLog.Mark("first-frame-rendered")`.

### 2.3 Motion API surface (`Exo/Helpers/ExoMotion.cs`)

| API | Timing | Behavior |
|-----|--------|----------|
| `EntranceMs` | 240 | Card enter |
| `FadeMs` | 180 | Opacity |
| `StaggerStepMs` | 22 | Dashboard cards |
| `ListStaggerStepMs` | 28 | Feature rows |
| `SelectMs` | 90 | Dim pulse (no scale) |
| `PlayPageEnter` | ~200 fade 0.9→1 | Module pages |
| `PlayListEnter` | waits up to ~24 frames for repeater | Module feature grids |
| Easing | `CubicEase EaseOut` (`Glide`) | All content |

`EnableDependentAnimation = true` is used on opacity/translate animations so they run even if not GPU-independent — **allowed residual** (Storyboard path), not composition Visual writes.

### 2.4 Other Storyboard sites

| Site | Motion |
|------|--------|
| `ExoLoader` | Forever orbit + core breath scale (loader only) |
| `SettingsSheet` | Open/close fade + soft drop/rise |
| `MainWindow` gear | 180° crank synced to sheet open/close |
| Button VSM templates | Hover wash / ring opacity ~80–160ms |

### 2.5 Residual risks (shell v3 must track)

| Risk | Why it remains |
|------|----------------|
| `EnableDependentAnimation` load | Loader forever + many short fades can tax weak GPUs; low priority |
| Task.Run + `DispatcherQueue` hit-test enable | Race if page unloaded mid-stagger |
| MotionDisabled only entrance | Loader still spins in safe mode if bound active |
| Reintroducing composition | High severity; `Ui.Smoke` greps for `ElementCompositionPreview` in motion helpers |
| Scale on logos via “polish” PRs | Soft type/logos; AGENTS + skill temptation conflict |
| UiPreview ≠ WinUI | Agents may green-light layout that fails WinUI composition |
| Dependent animation on Transform | Historical soft-blur if transform not cleared — mitigated by drop-null pattern |
| First-frame vs Activated | Icon re-apply / focus clear are one-shot; race on multi-monitor rare |

---

## 3. Duplication across module pages (shared plate opportunity)

### 3.1 Common plate skeleton (all four optimizers)

```
Border ExoModulePlate
  Grid
    Row0 *  — content
      Padding 16,12,16,8
      StackPanel header:
        SECTION TITLE (caps style)
        Status / HeaderStatus (ExoPageTitle 26)
        MessageBanner → GuidanceText (advisor)
      [module-specific controls]
      Grid:
        ExoLoader (busy/detect)
        FeatureTileGrid
    Row1 Auto — ExoActionBar
      [result banner]
      [progress bar + status]
      [optional apply report expander]
      Primary Apply CTA(s)
      Repair/Reset + Refresh
```

### 3.2 Duplication matrix

| Surface | Discord | Steam | Internet | NVIDIA |
|---------|---------|-------|----------|--------|
| Plate + header + advisor banner | ✓ | ✓ | ✓ | ✓ |
| FeatureTileGrid + list enter | ✓ | ✓ | Rows | ✓ |
| Progress bar block | ✓ | ✓ | status text only | ✓ |
| Apply report expander | ✓ | ✓ | ✓ | ✗ (no UI) |
| Primary CTA | Apply | Apply | **Low latency + Highest download** | Apply |
| Secondary | Repair / Refresh | Repair / Refresh | Repair / Refresh | **Reset** / Refresh |
| Extra header | — | — | — | G-SYNC + Display panel |
| Honesty caption | — | — | RepairHint | “Reset clears status only…” |
| Page code-behind | almost identical (~48 lines) | same | same + dual clicks | + Panel_Click |

Page code-behind pattern (copy-pasted four times):

1. Construct VM with `App.Services`
2. `OnNavigatedTo` → `InitializeAsync`
3. First `IsFeatureListVisible` → `ExoMotion.PlayListEnter`
4. Click → `Command.Execute` / method

### 3.3 ViewModel duplication

Shared concepts reimplemented per module:

- `IsBusy`, progress percent/status, last result glyph/brush
- `GuidanceText` / `HasGuidance` via `OptimizerAdvisor.Build`
- `Features` vs Internet `Rows` (same `FeatureRowViewModel` shape, different property name)
- Apply report load: Discord/Steam via `OptimizerStateService.TryReadApplyReport`; Internet via `NetworkOptimizerService.LoadLastApplyReport`
- Detect + elevate apply/repair orchestration with module-specific scripts

### 3.4 Shared plate opportunity (PR-6.2 target)

**Proposed control** (name illustrative): `ExoModuleHost` / `OptimizerPlate`

| Slot | Content |
|------|---------|
| `Header` | Section title + bindable status |
| `Advisor` | Guidance banner (built-in) |
| `Chrome` | Optional: G-SYNC row, dual modes, etc. |
| `Features` | `IEnumerable` → FeatureTileGrid + loader |
| `Results` | Last result + progress + optional report |
| `Actions` | `ICommand` primary(s), secondary Repair/Reset, Refresh |
| Behavior | List enter, Automation names via props |

**Expected payoff:**

- One place for padding / spacing / advisor chrome
- Advisor v2 UI without four XAML edits
- Reduce drift (Internet already diverged: dual CTA row, no ProgressBar twin, report placement)
- Page files shrink to chrome slots + VM wire-up

**Do not over-abstract:** NVIDIA panel stays separate; Internet dual-primary remains first-class action config, not forced into single Apply.

---

## 4. Advisor system — current vs desired

### 4.1 Current (v1) — `Exo/Services/OptimizerAdvisor.cs`

- **Pure static** `Build(module, isApplied, statusText, detailText, features)` — no I/O; smokeable
- Outputs one prose string:
  - All applied → module-specific re-apply hint
  - Missing features → “Next: Apply… still open: A, B, …”
  - Else status / generic Apply
  - Append short detail if &lt; 180 chars
  - Hardware/install hints from blob keywords (no NVIDIA GPU, Discord/Steam not installed, no physical NIC)
  - Always appends: “No Exo background tasks…”
- Wired from Discord / Steam / Internet / NVIDIA VMs into `GuidanceText` + `HasGuidance`
- UI: `ExoMessageBanner` + info glyph under title

### 4.2 Limitations of v1

| Gap | Impact |
|-----|--------|
| Flat string only | No step list, no severity, no deep-link actions |
| Not mapped to live `EXO_PROGRESS` | Progress bar text ≠ advisor during Apply |
| Not mapped to `EXO_REPORT` steps | Report expander is separate and post-hoc |
| “Applied” truth = detect rows | Detect/apply drift still possible (program Phase 1) |
| Internet dual modes not reflected | Advisor does not explain latency vs throughput choice |
| NVIDIA Reset honesty | Caption in XAML, not advisor engine |
| No “what this PC needs” structured model | Hard to unit-test richer guidance |

### 4.3 Desired (v2) — from REWRITE-PROGRAM PR-6.3

1. **Structured advisor model** (still pure C#):
   - `NextAction` (Apply / Repair / Install X / Open Panel)
   - `OpenFeatures[]` with ids matching contract table
   - `Blockers[]` (no GPU, no install, no elevation)
   - `HonestyNotes[]` (NVIDIA reset ≠ rollback)
2. **Live apply mode:** merge `EXO_PROGRESS` status + rolling `EXO_REPORT` lines into advisor body (“Applying: profile import…”)
3. **Post-apply:** summarize fail/skip counts; point to expandable report
4. **Internet:** mode-aware (“Low latency leaves Wi‑Fi up…”)
5. **UI:** optional compact step list under banner without growing plate height chaos
6. **Smokes:** golden strings / object snapshots per fixture detect JSON

---

## 5. Elevation / progress / reporting pipeline

### 5.1 Composition root

`AppServices` (`Exo/Services/AppServices.cs`):

| Service | Responsibility |
|---------|----------------|
| `SettingsService` | Persist theme, auto-update, kit versions |
| `ThemeService` | Apply theme |
| `PowerShellRunnerService` | Run scripts; single-flight gate |
| `ScriptBundleService` | Materialize kits under LocalAppData |
| `OptimizerStateService` | Detect Discord/Steam/NVIDIA; read apply reports |
| `GitHubUpdateService` | App + script kits from GitHub |
| `NvidiaPanelSettingsService` | Panel apply |
| `NetworkOptimizerService` | Internet apply/repair/benchmark (own elevation paths) |

`Initialize()`: fire-and-forget kit stamp + PowerShell 7 ensure + warm Get\*Root (off first paint).

### 5.2 Script materialization (`ScriptBundleService`)

- Working kits: `%LocalAppData%\Exo\...` working scripts dir
- **Stamp file** `.app-kit-stamp` = app version; mismatch → **full replace** Discord/Steam/NVIDIA from bundled `Exo/Scripts/*` (no merge of leftovers)
- Entry scripts: `Exo-*-Run/Detect/Repair.ps1`
- Custom Discord path override via settings

### 5.3 PowerShell runner

**Non-elevated:** `pwsh` redirect stdout/stderr; env `EXO`, `EXO_LOG`, noninteractive flags; parse `EXO_PROGRESS:n|status`.

**Elevated silent path:**

1. Write wrapper PS + VBS `ShellExecute runas` (window style 0)
2. Launch via `wscript.exe //B`
3. Poll log for progress; exit file for code
4. 30s without log → treat as elevation cancelled
5. 25 min timeout
6. Cancellation → cancel file + process kill

**Progress contract:**

```
EXO_PROGRESS:<0-100>|<status text>
```

Regex in `PowerShellRunnerService`.

### 5.4 Apply report contract

**Stream form (scripts):**

```
EXO_REPORT:<step>|ok
EXO_REPORT:<step>|fail:<reason>
EXO_REPORT:<step>|skip:<reason>
```

**Persisted form:**

- Discord/Steam: `%LocalAppData%\Exo\{discord|steam}-optimizer.json` → `applyReport` string array
- Internet: network state / last apply report via `NetworkOptimizerService` + snapshot JSON
- UI: `ApplyReportPresentation` → expandable list in foot (Discord/Steam/Internet)

**Detect path:** non-elevated detect scripts emit JSON line with `isApplied`, `statusText`, `features[]` (`title`/`detail`/`active`); `OptimizerStateService` falls back to heuristics if script fails.

### 5.5 Elevation usage by module

| Module | Detect | Apply | Repair/Reset |
|--------|--------|-------|--------------|
| Discord | no elev | elev | elev |
| Steam | no elev | elev | elev |
| NVIDIA | no elev | elev | Reset often non-elev status clear |
| Internet | service/snapshot | elev script from builder | elev repair |
| NVIDIA Panel | mixed | elev for hard paths | elev wipe optional |

### 5.6 Single-flight / concurrency

- `_runGate` SemaphoreSlim(1) on `PowerShellRunnerService` — one script run at a time globally
- Script update gate on GitHub kit refresh
- UI VMs set `IsBusy` to disable CTAs

### 5.7 Logging

- `%LocalAppData%\Exo\logs\run-*.log`, `exit-*.txt`, wrappers
- Settings “Open logs”
- Startup breadcrumbs separate file

---

## 6. Update / install paths

### 6.1 Fresh install

| Path | Flow |
|------|------|
| **Release asset** | GitHub Releases: only **`Exo.exe`** SFX (double-click) |
| `Install-Exo.ps1` | Fetch latest release `Exo.exe`, size + **SHA-256 digest** + version stamp check → launch SFX → dependency doctor (stable PS7) |
| SFX (`tools/ExoSfx.cs` via `Publish-Exo.ps1`) | Extract to `%LocalAppData%\Exo\app`, launch app |
| Start menu | `MainWindow.TryRepairStartMenuShortcut` rewrites `Programs\Exo.lnk` icon/target |

### 6.2 Local publish / release scripts

| Script | Role |
|--------|------|
| `Publish-Exo.ps1` | `dotnet publish` self-contained win-x64 + ReadyToRun → zip intermediate → compile SFX `release/Exo.exe` |
| `Release-Exo.ps1` | Requires clean **main == origin/main**; runs Publish; `gh release` with Exo.exe only; optional prune old releases; tag `v$VERSION` |
| `Run-Exo.ps1` | Dev convenience (not release) |
| `tools/Bump-Version.ps1` | Version bumps |

**Release guards:** dirty tree refused; wrong branch refused; local main SHA must match remote.

### 6.3 In-app app update (`GitHubUpdateService` + `ExoUpdateDialog`)

1. `CheckAppUpdateAsync` → `GET .../releases/latest`, prefer asset `Exo.exe` + digest
2. Compare assembly version vs tag
3. Confirm dialog “Update now / Later”
4. `InstallAppUpdateAsync` downloads to `%LocalAppData%\Exo\updates\Exo-Setup.exe` (clears old Exo*.exe), verifies size/SHA/PE version when available, launches SFX, `ShouldExit` → app exits so files unlock
5. Settings sheet: manual check + progress; toggle **Check on launch** (`AutoUpdateScripts` — name historical; gates **app** auto-check)

`MaybeAutoUpdateAsync` in `MainWindow`: if setting on, delay ~1.2s, check, confirm, install.

HTTP client timeout **30 minutes** (large installer on slow links).

### 6.4 Script kit update (not full app)

`CheckAndUpdateAllScriptsAsync`:

- Reads remote `VERSION` files for Discord/Steam/NVIDIA from configured repo/branch
- Downloads branch zip from codeload
- Replaces only kits that are behind
- Used when users want script hotfixes without full app ship (settings-adjacent / service API)

App upgrade path **also** forces kit stamp replace from bundled scripts so UI and PS stay matched.

### 6.5 PowerShell runtime

`PowerShellRunnerService.EnsurePowerShellRuntimeAsync` — winget/portable ensure for **stable PowerShell 7** (not Windows PowerShell 5.1 for product runs). Dependency doctor in install script aligns same goal.

---

## 7. CI gaps (release independence, hardware Apply, shell proof)

### 7.1 Workflow inventory

#### `ci.yml` — on PR/push to `main`

**Job `build-and-validate` (windows-latest):**

1. `dotnet build Exo.sln` + `Exo.NvDisplay`
2. Native AOT publish **probe** (`continue-on-error`)
3. `dotnet format --verify-no-changes`
4. Compile `tools/ExoSfx.cs` with Framework `csc`
5. `tools/Test-Repository.ps1`
6. Smokes: Ui, Network, Discord, Steam, Nvidia (`dotnet run` Release)

**Job `e2e-optimizers` (needs build-and-validate):**

- Install Steam + Discord (winget or vendor silent setup); seed userdata / session stores for noninteractive Apply
- Discord Apply → detect isApplied → Repair → detect false
- Steam Apply → detect → Repair
- Internet: `NetScriptDump` → elevated apply → snapshot exists → repair
- Startup publish probe (informational window timing)
- Failure log artifact upload

Honest comment in workflow: **no NVIDIA GPU** on hosted runners → NVIDIA Apply/DRS not in E2E.

#### `release.yml` — `workflow_dispatch` or tag `v*`

- Checkout **main**
- Setup .NET 8 + 10
- Run `./Release-Exo.ps1 -ReplaceExisting -PruneOldReleases`
- **`needs:` CI — ABSENT**

### 7.2 Gap analysis

| Gap | Detail | Severity |
|-----|--------|----------|
| **Release without CI** | Tag/dispatch can publish while `main` CI red or mid-fail | **Ship blocker** for program |
| **No workflow_run / environment gate** | No required check on GitHub side in-repo | High |
| **NVIDIA HW Apply** | Smokes = shape/logic only; no profile import proof in CI | High for GPU module |
| **Internet HW matrix** | E2E is server NIC, not Wi‑Fi-only / dual-NIC / VPN fixtures | Medium–High |
| **Discord/Steam realism** | Seeded session/userdata; not real login | Medium (still valuable) |
| **WinUI UI automation** | `Ui.Smoke` is static string/structure asserts; no click driver on real app | Medium for shell |
| **DPI / a11y CI** | None | Medium for v3 shell |
| **AOT** | Informational only; ship is ReadyToRun | Low |
| **Linux agents** | `Test-Linux.ps1` + smokes without GUI build | Process note, not CI gap |
| **Format + SFX** | Windows-only steps — correct for product | OK |

### 7.3 `Test-Repository.ps1` coverage (shape, not Apply)

- VERSION files semantic; match `Exo.csproj`
- Discord/Steam/NVIDIA version markers in god-scripts + settings defaults
- All `*.ps1` parse + ASCII-only
- Embedded Steam helper markers
- NVIDIA profile/XML/data checks (continued past first 150 lines in full script)
- Forbidden patterns / kit integrity (see full script in tree)

### 7.4 Smoke tools (pure / non-UAC)

| Project | Proves |
|---------|--------|
| `Ui.Smoke` | Shell structure tokens, motion bans, theme unity, version match gates |
| `Network.Smoke` | NetworkLogic, builder markers, EXO_REPORT parsing, fixtures |
| `Discord.Smoke` / `Steam.Smoke` / `Nvidia.Smoke` | DetectLogic + script markers + report emitters |
| `NetScriptDump` | Dump generated Internet scripts for E2E |

---

## 8. Shell rebuild PR plan (maps to REWRITE Phase 6 + gates)

Aligned with `docs/REWRITE-PROGRAM.md` Phase 6 and §10 PR order. Shell work ships as **v3.0** after module contracts stabilize; partial shell slices may land earlier as non-breaking.

### 8.1 Prerequisites (before heavy UI)

| PR | Content | Exit |
|----|---------|------|
| **S0 / CI** | `release.yml` waits for CI green on same SHA (or required check + manual hold documented) | Cannot publish red main |
| **S0b** | Shared `EXO_REPORT` schema + no `Exo-*` scheduled task audit smoke | Cross-module honesty |
| Module Phases 2–5 slices | Reduce detect/apply drift feeding advisor lies | Advisor v2 trustworthy |

### 8.2 Shell PR stack (Phase 6)

| PR | Title | Scope | Tests / gates | Forbidden |
|----|-------|-------|---------------|-----------|
| **6.1** | Design tokens audit | `App.xaml` + `ThemeResources` + `exo-ui-craft` alignment; document token table in docs | `Ui.Smoke`; no visual regressions on Dark/Light | No composition; no AcrylicBrush reintroduction |
| **6.2** | Shared module plate | New control; migrate Discord → Steam → Internet → NVIDIA; delete duplicated XAML | Ui.Smoke structure; manual nav smoke; module smokes unchanged | Behavior changes to Apply |
| **6.3** | Advisor v2 | Model + bindings + progress/report merge; Internet mode awareness | Unit tests in smokes; golden fixtures | God-script growth |
| **6.4** | DPI / frame strategy | Decide: keep fixed 1180×760 + content scale vs snap sizes (900/1180/1440); implement one | Manual 100/125/150/200% DPI QA checklist | Random free resize without redesign |
| **6.5** | Motion audit | Grep composition; inventory `EnableDependentAnimation`; safe-mode coverage for loader | Ui.Smoke ElementComposition ban; crash-loop safe mode test notes | New springs on content |
| **6.6** | Settings / update polish | Settings sheet density + update UX consistent with plate | Manual update dry-run | Background Exo tasks |
| **6.7** | UiPreview sync | React mock matches plate + top bar for agent click QA | `npm run preview:click` | Marketing README noise |

### 8.3 Suggested execution order (coordinator)

1. Land **CI release gate** (unblocks trustworthy shipping during rebuild).  
2. **6.1 tokens** (cheap, reduces thrash).  
3. **6.2 shared plate** starting with Discord+Steam (identical feet).  
4. Internet plate migration (action slot config).  
5. NVIDIA plate + panel spacing pass.  
6. **6.3 advisor v2** on shared host.  
7. **6.4 DPI decision** + implement.  
8. **6.5 motion** final audit → tag **v3.0.0** when modules also on contracts.

### 8.4 Ownership / non-overlap (AGENTS team structure)

| Owner slice | Files |
|-------------|-------|
| Shell chrome | `MainWindow.*`, `ThemeResources`, `App.xaml`, `ThemeService` |
| Shared plate | `Views/Controls/ExoModule*` (new), four optimizer pages |
| Advisor | `OptimizerAdvisor.cs`, VMs guidance, smokes |
| Motion | `ExoMotion.cs`, `ExoLoader`, SettingsSheet motion, Ui.Smoke greps |
| CI | `.github/workflows/*`, `Test-Repository.ps1` only as needed |

Executors must not release or run real Apply unless authorized.

---

## 9. DPI / accessibility / i18n

### 9.1 DPI

**Current:**

- Fixed **physical** window size 1180×760 via `AppWindow.Resize` / preferred min=max
- WinUI scales with system DPI; layout is **logical** px at design DPI
- Logos: decode/layout notes in converters / changelog — sharp buffers for downscale
- `UseLayoutRounding="True"` widespread to reduce blur
- No `RasterizationScale` custom handling; no per-monitor redesign modes

**Risks:**

| DPI | Risk |
|-----|------|
| 100% | Baseline design |
| 125–150% | Fixed frame may clip or feel cramped on small laptops |
| 200%+ | 1180 logical may exceed work area; center still attempts work-area center |
| Multi-monitor mixed DPI | Move/center best-effort; no explicit handler |

**v3 decision options (PR-6.4):**

1. **Keep fixed frame** + scale internal content only (simplest product craft)  
2. **Snap sizes** (e.g. 960 / 1180 / 1400) with same plate reflow  
3. **Allow resize within band** with min size and reflow tests  

Recommendation for Instrument brand: **(1) or (2)** — free resize fights glass-circle symmetry.

### 9.2 Accessibility

**Present:**

- `AutomationProperties.Name` on nav, settings, primary CTAs, many secondaries
- Decorative logo images: `AccessibilityView="Raw"`
- `UseSystemFocusVisuals="True"` on custom buttons
- Initial focus moved off chrome to content (`ClearChromeFocus`)
- Settings update status: `LiveSetting="Polite"`
- Keyboard: buttons are tab stops; no full keyboard map documented
- High-contrast: not specially themed (depends on WinUI + custom brushes)

**Gaps:**

| Area | Gap |
|------|-----|
| Feature rows | Not interactive controls; status is text only (OK) but no live region on detect refresh |
| Progress | No `LiveSetting` on apply progress text |
| Advisor | Not announced as live region |
| Color contrast | Dark stone-on-black generally OK; muted zinc may fail strict WCAG on some pairs — needs audit (`better-colors`) |
| Screen reader order | Top bar then frame; flyout focus trap is platform default |
| Reduced motion | No `UISettings.AnimationsEnabled` / accessibility setting; only crash safe mode |
| Tooltips | Explicitly avoided on main (`Ui.Smoke` no ToolTip) — names must carry weight |

### 9.3 Internationalization

**Current: English-only hardcoded UI strings** in XAML and C# (titles, advisor, buttons, dialogs).

| Concern | State |
|---------|-------|
| `x:Uid` / `.resw` | Not used |
| Pseudo-loc / RTL | No `FlowDirection` strategy |
| Date/number | Minimal UI need |
| Script messages | PowerShell English |
| String length | Fixed frame + long advisor prose may wrap awkwardly; no max-line discipline beyond wrap |

**v3.0 stance (recommended):** declare **en-US only** for v3; reserve resw extraction for post-3.0 if needed. Still fix **truncation / wrap** for long English honesty strings.

---

## 10. Acceptance criteria for v3.0 shell

Ship **v3.0.0** shell when **all** of the following are true (in addition to module contract readiness from Phases 1–5).

### 10.1 Product / visual

- [ ] Fixed Instrument shell retained unless PR-6.4 documents a replacement frame strategy
- [ ] AMOLED dark + cream light tokens documented and `Ui.Smoke`-gated
- [ ] Top bar: liquid-glass circles, EXO hidden on home, settings gear flyout (not page)
- [ ] **One shared module plate** used by Discord, Steam, Internet, NVIDIA (panel may remain special)
- [ ] Feature rows: status rail + Applied/Not applied from live detect
- [ ] NVIDIA Reset copy never claims driver rollback
- [ ] Home four-metric dashboard; no Detect probes on home

### 10.2 Motion / reliability

- [ ] Zero `ElementCompositionPreview` usage in product UI code
- [ ] Zero startup `AcrylicBrush` in MainWindow/Settings parse path
- [ ] Storyboard-only entrances; no content scale bounce
- [ ] Crash-loop: previous no-first-frame → `MotionDisabled` → app still shows UI
- [ ] First-frame marker still written; startup.log useful for support

### 10.3 Advisor / honesty

- [ ] Advisor v2 (or documented v1.5+) driven by detect feature ids
- [ ] During Apply, user-visible status matches `EXO_PROGRESS` (no stuck “Starting…”)
- [ ] Post-apply report reachable for Discord/Steam/Internet; NVIDIA has honesty path
- [ ] Footer always states or links the no-Exo-background-tasks rule where Apply is offered

### 10.4 Services / update

- [ ] Kit stamp full-replace on app version change
- [ ] Elevated apply remains silent PS7; cancel elevation surfaces clear error
- [ ] In-app update: SHA when GitHub provides digest; exit for replace
- [ ] Install path: `Install-Exo.ps1` + SFX still green

### 10.5 CI / release discipline

- [ ] **Release job requires CI success** on the released commit (or equivalent required check)
- [ ] `Test-Repository` + all five smokes on every PR
- [ ] E2E Discord/Steam/Internet still green (or explicitly quarantined with owner)
- [ ] NVIDIA remains human checklist + optional self-hosted GPU runner if available
- [ ] Human §6 checklist signed for shell-facing release notes

### 10.6 DPI / a11y / i18n (minimum bar)

- [ ] Written DPI strategy + manual pass at 100% / 125% / 150%
- [ ] All interactive chrome has `AutomationProperties.Name`
- [ ] Focus not trapped on invisible controls after nav
- [ ] Reduced-motion: either honor system setting **or** document intentional exception with safe-mode only
- [ ] en-US only OK if declared; no clipped primary CTAs at 150% DPI

### 10.7 Verification commands (ship checklist)

```text
pwsh -NoProfile -File ./tools/Test-Linux.ps1          # Linux/cloud
# or Windows:
dotnet run --project tools/Ui.Smoke -c Release
.\tools\Test-Repository.ps1
dotnet run --project tools/Network.Smoke -c Release
dotnet run --project tools/Discord.Smoke -c Release
dotnet run --project tools/Steam.Smoke -c Release
dotnet run --project tools/Nvidia.Smoke -c Release
.\Publish-Exo.ps1
# install to %LocalAppData%\Exo\app — manual shell QA
# Release-Exo.ps1 only when intentional + CI green
```

Optional: `tools/Exo.UiPreview` click QA for layout language only.

---

## Appendix A — File map (shell + cross-cutting)

```
Exo/
  App.xaml / App.xaml.cs          Theme dictionaries, app lifetime
  MainWindow.xaml(.cs)            Shell chrome, nav, settings flyout, auto-update
  Styles/ThemeResources.xaml      Styles + converters refs
  Helpers/ExoMotion.cs            Safe motion
  Helpers/ExoUpdateDialog.cs      Update UI
  Helpers/StartupLog.cs           Crash-loop breadcrumbs
  Helpers/UiStatusPresentation.cs Status tone helper
  Views/DashboardPage.*
  Views/*OptimizerPage.*
  Views/NvidiaPanelPage.*
  Views/Controls/ExoLoader.*
  Views/Controls/FeatureTileGrid.*
  Views/Controls/SettingsSheet.*
  ViewModels/*                    Per-module VMs + ApplyReport*
  Services/AppServices.cs
  Services/OptimizerAdvisor.cs
  Services/PowerShellRunnerService.cs
  Services/ScriptBundleService.cs
  Services/GitHubUpdateService.cs
  Services/OptimizerStateService.cs
  Services/ThemeService.cs
  Services/NetworkOptimizerService.cs
  Services/SettingsService.cs
tools/
  Test-Repository.ps1
  Test-Linux.ps1
  Ui.Smoke / Network.Smoke / Discord.Smoke / Steam.Smoke / Nvidia.Smoke
  Exo.UiPreview/                  Agent mock shell
  ExoSfx.cs                       Installer source
Publish-Exo.ps1 / Release-Exo.ps1 / Install-Exo.ps1
.github/workflows/ci.yml / release.yml
AGENTS.md
docs/REWRITE-PROGRAM.md
docs/rewrite/research-shell-ci.md  ← this document
.agents/skills/exo-ui-craft (+ better-ui, motion skills)
```

## Appendix B — Duplication LOC snapshot (approximate)

| Asset | Nature |
|-------|--------|
| Discord/Steam XAML | ~175 lines nearly isomorphic |
| Internet XAML | ~200 lines; dual CTA + report + benchmark |
| NVIDIA XAML | ~155 lines; G-SYNC + Reset caption |
| Page `.xaml.cs` ×4 | ~48 lines each, clone |
| Advisor call sites | 4 VMs, same `Build` signature |

Shared plate should reclaim most of the XAML isomorphism without merging VMs prematurely.

## Appendix C — Relationship to rebuild program phases

| Program phase | Shell relevance |
|---------------|-----------------|
| 0 Freeze | Shell rules already in AGENTS |
| 1 Shared engine | EXO_REPORT feeds advisor v2 + report UI |
| 2–5 Modules | Content of feature rows; shell should not block |
| **6 Shell** | This research’s primary execution track |
| 7 HW matrix | Does not replace shell DPI QA; complements release discipline |

---

## Appendix D — Open questions for human/coordinator

1. **DPI:** Prefer fixed 1180 forever vs snap sizes?  
2. **Reduced motion:** Honor OS setting in v3 or keep crash-safe-only?  
3. **Release gate:** Hard `needs: CI` vs environment protection rules on GitHub?  
4. **Advisor v2 depth:** Banner-only vs step checklist UI on first plate migration?  
5. **UiPreview:** Keep as agent-only, or invest to mirror shared plate 1:1?

---

*End of research inventory. No product code was modified; this file is planning input for the v3.0 shell rebuild.*
