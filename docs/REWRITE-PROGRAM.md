# Exo Rebuild Program

**Status:** Active plan (v3.0 track)  
**Starts from:** v2.6.8 on `main`  
**Owner:** coordinator agent + human (Erix) for real-machine Apply sign-off  
**Honest goal:** *production-reliable* aggressive optimizers on any supported PC — not magical “zero risk forever” (impossible), but **no brick, no Exo background footprint, Apply completes, Repair works, UI tells the truth**.

---

## 1. Why this plan exists

Recent releases fixed real bricks and honesty bugs, but the product still suffers from:

| Problem | Symptom |
|---------|---------|
| God-scripts | `Nvidia-Optimizer.ps1` ~200 KB, Steam ~114 KB — hard to reason about, easy to re-break |
| Dual truth | Detect vs Apply contracts drift (bindings, Wi‑Fi, display partial) |
| Soft-skip culture | “Honest fail” and soft-skip can look like the product worked when it didn’t |
| Verification gap | Smokes prove **script shape + pure logic**, not elevated Apply on real hardware |
| Agent thrash | Parallel Cursor/Grok PRs ship without a single module contract |

This program **stops thrash**: one architecture, one module at a time, freeze rules, real-PC gate before each release.

---

## 2. Non-negotiable product rules (ship blockers)

1. **Aggressive by design** — do not water down tweaks into “safe mode product” (see `AGENTS.md` / `TWEAK-AUDIT.md`).
2. **No Exo background footprint** — no Exo scheduled tasks, logon tasks, Run keys, or Exo-named services. Purge leftovers on Apply/Repair.
3. **Apply must work** — retry, alternate path, then fail loud only if nothing landed. Prefer success over partial theater.
4. **Deterministic scope** — only the selected app/hardware; no invented registry folklore.
5. **Repair is real** where we mutate (Internet snapshot; Discord/Steam repair). NVIDIA Reset = status clear only (honest copy forever).
6. **Detect ≡ Apply contract** — if Apply doesn’t do X, detect must not require X for “applied”.
7. **Motion** — XAML Storyboards only; never hand-off composition visuals.
8. **Stack** — .NET 10 + Windows App SDK current; self-contained publish.

---

## 3. Target architecture (end state)

```
┌─────────────────────────────────────────────────────────┐
│  WinUI shell (Exo Instrument)                           │
│  Home · Module plate · Live advisor · Panel (NVIDIA)    │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  C# contracts (pure, unit-tested)                       │
│  *DetectLogic / *ApplyPlan / OptimizerAdvisor           │
│  Feature rows, path policy, forbidden folklore lists    │
└───────────────────────────┬─────────────────────────────┘
                            │ elevates once
┌───────────────────────────▼─────────────────────────────┐
│  Thin PS runners (one entry per module)                 │
│  Invoke plan steps → report EXO_REPORT JSONL            │
│  No god-files; shared lib: Snapshot, Report, Elevate    │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│  Proof layer                                            │
│  Smokes (shape) · Fixture matrix · Optional HW Apply CI │
│  Human sign-off checklist per release                   │
└─────────────────────────────────────────────────────────┘
```

**Migration rule:** extract pure logic into C# first (like NetworkLogic today); PS becomes dumb executor. New code never grows the god-scripts.

---

## 4. Phases (multi-week, sequential)

### Phase 0 — Freeze & baseline (1–2 days) ✅ partially done

- [x] Product rules in AGENTS (no bg tasks, Apply must work)
- [x] Live advisor v1 (`OptimizerAdvisor`)
- [x] Internet fail-closed defaults (no Wi‑Fi kill, no auto-winsock)
- [ ] Publish **v2.6.8** release asset green on GitHub
- [ ] Tag program start; open tracking issue / milestone **v3.0**
- [ ] Human baseline: one clean PC checklist (see §6) run on current Latest

**Exit:** Latest release = known baseline; program doc on `main`.

---

### Phase 1 — Shared engine + contracts (3–5 days)

**PR-1.1 Shared Apply report / snapshot lib (PS + C#)**  
- Single `EXO_REPORT` schema, single snapshot helpers for Internet (template for others).  
- Forbidden: Register-ScheduledTask Exo-*, schtasks /Create Exo-*.

**PR-1.2 Module contract tests**  
- For each module: table of (feature id → detect predicate → apply step → repair step).  
- Smoke fails if detect requires what apply never writes.

**PR-1.3 CI gate**  
- Release workflow **requires** CI success (or document manual hold).  
- `Test-Repository` + all smokes on every PR.

**Exit:** No module can ship detect/apply drift without CI red.

---

### Phase 2 — Internet rebuild (template module) (4–7 days)

Internet is already the best-structured (NetworkLogic + script builder). Make it the gold standard.

**PR-2.1** Split `NetworkApplyScriptBuilder` into named step modules (snapshot, host stack, adapters, bindings, probe, rollback, repair).  
**PR-2.2** Hardware matrix fixtures: Intel/Realtek/Killer Ethernet; Wi‑Fi-only; dual-NIC; VPN present (exclude list).  
**PR-2.3** Real-machine gate: Apply latency + throughput + Repair on at least 1 Ethernet PC + 1 Wi‑Fi-only PC.  
**PR-2.4** UI: Internet advisor + live probe metrics polish; no false “Client/LLDP off”.

**Exit:** Internet Apply never disables Wi‑Fi; Repair restores connectivity; smokes + human checklist green.

---

### Phase 3 — Steam rebuild (3–5 days)

Steam is already the most reliable optimizer; extract god-script.

**PR-3.1** Pure `SteamLogic` expansion for every detect row.  
**PR-3.2** Apply steps: startup quiet, launch path, VDF merge, webhelper trim — each with soft-skip only when path **absent**, hard-retry when path **present but write failed**.  
**PR-3.3** Repair restores stock launch + quiet shell without Exo tasks.  
**PR-3.4** Real-machine: fresh Steam + existing library PC.

**Exit:** Fresh install Apply succeeds; Repair launches stock Steam.

---

### Phase 4 — Discord rebuild (5–8 days)

Highest residual risk (client updates, kernel/ffmpeg).

**PR-4.1** Split kit: Host / Equicord / Kernel / Windows quiet / QoS — separate scripts, one orchestrator.  
**PR-4.2** Kernel: never leave unbootable Discord — verify boot or auto-disable kernel.  
**PR-4.3** Repair: signed installer verify before delete (keep).  
**PR-4.4** Real-machine: Stable install, Apply, Discord update simulation, Repair.

**Exit:** Discord opens after Apply; Repair returns bootable stock.

---

### Phase 5 — NVIDIA rebuild (7–12 days)

Hardest: driver surface + no true Repair.

**PR-5.1** Split: profiles (NPI+DRS), display (NvDisplay), debloat, tray, detect.  
**PR-5.2** Display: retry NVAPI; ensure helper always bundled; multi-monitor matrix.  
**PR-5.3** UI: NVIDIA Panel remains; optimizer never promises driver rollback.  
**PR-5.4** Real-machine: desktop + laptop (if available); profile verify + max Hz.

**Exit:** Profiles import + DRS verify; display prefs land or clear error only after retries; no Exo tasks.

---

### Phase 6 — Shell UI craft pass (5–8 days)

Not “rewrite every pixel for fun” — **usable, modern, consistent**.

**PR-6.1** Design tokens audit (AMOLED, spacing from `exo-ui-craft`).  
**PR-6.2** Shared module plate component (header, advisor, features, foot) — kill four copy-pasted pages drift.  
**PR-6.3** Live advisor v2: step list, progress mapped to EXO_REPORT, “what this PC needs”.  
**PR-6.4** DPI / fixed-frame strategy decision (scale content vs allow snap sizes).  
**PR-6.5** Motion: storyboard-only audit; kill remaining `EnableDependentAnimation` risks if any composition creeps back.

**Exit:** One module plate; advisor driven by detect; Ui.Smoke + optional UiPreview click QA.

---

### Phase 7 — Hardware Apply matrix & release discipline (ongoing)

| Profile | Internet | Discord | Steam | NVIDIA |
|---------|----------|---------|-------|--------|
| Desktop Ethernet + Wi‑Fi | required | required | required | if GPU |
| Laptop Wi‑Fi only | required | required | optional | notebook path |
| Dual display NVIDIA | — | — | — | required |
| Fresh Discord/Steam install | — | required | required | — |

**Release rule:**  
1. CI green  
2. All smokes failed=0  
3. Human checklist §6 signed for changed modules  
4. Then tag + Release  

**Versioning:**  
- v2.7.x = Phase 1–2 shippable slices  
- v2.8.x = Steam/Discord  
- v2.9.x = NVIDIA  
- **v3.0.0** = shell craft + all modules on new contracts  

---

## 5. What we will *not* do

- Rewrite everything in one PR (guarantees bricks).  
- Invent registry folklore “for performance”.  
- Install Exo background tasks “to keep tray clean”.  
- Call NVIDIA Reset a driver rollback.  
- Claim zero risk on every PC forever (unsupported hardware always exists).  

---

## 6. Human real-machine checklist (every release)

Print / copy for each ship:

```
[ ] Install Latest Exo.exe from GitHub (SHA if published)
[ ] App opens; no flash-close; window draggable
[ ] Task Scheduler: no Exo-* tasks after Apply
[ ] Internet: Apply latency → still online; Wi-Fi still works if cable unplugged
[ ] Internet: Repair restores connectivity
[ ] Discord: Apply → Discord launches and can log in
[ ] Discord: Repair → stock Discord launches
[ ] Steam: Apply → Steam launches
[ ] Steam: Repair → stock path
[ ] NVIDIA (if present): Apply → profiles verified in detect; display Hz/color OK or Panel works
[ ] Live advisor text matches reality (no "Client/LLDP off" lies)
```

---

## 7. Working agreement (agents + human)

1. **One module (or shared lib) per PR stack** — no mega multi-module thrash.  
2. **Coordinator** merges/releases only after smokes + checklist.  
3. **Cursor/Grok** implement against this doc; if conflict with AGENTS, AGENTS wins.  
4. **Pain stop:** if a release bricks Internet or Discord boot, hotfix within 24h or revert tag.  

---

## 8. Immediate next actions (this week)

| # | Action | Owner |
|---|--------|--------|
| 1 | Land this doc on `main` | agent |
| 2 | Finish/fix v2.6.8 GitHub Release if still missing | agent |
| 3 | Phase 1.1 — shared EXO_REPORT + no-task audit smoke | agent |
| 4 | Phase 1.2 — Internet detect/apply contract table + tests | agent |
| 5 | Run §6 checklist on your PC once on Latest | **you** |

---

## 9. Key decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Full rewrite strategy | **Strangle / extract**, not big-bang rewrite | Keeps app shippable every week |
| Source of truth for logic | **C# pure cores** + thin PS | Testable on Linux CI; less god-script |
| Success metric | Apply works + Repair + no Exo bg + detect match | User pain is bricks and lies, not scores |
| NVIDIA Repair | **Never** claim full rollback | Driver reality; honesty without failure theater |
| UI rewrite | Shared plate + advisor first | Biggest UX win per line changed |

---

## 10. PR Plan (ordered)

1. **docs: REWRITE-PROGRAM** — this file (no behavior change).  
2. **ci: release waits for CI / harden gates**.  
3. **core: EXO_REPORT schema + forbidden Exo-task smoke**.  
4. **internet: split builder + contract tests** (Phase 2 start).  
5. **internet: fixture matrix + human checklist green → v2.7.0**.  
6. **steam: extract + retry-on-present** → v2.8.0.  
7. **discord: split kit + boot verify** → v2.8.x.  
8. **nvidia: split + display reliability** → v2.9.0.  
9. **ui: shared module plate + advisor v2** → **v3.0.0**.  

---

*Last updated: 2026-07-16 · Track: Exo v3.0 rebuild*
