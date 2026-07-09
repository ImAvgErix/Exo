# OptiHub NVIDIA profiles

Performance-focused **Base Profile** packs for GeForce 10/20/30/40/50 series.

| File | Use when |
|------|----------|
| `XX Series.nip` | Max FPS / lowest latency (Ultra Low Latency Ultra, G-SYNC off) |
| `XX Series G-SYNC.nip` | Adaptive sync monitors (G-SYNC on, ULL off to avoid conflict) |

## Series-specific

- **10**: no Resizable BAR / modern DLSS FG; RT off by default  
- **20**: rBAR on; DLSS overrides; no Frame Gen  
- **30**: rBAR full; DLSS quality presets; no FG  
- **40 / 50**: rBAR + DLSS + Frame Gen / RR overrides  

## Apply

Via OptiHub NVIDIA Optimizer (uses [nvidiaProfileInspector](https://github.com/Orbmu2k/nvidiaProfileInspector) `-silentImport`).

Profile pack version: see `PROFILE_VERSION`.
