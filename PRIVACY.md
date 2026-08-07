# Exo privacy statement

**Exo is local-first. It has no account, no telemetry, and no analytics.**

## What stays on your machine

- All settings, snapshots, journals and logs live under
  `%LocalAppData%\Exo` (or `%ProgramData%\Exo` for machine-level state).
- Detection, apply, verify and repair read and write your machine directly.
  Nothing about those operations is transmitted anywhere.
- Your registry snapshots are plain JSON on disk and are never uploaded.

## What touches the network, and why

Exo makes only these outbound connections, and only when you start them:

| Purpose | Hosts | When |
|---------|-------|------|
| Update check | `api.github.com` | When you press **Check for updates** (or on launch, only if you opted into launch-time checking) |
| Update download | `github.com` release assets, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` | Only after you confirm **Install** |
| Driver downloads (NVIDIA, chipset) | the vendor's documented release URLs | Only after the three-step check → prepare → install flow |
| NVIDIA profile / Discord kit files | pinned release URLs | Only during Apply, and each file is SHA-256 verified against the shipped manifest before execution |

Every download is pinned to a digest. If a file's SHA-256 does not match the
release metadata, nothing is executed and the failure is reported.

## What Exo never does

- No account creation, sign-in or device registration.
- No telemetry, crash-report uploads, or usage analytics.
- No advertising SDKs.
- No collection of personal data, browsing history, or app usage.
- No sending of your logs, snapshots or settings anywhere.

## Third-party components

Exo invokes the vendor's own signed components (PowerShell from Microsoft,
NVAPI from NVIDIA) and verifies them by location, signature and reparse-point
integrity before use. Runtime materials fetched for a module are checked
against the shipped manifest before execution.

## Changes

This statement is part of the repository; any change ships with a release and
is visible in the release notes.
