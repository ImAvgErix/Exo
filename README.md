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
| Safety | Confirmations · dry-run · optional restore points · Discord repair |

This repository was formerly **DiscOpti** (Discord-only PowerShell kit). The Discord kit still lives under `OptiHub/Scripts/Discord/` (kit **v1.1.5**). The GitHub remote may still be named `DiscOpti` until renamed.

---

## Get OptiHub (recommended)

### One-line install (easiest)

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-OptiHub.ps1" | iex
```

Downloads the latest Release zip into `%LocalAppData%\OptiHub\app` and launches `OptiHub.exe`. Best for most people.

### Manual zip

From [Releases](https://github.com/BarcusEric/DiscOpti/releases):

1. Grab `OptiHub-*-win-x64.zip`
2. Extract anywhere
3. Run `OptiHub.exe`

Use the zip when you want a portable folder you control. No separate .NET install required (self-contained). Windows 10 1809+ / Windows 11, 64-bit.

---

## Repair Discord

If Discord will not open after an optimize run:

- In OptiHub: open **Discord Optimizer** → **Repair Discord**
- Or one-liner (interactive clean reset, keeps login by default):

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex
```

Full logout reset (also clears session):

```powershell
$env:OPTIHUB_REPAIR_FULL = '1'
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Repair-Discord.ps1" | iex
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
cd C:\path\to\DiscOpti
.\Run-OptiHub.ps1
```

Skip rebuild: `.\Run-OptiHub.ps1 -NoBuild`

### Publish Release zip

```powershell
.\Publish-OptiHub.ps1
```

Output: `release\OptiHub-<version>-win-x64.zip` (self-contained `OptiHub.exe` + dependencies).

---

## Using Discord Optimizer

1. Open OptiHub → **Discord Optimizer**
2. Choose options (dry-run, restore point, quick reapply)
3. Click **Run** / **Reapply** (UAC when applying)
4. If Discord will not open afterward, use **Repair Discord** in the app or the repair one-liner above

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
├── Install-OptiHub.ps1
├── Install-DiscOptimizer.ps1   ← legacy redirect → Install-OptiHub
├── Repair-Discord.ps1          ← OptiHub repair one-liner
├── VERSION                     ← app version
└── OptiHub/
    ├── OptiHub.csproj
    ├── Views / ViewModels / Services / Models / Helpers / Styles / Assets
    └── Scripts/
        ├── Discord/            ← kit + OptiHub wrappers
        └── Placeholders/       ← Brave, Steam, Riot, Epic stubs
```

---

## Migrating from DiscOpti

If you previously used:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/DiscOpti/main/Install-DiscOptimizer.ps1" | iex
```

that script now redirects to **Install-OptiHub.ps1**. Prefer the OptiHub app for Discord runs going forward.

---

## Disclaimer

Optimizers modify application files and Windows settings. Use at your own risk. Keep backups. Third-party Discord clients (e.g. Equicord) may conflict with Discord’s Terms of Service — you are responsible for compliance. OptiHub authors are not liable for data loss or account issues.

---

## Credits

- Discord kit lineage: **DiscOpti** / Disc Optimizer
- **OptiHub** — WinUI 3 shell, safety UX, script orchestration, multi-optimizer hub
