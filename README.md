# OptiHub

**Maximum performance. No compromise.**

Performance-first Windows optimizer hub for latency, FPS, privacy, and lower background overhead. **Discord**, **Steam**, and **NVIDIA** are live; Brave, Riot, and Epic are on the way.

| Optimizer | Status |
|---|---|
| **Discord** | Live |
| **Steam** | Live |
| **NVIDIA** | Live (series profiles + verified driver/display preferences) |
| Brave · Riot · Epic | Coming soon |

---

## Performance policy

OptiHub is intentionally opinionated. Its profiles may disable or deprioritize visual effects, automatic startup, overlays, telemetry, vendor extras, power-saving behavior, and other convenience defaults when they compete with responsiveness.

This is not a generic “safe defaults” utility. Before an aggressive run, the app explains the tradeoffs and asks for confirmation. Detection, verification, and logs remain part of the workflow. Discord and Steam Repair restore their OptiHub-managed changes. NVIDIA Reset only clears OptiHub's status record; undo NVIDIA changes manually in NVIDIA settings or with a driver reinstall.

---

## Get OptiHub

**[Download OptiHub.exe](https://github.com/BarcusEric/OptiHub/releases/latest/download/OptiHub.exe)** and double-click.

Installs to `%LocalAppData%\OptiHub\app`. Windows 10 1809+ / Windows 11, 64-bit.

If SmartScreen appears: **More info** → **Run anyway**.

---

## What Discord optimize does (universal)

On **any** machine with normal Discord installed (or none — it can install Discord):

1. Equicord client mods + **full plugin registry** from shipped manifests  
2. OptiHub’s curated enable set (privacy, performance, AMOLED, no forced streamer mode)  
3. OpenASAR (faster startup)  
4. DiscOpt helper (5-second working-set trim, Above Normal priority, thread/raw-input tuning)
5. Complete client debloat + Windows startup, toast, and tray suppression

Everyone gets the same optimization baseline — not only people who already use Equicord.

The full applied state is reported only after the exact Discord build, kernel,
debloat, launcher, startup, notification, scheduled-task, and tray checks pass.
Repair restores only the captured stable-client Windows values and task states;
Canary, PTB, Store, and unrelated tasks are never matched by name.

---

## What Steam optimize does

- Applies aggressive client, CEF, overlay, download, and launcher preferences without modifying installed games.
- Purges disposable client caches and orphaned shader caches while preserving active downloads and installed-game shader caches.
- Reclaims `steamwebhelper` working sets every 5 seconds, runs the client High when idle, and drops it Below Normal while a game is active.
- Aborts maintenance when a game is active and preserves existing launch arguments in shortcuts.
- Records recovery before mutation, preserves the original startup values across
  reapplies, and clears recovery only after Repair verifies every restoration.
- Reports the full pack as applied only when the 1.5.0 record and live startup,
  launcher, helper, download, client-tweak, and shader-inventory checks agree.

---

## What NVIDIA optimize does

- Imports the profile that matches the detected GPU series.
- Applies supported RGB/full-range, color-depth, refresh-rate, scaling, and optional G-SYNC preferences through NVIDIA APIs instead of UI automation.
- Applies verified driver/MSI performance tweaks and disables overlay, telemetry, updater, FrameView, and NVIDIA App/GFE auto-start/background paths.
- Verifies downloaded tools and driver packages before use.
- Keeps NVIDIA App/GeForce Experience files and HDMI/DisplayPort audio intact while suppressing their performance-costing background behavior.
- Reports partial or restart-required results instead of marking an incomplete run as successful.
- Does not provide automatic rollback. **Reset status** clears only OptiHub's saved state; restore NVIDIA settings manually or reinstall the driver.

---

## Repair Discord

In OptiHub: Discord → **Repair Discord**

Or:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
```

---

## Build from source

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
cd C:\path\to\OptiHub
.\Run-OptiHub.ps1
.\Release-OptiHub.ps1
```

| Tool | Purpose |
|---|---|
| `tools/Bump-Version.ps1` | Bump app + kit versions |
| `tools/Test-Repository.ps1` | Run fast script, manifest, version, and profile checks |
| `tools/OptiHubSfx.cs` | Self-extracting installer source |

Release history is preserved by default. `Release-OptiHub.ps1` only replaces an
existing tag with `-ReplaceExisting` and only removes older releases with the
explicit `-PruneOldReleases` switch.

---

## Logs

`%LocalAppData%\OptiHub\logs` — also **Settings → Open logs folder**.

---

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

Optimizers make aggressive changes to application files, launch behavior, driver profiles, display preferences, and Windows settings. Read each confirmation and use at your own risk. Discord and Steam include Repair for OptiHub-managed changes; NVIDIA recovery is manual because Reset only clears OptiHub status.
