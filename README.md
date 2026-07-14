# OptiHub

**Peak performance optimizers for Windows — one hub, no folklore.**

[![Release](https://img.shields.io/github/v/release/UhhErix/OptiHub?style=flat-square&label=latest)](https://github.com/UhhErix/OptiHub/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/UhhErix/OptiHub/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/UhhErix/OptiHub/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-teal.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0f766e?style=flat-square)](https://github.com/UhhErix/OptiHub/releases/latest)

OptiHub is a focused Windows performance hub. Each optimizer is **aggressive by design**, **deterministic**, and **honest about what it applied**. Live status checklists track real state — not marketing checkmarks.

---

## Download

Grab the latest **double-click installer** from [Releases](https://github.com/UhhErix/OptiHub/releases/latest):

| Asset | What it is |
|-------|------------|
| `OptiHub.exe` | Self-extracting installer (recommended) |

**Requirements:** Windows 10/11 **x64**, admin elevation when applying optimizers, NVIDIA GPU for the NVIDIA path.

One-liner (PowerShell):

```powershell
irm https://raw.githubusercontent.com/UhhErix/OptiHub/main/Install-OptiHub.ps1 | iex
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
- **Repair where it matters** — Discord and Steam include Repair for OptiHub-managed changes
- **NVIDIA honesty** — Reset clears OptiHub status only; full driver recovery is manual (NVIDIA settings / reinstall)

---

## Install from source

```powershell
git clone https://github.com/UhhErix/OptiHub.git
cd OptiHub
.\Publish-OptiHub.ps1
# → release\OptiHub.exe
```

Build the WinUI app only:

```powershell
dotnet build OptiHub\OptiHub.csproj -c Release
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
OptiHub/                 WinUI 3 app + bundled scripts
  Scripts/Discord|Steam|Nvidia|…
  Services/              Peak logic, runners, panel
  Views/                 Dashboard, optimizers, NVIDIA Panel
  Styles/                Theme + button chrome
tools/                   Smoke projects + OptiHub.NvDisplay (NVAPI helper)
docs/                    Golden paths and audits
.github/                 CI, issue & PR templates
Publish-OptiHub.ps1      Single-file release build
Release-OptiHub.ps1      GitHub release (OptiHub.exe only)
```

---

## Safety & disclaimer

Optimizers change application files, launchers, driver profiles, display prefs, and Windows settings. Read each confirmation. **Use at your own risk.**

- Discord / Steam: use **Repair** to undo OptiHub-managed pieces
- NVIDIA: recovery is through NVIDIA tools or a clean driver install — OptiHub Reset is status-only

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).

---

## License

[MIT](LICENSE) — free to use, modify, and redistribute.

---

## Links

- **Releases:** https://github.com/UhhErix/OptiHub/releases
- **Issues:** https://github.com/UhhErix/OptiHub/issues
- **Changelog:** [CHANGELOG.md](CHANGELOG.md)
