# OptiHub

**One hub. Max performance.**

Windows optimizer hub for performance, privacy, and gaming. **Discord** and **Steam** are live; Brave, Riot, and Epic are on the way — one place for client tweaks without hunting scripts.

| Optimizer | Status |
|---|---|
| **Discord** | Live |
| **Steam** | Live |
| **NVIDIA** | Live (series profiles + App/debloat + display prefs) |
| Brave · Riot · Epic | Coming soon |

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
4. DiscOpt kernel (RAM trim, priority, raw input)  
5. Safe debloat + Windows quieting  

Everyone gets the same optimization baseline — not only people who already use Equicord.

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
| `tools/OptiHubSfx.cs` | Self-extracting installer source |

---

## Logs

`%LocalAppData%\OptiHub\logs` — also **Settings → Open logs folder**.

---

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

Optimizers modify application files and Windows settings. Use at your own risk.
