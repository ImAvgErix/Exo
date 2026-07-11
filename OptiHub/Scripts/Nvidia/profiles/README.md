# OptiHub NVIDIA profiles (v1.3.0)

Maximum-performance **Base Profile** packs for GeForce 10/20/30/40/50 series.
Applied with nvidiaProfileInspector `-silentImport` (no GUI).

On Apply, OptiHub clones the selected series pack into **per-game application
profiles** (Valorant, CS2, Marvel Rivals, R6, Fortnite, Apex, and more) so all
10 base packs (max FPS + G-SYNC × series) feed the same game catalog without
shipping 10×N separate NIP files.

Each game then gets **tier deltas** on top of that clone:

| Tier | Titles | Extra vs Base clone |
|------|--------|---------------------|
| **comp** | Val, CS2, Rivals, R6, Fortnite, Apex, LoL, OW2, RL, CoD, PUBG, Tarkov, Finals, Delta Force | Sticky latency stack (PRF=1, no driver FPS cap, no triple buffer, FXAA/AO/Ansel off) + re-pin max-FPS or G-SYNC pack policy + **Frame Gen override off** when the series pack has DLSS-FG |
| **hybrid** | Destiny 2 | Same sticky latency / pack pins; **leaves Frame Gen** as the series pack (more PvE-friendly) |

These packs intentionally favor FPS and latency over idle power, background
features, and driver-default image-quality choices. OptiHub validates the
performance-critical settings and records the exact profile SHA-256 before
marking an import complete.

Apply state is fail-closed: OptiHub invalidates its previous success marker
before driver/profile work begins and ties a successful import to the active
driver version. An interrupted or failed import must be applied again.

| File | Use when |
|------|----------|
| `XX Series.nip` | Max FPS / lowest latency (**Ultra** Low Latency, G-SYNC off, VSync force off) |
| `XX Series G-SYNC.nip` | Adaptive sync monitors (G-SYNC on, Ultra Low Latency **off**) |

## Shared (all packs)

- Power management: **Prefer maximum performance**
- Threaded optimization: **On**
- Max pre-rendered frames / max frames allowed: **1**
- Preferred refresh rate: **Highest available**
- Texture filtering quality: **High performance**
- Trilinear optimization: **On**
- Anisotropic filter + sample optimization: **On**
- Negative LOD bias: **Clamp**
- Shader cache: **On**, size unlimited
- Ambient occlusion / FXAA / MFAA / Ansel / overlays: **Off**
- Triple buffering: **Off**
- CUDA force P2: **Off** (better for gaming clocks)

## Performance pack only

- Ultra Low Latency: **Ultra**
- VSync: **Force off**
- G-SYNC global: **Off**

## G-SYNC pack only

- Ultra Low Latency: **Off** (avoids conflict with adaptive sync)
- VSync / VRR: G-SYNC-friendly values
- G-SYNC global / application: **On**

## Series-specific

| Series | Extras |
|--------|--------|
| **10** | No rBAR; RT forced off |
| **GTX 16** | Uses the 10-series pack so unsupported RT/DLSS/rBAR flags are not imported |
| **20** | rBAR on; DLSS DLL override + preset |
| **30** | rBAR full; DLSS + DLSS-RR overrides |
| **40 / 50** | rBAR + DLSS + Frame Gen + RR overrides |

Laptop/Notebook GPU names still select the matching profile series, but the
automatic clean-driver stage is intentionally blocked until OptiHub has an
official notebook-specific lookup. It never substitutes desktop driver
metadata or packages. Install the official NVIDIA notebook driver manually.

## Display (not in .nip)

Desktop **color / scaling** are applied through **NVAPI** (not mouse/keyboard automation):
current resolution at its highest supported refresh rate, supported color depth,
Full RGB, GPU scaling + No scaling + Override.
Live status requires the bundled NVAPI helper and complete enumeration/mapping
of every active NVIDIA-connected display; unavailable or partial checks fail
closed. Overlay preferences are verified separately from service/task debloat.

Profile pack version: see `PROFILE_VERSION`.
