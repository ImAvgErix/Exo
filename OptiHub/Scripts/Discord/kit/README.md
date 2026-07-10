# Discord Optimizer Kit

Bundled with [OptiHub](https://github.com/BarcusEric/OptiHub). Prefer the app:

This is the no-compromise profile: Equicord/OpenASAR, the DiscOpt 5-second
memory-trim and latency kernel, Above Normal priority, full disposable-cache
cleanup, allowlisted module/game-SDK debloat, English-only locale assets, and
Windows startup/toast/tray suppression. Login/session and Discord updater
integrity data are preserved. The in-app confirmation lists every tradeoff.

```powershell
irm "https://cdn.jsdelivr.net/gh/BarcusEric/OptiHub@main/Install-OptiHub.ps1" | iex
```

Repair reinstalls the signed stock Discord client, restores OptiHub-patched
shortcuts and the exact captured stable-client Windows integration, and keeps
login by default. Windows changes are path/ID scoped; Canary, PTB, Store, and
unrelated tasks are not changed. Recovery remains available until every restore
step verifies successfully:

```powershell
irm "https://cdn.jsdelivr.net/gh/BarcusEric/OptiHub@main/Repair-Discord.ps1" | iex
```

See the root README for full docs.
