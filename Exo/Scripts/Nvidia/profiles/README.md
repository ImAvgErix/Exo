# Exo NVIDIA profiles (v1.3.0)

Maximum-performance **Base Profile** packs for GeForce 10/20/30/40/50 series.
Applied natively through NVAPI (Exo.NvDisplay `--drs-apply`) — the same signed
driver API Profile Inspector itself uses, with per-setting write + readback
verification. Profile Inspector is retained only as a fallback for driver
branches the native path has not yet been exercised on; it is removed once the
native path has proven itself across the supported driver/GPU spread.

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

## Native apply verification

`Exo.NvDisplay --drs-apply` parses the same `.nip` packs and writes every
setting through the driver's own DRS API, then opens a fresh session and reads
each setting back. Exit 0 = every applicable setting verified; 3 = partial
(real partial, reported as such); 1 = hard fail; 2 = bad pack. Settings the
installed driver does not support are counted as not applicable and excluded
from the verified total.

Recorded evidence (2026-08-05, RTX 3070, GeForce driver via NVAPI):
`30 Series G-SYNC.nip` — base=78 settings, 70 applicable written and verified
70/70, failed=0, unsupported-by-driver=8. `--drs-backup` / `--drs-restore`
round-tripped a 2.6 MB driver settings database byte-exact. `--drs-status`
reads the same values back live without writing.

Removal gate for the Profile Inspector fallback: the native path must have
applied and verified cleanly on the supported driver/GPU spread (multiple
driver branches, all five series) with no exit-3 partials that the fallback
had to cover.

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

## What Apply changes beyond the profile packs

This section used to say Apply changed none of the following. That described the retired
SafePolicy build; 4.4.0 turned the full pipeline back on, and this file shipped inside the app
still telling users otherwise. What a real Apply does:

- **Profiles** — written natively through NVAPI and verified in a fresh driver session.
  Profile Inspector is a fallback for a genuine write failure, not the primary path.
- **Display** — primary refresh set to the panel maximum (secondary left alone), Full RGB,
  scaling override, and NVIDIA video colour. The Control Panel button in Exo remains available
  for anything Exo does not set.
- **NVIDIA App / GeForce Experience** — removed via silent NVI2, with the classic Control Panel
  installed afterwards so the display and colour UI still exists.
- **Services and tasks** — the telemetry service (`FvSvc`) and the App self-update task are
  disabled. `NVDisplay.ContainerLocalSystem` is never touched.
- **Driver expert tweaks** — MSI High, HDCP off, PowerMizer, installer telemetry and
  advertising RIDs off, overlay and capture off.
- **GPU ceilings** — power and thermal limits raised to the board's own reported maximum.
- **Driver package** — only on a separate, explicit opt-in, with three consents and the
  component diff shown before the last one.

Hardware topology is detected only to select the matching profile. Laptop GPUs still select a
profile series, but the automatic clean-driver stage stays blocked (see above).

Everything here is snapshotted before it is changed: the complete pre-Exo DRS database, the GPU
power and thermal limits, and the display preferences. Reset restores them.

Profile pack version: see `PROFILE_VERSION`.
