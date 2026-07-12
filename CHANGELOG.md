# Changelog

## 1.8.6

- **Steam**: fix Verified apply false-negative when kit version > 1.7.2 (all checklist items mark correctly after success)
- **Discord / Steam**: stop pinning exact kit version strings for apply markers; trust full-apply flags
- **PowerShell**: prefer Preview; expand WindowsApps discovery; auto-install Microsoft.PowerShell.Preview + Windows Terminal Preview via winget when missing
- **Steam kit** 1.7.5; Discord DiscOpt version stamp aligned to 1.3.5
## 1.8.5

- **Brand icon**: Microsoft Fluent **Developer Board** filled (MIT) — solid Windows-native mark, not thin Lucide outlines / not a speedometer
## 1.8.4

- **Brand icon**: Lucide `cpu` (ISC) instead of gauge — avoids Speedtest-like speedometer look; multi-size Start Menu ICO
## 1.8.3

- **Brand icon**: real **Lucide** `gauge` icon (ISC license, free commercial use) on pure black Start Menu tile
- Multi-size ICO 16–256; source SVG under Assets/Icons with LICENSE note
## 1.8.2

- **Brand icon**: clean **OH** monogram (OptiHub) — fused O+H on pure black with mint accent; multi-size Start Menu ICO
## 1.8.1

- **Brand icon**: new unique OptiHub mark (hex hub + performance bars + mint accent) on pure black
- **Start Menu / pin**: multi-size .ico (16–256) so the glyph fills Windows Start tiles like other modern apps
- Packaged as ApplicationIcon + Start Menu shortcut IconLocation (versioned path + shell refresh)
## 1.8.0

- **Shell**: pure **WinUI 3** again — exact **1.6.13** UI (Jakarta/Cormorant fonts, hover cards, full names, Coming soon, Settings, AMOLED/cream)
- **No WebView SPA shell** — native Frame navigation only (more reliable logos, Settings, motion)
- **Backend**: current optimizers, quiet `/quiet` app updates, PS7 host, installer/WebView2 prereq helpers remain

## 1.7.9

- **UI**: restored the polished fluid SPA (1.7.4 design language) — the thin 1.7.8 shell is gone
- **Logos**: fixed blank icons — virtual host navigation + embedded base64 logo map (NavigateToString blocked file:// images)
- Larger logo wells; dark wells in light mode so product marks stay visible
## 1.7.8

- **UI overhaul**: minimal Raycast/Linear-style SPA — tight 8px rhythm, hairline cards, no page scroll
- **Home / Settings / Optimizer**: fit the fixed window; compact status, denser feature grid, clean updates strip
- Removed heavy ambient chrome, italic hero type, oversized status wells, and version footer clutter
## 1.7.7

- **WebView2**: detect incomplete Evergreen Runtime (missing icudtl/resources) and repair on launch + install
- **WebView2**: create environment before Ensure; clear bad browser-folder env; init after window Activated
- **Settings**: centered update status text only; removed duplicate version footer under the status well
- **SPA**: update status well fully centered (no misaligned icon)
## 1.7.6

- **Settings**: center the Updates status well (check-result text + icon)

## 1.7.5

- **Settings gear top-left** in host chrome (1.7.4 had it top-right on XAML fallback)
- **Quiet in-app updates**: `/quiet` SFX, no console window, no installer MessageBox
- **Start Menu icon**: versioned `.ico` path + shell notify so the brand mark refreshes
- **Updates card**: status well matches Discord/Steam/NVIDIA result panels
- **WebView2 init**: keep host HWND path; XAML fallback remains if runtime fails

## 1.5.7

- **Discord**: lean Equicord profile (minimalism/privacy/QoL) with tuned plugin options; `eagerPatches` forced **off** (blanks Discord 1.0.9245)
- **Discord**: no forced pure-black OpenAsar CSS / `cmdPreset=perf` / HW accel off; AMOLED via Equicord theme only
- **Discord**: force-disable high-overhead convenience plugins (ImageZoom, ViewIcons, CopyUserURLs, CallTimer, etc.)
- **Discord**: preserve healthy Equicord settings on re-run; safer boot/TTI/audio defaults
- **NVIDIA**: Clean Driver+ / display apply / NvDisplay helper improvements; NvCpl scale tooling; profile pack notes (layered on 1.5.0 max-perf path)
- **App**: GitHub update service + script bundle service updates; settings/NVIDIA UI polish; Steam optimizer tweaks

## 1.5.0

- **NVIDIA maximum performance**: replaced fragile Control Panel mouse automation with verified NVAPI/profile operations; added MSI High, profile hash/invariant checks, Full RGB/max-refresh/GPU-scaling verification, and aggressive overlay/telemetry/updater/FrameView/App/GFE background suppression while preserving display audio.
- **Steam no-compromise pack**: added 5-second webhelper working-set reclamation, High idle and Below Normal in-game priority control, aggressive CEF/client/download tuning, deep cache cleanup, fail-closed orphan-only shader cleanup, active-game preflight, durable pre-mutation recovery, and live applied-state verification.
- **Discord no-compromise pack**: added 5-second working-set reclamation, Above Normal priority and thread/raw-input tuning, deep cache and allowlisted module/game-SDK/locale debloat, plus stable-client-scoped Windows suppression, exact captured-state repair, and live full-pack verification.
- **Faster, cleaner app**: current stable Windows App SDK, concurrent dashboard status checks, cached images, cancellation-aware background work, atomic settings/script updates, and safer native-window cleanup.
- **Consistent UI**: accessible navigation, unified button and loading states, a single theme choice, clearer optimizer status, and consistent G-SYNC terminology.
- **No-compromise UX**: made the performance-first policy and tradeoffs explicit on the dashboard, in Settings, and at every apply confirmation; distinguished Discord/Steam Repair from NVIDIA's status-only Reset; added large-text scrolling; and kept card interactions motion-free for reduced-motion compatibility.
- **Release integrity**: verified installer size/SHA-256 metadata, preserved release history by default, added rollback-safe installation, and expanded CI/repository validation.

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


