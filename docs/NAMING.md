# Exo naming glossary

Canonical product names vs historical code names. Prefer the left column in new code.

## Modules (product ↔ code)

| Product | C# / UI | Notes |
|---------|---------|-------|
| **Internet** | `ExoInternet*` types, `AppServices.Internet`, `InternetOptimizerPage` | Former **Network** optimizer types (`NetworkOptimizerService`, `NetworkLogic`, …). |
| **Discord** | `Discord*` | Script was historically `Disc-Optimizer.ps1`. |
| **Steam / NVIDIA / Riot / Epic / Windows** | matching module prefixes | Unchanged product names. |
| **Home** | `HomePage`, `HomeViewModel` | Former `DashboardPage` / `DashboardViewModel`. `HomeDashboardReader` stays. |

## Internet (former Network)

| Old | New |
|-----|-----|
| `NetworkOptimizerService` | `ExoInternetOptimizerService` |
| `NetworkLogic` | `ExoInternetLogic` |
| `NetworkSnapshot` (+ related model types) | `ExoInternetSnapshot`, `ExoInternetPreset`, `ExoInternetMediaProfile`, `ExoInternetApplyOptions`, `ExoInternetBenchmarkResult`, … |
| `NetworkApplyScriptBuilder` (+ `.Repair` / `.Benchmark`) | `ExoInternetApplyScriptBuilder` |
| `AppServices.Network` | `AppServices.Internet` |
| `tools/Network.Smoke` | `tools/Internet.Smoke` |

**Kept for Repair / on-disk compatibility (do not rename):**

- `%LocalAppData%\Exo\network-snapshot.json` — existing installs depend on this path.
- PowerShell `Save-ExoNetworkSnapshot` — emitted by the apply builder; Repair and snapshot harnesses look for this name.
- Standalone scripts `Repair-Internet.ps1` / `Repair-Discord.ps1` — filenames unchanged.
- Windows `NetworkThrottlingIndex` and other OS/TCP “network” terms — not Exo type names.

## Discord script

| Old | New |
|-----|-----|
| `Exo/Scripts/Discord/Disc-Optimizer.ps1` | `Discord-Optimizer.ps1` |
| Kit comments / User-Agent `Disc-Optimizer/…` | `Discord-Optimizer/…` |

`Exo-Discord-Run.ps1` still wraps the optimizer; Repair remains `Exo-Discord-Repair.ps1` / `Repair-Discord.ps1`.

## Apply script properties

`ScriptBundleService` apply entrypoints use `*ApplyScript` (not `*OptimizerScript`):

- `DiscordApplyScript`, `SteamApplyScript`, `NvidiaApplyScript`, `RiotApplyScript`, `EpicApplyScript`, `WindowsApplyScript`

Detect/Repair properties stay `*DetectScript` / `*RepairScript`.

## Shell UI

| Old | New |
|-----|-----|
| `SharedModulePlate` | `ExoModulePlate` (control under `Views/Controls`) |
| `DashboardPage` / `DashboardViewModel` | `HomePage` / `HomeViewModel` |

`Style` resource `ExoModulePlate` (Border chrome in `ThemeResources.xaml`) predates the control rename and keeps that key.
