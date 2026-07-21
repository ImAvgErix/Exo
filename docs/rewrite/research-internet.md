# Internet / Network Optimizer — Architecture Inventory (Full Rebuild Research)

**Status:** Research only (no product code changes)  
**App version at inventory:** 2.6.8  
**Scope:** Internet optimizer module only  
**Primary sources:** shipped C# / PS / docs / smoke harnesses under `C:\Users\Erix\Exo`  
**Aligned with:** `docs/REWRITE-PROGRAM.md` Phase 2 (Internet as template module)

---

## 0. Executive summary

The Internet module is the most mature Exo optimizer: pure decision core (`ExoInternetLogic`), giant elevated PowerShell generator (`ExoInternetApplyScriptBuilder`), orchestration + detect UI feed (`ExoInternetOptimizerService`), WinUI page + VM, standalone rescue (`Repair-Internet.ps1`), and the densest smoke suite (`tools/Internet.Smoke`).

**What works well**

- Fail-closed path policy since **v2.6.6+**: never `Disable-NetAdapter` on Wi‑Fi; never disable Client/LLDP; never touch NCSI/proxy AutoDetect; never force Speed & Duplex; no auto winsock/IP reset on Repair.
- Pristine pre-apply snapshot → true Repair restore → post-apply full-snapshot auto-rollback.
- Preset knobs (latency vs throughput) are audited by string markers in smoke.
- RegistryKeyword-first NIC writes (locale-safe for standardized props).
- Structured `EXO_REPORT` + before/after ping/DNS benchmark + rollback surface in UI.

**What is weak / rebuild drivers**

- `ExoInternetApplyScriptBuilder.cs` is a single ~1.8k-line string god-file (apply + repair + benchmark + snapshot + rollback duplicated with `Repair-Internet.ps1`).
- Detect feature rows only cover a **subset** of apply mutations; several applied knobs have no UI row (powercfg, DNS cache, NetBIOS, tunnels, DO, prefix policy, extended TCP).
- Stale copy still exists in a few places (success string about “Wi‑Fi disabled”; model XML comment about Client/LLDP off; older CHANGELOG/golden-path sentences about Wi‑Fi disable gates).
- `Balanced` preset exists in the enum/state but has **no Apply CTA** (only latency / highest download).
- Real-machine E2E is mostly human checklist; smokes are script-audit + mocked snapshot/repair exec, not live netsh/NIC mutation.
- Apply exit code is always `0` even after rollback (honest via apply-state JSON + message, not via process exit).

---

## 1. Current architecture diagram (text)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ WinUI shell                                                              │
│  MainWindow NavRail → InternetOptimizerPage.xaml                         │
│       │                                                                  │
│       ▼                                                                  │
│  InternetOptimizerViewModel                                              │
│    • Initialize / Refresh → Network.ProbeAsync()                         │
│    • Low latency / Highest download → ApplyPresetAsync(options fail-closed)│
│    • Repair → RepairAsync()                                              │
│    • LoadProofLayer: report / benchmark / rollback / HasRestoreSnapshot  │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ AppServices.Internet = ExoInternetOptimizerService                            │
│                                                                          │
│  ProbeAsync ──► .NET NetworkInterface + registry + netsh capture         │
│            ──► DetectMediaProfileAsync (temp PS probe script)            │
│            ──► ExoInternetLogic (path / NIC eval / LSO/RSC/autotune match)   │
│            ──► ExoInternetSnapshot + ExoInternetFeatureRow[]                     │
│                                                                          │
│  ApplyPresetAsync                                                        │
│    1. DetectMediaProfileAsync                                            │
│    2. RunBenchmarkAsync (before, once)                                   │
│    3. ExoInternetApplyScriptBuilder.Build(preset, options, media)            │
│    4. Write %TEMP%\exo-net-*.ps1 → powershell -Verb runas (elevated)     │
│    5. Parse %TEMP%\exo-net-last.log EXO_REPORT                           │
│    6. Load network-apply-state.json (rollback)                           │
│    7. Persist state → network-optimizer.json                             │
│    8. RunBenchmarkAsync (after) + ProbeAsync verify                      │
│                                                                          │
│  RepairAsync → BuildRepair() → elevated PS → ClearSavedPreset            │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ pure (no I/O)
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ ExoInternetLogic (static)                                                    │
│  KnobsFor · DecidePath · IsWifiAdapter · SelectBandDisplayValue          │
│  EvaluateNic · Autotune/Lso/RscMatches · AuditApplyScript                │
│  ParseApplyReport · TryParseBenchmark · Forbidden/Required markers       │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ generates strings
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ ExoInternetApplyScriptBuilder                                                │
│  Build()           → elevated apply (snapshot → mutate → probe → rollback)│
│  BuildRepair()     → elevated true-restore / stock fallback              │
│  BuildBenchmark()  → non-elevated EXO_BENCH JSON                         │
│                                                                          │
│  Shared: CommonSafetyFunctions (Test-ExoConnectivity, Report, paths)     │
│  RegistryTargets[] must stay synced with mutation set                    │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ on disk artifacts
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ %LocalAppData%\Exo\                                                      │
│   network-snapshot.json      pristine baseline (never overwritten)       │
│   network-apply-state.json   rollback + connectivityAfterApply           │
│   network-optimizer.json     preset, preferEth flag, report, benchmark   │
│ %TEMP%\exo-net-last.log / exo-net-repair-last.log                        │
└──────────────────────────────────────────────────────────────────────────┘

Standalone rescue (no app):
  Repair-Internet.ps1  (self-elevating, irm|iex, -Hard winsock/ip)
  ↻ duplicates restore logic from BuildRepair (drift risk)

Gate:
  tools/Internet.Smoke  → ExoInternetLogic + builder audit + PS parse + SnapshotExecHarness
  tools/NetScriptDump  → dump generated scripts for CI/manual E2E
  docs/INTERNET-GOLDEN-PATH.md → product contract doc
```

### Layer responsibilities (today)

| Layer | File(s) | Role |
|-------|---------|------|
| Pure decisions | `Exo/Services/ExoInternetLogic.cs` | Preset knobs, band score, path policy, Wi‑Fi classifier, NIC score, script audit markers, report/bench parsers |
| Script generation | `Exo/Services/ExoInternetApplyScriptBuilder.cs` | All elevated mutations + snapshot/rollback/repair/benchmark as PS text |
| Orchestration + detect | `Exo/Services/ExoInternetOptimizerService.cs` | Probe, apply/repair process elevation, state files, feature rows, matches-preset verify |
| Models | `Exo/Models/ExoInternetSnapshot.cs` | Presets, media profile, feature rows, apply options, report steps, bench, rollback status |
| UI | `InternetOptimizerViewModel.cs`, `InternetOptimizerPage.xaml(.cs)` | Dual CTA, repair/refresh, feature tiles, proof layer |
| Rescue | `Repair-Internet.ps1` | Offline-capable restore; `-Hard` nuclear |
| Contract doc | `docs/INTERNET-GOLDEN-PATH.md` | What apply is allowed to do |
| Tests | `tools/Internet.Smoke/*` | Compile-time/string/exec-mock gates |

### Data flow: Apply

1. VM sets **fail-closed options**: `PreferEthernetDisableWifi=false`, `RestartEthernet=false`.
2. Service detects media (physical eth/wifi, band, vendor, cores, bindings).
3. Optional baseline benchmark if none stored.
4. Builder emits PS with knobs + media hints baked in.
5. Elevated script: **snapshot or abort** → host registry/netsh → adapters → bindings/metrics → post-probe window (60s) → full rollback if offline → write apply-state.
6. Host always `exit 0` from apply script body after handling rollback; C# treats non-zero as hard failure (snapshot abort uses `exit 2`).
7. Service saves preset, parses report, surfaces rollback as apply failure message, runs after-bench + re-probe.

### Data flow: Repair

1. Service builds `BuildRepair()` PS → elevate.
2. Snapshot present → exact restore + adapter restart for adv props → delete snapshot only if zero restore failures.
3. No snapshot → approximate stock reset.
4. Always: re-enable disabled physical NICs, force `ms_tcpip`/`ms_tcpip6`/`ms_pacer`, DHCP renew, 30s probe.
5. Still offline → **exit 1**, no automatic winsock; user must run `Repair-Internet.ps1 -Hard`.
6. Service clears `network-optimizer.json` / apply-state via repair script and `ClearSavedPreset` on success paths.

---

## 2. Complete list of apply steps / mutations

Sources: `ExoInternetApplyScriptBuilder.Build`, `RegistryTargets[]`, `ExoInternetLogic.KnobsFor`, golden-path doc.

### 2.1 Pre-flight (no mutation)

| Step | Behavior | EXO_REPORT |
|------|----------|------------|
| `Save-ExoNetworkSnapshot` | If snapshot exists → keep pristine; else capture full baseline | `snapshot` ok/skip/fail |
| Abort | Snapshot fail → **exit 2**, zero mutations | `apply` fail |

**Snapshot contents (`snapshotVersion: 1`)**

- netsh raw: tcp global, tcp heuristics, udp global  
- structured: `Get-NetOffloadGlobalSetting`, `Get-NetTCPSetting` (many fields)  
- `regValues`: every `RegistryTargets` entry + dynamic per-IF Nagle + NetBT NetbiosOptions + class PnPCapabilities  
- `advancedProps` (RegistryKeyword + value per physical adapter)  
- `bindings` (ComponentID + enabled)  
- `adapterStates` (name, status, adminUp, wifi flag)  
- `ipInterfaces` (metric + AutomaticMetric)  
- `rss` (enabled, base processor, profile)  
- `powercfg` (3 setting pairs AC/DC)  
- `dynamicPorts` (ipv4/ipv6 × tcp/udp)  
- `prefixPolicies` (+ raw)  
- `services` (DoSvc start type)

### 2.2 Registry host stack

| Path | Name | Apply action |
|------|------|--------------|
| `HKLM\...\Tcpip\Parameters` | `DisableTaskOffload` | DWORD **0** |
| same | `EnablePMTUDiscovery` | DWORD **1** |
| same | `GlobalMaxTcpWindowSize`, `TcpWindowSize`, `EnableTCPChimney`, `EnableTCPA`, `EnableDCA`, `TcpNumConnections`, `LargeSystemCache` | **Remove** (clear folklore) |
| `...\Tcpip\ServiceProvider` | `LocalPriority` / `HostsPriority` / `DnsPriority` / `NetbtPriority` | **4 / 5 / 6 / 7** |
| `...\Multimedia\SystemProfile` | `SystemResponsiveness` | **10** |
| same | `NetworkThrottlingIndex` | **10** (force clean rewrite) |
| `...\SystemProfile\Tasks\Games` | `GPU Priority` | **8** |
| same | `Priority` | **6** |
| same | `Scheduling Category` | `"High"` |
| same | `SFIO Priority` | `"High"` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched` | `NonBestEffortLimit` | **0** |
| `...\DeliveryOptimization\Config` | `DODownloadMode` | **0** |
| `...\Dnscache\Parameters` | `MaxCacheTtl` | **86400** |
| same | `MaxNegativeCacheTtl` | **5** |
| `...\NetBT\Parameters\Interfaces\*` | `NetbiosOptions` | **2** (disable over TCP/IP) |
| `...\Class\{4d36e972-...}\NNNN` | `PnPCapabilities` | **24** (match DriverDesc) |
| Per `Tcpip\Parameters\Interfaces\*` | `TcpAckFrequency`, `TCPNoDelay`, `TcpDelAckTicks` | Latency: **1, 1, 0**; Throughput: **remove** |

**RegistryTargets also list (snapshot-only / intentional non-write today):**

- NCSI `NoActiveProbe`, `DisablePassivePolling` — **not written by apply** (fail-closed)  
- DNSClient `EnableMulticast` — **not written** (LLMNR left alone)  
- HKCU Internet Settings `AutoDetect` — **not written**  
- BITS `EnableBITSMaxBandwidth` — **removed only if present**

### 2.3 powercfg (active scheme)

| Subgroup GUID | Setting GUID | Value |
|---------------|--------------|-------|
| Wireless Adapter Settings | Power Saving Mode | **0** max perf (AC+DC) |
| PCI Express | Link State Power Management | **0** off (AC+DC) |
| USB | Selective suspend | **0** off (**AC only**) |
| Laptop extra | Wireless max on DC restamp | if `IsLikelyLaptop` |

### 2.4 netsh / Set-NetTCPSetting (host)

| Setting | Latency | Throughput | Notes |
|---------|---------|------------|-------|
| `rss=enabled` | yes | yes | |
| `autotuninglevel` | **normal** | **experimental** | |
| `rsc` | **disabled** | **enabled** | |
| heuristics | disabled | disabled | |
| supplemental congestion | cubic | cubic | internet + internetcustom |
| `taskoffload=enabled` (ip) | yes | yes | |
| timestamps | disabled | disabled | build-gated report |
| fastopen + fallback | enabled | enabled | |
| pacingprofile | **off** | skip (default) | latency only |
| hystart | **disabled** | skip | latency only |
| ecncapability | **disabled** | **enabled** | |
| udp `uro` | **disabled** if build ≥ 26100 | skip | latency only |
| dynamicport tcp/udp ipv4/ipv6 | start 1025 num 64511 | same | |
| Set-NetTCPSetting AutoTuning | Normal / Experimental | | Internet + InternetCustom |
| ScalingHeuristics | Disabled | Disabled | |
| MaxSynRetransmissions | 2 | 2 | InternetCustom |
| NonSackRttResiliency | Disabled | Disabled | |
| InitialRtoMs / MinRtoMs | **1000 / 300** | untouched | latency only |
| teredo / isatap / 6to4 | disabled | disabled | |
| IPv6 prefixpolicy `::ffff:0:0/96` | precedence **55** label 4 | only if PreferIpv4First | latency always; throughput if eth in use |

### 2.5 Services / optional features

| Target | Action |
|--------|--------|
| `DoSvc` | StartupType **Manual** |
| `Psched` | Automatic + Start-Service best-effort |
| SMBv1 optional feature | Disable if Enabled (NoRestart) |
| BITS policy `EnableBITSMaxBandwidth` | Remove if present |

### 2.6 Adapter bindings

| ComponentID | Apply |
|------------|-------|
| `ms_pacer`, `ms_tcpip`, `ms_tcpip6` | **Enable only** |
| Client / Server / LLDP / LLTD / Multiplexor | **Never disabled** (fail-closed since 2.6.6) |

### 2.7 Interface metrics / path

| Condition | Action |
|-----------|--------|
| Any Up Ethernet | Primary (real IPv4 + fastest): metric **1**; other eth: **5+**; AutomaticMetric **Disabled** |
| PreferEthernet option true | Wi‑Fi IPv4 metric **75** (adapter **stays enabled**) |
| PreferEthernet false (UI default) | No Wi‑Fi metric raise |
| Ethernet restart option true | Restart Up Ethernet only; re-stamp metrics up to 20s |
| Default UI | **No** NIC restart |

### 2.8 Per-adapter advanced (all physical non-VPN)

**Both media (when property exists)**

- Checksum offloads Rx+Tx (keywords + Set-NetAdapterChecksumOffload)  
- `*LsoV2IPv4/6` = preset (0 latency / 1 throughput) + Enable/Disable-NetAdapterLso  
- RSC enable/disable per preset  
- `*InterruptModeration` + ITR / DisplayName rate  
- `*FlowControl` (0 latency / 3 throughput)  
- Power keywords → **0**: `*EEE`, `*EnergyEfficientEthernet`, `*GreenEthernet`, `*SelectiveSuspend`, `*ReduceSpeedOnPowerDown`, `*PMARPOffload`, `*PMNSOffload`, `*WakeOnMagicPacket`, `*WakeOnPattern`, plus vendor: `AdvancedEEE`, `GreenEthernet`, `EnableGreenEthernet`, `PowerSavingMode`, `ULPMode`, `SipsEnabled`, `GigaLite`  
- `*IdleRestriction` (1 latency / 0 throughput)  
- Set-NetAdapterPowerManagement selective suspend / wake / ARP / NS off  
- Ring buffers `*ReceiveBuffers` / `*TransmitBuffers`: **mid (~75th %ile)** latency, **max** throughput  
- PnPCapabilities 24 via class key match  

**Ethernet-only**

- `*RSS` on + Set-NetAdapterRss Enabled  
- `*NumRssQueues` capped by `RssQueueBudget(physical cores)`  
- BaseProcessorNumber **2** if ≥4 logical CPUs  
- DMA coalescing / Adaptive IFS / Gigabit Lite off; Master Slave Auto  
- **Never** write Speed & Duplex / Wait-for-Link force (Wait-for-Link DisplayName try on Intel I225/6 only)  
- Jumbo `*JumboPacket` → 1514/1500/lowest  
- `*PriorityVLANTag` 1 + DisplayName priority enabled  
- RSS profile NUMAStatic then ClosestProcessor best-effort  
- Vendor extras: Intel ULP/SIPS/EEE; Realtek Green/EEE/ARP/NS; Killer idle/EEE  

**Wi‑Fi-only**

- Off via fuzzy DisplayName: MIMO PS, uAPSD, power save, packet coalescing, ULP, WoWLAN, BT collab, fat channel intolerant, mixed mode protection, etc.  
- Keyword scan power/wake/BT → Disabled/Off  
- TX power highest; channel width best/auto; wireless mode latest  
- MU-MIMO / OFDMA / Beamforming / BSS Color on when present  
- Throughput Booster: **on** throughput preset, **off** latency  
- Preferred Band via `Select-BandDisplayValue` (Prefer 6 if want6 else Prefer 5; never only/2.4)  
- Roaming: latency prefers Low (fallback Medium); throughput Medium  
- **Never** Restart-NetAdapter on Wi‑Fi  

### 2.9 Post-apply probe / rollback mutations

On connectivity failure after 60s window:

1. Re-enable all disabled physical adapters  
2. Full snapshot restore: registry, advanced props, bindings, metrics, subset of TCP netsh  
3. Restart adapters touched by adv props  
4. Force critical bindings + AutomaticMetric on all physical  
5. DNS ServiceProvider stock priorities 499/500/2000/2001  
6. Remove NCSI NoActiveProbe if present  
7. DNS clear + `ipconfig /renew`  
8. Re-probe ≤45s  
9. Write `network-apply-state.json` with `rollback:true`

### 2.10 Explicit non-mutations (fail-closed / non-goals)

- No public DNS force  
- No IPv6 disable  
- No SystemResponsiveness 0 / NetworkThrottlingIndex ffffffff  
- No MaxUserPort / chimney / LargeSystemCache folklore writes  
- No Wi‑Fi Disable-NetAdapter  
- No Client/LLDP disable  
- No NCSI NoActiveProbe / LLMNR / proxy AutoDetect  
- No auto winsock/ip reset  
- No Game Mode / HAGS / CPU plan (removed from Internet)  
- No force MTU/jumbo for gaming beyond standard frame  

---

## 3. Safety net — snapshot, probe, rollback, repair

### 3.1 Snapshot

| Property | Assessment |
|----------|------------|
| Captured before any mutation | **Strong** — smoke enforces order + abort |
| Never overwritten on re-apply | **Strong** — pristine baseline |
| Coverage vs mutation set | **Mostly strong** — RegistryTargets kept near writes; dynamic Nagle/NetBT/PnP; adv props by keyword; metrics; RSS; powercfg; ports; prefix; DoSvc |
| Gaps | SMBv1 feature state not snapshotted; tunnel states not snapshotted (repair uses `default`); some netsh globals only via English raw labels; Games MMCSS string props restored via regValues only if listed (GPU Priority etc. are in targets? **No** — Games keys are written but **not** in `RegistryTargets[]` → **snapshot gap**) |
| Type fidelity | **Hardened** — List[object]::new + ToArray; smoke exec mixed Int32/Int64/String/ExpandString/MultiString/Binary |

**Weakness detail — Games MMCSS keys:** apply writes `GPU Priority`, `Priority`, `Scheduling Category`, `SFIO Priority` under `Tasks\Games`, but `RegistryTargets` does **not** include those four names. True restore may leave Games MMCSS tweaks after Repair unless they existed pre-apply as other values (they won't be restored to pre-Exo if absent wasn't recorded).

### 3.2 Connectivity probe

| Probe | Where | Strength |
|-------|-------|----------|
| TCP 443 to 1.1.1.1 / 8.8.8.8, 3s | apply post, rollback, repair, rescue | Strong real-internet signal |
| Optional bind to local IPv4 | eth verification helper | Present; **Wi‑Fi disable gate unused** (no disable path) |
| DNS resolve msftconnecttest | fallback if TCP anchors blocked | Good for filtered nets |
| Link-up gate before probing | post-apply window | Strong (avoids premature rollback) |
| 60s post-apply / 45s rollback / 30s repair windows | | Strong vs old single-retry |

**Weaknesses**

- Corporate firewalls blocking 1.1.1.1/8.8.8.8 **and** public DNS → false offline → full rollback of a fine apply.  
- Captive portals may fail TCP 443 to CF/Google while “online” for browser.  
- Probe success does not prove gaming path (only reachability).  
- Apply script still documents “bound Ethernet gate” for Wi‑Fi disable that no longer exists.

### 3.3 Auto-rollback

| Aspect | Assessment |
|--------|------------|
| Trigger | Post-apply probe fail after full window |
| Scope | **Full snapshot restore** (2.6.6+ critical fix) |
| Adapter bounce after adv restore | Yes |
| Critical bindings force | Yes |
| Honesty | `rollback:true` + UI banner + apply message fail |
| Exit code | Apply still **exit 0** — C# detects via apply-state, not exit code |
| Weak | Message in service still says “Host-stack tweaks remain applied” in one branch — **stale** relative to full restore (full restore does restore host stack; residual gap is incomplete snapshot fields) |

### 3.4 In-app Repair

| Path | Behavior | Strength |
|------|----------|----------|
| Snapshot | Exact restore + restart touched NICs | Strong |
| Keep snapshot on partial fail | Retryable | Strong |
| No snapshot | Approximate stock | Weaker but documented |
| No auto Hard | Explicit only | Strong fail-closed |
| Always re-enable adapters + critical bindings | | Strong |

### 3.5 Standalone `Repair-Internet.ps1`

| Capability | Notes |
|------------|-------|
| Self-elevate + irm\|iex | Strong rescue story |
| Same restore/stock as in-app | **Duplicated** ~500 lines — drift risk vs `BuildRepair` |
| `-Hard` winsock + ip/ipv6 reset | Nuclear; reboot required; exit 2 |
| Offline EMERGENCY paste block | Strong for bricked UI |
| Clears apply-state + optimizer state | Yes |

### 3.6 Safety net summary scorecard

| Mechanism | Works | Weak |
|-----------|-------|------|
| Snapshot abort-before-mutate | Yes | Games MMCSS not in RegistryTargets |
| Pristine keep | Yes | User who wants “new baseline” must delete file manually |
| Post-apply rollback | Full restore | False positive on filtered nets; exit 0 ambiguity |
| Repair true restore | Yes | Duplicated in Rescue PS; adv prop name renames after driver update |
| Repair Hard | Explicit | Still breaks more than restore; not undoable |
| UI honesty | Report + rollback banner | Some stale success strings; Congestion always green |

---

## 4. Detect / UI feature rows vs Apply

### 4.1 Feature rows produced by `ProbeAsync` + media

| Row title | Source | OK condition | Matches apply? |
|-----------|--------|--------------|----------------|
| Task offload | Registry DisableTaskOffload | not disabled | Yes (always force 0) |
| LSO v2 | First Up adapter `*LsoV2IPv4` DisplayValue | `LsoMatches(preset)` | Yes for primary; other NICs not shown |
| RSC | netsh tcp show global | `RscMatches` | Yes (global); per-NIC RSC not shown |
| Auto-tuning | netsh Receive Window Auto-Tuning | `AutotuneMatches`; unknown/`—` skips | Yes |
| Congestion | netsh supplemental | **always true** | Apply sets CUBIC but row never fails |
| Nagle / ACK | any IF TcpAckFrequency/TCPNoDelay | latency wants off keys present; thr wants not forced | Partial (any IF, not all) |
| MMCSS | SystemResponsiveness + NetworkThrottlingIndex | both ~10; Balanced soft | Yes host; Games keys not verified |
| QoS reserve | NonBestEffortLimit | 0% or — | Yes |
| Path policy | DecidePath line | always true | Informational only |
| Adapter bindings | ms_pacer/tcpip/tcpip6 on | ok or !presetApplied | **Aligned** with fail-closed apply (2.6.6+) |
| Ethernet metric | live IPv4 metric | ≤5 when eth in use | Yes (want 1) |
| Wi‑Fi while Ethernet | wifi up/down + prefer flag | **always true** | Aligned (never fail for Wi‑Fi up) |
| Wi‑Fi capability | band target + gen + radio hint + current band | always true | Band apply not verified as OK/fail |
| NIC status | EvaluateNic FC/IM/Idle/SS | preset-aware | Partial (primary only) |
| Adapter | media · vendor · link class | always true | Informational |
| Last apply | rollback / connectivity flag | fail if rolled back | Honesty row |

### 4.2 Apply mutations with **no** detect row

- powercfg wireless / PCIe / USB  
- DNS ServiceProvider priorities  
- DNS cache TTL  
- NetBIOS NetbiosOptions  
- DO / DoSvc / BITS  
- Tunnels teredo/isatap/6to4  
- Prefix policy IPv4-first  
- Dynamic ports  
- Extended TCP (timestamps, TFO, pacing, HyStart, URO, ECN, RTO)  
- RSS BaseProcessorNumber / queue count  
- Jumbo / Priority VLAN / DMA coalescing  
- Wi‑Fi TX power, channel width, preferred band match, roam  
- PnPCapabilities  
- SMBv1  
- Per-adapter settings on non-primary NICs  

### 4.3 UI CTAs vs presets

| UI control | Behavior |
|------------|----------|
| Low latency | `LowestLatency`, options fail-closed |
| Highest download | `HighestThroughput` |
| Repair | snapshot or stock; caption swaps with `HasRestoreSnapshot` |
| Refresh | re-probe |
| Balanced | **No button** — only default saved state when never applied |
| Restart Ethernet | **Not exposed** (always false) |
| Prefer Ethernet | **Not exposed** (always false) |

### 4.4 Proof layer UI

- Benchmark summary (p50, jitter, DNS before→after)  
- Rollback notice banner  
- Expandable last-apply `EXO_REPORT` rows  
- Repair hint text  

### 4.5 Detect/Apply contract mismatches to fix in rebuild

1. Congestion always OK → either verify CUBIC or drop row.  
2. Preferred Band not checked as applied.  
3. Success message still can claim “Wi‑Fi disabled when Ethernet has a real IP” (`ExoInternetOptimizerService` policy string) — **false**.  
4. `ExoInternetMediaProfile` XML comment still mentions Client/LLDP off.  
5. Golden-path doc still has residual “Wi‑Fi disable gated on probe” language vs code never disables.  
6. Latency roam: code prefers Low; golden-path table says Medium for latency — **doc bug**.  
7. Ring buffers: golden-path says Max for latency; code uses mid — **doc bug**.

---

## 5. Known fail-closed rules from v2.6.6+

From CHANGELOG 2.6.6 / 2.6.7-ish honesty pass and shipped code:

1. **Never disable Wi‑Fi adapters** (`ShouldDisableWifi` always false; no `Disable-NetAdapter -Name` in apply).  
2. **Never disable Client / File Sharing / LLDP / LLTD** — enable-only critical stack.  
3. **Never write NCSI** `NoActiveProbe` / passive polling.  
4. **Never write** proxy WinHTTP/IE `AutoDetect` off.  
5. **Never force Speed & Duplex** (`*SpeedDuplex` forbidden by smoke).  
6. **Apply defaults:** `PreferEthernetDisableWifi=false`, `RestartEthernet=false` (VM hardcodes).  
7. **Repair never auto winsock/ip reset** — `Repair-Internet.ps1 -Hard` only.  
8. **Detect honesty:** bindings row only requires QoS+IPv4/IPv6; Wi‑Fi-while-Ethernet never fails for staying up.  
9. **Post-apply rollback is full snapshot restore**, not metrics/Wi‑Fi-only.  
10. **Snapshot failure aborts apply** (exit 2).  
11. **Unknown autotune/LSO/RSC = skip match** (no false “not applied”).  
12. **Prefer-* band never band-only / never 2.4 when higher exists.**  
13. **No XP folklore** (ForbiddenApplyPatterns + smoke).  
14. **No Game Mode / HAGS / power throttling** in Internet scripts.  
15. **VPN/virtual adapters excluded** from physical set.

---

## 6. Failure modes on weird PCs

### 6.1 VPN always on (WireGuard, OpenVPN, Tailscale, ZeroTier, Meta Tunnel, Cisco, etc.)

| Risk | Mitigation today | Residual |
|------|-------------------|----------|
| VPN NIC tuned as Ethernet | Description + Virtual filter | Enterprise VPN names not in list may get tuned |
| Metrics change breaks VPN split tunnel | Metrics only on physical eth/wifi | Custom VPN metric policy may fight Exo |
| Snapshot huge / restore wrong adapter | Resolve by name then ifDesc | Adapter rename after VPN install |
| Bound probe vs VPN default route | Post-probe is any-interface | May “pass” over VPN while LAN path broken |

### 6.2 Dual NIC / multi-homed (2.5G + 1G + dock)

| Risk | Mitigation | Residual |
|------|------------|----------|
| Wrong primary | Rank HasIp then ReceiveLinkSpeed | Policy may prefer dock that flaps |
| Secondary eth metric 5+ | Set-EthMetrics | USB dock re-enum renames adapters |
| Both tuned | Intentional | One flaky NIC can fail post-probe → full rollback of good primary |

### 6.3 Wi‑Fi only

| Risk | Mitigation | Residual |
|------|------------|----------|
| No eth metrics / RSS | Skipped | — |
| Band prefer wrong locale DisplayName | Keyword + fuzzy | Exotic OEM only-* lists |
| Wi‑Fi restart avoided | Never restart Wi‑Fi | Some props need radio toggle / reboot |
| Laptop DC power | Wireless max DC | Battery life impact |

### 6.4 Ethernet only

| Risk | Mitigation | Residual |
|------|------------|----------|
| Cable drop after apply | No Wi‑Fi kill | User has no Wi‑Fi hardware → still stranded if eth dies |
| Metric reverts to auto ~20–25 | Re-stamp; UI flags metric >5 | After reboot some drivers reset metric |

### 6.5 Enterprise / domain / GPO

| Risk | Residual |
|------|----------|
| GPO re-applies QoS / DO / firewall | Apply looks good then reverts |
| 1.1.1.1/8.8.8.8 blocked | False rollback or repair fail |
| Hardened netsh options missing | skip-with-reason (good) |
| Require signed scripts | Bypass -ExecutionPolicy may still hit WDAC |
| Elevation blocked | UAC cancel path |

### 6.6 Non-English Windows

| Risk | Mitigation | Residual |
|------|------------|----------|
| DisplayName English-only Wi‑Fi knobs | RegistryKeyword first for standards | Band/roam/TX power still DisplayName-heavy |
| netsh raw English labels for restore | structured Get-NetTCPSetting preferred | Raw label parse fails → defaults |
| Adapter names localized | ifDesc fallback | Both can change |

### 6.7 Other edge cases

| Scenario | Failure mode |
|----------|--------------|
| Hyper-V / WSL vEthernet | Filtered if Virtual/desc match; mis-class if not |
| Bluetooth PAN | NonWifiVirtual regex |
| Killer Control Center fights advanced props | Exo does not kill Killer service |
| Intel I225/I226 unstable with power off | Many power kills; rare link flap → rollback window |
| Driver update mid-session | Snapshot keywords missing on restore |
| APIPA only eth | ethInUse false → keep Wi‑Fi path |
| IPv6-only network | Usable IPv4 required for ethInUse; may mis-policy |
| Offline apply attempt | Snapshot ok; post-probe fail → full rollback (good) |
| Snapshot JSON from older Exo | version field required; unreadable → stock fallback |
| pwsh 7 vs Windows PowerShell 5.1 | Snapshot List typing bug fixed; rescue uses windows powershell for elevate |
| SMBv1 disable | Needs reboot sometimes; not snapshotted |
| Admin cancelled UAC | Clean fail message |
| Concurrent apply | No lock; dual elevate races on same snapshot |

---

## 7. Exact file split proposal for rebuild

Goal: PR-2.1 style modules; pure C# decisions stay pure; PS becomes thin executor **or** stays generated but split by concern. Prefer keeping generation testable without elevation.

### 7.1 Proposed tree

```
Exo/
  Modules/Internet/                          # or keep Services/ + Models/ split
    Models/
      ExoInternetPreset.cs                       # enum only
      ExoInternetMediaProfile.cs
      ExoInternetSnapshot.cs                     # probe aggregate
      ExoInternetFeatureRow.cs
      ExoInternetApplyOptions.cs
      ExoInternetApplyReportStep.cs
      ExoInternetBenchmarkResult.cs
      ExoInternetRollbackStatus.cs
      ExoInternetSnapshotDocument.cs             # typed snapshot schema v1/v2 (NEW)

    Logic/                                   # pure, no I/O — smoke-friendly
      ExoInternetLogic.cs                        # facade re-export or thin
      ExoInternetPresetKnobs.cs                  # KnobsFor + PresetKnobs
      NetworkPathPolicy.cs                   # DecidePath, ShouldDisableWifi, IsUsableIpv4
      NetworkWifiClassifier.cs               # IsWifiAdapter + regexes
      NetworkBandSelector.cs                 # Score/SelectBand + InferBandSupport
      NetworkNicEvaluator.cs                 # EvaluateNic, vendor, buffers, RSS budget
      NetworkMatchRules.cs                   # Autotune/Lso/RscMatches
      NetworkScriptAudit.cs                  # Forbidden/Required + AuditApplyScript
      NetworkReportParser.cs                 # ParseApplyReport, TryParseBenchmark

    Detect/
      INetworkProbe.cs
      NetworkProbeService.cs                 # ProbeAsync host facts
      NetworkMediaDetector.cs                # DetectMediaProfileAsync (+ optional PS)
      ExoInternetFeatureRowBuilder.cs            # ALL rows; contract table driven

    Apply/
      INetworkApplyEngine.cs
      NetworkApplyOrchestrator.cs            # elevation, settle, verify, state (from OptimizerService apply)
      NetworkRepairOrchestrator.cs
      NetworkBenchmarkRunner.cs
      NetworkStateStore.cs                   # network-optimizer.json, paths

    Scripting/                               # string builders only
      NetworkScriptPaths.cs                  # LocalAppData/TEMP constants
      NetworkRegistryCatalog.cs              # RegistryTargets MUST equal writes
      NetworkSafetyScript.cs                 # CommonSafetyFunctions, Report, probes
      ExoInternetSnapshotScript.cs               # Save-ExoNetworkSnapshot
      NetworkHostStackScript.cs              # registry, mmcss, qos, powercfg, tcp, dns
      NetworkAdapterScript.cs                # eth/wifi advanced + RSS + buffers
      NetworkBindingMetricScript.cs          # bindings, metrics, prefix, prefer-eth
      NetworkPostApplyScript.cs              # probe window + full rollback
      NetworkRepairScript.cs                 # true restore + stock fallback
      NetworkBenchmarkScript.cs
      NetworkApplyScriptComposer.cs          # Build(preset, options, media) assembles parts

    Ui/
      InternetOptimizerViewModel.cs
      (page stays Views/InternetOptimizerPage.*)

  # Standalone rescue: generate from same RepairScript module in CI
  Repair-Internet.ps1                        # GENERATED or thin wrapper calling shared fragment
```

### 7.2 File responsibilities (checklist)

| File | Responsibility | Must not |
|------|----------------|----------|
| `ExoInternetPresetKnobs.cs` | Single source for latency/throughput/balanced knobs | Touch disk |
| `NetworkPathPolicy.cs` | Eth-first metrics-only policy | Disable Wi‑Fi |
| `NetworkRegistryCatalog.cs` | Every reg write listed once; snapshot consumes same list | Drift from host stack writer |
| `ExoInternetFeatureRowBuilder.cs` | One table: detect field ↔ apply step ↔ OK rule | Invent rows without apply |
| `NetworkPostApplyScript.cs` | 60s probe + full rollback only | Partial Wi‑Fi-only rollback |
| `NetworkRepairScript.cs` | Shared by in-app + Rescue | Auto Hard |
| `NetworkStateStore.cs` | All JSON paths/schema | Silent swallow without tests |
| `NetworkApplyOrchestrator.cs` | Process elevation + honesty messages | Hardcode contradictory policy strings |

### 7.3 Contract table artifact (NEW)

`docs/rewrite/internet-contract.md` or embedded `NetworkApplyContract.cs`:

```
StepId | Mutation | Snapshot field | Detect row | EXO_REPORT | FailClosed
```

Smoke asserts every Apply step appears in catalog.

### 7.4 Shared rescue strategy

- **Preferred:** CI emits `Repair-Internet.ps1` from `NetworkRepairScript` + bootstrap header.  
- **Acceptable interim:** `#region SYNC` markers + smoke diff against `BuildRepair()` body.

---

## 8. Test matrix

### 8.1 Existing automation (keep / expand)

| Suite | What it proves |
|-------|----------------|
| `tools/Internet.Smoke` pure logic | Band scores, wifi class, path, knobs, match rules |
| Script audit | Required markers, forbidden folklore, preset divergence |
| Ordering | Snapshot before mutate; abort; rollback after body |
| Parse | PS AST zero errors for apply L/T, repair, bench |
| SnapshotExecHarness + Mocks | Mixed-type registry snapshot + repair type restore |
| `tools/Ui.Smoke` | Internet page/VM wiring existence |
| `tools/NetScriptDump` | Human/CI dump of generated PS |

### 8.2 Fixture matrix (offline, rebuild must add)

| Fixture ID | Hardware shape | Assert |
|------------|----------------|--------|
| F-ETH-INTEL-I226 | Intel 2.5G eth only | RSS base, vendor extras markers, metric path |
| F-ETH-REALTEK | Realtek GbE | Green Ethernet keywords |
| F-ETH-KILLER | Killer eth | Killer extras; service not killed |
| F-WIFI-AX211 | Intel Wi‑Fi 6E | Prefer 6GHz selection |
| F-WIFI-REALTEK | Realtek USB wifi misnamed Ethernet 2 | IsWifiAdapter true |
| F-DUAL | Eth+WiFi both up eth has IP | metrics-only; never Disable-NetAdapter |
| F-ETH-NOIP | Eth up APIPA/no IP + WiFi | KeepWifiBecauseEthNoIp |
| F-VPN-WG | WireGuard + eth | VPN excluded from physical set |
| F-HYPERV | vEthernet present | not wifi / excluded |
| F-BT-PAN | Bluetooth PAN | not wifi primary |
| F-LOCALE-DE | German DisplayNames | keyword path still applies FC/LSO |
| F-BUILD-OLD | build &lt; 26100 | uro skip reason |
| F-CORES-6/12 | 6 core 12 thread | RSS budget uses physical |
| F-LAPTOP | battery chassis | DC wireless max |
| F-SNAPSHOT-MIXED | mixed reg types | exec harness pass |
| F-ROLLBACK-MSG | forced post-probe fail | full restore markers |
| F-REPAIR-NOSNAP | no snapshot file | stock fallback markers |
| F-BALANCED | preset Balanced knobs | if Balanced apply added |

### 8.3 Real-machine cases (human / future E2E)

| Case | Steps | Pass criteria |
|------|-------|---------------|
| R1 Ethernet desktop | Apply latency → browse + game ping → unplug cable if WiFi present | Online after apply; WiFi works when cable out |
| R2 Ethernet no WiFi | Apply latency | Online; Repair restores |
| R3 Wi‑Fi only laptop | Apply latency + throughput | Online; band prefer applied if exposed |
| R4 Dual NIC | Apply; check metric 1 on primary | Secondary not preferred |
| R5 VPN on | Apply with WG connected | Still online; VPN stays up |
| R6 Enterprise DNS filter | Apply behind block of 1.1.1.1 | Document behavior; prefer no false rollback (future multi-anchor) |
| R7 Non-English Win11 | DE/JP | LSO/FC/RSC rows OK after apply |
| R8 Re-apply | Apply twice | Snapshot unchanged; no double-damage |
| R9 Rollback sim | Force bad advanced prop lab only | Auto full restore; banner |
| R10 Repair snapshot | Apply then Repair | Settings back; snapshot cleared |
| R11 Repair stock | Delete snapshot then Repair | Stock-like; online |
| R12 Rescue irm | Offline copy Repair-Internet.ps1 | Restores |
| R13 Hard | -Hard after intentional break | Reboot required; stack lives |
| R14 Reboot persistence | Apply latency, reboot | Preset rows still mostly OK; metric may need re-apply (document) |
| R15 UAC deny | Cancel elevation | No partial mutate (snapshot abort path N/A — process not started) |

### 8.4 Contract tests (must-add)

1. **Registry catalog completeness:** every `Set-Dword`/`Remove-Prop` path in host script ∈ catalog.  
2. **Feature row coverage report:** list apply steps without rows (allowlist).  
3. **Fail-closed static:** grep apply for Disable-NetAdapter, NoActiveProbe' 1, SpeedDuplex, ms_msclient disable.  
4. **Rescue drift:** `BuildRepair()` body hash vs `Repair-Internet.ps1` core.  
5. **Message honesty:** no “Wi‑Fi disabled” in user-visible strings.

---

## 9. Acceptance criteria for “Internet done”

Rebuild Phase 2 is **done** when all of the following are true:

### 9.1 Safety (non-negotiable)

- [ ] Apply **never** disables Wi‑Fi adapters (smoke + real dual-NIC).  
- [ ] Apply **never** disables Client/LLDP/Server bindings.  
- [ ] Apply **never** writes NCSI/proxy AutoDetect.  
- [ ] Apply **never** forces Speed & Duplex.  
- [ ] Snapshot failure ⇒ **zero** mutations.  
- [ ] Post-apply offline ⇒ **full** snapshot restore + honest UI.  
- [ ] Repair with snapshot restores connectivity on lab break scenarios.  
- [ ] Repair **does not** auto Hard; `-Hard` documented and exit 2.  
- [ ] Standalone rescue works without the app binary.

### 9.2 Correctness

- [ ] Latency vs throughput knob divergence audited (autotune, RSC, LSO, FC, IM, idle, ECN, pacing, HyStart, URO, RTO, nagle, buffers).  
- [ ] Detect rows for **contract-critical** knobs match apply (LSO, RSC, autotune, MMCSS, QoS, bindings, eth metric, NIC FC/IM/idle).  
- [ ] No stale user strings about Wi‑Fi disable or Client/LLDP off.  
- [ ] Golden-path doc matches code (roam, buffers, path policy).  
- [ ] Games MMCSS keys snapshotted **or** not written.  
- [ ] Registry catalog 1:1 with writes.

### 9.3 Structure

- [ ] `ExoInternetApplyScriptBuilder` split into named modules ≤ ~300–400 lines each.  
- [ ] Pure logic has no process/registry I/O.  
- [ ] Repair-Internet generated or drift-gated.  
- [ ] Contract table checked into repo.

### 9.4 Test gates

- [ ] `Internet.Smoke` exit 0 on CI (Windows).  
- [ ] Fixture matrix F-* covered by unit/smoke.  
- [ ] Real-machine R1 + R3 + R10 signed off once per release train.  
- [ ] PS parse zero errors for all generated scripts.

### 9.5 UX

- [ ] Dual CTAs remain: Low latency | Highest download.  
- [ ] Repair caption reflects snapshot vs stock.  
- [ ] Rollback banner + apply report visible when relevant.  
- [ ] Benchmark optional but non-blocking.  
- [ ] Busy state disables double-apply.

### 9.6 Explicit non-goals (still)

- Cloud AI knobs, router control, public DNS force, IPv6 disable, folklore RWIN, band-only force.

---

## 10. Open risks

| # | Risk | Severity | Mitigation in rebuild |
|---|------|----------|------------------------|
| 1 | God-script drift (`Build` vs `BuildRepair` vs `Repair-Internet.ps1`) | High | Single repair module + generate rescue |
| 2 | Snapshot incomplete (Games MMCSS, SMBv1, tunnels) | Medium | Catalog-driven snapshot; extend v2 schema |
| 3 | False offline rollback (filtered networks) | High | Multi-anchor probe (gateway + NCSI URL + configurable); longer DNS-only acceptance policy |
| 4 | DisplayName Wi‑Fi knobs on non-English | Medium | Expand keyword map; soft-skip without claiming applied |
| 5 | Apply exit 0 after rollback | Low/Med | Non-zero exit on rollback **or** always require apply-state (document) |
| 6 | Metric lost after reboot | Medium | Detect row already warns; optional logon re-stamp out of scope unless requested |
| 7 | Dual-homed post-probe via wrong interface | Medium | Prefer bind probe to primary eth IP when ethInUse |
| 8 | Enterprise GPO fights settings | Low | Honesty in UI (“policy override”) |
| 9 | Killer/OEM utilities overwrite advanced props | Medium | Detect mismatch after settle; don’t kill vendor services |
| 10 | Elevated PS blocked by WDAC | High rare | Future: native C# netsh/CIM apply path |
| 11 | Congestion/band rows always green | Low | Real verify or remove |
| 12 | Balanced preset dead code | Low | Remove or add third CTA |
| 13 | Benchmark pings blocked = empty proof | Low | Soft “could not measure” |
| 14 | Large snapshot on many virtual adapters | Low | Physical-only already |
| 15 | Doc/code contradiction confuses agents | Medium | Golden-path regen from contract table |
| 16 | Full rollback may bounce NIC mid-game | Accepted | Prefer not apply during match; UI busy already long |
| 17 | `ipconfig /renew` disruptive | Low | Only on rollback/repair |
| 18 | No distributed lock on state files | Low | Single-instance app assumption |

---

## 11. Key source map (absolute paths)

| Path | Role |
|------|------|
| `C:\Users\Erix\Exo\Exo\Services\ExoInternetLogic.cs` | Pure decisions + audit + parsers |
| `C:\Users\Erix\Exo\Exo\Services\ExoInternetApplyScriptBuilder.cs` | Apply/repair/benchmark PS generation |
| `C:\Users\Erix\Exo\Exo\Services\ExoInternetOptimizerService.cs` | Probe, elevate apply/repair, state, feature rows |
| `C:\Users\Erix\Exo\Exo\Models\ExoInternetSnapshot.cs` | DTOs / presets / options |
| `C:\Users\Erix\Exo\Exo\ViewModels\InternetOptimizerViewModel.cs` | UI state + fail-closed options |
| `C:\Users\Erix\Exo\Exo\Views\InternetOptimizerPage.xaml` | Layout + CTAs |
| `C:\Users\Erix\Exo\Exo\Views\InternetOptimizerPage.xaml.cs` | Code-behind |
| `C:\Users\Erix\Exo\Repair-Internet.ps1` | Standalone rescue |
| `C:\Users\Erix\Exo\docs\INTERNET-GOLDEN-PATH.md` | Product contract |
| `C:\Users\Erix\Exo\docs\REWRITE-PROGRAM.md` | Phase 2 plan |
| `C:\Users\Erix\Exo\tools\Internet.Smoke\Program.cs` | Primary smoke |
| `C:\Users\Erix\Exo\tools\Internet.Smoke\SnapshotExecHarness.ps1` | Exec regression |
| `C:\Users\Erix\Exo\tools\Internet.Smoke\SnapshotExecMocks.ps1` | Mocks |
| `C:\Users\Erix\Exo\tools\NetScriptDump\` | Script dump utility |

### State / log paths (runtime)

| Path | Purpose |
|------|---------|
| `%LocalAppData%\Exo\network-snapshot.json` | Pristine baseline |
| `%LocalAppData%\Exo\network-apply-state.json` | Rollback honesty |
| `%LocalAppData%\Exo\network-optimizer.json` | Preset, report, benchmark |
| `%TEMP%\exo-net-last.log` | Apply EXO_REPORT |
| `%TEMP%\exo-net-repair-last.log` | Repair log |

---

## 12. EXO_REPORT step catalog (apply)

Expected steps (smoke-enforced presence):

`snapshot`, `registry-host`, `dns-priorities`, `mmcss`, `qos-psched`, `powercfg`, `tcp-globals`, `tcp-timestamps`, `tcp-fastopen`, `tcp-fastopen-fallback` (via helper), `tcp-pacing`, `tcp-hystart`, `udp-uro`, `tcp-ecn`, `dynamic-ports`, `tcp-settings`, `nagle`, `adapters`, `rss-base`, `bindings`, `background-quiet`, `eth-metrics`, `prefix-policy`, `wifi-disable` (always skip metrics-only), `post-probe`, `rollback` (conditional), `apply`.

---

## 13. Rebuild sequencing recommendation (multi-week)

Fits `REWRITE-PROGRAM` Phase 2 (4–7 days core) stretched for multi-week FULL rebuild:

| Week | Focus |
|------|-------|
| W1 | Contract table + Registry catalog completeness + fix stale strings/docs + Games MMCSS snapshot gap |
| W2 | Split Scripting modules; keep smoke green; generate Repair-Internet |
| W3 | FeatureRowBuilder coverage; detect verify for band/prefix optional rows; remove always-true lies |
| W4 | Fixture pack F-*; probe multi-anchor design; real-machine R1/R3/R10 |
| W5 | Polish UX proof layer; performance (probe PS less often); freeze golden-path from contract |

---

## 14. Appendix — preset knob cheat sheet

| Knob | LowestLatency | HighestThroughput |
|------|---------------|-------------------|
| Autotune | normal | experimental |
| RSC | disabled | enabled |
| LSO | 0 | 1 |
| Interrupt moderation | 0 | 1 |
| Flow control | 0 | 3 |
| Idle restriction | 1 | 0 |
| Nagle keys | set | remove |
| ECN | disabled | enabled |
| Pacing off | yes | no |
| HyStart off | yes | no |
| URO off (24H2+) | yes | no |
| Tight RTO | yes | no |
| Buffers | mid | max |
| RSS queue budget | ≤ cores, latency-shaped | = physical cores |
| Prefer IPv4 prefix | yes | if ethernetInUse |
| Wi‑Fi throughput booster | off | on |
| Wi‑Fi roam | low→med | medium |

---

*End of research inventory. Product code untouched. Next implementation step: Phase 2 PR-2.1 module split behind green `Internet.Smoke`.*
