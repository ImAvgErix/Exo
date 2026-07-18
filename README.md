# Exo

Private, reversible Windows 11 optimization for Internet, NVIDIA, Discord,
Steam, Riot, and Epic.

![Exo dashboard](docs/exo-shell.png)

Exo detects the current PC before it changes anything. Each module has one
primary Apply action, a detector that explains the active policy, and a Repair
path that restores the captured pre-Exo state. The app is dark-only, local-first,
and leaves no Exo service, startup entry, scheduled task, tray process, account,
analytics, or advertising behind.

## What it optimizes

| Module | Policy |
|---|---|
| Internet | Measures the current route, link, latency, loss, load response, and DNS candidates; preserves multi-gig throughput and applies only supported Windows/NIC controls. |
| NVIDIA | Detects GPU series and display topology, applies verified global/per-game DRS profiles, and exposes G-SYNC as an explicit choice. |
| Discord | Applies a lean client/privacy profile, voice QoS, quiet Windows integration, dark styling, and a verified update-sensitive background policy. |
| Steam | Tunes supported client settings and lets background CEF helpers yield while gaming without killing, suspending, or purging them. |
| Riot | Applies reversible Windows startup/GPU/CPU policy to detected Riot games without touching Vanguard or game files. |
| Epic | Applies reversible Windows startup/GPU/CPU policy to executables found through Epic manifests without touching EOS, manifests, saves, or game files. |

Exo deliberately excludes folklore registry packs, forced MTU/jumbo frames,
anti-cheat changes, unsigned driver edits, destructive RAM purges, and global
settings whose correct value depends on the game. See
[the tweak audit](docs/TWEAK-AUDIT.md) for the full decision record.

## Install

Download `Exo.exe` from the [latest release](https://github.com/ImAvgErix/Exo/releases/latest)
and double-click it. The release is self-contained for Windows 11 x64.

PowerShell bootstrap alternative:

```powershell
irm https://raw.githubusercontent.com/ImAvgErix/Exo/main/Install-Exo.ps1 | iex
```

The bootstrap requires GitHub's SHA-256 digest and checks the embedded version
before it launches the installer. Current public builds are not code-signed, so
Windows may show SmartScreen. Do not download Exo from third-party mirrors.

## Safety model

- Apply/Repair scripts are length- and SHA-256-verified before and after UAC.
- Each mutation is capability-gated, snapshotted, verified, and repairable.
- Riot Vanguard, Epic Online Services, game binaries, saves, logins, and
  anti-cheat state are outside the mutation boundary.
- Exo reports observed metrics and policy state; it does not promise universal
  FPS, ping, RAM, or throughput gains.

Read [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md) before using the
aggressive Discord client modification or system-wide network policy.

## Build and test

Requirements: Windows 11, Visual Studio Build Tools/SDK support for Windows App
SDK, PowerShell 7, and .NET 10 SDK.

```powershell
dotnet build Exo.sln -c Release -p:Platform=x64
pwsh -File tools/Test-Repository.ps1
pwsh -File Publish-Exo.ps1
```

CI runs the UI, Network, Discord, Steam, NVIDIA, Riot/Epic, and contract smoke
suites. NVIDIA hardware/display behavior still requires a real NVIDIA Windows
machine because GitHub-hosted runners have no GPU.

## Contributing

Issues and focused pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md).
Exo is available under the [MIT License](LICENSE).
