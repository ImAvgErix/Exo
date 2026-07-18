# Exo Cua QA - 2026-07-18T06:52:14.6760466-05:00

- pid: 1940
- window_id: 2753866
- exe: C:\Users\Erix\AppData\Local\Exo\app\Exo.exe

## Discord

- screenshot: `docs/cua-qa/discord.png`
- elements: 50
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: DISCORD
[11] Text: Already optimized
[12] Text: Discord Stable is not installed. Install it, then refresh this page.
[13] Pane: ScrollHost
[14] Text: Client mods & privacy
[15] Text: Equicord loads privacy plugins and strips noisy telemetry.
[16] Text: Exo Host (fast launch)
[17] Text: Equicord loader + stock Discord shell + SKIP_HOST_UPDATE / chromium lean (no OpenAsar).
[18] Text: Background memory + input policy
[19] Text: Verified DiscOpt binaries apply a 4-second idle working-set policy, Above Normal process priority, and input-thread tuning.
[20] Text: Complete client debloat
[21] Text: Old builds, optional hook/clips modules, game SDK files, extra locales, and disposable caches are removed.
[22] Text: Discord runtime integrity
[23] Text: Required desktop, utility, voice, and media modules remain installed.
[24] Text: Dark mode
[25] Text: True-black Equicord theme without a forced overlay.
[26] Text: Lean plugin budget
[27] Text: 28 enabled / budget 32 / required dependencies gated
[28] Text: Windows background suppression
[29] Text: No Discord autostart or scheduled tasks; Windows toasts off; tray icon not promoted.
[30] Text: Start Menu / apps launch path
[31] Text: Start Menu Discord shortcut uses Update.exe (or Exo launch helper). No desktop icons.
[32] Text: Voice priority (QoS DSCP 46)
[33] Text: Windows QoS policy tags Discord voice UDP traffic as Expedited Forwarding for every installed variant.
[34] Text: Discord variants (PTB/Canary)
[35] Text: Only stable Discord is installed; PTB/Canary would be optimized automatically.
[36] Text: Verified optimizer record
[37] Text: A completed full apply is recorded for this exact Discord build.
[38] ScrollBar: Vertical
[39] Button: Toggle last apply report
```

**FAIL honesty:** Discord shows Already optimized while not-installed banner is visible.

## Steam

- screenshot: `docs/cua-qa/steam.png`
- elements: 44
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: STEAM
[11] Text: Launcher needs restore
[12] Text: 1 settings are ready for this Steam installation.
[13] Pane: ScrollHost
[14] Text: Steam install
[15] Text: Client found and ready.
[16] Text: Quiet CEF launcher
[17] Text: Fast quiet CEF flags + High priority Steam start (Steam launches before the contention guard).
[18] Text: Background priority policy
[19] Text: Background CEF pages get low memory priority plus EcoQoS while gaming; the foreground Steam window stays Normal. Allocated memory is not mislabeled as reclaimed.
[20] Text: Complete client debloat
[21] Text: Caches, leftovers, crashpads cleaned; games preserved.
[22] Text: Library / overlay tweaks
[23] Text: Quieter overlay and lighter library web views.
[24] Text: Hardware-accelerated client
[25] Text: Steam CEF uses the GPU instead of costly software rendering.
[26] Text: Windows quiet shell
[27] Text: No autostart; toasts off; tray not promoted.
[28] Text: Start Menu launch path
[29] Text: Shortcuts use Exo launcher; no desktop icons.
[30] Text: Verified apply
[31] Text: Full apply recorded with durable quiet + runtime intact.
[32] ScrollBar: Vertical
[33] Button: Toggle last apply report
[34] Text: Last apply ┬╖ 13 ok
[35] Button: Apply Steam
[36] Text: Apply
[37] Button: Repair
[38] Text: Repair
[39] TitleBar: Exo
```

## Internet

- screenshot: `docs/cua-qa/internet.png`
- elements: 23
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: INTERNET
[11] Text: Checking...
[12] Text: Detecting this PC...
[13] Button: Analyze this connection and apply the best measured settings
[14] Text: Analyze & Apply
[15] Button: Repair internet stack
[16] Text: Repair
[17] Text: Repair: reset to stock defaults
[18] TitleBar: Exo
[19] MenuItem: System
[20] Button: Minimize
[21] Button: Maximize
[22] Button: Close
```

## NVIDIA

- screenshot: `docs/cua-qa/nvidia.png`
- elements: 26
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: NVIDIA
[11] Text: Checking status...
[12] Text: Detecting this PC...
[13] Text: DETECTED HARDWARE
[14] Text: Detecting GPU and displays...
[15] Text: Use G-SYNC / VRR
[16] Text: Enable only when Adaptive-Sync, FreeSync, or G-SYNC is enabled in the monitor's physical menu.
[17] Text: Open NVIDIA Control Panel
[18] Text: Apply profile
[19] Text: Repair restores the complete NVIDIA profile database saved before Apply. Drivers and display settings stay untouched.
[20] Text: Repair
[21] TitleBar: Exo
[22] MenuItem: System
[23] Button: Minimize
[24] Button: Maximize
[25] Button: Close
```

## Riot

- screenshot: `docs/cua-qa/riot.png`
- elements: 37
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: RIOT
[11] Text: Not applied
[12] Text: Apply disables launcher auto-start and scopes high-performance GPU plus Above Normal CPU priority to detected game executables.
[13] Pane: ScrollHost
[14] Text: Riot detected
[15] Text: Installed client found
[16] Text: Startup quiet
[17] Text: Launcher no longer starts with Windows
[18] Text: Per-game GPU preference
[19] Text: 0 of 3 detected executable(s) use the high-performance GPU
[20] Text: Per-game CPU priority
[21] Text: Above Normal is scoped to game executables, not the launcher
[22] Text: Anti-cheat and updates
[23] Text: Services, anti-cheat, client files, and update paths are outside Exo policy
[24] Text: Exact Repair
[25] Text: Pre-Exo registry values are saved for restore
[26] Button: Toggle last apply report
[27] Text: Last apply ┬╖ 4 ok
[28] Button: Apply Riot
[29] Text: Apply
[30] Button: Repair
[31] Text: Repair
[32] TitleBar: Exo
[33] MenuItem: System
[34] Button: Minimize
[35] Button: Maximize
[36] Button: Close
```

## Epic

- screenshot: `docs/cua-qa/epic.png`
- elements: 37
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Home
[10] Text: EPIC
[11] Text: Not applied
[12] Text: Apply disables launcher auto-start and scopes high-performance GPU plus Above Normal CPU priority to detected game executables.
[13] Pane: ScrollHost
[14] Text: Epic detected
[15] Text: Installed client found
[16] Text: Startup quiet
[17] Text: Launcher no longer starts with Windows
[18] Text: Per-game GPU preference
[19] Text: 0 of 1 detected executable(s) use the high-performance GPU
[20] Text: Per-game CPU priority
[21] Text: Above Normal is scoped to game executables, not the launcher
[22] Text: Anti-cheat and updates
[23] Text: Services, anti-cheat, client files, and update paths are outside Exo policy
[24] Text: Exact Repair
[25] Text: Pre-Exo registry values are saved for restore
[26] Button: Toggle last apply report
[27] Text: Last apply ┬╖ 4 ok
[28] Button: Apply Epic
[29] Text: Apply
[30] Button: Repair
[31] Text: Repair
[32] TitleBar: Exo
[33] MenuItem: System
[34] Button: Minimize
[35] Button: Maximize
[36] Button: Close
```

## ShellHome

- screenshot: `docs/cua-qa/shellhome.png`
- elements: 63
```
[0] Button: Minimize
[1] Button: Maximize
[2] Button: Close
[3] Button: Discord
[4] Button: Steam
[5] Button: Internet
[6] Button: NVIDIA
[7] Button: Riot
[8] Button: Epic
[9] Button: Settings
[10] Pane: 
[11] Text: EXO
[12] Text: System overview
[13] Text: 5 optimizers are verified; 1 still needs attention.
[14] Text: VERIFIED OPTIMIZERS
[15] Text: 5 / 6 verified
[16] Text: Live detection decides what is applied. Open a module to review or change it.
[17] Text: SYSTEM MEMORY
[18] Text: 7.2 GB
[19] Text: 8.7 GB free ┬╖ 15.9 GB total
[20] ProgressBar: 
[21] Button: Open Discord optimizer
[22] Text: Discord
[23] Text: VERIFIED
[24] Text: Lean client active
[25] Text: Privacy patch ┬╖ voice QoS ┬╖ idle memory guard
[26] Text: 1.1 GB below session peak
[27] Button: Open Steam optimizer
[28] Text: Steam
[29] Text: VERIFIED
[30] Text: Background policy ready
[31] Text: Starts with the optimized launcher ┬╖ no unsafe RAM purges
[32] Text: Open Steam for a live memory sample
[33] Button: Open Internet optimizer
[34] Text: Internet
[35] Text: NOT APPLIED
[36] Text: 2.5G Ethernet
[37] Text: Analyze the live path, tune the stack, and select the fastest healthy DNS
[38] Text: No current route sample
[39] Button: Open NVIDIA optimizer
```

