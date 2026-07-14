**Status: LIVE** — Ethernet Properties bindings + deep NIC/Wi‑Fi advanced knobs. Prefer real driver props over folklore.

# Internet optimizer — peak path

**Version:** ships with current Exo app release  
**Goal:** lowest latency / best gaming **and** complete Ethernet + Wi‑Fi coverage (bindings, advanced props, host stack).

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
| NIC peak | Flow Control, Interrupt Moderation, IdleRestriction, SelectiveSuspend (if exposed) | UI status |
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
| Adapter bindings (Properties UI) | QoS+IPv4+IPv6 on; Client/File share/LLDP/LLTD off | same |
| Restart after apply | When Ethernet present | same |

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

1. If **any Ethernet is Up + real IPv4** → metric 1 on fastest usable eth, **disable all Wi‑Fi adapters**.  
2. Else if Wi‑Fi only → keep Wi‑Fi, apply band prefer from **live** client capability.  
3. Cable linked without IP → **do not** disable Wi‑Fi.

## Apply diagnostics

- Log: `%TEMP%\exo-net-last.log`

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

- `NetworkPeakLogic` — pure band/path/knob decisions
- `NetworkApplyScriptBuilder` — elevated script (same knobs)
- `tools/NetworkPeak.Smoke` — gates band/media/script audit

## Change rule

Prefer expanding **documented / driver-exposed** knobs for Ethernet + Wi‑Fi. Still reject XP folklore, forced public DNS, forced MTU/jumbo for gaming, and forced band-only.
