# Disc Optimizer v1.1.0

Disc Optimizer v1.1 is a full speed/privacy/AMOLED pass over the original toolkit.

## Highlights

- New local **DiscOpt AMOLED v1.1** theme with zero remote CSS imports.
- Stronger Discord app profile: midnight/black intent, reduced motion, compact display, no verbose logging, high-performance GPU flags, and OpenASAR quickstart/DOM optimizer.
- Safe cache cleaner that removes Discord cache/log/crash/GPU shader junk without touching login/session storage.
- Quieter Windows posture: Discord startup entries, scheduled tasks, toast notifications, and tray promotion are disabled.
- Stronger Equicord defaults for privacy/minimalism: NoTrack, ClearURLs, AnonymiseFileNames, NoRPC, no notification plugins, no profile effects, no typing animation, no reply pings, no onboarding delay, and local theme only.
- Black shortcut icon for the Start menu launcher.
- More robust module extraction and failure logging.

## What it keeps

- Discord account/session storage.
- Krisp module files so the native noise suppression menu stays visible, while Krisp-blocking plugins remain disabled.
- Required Discord modules for stable boot and voice.
- Stock backups for Discord bootstrap/kernel files.

## Release package

Build from the repository root on Windows after placing release-only binaries in the kit:

```powershell
powershell -ExecutionPolicy Bypass -File ".\scripts\New-ReleaseZip.ps1"
```

Expected optional release-only files:

- `Disc Optimizer\kit\tools\DiscordSetup.exe`
- `Disc Optimizer\kit\tools\EquilotlCli.exe`
- `Disc Optimizer\kit\downloads\PowerShell-7.7.0-preview.2-win-x64.zip`

The generated asset is `dist\Disc-Optimizer-v1.1.0.zip`.
