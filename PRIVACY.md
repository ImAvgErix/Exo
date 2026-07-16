# Privacy

Exo's design goal is **zero data leaving your PC except the downloads you explicitly trigger**.
There is no telemetry, no analytics, no crash reporting, no accounts, and no ads — in the app
or in any script.

## What Exo contacts, and when

| Endpoint | When | Why | What is sent |
|---|---|---|---|
| `api.github.com/repos/ImAvgErix/Exo/releases/latest` | Only when you click **Check for updates** (and on launch if you enable it) | App update check | Nothing beyond a plain HTTPS GET (GitHub sees your IP/UA, like any download) |
| `github.com/.../releases/latest/download/Exo.exe` | Only after you confirm an update | App download | — |
| `raw.githubusercontent.com` + `codeload.github.com` | Script-kit updates (toggleable: Settings → **Auto-update scripts**) | Optimizer scripts ship separately so fixes land without an app update | — |
| `api.github.com/repos/Orbmu2k/nvidiaProfileInspector/releases/latest` | NVIDIA apply, only when a profile pack needs the tool | Fetch NVIDIA Profile Inspector | — |
| `gfwsl.geforce.com` (NVIDIA AjaxDriverService) | NVIDIA detect/apply | Look up the latest driver version for your GPU model | GPU model only (required by NVIDIA's API) |
| `us/international.download.nvidia.com` | NVIDIA apply, only if the NVIDIA app is missing and you opt in | Official NVIDIA installer | — |
| `discord.com/api/downloads/...` | Discord apply, only if Discord is missing and you opt in | Official Discord installer | — |
| `api.github.com/repos/PowerShell/PowerShell/releases` | Setup / dependency doctor, only if pwsh is missing | PowerShell 7 bootstrap | — |

Everything is plain HTTPS GET. **No POST of personal data anywhere. No cookies. No identifiers.**
If you disable update checks and script auto-update, Exo makes **zero** network requests until
you run an optimizer that needs to download a tool — and it tells you before it does.

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
- The **Internet** module changes Windows TCP/DNS settings only (all snapshotted, all
  restorable via Repair). Its optional **Private DNS** toggle (off by default) points your
  adapters at Cloudflare (`1.1.1.1` / `1.0.0.1` + IPv6) and registers Windows DNS-over-HTTPS
  for those resolvers — from then on your *system's* DNS lookups go to Cloudflare, encrypted,
  under [Cloudflare's resolver privacy policy](https://developers.cloudflare.com/1.1.1.1/privacy/public-dns-resolver/).
  Exo itself still sends nothing; Repair restores your previous DNS servers and removes the
  DoH registrations it added.

## Verification

The outbound endpoint list above is generated from the shipped sources
(`grep -roE 'https://[a-zA-Z0-9./_-]+' Exo/`). If you ever find a call not listed here,
that's a bug — [open an issue](https://github.com/ImAvgErix/Exo/issues).
