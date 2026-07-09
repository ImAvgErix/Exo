# Changelog

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

