# Research inventory: Discord + Steam optimizers

**Scope:** Full architecture inventory for multi-week rebuild (v3.0 program).  
**Source tree:** `C:\Users\Erix\Exo` as of 2026-07-16 inventory.  
**Versions:** Discord kit `1.3.44` (`Exo/Scripts/Discord/VERSION`), Steam pack `1.9.0` (`Exo/Scripts/Steam/VERSION`).  
**Mode:** Read-only product analysis — no product code edits in this pass.

Related program docs: [`docs/REWRITE-PROGRAM.md`](../REWRITE-PROGRAM.md), [`AGENTS.md`](../../AGENTS.md), [`docs/TWEAK-AUDIT.md`](../TWEAK-AUDIT.md).

---

## Shared host pipeline (both modules)

```
WinUI Page (DiscordOptimizerPage | SteamOptimizerPage)
  └─ ViewModel (DiscordOptimizerViewModel | SteamOptimizerViewModel)
       ├─ Refresh → OptimizerStateService.Detect*Async
       │     ├─ C# heuristic (DiscordLogic | SteamLogic + filesystem/registry)
       │     └─ PowerShell Exo-*-Detect.ps1 → JSON { isApplied, statusText, features[] }
       ├─ Apply  → PowerShellRunnerService (elevate:true)
       │     └─ Exo-*-Run.ps1 → *Optimizer.ps1 (or Discord-Optimizer + kit/lib)
       └─ Repair → Exo-*-Repair.ps1
             └─ (Steam: Steam-Optimizer -Repair; Discord: standalone reinstall path)
```

| Layer | Discord | Steam |
|-------|---------|-------|
| Page | `Exo/Views/DiscordOptimizerPage.xaml(.cs)` | `Exo/Views/SteamOptimizerPage.xaml(.cs)` |
| VM | `Exo/ViewModels/DiscordOptimizerViewModel.cs` | `Exo/ViewModels/SteamOptimizerViewModel.cs` |
| Pure classifiers | `Exo/Services/DiscordLogic.cs` | `Exo/Services/SteamLogic.cs` |
| Detect orchestration | `OptimizerStateService.DetectDiscordAsync` | `OptimizerStateService.DetectSteamAsync` |
| Script roots | `ScriptBundleService.GetDiscordRoot()` | `ScriptBundleService.GetSteamRoot()` |
| Apply entry | `Exo-Discord-Run.ps1` | `Exo-Steam-Run.ps1` |
| Repair entry | `Exo-Discord-Repair.ps1` | `Exo-Steam-Repair.ps1` → `Steam-Optimizer.ps1 -Repair` |
| State file | `%LocalAppData%\Exo\discord-optimizer.json` | `%LocalAppData%\Exo\steam-optimizer.json` |
| Smoke | `tools/Discord.Smoke` | `tools/Steam.Smoke` |
| Report UI | `applyReport` array via `TryReadApplyReport("discord")` | same + `steam-trim-stats.json` |

**Host constraints (both):**

- Elevated Apply/Repair via `PowerShellRunnerService`.
- Requires **PowerShell 7** (`PSEdition Core`, major ≥ 7); never 5.1.
- Progress: `EXO_PROGRESS:n|status` lines; structured steps: `EXO_REPORT:step|ok|fail:…|skip:…`.
- Full detect only on page open (`InitializeAsync` → `RefreshAsync`); no “fast flash already optimized”.

---

# Module A — Discord

## 1. Architecture / call graph

### 1.1 File map

```
Exo/Scripts/Discord/
  Exo-Discord-Run.ps1          # WinUI apply wrapper (progress map, forces NoLaunch)
  Exo-Discord-Detect.ps1       # Live detect → JSON
  Exo-Discord-Repair.ps1       # Clean reinstall + recovery restore
  Discord-Optimizer.ps1           # God orchestrator + PS7 bootstrap (~850+ lines main)
  DiscordDetectCore.ps1        # Pure classifiers (dot-sourced by detect + smoke)
  VERSION                      # 1.3.44
  kit/
    config.ini                 # DiscOpt kernel settings (TrimIntervalMs=4000, PriorityClass=3)
    version.dll, ffmpeg.dll    # DiscOpt binaries (proxy + kernel)
    Discord.vbs                # Launch helper (legacy path still detected)
    profiles/*.json            # discord.json host flags, equicord plugins/overrides
    themes/amoled-cord.theme.css
    lib/
      10-Logging.ps1           # Logs, EXO_REPORT, network/GitHub helpers
      20-Discord.ps1           # Install/update/modules/login gates
      30-EquicordProfile.ps1   # (profile merge helpers live across 30/50)
      40-DebloatWindows.ps1    # Debloat, Windows quiet, QoS, variants, Exo Host
      50-EquicordInstall.ps1   # Equicord download + loader asar + profile
      60-KernelBoot.ps1        # Kernel install, boot safety, Start Menu, summary
    tools/                     # Optional module install helpers
```

### 1.2 Apply call graph

```
DiscordOptimizerViewModel.RunAsync
  → PS elevate: Exo-Discord-Run.ps1 -NonInteractive
       sets EXO=1, DISCOPT_NONINTERACTIVE=1, EXO_SKIP_BOOT_FLASH=1, NoLaunch=$true
       → Discord-Optimizer.ps1
            Initialize-DiscOptRuntime (PS7 / elevate / portable zip fallback)
            dot-source kit/lib/*.ps1 (sorted name)
            main:
              Stop-Discord
              Initialize-DiscordApplyState (recovery snapshot → discord-optimizer.json)
              Prepare-Discord / Assert-DiscordInstall
              Ensure-DiscordLoggedIn
              Invoke-Debloat / Clear-DiscordSafeCache
              Ensure-KrispModule / Ensure-RuntimeModules   # soft-skip on CDN fail
              Apply-DiscordProfile (settings.json host flags)
              Install-Equicord → Install-ExoHost + Apply-EquicordProfile
              Install-DiscOptKernel
              [quiet verify OR Confirm-DiscordBootsAfterMods]  # Exo host uses quiet path
              Apply-WindowsTweaks (startup/tasks/toasts/tray/GPU/FSO/QoS)
              Set-DiscordVariantQuiet (PTB/Canary)
              Restore-StartMenu
              verify debloat + windows suppression + OPEN_ON_STARTUP
              Complete-DiscordApplyState
```

### 1.3 Detect call graph

```
DetectDiscordAsync
  → DetectDiscordHeuristic (C#; DiscordLogic + disk)
  → Exo-Discord-Detect.ps1
       . DiscordDetectCore.ps1
       feature rows → JSON isApplied = ALL critical rows + markerOk
```

### 1.4 Repair call graph

```
RepairAsync → Exo-Discord-Repair.ps1
  stop Discord
  download signed DiscordSetup (Authenticode + Discord subject)
  delete %LocalAppData%\Discord (path-guarded)
  purge %AppData%\discord caches (login folders kept unless FullReset)
  remove amoled theme files under Equicord
  silent install -s
  restore shortcuts from wscript/Discord.vbs → Discord.exe
  Restore-RepairWindowsTweaks (recovery from state)
  Remove-ExoDiscordQosPolicies
  Restore-ExoDiscordVariantSettings (strip chromiumSwitches / TTI keys)
  start Discord via explorer (unelevated)
  delete discord-optimizer.json on full success
```

---

## 2. All mutations

### 2.1 Filesystem — Discord client (`%LocalAppData%\Discord\app-*`)

| Mutation | Function | Notes |
|----------|----------|-------|
| Remove leftover `app-*` builds | `Invoke-Debloat` | Keeps active build only |
| Remove optional modules `discord_hook-1`, `discord_clips-1` | `Invoke-Debloat` | Allowlist only |
| Remove `discord_game_sdk_*.dll` | `Invoke-Debloat` | Soft-drift in detect |
| Remove non-`en-US.pak` locales | `Invoke-Debloat` | Soft-drift in detect |
| Remove spellcheck `*.bdic` extras | `Remove-DiscordExtraSpellcheckDictionaries` | Keep en-US + system locale |
| Strip `.first-run`, `*.log`, Xbox winmd, etc. | `Invoke-Debloat` | Protected list excludes kernel/exes |
| Safe caches under AppData/Local | `Clear-DiscordSafeCache` / conflict leftovers | Never session storage |
| Stock shell layout `_app.asar` / `_app.asar.stock` | `Install-EquicordDirect`, `Ensure-AsarStockBackup` | Multi-MB stock shell required |
| Tiny Equicord loader `resources\app.asar` | `New-EquicordLoaderAsar` + `Write-DiscordResourceBytes` | require(equicord.asar) stub |
| Remove legacy OpenAsar-sized `_app.asar` | `Remove-LegacyOpenAsar` | Restore from `.stock` |
| DiscOpt `version.dll`, `config.ini` | `Install-DiscOptKernel` | From kit |
| DiscOpt `ffmpeg.dll` proxy + `ffmpeg_real.dll` backup | `Install-DiscOptKernel` | Proxy may soft-skip |
| Soft-disable rename `*.disabled` | `Disable-DiscOptKernelOnDisk` | Boot safety rollback |
| Stock runtime restore | `Use-StockDiscordRuntime` | Full mod rollback on brick |
| Krisp / notifications modules from CDN | `Ensure-KrispModule`, `Ensure-RuntimeModules` | Soft-skip optional |
| Desktop Exo shortcuts removed | `Clear-DiscordConflictLeftovers` | `Discord (Exo).lnk` |

### 2.2 Filesystem — Equicord / AppData

| Path | Mutation |
|------|----------|
| `%AppData%\Equicord\equicord.asar` | Download / cache |
| `%AppData%\Equicord\settings\settings.json` | Plugin/theme profile merge |
| `%AppData%\Equicord\themes\amoled-cord.theme.css` | Theme install |
| `%AppData%\discord\settings.json` | Host flags: `SKIP_HOST_UPDATE`, chromium lean, TTI, `OPEN_ON_STARTUP=false`, etc. |
| `%AppData%\discordptb|discordcanary\settings.json` | Quiet flags (no SKIP_HOST_UPDATE force-true; set false) |
| `%AppData%\discord\dictionaries\*.bdic` | Extra locale dicts removed |

### 2.3 Filesystem — kit / state / logs

| Path | Role |
|------|------|
| `%LocalAppData%\Exo\discord-optimizer.json` | Recovery + applyStatus + applyReport + debloatVerified |
| `kit\logs\*.log`, `last-error.log` | Run logs |
| `%LocalAppData%\Exo\logs\last-discord-error.log` | Hub mirror |
| `%LocalAppData%\Exo\runtime\PowerShell\` | Portable PS7 fallback (Discord bootstrap only) |

### 2.4 Registry

| Key / surface | Mutation |
|---------------|----------|
| `HKCU\...\Run` | Remove Discord autostart values (stable + variants) |
| `HKCU\...\StartupApproved\Run` | Remove Discord approvals |
| Scheduled tasks matching stable Discord path/name | `Disable-ScheduledTask` (path-scoped via `Get-StableDiscordTasks`) |
| `HKCU\...\Notifications\Settings\{Discord*}` | `Enabled=0`, `ShowInActionCenter=0` |
| `HKCU\Control Panel\NotifyIconSettings\*` (Discord exe) | `IsPromoted=0` |
| `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` | Discord.exe → `GpuPreference=2` if dGPU |
| `HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers` | `~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE` |
| **HKLM** `SOFTWARE\Policies\Microsoft\Windows\QoS\{Exo Discord Voice, PTB, Canary}` | DSCP 46, UDP, App Name = variant exe |

### 2.5 Launchers / shortcuts

| Action | Detail |
|--------|--------|
| Start Menu | Prefer `Update.exe --processStart Discord.exe`; also accept VBS / direct exe in detect |
| `Restore-StartMenu` | Single Programs entry under Discord Inc; uses kit `Discord.vbs` as fallback helper |
| Repair | Rewrites wscript/Discord.vbs shortcuts back to stock `Discord.exe` |

### 2.6 Tasks / services (vendor vs Exo)

| Kind | Behavior |
|------|----------|
| Discord/Squirrel scheduled tasks | **Disabled** when path-scoped to stable install |
| **Exo-named scheduled tasks** | **Must not create** — forbidden in `DiscordLogic.ForbiddenApplyPatterns` / smoke |
| Exo Windows services | **None** |
| Exo Run-key self-start | **None** |

### 2.7 Kernel binaries

- `kit/version.dll` → app dir (latency/priority/raw-input hook).
- `kit/ffmpeg.dll` (small proxy) → replaces `ffmpeg.dll`; stock moved to `ffmpeg_real.dll`.
- `kit/config.ini`: `EnableTrim=1`, `TrimIntervalMs=4000`, `PriorityClass=3` (Above Normal), thread + raw input flags.

---

## 3. Soft-skip vs hard-fail paths

| Path | Soft-skip (continue / report skip) | Hard-fail (throw / incomplete) |
|------|-------------------------------------|--------------------------------|
| Discord not installed | Install via Prepare-Discord | Install failure throws |
| Optional CDN modules (notifications) | Soft-skip + report | Required modules still throw if boot-critical |
| Krisp module CDN | Soft-skip always | — |
| Kernel ffmpeg proxy invalid/missing | Keep stock ffmpeg; version.dll+ini may remain; report `kernel|skip` | Missing stock ffmpeg entirely throws |
| Kernel install exception | Warn, disable kernel on disk, continue to verify | — |
| Boot check (standalone path) | Disable kernel → retry; then stock runtime | Stock also fails → throw + Repair hint |
| **Exo-hosted apply** | **Skips real boot check** (`EXO_SKIP_BOOT_FLASH`) | Quiet file verify only; incomplete → warn, may disable kernel |
| Equicord install | — | Throws on failure |
| Host flags / settings.json | — | Throws |
| Debloat / cache | — | Throws on failure |
| Windows tweaks | — | Throws on failure |
| QoS policy create/verify | Per-variant fail recorded; report may be `fail` | Does not always throw whole apply |
| Variant PTB/Canary settings | Per-variant warn | Report fail if flags/autostart incomplete |
| Post-apply verify (debloat, suppression, OPEN_ON_STARTUP) | — | Throws → incomplete state + report |
| Quick mode | Many skips; never writes full `applied` | By design incomplete |

---

## 4. Repair completeness

| Capability | Status |
|------------|--------|
| Signed installer re-download + Authenticode | **Yes** |
| Path-guarded program-files wipe | **Yes** (`LocalAppData\Discord` only) |
| Login preserved (Local Storage, IndexedDB, Cookies, …) | **Yes** (default) |
| Full reset option | **Yes** (`-FullReset` / env) |
| Equicord theme cleanup | **Partial** (amoled files only) |
| Equicord asar / settings left in place | **Yes — leftover** (client reinstall stock; Equicord data not fully purged) |
| Windows recovery restore (Run, toasts, tasks, tray, compat) | **Yes if recovery snapshot exists** |
| QoS Exo policies removed | **Yes** (fixed names) |
| PTB/Canary Exo flags stripped | **Yes** (selected keys) |
| PTB/Canary program reinstall | **No** (manual) |
| Kernel / Equicord loader removed | **Yes** (via full client wipe) |
| Marker cleared only on full success | **Yes**; repair-pending keeps recovery |
| Offline repair | **No** (needs network for installer) |

**Gap:** Repair is “stock Discord reinstall + restore OS integration,” not a surgical undo of every AppData Equicord plugin setting.

---

## 5. Update-resilience (client updates break mods)

| Event | Effect | Recovery |
|-------|---------|----------|
| Discord stable host update (new `app-*`) | New build lacks Equicord loader, kernel, debloat | Detect: `Discord updated - reapply`; marker path-bound to `appDir` |
| Discord overwrites `app.asar` | Equicord loader lost | Reapply / `-Launch` heal path |
| Discord replaces `ffmpeg.dll` | Proxy lost / layout invalid | Reapply; detect hash mismatch |
| Discord re-adds optional modules / locales / game SDK | Soft debloat drift | Detect soft-recovery if `debloatVerified` same app; else Not applied |
| Equicord upstream break | Loader/plugins fail | Reapply downloads latest; risk residual |
| Squirrel tasks re-enabled | Windows quiet fails | Reapply disables again |
| Settings.json user flip `OPEN_ON_STARTUP` | Windows quiet fails | Reapply |
| **Exo Apply under elevation never live-boots Discord** | Kernel may brick undetected until user opens Discord | User must open non-elevated; Repair if stuck |

**Highest residual product risk in whole app:** DiscOpt kernel + asar loader + silent elevated apply without boot flash.

---

## 6. Exo background tasks / services / startup

| Item | Present? |
|------|----------|
| `Register-ScheduledTask` Exo-Discord* | **Forbidden / smoke-blocked** |
| Exo Run key | **None** |
| Exo Windows service | **None** |
| Resident helper while Discord runs | **None** (kernel is in-process DLL, not Exo service) |
| Portable PS7 under `%LocalAppData%\Exo\runtime` | Install-time tooling only, not autostart |

**Vendor suppressions (allowed):** Discord Run entries, Discord scheduled tasks, toast policies.

---

## 7. Detect honesty vs Apply

### 7.1 Feature contract (detect rows)

| Detect row | Apply produces | Honesty notes |
|------------|----------------|---------------|
| Client mods & privacy (Equicord) | `Install-Equicord` | OK |
| Exo Host (fast launch) | Equicord + large `_app.asar` + SKIP_HOST_UPDATE flags | OK; OpenAsar **rejected** |
| Aggressive RAM + latency kernel | `Install-DiscOptKernel` + kit hashes | **Gap:** Apply may soft-skip ffmpeg proxy yet still exit 0; detect requires full layout + hashes → “success” then kernel tile off |
| Complete client debloat | `Invoke-Debloat` | Soft locales/SDK recoverable via state; hard leftovers never trust state — good |
| Discord runtime integrity | Required modules dirs | OK |
| True black AMOLED | Theme + settings | Theme-file alone can count — slightly soft |
| Windows background suppression | `Apply-WindowsTweaks` | Tray may warn “launch once” then detect fails until tray key exists |
| Start Menu launch path | `Restore-StartMenu` | Multi-accept (Update.exe / VBS / Discord.exe) |
| Voice priority QoS | `Set-DiscordVoiceQosPolicies` | Apply can continue with failed QoS; detect needs all installed variants OK |
| Discord variants PTB/Canary | `Set-DiscordVariantQuiet` | Vacuously true if none installed — OK |
| Verified optimizer record | `Complete-DiscordApplyState` | Path-bound to exact `appDir` |

### 7.2 Critical honesty issues

1. **Elevated quiet verify vs real boot safety:** Exo-hosted Apply never runs `Confirm-DiscordBootsAfterMods`. Detect cannot prove bootability.
2. **Kernel soft-skip vs full detect:** Proxy skip + apply exit 0 can disagree with kernel feature + `isApplied`.
3. **QoS / variant partial fails** may not fail the whole apply throw path while detect requires perfection for `isApplied`.
4. **Heuristic fallback** if detect script fails can diverge from live PS detect (titles slightly different).

### 7.3 Smokes vs reality

`Discord.Smoke` proves: pure `DiscordLogic`, `DiscordDetectCore` fixtures, apply **source shape** markers, live detect row presence 5/5.  
Does **not** prove elevated Apply, Equicord network, kernel boot, or QoS on real hardware.

---

## 8. File split proposal (rebuild)

Target: thin runner + named steps (REWRITE Phase 4).

```
Exo/Scripts/Discord/
  Exo-Discord-Run.ps1              # progress only
  Exo-Discord-Detect.ps1           # I/O collection → call pure tests
  Exo-Discord-Repair.ps1           # keep standalone + recovery
  steps/
    00-Host.ps1                    # PS7 assert, paths, EXO_REPORT
    10-Install.ps1                 # Prepare-Discord, modules, login gate
    20-Debloat.ps1                 # debloat + cache + spellcheck
    30-HostFlags.ps1               # settings.json profile
    40-Equicord.ps1                # asar layout + loader + profile/theme
    50-Kernel.ps1                  # install + disable + stock restore
    55-BootVerify.ps1              # optional non-elevated verify protocol
    60-WindowsQuiet.ps1            # Run/tasks/toasts/tray/GPU/FSO
    70-Qos.ps1                     # QoS policies
    80-Variants.ps1                # PTB/Canary
    90-Shortcuts.ps1               # Start Menu
    99-VerifyState.ps1             # marker write
  pure/  (or C# only)
    DiscordDetectCore.ps1 → migrate remaining predicates to DiscordLogic
Exo/Services/
  DiscordLogic.cs                  # expand: every feature predicate + apply plan schema
  DiscordApplyPlan.cs              # ordered steps, soft vs hard policy
kit/                               # binaries + profiles only (no orchestration)
```

**C# ownership:** all detect predicates, forbidden folklore lists, variant map, QoS map, debloat hard/soft rules.  
**PS ownership:** elevate once, mutate, emit EXO_REPORT, write state.

---

## 9. Test matrix

| Scenario | Expect |
|----------|--------|
| Fresh Windows, no Discord | Detect “not installed”; Apply installs + full stack; Discord opens non-elevated |
| Fresh Discord, never logged in | Apply login gate behavior documented; no session wipe |
| Already optimized | Detect all active; Reapply idempotent; recovery merge |
| Discord auto-update (new app-*) | Detect reapply status; Apply restores mods on new build |
| Kernel proxy incompatible GPU | Soft stock ffmpeg; honest kernel tile / no false isApplied |
| Boot failure with kernel | Standalone path rolls back; **Exo path needs explicit post-apply user open test** |
| PTB installed only | QoS + variant quiet; no Equicord on PTB (by design) |
| Stable + PTB + Canary | All QoS policies; variants row active |
| Equicord CDN down | Apply fails loud (hard) |
| Krisp CDN down | Soft-skip; Apply may still succeed |
| Repair default | Stock bootable Discord, login kept, QoS gone, marker cleared |
| Repair FullReset | Login cleared |
| Offline | Repair fails with clear message |
| Linux CI | Discord.Smoke pure + core fixtures only |

---

## 10. Acceptance criteria (rebuild exit)

1. Apply never creates Exo scheduled tasks / Run keys / services.
2. Detect feature set ≡ Apply plan step set (contract table in smoke).
3. Soft-skip only when **path absent**; present-path write failure → retry → hard fail.
4. Kernel: never leave unbootable Discord; either verified boot protocol or auto-disable + honest report.
5. Exo-hosted Apply either (a) de-elevated boot verify or (b) explicit UI “open Discord to verify” gate before claiming Done + applied marker.
6. Repair: signed install, login default keep, QoS removed, recovery restore, marker cleared on success.
7. Update: after new `app-*`, detect never shows full applied without reapply.
8. Smokes green + human checklist Stable Apply → open Discord → voice → Repair.
9. No OpenAsar production path; detect rejects legacy OpenAsar-only “quickstart”.

---

## 11. Risk ranking (Discord)

| Rank | Risk | Severity |
|------|------|----------|
| 1 | Kernel / ffmpeg proxy brick on some GPUs | **Critical** |
| 2 | Elevated Apply skips live boot check | **Critical** |
| 3 | Client update wipes asar/kernel until reapply | **High** |
| 4 | Equicord/upstream break black screen | **High** |
| 5 | Soft-skip kernel vs detect `isApplied` mismatch | **High** |
| 6 | QoS needs elevation/HKLM; partial policy | **Medium** |
| 7 | Debloat removes module Discord later requires | **Medium** (mitigated by allowlist) |
| 8 | Repair leaves Equicord AppData | **Low–Medium** |
| 9 | God-script / kit lib sprawl (regression surface) | **Medium** (eng risk) |

---

# Module B — Steam

## 1. Architecture / call graph

### 1.1 File map

```
Exo/Scripts/Steam/
  Exo-Steam-Run.ps1            # WinUI apply wrapper
  Exo-Steam-Detect.ps1         # Live detect → JSON
  Exo-Steam-Repair.ps1         # Thin → Steam-Optimizer -Repair
  Steam-Optimizer.ps1          # God script (~2580 lines): apply + repair + helpers
  SteamDetectCore.ps1          # Pure classifiers
  README.md
  VERSION                      # 1.9.0
```

### 1.2 Apply call graph

```
SteamOptimizerViewModel.RunAsync
  → PS elevate: Exo-Steam-Run.ps1 -NonInteractive -NoLaunch
       → Steam-Optimizer.ps1
            Assert-ExoPwsh7
            Get-SteamInstallPath
            abort if game active
            Stop-Steam
            capture/merge Windows recovery → steam-optimizer.json (applying)
            Invoke-SteamCompleteClientDebloat
            Disable-SteamWindowsStartup (hard verify)
            Apply-SteamWindowsQuiet (toasts/tray/tasks)
            Set-SteamGpuHighPerformance
            Clear-SteamSafeCaches + Clear-SteamShaderCaches (full pass)
            Optimize-SteamDownloadFolder
            Install-WebHelperTrimHelper → Exo-SteamWebHelperTrim.ps1
            Install-LeanSteamLauncher → Steam-Exo.cmd + Start Menu retarget
            Set-SteamBootstrapFastStart (steam.cfg merge)
            Set-SteamFastLoginHints (loginusers.vdf)
            Set-SteamLibraryConfigHints (config.vdf)
            Set-SteamLocalConfigTweaks (all userdata localconfig.vdf)
            verify essentials → applied | incomplete + EXO_REPORT
```

### 1.3 Detect call graph

```
DetectSteamAsync
  → DetectSteamHeuristic (C# + SteamLogic)
  → Exo-Steam-Detect.ps1 + SteamDetectCore.ps1
       features → isApplied = install + marker + cef + trim + debloat + dl + client + quiet + launch + runtime
```

### 1.4 Repair call graph

```
Exo-Steam-Repair.ps1 → Steam-Optimizer -Repair
  restore *.exo-bak for config.vdf, steam.cfg, loginusers.vdf, localconfig.vdf*
  retarget shortcuts from Steam-Exo* → steam.exe
  remove Steam-Exo.cmd, Exo-SteamWebHelperTrim.ps1, legacy aggressive cmds
  remove Exo desktop/Start Menu clones
  Restore-SteamWindowsIntegration (tasks, toasts, tray, App Paths, Run entries, StartupMode)
  clear steam-optimizer.json on full success
```

---

## 2. All mutations

### 2.1 Files in Steam install root

| File | Role |
|------|------|
| `Steam-Exo.cmd` | Quiet CEF launcher: `start /HIGH steam.exe` + CEF flags; starts trim helper via pwsh 7 |
| `Exo-SteamWebHelperTrim.ps1` | 3s EmptyWorkingSet loop, priority High/BelowNormal, Reinstate-SteamQuiet, trim stats |
| Remove legacy `Steam-Exo-Aggressive.cmd`, `-Lean`, `-Legacy` | Debloat / launcher install |
| `steam.cfg` | Merge `BootStrapperInhibitAll=enable`, `BootStrapperForceSelfUpdate=disable` (+ `.exo-bak`) |
| `config\config.vdf` | Download throttle / multi-download keys (+ `.exo-bak`) |
| `config\loginusers.vdf` | AutoLogin / RememberPassword / MostRecent hints |
| `userdata\*\config\localconfig.vdf` | VDF inject/rewrite overlay/library/friends noise (+ `.exo-bak`) |
| Caches under library roots | htmlcache, logs, dumps, appcache shards, CEF GPUCache, etc. |
| Orphan shader pre-caches | Only if multi-library inventory verified |

### 2.2 CEF launch flags (`DefaultCefArgs`)

```
-cef-disable-gpu
-cef-disable-gpu-compositing
-nofriendsui
-nointro
-nobigpicture
-vrdisable
-no-dwrite
-cef-disable-breakpad
-cef-disable-spell-checking
-cef-disable-extensions
```

**Forbidden (smoke + logic):** `-cef-disable-occlusion`, `-cef-disable-renderer-accessibility`, `'-silent'`.

### 2.3 Multi-library

`Get-SteamLibraryRoots` reads `steamapps\libraryfolders.vdf` `"path"` entries; cache/shader cleanup walks **all** library roots. If inventory unreadable → **hard-fail** full apply (shader step).

### 2.4 Registry / Windows

| Surface | Mutation |
|---------|----------|
| Run keys (HKCU/HKLM/WOW6432) matching Steam | Remove on apply; restore from recovery on repair |
| `HKCU\Software\Valve\Steam\StartupMode` | Force `0`; re-enforced by trim helper while Steam runs |
| Steam notification IDs under Notifications\Settings | `Enabled=0` |
| Tray NotifyIconSettings for steam.exe | `IsPromoted=0` |
| Scheduled tasks name/path Steam (exclude SteamVR/Link/OS/Deck) | Disable |
| `HKCU\...\App Paths\steam.exe` | Point at stock steam.exe |
| `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` | steam.exe + webhelper High if dGPU |

### 2.5 Shortcuts

| Action | Detail |
|--------|--------|
| Start Menu / Programs Steam.lnk | Target `Steam-Exo.cmd` |
| Taskbar pins | **Kept on stock steam.exe** (Windows pin reliability) |
| Desktop Steam*.lnk | **Removed** (no desktop icons policy) |
| Game shortcuts (`-applaunch`, `steam://`) | **Not rewritten** |

### 2.6 State / stats

| Path | Role |
|------|------|
| `%LocalAppData%\Exo\steam-optimizer.json` | Recovery, flags, applyReport |
| `%LocalAppData%\Exo\steam-trim-stats.json` | total/last24h reclaimed bytes (UI trim stats) |

### 2.7 Tasks / services

| Kind | Behavior |
|------|----------|
| Exo-Steam scheduled task create | **Forbidden** (SteamLogic + smoke) |
| Exo service / Run self | **None** |
| Resident behavior | **Only when user launches Steam via Exo launcher** — helper is child of launcher, mutex-limited, not an Exo logon task |

**Note:** `Exo-SteamWebHelperTrim.ps1` re-enforces `StartupMode=0` and strips Steam Run entries **while Steam is open**. That is user-session side effect of the launcher, not an Exo boot-time task. Rebuild should keep this distinction explicit in product copy.

---

## 3. Soft-skip vs hard-fail paths

| Path | Soft-skip | Hard-fail |
|------|-----------|-----------|
| Steam missing | — | Throw |
| Game running | — | Throw (apply + repair) |
| Startup quiet verify | — | Throw |
| Shader inventory unreadable | — | Throw (full pass) |
| `config.vdf` not present yet | Soft-skip Verified=true Skipped=true | Present but bad values → fail |
| `userdata` / `localconfig.vdf` missing | Soft-skip Verified=true | Present but verify fail → fail |
| `loginusers.vdf` missing | Soft skip report | — |
| `steam.cfg` write fail | Report fail bootstrap (not always whole throw) | Essential set may still include other checks |
| Quick mode | Cache/shader skip; never full applied | exit 0 with incomplete by design |
| All-users ProgramData shortcut | Soft skip if no admin write | User Start Menu still required for launchPath |
| Essential verification incomplete | — | Throw incomplete list |

---

## 4. Repair completeness

| Capability | Status |
|------------|--------|
| Restore VDF/cfg backups | **Yes** if `.exo-bak` exists |
| Remove launcher + trim helper | **Yes** |
| Shortcut retarget to steam.exe | **Yes** |
| Windows integration restore | **Yes** (tasks, notifications, tray, App Paths, Run, StartupMode) |
| Keep games / downloads | **Yes** (never touch steamapps content for repair) |
| Clear marker only on success | **Yes**; repair-pending retains recovery |
| Without prior apply / no recovery | Partial (file bak + launcher remove only) |
| Multi-user localconfig all bak | **Yes** (per userdata) |

---

## 5. Update-resilience

| Event | Effect | Recovery |
|-------|--------|----------|
| Steam client update | May rewrite config.vdf / localconfig; may leave launcher | Reapply; detect flags incomplete |
| Steam re-enables StartupMode | Trim helper re-enforces while running; detect may fail if helper not run | Reapply / launch via Exo |
| CEF path layout change | Runtime integrity still searches bin\cef\* | May need path update |
| New library folder | Next Apply inventories again | — |
| Steam removes CEF flags support | Launcher still passes flags | UX degrade, not brick |
| User launches stock steam.exe (taskbar) | No CEF flags / no trim helper | By design for pins; honesty: Start Menu is Exo path |

**Lower brick risk than Discord** — no binary inject into Steam; worst case bad CEF flags (forbidden list already avoids known hang flags).

---

## 6. Exo background footprint

| Item | Present? |
|------|----------|
| Exo scheduled task | **No** (forbidden) |
| Exo service | **No** |
| Helper process | **Only after user starts Steam-Exo.cmd** |
| steam-trim-stats.json writer | Child of helper |

---

## 7. Detect honesty vs Apply

### 7.1 Feature rows

| Detect row | Apply step | Honesty notes |
|------------|------------|---------------|
| Quiet CEF launcher | `Steam-Exo.cmd` text classifier | OK |
| RAM trim + priority | Helper text 2–15s | OK (interval flexible) |
| Complete client debloat | Debloat + download config verified | Combines filesystem + state.downloadOptimized |
| Library / overlay tweaks | localconfig + state snappy/overlay flags | Soft-pass detect if keys absent entirely |
| Windows quiet shell | startup + toasts + tray + tasks | OK |
| Start Menu launch path | Steam.lnk → Steam-Exo.cmd | Taskbar intentionally stock |
| Verified apply | `Test-SteamApplyRecord` + helper present | Many boolean flags |

### 7.2 Critical honesty issues

1. **Fresh install / never opened Steam:** Apply soft-skips `config.vdf` / `localconfig` with `Verified=true`, can still set `applyStatus=applied` if other essentials pass — but detect requires `downloadOptimized` (**not** skipped) and `snappyUi`/`overlayTweaks` (**false** when skipped).  
   → **Apply success banner vs Detect “not fully applied”** on first-run PCs. This is the largest Steam honesty bug.

2. **Taskbar vs Start Menu:** Users pinning Steam keep stock exe → no CEF/trim; detect still can be fully applied via Start Menu. Product should keep messaging (already partially present).

3. **Client tweaks soft-pass in detect** when expectation keys missing: Apply injects keys when userdata exists; if Steam later drops keys, detect may soft-pass without state — combined with state flags is mostly OK.

4. **Heuristic vs script detect** title naming differences minor.

### 7.3 Smokes

`Steam.Smoke`: SteamLogic, SteamDetectCore, apply source audit, pwsh host order, VDF injector **fixture execution**, no Exo-Steam task create, taskbar pin policy strings, soft-skip message markers.  
Not real elevated Apply / multi-library HW.

---

## 8. File split proposal (rebuild)

```
Exo/Scripts/Steam/
  Exo-Steam-Run.ps1
  Exo-Steam-Detect.ps1
  Exo-Steam-Repair.ps1
  steps/
    00-Host.ps1
    10-ResolveSteam.ps1          # path, game-active, stop
    20-Recovery.ps1              # snapshot/merge/save
    30-Debloat.ps1
    40-WindowsQuiet.ps1          # startup hard, toasts, tray, tasks
    50-CacheShader.ps1           # multi-library inventory policy
    60-Launcher.ps1              # Steam-Exo.cmd + shortcuts policy
    70-WebHelperTrim.ps1         # emit helper script body from template
    80-VdfConfig.ps1             # steam.cfg, config.vdf, loginusers
    90-VdfLocalConfig.ps1        # injector
    99-VerifyState.ps1
  pure/
    SteamDetectCore.ps1 → expand C# SteamLogic parity
Exo/Services/
  SteamLogic.cs                  # every detect predicate + soft/hard policy
  SteamApplyPlan.cs
  templates/Exo-SteamWebHelperTrim.ps1.template
```

---

## 9. Test matrix

| Scenario | Expect |
|----------|--------|
| Fresh Steam install, never opened | Soft-skip VDF honest: Apply should not claim full applied OR detect must match soft-skip contract |
| Opened once (userdata+config present) | Full inject; detect all green |
| Multi-library (2+ libraryfolders paths) | Shader orphan clean only with verified inventory; games preserved |
| Game running | Apply/Repair abort |
| Taskbar pin only user | Pins stay steam.exe; Start Menu uses Exo |
| Steam update after apply | Reapply restores; detect incomplete if launcher lost |
| Repair after full apply | Stock launch, no Exo files, quiet integration restored |
| No recovery state repair | Removes Exo files; warns missing recovery |
| Quick mode | Incomplete marker; detect not fully applied |
| Linux CI | Steam.Smoke only |

---

## 10. Acceptance criteria (rebuild exit)

1. No Exo scheduled tasks / services / Run self-start.
2. Soft-skip **only when target file/section absent**; absent → detect row uses same soft rule (or Apply withholds `applied`).
3. Present VDF write/verify failure → hard fail with EXO_REPORT.
4. Multi-library: never delete installed-game shader caches; inventory fail → hard fail or explicit skip policy shared with detect.
5. Taskbar pins remain stock; Start Menu Exo; no desktop icons.
6. Repair restores stock launch + recovery; games untouched.
7. Trim helper interval messaging consistent (3s product; detect accepts 2–15s).
8. Forbidden CEF flags never reintroduced (smoke).
9. Real-machine: fresh Steam + library PC Apply + Repair green.

---

## 11. Risk ranking (Steam)

| Rank | Risk | Severity |
|------|------|----------|
| 1 | Detect/Apply mismatch on first-run soft-skips | **High** (honesty) |
| 2 | VDF injector brace/structure corruption | **High** (mitigated by fixtures + bak) |
| 3 | Shader cleanup if inventory wrong | **High** (hard-fail helps) |
| 4 | CEF flags blank Steam on exotic GPU | **Medium** (forbidden list) |
| 5 | StartupMode fight with Steam client | **Medium** (helper re-enforce) |
| 6 | God-script maintainability | **Medium** (eng) |
| 7 | Users always use taskbar stock pin | **Low** (performance miss, not brick) |

---

# Cross-module comparison

| Dimension | Discord | Steam | Rebuild priority |
|-----------|---------|-------|------------------|
| Brick risk | **High** (DLL/asar) | Low–Medium | Discord first for safety protocol |
| God-script size | Kit lib modular already; Discord-Optimizer still thick | Single ~2.5k line file | Steam split easier |
| Soft-skip culture | Kernel/modules/QoS | VDF absent | Align policy engine |
| Repair quality | Strong reinstall | Strong bak+integration | Keep both patterns |
| Update churn | **Very high** | Medium | Discord reapply UX |
| Background footprint | None Exo | Helper only via launcher | Keep |
| Pure C# classifiers | Good kernel/debloat/QoS | Good CEF/trim/toast | Expand to full feature matrix |
| Smoke depth | Deep shape + live detect row | Deep shape + VDF fixture | Add contract tables |

---

# Shared rebuild contracts (both)

1. **EXO_REPORT schema** (already used): `step|ok`, `step|fail:reason`, `step|skip:reason` persisted even on failure.
2. **Forbidden patterns:** `Register-ScheduledTask -TaskName 'Exo-{Discord,Steam}…'`, folklore registry (`MaxUserPort`, `Win32PrioritySeparation`, FPS unlockers).
3. **Applied marker:** never set `applyStatus=applied` unless detect predicates for that marker would pass **on the same machine state**.
4. **Advisor:** `OptimizerAdvisor.Build` already coaches missing tiles — keep as UX layer only; truth must be in detect.

---

# Immediate rebuild PR sequence (suggested)

Aligned with [`REWRITE-PROGRAM.md`](../REWRITE-PROGRAM.md) Phases 3–4:

1. **Contract table smoke** (detect predicate ↔ apply step ↔ repair step) for both modules.  
2. **Steam soft-skip honesty fix** (cheap, high user-visible correctness).  
3. **Steam god-script split** into steps/.  
4. **Discord boot-verify protocol** for elevated host (de-elevate or deferred verify).  
5. **Discord kit split** Host / Equicord / Kernel / Quiet / QoS.  
6. Real-machine gates: Stable Discord Apply+Repair; Steam fresh + multi-library.

---

# Source index (absolute paths)

| Path |
|------|
| `C:\Users\Erix\Exo\Exo\Services\DiscordLogic.cs` |
| `C:\Users\Erix\Exo\Exo\Services\SteamLogic.cs` |
| `C:\Users\Erix\Exo\Exo\Services\OptimizerStateService.cs` |
| `C:\Users\Erix\Exo\Exo\Services\ScriptBundleService.cs` |
| `C:\Users\Erix\Exo\Exo\ViewModels\DiscordOptimizerViewModel.cs` |
| `C:\Users\Erix\Exo\Exo\ViewModels\SteamOptimizerViewModel.cs` |
| `C:\Users\Erix\Exo\Exo\Views\DiscordOptimizerPage.xaml.cs` |
| `C:\Users\Erix\Exo\Exo\Views\SteamOptimizerPage.xaml.cs` |
| `C:\Users\Erix\Exo\Exo\Scripts\Discord\**` |
| `C:\Users\Erix\Exo\Exo\Scripts\Steam\**` |
| `C:\Users\Erix\Exo\tools\Discord.Smoke\Program.cs` |
| `C:\Users\Erix\Exo\tools\Steam.Smoke\Program.cs` |

---

*End of inventory.*
