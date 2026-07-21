# Exo naming glossary

Canonical names on the **3.16.11 / 4.0.0 maximize-rehaul** base. Prefer the left column in new code.

## Modules (product ↔ code)

| Product | C# / UI | Notes |
|---------|---------|-------|
| **Internet** | `Network*` types, `AppServices.Network`, Internet UI pages | Product name stays Internet; code uses Network* (not ExoInternet*). |
| **Discord** | `Discord*` | Script was historically `Disc-Optimizer.ps1`. |
| **Steam / NVIDIA / Riot / Epic / Windows** | matching module prefixes | Unchanged product names. |
| **Home** | `HomePage`, `HomeViewModel` | Former `DashboardPage` / `DashboardViewModel`. `HomeDashboardReader` stays. |
| **AI / Maximize** | `Exo.Services.Ai.*`, `ai.*` RPCs | Living Grok agent + tool registry. |

## Internet (Network* code)

| Product concept | Code |
|-----------------|------|
| Internet optimizer service | `NetworkOptimizerService` |
| Presets / apply options / snapshot | `NetworkPreset`, `NetworkApplyOptions`, `NetworkSnapshot`, … |
| Apply script builder | `NetworkApplyScriptBuilder` |
| Composition root | `AppServices.Network` |
| Smoke | `tools/Network.Smoke` |

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

## AI / Maximize

| Surface | Name |
|---------|------|
| Agent | `ExoAIAgent` |
| Hands / tools | `ExoAiHands`, `ExoToolRegistry` |
| Safety | `ExoActionSafety` (denylist + conflict matrix) |
| Soft process policy | `ExoProcessSoftPolicy` |
| Bridge RPCs | `ai.getStatus`, `ai.run`, `ai.cancel` |
| Settings | `XaiApiKey`, `AiOptimalGateEnabled`, `UpscalerRiskAcknowledged` |
| Smoke | `tools/Ai.Smoke` |

Plan: `docs/EXO-MAXIMIZE-PLAN.md`.
