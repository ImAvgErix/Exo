# NVIDIA module — deep architecture inventory (rewrite research)

**Status:** Read-only inventory for Phase 5 (REWRITE-PROGRAM.md)  
**Pack version:** `Exo/Scripts/Nvidia/VERSION` = **1.12.0**  
**Profile pack:** `profiles/PROFILE_VERSION` = **1.4.0**  
**God-script:** `Exo/Scripts/Nvidia/Nvidia-Optimizer.ps1` (~4.3k lines)  
**Date:** 2026-07-16  
**Scope:** No product code changes; architecture truth for split + acceptance.

---

## 0. Map of the surface

### Entry points (UI → scripts)

| UI action | C# | Script | Elevates? |
|-----------|-----|--------|-----------|
| Apply / Reapply | `NvidiaOptimizerViewModel.RunAsync` | `Exo-Nvidia-Run.ps1` → `Nvidia-Optimizer.ps1` | Yes |
| Reset (labeled repair) | `NvidiaOptimizerViewModel.RepairAsync` | `Exo-Nvidia-Repair.ps1` → `Nvidia-Optimizer.ps1 -Repair` | **No** |
| Detect / refresh | `OptimizerStateService.DetectNvidiaAsync` | `Exo-Nvidia-Detect.ps1` (+ `NvidiaDetectCore.ps1`) | No |
| Display panel prefs | `NvidiaPanelSettingsService` | `Exo-Display-Apply.ps1` + `Exo.NvDisplay.exe` | Yes (apply) |
| Tray clear (panel/helper) | `NvidiaPanelSettingsService` | `Exo-Nvidia-TrayClear.ps1` | Best-effort |

Script paths resolved by `ScriptBundleService` under `%LocalAppData%\Exo\...` after bundle extract, with source at:

```
Exo/Scripts/Nvidia/
  Nvidia-Optimizer.ps1          # god-script (all stages)
  Exo-Nvidia-Run.ps1            # thin Apply wrapper (pwsh 7 required)
  Exo-Nvidia-Repair.ps1         # thin -Repair wrapper
  Exo-Nvidia-Detect.ps1         # live detect JSON
  NvidiaDetectCore.ps1          # pure classifiers (dot-sourced)
  Exo-Display-Apply.ps1         # display stage executor
  Exo-Nvidia-TrayClear.ps1      # tray + purge Exo tasks
  Exo-ColorDepth-Set.ps1        # legacy/CPL helpers (secondary)
  Exo-Cpl-ApplyDisplay.ps1
  Exo-Cpl-ScalingCommit.ps1
  Exo-Cpl-VideoOnly.ps1
  Exo-NvCpl-Scale.ps1
  profiles/*.nip + PROFILE_VERSION + README.md
  tools/Exo.NvDisplay.exe       # self-contained NVAPI helper + deps
```

### C# pure / services

| File | Role |
|------|------|
| `Exo/Services/NvidiaDetectLogic.cs` | Pure series/tray/DRS/display classifiers; apply-script audit markers |
| `Exo/Services/NvidiaPanelLogic.cs` | Pure CLI arg builders for panel (mode/depth/scaling/vibrance) |
| `Exo/Services/NvidiaPanelSettingsService.cs` | Load/save `nvidia-panel-settings.json`; probe + invoke display helper |
| `Exo/Services/OptimizerStateService.cs` | `DetectNvidiaAsync` + fast heuristic from marker |
| `Exo/Models/NvidiaPanelSettings.cs` | Panel defaults (primary max Hz, secondary 60, Full RGB, GPU no-scale…) |
| `Exo/ViewModels/NvidiaOptimizerViewModel.cs` | Module plate Apply/Reset/G-SYNC |
| `Exo/ViewModels/NvidiaPanelViewModel.cs` | Live panel (modes, vibrance, policy rows) |
| `tools/Exo.NvDisplay/Program.cs` | NVAPI + NVTweak display engine |
| `tools/Nvidia.Smoke/Program.cs` | Shape + pure-logic smoke (ships linked Detect/Panel logic) |

### Durable state (AppData)

| Path | Meaning |
|------|---------|
| `%LocalAppData%\Exo\nvidia-optimizer.json` | Apply marker / fail-closed progress / DRS verify fields |
| `%LocalAppData%\Exo\nvidia-panel-settings.json` | Exo panel prefs consumed by Display-Apply |
| `%LocalAppData%\Exo\nvidia-display-prefs.json` | Last display apply manifest (method, success flags) |
| `%LocalAppData%\Exo\tools\nvidiaProfileInspector\` | Managed NPI only (never user copies) |
| `%LocalAppData%\Exo\drivers\` | Cached GRD packages / extract trees |

---

## 1. Architecture and stages of Apply

### Host contract

- **Requires PowerShell 7** (`Core`, major ≥ 7). Windows PowerShell 5.1 is rejected.
- **Requires Administrator** for full Apply (driver, DRS import, system debloat).
- Progress: `EXO_PROGRESS:percent|status` on host + optional `EXO_LOG` file (UI polls log when elevated).
- Failures attribute to `Set-ExoStage` → `lastErrorStage` / `lastError` in marker with `applyInProgress=true` (fail-closed).

### Parameters (`Nvidia-Optimizer.ps1`)

| Param | Effect |
|-------|--------|
| `-Gsync` | Import `XX Series G-SYNC.nip` instead of max-FPS pack |
| `-Series 10\|20\|30\|40\|50` | Override auto series from GPU name |
| `-Repair` | Status clear only (see §6) |
| `-NonInteractive` | UI/CI path |
| `-SkipDriver` | Skip clean GRD + driver tweak stage (required path for notebooks after manual driver) |
| `-SkipProfile` | Skip NPI import |
| `-SkipApp` | Skip App wipe / CPL ensure |
| `-ForceDriver` | Force clean driver path even if “current” |
| `-InstallApp` | **Deprecated / ignored** — Exo is the panel |

### Pipeline order (canonical)

Comment in god-script:

1. **Driver first** (everything else sits on it)  
2. **3D Base + per-game profiles** (driver-level FPS/latency)  
3. **Client stack + debloat + display** (App wipe, CPL optional, NVAPI)

#### Stage table

| Stage id (`Set-ExoStage`) | Hub % | What happens | Hard fail? |
|---------------------------|-------|--------------|------------|
| *(Repair early exit)* | 40→100 | Delete marker only | N/A |
| `elevation-check` | 5 | Admin gate | Yes |
| `gpu-detect` | 12–15 | CIM `Win32_VideoController`; series; notebook hard-block on desktop driver | Yes if no GPU / unmapped series / notebook without `-SkipDriver` |
| *(fail-closed Save-State)* | — | Write marker with `applyInProgress=true`, all stage flags false | — |
| `driver-update` | 20–70 | Newest GRD lookup → Exo Clean Driver (Display.Driver only) → expert tweaks; or in-place retweak | Yes on `failed-clean` / `failed-no-url` / `failed-tweaks`; soft exit 0 + `pendingAfterDriver` if reboot required |
| `profile-pack-verify` | 40 | PROFILE_VERSION present; resolve NIP; SHA-256; assert pack | Yes |
| `profile-import` | 48 | Build combined NIP (Base + games + deltas); managed NPI `-silentImport` | Yes if exit ≠ 0 |
| `drs-verify` | 52 | NPI `-exportCustomized` vs pack Base pins | Soft (warn only; still records result) |
| `client-stack` | 64–72 | Wipe App/GFE (≤3 passes); ensure classic CPL | Soft if App remains / CPL missing |
| *(audio/bloat/tray)* | 70 | `Remove-NvidiaAudioComponents`, `Remove-NvidiaBloatComponents`, tray clear | Soft |
| `debloat` | 78–80 | Telemetry services/tasks; Gestalt=2; developer counters; overlay off; Windows toasts off | Overlay/debloat can soft-pass; hard later if residual |
| `display-policy` | 90–92 | `Set-NvidiaDisplayPreferences` → `Exo-Display-Apply.ps1` | Retried; hard if still false in post-verify |
| `finalize-checks` | 94 | Bind tweak + profile to active driver version strings | Yes if version unreadable after success path |
| `post-verify` / `display-policy-retry` | — | Force one more display pass; assert display + debloat + overlay | Yes |
| `save-state` | 96–100 | Full success marker, `applyInProgress=false` | — |

### Driver sub-path (Exo Clean Driver)

`Install-ExoCleanDriver`:

1. Download official Game Ready package (cached under `%LocalAppData%\Exo\drivers`).
2. Extract; locate signed `setup.exe`.
3. Silent install **Display.Driver only**:  
   `-s -n -clean Display.Driver` then fallback `-s -n Display.Driver`.
4. Exit 0 = success continue; exit 1 = success **reboot required** → stop pipeline, set `pendingAfterDriver=true`, UI shows restart message.
5. Strip leftover audio + NVI2 bloat packages.
6. `Apply-ExoDriverInstallTweaks` (MSI High, HDCP off, Ansel off, telemetry RIDs, PowerMizer, disable junk services).

If already newest GRD but tweaks missing → **in-place retweak** without full reinstall (`needsRetweak` detect path).

### Profile sub-path

1. Resolve pack: `Get-ProfileFile $seriesId $useGsync` → e.g. `40 Series.nip` / `40 Series G-SYNC.nip`.
2. `Assert-ExoNipProfile` (structure / performance-critical settings).
3. `New-ExoCombinedProfileNip`: clone Base into per-game application profiles from catalog (`Get-ExoGameProfileCatalog` — Val, CS2, Rivals, R6, Fortnite, Apex, LoL, OW2, RL, CoD, Destiny 2, PUBG, Tarkov, Finals, Delta Force, Deadlock, XDefiant, FragPunk, Warframe, PoE2, Dota 2, TF2, Rust, GTA V, FiveM, Helldivers 2, Wukong, Elden Ring, Wuthering Waves…).  
   **Minecraft `javaw.exe` deliberately excluded** (shared host process).
4. Tier deltas (`Apply-ExoGameProfileDeltas`): **comp** (sticky latency + FG off) vs **hybrid** (sticky latency; FG may keep pack default).
5. Managed NPI only: download pin `v3.0.1.11` with SHA-256; `-silentImport` of temp combined `.nip`.
6. Post-import DRS verify against **original pack Base** (not combined temp).

### Client / privacy sub-path

- Prefer **App removed**; Exo panel + NVAPI are authoritative; classic CPL optional fallback UI.
- Never restart `NVDisplay.ContainerLocalSystem` for soft refresh (re-promotes tray).
- `NvContainerLocalSystem` (App stack) stopped/disabled during tray path.

### Display sub-path

See §3. Invoked as `Set-NvidiaDisplayPreferences` → `Exo-Display-Apply.ps1` with live pre-check skip, hard retry, then post-verify retry.

---

## 2. Profile import / NPI / DRS verification

### Managed Profile Inspector (NPI)

| Field | Value |
|-------|-------|
| Pin tag | `v3.0.1.11` |
| URL | GitHub Orbmu2k release zip |
| SHA-256 | `68DB1640186DD6FD78B5F7949348808B9A542EE95E2A52810B2EEED026E80236` |
| Install dir | `%LocalAppData%\Exo\tools\nvidiaProfileInspector\` |
| Stamp | `EXO-NPI-VERSION.txt` |
| Import CLI | `-silentImport "path.nip"` |
| Export CLI | `-exportCustomized` (requires ≥ v3.0.1.11) |

Rules:

- **Never** delete or overwrite user-installed NPI elsewhere.
- Fresh install if stamp/hash mismatch.
- Silent import non-zero exit → profile **not** marked applied (throw).

### Pack assets

- 10 packs: series 10/20/30/40/50 × (max FPS | G-SYNC).
- GTX 16xx → **10 Series** pack (no RT/DLSS/rBAR invent).
- Series-specific extras (rBAR, DLSS, FG) documented in `profiles/README.md`.
- Shared pins: Prefer max performance, PRF=1, threaded opt, highest refresh, high-perf filtering, AO/FXAA/Ansel/overlay off, etc.
- Max FPS pack: ULL Ultra, VSync force off, G-SYNC off.
- G-SYNC pack: ULL off, G-SYNC on.

### Required DRS pin IDs (verify gate)

Must export after a correct import (Base Profile):

| SettingID | Intent (commented in code) |
|-----------|----------------------------|
| `274197361` | Power management mode |
| `390467` | ULL CPL state |
| `277041152` | ULL enabled |
| `277041154` | Frame limiter off |
| `294973784` | G-SYNC global |

Classifier (triplicated, must stay aligned):

- `NvidiaDetectLogic.ClassifyDrsVerification` (C#)
- `Get-ExoDrsVerificationResult` in `NvidiaDetectCore.ps1`
- same in `Nvidia-Optimizer.ps1`

### Result semantics

| Status | Meaning |
|--------|---------|
| `verified` | Intersection matches; required pins present |
| `drifted` | Value mismatch, missing required pin, or empty export Base |
| `unavailable` | No expected map, export missing (old NPI), parse fail |

**Profile stage applied** (detect): durable state contract OK **AND** `drsLive ≠ drifted`.

```text
IsProfileStageApplied(stateOk, drsLive) =
  stateOk && drsLive != "drifted"
```

State contract fields for profile:

`profileApplied`, `profileFile`, `profileVersion`, `profileSha256`, `profileDriverVersion`  
+ series match + pack file name match + live SHA-256 of ship file + driver version still equals recorded.

### Post-import vs live detect

- **Apply-time:** `Test-ExoDrsImportVerified` right after import; stores `drsVerified`, `drsMismatch`, `drsVerifyReason`.
- **Detect-time:** Re-runs export if managed NPI present and `profileApplied`; sets JSON `drsLive` / `drsLiveText` for UI (“Verified in driver” / “Drifted — re-apply”).

Drifted is **not** auto-fixed; user must Reapply.

---

## 3. Display path (NVAPI helper, registry NVTweak, multi-monitor)

### Layers

```
Exo panel JSON  →  Exo-Display-Apply.ps1  →  Exo.NvDisplay.exe (--apply/--status)
                         │                         │
                         ├─ NVTweak root + Devices  ├─ NVAPI color / HDMI full
                         ├─ Store CPL virtual hive  ├─ Win32 modes (GDI map)
                         ├─ Tray clear              ├─ Path scaling clear
                         └─ SUCCESS if nvapi OR registry
```

### Environment / panel policy

From `nvidia-panel-settings.json` (defaults in `NvidiaPanelSettings`):

| Pref | Default | Env to helper |
|------|---------|---------------|
| Primary refresh | `max` | `EXO_PRIMARY_REFRESH` |
| Secondary refresh | `60` | `EXO_SECONDARY_REFRESH` |
| Full RGB | true | `EXO_FULL_RGB` 0/1 |
| GPU no-scaling | true | `EXO_GPU_NOSCALE` |
| Scaling override | true | `EXO_SCALING_OVERRIDE` |
| Video NVIDIA color/image | true | (registry stamps) |
| Digital vibrance | 0 | Panel CLI `--set-vibrance` |

`NvidiaPanelSettingsService.Save` **forces** primary=max / secondary=60 (product policy).

### Exo.NvDisplay capabilities

- `--apply` / `--status` (bulk policy)
- `--list-displays`, `--list-color`
- `--set-mode WxH@Hz`, `--set-depth`, `--set-scaling`, `--set-color-range`
- `--get-vibrance` / `--set-vibrance`
- Optional `--display-id`
- Emits `EXO_NVDISPLAY_JSON:{...}` for machine parse

**Apply policy (default):**

- Keep current resolution.
- **Primary:** highest supported refresh.
- **Secondary:** 60 Hz (less GPU load on desktop).
- User color policy + RGB + Full (VESA) + best BPC with fallbacks.
- HDMI info-frame Full quantization.
- Clear stuck GPUScanOutToNative path; registry stamps GPU + No scaling + Override.
- NVTweak Gestalt=2 (advanced 3D image settings).

**Status / apply OK gate** (mirrored in `NvidiaDetectLogic.IsDisplayStatusOk`):

```text
ok = refreshOk && (registryOk || (colorOk && pathScalingOk))
```

Refresh is hard; registry for *active* display IDs OR live color+path.

### Multi-monitor / multi-GPU behavior

- Enumerate all physical NVIDIA GPUs; connected displays per GPU; de-dupe by `DisplayId`.
- One GPU throw does not abort whole enum.
- Partial GDI name map: skip refresh for unmapped IDs; still apply color + NVTweak.
- Zero active NVIDIA displays (Optimus laptop, iGPU-only panels):  
  `ok=true`, `skipped=no-active-nvidia-displays` — **display not required** for detect applied; profiles still matter.

### Registry NVTweak stamps (`Exo-Display-Apply`)

Per device under:

- `HKCU/HKLM\Software\NVIDIA Corporation\Global\NVTweak\Devices\*`
- Color / Video subkeys (Full range, Use NVIDIA color/image)

Root Gestalt / developer:

- `Gestalt=2`, `NvDevToolsVisible=1`, `RmProfilingAdminOnly=0`
- Also service-class NVTweak roots under `nvlddmkm`

**Store CPL virtual hive:** loads Appx `Helium\User.dat` and mirrors Gestalt + devices so Store CPL radios are less wrong (still **not** authoritative vs NVAPI).

### Retries

| Layer | Retries |
|-------|---------|
| NvDisplay `--apply` inside Display-Apply | 3 attempts; re-stamp NVTweak between |
| Optimizer `Set-NvidiaDisplayPreferences` | 1 hard retry on non-zero exit |
| Optimizer post-verify | 1 more full `Set-NvidiaDisplayPreferences` |
| Success definition | exit 0 if **nvapi OR registry** stamp verifies |

**Never** restart `NVDisplay.ContainerLocalSystem` to “apply” display (tray re-promotion).

### Helper discovery

```
Scripts/Nvidia/tools/Exo.NvDisplay.exe
%LocalAppData%\Exo\scripts\Nvidia\tools\Exo.NvDisplay.exe
%LocalAppData%\Exo\app\Scripts\Nvidia\tools\Exo.NvDisplay.exe
(+ optional win-x64 subfolder)
```

Missing helper: Display-Apply can still SUCCESS on registry-only; optimizer post-verify fails only if **both** NVAPI and registry fail.

---

## 4. Debloat / tray / services touched

### Services

| Service | Action | Notes |
|---------|--------|-------|
| `NvTelemetryContainer` | Stop + **Disabled** | Non-display |
| `NvCamera` | Stop + **Disabled** | Ansel |
| `FvSvc` | Stop + **Disabled** | FrameView |
| `NvContainerNetworkService` | Stop + **Manual** | On-demand App network |
| `NvContainerLocalSystem` | Stop + **Disabled** | App stack (tray path) |
| `NVDisplay.ContainerLocalSystem` | **Never disable** | Display stack |

### NVIDIA scheduled tasks (vendor)

Patterns disabled (two passes):  
`*NvTm*`, `*NVIDIA*Telemetry*`, `*NvProfile*`, `*NvNode*`, `*NvBackend*`, `*NVIDIA*App*`, `*SelfUpdate*`, `*FrameView*`, `NvDriverUpdateCheckDaily*`, GFE SelfUpdate, etc.

Skip names matching `Display|LocalSystem` or `^Exo`.

### Packages / components

- NVI2 uninstall of App/GFE/ShadowPlay/Node/Backend-style **bloat** packages (`Remove-NvidiaBloatComponents`); protect display driver packages.
- Full remove Virtual/HD Audio components + PnP devices (`Remove-NvidiaAudioComponents`).
- Appx NVIDIA Control Panel can be removed or reinstalled depending path; apply currently **ensures** classic CPL as optional UI when not `-SkipApp`.
- Desktop shortcuts / ARP leftovers cleaned.

### Overlay / privacy registry

- OverlayEnabled / EnableOverlay = 0 under NVIDIA App + GFExperience.
- ShadowPlay `NVSPCAPS` binary prefs zeroed (RecEnabled, Dwm*, indicators, GameStream).
- Windows notification toggles for NVIDIA.
- Run keys: remove NVIDIA App / GFE / ShadowPlay / FrameView autostart.
- FTS / advertising RID DWORDs = 0; AnselEnable = 0.

### Tray strategy (`Exo-Nvidia-TrayClear.ps1`)

1. Disable App container + kill App/Overlay/Share/helpers (not display container).
2. Delete **App/GFE** NotifyIconSettings keys.
3. **Hide** display-container keys: keep key, `IsPromoted=0` (delete would re-create promoted).
4. Multi-pass settle (`SettlePasses`).
5. Purge Exo scheduled tasks (see §5).
6. **Never** register Exo tasks/Run keys/services.

Detect row: `Taskbar tray (display hide / App gone)`.

### Expert driver tweaks (post clean install / retweak)

- MSISupported=1 + DevicePriority=3 on NVIDIA **Display** PCI nodes.
- `RMHdcpKeyglobZero=1` on display class `{4d36e968-…}` NVIDIA nodes.
- PowerMizerLevel / LevelAC = max prefer when keys exist.
- Telemetry services + audio/bloat strip again.

---

## 5. Exo scheduled tasks — history and current purge rules

### Product rule (AGENTS.md + REWRITE-PROGRAM)

> **No Exo background footprint:** never create Exo scheduled tasks, logon tasks, Run-key startup entries, or Exo-named Windows services. Apply runs when the user clicks Apply; purge leftovers on Apply/Repair.

### Historical task names (must only be deleted)

These existed in older builds (tray hide on logon / display persist) and are **forbidden to recreate**:

| Task name |
|-----------|
| `Exo-NvidiaTrayHide` |
| `Exo-NvidiaDisplayPersist` |
| `Exo-NvidiaBackgroundPersist` |
| `Exo-NvidiaTray` |
| `Exo-Nvidia` |

Plus wildcard purge: any scheduled task whose name matches `(?i)^Exo-`.

### Where purge runs

| Script | Function |
|--------|----------|
| `Exo-Nvidia-TrayClear.ps1` | `Unregister-ExoTrayTasks` (named + `^Exo-`) |
| `Exo-Display-Apply.ps1` | `Unregister-LegacyPersistTask` |
| `Nvidia-Optimizer.ps1` `Disable-NvidiaTelemetry` | same named list + `^Exo-` |

### Smoke / CI enforcement

`NvidiaDetectLogic.ForbiddenApplyPatterns` / `AuditApplyScriptText`:

- Forbidden: `Register-ScheduledTask ... Exo-Nvidia*`, `schtasks /Create ... Exo-Nvidia`, MaxUserPort folklore, FPS Unlocker.
- Required markers: `Import-ExoNipProfile`, `Apply-ExoGameProfileDeltas`, `Exo-Display-Apply`, `Exo-Nvidia-TrayClear`, `IsPromoted`, `silentImport`.
- Regex forbids any `Register-ScheduledTask` line containing `Exo-Nvidia`.

`Nvidia.Smoke` asserts apply blob has **no** `Register-ScheduledTask` with Exo-Nvidia and **does** contain tray hide task name only in purge context.

---

## 6. Reset vs Repair — product truth

### What the UI button does

- ViewModel method: `RepairAsync` → `Exo-Nvidia-Repair.ps1` → `Nvidia-Optimizer.ps1 -Repair`.
- Page XAML comment: **“Reset clears Exo status only — status clear, not a driver restore.”**
- Message: `OptimizerMessages.NvidiaStatusCleared` =  
  **“Status cleared. Driver and profiles unchanged.”**
- Does **not** elevate.

### What `-Repair` actually does

```powershell
function Invoke-Repair {
  # Delete %LocalAppData%\Exo\nvidia-optimizer.json only
  # Explicitly leaves driver, DRS profiles, App installs intact
}
```

### What it does **not** do

| Action | Done by Reset? |
|--------|----------------|
| Roll back Game Ready driver | **No** |
| Restore stock DRS / Base Profile | **No** |
| Reinstall NVIDIA App / GFE | **No** |
| Undo MSI / HDCP / telemetry | **No** |
| Reset refresh rates / Full RGB | **No** |
| Uninstall Exo | **No** |
| Create restore point | **No** |

### Honest recovery path (product copy forever)

Per AGENTS.md:

> Never describe NVIDIA Reset as rollback: it only clears Exo status, while NVIDIA recovery remains manual through NVIDIA settings or a driver reinstall.

After Reset, detect shows “Not applied”; user must **Apply** again to re-import packs and re-stamp display. True recovery = user/NVIDIA installer / DDU-class tools outside Exo.

### Contrast with other modules

| Module | Repair meaning |
|--------|----------------|
| Internet | Snapshot restore |
| Discord / Steam | Real repair toward stock bootable client |
| **NVIDIA** | **Status marker clear only** |

---

## 7. Failure modes

### Laptop / notebook GPU

- Detected via name: `Laptop GPU|Notebook|Mobile|Max-Q|MX\d+|…M`.
- **Desktop GRD auto-download is hard-blocked** unless `-SkipDriver`.
- Error text directs official NVIDIA **notebook** driver, then Apply with profile/display/debloat only.
- Detect: driver stage OK if any readable version; desktop update/retweak gates do not force fail forever.
- Optimus: no active NVIDIA-connected panels → display skipped OK; 3D profiles still apply.

### Multi-GPU

- Enum tolerates per-adapter failures.
- Primary GPU for series/driver is **first** CIM NVIDIA controller (ordering risk if iGPU listed oddly — mitigated by name filter for NVIDIA only).
- Multiple NVIDIA displays de-duped by DisplayId.

### Missing / broken Exo.NvDisplay helper

- Status: `helper unavailable` → display row inactive.
- Apply: registry NVTweak path can still SUCCESS; post-verify throws only if registry also fails.
- Publish must ship `Scripts/Nvidia/tools/` self-contained runtime (currently large .NET deps tree + `NvAPIWrapper.dll`).

### Driver version / reboot

| Situation | Behavior |
|-----------|----------|
| Update available | Clean install path |
| Newest but no tweaks | In-place retweak |
| Clean install exit 1 | `RESTART_REQUIRED`; `pendingAfterDriver=true`; profiles **not** applied until second Apply |
| Install success but wrong version string | failed clean |
| Lookup blocked / no URL | `failed-no-url` exit 1 |
| Interrupted mid-apply | `applyInProgress=true` + lastErrorStage; detect fail-closed |
| Driver changes after success | `profileDriverVersion` mismatch → “Driver changed - reapply” |
| DRS drifted after games/App | `drsLive=drifted` → reapply |

### NPI / network

- Cannot download pin → profile import throws.
- Old NPI without `-exportCustomized` → verify `unavailable` (non-fatal).
- Silent import timeout (120s) → throw, profile not applied.

### Client wipe soft failures

- App may remain after 3 passes → warn, continue (detect client row fails until gone).
- Debloat/overlay soft-pass during CPL path; post-verify can still hard-fail hard residuals.

### Privilege / host

- Non-admin Apply: elevation-check throw.
- PS 5.1 host: Run wrapper throws before god-script.

### Store CPL UI lies

- Virtual hive / radios often stale; detect **must not** require CPL radio match.
- Advanced 3D success = DRS profiles applied, not Gestalt UI.

---

## 8. God-script split plan (concrete files)

Target (REWRITE Phase 5 / PR-5.1): thin orchestrator + stage modules; pure logic already partly in C#.

### Proposed layout

```
Exo/Scripts/Nvidia/
  Exo-Nvidia-Run.ps1                 # keep thin
  Exo-Nvidia-Repair.ps1              # keep thin
  Exo-Nvidia-Detect.ps1              # keep; slim I/O only
  NvidiaDetectCore.ps1               # keep pure (expand)
  lib/
    Exo.Nvidia.Common.ps1            # logging, stages, state I/O, hub progress, admin/pwsh
    Exo.Nvidia.Gpu.ps1               # Get-NvidiaGpus, series, notebook, driver version convert
    Exo.Nvidia.Driver.ps1            # lookup, download, extract, clean install, expert tweaks
    Exo.Nvidia.Npi.ps1               # pin install, silentImport, exportCustomized
    Exo.Nvidia.Profiles.ps1          # pack resolve, assert, combined NIP, game catalog, deltas
    Exo.Nvidia.Drs.ps1               # verify classifier + Test-ExoDrsImportVerified
    Exo.Nvidia.Client.ps1            # App/GFE wipe, CPL ensure/remove, NVI2, audio, bloat
    Exo.Nvidia.Debloat.ps1           # telemetry services/tasks, overlay, notifications, Run keys
    Exo.Nvidia.Display.ps1           # Set-NvidiaDisplayPreferences wrapper (calls Exo-Display-Apply)
    Exo.Nvidia.Tray.ps1              # optional: share with TrayClear or keep separate file
  Exo-Display-Apply.ps1              # keep as display executor (already split)
  Exo-Nvidia-TrayClear.ps1           # keep
  Nvidia-Optimizer.ps1               # shrink to orchestrator (~200–400 lines): param + stage order only
  profiles/                          # unchanged binary packs
  tools/Exo.NvDisplay*               # unchanged publish payload
```

### C# expansion (parallel)

| New / expanded type | Responsibility |
|---------------------|----------------|
| `NvidiaDetectLogic` | Keep; add feature-row predicates for full detect contract table |
| `NvidiaApplyPlan` (new) | Ordered stage ids + skip flags from detect extras |
| `NvidiaDriverLogic` (new pure) | Version compare, series→lookup pairs fixtures |
| Keep `NvidiaPanelLogic` | CLI contract for panel |

### Orchestrator skeleton (post-split)

```text
Assert-ExoPwsh7 / admin
if Repair → Clear-State; exit
Detect GPU → series → notebook gate
Save-State fail-closed
Invoke-NvidiaDriverStage
Invoke-NvidiaProfileStage (import + DRS)
Invoke-NvidiaClientStage
Invoke-NvidiaDebloatStage
Invoke-NvidiaDisplayStage
Post-verify → Save-State success
```

### Migration rules

1. Extract pure classifiers first; smoke must pin DRS + series + tray.
2. Move functions **without** behavior change; keep exit codes / state schema.
3. Forbidden to reintroduce `Register-ScheduledTask Exo-*`.
4. God-script may remain as `.` sourced facade until all call sites use modules.
5. Delete dead App-install path leftovers (`Install-NvidiaApp*`) only after detect/UI no longer reference optional App configure flags (marker still has historical keys).

### Size target

| File | Today | Target |
|------|-------|--------|
| `Nvidia-Optimizer.ps1` | ~4300 lines | ≤400 orchestrator |
| Largest stage module | — | ≤800 lines |
| Display-Apply | ~530 lines | keep or split NVTweak vs helper invoke |

---

## 9. Test matrix

### Automated (CI / local)

| Suite | What it proves | HW needed? |
|-------|----------------|------------|
| `tools/Nvidia.Smoke` | Series mapping, notebook names, tray classifiers, DRS classifier table, profile stage applied, script markers, NPI pin strings, panel CLI builders, optional live NvDisplay if present | No |
| `tools/Ui.Smoke` | XAML/VM wiring, detect script path, NvDisplay source markers, no home probe on every load | No |
| `tools/Test-Repository.ps1` | VERSION/PROFILE_VERSION, NVAPI marker required in apply/detect, single-GPU `@()` wrap, NIP assets present | No |
| `tools/Test-Linux.ps1` | Smoke project builds under Linux agent | No |

### Hardware Apply matrix (human / release gate)

| Profile | Driver stage | Profile + DRS | Display | Debloat/tray | Notes |
|---------|--------------|---------------|---------|--------------|-------|
| Desktop single GPU, single monitor max Hz | Clean or retweak | Max-FPS pack | Primary max | Full | Baseline |
| Desktop + G-SYNC monitor | Same | G-SYNC pack | Full RGB + VRR | Full | Toggle G-SYNC in UI |
| Dual monitor (primary high Hz + secondary) | Same | Same | Primary max / secondary 60 | Full | Core multi-mon |
| Three+ monitors | Same | Same | Same policy | Full | Partial GDI map edge |
| Laptop Optimus (iGPU panels) | `-SkipDriver` after manual NB driver | Import OK | Skip display OK | Full | Must not fail whole Apply |
| Laptop MUX / dGPU-connected panel | SkipDriver or future NB lookup | Import | Live display | Full | |
| Multi-NVIDIA (e.g. + RTX secondary) | First controller series | Import | Multi enum | Full | |
| After Windows restart (pendingAfterDriver) | Second Apply | Completes | Completes | Completes | |
| Driver upgrade outside Exo | Detect “changed” | Reapply | Reapply | | |
| Helper deleted from tools/ | Registry-only path / loud fail | | | | |
| Offline (no GRD lookup) | failed-no-url if update needed; retweak may work | | | | |

### Detect ≡ Apply contract checks (must stay green)

| Feature row (detect) | Apply must write |
|----------------------|------------------|
| NVIDIA GPU | CIM presence |
| Driver (newest + install tweaks) | Clean/retweak + services/MSI |
| 3D Base Profile | silentImport + state + not drifted |
| Per-game profiles | catalog count ≥ 10 + gameProfilesApplied |
| Exo display policy | NvDisplay ok or skip no-panels |
| Exo 3D profile (driver DRS) | profiles applied |
| Driver only (no App / CPL) | App gone |
| Privacy / telemetry / overlay off | debloat + overlay tests |
| Taskbar tray | IsPromoted=0; app ghosts gone |

---

## 10. Acceptance criteria

### Ship blockers (module)

1. **Apply completes** on supported desktop with NVIDIA GPU: driver stage OK (or honest restart), profiles imported, DRS not drifted (or unavailable with import success), display prefs landed or Optimus skip, App removed, tray hide pass, no Exo tasks left.
2. **Detect ≡ Apply:** no feature row “active” requirement for work Apply never performs.
3. **DRS:** after import, required pins exportable as verified when NPI pin present; drifted forces reapply messaging, not silent green.
4. **Display:** NVAPI retry ≥3; helper bundled in publish; multi-monitor primary/secondary policy; registry fallback when NVAPI soft-fails.
5. **No Exo background footprint:** zero `Exo-*` scheduled tasks after Apply; smokes forbid re-create.
6. **Reset honesty:** button only clears marker; UI string never says driver rollback.
7. **Notebook:** never installs desktop GRD package automatically; clear error or `-SkipDriver` path works.
8. **Fail-closed state:** interrupted Apply cannot report `isApplied=true`.
9. **pwsh 7 + admin** requirements documented in error strings.
10. **Smokes green** (`Nvidia.Smoke` failed=0) including audit markers and NPI pin.

### UX acceptance

- Advisor (`OptimizerAdvisor`) explains missing rows in plain language.
- Restart path: “Restart Windows, then Apply again.”
- Drift path: “Profile drifted — re-apply.”
- Panel remains for live mode/vibrance without claiming Control Panel parity.

### Release gate (REWRITE §7)

CI green → all smokes → human checklist on desktop dual-display (and laptop if available) → tag.

---

## 11. What can NEVER be automatic

These must remain **user-initiated**, **manual**, or **explicit flags** — never silent background automation:

| Action | Why |
|--------|-----|
| **Full driver reinstall / DDU / factory reset** | Data-loss and brick risk; Reset is not this |
| **Desktop GRD install on notebook/laptop GPUs** | Wrong package bricks mobile firmware/power; hard block without `-SkipDriver` after manual NB driver |
| **Accept unsigned / INF strip (EAC)** | Explicitly skipped in expert tweaks as unsafe |
| **Forced reboot without user consent** | Exit 1 sets pending; user restarts Windows |
| **Exo scheduled tasks / logon persist / Run keys / Exo services** | Product rule; purge only |
| **Restart NVDisplay.Container to “fix” display** | Re-promotes tray; forbidden soft-refresh |
| **Deleting display-container NotifyIcon key** | Windows recreates as promoted |
| **Calling Reset a driver/profile rollback** | Copy forever |
| **Silent NVIDIA App reinstall as product path** | `-InstallApp` ignored; App is stripped |
| **Auto-fix “drifted” DRS without Reapply click** | User owns re-import |
| **Invented registry folklore** (e.g. MaxUserPort) | Forbidden by smoke audit |
| **Background Exo agent keeping tray clean** | Apply-only product |
| **Unattended elevated Apply without UI/user** | Elevates only on Apply click |
| **Minecraft/javaw global profile** | Shared process; catalog excludes |

### Explicit opt-in / advanced only

| Flag / action | Scope |
|---------------|--------|
| `-ForceDriver` | Force clean path when version “current” |
| `-SkipDriver` / `-SkipProfile` / `-SkipApp` | Advanced partial apply |
| `-Gsync` / UI toggle | Pack choice |
| Panel vibrance / depth / mode | User panel mutations |
| Manual notebook driver install | Outside Exo |

---

## Appendix A — Detect feature rows (authoritative order)

1. NVIDIA GPU  
2. Driver (newest + install tweaks)  
3. 3D Base Profile (+ `drsLive`)  
4. Per-game profiles  
5. Exo display policy (driver)  
6. Exo 3D profile (driver DRS)  
7. Driver only (no App / CPL)  
8. Privacy / telemetry / overlay off  
9. Taskbar tray (display hide / App gone)  

`isApplied` requires all of: GPU, not pending, not in-progress, profile+DRS, games, display, background, client, advanced3d, tray, driverStageOk.

## Appendix B — Progress milestones (UI)

| % | Status (approx.) |
|---|------------------|
| 5 | Starting |
| 12–15 | GPU / series |
| 20–55 | Driver download/extract/install |
| 40–52 | Profile Inspector + DRS verify |
| 64–80 | Client wipe / debloat |
| 90–94 | Display + finalize |
| 96–100 | Save state / done or failed stage |

## Appendix C — Related rewrite docs

- `docs/REWRITE-PROGRAM.md` — Phase 5 NVIDIA rebuild  
- `docs/TWEAK-AUDIT.md` — aggressive tweak philosophy  
- `AGENTS.md` — no background footprint; Reset honesty  

## Appendix D — Inventory file index (absolute)

| Path |
|------|
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Nvidia-Optimizer.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Exo-Nvidia-Run.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Exo-Nvidia-Repair.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Exo-Nvidia-Detect.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\NvidiaDetectCore.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Exo-Display-Apply.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\Exo-Nvidia-TrayClear.ps1` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\profiles\` |
| `C:\Users\Erix\Exo\Exo\Scripts\Nvidia\tools\Exo.NvDisplay.exe` |
| `C:\Users\Erix\Exo\Exo\Services\NvidiaDetectLogic.cs` |
| `C:\Users\Erix\Exo\Exo\Services\NvidiaPanelLogic.cs` |
| `C:\Users\Erix\Exo\Exo\Services\NvidiaPanelSettingsService.cs` |
| `C:\Users\Erix\Exo\Exo\Services\OptimizerStateService.cs` |
| `C:\Users\Erix\Exo\Exo\ViewModels\NvidiaOptimizerViewModel.cs` |
| `C:\Users\Erix\Exo\Exo\ViewModels\NvidiaPanelViewModel.cs` |
| `C:\Users\Erix\Exo\tools\Exo.NvDisplay\Program.cs` |
| `C:\Users\Erix\Exo\tools\Nvidia.Smoke\Program.cs` |

---

*End of research inventory. Safe next engineering step: PR-5.1 extract `lib/Exo.Nvidia.*.ps1` without behavior change; keep smokes pinning NPI pin, DRS classifier, and forbidden task registration.*
