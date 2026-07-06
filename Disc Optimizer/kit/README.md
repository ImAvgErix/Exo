# Disc Optimizer

One kit: Discord stable, Equicord, OpenASAR, AMOLED theme, privacy plugins, DiscOpt kernel.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1"          # full
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -Quick   # after Discord updates
powershell -ExecutionPolicy Bypass -File "Disc-Optimizer.ps1" -FreshInstall
```

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