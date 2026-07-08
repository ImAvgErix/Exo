# Discord Optimizer Kit (OptiHub)

Bundled kit used by **OptiHub**: Discord stable, Equicord, OpenASAR, AMOLED theme, privacy plugins, safe cache clean, and DiscOpt kernel.

## Recommended: install OptiHub

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-OptiHub.ps1" | iex
```

That downloads the latest OptiHub release (`OptiHub.exe`) and launches the app. Discord Optimizer lives inside OptiHub.

## Repair Discord

From OptiHub: **Repair Discord**. Or one-liner:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex
```

Full logout reset (also clears login):

```powershell
$env:OPTIHUB_REPAIR_FULL = '1'
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex
```

## Run the kit script directly

```powershell
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1"          # full
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -Quick   # after Discord updates
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -FreshInstall
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -SkipCacheClean
```

## Defaults

- Equicord + OpenASAR installed automatically (verify-then-install).
- `amoled-cord.theme.css` (proven AMOLED theme).
- Midnight/black app profile, compact display intent, reduced motion.
- OpenASAR quickstart + DOM optimizer + no-track/no-typing flags.
- Safe cache cleanup while preserving login/session storage.
- Windows startup entries, scheduled tasks, toast notifications, and tray promotion disabled for Discord.
- Equicord privacy/minimalism plugins enabled; noisy notification/activity plugins forced off.

Export live plugin tweaks back into the kit:

```powershell
powershell -ExecutionPolicy Bypass -File "Export-Profile.ps1"
```

## Voice / noise suppression

- Krisp **module** stays installed (Discord needs it for the dropdown).
- **BlockKrisp** and **AltKrispSwitch** stay off (they break selecting None).
- Storage is patched each run so noise suppression defaults to **off**.

## `tools/`

| Item | Role |
|------|------|
| `openasar.asar` | Bundled OpenASAR bootstrap |
| `Install-*.ps1` | CDN module helpers |
| `discord-modules/` | Core modules only (6) for fast restore |

`DiscordSetup.exe` and `EquilotlCli.exe` are not committed to git; the script downloads what it needs on demand.
