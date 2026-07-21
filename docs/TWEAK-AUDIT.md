# Exo tweak audit (evidence-based)

Last pass: v3.6.1. Goal: an evidence-based ceiling of real OS/driver/client performance for every module —
every known tweak in the landscape is either implemented or listed here with a concrete exclusion
reason. Nothing is silently skipped, and nothing invented (no dead registry folklore).

Exclusion reasons allowed: **folklore** (fake/no-op), **breaks signing/anti-cheat**, or
**documented breakage of the target itself**.

## Internet

Safety contract (v2.4.0): every mutation is captured to a pristine pre-apply snapshot
(`%LocalAppData%\Exo\network-snapshot.json`, never overwritten); Wi‑Fi disable is gated on a real
TCP-443 probe bound to the Ethernet interface; a failed post-apply probe auto-rolls back; Repair is
an exact snapshot restore (stock reset only as no-snapshot fallback); `Repair-Internet.ps1` recovers
without the app. See `docs/INTERNET-GOLDEN-PATH.md`.

| Knob | Verdict | Why |
|------|---------|-----|
| `DisableTaskOffload=0` | **Keep** | `=1` is a real footgun; kills stack offloads |
| Autotune normal | **Keep** | Supported adaptive default for both policies; experimental removed because it can grow queues without improving typical multi-gig links |
| Heuristics disabled | **Keep** | Prevents Windows from restricting autotune |
| RSS on | **Keep** | Documented multi-queue receive |
| RSS `BaseProcessorNumber 2` (Ethernet, ≥4 CPUs) | **Implemented (v2.4.0)** | Keeps NIC interrupts off core 0; supported `Set-NetAdapterRss` path |
| Adaptive RSS profile + processor/queue budget | **Implemented (v3.2.0)** | Lowest latency uses `ClosestProcessor`; throughput uses `NUMAStatic`; `MaxProcessors` and `NumberOfReceiveQueues` are set only when the installed cmdlet exposes them |
| D0 packet coalescing off | **Implemented (v3.2.0, capability-gated)** | Supported `Set-NetAdapterPowerManagement` parameter; unsupported drivers are reported as N/A |
| Adapter power + extended RSS snapshot/restore | **Implemented (v3.2.0)** | Snapshot v2 captures supported power values, RSS profile, base CPU, processor budget, and queue count for exact Repair |
| RSC on/off by preset | **Keep** | Real coalescing latency vs throughput tradeoff |
| LSO on/off by preset | **Keep** | Real driver offload tradeoff |
| CUBIC | **Keep** | Current Windows default path |
| Nagle keys (latency only) | **Keep** | Still applied per interface for TCP |
| TCP timestamps disabled | **Implemented (v2.4.0, both presets)** | Documented netsh; header overhead |
| TCP Fast Open + fallback | **Implemented (v2.4.0, both)** | RFC 7413, Win10+ |
| `pacingprofile=off` | **Implemented (v2.4.0, latency)** | Documented netsh; pacing adds latency |
| HyStart disabled | **Implemented (v2.4.0, latency)** | Documented netsh |
| UDP URO disabled | **Implemented (v2.4.0, latency, 24H2+ gated)** | `netsh int udp set global uro=` exists only on build 26100+ |
| ECN per preset | **Implemented (v2.4.0)** | disabled (latency) / enabled (throughput) |
| `InitialRtoMs 1000` / `MinRtoMs 300` | **Implemented (v2.4.0, latency)** | Documented `Set-NetTCPSetting` |
| `MaxSynRetransmissions 2` / `NonSackRttResiliency Disabled` | **Implemented (v2.4.0, both)** | Documented |
| DNS ServiceProvider priorities | **Corrected (v3.0.11)** | 4/5/6/7 retired as folklore (measured DNS 100ms → 1s+); apply/repair pin documented defaults 499/500/2000/2001 |
| IPv4-first prefix policy (`::ffff:0:0/96` 55 4) | **Implemented (v2.4.0)** | Replaces the metric+20 hack; snapshot-restorable |
| DoSvc Manual + `DODownloadMode 0` + BITS throttle policy removal | **Implemented (v2.4.0)** | Background download quiet, all snapshotted |
| ULP / SIPS / Advanced EEE / Green Ethernet off | **Implemented (v2.4.0, keyword sweep)** | Driver-exposed power knobs only |
| SystemResponsiveness **10** | **Keep** | MS: &lt;10 or &gt;100 **clamp to 20**; 0 is wrong |
| NetworkThrottlingIndex **10** (force) | **Keep** | OS default; `ffffffff` can raise DPC/audio issues |
| QoS NonBestEffortLimit 0 | **Keep** | Real GPO (old 20% reserve) |
| Games MMCSS Priority/GPU | **Keep** | Real when apps register with MMCSS |
| Flow Control off (latency) | **Keep** | Pause frames stall gaming under congestion |
| IdleRestriction on (latency, Intel) | **Keep** | Blocks NIC low-power idle on I225/I226-class |
| NIC EEE / selective suspend off | **Keep** | Real link power renegotiation source |
| powercfg wireless max / PCIe ASPM off | **Keep** | Documented power plan settings |
| Interface metric 1 on verified eth | **Keep (probe-gated v2.4.0)** | Wi‑Fi remains enabled; metrics prefer Ethernet only after an eth-bound internet probe succeeds |
| PnPCapabilities 24 | **Keep** | Stops "turn off this device to save power" |
| Dynamic ports via netsh | **Keep** | Modern replacement for MaxUserPort |
| Advanced-property writes by `RegistryKeyword` | **Implemented (v2.4.0)** | Locale-independent (`*FlowControl` etc.); English DisplayName fuzzy match kept only for vendor-specific knobs |
| MaxUserPort / MaxFreeTcbs / TcpNumConnections | **Excluded** | XP/server-era; ignored on modern desktop |
| TCP chimney / NetDMA / DCA registry | **Excluded** | Removed from modern Windows |
| LargeSystemCache | **Excluded** | Server-oriented; can hurt desktop |
| Static TcpWindowSize / GlobalMaxTcpWindowSize | **Excluded** | Auto-tuning owns RWIN |
| AFD dynamic backlog / TCPNoDelay global | **Excluded** | Server folklore |
| WinINET MaxConnections | **Excluded** | IE-era; modern browsers ignore |
| DefaultTTL / KeepAlive / SynAttackProtect | **Excluded** | No meaningful client gaming effect |
| Tcp1323Opts registry | **Excluded** | Superseded by netsh timestamps (implemented) |
| Disable IPv6 (`DisabledComponents`) | **Excluded** | Breaks modern stacks; IPv4-first via prefix policy instead |
| Automatic public DNS selection | **Implemented (v3.6.0)** | Analyze directly tests Cloudflare, Google, and Quad9 on the current route, selects the fastest healthy responder, registers its published DoH template when Windows supports it, and snapshots exact prior adapter DNS/DoH state for Repair. Cloudflare is only the offline fallback. |
| Force MTU / jumbo for gaming | **Excluded** | Path-MTU breakage risk (PPPoE/VPN) |
| Interrupt affinity via registry | **Excluded** | `Set-NetAdapterRss` is the supported path (implemented) |
| Game Mode / GameDVR / HAGS / power plan | **Out of module scope** | Internet module is network-only since v2.3.4; these belong to the future Windows module |
| Scheduled tray tasks | **Excluded** | Background noise |

### Ethernet vs Wi‑Fi (same apply, different branches)

| Behavior | Ethernet | Wi‑Fi |
|----------|----------|-------|
| Stack (autotune, Nagle, MMCSS, QoS) | Same | Same |
| Checksum / LSO / RSC (if exposed) | Set; missing props skipped | Set if exposed; often no-ops |
| RSS / multi-queue + CPU spread | **On** (supported) | **Skipped** (many wireless NICs lack RSS) |
| EEE / green ethernet / ULP / SIPS | Off | Off if present |
| Wi‑Fi power-save / uAPSD / MIMO PS | n/a | **Off** |
| Preferred band | n/a | Prefer 6 GHz, else 5 GHz (**never** force-only) |
| Restart adapter after apply | **Yes** (Up Ethernet) | **No** (would drop association) |
| Ethernet verified online (bound probe) | **Prefer Ethernet** (metric 1; Wi‑Fi stays available) | n/a |
| Ethernet has IP but probe fails | Wi‑Fi **stays enabled** | n/a |
| Wi‑Fi-only machine | n/a | Path policy no-ops (`wifi-disable\|skip`) |
| VPN / virtual adapters | **Excluded from NIC sweep** (interface type + hardware check) | Same |

## NVIDIA

Verification contract (v2.4.0): after Profile Inspector import, Exo exports the live DRS database
(NPI `-exportCustomized`, pinned v3.0.1.11 + SHA-256) and verifies the pinned values; detect
re-verifies live, so the tile flips to *Drifted — re-apply* if NVIDIA App/CPL resets settings.
Repair restores the full pre-Exo DRS database captured before the first Apply.

| Surface | Verdict | Why |
|---------|---------|-----|
| Automatic driver/package reinstall or component strip | **Excluded from default policy (v3.6.0)** | Exo does not replace the installed driver, audio, NVIDIA App, Control Panel, services, tasks, or overlays merely to apply latency profiles. |
| MSI mode / service / telemetry registry edits | **Excluded from default policy (v3.6.0)** | Driver-version and hardware-dependent changes are not required for a reversible DRS policy and cannot be universally verified as faster. |
| DRS Base pack import (`-silentImport`) | **Keep** | 10 series packs |
| Post-import DRS verification (`-exportCustomized`) | **Implemented (v2.4.0)** | NPI pinned v3.0.1.11 + SHA-256 |
| Live DRS drift detect | **Implemented (v2.4.0)** | Non-elevated export + shared classifier |
| Vulkan/OpenGL present method = layered DXGI | **Implemented (v2.4.0)** | Pack pin `550867192=1` |
| Background app max frame rate 30 | **Implemented (v2.4.0)** | Pack pin `277041158=30` |
| Resizable BAR / DLSS / Frame Generation / RT global overrides | **Excluded from combined Base profile** | Driver allowlists and each game/engine own these; forcing them globally can regress compatibility or frame pacing. |
| Shader cache unlimited / LOD clamp / threaded optimization | **Keep** | Stable DRS profile controls, verified after import. |
| Per-game catalog | **Expanded to 29 titles (v2.4.0)** | Comp deltas: PRF=1, max perf, ULL per pack, frame-gen off |
| G-SYNC latency/sync policy | **Explicit (v3.6.0)** | Exo never infers the monitor's physical adaptive-sync state. The off path keeps raw-latency sync pins; the user toggle selects the G-SYNC + driver VSync profile. Reflex remains authoritative when supported and enabled by the game. |
| Minecraft `javaw.exe` profile | **Excluded** | Shared Java host — would force max-perf pins on every Java app |
| Automatic resolution / refresh / scaling / color changes | **Excluded from safe default (v3.6.0)** | Hardware is detected and reported, but Apply leaves the active display configuration unchanged. |
| CPL: digital vibrance (DVC) | **Implemented (v2.4.0; panel slider v2.4.1)** | get/set/status with readback verify; per-display slider in the Exo NVIDIA Panel |
| CPL: per-display G-SYNC toggle | **Excluded** | Public NVAPI `GSync_*` APIs are Quadro Sync genlock hardware, not consumer VRR; NvAPIWrapper ships no G-SYNC surface. Consumer per-display VRR has no documented public API — DRS pack pins (`* G-SYNC.nip`) cover behavior |
| Unsigned INF edits (NvCleanInstall "tweaks") | **Excluded** | Breaks driver signing |
| EAC/anti-cheat component strip | **Excluded** | Prohibited |
| `D3PCLatency` / PCIe latency registry keys | **Excluded** | Folklore-grade; not verifiably documented |
| Tray icons / App startup entries / package ghosts | **Excluded from default policy (v3.6.1)** | Apply no longer edits unrelated NVIDIA shell, startup, service, task, or package state. |

## Discord

| Surface | Verdict | Why |
|---------|---------|-----|
| DiscOpt kernel (priority, 4s trim, raw input) | **Keep, compatibility-gated** | Exact bundled binaries/config are verified and stock ffmpeg is backed up; this remains an update-sensitive third-party in-process modification and is not described as a guaranteed RAM/latency gain. Boot failure disarms it. |
| Voice QoS DSCP 46 policy (per variant) | **Implemented (v2.4.0)** | Documented Windows QoS policy schema; repair removes Exo names only |
| Spellcheck dictionary trim (keep en-US + system locale) | **Implemented (v2.4.0)** | Deterministic allow list; re-downloaded on demand |
| Locale `.pak` trim | **Keep** | Existing deterministic debloat |
| `disable-background-timer-throttling` | **Implemented (v2.4.0)** | Real Chromium switch; full-rate timers when hidden (voice latency) |
| `disable-hang-monitor` | **Implemented (v2.4.0)** | Real switch; disables the unresponsive-page watchdog dialog only |
| `disable-renderer-backgrounding` / occluded-windows | **Keep** | Verified real |
| PTB / Canary variants (QoS + quiet pass) | **Implemented (v2.4.0)** | All installed variants optimized; detect requires all |
| Equicord plugin budget | **Implemented (v3.2.0)** | Curated privacy/minimalism set plus manifest-required transitive dependencies; all other optional plugins disabled, max budget enforced and detected live |
| PTB/Canary Equicord + kernel install | **Excluded** | Test channels churn; module layout not guaranteed |
| `single-process`, `disable-gpu`, `in-process-gpu`, etc. | **Excluded (forbidden)** | Documented client blanking (changelog) |
| Zoom/locale payload removal inside modules | **Excluded** | No deterministic target; unknown-file deletion broke 1.0.92xx boots |
| Boot-to-ready measurement | **Excluded** | No reliable start→ready timestamp pair in Discord logs; a fake number is worse than none |
| Legacy OpenAsar layout accepted by detect | **Removed (v2.4.0)** | Apply no longer produces it; rows are binary |

## Steam

| Surface | Verdict | Why |
|---------|---------|-----|
| CEF lean launcher /HIGH + cache clean | **Keep** | Real client overhead reduction |
| VDF key **injection** at verified section paths | **Implemented (v2.4.0)** | Missing target keys inserted (`.exo-bak` first); modern Steam omits them |
| Library low-bandwidth / low-perf / community content off | **Implemented (v2.4.0)** | Verified `UserLocalConfigStore` keys |
| Friends notifications + sounds fully quiet | **Implemented (v2.4.0)** | Verified `friends` section |
| config.vdf: `DownloadThrottleKbps=0`, `AllowDownloadsDuringGameplay=0`, `AutoUpdateWindowEnabled=0` | **Implemented (v2.4.0)** | Verified `InstallConfigStore` path |
| `H264HWAccel` / `GPUAccelWebViews*` etc. | **Rewrite-existing-only** | Modern section path unverifiable — never invented at a guessed path |
| In-game client/CEF contention guard | **Corrected (v3.6.0)** | Foreground Steam stays Normal/HighQoS. Only background webhelpers receive BelowNormal CPU, low memory priority, and EcoQoS while a game runs; every helper is explicitly restored afterward. No working-set trim, hard cap, suspension, or kill. |
| Webhelper working-set trim / reclaimed-RAM claims | **Removed (v3.0.11; UI retired v3.2.0)** | `EmptyWorkingSet` froze modern CEF; stale `steam-trim-stats.json` is no longer surfaced as current optimization data |
| Download cache ceiling | **Excluded** | No verifiable config.vdf key |
| Auto-update window hour pinning | **Excluded** | Redundant with the two implemented keys |
| `-silent` launcher flag | **Excluded** | Launcher backs explicit launches; minimized start confuses users |
| `-cef-disable-occlusion` / `-cef-disable-renderer-accessibility` | **Excluded (forbidden)** | Documented blanking/hangs on some GPUs |
| Windows quiet (toasts/tray/autostart) | **Keep** | Real startup/notification reduction |
| Random "FPS registry packs" | **Not used** | Folklore |

## Riot and Epic

| Surface | Verdict | Why |
|---------|---------|-----|
| Installed-game discovery | **Implemented (v3.6.0)** | Riot paths and Epic launcher manifests are parsed locally; missing launchers/games are valid states. |
| Quiet launcher startup | **Implemented (v3.6.0)** | Only known per-user startup values are snapshotted and changed. Repair restores the exact prior value. |
| Windows GPU preference | **Implemented (v3.6.0)** | `GpuPreference=2;` is written only for detected game executables, with pristine per-value snapshot and readback verification. |
| Forced IFEO CPU priority for games | **Removed (v3.6.1)** | The launcher optimizer does not override game scheduling policy with undocumented per-image registry values; engines, anti-cheat, and Windows retain control. |
| Hybrid-GPU launcher split | **Implemented (v3.6.1)** | Installed games prefer the high-performance adapter. On an actual iGPU+dGPU system, Riot/Epic launcher UI prefers the integrated adapter so it does not wake or contend with the gaming GPU; single-GPU systems keep Windows automatic selection. |
| Launcher/game cache deletion | **Excluded** | Saves little persistent overhead, can force re-download/rebuild, and is not a latency optimization. |
| Process killing/background guard | **Excluded** | Exo does not close a game, launcher download, or protected session and installs no resident watcher. |
| Vanguard/EOS/services/files/manifests/saves | **Forbidden** | Security, anti-cheat, account, game, and store integrity are outside the mutation boundary. |

## App runtime / publish

| Surface | Verdict | Why |
|---------|---------|-----|
| Self-contained win-x64 + ReadyToRun publish | **Keep** | End users need no runtime; R2R removes JIT warmup |
| Native AOT publish | **Excluded (v2.4.1)** | App state serialization (`SettingsService`, `NetworkOptimizerService`, `NvidiaPanelSettingsService`) is reflection-based `System.Text.Json`; AOT silently degrades those loads to defaults instead of failing loudly. Adoption requires a source-generated-context migration plus on-hardware QA. CI carries an informational compile probe (`EXO_AOT_PROBE`) so the toolchain status stays visible |
| Native AOT / trimming | **Prepared, not shipped** | Source-generated JSON and reflection-free app paths now pass the AOT analysis phase without warnings. Shipping stays on the verified self-contained WinUI path until a native C++ linker build is available for full runtime QA. |
| CI startup measurement | **Implemented (v2.4.1)** | e2e job publishes the real app and reports `EXO_STARTUP_MS` (process start → main window); headless-runner misses warn instead of faking a number |

## How we decide

1. Prefer Microsoft/vendor docs / `netsh` / `Set-Net*` over blog lists
2. Prefer driver advanced properties (`RegistryKeyword`) over dead Tcpip Parameters
3. Prefer clamp-aware values (e.g. SystemResponsiveness 10)
4. Prefer apply-time cleanup over background tasks
5. Every mutation ships with a detect row, a snapshot/repair entry, and a smoke marker — nothing write-only
