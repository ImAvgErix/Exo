# Changelog

## 1.2.1

- **NVIDIA**: always check NVIDIA for newest Game Ready; if behind, prompt/launch NVCleanstall + official download
- **NVCleanstall checklist**: unattended express, auto reboot, clean install, disable Ansel, disable installer+driver telemetry, MSI High, disable HDCP, EAC-compatible method, accept unsigned driver
- **Conflict cleanup** for App/GFE/CPL leftovers; Steam/Discord leftover clears on apply
## 1.2.0

- **NVIDIA Optimizer** (live card): auto-detect GPU series, G-SYNC toggle, import OptiHub Base Profile via Profile Inspector
- Improved public .nip packs for 10/20/30/40/50 series (FPS/latency + series rBAR/DLSS)
- Downloads Profile Inspector + optional NVIDIA App; telemetry task/service trim; display Full RGB / high bpc guidance

## 1.1.8

- **Light mode**: stronger charcoal outlines; dark logo wells so white Steam/Epic marks stay visible
- **About / README**: hub wording for Discord, Steam, and more (not Discord-only)

## 1.1.7

- **Steam**: former aggressive CEF flags are now the only/default launcher (nofriendsui, nointro, etc.)
- **No desktop shortcuts** created for Steam or Discord; removes prior OptiHub desktop icons
- Start Menu / taskbar still retargeted to OptiHub launchers

## 1.1.6

- **Steam**: retarget Start Menu, taskbar pins, and Desktop shortcuts to lean launcher (Open Steam from Start apps still gets CEF flags + trim helper)
- **Discord**: retarget Start Menu, taskbar, and Desktop Discord shortcuts to Discord.vbs (-Launch) so OpenASAR/kernel always load
- Ensures canonical Start Menu entries exist for both apps

## 1.1.5

- **Steam aggressive launcher** (optional Desktop shortcut): nofriendsui, nointro, nobigpicture, vrdisable, no-dwrite, cef-disable-breakpad
- **In-game priority yield**: steam + steamwebhelper set BELOW_NORMAL while a game runs; HIGH in library
- **5s webhelper EmptyWorkingSet** idle + in-game (no suspend)
- **Shader pre-cache clean** + multi-library cache paths
- **Overlay/library VDF hints**: quieter overlay noise, no downloads while playing when keys exist
- Lean default launcher remains **Steam (OptiHub Lean)**

## 1.1.4

- **Steam webhelper**: trim every 5s (DiscOpt cadence), always on idle + in-game
- **In-game**: suspend `steamwebhelper` while a Steam game runs; resume after (overlay may pause)
- Fixed game-detection typo that skipped idle trim logic
- CI: ASCII-only PowerShell scripts (non-ASCII was failing GitHub checks)

## 1.1.3

- Steam performance pack confirm copy / CEF lean webhelper notes

## 1.1.0

- **Steam Optimizer** live: startup quieting, safe cache clean, client config hints
- Universal multi-PC Steam detect / run / repair (no game file injection)
- Discord kit modular lib (from 1.0.42)

## 1.0.42

- Deeper kit split: kit/lib modules
- Universal Equicord profile

