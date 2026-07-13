**Status: FROZEN as of v1.9.30** — do not change Internet apply/detect behavior without a proven OS/driver regression.

# Internet optimizer — golden path (freeze target)

**Version:** 1.9.30+  
**Goal:** lowest latency / best gaming path. Change only if Windows/driver behavior breaks.

## Detection (local facts only)

| Signal | Source | Use |
|--------|--------|-----|
| Wi‑Fi vs Ethernet | `PhysicalMediaType` / `Native 802.11` / `802.3`, name/desc fallback | Branch NIC settings |
| Ethernet **usable** | Status Up **and** IPv4 not `169.254.*` | Prefer Ethernet 100% |
| Client 6 GHz | `netsh wlan show drivers` radio types (`802.11be`, 6 GHz, 6E) **or** Preferred Band valid values | Prefer 6 GHz |
| Client 5 GHz | drivers + Preferred Band values | Prefer 5 GHz if no 6 |
| Connected band | `netsh wlan show interfaces`: Band / Radio type / Channel | UI hint only |
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
| NetworkThrottlingIndex | **10** | **10** | Default; ffffffff can raise DPC |
| QoS NonBestEffortLimit | 0 | 0 | Real reserve removal |
| Games MMCSS | High / GPU 8 | same | Real when apps use MMCSS |
| Dynamic ports | netsh full range | same | Modern API |

## Ethernet NIC (when not Wi‑Fi)

| Setting | Latency | Throughput |
|---------|---------|------------|
| Checksum offload Rx+Tx | On | On |
| LSO v2 | **Off** | **On** |
| Interrupt moderation | **Off** | Adaptive/Medium |
| RSS + queues | On (if supported) | On |
| EEE / green / selective suspend | Off | Off |
| Ring buffers | Max if exposed | Max |
| PnP power-off device | Disabled (24) | Disabled |
| Interface metric | **1** if usable | **1** if usable |
| Restart after apply | **User prompt only** | User prompt only |

## Wi‑Fi NIC

| Setting | Latency | Throughput |
|---------|---------|------------|
| Checksum / LSO / RSC | Best-effort if exposed | same |
| Interrupt moderation | Off | Adaptive |
| RSS | **Skip** (often unsupported) | Skip |
| Power-save / MIMO PS / uAPSD | **Off** | Off |
| Preferred Band | Prefer **6 GHz** if client supports, else **Prefer 5 GHz** | same |
| Force band-only | **Never** | Never |
| Restart | **Never** | Never |

## Path policy (gaming)

1. If **any Ethernet is Up + real IPv4** → set metric 1, **disable all Wi‑Fi adapters**.  
2. Else if Wi‑Fi only → keep Wi‑Fi, apply band prefer from client capability.  
3. Cable linked without IP → **do not** disable Wi‑Fi.

## Explicit non-goals (do not re-add)

- XP/server registry folklore (MaxUserPort, chimney, LargeSystemCache, …)  
- SystemResponsiveness 0 / ThrottlingIndex ffffffff  
- Logon/tray scheduled tasks  
- Cloud “AI” picking knobs  
- Force MTU / jumbo frames for gaming  
- Force 5/6 GHz **only** modes  

## Freeze rule

After v1.9.30 ships with this path: **no Internet behavior changes** unless a real OS/driver regression is proven.
