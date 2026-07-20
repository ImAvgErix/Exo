# Exo

**Competitive Windows optimization — measured, reversible, product-grade.**

Exo is a private WinUI 3 app that tunes the host stack that actually affects gaming and voice: network path, GPU driver policy, Discord, Steam, Riot, Epic, and Windows itself. Every change is detected on the live PC, applied through a controlled pipeline, and restorable with Repair.

<p align="center">
  <img src="docs/media/home.png" alt="Exo home — live hardware and network overview" width="920" />
</p>

---

## Why Exo

Most “FPS packs” dump folklore registry keys and hope for the best. Exo is built around a different contract:

| Principle | What it means |
|---|---|
| **Detect first** | Live registry, power plan, netsh, and app paths — not a marketing checklist |
| **Apply with a real pipeline** | Native C# for host/launcher modules; specialized kits for Discord & NVIDIA |
| **Never hang** | Hard timeouts on DISM, scheduled tasks, and elevated work |
| **Repair always** | Pre-Exo snapshots so you can undo |
| **Anti-cheat safe** | No Vanguard, EOS, or game-binary mutation |

---

## Modules

| Module | What Apply does |
|---|---|
| **Windows** | Full host stack in native C#: Game Mode, HAGS, MMCSS (`SystemResponsiveness=10`), competitive power plan, input, Defender policy, WU pause, task quiet, optional features (timeout-safe DISM), shell declutter |
| **Internet** | Lowest-latency or high-throughput profile from measured quality; TCP/QoS/NIC knobs only — no folklore DNS/MTU packs |
| **NVIDIA** | Verified DRS / profile path with G-SYNC vs raw-latency choice; display policy without unsigned driver hacks |
| **Discord** | Lean client path, voice QoS, Windows quiet (toasts/startup/tray) — in-app audio prefs left alone on Stable |
| **Steam** | Quiet CEF launcher, memory-guard yield, library GPU/FSO policy, download/config hygiene |
| **Riot / Epic** | Startup quiet, high-perf GPU preference, FSO off, DSCP 46, silent yield companion — **never** anti-cheat or game files |

Stable Apply is the safe reversible stack. Experimental only forces tighter re-imports or deeper loops where the module defines them.

---

## Install

**Requirements:** Windows 11 x64

1. Download **`Exo.exe`** from the [latest release](https://github.com/ImAvgErix/Exo/releases/latest)
2. Double-click to install (self-contained — **no separate .NET install**)
3. Launch from Start Menu or `%LocalAppData%\Exo\app\Exo.exe`

PowerShell bootstrap (verifies published SHA-256):

```powershell
irm https://raw.githubusercontent.com/ImAvgErix/Exo/main/Install-Exo.ps1 | iex
```

In-app updates: **Settings → Check for updates** (progress in the panel, quiet reinstall, restart when ready).

Public builds are not code-signed; SmartScreen may appear. Prefer official GitHub releases only.

---

## Product surface

- Fixed dark **liquid-glass** shell (WinUI 3 + React/WebView2)
- Home: live CPU / GPU / RAM / network (link rate, idle & load latency, loss, DNS, rating)
- Module pages: feature list with live detect, **Apply / Reapply**, **Repair**
- Settings overlay from the chrome (no separate account or cloud)

---

## Safety model

- Apply scripts are integrity-checked (length + SHA-256) across elevation
- Mutations are snapshotted for Repair where the module owns recovery
- Riot Vanguard, Epic Online Services, game binaries, saves, and logins are **out of bounds**
- Exo does **not** install services, tray stay-resident agents, analytics, or ads
- Exo reports observed policy and metrics; it does not promise universal FPS or ping

See [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

---

## Build (developers)

```powershell
# UI (when changing React shell)
cd ui; npm ci; npm run build; cd ..

dotnet build Exo.sln -c Release -p:Platform=x64
pwsh -File tools/Test-Repository.ps1
pwsh -File Publish-Exo.ps1
```

Requirements: Windows 11, .NET 10 SDK, Windows App SDK / WinUI 3 tooling, PowerShell 7, Node.js for UI rebuilds.

---

## Documentation

| Doc | Purpose |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | Release notes |
| [docs/TWEAK-AUDIT.md](docs/TWEAK-AUDIT.md) | Evidence-based tweak keep/drop list |
| [docs/INTERNET-GOLDEN-PATH.md](docs/INTERNET-GOLDEN-PATH.md) | Network stack contract |
| [CONTRIBUTING.md](CONTRIBUTING.md) | How to contribute |

---

## License

[MIT](LICENSE) · Maintained as a focused gaming host optimizer, not a generic “tweaker pack.”
