# OptiHub NVIDIA profiles (v1.1.0)

Performance-focused **Base Profile** packs for GeForce 10/20/30/40/50 series.
Applied with nvidiaProfileInspector `-silentImport` (no GUI).

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
| **20** | rBAR on; DLSS DLL override + preset |
| **30** | rBAR full; DLSS + DLSS-RR overrides |
| **40 / 50** | rBAR + DLSS + Frame Gen + RR overrides |

## Display (not in .nip)

Desktop **color / scaling** are applied via **NVIDIA Control Panel** (not the NVIDIA App):
GPU scaling + No scaling + Override, color source NVIDIA.

Profile pack version: see `PROFILE_VERSION`.
