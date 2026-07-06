# Disc Optimizer

Disc Optimizer is a Windows Discord performance kit focused on gaming, OLED-black visuals, privacy, and low distraction. It does more than a normal file debloater: it installs/repairs Discord stable, applies Equicord + OpenASAR, writes a tuned Discord profile, cleans safe caches, applies Windows startup/notification tweaks, and installs the DiscOpt DLL/config layer for priority, memory trim, and raw input support.

Current version: **1.1.0**.

## What v1.1 does

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
  - Local zero-import `DiscOpt AMOLED v1.1` theme; no remote CSS fetch required.
  - Midnight/black app profile, compact display intent, reduced motion, no noisy notification volume, hidden Nitro/profile/quest clutter.
  - Black Start menu shortcut icon.
  - Keeps the native noise suppression dropdown working by preserving Krisp module files while forcing Krisp-blocking plugins off.
- **Ease of use**
  - Right-click-run PowerShell script with auto-elevation and portable PowerShell fallback.
  - Clear `kit\logs\last-error.log` failure logs instead of auto-closing with no explanation.
  - `-Quick` mode reapplies the profile after Discord updates.

## Download / run

From a release ZIP, extract the folder, then right-click `Disc-Optimizer.ps1` and choose **Run with PowerShell**.

```powershell
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1"
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1" -Quick
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1" -FreshInstall
```

Useful flags:

- `-Quick` reapplies settings after a Discord update.
- `-VerifyOnly` checks the local kit without changing Discord.
- `-NoLaunch` applies changes but does not restart Discord at the end.
- `-SkipCacheClean` preserves all cache folders.
- `-SkipKernel`, `-SkipOpenAsar`, `-SkipEquicord`, and `-SkipDebloat` disable individual steps.

## What it does not touch

- Discord account/session storage is preserved.
- `Local Storage`, `IndexedDB`, cookies, and core session DBs are not cache-cleaned.
- Stock Discord bootstrap/kernel backups are kept when available.

## How it compares to a basic debloater

Most debloaters only delete files. Disc Optimizer also:

- repairs/installs Discord stable when needed;
- restores bundled core modules for fast recovery;
- applies Equicord/OpenASAR settings and a local AMOLED theme;
- writes Discord launch/profile flags;
- disables Windows startup/toast/task entries;
- keeps voice UI compatibility;
- adds DLL/config-based memory trim, priority, and raw input support.

## Build the v1.1 release ZIP

Large redistributable binaries are intentionally not committed to git. For an offline-first release, place these files into the kit before packaging:

- `Disc Optimizer\kit\tools\DiscordSetup.exe`
- `Disc Optimizer\kit\tools\EquilotlCli.exe`
- `Disc Optimizer\kit\downloads\PowerShell-7.7.0-preview.2-win-x64.zip`

Then run:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\New-ReleaseZip.ps1"
```

The output is `dist\Disc-Optimizer-v1.1.0.zip`.
