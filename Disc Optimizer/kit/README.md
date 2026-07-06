# Disc Optimizer Kit

One kit: Discord stable, Equicord, OpenASAR, local AMOLED theme, privacy/minimalism plugins, safe cache clean, and DiscOpt kernel DLL/config files.

## One copy-paste install

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-DiscOptimizer.ps1" | iex
```

## Run directly

```powershell
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1"          # full
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -Quick   # after Discord updates
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -FreshInstall
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -SkipCacheClean
```

## Defaults

- Equicord + OpenASAR installed automatically (verify-then-install, no manual steps).
- Local `discopt-amoled-v1.1.theme.css` (no remote CSS import).
- Midnight/black app profile, compact display intent, reduced motion.
- OpenASAR quickstart + DOM optimizer + no-track/no-typing flags.
- Safe cache cleanup for cache/log/crash/GPU shader junk while preserving login/session storage.
- Windows startup entries, scheduled tasks, toast notifications, and tray promotion disabled for Discord.
- Equicord privacy/minimalism plugins enabled; noisy notification/activity plugins forced off.
- Discord icon left at default.

Export live plugin tweaks back into the kit:

```powershell
powershell -ExecutionPolicy Bypass -File "Export-Profile.ps1"
```

## Voice / noise suppression

- Krisp **module** stays installed (Discord needs it for the dropdown).
- **BlockKrisp** and **AltKrispSwitch** stay off (they break selecting None).
- Storage is patched each run so noise suppression defaults to **off**.
- Mic/deafen menus stay visible (`removeAudioMenus: false`).

## `tools/`

| Item | Role |
|------|------|
| `openasar.asar` | Bundled OpenASAR bootstrap |
| `Install-*.ps1` | CDN module helpers |
| `discord-modules/` | Core modules only (6) for fast restore |

`DiscordSetup.exe` and `EquilotlCli.exe` are not committed to git; the script downloads what it needs on demand.
