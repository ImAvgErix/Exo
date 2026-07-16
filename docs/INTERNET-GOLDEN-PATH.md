**Status: LIVE** — Ethernet Properties bindings + deep NIC/Wi‑Fi advanced knobs. Prefer real driver props over folklore.

# Internet optimizer — applied path

**Version:** ships with current Exo app release  
**Goal:** lowest latency / best gaming **and** complete Ethernet + Wi‑Fi coverage (bindings, advanced props, host stack) — with a snapshot/rollback safety net that makes "reinstall Windows to get internet back" impossible.

## Safety contract (non-negotiable, gated by smoke)

1. **Pre-apply snapshot** — before the first mutation the apply script captures the original state to `%LocalAppData%\Exo\network-snapshot.json` (`snapshotVersion` + timestamp): every registry value it writes (or `absent`), netsh tcp/udp globals, `Get-NetTCPSetting` fields, per-adapter advanced properties by `RegistryKeyword`, bindings, interface metrics (+ `AutomaticMetric`), adapter enabled state, RSS config, powercfg values, dynamic ports, IPv6 prefix policies, `DoSvc` start type. An existing snapshot is **never overwritten** (first snapshot = pristine baseline). If snapshot capture fails, the apply **aborts** with no mutation.
2. **Verified Ethernet gate** — Ethernet-first is metrics-only (Wi‑Fi adapters are never disabled); a real TCP‑443 probe (`1.1.1.1` / `8.8.8.8`, ~3 s timeout) **bound to the Ethernet adapter's IPv4 endpoint** succeeds. Wi‑Fi‑only machines no-op. Disabled adapters are recorded in `network-apply-state.json`.
3. **Post-apply check + auto-rollback** — apply ends with an any-interface connectivity probe inside a full retry window (link renegotiation). On failure it performs a **full snapshot restore** (registry, NIC advanced props + adapter restart, bindings, TCP globals, metrics), re-enables every disabled physical adapter, force-enables `ms_tcpip`/`ms_tcpip6`/`ms_pacer`, renews DHCP, re-probes, and writes `rollback:true` + reason into `network-apply-state.json`. (Older Wi‑Fi/metrics-only rollback left host-stack tweaks applied and could strand users.)
4. **True restore** — `BuildRepair` restores exact snapshot values (registry restored or removed when `absent`, advanced props by `RegistryKeyword` **then Restart-NetAdapter** so they take effect, bindings, metrics, adapter enable, netsh/TCP, RSS, powercfg, ports, prefix policies, services). Snapshot + state are deleted only on full success. Without a snapshot it falls back to the approximate stock reset. Every disabled physical adapter is re-enabled. If still offline, Repair exits failed and tells you to run `Repair-Internet.ps1 -Hard` explicitly (winsock/ip reset is never automatic).
5. **Standalone rescue** — `Repair-Internet.ps1` at repo root (self-elevating, `irm | iex`-runnable, `-Hard` for forced winsock/IP reset) does the same restore/stock-reset + re-enable without the app. Header includes an OFFLINE emergency paste block when download is impossible.
6. **Proof + honesty** — every major step emits `EXO_REPORT:<step>|ok|fail:<reason>|skip:<reason>`; a non-elevated ping/DNS benchmark runs before/after apply and the delta is persisted in state.

## Detection (local facts only)

| Signal | Source | Use |
|--------|--------|-----|
| Wi‑Fi vs Ethernet | `PhysicalMediaType` / `Native 802.11` / `802.3`, name/desc fallback | Branch NIC settings |
| Ethernet **usable** | Status Up **and** IPv4 not `169.254.*` | Prefer Ethernet 100% + metric 1 |
| Ethernet metric | `Get-NetIPInterface` IPv4 | UI / verify (want 1 on primary) |
| Client 6 GHz | `netsh wlan show drivers` + Preferred Band valid values (**re-probed at apply**) | Prefer 6 GHz |
| Client 5 GHz | drivers + Preferred Band values | Prefer 5 GHz if no 6 |
| Connected band | `netsh wlan show interfaces`: Band / Radio type / Channel | UI hint only |
| Current Preferred Band | Driver `DisplayValue` (fuzzy name match) | UI verify |
| NIC status | Flow Control, Interrupt Moderation, IdleRestriction, SelectiveSuspend (if exposed) | UI status |
| Dual NIC | All physical adapters | Tune both; policy on usable Ethernet |

**Not used:** cloud AI, router admin APIs, ISP APIs.

## Host stack (both media)

| Setting | Latency preset | Throughput preset | Why |
|---------|----------------|-------------------|-----|
| DisableTaskOffload | 0 | 0 | `1` is a real footgun |
| autotuninglevel | **normal** | **experimental** | MS-supported |
| heuristics | disabled | disabled | Stops autotune clamp |
| RSS (global) | enabled | enabled | Documented |
| RSC (global + NIC) | **disabled** | **enabled** | Latency vs CPU tradeoff |
| Congestion | CUBIC | CUBIC | Modern default path |
| Nagle (per-IF) | ACK/NoDelay on | keys removed | TCP gaming latency |
| SystemResponsiveness | **10** | **10** | MS: &lt;10 clamps to 20 |
| NetworkThrottlingIndex | **10** (force overwrite) | **10** | Default; ffffffff can raise DPC |
| QoS NonBestEffortLimit | 0 | 0 | Real reserve removal |
| Games MMCSS | High / GPU 8 | same | Real when apps use MMCSS |
| Dynamic ports | netsh full range | same | Modern API |
| powercfg wireless | Max Performance | Max Performance | Stops radio power save |
| powercfg PCIe ASPM | Off | Off | Link state power stalls |
| powercfg USB sel-suspend (AC) | Off | Off | USB NIC dongles |
| TCP timestamps | disabled | disabled | 12-byte header saving, documented netsh |
| TCP Fast Open (+fallback) | enabled | enabled | RFC 7413, Win10+ |
| pacingprofile | **off** | untouched | Removes send pacing delay (latency) |
| HyStart | **disabled** | untouched | Avoids slow-start ramp stalls (latency) |
| UDP URO | **disabled** (24H2+ only, else skip-with-reason) | untouched | Coalescing adds latency; build-gated ≥ 26100 |
| ECN capability | **disabled** | **enabled** | AQM marks help throughput, spare marks hurt latency |
| InitialRtoMs / MinRtoMs | **1000 / 300** | untouched | Faster retransmit on loss (latency) |
| MaxSynRetransmissions | 2 | 2 | Faster connect failure |
| NonSackRttResiliency | Disabled | Disabled | Default-off resiliency stays off |
| DNS ServiceProvider priorities | 4/5/6/7 | same | Documented Local/Hosts/Dns/Netbt order |
| RSS BaseProcessorNumber | **2** on Ethernet when ≥ 4 logical CPUs | same | Keeps NIC interrupts off core 0 |
| IPv4 fast path | prefixpolicy `::ffff:0:0/96` 55 4 | same | Documented precedence, replaces old "IPv6 metric = IPv4+20" hack |
| Delivery Optimization | DODownloadMode 0 + DoSvc Manual | same | Background download quiet |
| BITS throttle policy | remove `EnableBITSMaxBandwidth` if present | same | No hidden bandwidth cap |

## Ethernet NIC (when not Wi‑Fi)

| Setting | Latency | Throughput |
|---------|---------|------------|
| Checksum offload Rx+Tx | On | On |
| LSO v2 | **Off** | **On** |
| Interrupt moderation | **Off** | Adaptive/Medium |
| Flow Control | **Off** | Rx & Tx |
| RSS + queues + profile | On (if supported) | On |
| DMA coalescing / Adaptive IFS | Off | Off |
| Jumbo Packet | Disabled / 1514 | same |
| Priority & VLAN | Packet Priority on when exposed | same |
| Speed & Duplex | Auto Negotiation | same |
| EEE / green / selective suspend | Off | Off |
| IdleRestriction (Intel) | **On** (block low-power idle) | Off |
| ARP/NS offload / Wake | Off | Off |
| Ring buffers | Max if exposed | Max |
| PnP power-off device | Disabled (24) | Disabled |
| Interface metric | **1** primary usable; others 5+ | same |
| Adapter bindings (Properties UI) | QoS+IPv4+IPv6 **on** only (Client/LLDP left alone — never disable) | same |
| Restart after apply | **Off by default** (user-confirmed Ethernet restart only) | same |

## Wi‑Fi NIC

| Setting | Latency | Throughput |
|---------|---------|------------|
| Checksum / LSO / RSC | Best-effort if exposed | same |
| Interrupt moderation | Off | Adaptive |
| Flow Control | Off | On |
| RSS | **Skip** (often unsupported) | Skip |
| Power-save / MIMO PS / uAPSD / coalescing / ULP / WoWLAN | **Off** | Off |
| Bluetooth collab / fat channel intolerant | Off | Off |
| Transmit power | Highest | Highest |
| Channel width | Auto / best | Auto / best |
| Wireless mode | Latest (be/ax/ac…) | same |
| MU-MIMO / OFDMA / Beamforming | On when exposed | On |
| Throughput Booster | Off | On when exposed |
| Preferred Band | Prefer **6 GHz** if client supports, else **Prefer 5 GHz** | same |
| Roaming aggressiveness | Medium when exposed | Medium |
| Force band-only | **Never** | Never |
| Restart | **Never** | Never |

## Path policy (gaming)

1. If **any Ethernet is Up + real IPv4** → metric 1 on fastest usable eth; optionally raise Wi‑Fi interface metrics (prefer Ethernet). **Never `Disable-NetAdapter` on Wi‑Fi** (stranded users when cable/DHCP later failed).  
2. Else if Wi‑Fi only → keep Wi‑Fi, apply band prefer from **live** client capability.  
3. Cable linked without IP → keep Wi‑Fi; do not force Ethernet-only path.
4. End of apply → any-interface probe; on failure **full snapshot restore** (registry, NIC props, bindings, TCP, metrics) and mark `rollback:true`.

## Adapter targeting (locale + hardware safe)

- Advanced properties are written by **`RegistryKeyword`** (`*FlowControl`, `*InterruptModeration`, `*LsoV2IPv4/6`, `*RscIPv4/6`, `*EEE`, `*JumboPacket`, `*ReceiveBuffers`, `*TransmitBuffers`, `*RSS`, `*NumRssQueues`, `*PriorityVLANTag`, `*WakeOnMagicPacket`, `*WakeOnPattern`, `*SpeedDuplex`, …); English `DisplayName` fuzzy match remains only for vendor-specific knobs with no standardized keyword.
- VPN/virtual adapters are excluded via `Get-NetAdapter` `Virtual`/`HardwareInterface` + interface description, not name heuristics alone.
- Every netsh option that may not exist on a given build is try/catch-wrapped and reported as `skip:<reason>` — never silent.

## Apply diagnostics

- Log: `%TEMP%\exo-net-last.log`
- Structured report: `EXO_REPORT:<step>|ok` / `|fail:<reason>` / `|skip:<reason>` lines, parsed into `network-optimizer.json`
- Snapshot: `%LocalAppData%\Exo\network-snapshot.json` (pristine baseline, never overwritten)
- Apply outcome: `%LocalAppData%\Exo\network-apply-state.json` (rollback marker, disabled Wi‑Fi list, post-apply connectivity)
- Benchmark before/after: persisted in `network-optimizer.json` (`benchmark.before` / `benchmark.after`)

## Explicit non-goals (do not re-add)

- XP/server registry folklore (MaxUserPort, chimney, LargeSystemCache, …)  
- SystemResponsiveness 0 / ThrottlingIndex ffffffff  
- Logon/tray scheduled tasks  
- Cloud “AI” picking knobs  
- Force MTU / jumbo frames for gaming  
- Force 5/6 GHz **only** modes  
- Force public DNS (user choice)  
- Disable IPv6  

## Implementation core

- `NetworkLogic` — pure band/path/knob decisions + report/benchmark parsing
- `NetworkApplyScriptBuilder` — elevated apply/repair scripts + non-elevated benchmark (snapshot, probe gate, rollback baked in)
- `NetworkOptimizerService` — apply/repair orchestration, benchmark before/after, report + rollback surfacing
- `Repair-Internet.ps1` — standalone self-elevating rescue (snapshot restore or stock reset)
- `tools/Network.Smoke` — gates band/media/script audit, ordering (snapshot → probe → disable → rollback), tweak markers, and PS parse of every generated script
- `tools/NetScriptDump` — dumps generated scripts for Windows CI E2E execution

## Change rule

Prefer expanding **documented / driver-exposed** knobs for Ethernet + Wi‑Fi. Still reject XP folklore, forced public DNS, forced MTU/jumbo for gaming, and forced band-only.
