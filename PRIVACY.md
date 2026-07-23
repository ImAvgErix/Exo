# Privacy

Exo's design goal is **no telemetry and no data upload**. Network Analyze and
vendor/tool downloads make the outbound requests listed below.
There is no telemetry, no analytics, no crash reporting, no accounts, and no ads — in the app
or in any script.

## What Exo contacts, and when

| Endpoint | When | Why | What is sent |
|---|---|---|---|
| `api.github.com/repos/ImAvgErix/Exo/releases/latest` | Only when you click **Check for updates** (and on launch if you enable it) | App update check | Nothing beyond a plain HTTPS GET (GitHub sees your IP/UA, like any download) |
| `github.com/.../releases/latest/download/Exo.exe` | Only after you confirm an update | App download | — |
| Cloudflare, Google, and Quad9 public endpoints | Internet Analyze/Apply | Measure route quality, throughput, and resolver response on this connection | Test traffic, DNS queries, and your public IP as seen by those providers |
| `api.github.com/repos/Orbmu2k/nvidiaProfileInspector/releases/latest` | NVIDIA apply, only when a profile pack needs the tool | Fetch NVIDIA Profile Inspector | — |
| `gfwsl.geforce.com` (NVIDIA AjaxDriverService) | NVIDIA detect/apply | Look up the latest driver version for your GPU model | GPU model only (required by NVIDIA's API) |
| `us/international.download.nvidia.com` | NVIDIA apply, only if the NVIDIA app is missing and you opt in | Official NVIDIA installer | — |
| `discord.com/api/downloads/...` | Discord apply, only if Discord is missing and you opt in | Official Discord installer | — |
| `api.github.com/repos/Equicord/Equilotl` and Equicord release assets | Discord full apply | Fetch the selected third-party client modification when the bundled copy is unavailable | — |
| `api.github.com/repos/PowerShell/PowerShell/releases` | Only after you start Apply/Repair and PowerShell 7 is missing | PowerShell 7 bootstrap (the UI shows **Preparing PowerShell 7…**) | — |

Everything is plain HTTPS GET. **No POST of personal data anywhere. No cookies. No identifiers.**
If you disable update checks, Exo makes no request until you run Internet Analyze/Apply or
an optimizer needs a vendor/tool download. Optimizer kits ship with each app release. Exo never installs
PowerShell or copies optimizer kits merely because the app opened; dependency work begins
only after your Apply/Repair action.

## What stays on your PC (all under `%LocalAppData%\Exo`)

- `*.json` — optimizer state files (what was applied, verify results, snapshot manifests)
- `logs/` — run logs you can open from Settings → **Open logs**
- Snapshots of every setting an optimizer changes, so Repair can restore them

Nothing is uploaded. Delete the folder and it's gone.

## Background footprint

Exo installs **no services, no scheduled tasks, no startup entries, no drivers**, and keeps
**no process running** when the window is closed (this is enforced by automated contract
tests — see `Exo.NoBackground` and the smoke suites). Optimizers change settings and stop;
they never install watchers.

## What the optimizers touch on other vendors' behalf

- The **Discord** module sets the client's `no-pings` switch (Discord's own stats/telemetry
  opt-out) and removes Discord's auto-update scheduled tasks (manual updates still work —
  Squirrel state is never touched).
- The **Steam** module is read-only about Steam's network behavior; it tunes local client
  settings only.
- The **Internet** module changes Windows TCP/DNS settings only (all snapshotted and
  restorable via Repair). Analyze tests Cloudflare, Google, and Quad9 on the current route,
  selects the fastest healthy candidate, and requests its published DNS-over-HTTPS template
  where Windows supports automatic DoH. Repair restores the previous DNS servers and removes
  registrations Exo added.
- The **Games** module reads and writes user config files only (quality settings, borderless
  mode) for installed titles. It never contacts a game's servers, scans game content, mods
  game binaries, or uploads the detected installation list.
- The **Brave** module sets local managed policies and profile preferences (telemetry/reporting
  off, high-perf GPU) and never contacts Brave's servers on your behalf.

## Verification

The outbound endpoint list above is generated from the shipped sources
(`grep -roE 'https://[a-zA-Z0-9./_-]+' Exo/`). If you ever find a call not listed here,
that's a bug — [open an issue](https://github.com/ImAvgErix/Exo/issues).
