# Exo rebuild research library

This folder is the **deep inventory** behind the rebuild program. Do not delete; treat as source of truth for implementers.

| Document | Lines (approx) | Scope |
|----------|----------------|-------|
| [../REWRITE-PROGRAM.md](../REWRITE-PROGRAM.md) | Master plan (executive + schedule + PR DAG) | All modules |
| [research-internet.md](research-internet.md) | ~720 | Internet apply/detect/repair god-file split |
| [research-discord-steam.md](research-discord-steam.md) | ~620 | Discord kit + Steam god-script |
| [research-nvidia.md](research-nvidia.md) | ~580 | NVIDIA profiles, display, tray, Reset truth |
| [research-shell-ci.md](research-shell-ci.md) | ~610 | WinUI shell, services, CI/release gaps |

**How to use**

1. Read **REWRITE-PROGRAM.md** for sequence and ship rules.  
2. Open the matching research doc before touching a module.  
3. When inventory drifts from code, update research in the same PR as the behavior change.

**Generated:** 2026-07-16 via parallel explore agents against `main` @ v2.6.8.
