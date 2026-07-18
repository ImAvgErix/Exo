# Exo NVIDIA profiles (v1.3.0)

Maximum-performance **Base Profile** packs for GeForce 10/20/30/40/50 series.
Applied with nvidiaProfileInspector `-silentImport` (no GUI).

On Apply, Exo clones the selected series pack into **per-game application
profiles** (Valorant, CS2, Marvel Rivals, R6, Fortnite, Apex, and more) so all
10 base packs (max FPS + G-SYNC × series) feed the same game catalog without
shipping 10×N separate NIP files.

Each game then gets **tier deltas** on top of that clone:

| Tier | Titles | Extra vs Base clone |
|------|--------|---------------------|
| **comp** | Val, CS2, Rivals, R6, Fortnite, Apex, LoL, OW2, RL, CoD, PUBG, Tarkov, Finals, Delta Force | Sticky latency stack (PRF=1, no driver FPS cap, no triple buffer, FXAA/AO/Ansel off) + re-pin max-FPS or G-SYNC pack policy + **Frame Gen override off** when the series pack has DLSS-FG |
| **hybrid** | Destiny 2 | Same sticky latency / pack pins; **leaves Frame Gen** as the series pack (more PvE-friendly) |

These packs intentionally favor FPS and latency over idle power and background
features. At combined-profile generation time Exo removes hidden global rBAR,
DLSS/Frame Generation, ray-tracing, CUDA-memory, and Vulkan-present overrides:
those are game/engine/driver-specific and forcing them globally can regress a
different title. Documented latency/performance controls remain pinned and Exo
validates them after import.

Apply state is fail-closed: Exo invalidates its previous success marker
before driver/profile work begins and ties a successful import to the active
driver version. An interrupted or failed import must be applied again.

| File | Use when |
|------|----------|
| `XX Series.nip` | Max FPS / lowest latency (**Ultra** Low Latency, G-SYNC off, VSync force off) |
| `XX Series G-SYNC.nip` | Adaptive sync monitors (G-SYNC + driver VSync on, Ultra Low Latency **Ultra**; Reflex takes priority automatically in supported games) |

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
- OS VRR override: **Off** (the app toggle off means every VRR path is off)

## G-SYNC pack only

- VSync / VRR: G-SYNC-friendly values
- G-SYNC global / application: **On**
- Ultra Low Latency: **Ultra** globally; Reflex takes priority automatically when a game enables it

## Series-specific

| Series | Extras |
|--------|--------|
| **10** | No rBAR; RT forced off |
| **GTX 16** | Uses the 10-series pack so unsupported RT/DLSS/rBAR flags are not imported |
| **20 / 30 / 40 / 50** | Series pack selection stays explicit; driver allowlists decide rBAR and each game decides DLSS/Frame Generation/RT |

Laptop/Notebook GPU names still select the matching profile series, but the
automatic clean-driver stage is intentionally blocked until Exo has an
official notebook-specific lookup. It never substitutes desktop driver
metadata or packages. Install the official NVIDIA notebook driver manually.

## Driver, apps, and display (not changed by Apply)

The shipping Apply path is deliberately limited to reversible DRS profile
imports. It does not reinstall or strip the NVIDIA driver, audio components,
NVIDIA App, overlays, services, tasks, refresh rate, color, scaling, or monitor
configuration. Hardware and display topology are detected only to select and
explain the matching profile. The app can open NVIDIA Control Panel; when it is
missing, Apply attempts to provision the official Store package.

This boundary is intentional: component removal, blanket service disabling,
undocumented MSI/affinity edits, and forced display modes do not have a
hardware-independent performance win across Windows 11 desktops and laptops.

Profile pack version: see `PROFILE_VERSION`.
