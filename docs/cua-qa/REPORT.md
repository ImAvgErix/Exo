# Exo Cua QA - 2026-07-18T14:52:59.6929038-05:00

- pid: 7568
- window_id: 22872122
- exe: C:\Users\Erix\AppData\Local\Exo\app\Exo.exe

## Discord

- screenshot: `docs/cua-qa/discord.png`
- elements: 60
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: DISCORD
[20] Text: Already optimized
[21] Text: Verified on this installation. Apply again after Discord replaces its client files.
[22] Text: WHAT EXO WILL CHANGE
[23] Text: Hardware-aware, reversible
[24] Text: Client mods & privacy
[25] Text: Equicord loads privacy plugins and strips noisy telemetry.
[26] Text: Exo Host (fast launch)
[27] Text: Equicord loader + stock Discord shell + SKIP_HOST_UPDATE / chromium lean (no OpenAsar).
[28] Text: Background memory + input policy
[29] Text: Verified DiscOpt binaries apply a 4-second idle working-set policy, Above Normal process priority, and input-thread tuning.
[30] Text: Complete client debloat
[31] Text: Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.
[32] Text: Discord runtime integrity
[33] Text: Required desktop, utility, voice, and media modules remain installed.
[34] Text: Dark mode
[35] Text: True-black Equicord theme without a forced overlay.
[36] Text: Lean plugin budget
[37] Text: 18 curated features + 10 required APIs; every optional extra is off
[38] Text: Windows background suppression
[39] Text: No Discord autostart or scheduled tasks; Windows toasts off; tray icon not promoted.
```

## Steam

- screenshot: `docs/cua-qa/steam.png`
- elements: 56
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: STEAM
[20] Text: 1 setting needs Apply (contention guard)
[21] Text: One launcher setting is out of policy. Apply restores it without touching games.
[22] Text: WHAT EXO WILL CHANGE
[23] Text: Hardware-aware, reversible
[24] Text: Steam install
[25] Text: Client found and ready.
[26] Text: Quiet CEF launcher
[27] Text: Fast quiet CEF flags and High priority Steam start before the in-game contention guard attaches.
[28] Text: In-game contention guard
[29] Text: Background CEF helpers drop to low memory priority plus EcoQoS while a game runs; the foreground Steam window stays Normal.
[30] Text: Complete client debloat
[31] Text: Caches, leftovers, and crashpads cleaned; installed games and shader caches stay preserved.
[32] Text: Library / overlay tweaks
[33] Text: Quieter overlay, lighter library web views, and less CEF busywork in the background.
[34] Text: Hardware-accelerated client
[35] Text: Steam CEF uses the GPU instead of costly software rendering for the library UI.
[36] Text: Windows quiet shell
[37] Text: No Steam autostart or toast spam; tray icon is not promoted into the always-visible row.
[38] Text: Start Menu launch path
[39] Text: Start Menu shortcuts use the Exo quiet launcher; desktop icons are not recreated.
```

## Internet

- screenshot: `docs/cua-qa/internet.png`
- elements: 42
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: INTERNET
[20] Text: Ethernet path - 2.5 Gbps
[21] Text: Ready to measure this connection and apply one balanced policy.
[22] Text: WHAT EXO WILL CHANGE
[23] Text: Hardware-aware, reversible
[24] Text: Connection path
[25] Text: 2.5 Gbps Ethernet gets the lowest route metric; Wi-Fi is never disabled.
[26] Text: Adaptive tuning
[27] Text: Measures idle latency, full-load queueing, and throughput before choosing one combined policy.
[28] Text: DNS privacy
[29] Text: Tests Cloudflare, Google, and Quad9 on this route, selects the fastest healthy resolver, and requests automatic DoH when Windows supports it.
[30] Text: Safe repair
[31] Text: Apply takes a pre-change snapshot; Repair can return the Windows network stack to stock defaults.
[32] Button: Analyze this connection and apply the best measured settings
[33] Text: Analyze & Apply
[34] Button: Repair internet stack
[35] Text: Repair
[36] Text: Repair: reset to stock defaults
[37] TitleBar: Exo
[38] MenuItem: System
[39] Button: Minimize
```

## NVIDIA

- screenshot: `docs/cua-qa/nvidia.png`
- elements: 56
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: NVIDIA
[20] Text: Driver changed - reapply
[21] Text: 3 settings are ready for the detected GPU and display path.
[22] Text: DETECTED HARDWARE
[23] Text: NVIDIA GeForce RTX 3070 - 1920x1080@165 DisplayPort - raw latency
[24] Text: Use G-SYNC / VRR
[25] Text: Enable only when Adaptive-Sync, FreeSync, or G-SYNC is enabled in the monitor's physical menu.
[26] Button: Use G-SYNC or VRR profile
[27] Button: Open NVIDIA Control Panel
[28] Text: Open NVIDIA Control Panel
[29] Text: WHAT EXO WILL CHANGE
[30] Text: Hardware-aware, reversible
[31] Text: NVIDIA GPU
[32] Text: NVIDIA GeForce RTX 3070
[33] Text: Installed driver
[34] Text: Driver 610.74 detected. Exo leaves driver installation and MSI/service policy unchanged; update through NVIDIA when you choose.
[35] Text: Hardware-matched policy
[36] Text: NVIDIA GeForce RTX 3070 - 1920x1080@165 DisplayPort - raw latency
[37] Text: 3D Base Profile
[38] Text: Not applied yet. Apply runs Profile Inspector -silentImport (no GUI / replace click).
[39] Text: Latency / sync policy
```

## Riot

- screenshot: `docs/cua-qa/riot.png`
- elements: 52
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: RIOT
[20] Text: Already optimized
[21] Text: Verified Windows policy only. Riot client files, services, anti-cheat, updates, and game settings stay untouched.
[22] Text: WHAT EXO WILL CHANGE
[23] Text: Hardware-aware, reversible
[24] Text: Riot install
[25] Text: Found: League of Legends, VALORANT.
[26] Text: Game discovery
[27] Text: 3 executable(s) ready for GPU policy.
[28] Text: Startup quiet
[29] Text: Launcher is removed from Windows Run so it does not autostart with the session.
[30] Text: High-performance GPU
[31] Text: All 3 detected game executable(s) use the high-performance GPU.
[32] Text: Hybrid GPU split
[33] Text: Single-GPU PC: games use the only adapter; no launcher override is needed.
[34] Text: Anti-cheat boundary
[35] Text: Vanguard, Riot Client services, game files, and updates are never modified.
[36] Text: Exact Repair snapshot
[37] Text: Pre-Exo registry values are saved so Repair can restore startup and GPU prefs exactly.
[38] Text: Verified optimizer record
[39] Text: A completed full apply is recorded for this Riot installation.
```

## Epic

- screenshot: `docs/cua-qa/epic.png`
- elements: 52
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: EPIC
[20] Text: Already optimized
[21] Text: Verified Windows policy only. Epic client files, services, anti-cheat, updates, and game settings stay untouched.
[22] Text: WHAT EXO WILL CHANGE
[23] Text: Hardware-aware, reversible
[24] Text: Epic install
[25] Text: Found: Launcher.
[26] Text: Game discovery
[27] Text: 1 executable(s) ready for GPU policy.
[28] Text: Startup quiet
[29] Text: Launcher is removed from Windows Run so it does not autostart with the session.
[30] Text: High-performance GPU
[31] Text: All 1 detected game executable(s) use the high-performance GPU.
[32] Text: Hybrid GPU split
[33] Text: Single-GPU PC: games use the only adapter; no launcher override is needed.
[34] Text: Anti-cheat boundary
[35] Text: Epic Online Services, launcher files, caches, and updates are never modified.
[36] Text: Exact Repair snapshot
[37] Text: Pre-Exo registry values are saved so Repair can restore startup and GPU prefs exactly.
[38] Text: Verified optimizer record
[39] Text: A completed full apply is recorded for this Epic installation.
```

## ShellHome

- screenshot: `docs/cua-qa/shellhome.png`
- elements: 74
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Open system overview
[4] Text: EXO
[5] Button: Settings
[6] Button: Discord
[7] Text: Discord
[8] Button: Steam
[9] Text: Steam
[10] Button: Internet
[11] Text: Internet
[12] Button: NVIDIA
[13] Text: NVIDIA
[14] Button: Riot
[15] Text: Riot
[16] Button: Epic
[17] Text: Epic
[18] Pane: 
[19] Text: SYSTEM
[20] Text: Optimization status
[21] Text: 5 verified - next: open Internet.
[22] Text: LIVE SYSTEM READ
[23] Text: 5 / 6 verified
[24] Text: Every module detects this PC first, applies only supported changes, and keeps a repair path.
[25] Text: SYSTEM MEMORY
[26] Text: 4.2 GB
[27] Text: 11.7 GB free ┬╖ 15.9 GB total
[28] ProgressBar: 
[29] Text: RECOMMENDED NEXT
[30] Text: Analyze the live path, tune the stack, and select the fastest healthy DNS
[31] Button: Open recommended next optimizer
[32] Text: Open Internet
[33] Button: Open Discord optimizer
[34] Text: Discord
[35] Text: VERIFIED
[36] Text: Lean client active
[37] Text: Privacy patch ┬╖ voice QoS ┬╖ idle memory guard
[38] Text: 1.1 GB below session peak
[39] Button: Open Steam optimizer
```

