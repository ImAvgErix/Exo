# Disc Optimizer

Disc Optimizer is a Windows Discord performance kit focused on gaming, OLED-black visuals, privacy, and low distraction. It does more than a normal file debloater: it installs/repairs Discord stable, installs Equicord + OpenASAR automatically, writes a tuned Discord profile, cleans safe caches, applies Windows startup/notification tweaks, and installs the DiscOpt DLL/config layer for priority, memory trim, and raw input support.

## Install & run — one copy-paste

Open PowerShell and paste this single line. It downloads the current source, installs/updates it under `Documents\Disc Optimizer`, and runs the optimizer. No release download, no extra steps.

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-DiscOptimizer.ps1" | iex
```

That is all you need. The script will:

1. Install or repair Discord stable if needed.
2. Verify Equicord + OpenASAR and install them automatically if missing.
3. Apply all performance / privacy / AMOLED / minimalism tweaks.
4. Restart Discord.

Reapply after a Discord update (optional):

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-DiscOptimizer.ps1" | iex
```

Running it again is always safe — it verifies what's already applied and only fixes what's missing.

## What it does

- **Speed / gaming**
  - Keeps only the Discord modules needed for stable boot, voice, and notifications.
  - Removes old `app-*` builds, unused locales, GPU/SwiftShader junk, crash/log/cache clutter, and stale package metadata.
  - Uses OpenASAR quickstart + DOM optimizer settings.
  - Enables high-performance GPU Chromium switches and disables unnecessary Chromium telemetry/background features.
  - Installs the DiscOpt kernel files (`ffmpeg.dll`, `version.dll`, `config.ini`) for memory trim, priority, thread priority, and raw input behavior.
- **Privacy**
  - Enables NoTrack, ClearURLs, AnonymiseFileNames, SilentTyping, StreamerModeOn, and NoRPC.
  - Disables native/Equicord notification plugins and tracking-heavy extras.
  - Disables Discord startup entries, scheduled tasks, Windows toast notifications, and tray promotion.
- **Minimalism / comfort**
  - Local zero-import `DiscOpt AMOLED` theme; no remote CSS fetch required.
  - Midnight/black app profile, compact display intent, reduced motion, no noisy notification volume, hidden Nitro/profile/quest clutter.
  - Keeps the native noise suppression dropdown working by preserving Krisp module files while forcing Krisp-blocking plugins off.
- **Ease of use**
  - One-line install; Equicord and OpenASAR are installed for you (verify-then-install, no manual steps).
  - Clear `kit\logs\last-error.log` failure logs instead of auto-closing with no explanation.

## What it does not touch

- Discord account/session storage is preserved.
- `Local Storage`, `IndexedDB`, cookies, and core session DBs are not cache-cleaned.
- Stock Discord bootstrap/kernel backups are kept when available.
- Discord's icon is left at default.

## How it compares to a basic debloater

Most debloaters only delete files. Disc Optimizer also:

- repairs/installs Discord stable when needed;
- restores bundled core modules for fast recovery;
- installs Equicord/OpenASAR automatically and applies a local AMOLED theme;
- writes Discord launch/profile flags;
- disables Windows startup/toast/task entries;
- keeps voice UI compatibility;
- adds DLL/config-based memory trim, priority, and raw input support.

## Advanced flags

If you run `Disc-Optimizer.ps1` directly, these switches are available:

- `-Quick` reapplies settings after a Discord update.
- `-VerifyOnly` checks the local kit without changing Discord.
- `-NoLaunch` applies changes but does not restart Discord at the end.
- `-SkipCacheClean` preserves all cache folders.
- `-SkipKernel`, `-SkipOpenAsar`, `-SkipEquicord`, and `-SkipDebloat` disable individual steps.

## Requirements

- 64-bit Windows.
- Internet connection (the script downloads Discord, Equicord, and OpenASAR as needed).
