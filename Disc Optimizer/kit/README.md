# Disc Optimizer Kit v1.1

One kit: Discord stable, Equicord, OpenASAR, local AMOLED theme, privacy/minimalism plugins, safe cache clean, black shortcut icon, and DiscOpt kernel DLL/config files.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1"          # full
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -Quick   # after Discord updates
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -FreshInstall
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -SkipCacheClean
```

## v1.1 defaults

- Local `discopt-amoled-v1.1.theme.css` (no remote CSS import).
- Midnight/black app profile, compact display intent, reduced motion.
- OpenASAR quickstart + DOM optimizer + no-track/no-typing flags.
- Safe cache cleanup for cache/log/crash/GPU shader junk while preserving login/session storage.
- Windows startup entries, scheduled tasks, toast notifications, and tray promotion disabled for Discord.
- Equicord privacy/minimalism plugins enabled; noisy notification/activity plugins forced off.
- Black Start menu shortcut icon.

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
| `DiscordSetup.exe` | Install / update Discord x64 |
| `EquilotlCli.exe` | Equicord + OpenASAR |
| `openasar.asar` | Bundled OpenASAR bootstrap |
| `Install-*.ps1` | CDN module helpers |
| `discord-modules/` | Core modules only (6) for fast restore |

Release-only offline helpers such as `DiscordSetup.exe`, `EquilotlCli.exe`, and portable PowerShell ZIPs are not committed to git; they are included in release ZIPs when present locally.