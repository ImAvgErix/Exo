# OptiHub

**OptiHub** is an all-in-one Windows optimizer hub — performance, privacy, and gaming-focused tweaks.

| Optimizer | Status |
|---|---|
| **Discord** | Live |
| Brave · Steam · Riot · Epic | Coming soon |

| | |
|---|---|
| Stack | C# · WinUI 3 · Windows App SDK · .NET 8 |
| Theme | AMOLED black · clean light · white accents |
| Kernel | DiscOpt memory trim + raw input + priority |
| Install | Single **OptiHub.exe** double-click |

---

## Get OptiHub

**Download and double-click:**

**[OptiHub.exe](https://github.com/BarcusEric/OptiHub/releases/latest/download/OptiHub.exe)**

Installs into `%LocalAppData%\OptiHub\app` and launches OptiHub.

Windows 10 1809+ / Windows 11, 64-bit.

If **SmartScreen** appears: **More info** → **Run anyway**.

What’s new: [Releases](https://github.com/BarcusEric/OptiHub/releases)

---

## Repair Discord

If Discord will not open after an optimize run:

- In OptiHub: open Discord → **Repair Discord**
- Or download a fresh Discord from [discord.com/download](https://discord.com/download)

---

## Build from source

| Requirement | Notes |
|---|---|
| Windows 10 1809+ / Windows 11 | 64-bit |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Required to build |

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
cd C:\path\to\OptiHub
.\Run-OptiHub.ps1
```

Publish a release (creates **OptiHub.exe** only):

```powershell
.\Release-OptiHub.ps1
```

---

## Using Discord

1. Open OptiHub → **Discord**
2. Click **Run** / **Reapply** (confirms first; UAC when applying)
3. If Discord will not open, use **Repair Discord**

Settings: `%LocalAppData%\OptiHub\settings.json`

---

## Disclaimer

Optimizers modify application files and Windows settings. Use at your own risk. Keep backups. You are responsible for compliance with any third-party terms. OptiHub authors are not liable for data loss or account issues.
