# Exo

**Performance optimizers for Windows — one hub, no folklore.**

[![Release](https://img.shields.io/github/v/release/ImAvgErix/Exo?style=flat-square&label=latest)](https://github.com/ImAvgErix/Exo/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/ImAvgErix/Exo/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/ImAvgErix/Exo/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-teal.svg?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0f766e?style=flat-square)](https://github.com/ImAvgErix/Exo/releases/latest)

![Exo's AMOLED WinUI dashboard](docs/exo-shell.png)

Exo is a focused Windows performance hub. Each optimizer is **aggressive by design**, **deterministic**, and **honest about what it applied**. Live status checklists track real state — not marketing checkmarks. Internet shows **before/after ping, jitter, and DNS numbers** measured on your machine; NVIDIA reads the driver's actual profile database back and shows **"Verified in driver"** — or tells you it drifted.

---

## Download

Grab the latest **double-click installer** from [Releases](https://github.com/ImAvgErix/Exo/releases/latest):

| Asset | What it is |
|-------|------------|
| `Exo.exe` | Self-extracting installer (recommended) |

**Requirements:** Windows 10/11 **x64**, admin elevation only when applying optimizers, and an NVIDIA GPU for the NVIDIA path. If PowerShell 7 is missing, Exo prepares it only after you click **Apply** or **Repair** — never at startup.

One-liner (PowerShell):

```powershell
irm https://raw.githubusercontent.com/ImAvgErix/Exo/main/Install-Exo.ps1 | iex
```

---

## What’s live

| Module | What it does | Recovery |
|--------|--------------|----------|
| **Internet** | Ethernet-first metrics with connectivity-probe-gated Wi‑Fi disable, TCP fast path (initial RTO, MinRto, timestamps off, pacing off, TCP Fast Open, HyStart per preset), UDP URO off on Win11 24H2+, ECN per preset, DNS provider priorities pinned to Windows defaults (4/5/6/7 folklore removed), optional **Private DNS** toggle (Cloudflare + DNS-over-HTTPS, Win11 22H2+, off by default, snapshot-restorable), RSS CPU spread, deep adapter power kill, NIC latency knobs matched by `RegistryKeyword` (works on non-English Windows), throttle index, Delivery Optimization to Manual — with a **before/after ping / jitter / DNS benchmark** in the UI | Pre-apply snapshot to `%LocalAppData%\Exo\network-snapshot.json`, true snapshot-restore **Repair** (adapter restart so NIC props apply; hard winsock/IP reset if still dead), post-apply **full-snapshot auto-rollback** if connectivity breaks, standalone [`Repair-Internet.ps1`](Repair-Internet.ps1) rescue (`-Hard` / offline emergency block) |
| **Discord** | DiscOpt kernel (4s RAM trim, AboveNormal priority), Exo Host, Equicord + AMOLED theme, deep module/dictionary debloat, **DSCP 46 QoS for voice UDP** across Stable/PTB/Canary, Windows quiet (tray/toasts/autostart) | **Repair** restores stock, bootable Discord; [`Repair-Discord.ps1`](Repair-Discord.ps1) one-liner works even without Exo installed |
| **Steam** | High-priority CEF launcher with stable flags, webhelper companion (in-game CPU yield + startup quiet; working-set trims retired after they froze CEF), VDF key injection (target settings inserted even when modern Steam omits the keys), deep client quiet (library low-bandwidth/low-perf, community content off), multi-library support | **Repair** restores backed-up configs and the stock launch path |
| **NVIDIA** | Series profile packs imported via Profile Inspector and **verified live against the driver's own profile database** ("Verified in driver" vs "Drifted — re-apply"), expanded per-game catalog (Apex, OW2, Marvel Rivals, R6, PUBG, CoD, Rust, Tarkov, LoL, Dota 2, Rocket League, GTA V/FiveM), per-series DRS pins (Resizable BAR, present method, background FPS cap), deep driver component strip (ShadowPlay/NvBackend/telemetry), Full RGB + max refresh, GPU no-scaling, **NVIDIA Panel** with live color depth + digital vibrance | **Reset clears Exo status only** — driver recovery is manual (NVIDIA settings / clean driver reinstall) |

**Coming soon:** Epic, Riot, Brave, Windows.

---

## Shell (2.2+)

- Fixed **1180×760** window (no maximize / resize)
- **Home** — centered tagline + logo cards (labels under marks)
- **Settings** — gear crank + dropdown flyout (Dark/Light, updates, logs, version)
- **Motion** — highlight rings / wash on hover (no blurry content scale); crisp logos at 2× decode
- **Last-apply report** — every module keeps a step-by-step ok/fail/skip report of its last Apply

---

## NVIDIA Panel

Built-in Control Panel–style controls (no mouse automation of the Store app):

- **Color bit depth** — dropdown per display (8 / 10 / 12-bit); **Set** applies via NVAPI
- **Digital vibrance** — per-display slider, applied live
- **Applied defaults** — Full RGB, primary max Hz / secondary 60 Hz, GPU no-scaling, video NVIDIA color/image, tray clean
- Optimizer **Apply** still forces those defaults; the Panel is the manual override

---

## Philosophy

- **No folklore** — no invented registry keys, no “DNS AI”, no logon tray spam tasks
- **Detect what you applied** — pure classifiers (`*Logic` / `*DetectCore`) shared by UI and smoke tests
- **Verify, don't assume** — NVIDIA exports the driver's real profile database after import and pins are checked value-by-value; Internet benchmarks ping/jitter/DNS before and after
- **Repair where it matters** — Internet restores its pre-apply snapshot; Discord and Steam include Repair for Exo-managed changes
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
dotnet run --project tools\Ui.Smoke -c Release
dotnet run --project tools\Network.Smoke -c Release
dotnet run --project tools\Discord.Smoke -c Release
dotnet run --project tools\Steam.Smoke -c Release
dotnet run --project tools\Nvidia.Smoke -c Release
```

---

## Project layout

```
Exo/                 WinUI 3 app + bundled scripts
  Scripts/Discord|Steam|Nvidia|…
  Services/              Logic, runners, panel
  Views/                 Dashboard, optimizers, NVIDIA Panel
  Styles/                Theme + button chrome
tools/                   Smoke projects + Exo.NvDisplay (NVAPI helper)
docs/                    Golden paths and audits
.github/                 CI, issue & PR templates
Publish-Exo.ps1      Single-file release build
Release-Exo.ps1      GitHub release (Exo.exe only)
```

---

## Safety

Optimizers change application files, launchers, driver profiles, display prefs, and Windows settings — recovery for each module is listed in the table above.

---

## Privacy

Exo itself ships **no telemetry, no analytics, no accounts**. Everything runs and stays local:

- Settings, logs, optimizer state, and the network snapshot live in `%LocalAppData%\Exo` — nothing leaves your machine
- Network calls happen only when you explicitly check/apply, or when you opt into launch-time app update checks. Optimizer kits ship with the app; Exo never refreshes scripts or installs dependencies merely because it opened.
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
The app checks GitHub Releases, downloads `Exo.exe`, verifies size + SHA-256 + version stamp, then stage-swaps itself and relaunches. Every app release contains its matching optimizer kits. Missing PowerShell is prepared on demand after **Apply** or **Repair**, with progress shown in the UI.

**An optimizer broke something — how do I get back?**
Internet: Repair restores the exact pre-apply snapshot (and auto-rollback already fires if connectivity breaks right after apply); offline, run [`Repair-Internet.ps1`](Repair-Internet.ps1). Discord/Steam: use **Repair** in the app or [`Repair-Discord.ps1`](Repair-Discord.ps1). NVIDIA: Reset clears Exo status only — restore driver settings through NVIDIA tools or a clean driver install.

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
