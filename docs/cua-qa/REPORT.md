# Exo Cua QA - 2026-07-18T19:35:25.7490451-05:00

- pid: 10980
- window_id: 23069406
- exe: C:\Users\Erix\AppData\Local\Exo\app\Exo.exe
- nav_fails: 0


## Discord

- screenshot: `docs/cua-qa/discord.png`
- nav: idx=6 label=Discord mode=background effect=unverifiable verified=False
- landed: True
- elements: 54
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
[18] Text: DISCORD
[19] Text: Already optimized
[20] Text: Client mods & privacy
[21] Text: Equicord loads privacy plugins and strips noisy telemetry.
[22] Text: Exo Host (fast launch)
[23] Text: Equicord loader + stock Discord shell + SKIP_HOST_UPDATE / chromium lean (no OpenAsar).
[24] Text: Background memory + input policy
[25] Text: Verified DiscOpt binaries apply a 2.5-second idle working-set policy, Above Normal process priority, raw input, and input-thread tuning.
[26] Text: Complete client debloat
[27] Text: Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.
[28] Text: Discord runtime integrity
[29] Text: Required desktop, utility, voice, and media modules remain installed.
[30] Text: Dark mode
[31] Text: True-black Equicord theme without a forced overlay.
[32] Text: Lean plugin budget
[33] Text: 18 curated features + 10 required APIs; every optional extra is off
[34] Text: Windows background suppression
[35] Text: No Discord autostart or scheduled tasks; Windows toasts off; tray icon not promoted.
[36] Text: Start Menu / apps launch path
[37] Text: Start Menu Discord shortcut uses Update.exe (or Exo launch helper). No desktop icons.
[38] Text: Voice priority (QoS DSCP 46)
[39] Text: Windows QoS policy tags Discord voice UDP traffic as Expedited Forwarding for every installed variant.
```

## Steam

- screenshot: `docs/cua-qa/steam.png`
- nav: idx=8 label=Steam mode=background effect=unverifiable verified=False
- landed: True
- elements: 50
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
[18] Text: STEAM
[19] Text: Already optimized
[20] Text: Steam install
[21] Text: Client found and ready.
[22] Text: Quiet CEF launcher
[23] Text: Fast quiet CEF flags and High priority Steam start before the in-game contention guard attaches.
[24] Text: In-game contention guard
[25] Text: Non-foreground steamwebhelper always soft-reclaims idle pages + EcoQoS; tighter while a game runs. Foreground Steam stays Normal. EmptyWorkingSet never used (freezes CEF).
[26] Text: Complete client debloat
[27] Text: Caches, leftovers, and crashpads cleaned; installed games and shader caches stay preserved.
[28] Text: Library / overlay tweaks
[29] Text: Quieter overlay, lighter library web views, and less CEF busywork in the background.
[30] Text: Hardware-accelerated client
[31] Text: Steam CEF uses the GPU instead of costly software rendering for the library UI.
[32] Text: Windows quiet shell
[33] Text: No Steam autostart or toast spam; tray icon is not promoted into the always-visible row.
[34] Text: Start Menu launch path
[35] Text: Start Menu shortcuts use the Exo quiet launcher; desktop icons are not recreated.
[36] Text: Runtime integrity
[37] Text: Required Steam binaries and the durable quiet helper remain on disk after apply.
[38] Text: Verified optimizer record
[39] Text: A completed full apply is recorded with durable quiet policy intact.
```

## Internet

- screenshot: `docs/cua-qa/internet.png`
- nav: idx=10 label=Internet mode=background effect=unverifiable verified=False
- landed: True
- elements: 41
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
[18] Text: INTERNET
[19] Text: Optimized - Ethernet path
[20] Button: Lowest latency policy
[21] Text: Lowest latency
[22] Button: High throughput policy
[23] Text: High throughput Γ£ô
[24] Text: Connection path
[25] Text: 2.5 Gbps Ethernet gets the lowest route metric; Wi-Fi is never disabled.
[26] Text: Policy
[27] Text: Last apply used high throughput. Toggle selects Lowest latency (FC/IM off) or High throughput (FC/IM on).
[28] Text: DNS privacy
[29] Text: Google - selected by live test; automatic DoH active
[30] Text: Safe repair
[31] Text: A pre-Exo snapshot is ready; Repair restores DNS, DoH, routes, TCP, and NIC settings.
[32] Text: 12.1 ms idle - full-load +1.5 ms download / +47.9 ms upload - 0% idle loss - Google DNS
[33] Button: Analyze this connection and apply the best measured settings
[34] Text: Analyze & Apply
[35] Button: Repair internet stack
[36] Text: Repair
[37] TitleBar: Exo
[38] MenuItem: System
[39] Button: Minimize
```

## NVIDIA

- screenshot: `docs/cua-qa/nvidia.png`
- nav: idx=12 label=NVIDIA mode=background effect=unverifiable verified=False
- landed: True
- elements: 47
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
[18] Text: NVIDIA
[19] Text: All applied
[20] Text: NVIDIA GeForce RTX 3070 - 1920x1080@165 DisplayPort - raw latency
[21] Text: G-SYNC / VRR
[22] Button: Use G-SYNC or VRR profile
[23] Button: Open NVIDIA Control Panel
[24] Text: Control Panel
[25] Text: NVIDIA GPU
[26] Text: NVIDIA GeForce RTX 3070
[27] Text: Installed driver
[28] Text: Driver 610.74 detected. Exo leaves driver installation and MSI/service policy unchanged; update through NVIDIA when you choose.
[29] Text: Hardware-matched policy
[30] Text: NVIDIA GeForce RTX 3070 - 1920x1080@165 DisplayPort - raw latency
[31] Text: 3D Base Profile
[32] Text: Max FPS / latency pack - 30 Series.nip (Verified in driver)
[33] Text: Latency / sync policy
[34] Text: Raw-latency path: G-SYNC/VRR and VSync off + Ultra Low Latency; Reflex takes priority automatically in supported games.
[35] Text: Per-game profiles
[36] Text: Imported 29 game profiles from your series pack + competitive/hybrid deltas (Valorant, Counter-Strike 2, Marvel Rivals, Rainbow Six Siege, Fortnite, Apex Legends...).
[37] Text: NVIDIA Control Panel
[38] Text: Available from Exo. NVIDIA App, overlays, services, and driver packages are left under your control.
[39] Button: Apply NVIDIA
```

## Riot

- screenshot: `docs/cua-qa/riot.png`
- nav: idx=14 label=Riot mode=background effect=unverifiable verified=False
- landed: True
- elements: 46
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
[18] Text: RIOT
[19] Text: Already optimized
[20] Text: Riot install
[21] Text: Found: League of Legends, VALORANT.
[22] Text: Game discovery
[23] Text: 3 executable(s) used only to detect when a game is running.
[24] Text: Startup quiet
[25] Text: Launcher brand is removed from Windows Run; Exo yield companion may remain as Exo-* only.
[26] Text: Shell quiet
[27] Text: Launcher toast notifications muted; non-anti-cheat scheduled tasks quieted (Steam-parity, app-scoped).
[28] Text: Launcher yield while gaming
[29] Text: While a game runs, launcher UI drops to low memory priority + EcoQoS and soft-reclaims idle pages. Games and anti-cheat stay untouched.
[30] Text: Anti-cheat boundary
[31] Text: Vanguard, Riot Client services, game files, and updates are never modified.
[32] Text: Exact Repair snapshot
[33] Text: Pre-Exo Run entries are saved so Repair restores startup exactly.
[34] Text: Verified optimizer record
[35] Text: A completed full apply is recorded for this Riot installation.
[36] Button: Toggle last apply report
[37] Text: Last apply ┬╖ 1 done ┬╖ 6 already set
[38] Button: Apply Riot
[39] Text: Reapply
```

## Epic

- screenshot: `docs/cua-qa/epic.png`
- nav: idx=16 label=Epic mode=background effect=unverifiable verified=False
- landed: True
- elements: 46
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
[18] Text: EPIC
[19] Text: Already optimized
[20] Text: Epic install
[21] Text: Found: Launcher.
[22] Text: Game discovery
[23] Text: 1 executable(s) used only to detect when a game is running.
[24] Text: Startup quiet
[25] Text: Launcher brand is removed from Windows Run; Exo yield companion may remain as Exo-* only.
[26] Text: Shell quiet
[27] Text: Launcher toast notifications muted; non-anti-cheat scheduled tasks quieted (Steam-parity, app-scoped).
[28] Text: Launcher yield while gaming
[29] Text: While a game runs, launcher UI drops to low memory priority + EcoQoS and soft-reclaims idle pages. Games and anti-cheat stay untouched.
[30] Text: Anti-cheat boundary
[31] Text: Epic Online Services, launcher files, caches, and updates are never modified.
[32] Text: Exact Repair snapshot
[33] Text: Pre-Exo Run entries are saved so Repair restores startup exactly.
[34] Text: Verified optimizer record
[35] Text: A completed full apply is recorded for this Epic installation.
[36] Button: Toggle last apply report
[37] Text: Last apply ┬╖ 1 done ┬╖ 6 already set
[38] Button: Apply Epic
[39] Text: Reapply
```

## ShellHome

- screenshot: `docs/cua-qa/shellhome.png`
- nav: idx=3 label=Open system overview mode=background effect=unverifiable verified=False
- landed: True
- elements: 67
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
[18] Text: SYSTEM
[19] Text: Optimization status
[20] Text: Every optimizer has a verified apply record.
[21] Text: LIVE SYSTEM READ
[22] Text: 6 / 6 verified
[23] Text: SYSTEM MEMORY
[24] Text: 4.6 GB
[25] Text: 11.4 GB free - 15.9 GB total
[26] ProgressBar: 
[27] Button: Open Discord optimizer
[28] Text: Discord
[29] Text: VERIFIED
[30] Text: Lean client active
[31] Text: Privacy patch - voice QoS - idle memory guard
[32] Text: 1.1 GB below session peak
[33] Button: Open Steam optimizer
[34] Text: Steam
[35] Text: VERIFIED
[36] Text: Background policy ready
[37] Text: Starts with the optimized launcher - no unsafe RAM purges
[38] Text: Open Steam for a live memory sample
[39] Button: Open Internet optimizer
```

