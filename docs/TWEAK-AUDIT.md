# OptiHub tweak audit (evidence-based)

Last pass: v1.9.32. Goal: keep only knobs with real OS/driver behavior; drop folklore.

## Internet

| Knob | Verdict | Why |
|------|---------|-----|
| `DisableTaskOffload=0` | **Keep** | `=1` is a real footgun; kills stack offloads |
| Autotune normal / experimental | **Keep** | Documented `netsh` / `Set-NetTCPSetting` |
| Heuristics disabled | **Keep** | Prevents Windows from restricting autotune |
| RSS on | **Keep** | Documented multi-queue receive |
| RSC on/off by preset | **Keep** | Real coalescing latency vs throughput tradeoff |
| LSO on/off by preset | **Keep** | Real driver offload tradeoff |
| CUBIC | **Keep** | Current Windows default path |
| Nagle keys (latency only) | **Keep** | Still applied per interface for TCP |
| SystemResponsiveness **10** | **Keep** | MS: &lt;10 or &gt;100 **clamp to 20**; 0 is wrong |
| NetworkThrottlingIndex **10** (force) | **Keep** | OS default; `ffffffff` can raise DPC/audio issues — overwrite always |
| QoS NonBestEffortLimit 0 | **Keep** | Real GPO (old 20% reserve) |
| Games MMCSS Priority/GPU | **Keep** | Real when apps register with MMCSS |
| Flow Control off (latency) | **Keep** | Pause frames stall gaming under congestion |
| IdleRestriction on (latency, Intel) | **Keep** | Blocks NIC low-power idle on I225/I226-class |
| NIC EEE / selective suspend off | **Keep** | Real link power renegotiation source |
| powercfg wireless max / PCIe ASPM off | **Keep** | Documented power plan settings |
| Interface metric 1 on usable eth | **Keep** | Real route preference |
| PnPCapabilities 24 | **Keep** | Stops “turn off this device to save power” |
| Dynamic ports via netsh | **Keep** | Modern replacement for MaxUserPort |
| Fuzzy Preferred Band match | **Keep** | Vendor display strings vary; prefer never force-only |
| MaxUserPort / MaxFreeTcbs / TcpNumConnections | **Removed** | XP/server-era; ignored or irrelevant on modern desktop |
| TCP chimney / NetDMA / DCA registry | **Removed** | Removed from modern Windows |
| LargeSystemCache | **Removed** | Server-oriented; can hurt desktop |
| Static TcpWindowSize / GlobalMaxTcpWindowSize | **Removed** | Auto-tuning owns RWIN |
| AFD dynamic backlog / FastSend… | **Removed** | Server folklore |
| DNS cache TTL / ServiceProvider order | **Removed** | No proven gaming gain |
| WinINET MaxConnections | **Removed** | IE-era; modern browsers ignore |
| DefaultTTL / KeepAlive / SynAttackProtect | **Removed** | No meaningful client gaming effect |
| BBR2 force, timestamps, initialRto, fastopen | **Removed** | Mixed support / leave OS defaults |
| Force MTU 1500 | **Removed** | Can break PPPoE/VPN; default is fine |
| Scheduled tray tasks | **Removed** | Background noise |

### Ethernet vs Wi‑Fi (same apply, different branches)

| Behavior | Ethernet | Wi‑Fi |
|----------|----------|-------|
| Stack (autotune, Nagle, MMCSS, QoS) | Same | Same |
| Checksum / LSO / RSC (if exposed) | Set; missing props skipped | Set if exposed; often no-ops |
| RSS / multi-queue | **On** (supported) | **Skipped** (MS: many wireless NICs lack RSS) |
| EEE / green ethernet | Off | Off if present |
| Wi‑Fi power-save / uAPSD / MIMO PS | n/a | **Off** |
| Preferred band | n/a | Prefer 5 GHz (**not** 5 GHz-only — 2.4 APs still work) |
| Restart adapter after apply | **Yes** (Up Ethernet) | **No** (would drop association) |
| Dual NIC PCs | Both physical adapters tuned | Both physical adapters tuned |
| Ethernet linked + real IPv4 | **Prefer Ethernet 100%** (metric 1, disable Wi‑Fi) | n/a |
| Adapter restart | **Only if user confirms in dialog** | Never auto-restart |
| Band prefer | n/a | Prefer **6 GHz** if client supports, else **5 GHz** (never force-only) |

### “Smart” detection (no cloud AI)

OptiHub uses **local capability detection**, not a generative model:

- **Internet**: Ethernet-up? disable Wi‑Fi; Wi‑Fi radio/driver 5/6 GHz support via adapter properties + `netsh wlan`
- **NVIDIA**: GPU series / G-SYNC / notebook detect (existing)
- **Discord / Steam**: install path + live feature verification (existing)

Router firmware is not queried over the WAN; band choice uses **your PC’s radio + connected BSS hints**.

## NVIDIA

| Area | Verdict |
|------|---------|
| Driver-only + OptiHub panel | **Keep** — App/CPL are not required for gaming |
| MSI High / telemetry / Ansel / HDCP off | **Keep** — real driver/service knobs |
| Profile Inspector ULL / max perf / pre-render 1 | **Keep** — real DRS settings |
| Tray: hide display IsPromoted=0; delete App ghosts | **Keep** — no logon task |
| Logon/persist scheduled tasks | **Removed** |

## Discord / Steam

| Area | Verdict |
|------|---------|
| Discord kernel (priority, trim, raw input) | **Keep** — in-process real behavior |
| Steam CEF lean launcher /HIGH + cache clean | **Keep** — real client overhead reduction |
| Windows quiet (toasts/tray/autostart) | **Keep** — real startup/notification reduction |
| Random "FPS registry packs" | **Not used** |
| Detect false-fails (TrimInterval hardcode) | **Fixed** — peak config range + kit proxy hashes (`DiscordDetectCore` / `DiscordPeakLogic`) |

## How we decide

1. Prefer Microsoft docs / `netsh` / `Set-Net*` over blog lists  
2. Prefer driver advanced properties over dead Tcpip Parameters  
3. Prefer clamp-aware values (e.g. SystemResponsiveness 10)  
4. Prefer apply-time cleanup over background tasks  
