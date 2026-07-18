# Discord Optimizer Kit

Bundled with [Exo](https://github.com/ImAvgErix/Exo). Prefer the app:

The full profile uses Equicord + Exo Host (stock Discord shell
with SKIP_HOST_UPDATE and lean chromium flags — OpenASAR is no longer used),
the DiscOpt 4-second memory-trim and latency kernel, Above Normal priority,
full disposable-cache cleanup, allowlisted module/game-SDK debloat,
English-only locale assets, voice QoS (DSCP 46) policies, and Windows
startup/toast/tray suppression. Login/session and Discord updater integrity
data are preserved. This is an update-sensitive client modification, not a
guaranteed RAM or latency reduction; Apply verifies the exact installed files
and Repair restores the signed stock client.

```powershell
irm "https://cdn.jsdelivr.net/gh/ImAvgErix/Exo@main/Install-Exo.ps1" | iex
```

Repair reinstalls the signed stock Discord client, restores Exo-patched
shortcuts and the exact captured stable-client Windows integration, removes the
Exo QoS policies and Exo flags written to PTB/Canary settings, and keeps login
by default. Windows changes are path/ID scoped; Store installs and unrelated
tasks are not changed. Recovery remains available until every restore step
verifies successfully:

```powershell
irm "https://cdn.jsdelivr.net/gh/ImAvgErix/Exo@main/Repair-Discord.ps1" | iex
```

See the root README for full docs.
