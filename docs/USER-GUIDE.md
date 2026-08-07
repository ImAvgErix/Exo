# Exo — user guide

Exo is a private, reversible Windows optimization suite. Every change it makes
is detected, explained, snapshotted, applied, verified and reversible. If a
tweak cannot be verified it is reported honestly — "no measurable gain" is a
valid verdict.

## The screen

Each module (Discord, Internet, Steam, NVIDIA, Spotify, Brave, System) is a
card with a status tag:

| Tag | Meaning |
|-----|---------|
| **READY** | Detected, not yet applied |
| **APPLIED** / **VERIFIED** | Detected applied; VERIFIED means a live read-back confirmed it |
| **PARTIAL** | Some settings landed; some could not be written or verified |
| **NOT INSTALLED** | The target app or device is not on this machine |
| **BLOCKED** | A dependency (driver, app, kit) is missing or unsafe |

Open a module card to see each individual setting with its desired value and
the evidence for it. **Apply** runs the module's settings; **Repair** restores
available snapshots (the restore can be partial — Exo says so before you
confirm); **Verify** forces a fresh detection read.

## Applying changes

1. Open a module and read what it will change. Each setting states its value
   and why.
2. Press **Apply**. The first elevated module costs one UAC prompt; the rest
   of that session's elevated work stays in one batch.
3. **Stop** cancels a long run without corrupting the result report.

A snapshot is written before anything changes. **Repair** restores it.

## Updates

**Check for updates** only reads GitHub — it never downloads or installs.
When a newer release exists, a separate **Install** action appears that names
the version and release summary; nothing downloads until you confirm it.
Updates install to `%LocalAppData%\Exo\app`, keep the previous build as a
backup, and restore it automatically if the new build fails to start.

## Startup, services and storage

The Startup & services panel lists Run-key/Startup-folder entries and Windows
services. Toggling a startup entry moves the value to Exo's disabled backup
key — nothing is deleted, every change is snapshotted, and **Repair** restores
it. Storage maintenance scans known temp roots and journals every deletion.

## Privacy

Exo keeps everything local: no account, no telemetry, no network calls beyond
the update check and driver downloads you explicitly start. See
[PRIVACY.md](PRIVACY.md). The privacy levers in the System module (advertising
ID, telemetry, activity history, tailored experiences) are applied and
restored like any other tweak.

## Reversibility and safety

- Every tweak is classified: **Safe / Caution / High** risk and
  **Fully reversible / Best effort / Irreversible**. Nothing irreversible is
  ever applied silently.
- HAGS and telemetry changes that need a restart say so before apply.
- No folklore: Exo does not ship fake RAM "boosters", standby-list flushes or
  registry values that guides invented. If there is no evidence for a change,
  it is not in the catalog.

## Troubleshooting

- **A module reads NOT INSTALLED but the app is present**: the kit's detect
  step could not confirm it. Open the module and press **Verify**.
- **SmartScreen on first run**: the local build is unsigned; **More info →
  Run anyway**. Signed builds suppress this.
- **Exo will not start after an update**: the previous build is restored
  automatically; if not, reinstall from GitHub Releases.

## Logs

`%LocalAppData%\Exo\logs` — one file per run, with the exact settings written,
read back, skipped and refused. The Issues button in Settings opens the
GitHub issue tracker with your log ready to attach.
