# OptiHub

**OptiHub** is an all-in-one Windows optimizer hub - performance, privacy, and gaming-focused tweaks.

| Optimizer | Status |
|---|---|
| **Discord** | Live |
| Brave · Steam · Riot · Epic | Coming soon |

| | |
|---|---|
| Stack | C# · WinUI 3 · Windows App SDK · .NET 8 · single `OptiHub.exe` |
| Theme | AMOLED black · clean light · white accents |
| Safety | Always confirms · Repair Discord · no elevated Discord flash |
| Size | Slim kit - Discord via winget/CDN, Equicord downloaded on demand |
| Kernel | DiscOpt memory trim + raw input + priority (on by default) |

---

## Get OptiHub

Paste into PowerShell:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Install-OptiHub.ps1" | iex
```

Installs into `%LocalAppData%\OptiHub\app` and launches OptiHub. Windows 10 1809+ / Windows 11, 64-bit.

What's new: [Releases](https://github.com/BarcusEric/OptiHub/releases)

---

## Repair Discord

If Discord will not open after an optimize run:

- In OptiHub: open Discord → **Repair Discord**
- Or:

```powershell
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
```

Full logout reset:

```powershell
$env:OPTIHUB_REPAIR_FULL = '1'
irm "https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Repair-Discord.ps1" | iex
```

---

## Build from source

| Requirement | Notes |
|---|---|
| Windows 10 1809+ / Windows 11 | 64-bit |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Required to build |
| PowerShell 7+ | Preferred for `Run-OptiHub.ps1` |

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
cd C:\path\to\OptiHub
.\Run-OptiHub.ps1
```

Publish a release:

```powershell
.\Release-OptiHub.ps1
```

---

## Using Discord

1. Open OptiHub → **Discord**
2. Click **Run** / **Reapply** (confirms first; UAC when applying)
3. If Discord will not open, use **Repair Discord**

Scripts: `%LocalAppData%\OptiHub\scripts\Discord` · Settings: `%LocalAppData%\OptiHub\settings.json`

---

## Disclaimer

Optimizers modify application files and Windows settings. Use at your own risk. Keep backups. You are responsible for compliance with any third-party terms. OptiHub authors are not liable for data loss or account issues.
