# OptiHub

**OptiHub** is an all-in-one Windows optimizer hub — a cozy WinUI 3 app for performance, privacy, and gaming-focused tweaks.

| Optimizer | Status |
|---|---|
| **Discord Optimizer** | Live (Equicord, OpenASAR, DiscOpt kernel, AMOLED, privacy, debloat) |
| Brave · Steam · Riot · Epic | Coming soon |

| | |
|---|---|
| Stack | C# · WinUI 3 · Windows App SDK · .NET 8 · MVVM |
| Theme | AMOLED pure black dark · clean off-white light · **white** accents |
| Safety | Always confirms · restore point when possible · Discord repair |

Discord kit lives under `OptiHub/Scripts/Discord/` (kit **v1.1.5**).

---

## Get OptiHub

Paste into PowerShell:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Install-OptiHub.ps1" | iex
```

Installs into `%LocalAppData%\OptiHub\app` and launches `OptiHub.exe`. No separate .NET install. Windows 10 1809+ / Windows 11, 64-bit.

What’s new: [Releases](https://github.com/BarcusEric/OptiHub/releases)

---

## Repair Discord

If Discord will not open after an optimize run:

- In OptiHub: open **Discord Optimizer** → **Repair Discord**
- Or one-liner (interactive clean reset, keeps login by default):

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
```

Full logout reset (also clears session):

```powershell
$env:OPTIHUB_REPAIR_FULL = '1'
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
```

---

## Build from source

### Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 1809+ / Windows 11 | 64-bit |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Required to build |
| PowerShell 7+ | Preferred for `Run-OptiHub.ps1` |

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

### Run (Debug)

```powershell
cd C:\path\to\OptiHub
.\Run-OptiHub.ps1
```

Skip rebuild: `.\Run-OptiHub.ps1 -NoBuild`

### Publish a release (maintainers)

```powershell
.\Release-OptiHub.ps1
```

Builds the app, keeps a single GitHub Release (deletes older ones), and writes release notes with the PowerShell install paste. Users install via that paste — not by downloading files from the release page.

---

## Using Discord Optimizer

1. Open OptiHub → **Discord Optimizer**
2. Click **Run** / **Reapply** (confirms first; UAC when applying; restore point when Windows allows)
3. If Discord will not open afterward, use **Repair Discord** in the app or the repair one-liner above

Scripts are copied to `%LocalAppData%\OptiHub\scripts\Discord` on first launch. Settings live in `%LocalAppData%\OptiHub\settings.json`.

### What Discord Optimizer does

- Installs/repairs Discord stable when needed; Equicord + OpenASAR automatically
- AMOLED theme, privacy plugins, cache/debloat, Windows startup/toast quieting
- DiscOpt kernel (`ffmpeg.dll` / `version.dll` / `config.ini`) for priority, memory trim, raw input
- Boot safety: verifies Discord starts; rolls back if needed

---

## Project layout

```
├── OptiHub.sln
├── Run-OptiHub.ps1
├── Publish-OptiHub.ps1
├── Release-OptiHub.ps1
├── Install-OptiHub.ps1
├── Install-DiscOptimizer.ps1   ← legacy redirect → Install-OptiHub
├── Repair-Discord.ps1
├── VERSION
└── OptiHub/
```

---

## Disclaimer

Optimizers modify application files and Windows settings. Use at your own risk. Keep backups. Third-party Discord clients (e.g. Equicord) may conflict with Discord’s Terms of Service — you are responsible for compliance. OptiHub authors are not liable for data loss or account issues.
