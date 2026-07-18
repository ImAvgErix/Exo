# Exo security model

Exo changes Windows and application settings, so optimizer execution is treated
as a privileged transaction rather than a normal UI action.

## Trust boundary

- The desktop process runs as the signed-in user (`asInvoker`).
- Apply and Repair request elevation per action through
  `PowerShellRunnerService`. Internet no longer owns a separate elevation path.
- Shipped optimizer files are length- and SHA-256-checked against the manifest
  compiled into the Exo executable before the UAC request.
- The elevated in-memory bootstrap hashes the target again before execution. A
  file changed between planning and elevation fails closed.
- App-generated elevated scripts are limited to Exo's named network transaction
  files in the current user's temporary directory and receive the same
  in-boundary re-hash.
- Elevation uses PowerShell 7 and an encoded in-memory bootstrap. Exo does not
  depend on VBScript or execute a user-writable wrapper script.
- A cancellation marker asks the elevated boundary to terminate its child and
  reports cancellation. If mutation already began, the user should run Repair.

UAC is not treated as a complete security boundary. A malicious process already
running as the same user can read or modify per-user files and interact with the
desktop. The double hash blocks modified optimizer bytes from being accepted by
the normal Exo flow; it does not replace code signing, OS integrity, or malware
protection.

## Process and startup hardening

- Exo removes the current working directory from native DLL search before WinUI
  starts, while preserving the Windows App SDK package graph required by the
  unpackaged app.
- Only one Exo UI process is allowed. Later launches redirect activation to the
  primary instance.
- Fatal startup diagnostics record the failing boot phase and runtime facts, but
  redact the user profile, AppData paths, and user name.
- Exo creates no background service, scheduled task, watchdog, startup entry, or
  resident tray helper.

## Downloads and updates

The in-app updater accepts HTTPS GitHub release assets, verifies the release
metadata size, SHA-256 digest when published, and embedded product version before
launching the installer. Missing independent digests and unsigned release assets
remain a supply-chain limitation until Exo releases are code-signed and publish a
separately trusted checksum. Do not describe the current release channel as
cryptographically authenticated end to end.

PowerShell's portable-runtime fallback selects stable official PowerShell GitHub
releases and verifies the asset size and GitHub digest when available. A future
release must make a missing digest a hard failure before this path is considered
fully fail-closed.

## Optimizer boundaries

Exo must never modify game executables, anti-cheat files/drivers/services, login
tokens, saves, store manifests, or player input. Riot Vanguard (`vgc`, `vgk`, and
all related files, services, drivers, and registry state) is explicitly outside
the mutation boundary. Epic Online Services binaries and service configuration
are also outside it.

Every newly shipped mutation requires live eligibility detection, a versioned
pre-state snapshot, post-apply verification, and an exact Repair operation. If
one of those pieces is missing, the action is not release-ready.

## Reporting a vulnerability

Open a private security advisory in the GitHub repository. Include the affected
version, exact reproduction steps, and whether the issue crosses from the normal
user context into an elevated action. Do not post working privilege-escalation or
token-exposure details in a public issue before a fix is available.
