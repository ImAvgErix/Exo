# Exo

**Peak performance optimizers for Windows — one hub, no folklore.**

[![Release](https://img.shields.io/github/v/release/ImAvgErix/Exo?style=flat-square&label=latest)](https://github.com/ImAvgErix/Exo/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/ImAvgErix/Exo/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/ImAvgErix/Exo/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-teal.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0f766e?style=flat-square)](https://github.com/ImAvgErix/Exo/releases/latest)

Exo is a focused Windows performance hub. Each optimizer is **aggressive by design**, **deterministic**, and **honest about what it applied**. Live status checklists track real state — not marketing checkmarks.

---

## Download

Grab the latest **double-click installer** from [Releases](https://github.com/ImAvgErix/Exo/releases/latest):

| Asset | What it is |
|-------|------------|
| `Exo.exe` | Self-extracting installer (recommended) |

**Requirements:** Windows 10/11 **x64**, admin elevation when applying optimizers, NVIDIA GPU for the NVIDIA path.

One-liner (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ImAvgErix/Exo/main/Install-Exo.ps1 | iex
```

---

## What’s live

| Module | What it does |
|--------|----------------|
| **Internet** | Ethernet-first metrics, Wi‑Fi band prefer (6 → 5 GHz), NIC latency knobs, throttle index, power-plan network bits |
| **Discord** | DiscOpt kernel (RAM trim + priority), OpenAsar, Equicord theme, full client debloat, Windows quiet (tray/toasts/autostart) |
| **Steam** | High-priority CEF launcher, webhelper trim, client debloat, launch hygiene |
| **NVIDIA** | Series profile packs (Profile Inspector), Full RGB + peak refresh, GPU no-scaling, tray cleanup, **NVIDIA Panel** for live color depth |

**Coming soon:** AMD, Brave, Riot, Epic, Windows.

---

## Shell (2.2+)

- Fixed **1180×760** window (no maximize / resize)
- **Home** — centered tagline + logo cards (labels under marks)
- **Settings** — gear crank + dropdown flyout (Dark/Light, updates, logs, version)
- **Motion** — highlight rings / wash on hover (no blurry content scale); crisp logos at 2× decode

---

## NVIDIA Panel

Built-in Control Panel–style controls (no mouse automation of the Store app):

- **Color bit depth** — dropdown per display (8 / 10 / 12-bit); **Set** applies via NVAPI
- **Peak defaults** — Full RGB, primary max Hz / secondary 60 Hz, GPU no-scaling, video NVIDIA color/image, tray clean
- Optimizer **Apply** still forces peak defaults; the Panel is the manual override

---

## Philosophy

- **No folklore** — no invented registry keys, no “DNS AI”, no logon tray spam tasks
- **Detect what you applied** — pure classifiers (`*PeakLogic` / `*DetectCore`) shared by UI and smoke tests
- **Repair where it matters** — Discord and Steam include Repair for Exo-managed changes
- **NVIDIA honesty** — Reset clears Exo status only; full driver recovery is manual (NVIDIA settings / reinstall)

---

## Install from source

```powershell
git clone https://github.com/ImAvgErix/Exo.git
cd Exo
.\Publish-Exo.ps1
# → release\Exo.exe
```

Build the WinUI app only:

```powershell
dotnet build Exo\Exo.csproj -c Release
```

Smoke tests (shipped logic, no UAC):

```powershell
.\tools\Test-Repository.ps1
dotnet run --project tools\UiPeak.Smoke -c Release
dotnet run --project tools\NetworkPeak.Smoke -c Release
dotnet run --project tools\DiscordPeak.Smoke -c Release
dotnet run --project tools\SteamPeak.Smoke -c Release
dotnet run --project tools\NvidiaPeak.Smoke -c Release
```

---

## Project layout

```
Exo/                 WinUI 3 app + bundled scripts
  Scripts/Discord|Steam|Nvidia|…
  Services/              Peak logic, runners, panel
  Views/                 Dashboard, optimizers, NVIDIA Panel
  Styles/                Theme + button chrome
tools/                   Smoke projects + Exo.NvDisplay (NVAPI helper)
docs/                    Golden paths and audits
.github/                 CI, issue & PR templates
Publish-Exo.ps1      Single-file release build
Release-Exo.ps1      GitHub release (Exo.exe only)
```

---

## Safety & disclaimer

Optimizers change application files, launchers, driver profiles, display prefs, and Windows settings. Read each confirmation. **Use at your own risk.**

- Discord / Steam: use **Repair** to undo Exo-managed pieces
- NVIDIA: recovery is through NVIDIA tools or a clean driver install — Exo Reset is status-only

---

## Privacy

Exo itself ships **no telemetry, no analytics, no accounts**. Everything runs and stays local:

- Settings, logs, and optimizer state live in `%LocalAppData%\Exo` — nothing leaves your machine
- Network calls happen only when you ask: GitHub for app/script updates, and vendor downloads the optimizers need (OpenAsar, Equicord, NVIDIA Profile Inspector)
- The optimizers *remove* telemetry from their targets (Discord client tracking, NVIDIA telemetry services and tasks) — that's the point

---

## FAQ

**Windows SmartScreen blocks the installer.**
Exo is an unsigned open-source build, so SmartScreen hasn't "seen" it. Click **More info → Run anyway**. The updater verifies every download against the GitHub release SHA-256 before running it.

**Why does it ask for admin?**
Only when applying an optimizer — services, scheduled tasks, and driver profiles need elevation. Browsing the app doesn't.

**Where does it install?**
`%LocalAppData%\Exo\app`, per-user, no system folders touched. Uninstall = delete that folder and the Start Menu shortcut.

**How do updates work?**
The app checks GitHub Releases, downloads `Exo.exe`, verifies size + SHA-256 + version stamp, then stage-swaps itself and relaunches. Script kits update separately from the repo's `main`.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).

---

## License

[MIT](LICENSE) — free to use, modify, and redistribute.

---

## Links

- **Releases:** https://github.com/ImAvgErix/Exo/releases
- **Issues:** https://github.com/ImAvgErix/Exo/issues
- **Changelog:** [CHANGELOG.md](CHANGELOG.md)
