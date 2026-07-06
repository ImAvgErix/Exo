# Disc Optimizer

Discord optimization kit for Windows: stable Discord, Equicord/OpenASAR, AMOLED theme, privacy/performance plugin defaults, and the DiscOpt kernel files.

## Run on Windows

Extract the release ZIP, then right-click `Disc-Optimizer.ps1` and choose **Run with PowerShell**.

```powershell
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1"
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1" -Quick
powershell -ExecutionPolicy Bypass -File ".\Disc-Optimizer.ps1" -FreshInstall
```

Useful flags:

- `-Quick` reapplies settings after a Discord update.
- `-VerifyOnly` checks the local kit without changing Discord.
- `-NoLaunch` applies changes but does not restart Discord at the end.
- `-SkipKernel`, `-SkipOpenAsar`, `-SkipEquicord`, and `-SkipDebloat` disable individual steps.

## Notes

- The script must run on 64-bit Windows.
- Logs are written under `kit\logs`; check `last-error.log` if a run fails.
- Large release-only files such as `DiscordSetup.exe`, `EquilotlCli.exe`, and portable PowerShell caches are intentionally ignored in git. Put them under `kit\tools` / `kit\downloads` when building a fully offline release ZIP.

## Current defaults

- AMOLED theme enabled.
- Discord startup/toasts reduced.
- Hardware acceleration and high-performance GPU switch enabled.
- Analytics/noise/upsell clutter plugins tuned for performance and privacy.
- Krisp module kept installed so the native noise suppression menu stays available; Krisp-blocking plugins are forced off.
