# Exo Rebuild Program — Comprehensive Master Plan

**Status:** ACTIVE — **v2 dual-track plan** (pain-first + architecture)  
**Baseline code:** v2.6.8 on `main`  
**Research corpus:** `docs/rewrite/` (~2,500 lines of module inventories from 4 parallel audits)  
**Last expanded:** 2026-07-16 (critique pass: dual-track is the recommended plan)

> **Honest goal:** production-reliable aggressive optimizers on any *supported* Windows 10/11 x64 PC.  
> Not magic "zero risk forever." Risk is driven down via contracts, retries, repair, CI gates, and real-machine sign-off.

---

## 0. Plan critique — is this the best plan?

### 0.1 What the first plan was good at

- Correct **strangle** strategy (big-bang rewrite would brick you again)
- Real **module inventories** (Internet / Discord / Steam / NVIDIA / shell)
- Clear **laws**, PR DAG, hardware matrix, agent rules
- Honest about NVIDIA (no true driver rollback)

### 0.2 What was wrong for *your* pain

| Gap in v1 plan | Why it matters |
|----------------|----------------|
| Architecture-first (Internet split → Steam → Discord…) | You feel pain **now**; 4 weeks of Internet refactor does not fix Discord boot or “applied but red” |
| 14–16 week single track | Feels endless; no early “it just works” milestone |
| Under-weighted **honesty bugs** | Steam soft-skip applied, Discord boot skip — **days of work**, huge trust gain |
| UI polish in Phase 6 only | Advisor + detect lies are **trust**, not cosmetics — pull earlier |
| No **agent freeze** | Cursor/Grok mega-PRs will re-break anything we rebuild |
| No **kill switch** | Plan did not say when to abandon a WP or revert a release |
| “Zero risk forever” aspirational language elsewhere | Impossible; plan must say **supported matrix only** |

### 0.3 Alternatives considered

| Approach | Verdict |
|----------|---------|
| **A. Greenfield rewrite (new repo, C# only)** | Cleanest long-term; **3–6+ months dark**; highest chance of never shipping. **Reject** while you need a product. |
| **B. Architecture-only strangle (v1 plan)** | Safe; **too slow on user-visible pain**. Partial keep. |
| **C. Hotfix forever / no structure** | How you got here. **Reject**. |
| **D. Dual-track (pain blitz + architecture)** | **Adopt.** Stop bleeding in 1–2 weeks; rebuild structure in parallel behind flags/contracts. |

### 0.4 Best plan (adopted): dual-track

```
TRACK P — PAIN (user-visible, max 2 weeks)
  P0  Freeze random agent mega-PRs (only plan WPs)
  P1  CI must pass before Release
  P2  Detect ≡ Apply honesty (Internet strings, Steam soft-skip, Discord boot)
  P3  No Exo background tasks (already mostly done; lock with smoke)
  P4  Discord boot-safe + NVIDIA display retries (already started; finish hard)
  P5  You run real Apply checklist on your PC once → ship v2.7.0 "trust"

TRACK A — ARCHITECTURE (weeks 2–14, never blocks Track P hotfixes)
  A1  Shared EXO_REPORT + contract tables
  A2  Split Internet builder (template)
  A3  Split Steam / Discord / NVIDIA god-scripts
  A4  ExoModulePlate + Advisor v2
  A5  v3.0 when contracts + plate + all modules extracted
```

**Rule:** Track P always wins scheduling if something bricks Internet/Discord boot.  
**Rule:** Track A never ships a half-split god-script that fails smoke.

### 0.5 Single success metric (weekly)

> **“On a clean install of Latest, Apply Internet + Discord + Steam without losing network or a bootable client; UI does not lie; Task Scheduler has zero Exo-* tasks.”**

If a week does not move that metric, the week was wasted architecture.

---

## Table of contents

0. [Plan critique — is this the best plan?](#0-plan-critique--is-this-the-best-plan)
1. [Mission and pain](#1-mission-and-pain)
2. [Non-negotiable laws](#2-non-negotiable-laws)
3. [Current system map](#3-current-system-map)
4. [Target architecture](#4-target-architecture)
5. [Cross-cutting contracts](#5-cross-cutting-contracts)
6. [Module deep plans](#6-module-deep-plans)
7. [Shell and CI deep plan](#7-shell-and-ci-deep-plan)
8. [Week-by-week schedule](#8-week-by-week-schedule)
9. [Full PR DAG](#9-full-pr-dag)
10. [Test and hardware matrix](#10-test-and-hardware-matrix)
11. [Human sign-off checklist](#11-human-sign-off-checklist)
12. [Risk register](#12-risk-register)
13. [Agent working rules](#13-agent-working-rules)
14. [Definition of done (v3.0)](#14-definition-of-done-v30)
15. [Research index](#15-research-index)
16. [Immediate next actions](#16-immediate-next-actions)

---

## 1. Mission and pain

### 1.1 Product mission

Exo is a **free, no-compromise Windows performance hub**: Internet latency/throughput, Discord, Steam, NVIDIA profiles/display — aggressive, deterministic, local-only (no telemetry).

Users should click **Apply** and get:

- Real tweaks applied (not folklore)
- App/client still boots
- Internet still works
- UI status matches reality
- **No Exo background tasks/services/startup**
- Repair when we promise repair

### 1.2 Why users feel pain (mapped to root causes)

| Pain | Root cause (from deep research) |
|------|----------------------------------|
| Bricked Internet | Historical Wi-Fi disable / NCSI / winsock; largely fixed 2.6.4-2.6.6; residual NIC advanced-prop risk |
| "Applied" but red UI | Detect/Apply contract drift (bindings, Wi-Fi, Steam soft-skip, Discord kernel) |
| Discord will not open | Kernel/ffmpeg/asar after client update |
| NVIDIA "failed" | Display NVAPI flaky; god-script; Reset is not rollback |
| Random regressions | 200 KB PS god-files + parallel agents without contracts |
| CI theater | Smokes = shape/logic; Release can ship without CI green; no GPU Apply |

### 1.3 Strategy: strangle, not big-bang

1. Extract pure C# cores (like ExoInternetLogic today).  
2. Split god-scripts into stage libraries.  
3. One module per PR stack.  
4. Ship weekly on main.  
5. Human Apply checklist before releases that touch optimizers.

**Why not rewrite every line in one shot:** that is how you get multi-week dark periods and worse bricks. This plan rewrites *everything that matters*, in order, with proof at each step.

---

## 2. Non-negotiable laws

Ship blockers — any PR that violates is rejected.

| # | Law | Enforcement |
|---|-----|-------------|
| L1 | Aggressive by design — no silent safe-mode product | AGENTS.md, TWEAK-AUDIT.md |
| L2 | No Exo background footprint (tasks/Run/services Exo-*) | Smoke forbids create; purge on Apply |
| L3 | Apply must work (retry, alternate path, loud fail only if nothing landed) | Module acceptance |
| L4 | Detect equals Apply contract | Contract smoke tables |
| L5 | No invented registry folklore | Forbidden marker lists |
| L6 | Internet: never disable Wi-Fi; never auto winsock; snapshot+rollback | Internet.Smoke + golden path |
| L7 | Discord/Steam: Repair restores bootable client | E2E + human |
| L8 | NVIDIA Reset = status clear only | UI copy + smoke |
| L9 | Motion: XAML Storyboards only | Ui.Smoke |
| L10 | Stack: .NET 10 + WinUI + self-contained | csproj + CI |
| L11 | Release after CI green + checklist for touched modules | workflow + human |
| L12 | PowerShell 7+ only for optimizers | Run wrappers |

---

## 3. Current system map

### 3.1 Scale at v2.6.8

| Surface | Approx |
|---------|--------|
| C# under Exo/ | ~24k lines, ~80 files |
| PowerShell Scripts | ~36 scripts, ~700 KB |
| Largest scripts | Nvidia-Optimizer ~200 KB; Steam-Optimizer ~114 KB |
| XAML | ~25 views/styles |
| Smokes | Network, Discord, Steam, Nvidia, Ui |
| CI | ci.yml build/format/smokes/D-S-Net E2E; release.yml tag push **without CI gate** |

### 3.2 Shared host pipeline

```
WinUI Page -> ViewModel
  |- Refresh -> OptimizerStateService / Network.Probe
  |     |- C# heuristic (Logic)
  |     +- PS Detect -> JSON features
  |- Apply -> PowerShellRunner (UAC) -> Exo-*-Run -> Optimizer
  |     +- EXO_PROGRESS / EXO_REPORT -> UI
  +- Repair -> Exo-*-Repair
```

### 3.3 Module maturity scorecard

| Module | Maturity | God-file | Brick risk | Honesty risk | Repair |
|--------|----------|----------|------------|--------------|--------|
| Internet | Highest | High (builder) | Low post-2.6.6 | Medium (stale strings) | High snapshot |
| Steam | High | High | Low | High soft-skip applied | High |
| Discord | Medium | Medium-high | Medium kernel | High | High reinstall |
| NVIDIA | Medium | Extreme | Medium display | Medium | **None** (status clear) |
| Shell | Medium | Page dup | Low motion fixed | Advisor v1 thin | n/a |

---

## 4. Target architecture

### 4.1 End state layers

```
SHELL (WinUI 3 / .NET 10 / fixed frame)
  Home | ExoModulePlate | AdvisorV2 | NVIDIA Panel
           |
CONTRACTS (pure C#, Linux CI unit tests)
  FeatureId | DetectResult | ApplyPlan | Forbidden[]
  ExoInternetLogic | DiscordLogic | SteamLogic | NvidiaLogic
           |
EXECUTORS (thin PS7 + small helpers)
  one Run.ps1 per module | lib/Step-*.ps1 | no god-files
  shared: Exo.Report | Exo.Snapshot | Exo.NoTasks
           |
PROOF
  Smokes | Contract tables | Fixture matrix | HW Apply gate
  Release = CI green + human checklist
```

### 4.2 Shared libraries to create

| Library | Lang | Responsibility |
|---------|------|----------------|
| Exo.Contracts | C# | FeatureId, DetectResult, ApplyPlan, ReportStep |
| Exo.Report | C#+PS | EXO_REPORT emit/parse, UI rows |
| Exo.NoBackground | C#+PS | Purge Exo-* tasks/Run; forbidden patterns |
| Exo.Elevate | C# | Single elevation, progress log, cancel |
| Scripts/lib/Exo.Common.ps1 | PS | Logging, paths, AppData, PS7 assert |

### 4.3 Target module folder shape

```
Exo/Modules/<Name>/
  <Name>Logic.cs
  <Name>Detect (C# and/or PS)
  <Name>Apply steps (PS lib)
  <Name>Repair
  ViewModel + Page (via ExoModulePlate)
```

Migrate by adding files first; delete god-script last.

---

## 5. Cross-cutting contracts

### 5.1 EXO_REPORT canonical

```
EXO_REPORT:<stepId>|<status>|<optional reason>
status in { ok, fail, skip }
```

Every Apply step emits a line. Last apply UI uses one schema for all modules.

### 5.2 EXO_PROGRESS

```
EXO_PROGRESS:<0-100>|<human status>
```

### 5.3 Feature contract table (per module, checked by smoke)

| featureId | detect predicate | apply stepId(s) | repair stepId(s) | soft-skip when absent? |
|-----------|------------------|-----------------|------------------|------------------------|
| (filled per module in research docs) | | | | |

### 5.4 Soft-skip policy

| Situation | Behavior |
|-----------|----------|
| Resource absent | skip OK; do **not** mark that feature applied |
| Resource present, write fails | retry 2-3x then fail feature |
| Optional enhancement | skip OK if core works; detect must not require optional |

### 5.5 AppData state files

| File | Owner |
|------|-------|
| network-snapshot.json | Internet pristine baseline |
| network-apply-state.json | rollback |
| network-optimizer.json | preset, report, benchmark |
| discord-optimizer.json | Discord |
| steam-optimizer.json | Steam |
| steam-trim-stats.json | optional |
| nvidia-optimizer.json | NVIDIA marker/DRS |
| nvidia-panel-settings.json | Panel |
| nvidia-display-prefs.json | last display method |

### 5.6 Forbidden (repo-wide smoke)

- Register-ScheduledTask Exo (not Unregister)
- schtasks /Create Exo
- Run key to Exo path
- New-Service Exo*
- ElementCompositionPreview in motion paths
- Folklore markers (ExoInternetLogic.Forbidden + module lists)

---

## 6. Module deep plans

### 6.1 Internet (Phase 2) — TEMPLATE

Full inventory: **docs/rewrite/research-internet.md** (~720 lines)

**Strengths:** fail-closed path; snapshot; ExoInternetLogic; dense smoke.  
**Weak:** ExoInternetApplyScriptBuilder god-file; repair triplicated; detect subset of apply; stale strings; MMCSS snapshot gap.

| WP | Work | Days |
|----|------|------|
| I-1 | Split builder: Snapshot, HostStack, Adapters, Bindings, Metrics, Probe, Rollback, Repair, Benchmark | 3-4 |
| I-2 | RegistryTargets sync all mutations | 1 |
| I-3 | Detect rows for every user-visible apply class | 2 |
| I-4 | Kill stale UI strings | 0.5 |
| I-5 | Fixtures eth/wifi/dual/vpn | 2 |
| I-6 | Single source Repair-Internet vs BuildRepair | 2 |
| I-7 | Real-machine R1+R2 | 1 |

**Acceptance:** no Wi-Fi disable; rollback works; Repair no auto winsock; contract 100%; smoke+human green.

---

### 6.2 Steam (Phase 3)

Full inventory: **docs/rewrite/research-discord-steam.md**

**Critical bug:** soft-skip missing config can still set applyStatus=applied while detect incomplete.

| WP | Work | Days |
|----|------|------|
| S-1 | Split god-script into Recovery, Startup, Launch, VDF, Debloat, Shader, Trim, Repair | 3-4 |
| S-2 | Soft-skip != overall applied | 1-2 |
| S-3 | Expand SteamLogic all rows | 2 |
| S-4 | Multi-library fixtures | 1 |
| S-5 | Real-machine fresh + multi-lib | 1 |

**Acceptance:** fresh Apply honest; pins stay steam.exe; Repair stock; no Exo tasks.

---

### 6.3 Discord (Phase 4)

Full inventory: **docs/rewrite/research-discord-steam.md**

**Critical risks:** EXO_SKIP_BOOT_FLASH; kernel soft-skip vs detect; client updates.

| WP | Work | Days |
|----|------|------|
| D-1 | Orchestrator only; kit libs own stages | 2 |
| D-2 | Boot verify always (or disable kernel) | 2 |
| D-3 | Detect optional proxy vs required version.dll | 1 |
| D-4 | Update-heal harden | 2 |
| D-5 | Keep signed installer-before-delete Repair | keep |
| D-6 | Real-machine update simulation | 1-2 |

**Acceptance:** launches after Apply; never unbootable; Repair stock; smoke+E2E green.

---

### 6.4 NVIDIA (Phase 5)

Full inventory: **docs/rewrite/research-nvidia.md**

**Truth:** Reset clears status only. No Exo logon tasks. Profiles+DRS+display with retries.

| WP | Work | Days |
|----|------|------|
| N-1 | Split god-script into stage libs | 5-7 |
| N-2 | Single DRS classifier | 2 |
| N-3 | Display bundle+retry+multi-mon | 3 |
| N-4 | Laptop/Optimus paths | 2 |
| N-5 | Panel + advisor | 2 |
| N-6 | Real-machine dual mon | 2 |

**Never automatic:** DDU, full driver reinstall, tray logon task, Reset-as-rollback.

---

### 6.5 Placeholders

Epic / Riot / Brave / Windows — **frozen until v3.0 core modules done.**

---

## 7. Shell and CI deep plan

Full inventory: **docs/rewrite/research-shell-ci.md**

### 7.1 Shell (Phase 6)

| WP | Work | Days |
|----|------|------|
| U-1 | Token audit vs exo-ui-craft | 1-2 |
| U-2 | ExoModulePlate control | 3-4 |
| U-3 | Migrate 4 pages to plate | 2-3 |
| U-4 | Advisor v2 from EXO_REPORT | 2-3 |
| U-5 | DPI strategy | 2-3 |
| U-6 | Motion + reduced-motion | 1-2 |
| U-7 | Home metrics honesty | 2 |
| U-8 | a11y pass | 1-2 |

### 7.2 CI/release (Phase 1)

| WP | Work | Days |
|----|------|------|
| C-1 | Release requires CI on same SHA | 0.5-1 |
| C-2 | Shared contract-table runner | 2 |
| C-3 | Document NVIDIA HW as manual | 1 |
| C-4 | Self-hosted GPU runner | backlog |

---

## 8. Week-by-week schedule (dual-track)

### Track P — stop the pain (weeks 0–2)

| Week | Focus | Ship gate |
|------|-------|-----------|
| **W0** | Plan live; freeze random multi-module agent PRs; confirm 2.6.8 release | Program + freeze |
| **W1a** | **P1** Release requires CI | No more untested releases |
| **W1b** | **P2** Honesty blitz: Steam soft-skip≠applied; Internet stale detect strings; Discord boot verify | Trust UI |
| **W1c** | **P3–P4** Lock no Exo tasks; NVIDIA display retry path complete | No bg tasks / display works |
| **W2** | **P5** You run full Apply checklist on your PC; ship **v2.7.0 “trust”** | **User-visible win** |

### Track A — rebuild structure (weeks 2–14, parallel after W1)

| Week | Focus | Ship gate |
|------|-------|-----------|
| W2–W3 | A1 contracts + A2 Internet builder split start | smokes only |
| W4 | Internet fixtures + split finish | **v2.7.x** if needed |
| W5–W6 | Steam god-script split | **v2.8.0** |
| W7–W8 | Discord kit ownership + update heal | **v2.8.1** |
| W9–W11 | NVIDIA split + display reliability | **v2.9.0** |
| W12–W14 | ExoModulePlate + Advisor v2 + DPI | **v3.0.0** |
| W15+ | Stabilize; no new modules | bugfix |

~14 weeks to v3.0 structure; **first trust release at week 2**, not week 4.

---

## 9. Full PR DAG

```
PR-D0  docs research + this plan
  ->
PR-C1  release requires CI
  ->
PR-C2  NoBackground + EXO_REPORT schema
  ->
PR-I1  internet builder split
  ->
PR-I2  internet contracts + fixtures
  ->
PR-I3  internet repair dedupe -> v2.7.0
  ->
PR-S1  steam split
  ->
PR-S2  steam honesty -> v2.8.0
  ->
PR-D1  discord boot-safe
  ->
PR-D2  discord detect optional kernel -> v2.8.1
  ->
PR-N1  nvidia split
  ->
PR-N2  nvidia display reliability -> v2.9.0
  ->
PR-U1  ExoModulePlate migrate
  ->
PR-U2  advisor v2 + DPI + motion -> v3.0.0
```

Optional parallel after C2: ExoModulePlate scaffold only (no module behavior).

---

## 10. Test and hardware matrix

### 10.1 Every PR

Test-Repository, Network/Discord/Steam/Nvidia/Ui smokes, CI E2E D/S/Net.

### 10.2 Fixture IDs

F-NET-ETH-INTEL, F-NET-WIFI-ONLY, F-NET-VPN, F-NET-DUAL,  
F-STM-FRESH, F-STM-MULTI-LIB,  
F-DSC-STABLE, F-DSC-UPDATE,  
F-NV-DESKTOP, F-NV-LAPTOP, F-NV-NO-HELPER

### 10.3 Real-machine

| ID | Machine | Modules |
|----|---------|---------|
| R1 | Desktop eth+wifi | Internet, NVIDIA |
| R2 | Laptop wifi-only | Internet |
| R3 | Discord daily | Discord |
| R4 | Steam library | Steam |
| R5 | Dual display NVIDIA | NVIDIA |

---

## 11. Human sign-off checklist

```
Tag: ____  SHA: ____  Date: ____

[ ] CI green on SHA
[ ] Smokes failed=0
[ ] Install Exo.exe; version matches
[ ] Opens; drag works; no flash-close
[ ] Task Scheduler: ZERO Exo-* after Apply
[ ] Internet Apply online; unplug eth, Wi-Fi works
[ ] Internet Repair works
[ ] Discord Apply launches; Repair stock
[ ] Steam Apply launches; Repair stock
[ ] NVIDIA Apply profiles+display (if GPU)
[ ] NVIDIA Reset copy = status only
[ ] Advisor matches missing features
[ ] Signer: ____
```

---

## 12. Risk register

| ID | Risk | Sev | Mitigation |
|----|------|-----|------------|
| R-01 | Split re-break | High | Strangle; tests every PR |
| R-02 | False Internet rollback | Med | DNS anchor; docs |
| R-03 | Discord kernel brick | High | Boot verify D-2 |
| R-04 | Steam applied lie | High | S-2 |
| R-05 | NVIDIA no repair | Acc | Honest UI |
| R-06 | Release w/o CI | High | C-1 |
| R-07 | Agent thrash | High | This plan only |
| R-08 | DPI UI | Med | U-5 |
| R-09 | Missing NvDisplay | High | Publish verify |
| R-10 | Enterprise netsh block | Med | skip+reason |
| R-11 | Parallel random PRs | High | Freeze to DAG |
| R-12 | Scope creep modules | Med | Freeze placeholders |

---

## 13. Agent working rules

1. Read research-*.md for that module before coding.  
2. One PR stack = one module or core.  
3. Update research when architecture changes.  
4. Never Register-ScheduledTask Exo.  
5. Executors do not push/release unless authorized.  
6. Coordinator runs smokes + VERSION/CHANGELOG.  
7. Internet/Discord boot regression: hotfix or revert 24h.  
8. Prefer retry-to-success over failure theater.

---

## 14. Definition of done (v3.0)

- [x] All four modules have contract tables + smokes (`tools/Contracts.Smoke`)  
- [x] No PS god-file > 80 KB without exception note (**exception:** `Steam-Optimizer.ps1` ~118 KB and `Nvidia-Optimizer.ps1` ~204 KB remain monoliths with thin `lib/*Bootstrap.ps1` stage entry points; full strangle deferred post-v3)  
- [x] ExoModulePlate on all optimizers  
- [x] Advisor v2 from detect + EXO_REPORT  
- [x] Release requires CI  
- [ ] Human checklist R1-R4 done once  
- [x] Zero Exo-* tasks after full Apply (smoke + purge)  
- [x] NVIDIA Reset wording audited  
- [x] .NET 10 throughout  
- [x] README/CHANGELOG honest  

### God-file size exception (Wave 3)

| File | Size | Status |
|------|-----:|--------|
| `Exo/Scripts/Steam/Steam-Optimizer.ps1` | ~118 KB | **Exception** — thin `Steam/lib/Steam.Bootstrap.ps1` stage IDs; full extract next |
| `Exo/Scripts/Nvidia/Nvidia-Optimizer.ps1` | ~204 KB | **Exception** — thin `Nvidia/lib/Nvidia.Bootstrap.ps1` stage IDs; full extract next |
| `ExoInternetApplyScriptBuilder` | split | **Done** — `.Repair.cs` + `.Benchmark.cs` partials |

---

## 15. Research index

| Document | Lines | Scope |
|----------|------:|-------|
| [rewrite/README.md](rewrite/README.md) | index | Library map |
| [rewrite/research-internet.md](rewrite/research-internet.md) | ~720 | Mutations, safety, split, fixtures |
| [rewrite/research-discord-steam.md](rewrite/research-discord-steam.md) | ~620 | Call graphs, soft-skip, repair |
| [rewrite/research-nvidia.md](rewrite/research-nvidia.md) | ~580 | Stages, NPI/DRS, display, never-auto |
| [rewrite/research-shell-ci.md](rewrite/research-shell-ci.md) | ~610 | Tokens, motion, CI gaps, plate |
| [TWEAK-AUDIT.md](TWEAK-AUDIT.md) | — | Tweak keep/exclude |
| [INTERNET-GOLDEN-PATH.md](INTERNET-GOLDEN-PATH.md) | — | Internet safety narrative |
| [../AGENTS.md](../AGENTS.md) | — | Product + agent laws |

**Total research + plan:** ~3,000+ lines of rebuild guidance.

---

## 16. Immediate next actions (best order)

| # | Track | Action | Owner |
|---|-------|--------|-------|
| 1 | — | Land plan critique (dual-track) on main | coordinator |
| 2 | P | Ensure v2.6.8 **Exo.exe** release is live | coordinator |
| 3 | P | **PR-C1** Release workflow requires CI green | agent |
| 4 | P | **PR-P2** Honesty blitz (Steam applied, Internet detect, Discord boot) | agent |
| 5 | P | You run §11 checklist once → **v2.7.0 trust** | **you** |
| 6 | A | Only after P green: Internet builder split (I-1) | agent |

**Do not start Internet god-file split before honesty blitz** — that optimizes the plan, not your pain.

---

*Best plan = dual-track. Implementation starts at section 16 Track P — not architecture tourism.*
